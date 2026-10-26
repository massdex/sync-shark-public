using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

// Normalized Rewind persistence on the EXISTING authority database and writer. There is no second
// database, service, journal framework or parallel authority: every read and write below runs inside
// the same transaction boundary, connection scope and health admission as the rest of the authority.
//
// Growth is decoupled per reviewed path: the header carries only bounded header/current-preview
// identity fields, the reviewed preview is stored once per admitted preview, and one checkpoint is a
// single indexed progress-row update inside one writer transaction. Nothing here serializes, sorts or
// rescans the whole reviewed path set for a checkpoint.
public sealed partial class SyncAuthorityDatabase
{
    private const string RewindSessionColumns = """
        session_id,installation_id,connection_id,pair_id,pair_name,dropbox_root,
        local_checkpoint_scan_id,dropbox_checkpoint_scan_id,dropbox_checkpoint_cursor,
        checkpoint_utc,updated_utc,status,local_preview_scan_id,dropbox_preview_scan_id,
        preview_generation,operation_group_id,detail
        """;

    // The bounded publication marker that replaces the growing aggregate collection, bound to the
    // authority installation that actually owns the normalized rows.
    internal async Task<string?> LoadAuthorityInstallationIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarStringAsync(connection, null,
            "SELECT installation_id FROM installation LIMIT 1;", cancellationToken);
    }

    internal async Task<int> CountRewindSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return checked((int)await ScalarLongAsync(connection, null,
            "SELECT count(*) FROM rewind_session;", cancellationToken));
    }

    // Exact existence, so a legacy import can distinguish "absent" from "conflicting retained identity".
    internal async Task<bool> RewindSessionExistsAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(sessionId, nameof(sessionId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null,
            "SELECT count(*) FROM rewind_session WHERE session_id=$id;", cancellationToken,
            ("$id", sessionId)) == 1;
    }

    // A normalized Rewind row carries a real foreign key to its retained pair definition, so the
    // publication sequence verifies the referenced pair exists before either copy is changed.
    internal async Task<bool> PairDefinitionIsRetainedAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null,
            "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state<>'retired';", cancellationToken,
            ("$id", pairId)) == 1;
    }

    // One coherent read snapshot for the session headers and their exact reviewed sets.
    internal async Task<IReadOnlyList<SyncRewindSession>> LoadRewindSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var snapshot = connection.BeginTransaction(deferred: true);
        var headers = new List<RewindSessionHeader>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = snapshot;
            command.CommandText = "SELECT " + RewindSessionColumns +
                                  " FROM rewind_session ORDER BY checkpoint_utc DESC, session_id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) headers.Add(ReadRewindSessionHeader(reader));
        }
        if (headers.Count == 0) return [];
        var previews = await LoadRewindPreviewItemsAsync(connection, snapshot, null, cancellationToken);
        var progress = await LoadRewindPathProgressAsync(connection, snapshot, null, cancellationToken);
        var sessions = new List<SyncRewindSession>(headers.Count);
        foreach (var header in headers)
        {
            previews.TryGetValue(header.SessionId, out var items);
            progress.TryGetValue(header.SessionId, out var pathProgress);
            sessions.Add(header.ToSession(items ?? [], pathProgress ?? []));
        }
        return sessions;
    }

    // Materialized once before a loop. Never issue one whole-session read per pair.
    internal async Task<IReadOnlySet<string>> LoadActiveRewindPairIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pair_id FROM rewind_session WHERE status<>'resumed';";
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    internal async Task<SyncRewindSession?> LoadActiveRewindSessionAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var snapshot = connection.BeginTransaction(deferred: true);
        RewindSessionHeader? header = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = snapshot;
            command.CommandText = "SELECT " + RewindSessionColumns +
                " FROM rewind_session WHERE pair_id=$pair AND status<>'resumed' ORDER BY checkpoint_utc DESC, session_id LIMIT 1;";
            command.Parameters.AddWithValue("$pair", pairId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) header = ReadRewindSessionHeader(reader);
        }
        if (header is null) return null;
        var previews = await LoadRewindPreviewItemsAsync(connection, snapshot, header.SessionId, cancellationToken);
        var progress = await LoadRewindPathProgressAsync(connection, snapshot, header.SessionId, cancellationToken);
        previews.TryGetValue(header.SessionId, out var items);
        progress.TryGetValue(header.SessionId, out var pathProgress);
        return header.ToSession(items ?? [], pathProgress ?? []);
    }

    // The exact semantic equality test the legacy publication sequence uses for idempotent completion.
    // A retained session that differs in any normalized field is a conflict, never a completion.
    internal async Task<bool> RewindSessionMatchesAsync(SyncRewindSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var snapshot = connection.BeginTransaction(deferred: true);
        RewindSessionHeader? header = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = snapshot;
            command.CommandText = "SELECT " + RewindSessionColumns + " FROM rewind_session WHERE session_id=$id;";
            command.Parameters.AddWithValue("$id", session.SessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) header = ReadRewindSessionHeader(reader);
        }
        if (header is null) return false;
        var previews = await LoadRewindPreviewItemsAsync(connection, snapshot, session.SessionId, cancellationToken);
        var progress = await LoadRewindPathProgressAsync(connection, snapshot, session.SessionId, cancellationToken);
        previews.TryGetValue(session.SessionId, out var items);
        progress.TryGetValue(session.SessionId, out var pathProgress);
        var retained = header.ToSession(items ?? [], pathProgress ?? []);
        var pairIds = new HashSet<string>(StringComparer.Ordinal) { session.PairId };
        return string.Equals(
            System.Text.Json.JsonSerializer.Serialize(SyncRewindState.Normalize(retained, pairIds)),
            System.Text.Json.JsonSerializer.Serialize(SyncRewindState.Normalize(session, pairIds)),
            StringComparison.Ordinal);
    }

    // Full atomic publication of one exact session state. Used by the compatibility import and by the
    // explicit whole-state publication the existing reviewed-batch tests drive. The single-path
    // checkpoint below stays the only per-effect write.
    internal async Task PublishRewindSessionAsync(string installationId, SyncRewindSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireIdentifier(session.SessionId, nameof(session.SessionId));
        RequireIdentifier(session.PairId, nameof(session.PairId));
        var normalizedInstallation = SyncControlNamespace.NormalizeInstallationId(installationId);
        await WriteAsync(async (connection, transaction) =>
        {
            var generation = await ScalarLongAsync(connection, transaction,
                "SELECT preview_generation FROM rewind_session WHERE session_id=$id;", cancellationToken,
                ("$id", session.SessionId));
            generation = session.Preview is null ? 0 : generation + 1;
            await UpsertRewindSessionHeaderAsync(connection, transaction, normalizedInstallation, session,
                generation, cancellationToken);
            await ReplaceRewindPreviewItemsAsync(connection, transaction, session, cancellationToken);
            await ReplaceRewindPathProgressAsync(connection, transaction, session, cancellationToken);
        }, cancellationToken);
    }

    // One atomic pre-effect preview replacement. The exact expected old preview identity and an
    // eligible pre-effect state are verified before the new header, reviewed path set and current
    // preview identity are published together, and only progress owned by the superseded unexecuted
    // preview is cleared. A refused replacement writes nothing.
    internal async Task PublishRewindPreviewAsync(string installationId, SyncRewindSession published,
        string? expectedSessionId, string? expectedLocalPreviewScanId, string? expectedDropboxPreviewScanId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(published);
        RequireIdentifier(published.SessionId, nameof(published.SessionId));
        RequireIdentifier(published.PairId, nameof(published.PairId));
        var normalizedInstallation = SyncControlNamespace.NormalizeInstallationId(installationId);
        await WriteAsync(async (connection, transaction) =>
        {
            var retained = await LoadRewindSessionHeaderAsync(connection, transaction, published.SessionId,
                cancellationToken);
            if (retained is null)
            {
                if (expectedSessionId is not null)
                    throw new InvalidOperationException(
                        "The reviewed Rewind session is no longer retained. Detect and review the current preview again.");
            }
            else
            {
                if (expectedSessionId is not null &&
                    (!string.Equals(retained.SessionId, expectedSessionId, StringComparison.Ordinal) ||
                     !string.Equals(retained.LocalPreviewScanId, expectedLocalPreviewScanId, StringComparison.Ordinal) ||
                     !string.Equals(retained.DropboxPreviewScanId, expectedDropboxPreviewScanId, StringComparison.Ordinal)))
                    throw new InvalidOperationException(
                        "The retained Rewind preview changed while it was being replaced. Detect and review the current preview again; no new local effect was issued.");
                if (!SyncRewindState.PreviewIsReplaceable(retained.Status))
                    throw new InvalidOperationException(
                        "This Rewind batch has entered durable apply, so its reviewed path set cannot be replaced.");
                if (retained.OperationGroupId is not null &&
                    await ScalarLongAsync(connection, transaction,
                        "SELECT count(*) FROM operation WHERE operation_group_id=$group;", cancellationToken,
                        ("$group", retained.OperationGroupId)) != 0)
                    throw new InvalidOperationException(
                        "This Rewind batch has admitted an effect, so its reviewed path set cannot be replaced.");
            }
            var other = await ScalarStringAsync(connection, transaction,
                "SELECT session_id FROM rewind_session WHERE pair_id=$pair AND status<>'resumed' AND session_id<>$id LIMIT 1;",
                cancellationToken, ("$pair", published.PairId), ("$id", published.SessionId));
            if (other is not null)
                throw new InvalidOperationException("Another Rewind checkpoint is already active for this pair.");
            var generation = NextRewindPreviewGeneration(retained, published);
            await UpsertRewindSessionHeaderAsync(connection, transaction, normalizedInstallation, published,
                generation, cancellationToken);
            await ReplaceRewindPreviewItemsAsync(connection, transaction, published, cancellationToken);
            await ClearSupersededRewindProgressAsync(connection, transaction, published, cancellationToken);
        }, cancellationToken);
    }

    // The current preview identity is the pair of exact preview scan ids. This ordinal records which
    // publication of that identity is current, so a superseded generation is recognizable in retained
    // evidence without becoming a second authority.
    private static long NextRewindPreviewGeneration(RewindSessionHeader? retained, SyncRewindSession published) =>
        published.Preview is null ? 0 : (retained?.PreviewGeneration ?? 0) + 1;

    // Bounded header/state transition only. Progress rows are never rewritten here, so a stale
    // whole-session header save cannot regress a terminal per-path checkpoint.
    internal async Task PersistRewindSessionStateAsync(string sessionId, string pairId, SyncRewindStatus status,
        string detail, string? operationGroupId, bool clearOperationGroup,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(sessionId, nameof(sessionId));
        RequireIdentifier(pairId, nameof(pairId));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        await WriteAsync(async (connection, transaction) =>
        {
            var retained = await LoadRewindSessionHeaderAsync(connection, transaction, sessionId, cancellationToken);
            if (retained is null)
                throw new InvalidOperationException("The retained Rewind session is no longer available.");
            if (!string.Equals(retained.PairId, pairId, StringComparison.Ordinal))
                throw new InvalidOperationException("The Rewind session belongs to another sync pair.");
            var utc = Utc(DateTimeOffset.UtcNow);
            if (clearOperationGroup)
                await ExecuteAsync(connection, transaction,
                    "UPDATE rewind_session SET status=$status,detail=$detail,operation_group_id=NULL,updated_utc=$utc WHERE session_id=$id;",
                    cancellationToken, ("$status", SyncRewindState.StatusLiteral(status)), ("$detail", detail),
                    ("$utc", utc), ("$id", sessionId));
            else if (operationGroupId is null)
                await ExecuteAsync(connection, transaction,
                    "UPDATE rewind_session SET status=$status,detail=$detail,updated_utc=$utc WHERE session_id=$id;",
                    cancellationToken, ("$status", SyncRewindState.StatusLiteral(status)), ("$detail", detail),
                    ("$utc", utc), ("$id", sessionId));
            else
                await ExecuteAsync(connection, transaction,
                    "UPDATE rewind_session SET status=$status,detail=$detail,operation_group_id=$group,updated_utc=$utc WHERE session_id=$id;",
                    cancellationToken, ("$status", SyncRewindState.StatusLiteral(status)), ("$detail", detail),
                    ("$group", operationGroupId), ("$utc", utc), ("$id", sessionId));
        }, cancellationToken);
    }

    // One reviewed path checkpoint: exactly one indexed progress-row update plus the bounded header
    // state/detail in the SAME writer transaction. The path is validated against the stored reviewed
    // preview, so an unknown path, a mismatched preview or group, a foreign pair or a missing session
    // refuses before any effect-visible write.
    internal async Task PersistRewindPathCheckpointAsync(string sessionId, string pairId,
        string localPreviewScanId, string dropboxPreviewScanId, string? operationGroupId,
        SyncAuthorityReviewedPathCheckpoint checkpoint, SyncRewindStatus status, string detail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RequireIdentifier(sessionId, nameof(sessionId));
        RequireIdentifier(pairId, nameof(pairId));
        var relativePath = PilotPath.NormalizeRelative(checkpoint.RelativePath);
        var pathKey = SyncRewindState.PathKey(relativePath);
        await WriteAsync(async (connection, transaction) =>
        {
            var retained = await LoadRewindSessionHeaderAsync(connection, transaction, sessionId, cancellationToken);
            if (retained is null)
                throw new InvalidOperationException("The retained Rewind session is no longer available.");
            if (!string.Equals(retained.PairId, pairId, StringComparison.Ordinal))
                throw new InvalidOperationException("The Rewind checkpoint belongs to another sync pair.");
            if (retained.Status == SyncRewindStatus.Resumed)
                throw new InvalidOperationException("This Rewind session is closed and cannot accept a checkpoint.");
            if (!string.Equals(retained.LocalPreviewScanId, localPreviewScanId, StringComparison.Ordinal) ||
                !string.Equals(retained.DropboxPreviewScanId, dropboxPreviewScanId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "This Rewind checkpoint does not match the retained reviewed preview generation.");
            if (!string.Equals(retained.OperationGroupId, operationGroupId, StringComparison.Ordinal))
                throw new InvalidOperationException("This Rewind checkpoint does not match its admitted operation group.");
            var member = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM rewind_preview_item WHERE session_id=$id AND relative_path_key=$key;",
                cancellationToken, ("$id", sessionId), ("$key", pathKey));
            if (member != 1)
                throw new InvalidDataException("A Rewind checkpoint escaped its exact reviewed path set.");
            var previous = await ScalarStringAsync(connection, transaction,
                "SELECT status FROM rewind_path_progress WHERE session_id=$id AND relative_path_key=$key;",
                cancellationToken, ("$id", sessionId), ("$key", pathKey));
            if (previous is not null && SyncRewindState.IsTerminal(SyncRewindState.ParsePathStatus(previous)) &&
                !SyncRewindState.IsTerminal(checkpoint.Status))
                throw new InvalidOperationException(
                    "A terminal Rewind path checkpoint cannot be regressed by a stale update.");
            var utc = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO rewind_path_progress(session_id,relative_path,relative_path_key,status,operation_id,detail,updated_utc)
                VALUES($id,$path,$key,$status,$operation,$detail,$utc)
                ON CONFLICT(session_id,relative_path_key) DO UPDATE SET
                    relative_path=excluded.relative_path,status=excluded.status,operation_id=excluded.operation_id,
                    detail=excluded.detail,updated_utc=excluded.updated_utc;
                """,
                cancellationToken, ("$id", sessionId), ("$path", relativePath), ("$key", pathKey),
                ("$status", SyncRewindState.PathStatusLiteral(checkpoint.Status)),
                ("$operation", (object?)checkpoint.OperationId ?? DBNull.Value),
                ("$detail", checkpoint.Decision.Reason?.Trim() ?? string.Empty), ("$utc", utc));
            await ExecuteAsync(connection, transaction,
                "UPDATE rewind_session SET status=$status,detail=$detail,updated_utc=$utc WHERE session_id=$id;",
                cancellationToken, ("$status", SyncRewindState.StatusLiteral(status)), ("$detail", detail),
                ("$utc", utc), ("$id", sessionId));
        }, cancellationToken);
    }

    private async Task UpsertRewindSessionHeaderAsync(SqliteConnection connection, SqliteTransaction transaction,
        string installationId, SyncRewindSession session, long previewGeneration, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO rewind_session(session_id,installation_id,connection_id,pair_id,pair_name,dropbox_root,
                local_checkpoint_scan_id,dropbox_checkpoint_scan_id,dropbox_checkpoint_cursor,checkpoint_utc,
                updated_utc,status,local_preview_scan_id,dropbox_preview_scan_id,preview_generation,operation_group_id,detail)
            VALUES($id,$installation,$connection,$pair,$name,$root,$localCheckpoint,$dropboxCheckpoint,$cursor,
                $checkpointUtc,$utc,$status,$localPreview,$dropboxPreview,$generation,$group,$detail)
            ON CONFLICT(session_id) DO UPDATE SET
                installation_id=excluded.installation_id,connection_id=excluded.connection_id,pair_id=excluded.pair_id,
                pair_name=excluded.pair_name,dropbox_root=excluded.dropbox_root,
                local_checkpoint_scan_id=excluded.local_checkpoint_scan_id,
                dropbox_checkpoint_scan_id=excluded.dropbox_checkpoint_scan_id,
                dropbox_checkpoint_cursor=excluded.dropbox_checkpoint_cursor,
                updated_utc=excluded.updated_utc,status=excluded.status,
                local_preview_scan_id=excluded.local_preview_scan_id,
                dropbox_preview_scan_id=excluded.dropbox_preview_scan_id,
                preview_generation=excluded.preview_generation,operation_group_id=excluded.operation_group_id,
                detail=excluded.detail;
            """,
            cancellationToken, ("$id", session.SessionId), ("$installation", installationId),
            ("$connection", session.ConnectionId), ("$pair", session.PairId), ("$name", session.PairName),
            ("$root", PilotPath.NormalizeDropboxRoot(session.DropboxRoot)),
            ("$localCheckpoint", session.LocalCheckpointScanId),
            ("$dropboxCheckpoint", session.DropboxCheckpointScanId),
            ("$cursor", session.DropboxCheckpointCursor), ("$checkpointUtc", Utc(session.CheckpointUtc)),
            ("$utc", Utc(DateTimeOffset.UtcNow)),
            ("$status", SyncRewindState.StatusLiteral(session.Status)),
            ("$localPreview", (object?)session.LocalPreviewScanId ?? DBNull.Value),
            ("$dropboxPreview", (object?)session.DropboxPreviewScanId ?? DBNull.Value),
            ("$generation", previewGeneration),
            ("$group", (object?)session.OperationGroupId ?? DBNull.Value),
            ("$detail", (object?)session.Detail ?? DBNull.Value));
    }

    private async Task ReplaceRewindPreviewItemsAsync(SqliteConnection connection, SqliteTransaction transaction,
        SyncRewindSession session, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "DELETE FROM rewind_preview_item WHERE session_id=$id;", cancellationToken, ("$id", session.SessionId));
        foreach (var item in session.Preview ?? [])
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO rewind_preview_item(session_id,relative_path,relative_path_key,group_label,
                    proposed_effect,dropbox_changed,local_changed,reason)
                VALUES($id,$path,$key,$group,$effect,$dropboxChanged,$localChanged,$reason);
                """,
                cancellationToken, ("$id", session.SessionId),
                ("$path", PilotPath.NormalizeRelative(item.RelativePath)),
                ("$key", SyncRewindState.PathKey(item.RelativePath)), ("$group", item.Group),
                ("$effect", item.ProposedEffect), ("$dropboxChanged", item.DropboxChanged ? 1 : 0),
                ("$localChanged", item.LocalChanged ? 1 : 0), ("$reason", item.Reason));
    }

    // Clears only the progress owned by the superseded unexecuted preview, then seeds the new reviewed
    // set. Progress for a path that is still part of the replacement is preserved.
    private async Task ClearSupersededRewindProgressAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncRewindSession published, CancellationToken cancellationToken)
    {
        var retained = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT relative_path_key FROM rewind_path_progress WHERE session_id=$id;";
            command.Parameters.AddWithValue("$id", published.SessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) retained.Add(reader.GetString(0));
        }
        var keys = (published.Preview ?? []).Select(item => SyncRewindState.PathKey(item.RelativePath))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in retained.Where(key => !keys.Contains(key)))
            await ExecuteAsync(connection, transaction,
                "DELETE FROM rewind_path_progress WHERE session_id=$id AND relative_path_key=$key;",
                cancellationToken, ("$id", published.SessionId), ("$key", key));
        foreach (var item in published.Preview ?? [])
        {
            var path = PilotPath.NormalizeRelative(item.RelativePath);
            var progress = (published.PathProgress ?? []).FirstOrDefault(entry =>
                string.Equals(SyncRewindState.PathKey(entry.RelativePath), SyncRewindState.PathKey(path),
                    StringComparison.Ordinal));
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO rewind_path_progress(session_id,relative_path,relative_path_key,status,operation_id,detail,updated_utc)
                VALUES($id,$path,$key,$status,$operation,$detail,$utc)
                ON CONFLICT(session_id,relative_path_key) DO UPDATE SET
                    relative_path=excluded.relative_path,status=excluded.status,operation_id=excluded.operation_id,
                    detail=excluded.detail,updated_utc=excluded.updated_utc;
                """,
                cancellationToken, ("$id", published.SessionId), ("$path", path),
                ("$key", SyncRewindState.PathKey(path)),
                ("$status", SyncRewindState.PathStatusLiteral(
                    progress?.Status ?? SyncRewindPathStatus.Pending)),
                ("$operation", (object?)progress?.OperationId ?? DBNull.Value),
                ("$detail", progress?.Detail ?? "Reviewed and awaiting apply."),
                ("$utc", Utc(progress?.UpdatedUtc ?? DateTimeOffset.UtcNow)));
        }
    }

    private async Task ReplaceRewindPathProgressAsync(SqliteConnection connection, SqliteTransaction transaction,
        SyncRewindSession session, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "DELETE FROM rewind_path_progress WHERE session_id=$id;", cancellationToken, ("$id", session.SessionId));
        foreach (var item in session.PathProgress ?? [])
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO rewind_path_progress(session_id,relative_path,relative_path_key,status,operation_id,detail,updated_utc)
                VALUES($id,$path,$key,$status,$operation,$detail,$utc);
                """,
                cancellationToken, ("$id", session.SessionId),
                ("$path", PilotPath.NormalizeRelative(item.RelativePath)),
                ("$key", SyncRewindState.PathKey(item.RelativePath)),
                ("$status", SyncRewindState.PathStatusLiteral(item.Status)),
                ("$operation", (object?)item.OperationId ?? DBNull.Value), ("$detail", item.Detail),
                ("$utc", Utc(item.UpdatedUtc)));
    }

    private async Task<RewindSessionHeader?> LoadRewindSessionHeaderAsync(SqliteConnection connection,
        SqliteTransaction transaction, string sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + RewindSessionColumns + " FROM rewind_session WHERE session_id=$id;";
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return ReadRewindSessionHeader(reader);
    }

    private static RewindSessionHeader ReadRewindSessionHeader(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
        ParseRewindUtc(reader.GetString(9)), ParseRewindUtc(reader.GetString(10)),
        SyncRewindState.ParseStatus(reader.GetString(11)),
        reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.GetInt64(14), reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetString(16));

    private static DateTimeOffset ParseRewindUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private async Task<Dictionary<string, IReadOnlyList<SyncRewindPreviewItem>>> LoadRewindPreviewItemsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string? sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session_id,relative_path,group_label,proposed_effect,dropbox_changed,local_changed,reason
            FROM rewind_preview_item
            WHERE $id IS NULL OR session_id=$id
            ORDER BY session_id,relative_path_key;
            """;
        command.Parameters.AddWithValue("$id", (object?)sessionId ?? DBNull.Value);
        var result = new Dictionary<string, List<SyncRewindPreviewItem>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            if (!result.TryGetValue(id, out var items)) result.Add(id, items = []);
            items.Add(new SyncRewindPreviewItem(reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4) == 1, reader.GetInt64(5) == 1, reader.GetString(6)));
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<SyncRewindPreviewItem>)pair.Value,
            StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, IReadOnlyList<SyncRewindPathProgress>>> LoadRewindPathProgressAsync(
        SqliteConnection connection, SqliteTransaction transaction, string? sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session_id,relative_path,status,operation_id,detail,updated_utc
            FROM rewind_path_progress
            WHERE $id IS NULL OR session_id=$id
            ORDER BY session_id,relative_path_key;
            """;
        command.Parameters.AddWithValue("$id", (object?)sessionId ?? DBNull.Value);
        var result = new Dictionary<string, List<SyncRewindPathProgress>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            if (!result.TryGetValue(id, out var items)) result.Add(id, items = []);
            items.Add(new SyncRewindPathProgress(reader.GetString(1),
                SyncRewindState.ParsePathStatus(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                ParseRewindUtc(reader.GetString(5))));
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<SyncRewindPathProgress>)pair.Value,
            StringComparer.Ordinal);
    }

    private sealed record RewindSessionHeader(
        string SessionId, string InstallationId, string ConnectionId, string PairId, string PairName,
        string DropboxRoot, string LocalCheckpointScanId, string DropboxCheckpointScanId,
        string DropboxCheckpointCursor, DateTimeOffset CheckpointUtc, DateTimeOffset UpdatedUtc,
        SyncRewindStatus Status, string? LocalPreviewScanId, string? DropboxPreviewScanId,
        long PreviewGeneration, string? OperationGroupId, string? Detail)
    {
        internal SyncRewindSession ToSession(IReadOnlyList<SyncRewindPreviewItem> preview,
            IReadOnlyList<SyncRewindPathProgress> progress) => new(SessionId, ConnectionId, PairId, PairName,
            DropboxRoot, LocalCheckpointScanId, DropboxCheckpointScanId, DropboxCheckpointCursor, CheckpointUtc,
            Status, LocalPreviewScanId, DropboxPreviewScanId, preview, Detail, OperationGroupId, progress);
    }
}
