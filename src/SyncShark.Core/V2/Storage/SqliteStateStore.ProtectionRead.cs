using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // These predicates probe one file/directory, never a candidate's whole subtree. Pending
    // obligations belong to the current entity, not the path saved in expected_json.
    internal const string ProtectionHeldSql = """
        ((f.flags!=0 AND (f.flags & $flags)!=0) OR ($gap=1 AND f.kind=0 AND
          (($done=0 AND f.object_id>$after) OR EXISTS(SELECT 1 FROM local_gap_item gi
            WHERE gi.root_no=f.root_no AND gi.object_id=f.object_id))))
        """;

    internal static readonly string ProtectionDirectSql = """
        WITH a AS (SELECT json_extract(value,'$.n') n,json_extract(value,'$.dir') d,
          json_extract(value,'$.name') name,json_extract(value,'$.self') self FROM json_each($a)),
        matches AS (
          SELECT a.n n,a.self self FROM a CROSS JOIN dir d INDEXED BY dir_key ON d.path_key=a.d
          CROSS JOIN file_state f NOT INDEXED ON f.root_no=$r AND f.dir_no=d.dir_no AND f.name_key=a.name
          WHERE
        """ + ProtectionHeldSql + """
        ) SELECT n,MAX(self) FROM matches GROUP BY n;
        """;

    // Nested candidate ranges are removed before binding $ranges. The equality and range
    // branches must stay separate: an OR between them can turn a seek into a full dir scan.
    // The EXISTS below examines only ONE directory via file_state's primary key; it does
    // not recursively walk a candidate subtree. Each directory is visited once per batch.
    internal static readonly string ProtectionDirectoriesSql = """
        WITH q AS (SELECT value k,value || '/' lo,value || '0' hi FROM json_each($ranges)),
        directories AS (
          SELECT d.dir_no,d.path_key FROM q CROSS JOIN dir d INDEXED BY dir_key ON d.path_key=q.k
          UNION ALL
          SELECT d.dir_no,d.path_key FROM q CROSS JOIN dir d INDEXED BY dir_key
            ON d.path_key>=q.lo AND d.path_key<q.hi)
        SELECT d.path_key FROM directories d WHERE EXISTS(
          SELECT 1 FROM file_state f NOT INDEXED WHERE f.root_no=$r AND f.dir_no=d.dir_no AND
        """ + ProtectionHeldSql + ");";

    internal const string ProtectionRootSql = """
        SELECT EXISTS(SELECT 1 FROM file_state f INDEXED BY fs_flagged
          WHERE f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0)
        OR ($gap=1 AND (($done=0 AND EXISTS(SELECT 1 FROM file_state f INDEXED BY fs_obj
          WHERE f.root_no=$r AND f.object_id>$after AND f.kind=0))
          OR EXISTS(SELECT 1 FROM local_gap_item gi CROSS JOIN file_state f INDEXED BY fs_obj
            ON f.root_no=gi.root_no AND f.object_id=gi.object_id WHERE gi.root_no=$r)));
        """;

    private static void BindProtection(SqliteCommand command, long root, ItemFlags flags,
        bool gap, bool done, string after)
    {
        command.Parameters.AddWithValue("$r", root);
        command.Parameters.AddWithValue("$flags", (long)flags);
        command.Parameters.AddWithValue("$gap", gap ? 1 : 0);
        command.Parameters.AddWithValue("$done", done ? 1 : 0);
        command.Parameters.AddWithValue("$after", after);
    }

    private static bool ProtectionDescendant(string directory, string candidate) =>
        directory.Equals(candidate, StringComparison.Ordinal) ||
        (directory.Length > candidate.Length && directory[candidate.Length] == '/' &&
            directory.StartsWith(candidate, StringComparison.Ordinal));

    private static IReadOnlyList<ProtectionMatch> ReadProtectionBatch(SqliteConnection connection,
        SqliteTransaction transaction, long? rootNumber, ItemFlags flags, IReadOnlyList<string> paths)
    {
        var result = new ProtectionMatch[paths.Count];
        // The caller pins this snapshot at a store-call boundary before releasing _gate.
        if (rootNumber is null) return result;
        var root = rootNumber.Value;
        var gap = false;
        var done = true;
        var after = "";
        if ((flags & ItemFlags.NeedsRehash) != 0)
        {
            using var gapCommand = StoreSchema.Command(connection, transaction,
                "SELECT discovery_done,after_object FROM local_gap WHERE root_no=$r;");
            gapCommand.Parameters.AddWithValue("$r", root);
            using var gapReader = gapCommand.ExecuteReader();
            if (gapReader.Read())
            {
                gap = true;
                done = gapReader.GetInt64(0) != 0;
                after = gapReader.GetString(1);
            }
        }
        var keys = paths.Select(RowCodec.Key).ToArray();
        var ancestors = new List<object>();
        for (var n = 0; n < keys.Length; n++)
        {
            for (var current = keys[n]; current.Length > 0;)
            {
                var parts = RowCodec.Split(current);
                ancestors.Add(new { n, dir = parts.Parent, name = parts.Name, self = current == keys[n] ? 1 : 0 });
                current = parts.Parent;
            }
        }
        using (var command = StoreSchema.Command(connection, transaction, ProtectionDirectSql))
        {
            BindProtection(command, root, flags, gap, done, after);
            command.Parameters.AddWithValue("$a", System.Text.Json.JsonSerializer.Serialize(ancestors));
            using var reader = command.ExecuteReader();
            while (reader.Read()) result[reader.GetInt32(0)] = new(true, reader.GetInt64(1) != 0);
        }
        if (keys.Contains("", StringComparer.Ordinal))
        {
            using var command = StoreSchema.Command(connection, transaction, ProtectionRootSql);
            BindProtection(command, root, flags, gap, done, after);
            var contains = Convert.ToInt64(command.ExecuteScalar()) != 0;
            for (var n = 0; n < keys.Length; n++)
                if (keys[n].Length == 0) result[n] = new(false, contains);
        }
        var unresolved = Enumerable.Range(0, keys.Length)
            .Where(n => keys[n].Length != 0 && !result[n].Contains)
            .GroupBy(n => keys[n], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var ranges = new List<string>();
        foreach (var key in unresolved.Keys.Order(StringComparer.Ordinal))
        {
            // Do not rely on the immediately preceding spelling: A-Other sorts between A
            // and A/Child. At most 1024 candidates are retained, independent of root size.
            if (!ranges.Any(parent => ProtectionDescendant(key, parent))) ranges.Add(key);
        }
        if (ranges.Count != 0)
        {
            using var command = StoreSchema.Command(connection, transaction, ProtectionDirectoriesSql);
            BindProtection(command, root, flags, gap, done, after);
            command.Parameters.AddWithValue("$ranges", System.Text.Json.JsonSerializer.Serialize(ranges));
            using var reader = command.ExecuteReader();
            while (unresolved.Count != 0 && reader.Read())
            {
                for (var directory = reader.GetString(0); directory.Length != 0;)
                {
                    if (unresolved.Remove(directory, out var indices))
                        foreach (var n in indices) result[n] = new(result[n].Covered, true);
                    directory = RowCodec.Split(directory).Parent;
                }
            }
        }
        return result;
    }
}
