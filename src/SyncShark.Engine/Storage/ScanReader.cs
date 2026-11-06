using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Reads the joined scan (section 3.4): the ScanRows decoder, the rename candidates and a discovery scan's
/// deleted names.</summary>
public static class ScanReader
{
    /// <summary>One Row per consecutive (synced, k) group; context is called once per Row, in result order.</summary>
    public static IEnumerable<Row> Rows(SqliteConnection read, string pairId, Func<Row, RowContext> context, string? scope = null, byte[]? scanId = null)
    {
        using var next = Command(read, scope is null ? StateSchema.NextScanKey : StateSchema.NextScopedScanKey, pairId, scanId);
        var scopeKey = scope is null ? null : PathNames.Key(scope);
        var bounds = PathNames.DescendantBounds(scopeKey ?? string.Empty);
        if (scopeKey is not null)
        {
            next.Parameters.AddWithValue(StateSchema.ParamScope, scopeKey);
            next.Parameters.AddWithValue(StateSchema.ParamScopeLow, bounds.Low);
            next.Parameters.AddWithValue(StateSchema.ParamScopeHigh, bounds.High);
        }
        var after = next.Parameters.AddWithValue(StateSchema.ParamAfter, scopeKey ?? "");
        var first = next.Parameters.AddWithValue(StateSchema.ParamFirst, true);
        using var command = Command(read, StateSchema.ScanRows, pairId, scanId);
        var target = command.Parameters.AddWithValue(StateSchema.ParamTargetKey, "");
        var page = new List<Group>();
        while (true)
        {
            page.Clear();
            // Select the next key and all its candidates in one short snapshot; release it before caller work.
            using (var transaction = read.BeginTransaction(deferred: true))
            {
                next.Transaction = transaction;
                if (next.ExecuteScalar() is not string key) yield break;
                after.Value = key;
                first.Value = false;
                command.Transaction = transaction;
                target.Value = key;
                using (var reader = command.ExecuteReader())
                {
                    Group? group = null;
                    while (reader.Read())
                    {
                        var synced = StateEncoding.ReadBool(reader, StateSchema.RowSynced);
                        if (group is null || group.Synced != synced)
                        {
                            group = new Group(synced, key)
                            {
                                SyncedEntry = synced ? StateEncoding.ReadSynced(reader, key, StateSchema.RowLocalPath) : null,
                            };
                            page.Add(group);
                        }
                        Add(reader, group);
                    }
                }
                transaction.Commit();
            }
            foreach (var group in page)
                yield return new Row(context, group.Key, group.Local, group.Dropbox, group.SyncedEntry, group.Extra, null);
        }
    }

    /// <summary>Ordered by path depth, then path. Both sides compare the whole path exactly.</summary>
    public static IReadOnlyList<RenameCandidate> Renames(SqliteConnection read, string pairId, byte[]? scanId = null)
    {
        var candidates = new List<RenameCandidate>();
        using var command = Command(read, StateSchema.ScanRenames, pairId, scanId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var side = (Side)reader.GetInt64(StateSchema.ScanSide);
            var synced = StateEncoding.ReadSynced(reader, reader.GetString(StateSchema.RenameLocalKey),
                StateSchema.RenameLocalPath);
            candidates.Add(new RenameCandidate(side, ScannedEntry(reader, localProjection: side == Side.Local), synced));
        }
        return candidates.OrderBy(candidate => PathNames.Depth(candidate.Moved.Path))
            .ThenBy(candidate => candidate.Moved.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads this side's entry by its own canonical key, not its mapped join key.</summary>
    public static Entry? EntryByKey(SqliteConnection read, string pairId, Side side, string key, byte[]? scanId = null)
    {
        using var command = StorageCommand.Create(read, null, StateSchema.SelectScanEntry,
            StateSchema.ParamPair, pairId);
        command.Parameters.AddWithValue(StateSchema.ParamScan, scanId ?? Guid.Empty.ToByteArray());
        command.Parameters.AddWithValue(StateSchema.ParamSide, (int)side);
        command.Parameters.AddWithValue(StateSchema.ParamTargetKey, key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ScannedEntry(reader, localProjection: side == Side.Local) : null;
    }

    /// <summary>Whether a discovery scan recorded any deleted name; only a discovery scan has that relation. Asked once
    /// per completed scan so an empty relation costs no per-file membership statement.</summary>
    public static bool HasDeletedNames(SqliteConnection read, long pairRow) =>
        Exists(read, StateSchema.HasDeletedNames, (StateSchema.ParamPairRow, pairRow));

    /// <summary>Whether this exact key, supplied by the path owner, is a deleted name. A match selects investigation
    /// only: it never proves a deletion, and it means nothing before the scan completes.</summary>
    public static bool DeletedNameExact(SqliteConnection read, long pairRow, string key) =>
        Exists(read, StateSchema.DeletedNameExact, (StateSchema.ParamPairRow, pairRow), (StateSchema.ParamPath, key));

    private static bool Exists(SqliteConnection read, string sql, params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(read, null, sql, arguments);
        return (long)command.ExecuteScalar()! != 0;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, string pairId, byte[]? scanId) =>
        StorageCommand.Create(connection, null, sql, (StateSchema.ParamPair, pairId),
            (StateSchema.ParamScan, scanId ?? Guid.Empty.ToByteArray()));

    private static void Add(SqliteDataReader reader, Group group)
    {
        // Local names cannot share a key case-insensitively on Windows, so Extra holds only Dropbox ambiguity.
        if (group.Local is null && !reader.IsDBNull(StateSchema.RowLocalOwnKey))
            group.Local = LocalEntry(reader);
        if (!reader.IsDBNull(StateSchema.RowDropboxOwnKey))
        {
            var entry = DropboxEntry(reader);
            if (group.Dropbox is null) group.Dropbox = entry;
            else group.Extra.Add(entry);
        }
    }

    private static Entry LocalEntry(SqliteDataReader reader) =>
        StateEncoding.ReadEntry(reader, StateSchema.RowLocalEntryPath, localProjection: true);

    private static Entry DropboxEntry(SqliteDataReader reader) =>
        StateEncoding.ReadEntry(reader, StateSchema.RowDropboxEntryPath, localProjection: false);

    private static Entry ScannedEntry(SqliteDataReader reader, bool localProjection)
    {
        // One statement layout carries revision and hash on both sides; the local projection hides them.
        var entry = StateEncoding.ReadEntry(reader, StateSchema.ScanPath, localProjection: false);
        return localProjection ? entry with { Revision = string.Empty, ContentHash = string.Empty } : entry;
    }

    private sealed class Group(bool synced, string key)
    {
        public bool Synced { get; } = synced;
        public string Key { get; } = key;
        /// <summary>The saved binding for the group, set when Synced is true.</summary>
        public SyncedEntry? SyncedEntry { get; init; }
        /// <summary>The local entry seen in this scan, or null.</summary>
        public Entry? Local { get; set; }
        /// <summary>The Dropbox entry seen in this scan, or null.</summary>
        public Entry? Dropbox { get; set; }
        /// <summary>Further Dropbox entries sharing this key (ambiguity candidates).</summary>
        public List<Entry> Extra { get; } = [];
    }
}
