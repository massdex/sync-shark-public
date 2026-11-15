using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed record SyncAuthorityHistoryRetentionHold(string GraphId, long RowCount, string Reason,
    IReadOnlyDictionary<string, long> RowsByTable);

public sealed record SyncAuthorityHistoryRetentionResult(DateTimeOffset CutoffUtc, int MaximumDeletedRows,
    int MaximumInspectedRows, long MaximumLoadedValueBytes, long InspectedRows, long LoadedValueBytes,
    long SelectedRows, IReadOnlyDictionary<string, long> RowsByTable,
    IReadOnlyDictionary<string, long> KeptReasons, IReadOnlyList<SyncAuthorityHistoryRetentionHold> HeldGraphs,
    bool Applied);

public sealed class SyncAuthorityHistoryRetentionAdmissionException(string reason, long inspectedRows,
    long? loadedValueBytes, int maximumInspectedRows, long maximumLoadedValueBytes)
    : InvalidOperationException("History graph admission refused: " + reason + ". No rows or guards were changed.")
{
    public string Reason { get; } = reason;
    public long InspectedRows { get; } = inspectedRows;
    // Row-budget refusal is a counted prefix/lower bound, not an invented total for the database.
    public bool RowCountIsComplete { get; } = loadedValueBytes is not null;
    public long? LoadedValueBytes { get; } = loadedValueBytes;
    public int MaximumInspectedRows { get; } = maximumInspectedRows;
    public long MaximumLoadedValueBytes { get; } = maximumLoadedValueBytes;
}

// Deliberately no scheduler, setting/default caller, CLI activation or schema migration.
// A caller must supply a finite ceiling; the 20k target NEVER splits a complete inventory.
// Replay identities remain authority, not disposable log rows. Unknown graphs are held.
public sealed partial class SyncAuthorityDatabase
{
    private const int HistoryRetentionDays = 90;
    private const int HistoryRetentionBatchTarget = 20_000;
    internal Action<string>? HistoryRetentionBoundaryForTest { get; set; }

    public Task<SyncAuthorityHistoryRetentionResult> PlanHistoryRetentionAsync(int maximumDeletedRows,
        int maximumInspectedRows, long maximumLoadedValueBytes,
        CancellationToken cancellationToken = default) =>
        HistoryRetentionAsync(maximumDeletedRows, maximumInspectedRows, maximumLoadedValueBytes, apply: false, cancellationToken);

    public Task<SyncAuthorityHistoryRetentionResult> ExpireHistoryAsync(int maximumDeletedRows,
        int maximumInspectedRows, long maximumLoadedValueBytes,
        CancellationToken cancellationToken = default) =>
        HistoryRetentionAsync(maximumDeletedRows, maximumInspectedRows, maximumLoadedValueBytes, apply: true, cancellationToken);

    private async Task<SyncAuthorityHistoryRetentionResult> HistoryRetentionAsync(int maximumDeletedRows,
        int maximumInspectedRows, long maximumLoadedValueBytes, bool apply, CancellationToken token)
    {
        if (maximumDeletedRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDeletedRows),
            "Supply a positive finite maximum deleted-row ceiling.");
        if (maximumInspectedRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumInspectedRows));
        if (maximumLoadedValueBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLoadedValueBytes));
        // Maintenance owns its one commit. Never borrow a queued/shared effect transaction.
        if (_executionConnectionScope.Value is { Active: true })
            throw new InvalidOperationException("History retention cannot run in an effect/execution connection scope.");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-HistoryRetentionDays);
        await _writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(token, reuseConnection: false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var admission = await AdmitHistoryGraphAsync(connection, transaction, maximumInspectedRows,
                maximumLoadedValueBytes, token);
            HistoryRetentionBoundaryForTest?.Invoke("graph-admitted-before-materialization");
            var graph = await LoadHistoryRetentionGraphAsync(connection, transaction, cutoff, token);
            graph.Propagate(token);
            var selected = graph.Select(maximumDeletedRows, token);
            var counts = HistoryCounts(selected);
            var heldReasons = graph.Rows.Values.Where(row => row.Kept)
                .SelectMany(row => row.Reasons).GroupBy(reason => reason, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.Ordinal);
            var result = new SyncAuthorityHistoryRetentionResult(cutoff, maximumDeletedRows,
                maximumInspectedRows, maximumLoadedValueBytes, admission.Rows, admission.ValueBytes, selected.Count,
                HistoryReadOnly(counts), HistoryReadOnly(heldReasons), graph.Holds.ToArray(), apply);
            if (!apply || selected.Count == 0)
            {
                await transaction.RollbackAsync(token);
                return result;
            }
            token.ThrowIfCancellationRequested();
            HistoryRetentionBoundaryForTest?.Invoke("selected-before-delete-guards");
            // SQLite holds the immediate writer transaction across selection, count, revalidation,
            // DDL, row deletion, restoration and commit. Other connections cannot admit a reference.
            graph.RequireClosedSelection(selected);
            var guards = await CaptureHistoryDeleteGuardsAsync(connection, transaction, selected, token);
            foreach (var guard in guards)
                await ExecuteAsync(connection, transaction, "DROP TRIGGER " + HistoryQuote(guard.Name) + ";", token);
            HistoryRetentionBoundaryForTest?.Invoke("delete-guards-suspended");
            var remaining = selected.ToHashSet();
            long deleted = 0;
            while (remaining.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                // Only relational incoming references constrain SQL delete order. Soft cycles have
                // already been selected as one graph and must not force partial commits.
                var ready = remaining.Where(row => !row.RelationalUsers.Any(remaining.Contains)).ToArray();
                if (ready.Length == 0) throw new InvalidDataException("A selected authority graph has a foreign-key cycle.");
                foreach (var table in ready.GroupBy(row => row.Table, StringComparer.Ordinal))
                {
                    foreach (var chunk in table.Chunk(400))
                    {
                        var keys = JsonSerializer.Serialize(chunk.Select(row => row.RowId));
                        var actual = await ExecuteCountAsync(connection, transaction,
                            "DELETE FROM " + HistoryQuote(table.Key) +
                            " WHERE rowid IN (SELECT value FROM json_each($rows));",
                            token, ("$rows", keys));
                        if (actual != chunk.Length)
                            throw new InvalidDataException("The counted authority graph changed inside retention.");
                        deleted = checked(deleted + actual);
                        if (deleted > maximumDeletedRows)
                            throw new InvalidDataException("History retention exceeded its explicit deletion ceiling.");
                        foreach (var row in chunk) remaining.Remove(row);
                        HistoryRetentionBoundaryForTest?.Invoke("rows-deleted");
                    }
                }
            }
            if (deleted != result.SelectedRows) throw new InvalidDataException("History expiry row counts disagree.");
            foreach (var guard in guards) await ExecuteAsync(connection, transaction, guard.Sql, token);
            await VerifyHistoryDeleteGuardsAsync(connection, transaction, guards, token);
            if (await ScalarStringAsync(connection, transaction, "PRAGMA foreign_key_check;", token) is not null)
                throw new InvalidDataException("History expiry broke an authority foreign key.");
            var inventory = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT lower(type)||':'||name FROM sqlite_schema " +
                    "WHERE type IN ('table','index','trigger','view') AND name NOT LIKE 'sqlite_%' ORDER BY lower(type),name;";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) inventory.Add(reader.GetString(0));
            }
            if (!inventory.SequenceEqual(ExpectedCurrentSchemaObjects, StringComparer.Ordinal) ||
                await ScalarLongAsync(connection, transaction, "PRAGMA user_version;", token) != SyncAuthoritySchema.Version)
                throw new InvalidDataException("Retention did not restore the exact authority schema inventory/version.");
            HistoryRetentionBoundaryForTest?.Invoke("guards-restored-before-commit");
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(token);
            // Retained complete-cut cache entries can name an expired cut. Invalidate, never rebind.
            _completeExcludedPaths.Clear();
            _planningContentFingerprints.Clear();
            return result;
        }
        finally { _writer.Release(); }
    }

    private static async Task<(long Rows, long ValueBytes)> AdmitHistoryGraphAsync(SqliteConnection connection,
        SqliteTransaction transaction, int maximumRows, long maximumBytes, CancellationToken token)
    {
        var tables = ExpectedCurrentSchemaObjects.Where(name => name.StartsWith("table:", StringComparison.Ordinal))
            .Select(name => name[6..]).ToArray();
        long rows = 0;
        // Count FIRST. An over-row-budget database is rejected before even the value-byte scan,
        // and before ANY row/value dictionaries, JSON trees, edges or copy projections are loaded.
        foreach (var table in tables)
        {
            rows = checked(rows + await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM " + HistoryQuote(table) + ";", token));
            if (rows > maximumRows)
                throw new SyncAuthorityHistoryRetentionAdmissionException("graph_row_budget", rows, null, maximumRows, maximumBytes);
        }
        long bytes = 0;
        foreach (var table in tables)
        {
            var columns = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "PRAGMA table_info(" + HistoryQuote(table) + ");";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) columns.Add(reader.GetString(1));
            }
            var sum = string.Join("+", columns.Select(column => "coalesce(length(CAST(" + HistoryQuote(column) + " AS BLOB)),0)"));
            bytes = checked(bytes + await ScalarLongAsync(connection, transaction,
                "SELECT coalesce(sum(" + sum + "),0) FROM " + HistoryQuote(table) + ";", token));
            if (bytes > maximumBytes)
                throw new SyncAuthorityHistoryRetentionAdmissionException("graph_value_byte_budget", rows, bytes, maximumRows, maximumBytes);
        }
        // These bound rows and serialized value bytes, NOT measured CLR resident memory. Metadata,
        // node/edge/JSON overhead and SQLite's page cache remain an activation resource assessment.
        return (rows, bytes);
    }

    private sealed class HistoryRow(string table, long rowId, Dictionary<string, object?> values)
    {
        internal string Table { get; } = table;
        internal long RowId { get; } = rowId;
        internal Dictionary<string, object?> Values { get; } = values;
        internal HashSet<HistoryRow> Dependencies { get; } = [];
        internal HashSet<HistoryRow> RelationalUsers { get; } = [];
        internal HashSet<HistoryRow> AuditChildren { get; } = [];
        internal HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);
        internal bool Kept;
        internal bool FullAudit;
        internal bool Visited;
        internal bool AuditVisited;
        internal DateTimeOffset Age;
        internal string Text(string name) => Values.TryGetValue(name, out var value) && value is string text ? text : "";
        internal long Number(string name) => Values.TryGetValue(name, out var value) && value is long number ? number : 0;
        internal string Key => Table + ":" + RowId.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class HistoryGraph
    {
        internal Dictionary<(string Table, long Id), HistoryRow> Rows { get; } = [];
        internal Dictionary<string, List<HistoryRow>> Tables { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, List<HistoryRow>> Identities { get; } = new(StringComparer.Ordinal);
        internal List<SyncAuthorityHistoryRetentionHold> Holds { get; } = [];
        private readonly Queue<HistoryRow> _pending = new();
        internal IEnumerable<HistoryRow> Table(string table) => Tables.GetValueOrDefault(table) ?? [];
        internal void Pin(HistoryRow row, string reason, bool fullAudit = true)
        {
            row.Reasons.Add(reason);
            if (!row.Kept || fullAudit && !row.FullAudit)
            {
                row.Kept = true;
                row.FullAudit |= fullAudit;
                _pending.Enqueue(row);
            }
        }
        internal void Link(HistoryRow child, HistoryRow parent, bool relational = false)
        {
            child.Dependencies.Add(parent);
            if (relational) parent.RelationalUsers.Add(child);
        }
        internal void Together(IEnumerable<HistoryRow> rows)
        {
            HistoryRow? first = null;
            foreach (var row in rows)
            {
                if (first is null) first = row;
                else { Link(first, row); Link(row, first); }
            }
        }
        internal void Propagate(CancellationToken token)
        {
            while (_pending.TryDequeue(out var row))
            {
                token.ThrowIfCancellationRequested();
                if (!row.Visited)
                {
                    row.Visited = true;
                    foreach (var parent in row.Dependencies)
                        Pin(parent, "retained_reference", row.FullAudit);
                }
                // A terminal operation's replay identity alone does not keep its entire disposable
                // audit. A retained live/current consumer DOES keep every exact audit child.
                if (row.FullAudit && !row.AuditVisited)
                {
                    row.AuditVisited = true;
                    foreach (var parent in row.Dependencies) Pin(parent, "retained_reference");
                    foreach (var child in row.AuditChildren) Pin(child, "retained_operation_graph");
                }
            }
        }
        internal List<HistoryRow> Select(int maximum, CancellationToken token)
        {
            var eligible = Rows.Values.Where(row => !row.Kept).ToHashSet();
            var neighbours = eligible.ToDictionary(row => row, _ => new HashSet<HistoryRow>());
            foreach (var row in eligible)
                foreach (var parent in row.Dependencies)
                    if (eligible.Contains(parent))
                    { neighbours[row].Add(parent); neighbours[parent].Add(row); }
            var components = new List<List<HistoryRow>>();
            while (eligible.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                var first = eligible.First();
                var queue = new Queue<HistoryRow>(); queue.Enqueue(first); eligible.Remove(first);
                var component = new List<HistoryRow>();
                while (queue.TryDequeue(out var row))
                {
                    component.Add(row);
                    foreach (var neighbour in neighbours[row])
                        if (eligible.Remove(neighbour)) queue.Enqueue(neighbour);
                }
                components.Add(component);
            }
            var selected = new List<HistoryRow>();
            foreach (var component in components.OrderBy(group => group.Min(row => row.Age))
                         .ThenBy(group => group.Min(row => row.Key), StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var reason = component.Count > maximum ? "oversize_complete_graph" :
                    component.Count > maximum - selected.Count ? "transaction_ceiling" :
                    selected.Count >= HistoryRetentionBatchTarget ? "soft_batch_target" : null;
                if (reason is not null)
                    Holds.Add(new(component.Select(row => row.Key).Order(StringComparer.Ordinal).First(), component.Count, reason,
                        HistoryReadOnly(HistoryCounts(component))));
                else selected.AddRange(component);
            }
            return selected;
        }
        internal void RequireClosedSelection(IReadOnlyCollection<HistoryRow> selection)
        {
            var chosen = selection.ToHashSet();
            foreach (var row in Rows.Values)
                if (!chosen.Contains(row) && row.Dependencies.Any(chosen.Contains))
                    throw new InvalidDataException("A retained authority row references the selected expiry graph.");
            foreach (var row in selection)
                if (row.Kept) throw new InvalidDataException("Retained evidence entered an expiry graph.");
        }
    }

    private async Task<HistoryGraph> LoadHistoryRetentionGraphAsync(SqliteConnection connection,
        SqliteTransaction transaction, DateTimeOffset cutoff, CancellationToken token)
    {
        var graph = new HistoryGraph();
        var tables = ExpectedCurrentSchemaObjects.Where(name => name.StartsWith("table:", StringComparison.Ordinal))
            .Select(name => name[6..]).ToArray();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT rowid AS retention_rowid,* FROM " + HistoryQuote(table) + ";";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var index = 1; index < reader.FieldCount; index++)
                    values.Add(reader.GetName(index), reader.IsDBNull(index) ? null : reader.GetValue(index));
                var row = new HistoryRow(table, reader.GetInt64(0), values);
                graph.Rows.Add((table, row.RowId), row);
                if (!graph.Tables.TryGetValue(table, out var tableRows)) graph.Tables.Add(table, tableRows = []);
                tableRows.Add(row);
                // Only single-column, owner identity columns enter the soft-reference lookup below.
                var idColumn = HistoryIdentityColumn(table);
                if (idColumn is not null && values.TryGetValue(idColumn, out var idValue) && idValue is string id)
                {
                    if (!graph.Identities.TryGetValue(id, out var owners)) graph.Identities.Add(id, owners = []);
                    owners.Add(row);
                }
            }
        }
        foreach (var table in tables)
        {
            var foreignKeys = new List<(long Id, long Sequence, string Parent, string From, string To)>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA foreign_key_list(" + HistoryQuote(table) + ");";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    if (reader.IsDBNull(4)) throw new InvalidDataException("An unclassified implicit authority FK retains history.");
                    foreignKeys.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4)));
                }
            }
            foreach (var fk in foreignKeys.GroupBy(item => item.Id))
            {
                var parts = fk.OrderBy(item => item.Sequence).ToArray();
                var parent = parts[0].Parent;
                if (!tables.Contains(parent, StringComparer.Ordinal))
                    throw new InvalidDataException("An authority FK names an unclassified table.");
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT c.rowid,p.rowid FROM " + HistoryQuote(table) + " c JOIN " +
                    HistoryQuote(parent) + " p ON " + string.Join(" AND ", parts.Select(part =>
                        "c." + HistoryQuote(part.From) + "=p." + HistoryQuote(part.To))) + ";";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    graph.Link(graph.Rows[(table, reader.GetInt64(0))], graph.Rows[(parent, reader.GetInt64(1))], true);
            }
        }
        var operations = graph.Table("operation").ToDictionary(row => row.Text("operation_id"), StringComparer.Ordinal);
        foreach (var row in graph.Rows.Values)
        {
            token.ThrowIfCancellationRequested();
            var owner = row.Text("operation_id");
            if (row.Table != "operation" && operations.TryGetValue(owner, out var operation))
                operation.AuditChildren.Add(row);
            var dateColumn = HistoryDateColumn(row);
            var dateText = dateColumn is null ? "" : row.Text(dateColumn);
            if (dateColumn == "operation_terminal")
                dateText = operations.GetValueOrDefault(owner)?.Text("terminal_utc") ?? "";
            if (!DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var age))
            { row.Age = DateTimeOffset.MaxValue; graph.Pin(row, "permanent_or_unclassified_authority"); }
            else
            {
                row.Age = age;
                if (age >= cutoff) graph.Pin(row, "recent_audit");
                if (!HistoryTerminalEligible(row, operations)) graph.Pin(row, "unresolved_or_live_authority");
            }
            if (row.Table is "operation" or "command_request")
                graph.Pin(row, "replay_identity", fullAudit: row.Table == "operation" &&
                    (!HistoryOperationTerminal(row) || age >= cutoff));
            // AppendActivityEventsAsync uses the original payload as its replay/collision barrier.
            // This unchanged schema has no compact retired-event identity store. Keep the barrier
            // rather than silently converting an old replay into a new event after reopen.
            if (row.Table == "history_event" && row.Text("event_kind") == "presentation")
                graph.Pin(row, "presentation_replay_identity", fullAudit: false);
            // pair_id/source_event_key is the durable monitor ingestion deduplication barrier.
            // Cursor advancement alone has not proved that an old provider/native input cannot replay.
            if (row.Table == "monitor_event") graph.Pin(row, "monitor_event_replay_identity", fullAudit: false);
            foreach (var pair in row.Values)
                if (pair.Value is string json && (pair.Key.EndsWith("_json", StringComparison.Ordinal) ||
                    pair.Key is "bound_identity" or "intended_parent_identity" or "parent_identity") &&
                    (pair.Key.EndsWith("_json", StringComparison.Ordinal) || json.TrimStart().StartsWith('{')))
                    HistoryJsonReferences(graph, row, pair.Key, json);
            foreach (var pair in row.Values)
                if (pair.Value is string value && !pair.Key.EndsWith("_json", StringComparison.Ordinal) &&
                    pair.Key.EndsWith("_id", StringComparison.Ordinal) && pair.Key != HistoryIdentityColumn(row.Table) &&
                    !(row.Table == "lease_fence" && pair.Key == "scope_id"))
                    HistoryReference(graph, row, pair.Key, value, explicitProperty: false);
        }
        // Every complete inventory is indivisible, including uncertain/null-member observations.
        var observationsByScan = graph.Table("observation").ToLookup(row => row.Text("scan_generation_id"), StringComparer.Ordinal);
        var observationsByVersion = graph.Table("observation").Where(row => row.Number("complete_generation") == 1)
            .ToLookup(row => row.Text("object_version_id"), StringComparer.Ordinal);
        var versionsByObjectSide = graph.Table("object_version").ToLookup(row =>
            (row.Text("object_id"), row.Text("side")));
        foreach (var scan in graph.Table("scan_generation"))
            graph.Together(observationsByScan[scan.Text("scan_generation_id")].Prepend(scan));
        foreach (var operation in operations.Values)
            graph.Together(operation.AuditChildren);
        // A legacy blocked restore command can reference only its source, with no separate consumer
        // operation. Keep the entire exact JSON-linked source/preimage family together, even when an
        // older release marker labels one family member terminal. A marker is not uncertainty proof.
        foreach (var artifact in graph.Table("recovery_artifact"))
            foreach (var source in artifact.Dependencies.Where(row => row.Table == "recovery_artifact").ToArray())
                graph.Link(source, artifact);
        // Latest scans and complete version evidence, current baselines, generation/cursor high water.
        HistoryPinRanked(graph, "scan_generation", ["pair_id", "side"], "generation", 2,
            row => row.Text("status") == "complete", "latest_two_complete_scans");
        HistoryPinRanked(graph, "scan_generation", ["root_binding_id"], "generation", 1, _ => true, "scan_generation_high_water");
        HistoryPinRanked(graph, "membership", ["root_binding_id", "relative_path_key"], "generation", 1, _ => true, "membership_generation_high_water");
        HistoryPinRanked(graph, "synchronization_point", ["pair_id", "object_id"], "authority_generation", 1, _ => true, "current_baseline");
        HistoryPinRanked(graph, "conflict", ["pair_id", "object_id"], "conflict_generation", 1, _ => true, "conflict_generation_high_water");
        var highHistory = graph.Table("history_event").MaxBy(row => row.RowId);
        if (highHistory is not null) graph.Pin(highHistory, "activity_sequence_high_water");
        foreach (var version in graph.Table("object_version"))
        {
            var evidence = observationsByVersion[version.Text("object_version_id")]
                .OrderByDescending(row => row.Text("observed_utc"), StringComparer.Ordinal)
                .ThenByDescending(row => row.Text("observation_id"), StringComparer.Ordinal).FirstOrDefault();
            if (evidence is not null) graph.Link(version, evidence);
        }
        // Origin is needed for any logical object that remains, not just its latest versions.
        foreach (var logical in graph.Table("logical_object"))
            foreach (var side in new[] { "local", "dropbox" })
            {
                var origin = versionsByObjectSide[(logical.Text("object_id"), side)]
                    .SelectMany(version => observationsByVersion[version.Text("object_version_id")])
                    .OrderBy(row => row.Number("scan_generation"))
                    .ThenBy(row => row.Text("observed_utc"), StringComparer.Ordinal)
                    .ThenBy(row => row.Text("observation_id"), StringComparer.Ordinal).FirstOrDefault();
                if (origin is not null) graph.Link(logical, origin);
            }
        // Preserve the whole exact context set. Removing an ambiguous competing context is unsafe.
        var contextsByKey = graph.Table("history_event").Where(row => row.Text("event_kind") == "presentation")
            .ToLookup(HistoryContextKey, StringComparer.Ordinal);
        foreach (var contexts in contextsByKey.Where(group => group.Key.Length != 0))
            graph.Together(contexts);
        foreach (var row in graph.Rows.Values.Where(row => row.Table is "history_event" or "reconciliation_decision"))
        {
            var operationId = row.Table == "history_event" ? row.Text("operation_id") :
                HistoryJsonText(row.Text("decision_json"), "operationId");
            if (operationId.Length == 0 && row.Table == "history_event")
                operationId = HistoryJsonText(row.Text("detail_json"), "OperationId");
            var pairId = row.Table == "history_event" ? HistoryJsonText(row.Text("detail_json"), "PairId") : row.Text("pair_id");
            if (pairId.Length == 0 && operations.TryGetValue(operationId, out var operation)) pairId = operation.Text("pair_id");
            if (pairId.Length != 0 && operationId.Length != 0)
                foreach (var context in contextsByKey[pairId + "\n" + operationId])
                    graph.Link(row, context);
        }
        // ReadCompletedCopyMovesAsync can derive many point/member combinations. Do NOT materialize
        // that unbounded projection on top of the admitted rows. Retain its conservative row superset:
        // every completed create_local graph and every same-pair/object point, including prior points.
        // The actual consumer necessarily requires that exact operation kind/status/pair/object;
        // point/member/version/receipt edges then preserve every qualifying copy candidate. This
        // intentionally over-retains non-copy creates rather than inventing a scalable query here.
        var pointsByObject = graph.Table("synchronization_point").ToLookup(row => (row.Text("pair_id"), row.Text("object_id")));
        // This source remains excluded from the product. Verified compact rows
        // pin their exact foreign-key references; unnormalized objects retain the
        // original conservative history hold. This does not activate pruning.
        var normalizedCopies = graph.Table("current_copy_completion")
            .Where(row => row.Number("coverage_verified") == 1)
            .Select(row => (row.Text("pair_id"), row.Text("object_id"))).ToHashSet();
        foreach (var row in graph.Table("current_copy_completion").Concat(graph.Table("copy_predecessor")))
            graph.Pin(row, "current_completed_copy_proof");
        foreach (var operation in operations.Values.Where(row => row.Text("kind") == "create_local" && row.Text("status") == "completed"))
        {
            if (normalizedCopies.Contains((operation.Text("pair_id"), operation.Text("object_id")))) continue;
            graph.Pin(operation, "potential_completed_copy_evidence");
            foreach (var point in pointsByObject[(operation.Text("pair_id"), operation.Text("object_id"))])
                graph.Pin(point, "potential_completed_copy_evidence");
        }
        return graph;
    }

    private static void HistoryPinRanked(HistoryGraph graph, string table, string[] keys, string rank,
        int count, Func<HistoryRow, bool> predicate, string reason)
    {
        foreach (var group in graph.Table(table).Where(predicate).GroupBy(row =>
                     string.Join("\u001f", keys.Select(row.Text)), StringComparer.Ordinal))
            foreach (var row in group.OrderByDescending(row => row.Number(rank)).ThenByDescending(row => row.RowId).Take(count))
                graph.Pin(row, reason);
    }

    private static string? HistoryDateColumn(HistoryRow row) => row.Table switch
    {
        "scan_generation" => "completed_utc",
        "observation" or "membership" or "object_version" => "observed_utc",
        "logical_object" => "created_utc",
        "synchronization_point" => "accepted_utc",
        "reconciliation_decision" => "decided_utc",
        "history_event" => "occurred_utc",
        "operation" or "operation_group" or "command_request" or "operation_step" or "conflict_resolution" => "terminal_utc",
        "operation_member" or "operation_dependency" => "operation_terminal",
        "operation_attempt" => "ended_utc",
        "effect_receipt" => "observed_utc",
        "resource_allocation" or "namespace_allocation" => "created_utc",
        "upload_session_checkpoint" => "updated_utc",
        "lease" => "expires_utc",
        "recovery_artifact" => "released_utc",
        "conflict" => "resolved_utc",
        "tombstone" or "tombstone_observation" => null, // no durable terminal time: explicit hold
        // S10: the detached activity-context snapshot is deliberately held, never expiry-eligible in this
        // lane; removing one stays a separate bounded step authorised by zero reverse references.
        "activity_context" => null,
        "monitor_event" => "consumed_utc",
        "dirty_item" => "latest_event_utc",
        _ => null
    };

    private static bool HistoryOperationTerminal(HistoryRow row) =>
        row.Text("status") is "completed" or "cancelled" or "superseded";

    private static bool HistoryTerminalEligible(HistoryRow row, Dictionary<string, HistoryRow> operations)
    {
        bool TerminalOwner() => operations.TryGetValue(row.Text("operation_id"), out var owner) && HistoryOperationTerminal(owner);
        return row.Table switch
        {
            "scan_generation" => row.Text("status") is "complete" or "failed" or "superseded",
            "observation" or "membership" or "object_version" or "logical_object" or "synchronization_point" or
                "reconciliation_decision" or "history_event" => true,
            "operation" => HistoryOperationTerminal(row),
            "operation_group" => row.Text("status") is "completed" or "cancelled",
            "command_request" => row.Text("status") is "completed" or "rejected" or "stale" or "cancelled",
            "operation_member" or "operation_dependency" => TerminalOwner(),
            "operation_step" => TerminalOwner() && row.Text("status") is "completed" or "cancelled" or "superseded",
            "operation_attempt" => TerminalOwner() && row.Text("status") is "verified_success" or "verified_not_applied",
            // Historic unknown/partial receipts remain conservatively held until an exact resolution
            // predicate is separately proved; a terminal label alone is not that proof.
            "effect_receipt" => row.Text("certainty") is "known_success" or "known_not_applied",
            "resource_allocation" or "namespace_allocation" => TerminalOwner() && row.Text("status") == "released",
            "upload_session_checkpoint" => TerminalOwner() && row.Text("status") is "completed" or "abandoned",
            "recovery_artifact" => row.Text("status") == "released",
            "conflict" => row.Text("status") is "resolved" or "superseded",
            "conflict_resolution" => row.Text("status") is "completed" or "stale" or "cancelled",
            "monitor_event" => row.Text("consumed_utc").Length != 0,
            "dirty_item" => row.Text("status") == "closed",
            "lease" => row.Text("status") is "released" or "expired" or "revoked",
            _ => false
        };
    }

    private static string? HistoryIdentityColumn(string table) => table switch
    {
        "installation" => "installation_id", "capability_profile" => "capability_profile_id",
        "sync_pair" => "pair_id", "root_binding" => "root_binding_id", "logical_object" => "object_id",
        "object_version" => "object_version_id", "membership" => "membership_id", "observation" => "observation_id",
        "synchronization_point" => "synchronization_point_id", "operation" => "operation_id",
        "operation_member" => "operation_member_id", "resource_allocation" => "allocation_id", "lease" => "lease_id",
        "operation_attempt" => "attempt_id", "effect_receipt" => "effect_receipt_id", "history_event" => "history_event_id",
        "process_instance" => "process_instance_id", "provider_account" => "provider_account_id",
        "recovery_root_binding" => "recovery_root_binding_id", "volume" => "volume_id", "scan_generation" => "scan_generation_id",
        "monitor_cursor" => "monitor_cursor_id", "monitor_event" => "monitor_event_id", "dirty_item" => "dirty_item_id",
        "reconciliation_decision" => "reconciliation_decision_id", "conflict" => "conflict_id",
        "operation_group" => "operation_group_id", "operation_step" => "operation_step_id",
        "namespace_allocation" => "namespace_allocation_id", "control_namespace" => "control_namespace_id",
        "recovery_artifact" => "recovery_artifact_id", "tombstone" => "tombstone_id",
        "conflict_resolution" => "conflict_resolution_id", "command_request" => "command_request_id",
        "migration_import" => "migration_import_id",
        // S10: the detached immutable activity-context snapshot identity participates in the shared
        // soft-reference lookup, so a retained record's exact snapshot is held by that record.
        "activity_context" => "context_key", _ => null
    };

    private static string? HistoryReferenceTable(string property) => property.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "operationid" or "operationids" or "dependsonoperationid" or "copyoperationid" => "operation",
        "commandrequestid" or "restorecommandid" or "runid" or "retryofrunid" => "command_request",
        "scanid" or "scangenerationid" or "localscangenerationid" or "dropboxscangenerationid" or "localscan" or "remotescan" => "scan_generation",
        "synchronizationpointid" or "priorpointid" or "currentpointid" => "synchronization_point",
        "objectid" => "logical_object", "objectversionid" or "sourceobjectversionid" or "localobjectversionid" or
            "dropboxobjectversionid" or "oldversionid" or "newversionid" or "oldremoteversionid" or "newremoteversionid" => "object_version",
        "membershipid" or "priormembershipid" or "localmembershipid" or "dropboxmembershipid" or "oldmembershipid" or "newmembershipid" => "membership",
        "recoveryartifactid" or "sourcerecoveryartifactid" => "recovery_artifact",
        "allocationid" or "recoveryallocationid" => "resource_allocation",
        "attemptid" => "operation_attempt", "effectreceiptid" => "effect_receipt", "observationid" => "observation",
        "conflictid" => "conflict", "conflictresolutionid" => "conflict_resolution",
        "operationgroupid" => "operation_group", "rootbindingid" or "localrootbindingid" or "dropboxrootbindingid" or
            "endpointrootbindingid" or "sourcerootbindingid" or "destinationrootbindingid" => "root_binding",
        "pairid" or "sourcepairid" or "destinationpairid" => "sync_pair",
        "provideraccountid" or "connectionid" => "provider_account",
        "installationid" => "installation", "capabilityprofileid" => "capability_profile",
        "recoveryrootbindingid" => "recovery_root_binding", "volumeid" => "volume",
        "processinstanceid" => "process_instance",
        "controlnamespaceid" => "control_namespace", "namespaceallocationid" => "namespace_allocation",
        "leaseid" or "recordedleaseid" or "transitionleaseid" or "currentleaseid" => "lease",
        "lasthistoryeventid" => "history_event", "monitorcursorid" => "monitor_cursor",
        // CCS7: the retained activity context reference is an understood history-event dependency, so the
        // full-context anchor a newer presentation row references is held by that dependent row and can
        // never become eligible for expiry alone. This is source compatibility only: it neither activates nor
        // reopens M7, and it changes no destructive selection, guard or migration semantics.
        "endpointcontexteventid" => "history_event",
        // S10: the detached immutable activity-context snapshot a schema 10 record references is an understood
        // dependency of that exact record, so a retained event holds its snapshot through the same registry the
        // legacy anchor uses. This is source compatibility only: it neither activates nor reopens retention,
        // and it changes no destructive selection, guard or migration semantics.
        "endpointcontextkey" => "activity_context",
        _ => null
    };

    private static void HistoryReference(HistoryGraph graph, HistoryRow owner, string property,
        string value, bool explicitProperty)
    {
        if (value.Length == 0) return;
        var table = HistoryReferenceTable(property);
        // A new ID-shaped JSON field must not silently become deletable just because its target
        // is absent or its spelling is not in this source-controlled reference vocabulary.
        var key = property.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        if (explicitProperty && table is null && (key.EndsWith("id", StringComparison.Ordinal) ||
            key.EndsWith("ids", StringComparison.Ordinal)) && !HistoryExternalIdentityKeys.Contains(key))
            graph.Pin(owner, "unclassified_json_reference");
        if (graph.Identities.TryGetValue(value, out var matches))
        {
            var selected = table is null ? matches : matches.Where(row => row.Table == table).ToList();
            foreach (var row in selected)
                if (!ReferenceEquals(row, owner)) graph.Link(owner, row);
            if (table is null && explicitProperty) graph.Pin(owner, "unclassified_json_reference");
            if (table is not null && selected.Count == 0 && explicitProperty)
                graph.Pin(owner, "unavailable_json_reference");
        }
        else if (table is not null && explicitProperty) graph.Pin(owner, "unavailable_json_reference");
    }

    // Provider/native identity scalars, not authority-row references. Existing matching authority
    // identities are still linked below, so even an accidental collision cannot orphan a row.
    private static readonly HashSet<string> HistoryExternalIdentityKeys = new(StringComparer.Ordinal)
    {
        "fileid", "folderid", "parentid", "parentfolderid", "rootnamespaceid", "homenamespaceid",
        "endpointnamespaceid", "namespaceid", "accountid", "providerfileid", "retainedfileid",
        "retainedparentfolderid", "stagefileid", "clientid", "eventid", "workerid"
    };

    private static void HistoryJsonReferences(HistoryGraph graph, HistoryRow row, string column, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            { graph.Pin(row, "unclassified_json_shape"); return; }
            if (row.Table == "object_version" && column == "identity_json" && !HistoryKnownObjectIdentity(document.RootElement))
                graph.Pin(row, "unclassified_object_identity");
            Walk(document.RootElement, "", 0);
            void Walk(JsonElement element, string property, int depth)
            {
                if (depth > 64) { graph.Pin(row, "unclassified_json_depth"); return; }
                if (element.ValueKind == JsonValueKind.Object)
                    foreach (var item in element.EnumerateObject()) Walk(item.Value, item.Name, depth + 1);
                else if (element.ValueKind == JsonValueKind.Array)
                    foreach (var item in element.EnumerateArray()) Walk(item, property, depth + 1);
                else if (element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString()!;
                    HistoryReference(graph, row, property, value, true);
                    if (value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('['))
                    {
                        try { using var nested = JsonDocument.Parse(value); Walk(nested.RootElement, property, depth + 1); }
                        catch (JsonException) { graph.Pin(row, "unclassified_embedded_json"); }
                    }
                }
                else if (HistoryReferenceTable(property) is not null && element.ValueKind != JsonValueKind.Null)
                    graph.Pin(row, "unclassified_reference_type");
            }
        }
        catch (JsonException) { graph.Pin(row, "malformed_json"); }
    }

    private static bool HistoryKnownObjectIdentity(JsonElement identity) =>
        identity.ValueKind == JsonValueKind.Object &&
        (identity.TryGetProperty("fileId", out var file) && file.ValueKind == JsonValueKind.String ||
         identity.TryGetProperty("FileId", out var legacy) && legacy.ValueKind is JsonValueKind.String or JsonValueKind.Number) &&
        (identity.TryGetProperty("volumeSerial", out _) || identity.TryGetProperty("VolumeSerialNumber", out _) ||
         identity.TryGetProperty("revision", out _) || identity.TryGetProperty("Revision", out _) ||
         identity.TryGetProperty("identityKind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "cloud");

    private static string HistoryJsonText(string json, string property)
    {
        try { using var document = JsonDocument.Parse(json); return document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : ""; }
        catch (JsonException) { return ""; } // classification above retains malformed graph
    }
    private static string HistoryContextKey(HistoryRow row)
    {
        var pair = HistoryJsonText(row.Text("detail_json"), "PairId");
        var operation = HistoryJsonText(row.Text("detail_json"), "OperationId");
        return pair.Length == 0 || operation.Length == 0 ? "" : pair + "\n" + operation;
    }
    private static Dictionary<string, long> HistoryCounts(IEnumerable<HistoryRow> rows) =>
        rows.GroupBy(row => row.Table, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, long> HistoryReadOnly(Dictionary<string, long> values) =>
        new ReadOnlyDictionary<string, long>(values);
    private static string HistoryQuote(string name)
    {
        if (!Regex.IsMatch(name, "^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Unclassified authority SQL identifier.");
        return "\"" + name + "\"";
    }

    private sealed record HistoryGuard(string Name, string Sql);
    private static readonly IReadOnlyDictionary<string, string> HistoryDeleteGuardTables = new Dictionary<string, string>
    {
        ["observation_no_delete"] = "observation", ["history_no_delete"] = "history_event",
        ["attempt_no_delete"] = "operation_attempt", ["receipt_no_delete"] = "effect_receipt",
        ["operation_member_delete_only_while_planned"] = "operation_member",
        ["operation_dependency_delete_only_while_planned"] = "operation_dependency"
    };
    private static string HistoryNormalizeGuard(string sql) =>
        Regex.Replace(Regex.Replace(sql, @"\bIF\s+NOT\s+EXISTS\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            @"\s+", "", RegexOptions.CultureInvariant).TrimEnd(';').ToLowerInvariant();

    private static async Task<List<HistoryGuard>> CaptureHistoryDeleteGuardsAsync(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyCollection<HistoryRow> selected, CancellationToken token)
    {
        var result = new List<HistoryGuard>();
        foreach (var item in HistoryDeleteGuardTables.Where(item => selected.Any(row => row.Table == item.Value)))
        {
            var actual = await ScalarStringAsync(connection, transaction,
                "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name=$name;", token, ("$name", item.Key));
            var match = Regex.Match(SyncAuthoritySchema.Sql,
                @"CREATE\s+TRIGGER\s+IF\s+NOT\s+EXISTS\s+" + Regex.Escape(item.Key) + @"\b[\s\S]*?END;",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (actual is null || !match.Success || HistoryNormalizeGuard(actual) != HistoryNormalizeGuard(match.Value))
                throw new InvalidDataException("History expiry requires the exact compiled delete guard: " + item.Key);
            result.Add(new(item.Key, actual));
        }
        return result;
    }
    private static async Task VerifyHistoryDeleteGuardsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<HistoryGuard> guards, CancellationToken token)
    {
        foreach (var guard in guards)
            if (await ScalarStringAsync(connection, transaction,
                    "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name=$name;", token, ("$name", guard.Name)) != guard.Sql)
                throw new InvalidDataException("History expiry did not restore the exact original delete guard.");
    }
}
