using System.Globalization;
using System.Reflection;
using Massdex.SyncShark.Engine.Storage;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Reads the persisted pending-acquired-work fact the retired IStateStore.PendingByIdentity answered:
/// one selection per known-identity group across every pair sharing the source, independent of holds and passes.
/// It runs the same Storage statement through the test's own connection; production exposes no such read.</summary>
internal static class PendingWork
{
    internal static IReadOnlyList<WorkSelection> ByIdentity(IStateStore store, Side side, string domain, string identity) =>
        ByIdentity(store as StateStore ?? throw new InvalidOperationException(
            "PendingWork.ByIdentity reads the SQLite StateStore"), side, domain, identity);

    internal static IReadOnlyList<WorkSelection> ByIdentity(StateStore store, Side side, string domain, string identity)
    {
        using var connection = OpenReadOnly(store);
        var through = Scalar(connection, StateSchema.WorkWatermark);
        var seen = new HashSet<(long, long, long, long?, string?, string?)>();
        var pairs = new Dictionary<long, (string Id, long Generation)?>();
        var sources = new Dictionary<(long Pair, Side Side), long?>();
        var selections = new List<WorkSelection>();
        long afterPair = StateSchema.BeforeFirstPairRow, after = 0;
        while (Read(connection, side, domain, identity, afterPair, after) is { } row)
        {
            (afterPair, after) = (row.PairRow, row.WorkId);
            if (!seen.Add((row.PairRow, row.PairGeneration, row.SourceEpoch, row.Parent, row.Obligation, row.Before)))
                continue;
            if (row.WorkId > through) continue;
            if (!pairs.TryGetValue(row.PairRow, out var pair)) pairs[row.PairRow] = pair = PairOf(connection, row.PairRow);
            if (pair is not { } owned || owned.Generation != row.PairGeneration) continue;
            if (!sources.TryGetValue((row.PairRow, side), out var epoch))
                sources[(row.PairRow, side)] = epoch = SourceEpoch(connection, row.PairRow, side);
            if (epoch != row.SourceEpoch) continue;
            if (row.Parent is { } parent && ParentExists(connection, row.PairRow, parent)) continue;
            selections.Add(new WorkSelection(owned.Id, row.PairGeneration, row.SourceEpoch, side, row.Domain,
                row.Identity, row.Key, row.WorkId, through) { Obligation = row.Obligation });
        }
        return selections;
    }

    private static Row? Read(SqliteConnection connection, Side side, string domain, string identity,
        long afterPair, long after)
    {
        using var command = connection.CreateCommand();
        command.CommandText = StateSchema.PendingByIdentity;
        command.Parameters.AddWithValue(StateSchema.ParamSide, (int)side);
        command.Parameters.AddWithValue(StateSchema.ParamDomain, domain);
        command.Parameters.AddWithValue(StateSchema.ParamIdentity,
            StateEncoding.IdFor(side, identity) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(StateSchema.ParamAfterPair, afterPair);
        command.Parameters.AddWithValue(StateSchema.ParamAfter, after);
        command.Parameters.AddWithValue(StateSchema.ParamTake, StateSchema.OneRow);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new Row(reader.GetInt64(StateSchema.WorkId), reader.GetInt64(StateSchema.WorkPairId),
            reader.GetInt64(StateSchema.WorkPairGeneration), reader.GetInt64(StateSchema.WorkSourceEpoch),
            reader.GetString(StateSchema.WorkDomain), reader.GetString(StateSchema.WorkPathKey),
            reader.IsDBNull(StateSchema.WorkIdentity) ? null
                : Convert.ToString(reader.GetValue(StateSchema.WorkIdentity), CultureInfo.InvariantCulture),
            reader.IsDBNull(StateSchema.WorkBeforeJson) ? null : reader.GetString(StateSchema.WorkBeforeJson),
            reader.IsDBNull(StateSchema.WorkParentAnchor) ? null : reader.GetInt64(StateSchema.WorkParentAnchor),
            reader.IsDBNull(StateSchema.WorkObligationKey) ? null : reader.GetString(StateSchema.WorkObligationKey));
    }

    private static (string Id, long Generation)? PairOf(SqliteConnection connection, long pair)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pair_id,execution_generation FROM pair WHERE id=$p;";
        command.Parameters.AddWithValue(StateSchema.ParamPair, pair);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private static long? SourceEpoch(SqliteConnection connection, long pair, Side side)
    {
        using var command = connection.CreateCommand();
        command.CommandText = StateSchema.SourceFence;
        command.Parameters.AddWithValue(StateSchema.ParamPairRow, pair);
        command.Parameters.AddWithValue(StateSchema.ParamSide, (int)side);
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetInt64(StateSchema.SourceEpoch) : null;
    }

    private static bool ParentExists(SqliteConnection connection, long pair, long work)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM change_work WHERE work_id=$work AND pair_id=$pair;";
        command.Parameters.AddWithValue(StateSchema.ParamWork, work);
        command.Parameters.AddWithValue(StateSchema.ParamPairRow, pair);
        return command.ExecuteScalar() is not null;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // Reflection retrieves only the file path; commands never use the store's shared connection.
    internal static SqliteConnection OpenReadOnly(StateStore store)
    {
        var source = (SqliteConnection)typeof(StateStore)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source.DataSource, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    // B-209 repro, test only: prints the durable work, issue, episode and synced rows around the two lock keys.
    internal static void DumpLockEpisode(StateStore store, string pairId, string path, string moved)
    {
        var key = PathNames.Key(path);
        var movedKey = PathNames.Key(moved);
        Console.WriteLine($"B209 dump pair={pairId} path={path} moved={moved} keys={key},{movedKey}");
        using var connection = OpenReadOnly(store);
        Print(connection,
            "B209 pair: execution_generation|pending_json",
            "SELECT execution_generation,pending_json FROM pair WHERE pair_id=$pair ORDER BY id;",
            ("$pair", pairId));
        Print(connection,
            "B209 source_state: side|source_epoch|repair_reason",
            "SELECT side,source_epoch,repair_reason FROM source_state " +
            "WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$pair) ORDER BY side;",
            ("$pair", pairId));
        Print(connection,
            "B209 change_work: work_id|origin|dispatch|key|old_key|identity|issue_members|episode_issue|" +
            "kind|dirty|acq|parent|obligation|discovery|has_before",
            "SELECT w.work_id,w.work_origin,w.dispatch_kind,w.path_key,w.old_path_key,w.identity," +
            " (SELECT group_concat(m.issue_id) FROM issue_member m WHERE m.work_id=w.work_id)," +
            " (SELECT i.issue_id FROM issue i WHERE i.item_retry_work_id=w.work_id)," +
            " json_extract(w.notice_json,'$.Value.Kind'),json_extract(w.notice_json,'$.Value.LocalContentDirty')," +
            " w.acquisition_work_id,w.parent_anchor,w.obligation_key,w.discovery_required,(w.before_json IS NOT NULL)" +
            " FROM change_work w WHERE w.pair_id=(SELECT id FROM pair WHERE pair_id=$pair)" +
            " AND w.path_key IN ($key,$movedKey) ORDER BY w.work_id;",
            ("$pair", pairId), ("$key", key), ("$movedKey", movedKey));
        Print(connection,
            "B209 issue: issue_id|kind|cause|path|dismissed|episode|revision|episode_work",
            "SELECT issue_id,kind,cause,path,dismissed,item_retry_episode,item_retry_revision,item_retry_work_id" +
            " FROM issue WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$pair)" +
            " AND (lower(path) IN (lower($path),lower($moved))" +
            " OR item_retry_work_id IN (SELECT work_id FROM change_work" +
            " WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$pair) AND path_key IN ($key,$movedKey))" +
            " OR issue_id IN (SELECT issue_id FROM issue_member" +
            " WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$pair) AND path_key IN ($key,$movedKey)))" +
            " ORDER BY issue_id;",
            ("$pair", pairId), ("$path", path), ("$moved", moved), ("$key", key), ("$movedKey", movedKey));
        Print(connection,
            "B209 issue_member: member_id|issue_id|work_id|side|path_key|identity",
            "SELECT member_id,issue_id,work_id,side,path_key,identity FROM issue_member" +
            " WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$pair) AND path_key IN ($key,$movedKey)" +
            " ORDER BY member_id;",
            ("$pair", pairId), ("$key", key), ("$movedKey", movedKey));
        Print(connection,
            "B209 synced_entry: local_path|local_id|dropbox_path|dropbox_hash",
            "SELECT local_path,local_id,dropbox_path,dropbox_hash FROM synced_entry" +
            " WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$pair)" +
            " AND lower(local_path) IN (lower($path),lower($moved)) ORDER BY local_path;",
            ("$pair", pairId), ("$path", path), ("$moved", moved));
    }

    private static void Print(SqliteConnection connection, string title, string sql,
        params (string Name, string Value)[] arguments)
    {
        Console.WriteLine(title);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            Console.WriteLine("B209 row " + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i =>
                reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
    }

    private sealed record Row(long WorkId, long PairRow, long PairGeneration, long SourceEpoch, string Domain,
        string Key, string? Identity, string? Before, long? Parent, string? Obligation);
}
