using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

// A reviewed move is not copy permission. Pin the old membership and both
// versions the user saw; current planning/execution must still validate them.
public sealed record SyncBootstrapMoveReview(SyncAuthorityOperationKind Kind, string FromPath,
    string ObjectId, string LocalObjectVersionId, string RemoteObjectVersionId)
{
    public string Action => Kind switch
    {
        SyncAuthorityOperationKind.MoveLocal => "MoveLocal",
        SyncAuthorityOperationKind.MoveRemote => "MoveRemote",
        _ => throw new InvalidDataException("A reviewed move must name a move operation.")
    };
}

public static class SyncBootstrapActions
{
    // These compatibility keys name engine ports, never a fixed provider or
    // logical left/right endpoint. Only a verified saved baseline creates them.
    public const string LocalUpdatedGroup = "Local changed since sync";
    public const string RemoteUpdatedGroup = "Dropbox changed since sync";
    public const string RemoteDeletedGroup = "Deleted remotely after local change";

    public static string? TransferAction(string group) => group switch
    {
        "Local only" or LocalUpdatedGroup => "Upload",
        "Dropbox only" or RemoteUpdatedGroup => "Download",
        RemoteDeletedGroup => "DeleteLocal",
        _ => null
    };

    public static bool IsActionable(string group) => TransferAction(group) is not null;

    public static SyncAuthorityOperationKind TransferKind(string group) => group switch
    {
        "Local only" => SyncAuthorityOperationKind.CreateRemote,
        "Dropbox only" => SyncAuthorityOperationKind.CreateLocal,
        LocalUpdatedGroup => SyncAuthorityOperationKind.UpdateRemote,
        RemoteUpdatedGroup => SyncAuthorityOperationKind.UpdateLocal,
        RemoteDeletedGroup => SyncAuthorityOperationKind.DeleteLocal,
        _ => throw new InvalidDataException("This first-sync item has no admitted transfer direction.")
    };

    public static IReadOnlyList<string> For(string group, SyncBootstrapMoveReview? move = null) =>
        move is not null ? [move.Action, "Skip for now"] : TransferAction(group) is { } action
            ? [action, "Skip for now"] : [];

    public static bool IsMove(string action) => action is "MoveLocal" or "MoveRemote";

    /// <summary>Two modification times this close are a tie: the user decides, nothing is replaced.</summary>
    public static readonly TimeSpan NewestWinsTieWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The one newest-wins rule for a two-sided content conflict, shared by the first-sync review and
    /// the reconciliation planner. Returns the port whose version is newer ("local" or "dropbox"), or null
    /// for a tie (within <see cref="NewestWinsTieWindow"/>) or a missing time; null always stays manual.
    /// </summary>
    public static string? NewerPort(DateTimeOffset? local, DateTimeOffset? dropbox)
    {
        if (local is null || dropbox is null) return null;
        var delta = local.Value - dropbox.Value;
        if (delta.Duration() <= NewestWinsTieWindow) return null;
        return delta > TimeSpan.Zero ? "local" : "dropbox";
    }

    public const string TieReason =
        "Tie — needs your decision: both versions changed and were last modified within 2 seconds of each other, so neither was replaced. Choose which to keep, or keep both.";

    public const string MissingTimeReason =
        "Needs your decision: both versions changed but a reliable last-modified time is missing for one side, so neither was replaced. Choose which to keep, or keep both.";

    /// <summary>Why <see cref="NewerPort"/> chose no side for these times.</summary>
    public static string ManualReason(DateTimeOffset? local, DateTimeOffset? dropbox) =>
        local is null || dropbox is null ? MissingTimeReason : TieReason;

    /// <summary>The newest-wins times of two observed entries: local lastWriteUtc, remote modifiedUtc.</summary>
    internal static (DateTimeOffset? Local, DateTimeOffset? Dropbox) ModifiedUtc(
        SyncAuthorityObservedEntry local, SyncAuthorityObservedEntry dropbox) =>
        (EvidenceTime(local.EvidenceJson, "lastWriteUtc"), EvidenceTime(dropbox.EvidenceJson, "modifiedUtc"));

    internal static DateTimeOffset? IdentityTime(string identityJson, string property)
    {
        using var identity = JsonDocument.Parse(identityJson);
        return Time(identity.RootElement, property);
    }

    // Evidence carries the time directly, or inside its nested identity JSON string.
    internal static DateTimeOffset? EvidenceTime(string evidenceJson, string property)
    {
        using var evidence = JsonDocument.Parse(evidenceJson);
        return Time(evidence.RootElement, property) ??
               (evidence.RootElement.ValueKind == JsonValueKind.Object &&
                evidence.RootElement.TryGetProperty("identity", out var identity) &&
                identity.ValueKind == JsonValueKind.String && identity.GetString() is { } nested
                   ? IdentityTime(nested, property)
                   : null);
    }

    private static DateTimeOffset? Time(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}

public sealed partial class SyncAuthorityDatabase
{
    // Reuses the review's checked connection and exact retained scan IDs. This
    // is one set query for a page, never a database lookup for each file.
    private static async Task AddBootstrapMoveReviewsAsync(SqliteConnection connection, string pairId,
        string localScan, string remoteScan, List<SyncAuthorityBootstrapEvidence> items,
        CancellationToken token, Action? nativeProgressForTest = null)
    {
        // The same moves the reconciliation planner classifies: one stable identity per side, one side at
        // its baseline path and version, the other at a new path. A moving file may also carry an edit; a
        // folder moves only locally with both versions unchanged (the planner refuses other folder moves).
        var paths = items.Where(item => item.Group is "Local only" or "Dropbox only")
            .Select(item => item.RelativePath).ToArray();
        if (paths.Length == 0) return;
        // A private, temporary cache for this large retained-inventory read only.
        // Restore even after SQL failure/cancellation before a pooled connection
        // can be returned. No persistent pragma, health predicate or write changes.
        var previousCache = await ScalarLongAsync(connection, null, "PRAGMA cache_size;", token);
        try
        {
            await ExecuteAsync(connection, null, "PRAGMA cache_size=-65536;", token);
            await ReadBootstrapMoveReviewsAsync(connection, pairId, localScan, remoteScan,
                items, paths, token, nativeProgressForTest);
        }
        finally
        {
            await ExecuteAsync(connection, null, "PRAGMA cache_size=" +
                previousCache.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";",
                CancellationToken.None);
        }
    }

    private static async Task ReadBootstrapMoveReviewsAsync(SqliteConnection connection, string pairId,
        string localScan, string remoteScan, List<SyncAuthorityBootstrapEvidence> items, string[] paths,
        CancellationToken token, Action? nativeProgressForTest)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH local_now AS (
            SELECT m.relative_path_key path,v.object_id,v.object_version_id,
            count(*) OVER (PARTITION BY v.object_id) locations
            FROM effective_observation o JOIN membership m ON m.membership_id=o.membership_id
            JOIN object_version v ON v.object_version_id=o.object_version_id
            WHERE o.scan_generation_id=$local
            ), remote_now AS (
            SELECT m.relative_path_key path,v.object_id,v.object_version_id,
            count(*) OVER (PARTITION BY v.object_id) locations
            FROM effective_observation o JOIN membership m ON m.membership_id=o.membership_id
            JOIN object_version v ON v.object_version_id=o.object_version_id
            WHERE o.scan_generation_id=$remote
            ), baseline AS MATERIALIZED (
            SELECT s.object_id,s.local_object_version_id,s.dropbox_object_version_id,
            lm.relative_path_key local_path,rm.relative_path_key remote_path,obj.object_kind kind
            FROM synchronization_point s
            JOIN logical_object obj ON obj.object_id=s.object_id
            JOIN membership lm ON lm.membership_id=s.local_membership_id
            JOIN membership rm ON rm.membership_id=s.dropbox_membership_id
            JOIN object_version rv ON rv.object_version_id=s.dropbox_object_version_id
            WHERE s.pair_id=$pair AND s.authority_generation=(
            SELECT max(n.authority_generation) FROM synchronization_point n
            WHERE n.pair_id=s.pair_id AND n.object_id=s.object_id)
            ), paired AS MATERIALIZED (
            -- One row per object with exactly one location on each side and a baseline. Grouping
            -- replaces CTE-to-CTE joins: SQLite estimated the window CTEs as tiny and nested full
            -- scans of them, which was quadratic in the inventory size.
            SELECT object_id,
            max(CASE WHEN side=0 THEN path END) local_now_path,
            max(CASE WHEN side=0 THEN version END) local_now_version,
            max(CASE WHEN side=1 THEN path END) remote_now_path,
            max(CASE WHEN side=1 THEN version END) remote_now_version,
            max(CASE WHEN side=2 THEN path END) local_path,
            max(CASE WHEN side=2 THEN version END) local_object_version_id,
            max(CASE WHEN side=2 THEN remote_path END) remote_path,
            max(CASE WHEN side=2 THEN remote_version END) dropbox_object_version_id,
            max(CASE WHEN side=2 THEN kind END) kind
            FROM (SELECT 0 side,object_id,path,object_version_id version,NULL remote_path,NULL remote_version,
            NULL kind FROM local_now WHERE locations=1
            UNION ALL SELECT 1,object_id,path,object_version_id,NULL,NULL,NULL FROM remote_now WHERE locations=1
            UNION ALL SELECT 2,object_id,local_path,local_object_version_id,remote_path,dropbox_object_version_id,kind
            FROM baseline)
            GROUP BY object_id
            HAVING count(CASE WHEN side=0 THEN 1 END)=1 AND count(CASE WHEN side=1 THEN 1 END)=1
            AND count(CASE WHEN side=2 THEN 1 END)=1
            ), moves AS (
            SELECT b.remote_now_path target,b.local_now_path prior,0 remote_destination,b.object_id,
            b.local_now_version local_version,b.remote_now_version remote_version
            FROM paired b
            JOIN object_version current_remote ON current_remote.object_version_id=b.remote_now_version
            JOIN object_version baseline_remote ON baseline_remote.object_version_id=b.dropbox_object_version_id
            WHERE b.local_now_path=b.local_path AND b.remote_now_path<>b.remote_path
            AND b.local_now_version=b.local_object_version_id AND b.kind='file'
            AND current_remote.content_hash IS NOT NULL AND baseline_remote.content_hash IS NOT NULL
            AND current_remote.size_bytes IS NOT NULL AND baseline_remote.size_bytes IS NOT NULL
            AND coalesce(json_extract(current_remote.identity_json,'$.fileId'),'')<>''
            AND json_extract(current_remote.identity_json,'$.fileId')=json_extract(baseline_remote.identity_json,'$.fileId')
            AND NOT EXISTS(SELECT 1 FROM local_now d WHERE d.path=b.remote_now_path)
            AND NOT EXISTS(SELECT 1 FROM remote_now old
            JOIN object_version old_version ON old_version.object_version_id=old.object_version_id
            WHERE old.path=b.remote_path
            AND (old.object_id=b.object_id OR coalesce(json_extract(old_version.identity_json,'$.fileId'),'')=''
            OR json_extract(old_version.identity_json,'$.fileId')=json_extract(current_remote.identity_json,'$.fileId')))
            UNION ALL
            SELECT b.local_now_path,b.remote_now_path,1,b.object_id,b.local_now_version,b.remote_now_version
            FROM paired b
            WHERE b.local_now_path<>b.local_path AND b.remote_now_path=b.remote_path
            AND (b.kind='file' OR b.local_now_version=b.local_object_version_id)
            AND b.remote_now_version=b.dropbox_object_version_id
            AND NOT EXISTS(SELECT 1 FROM remote_now d WHERE d.path=b.local_now_path)
            )
            SELECT target,prior,remote_destination,object_id,local_version,remote_version
            FROM moves WHERE target IN (SELECT value FROM json_each($paths));
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$local", localScan);
        command.Parameters.AddWithValue("$remote", remoteScan);
        command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(paths));
        var moves = new Dictionary<string, SyncBootstrapMoveReview>(StringComparer.OrdinalIgnoreCase);
        var queryStarted = TimingStart();
        await ReadWithNativeCancellationAsync(connection, async () =>
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var kind = reader.GetInt64(2) == 0 ? SyncAuthorityOperationKind.MoveLocal : SyncAuthorityOperationKind.MoveRemote;
                moves.Add(reader.GetString(0), new(kind, reader.GetString(1), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5)));
            }
            return true;
        }, token, nativeProgressForTest);
        ReportTiming("bootstrap-move-review-query", queryStarted);
        for (var i = 0; i < items.Count; i++)
            if (moves.TryGetValue(items[i].RelativePath, out var move)) items[i] = items[i] with { Move = move };
    }
}
