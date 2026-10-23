using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed record SyncActivityRetentionWindow(bool Enabled, int OrdinaryDays, long MaximumEvents,
    long MaximumEncodedBytes, int DiagnosticDays, long MaximumDiagnosticBytes, long RetainedEvents,
    long EncodedBytes, long DiagnosticEvents, long DiagnosticEncodedBytes, long ExpiredEvents,
    long ExpiredThroughSequence, long LastAllocatedSequence, long? RetainedFromSequence,
    string? RetainedFromUtc, bool LegacyHeld, bool SafetyHeld, bool IncompleteRunHistory = false)
{
    public SyncActionJournalStatus? Actions { get; init; }
    public SyncConsentLifetimeStatus? Consents { get; init; }
    public SyncRunSummaryStatus? RunSummaries { get; init; }
    public SyncContextLifetimeStatus? Contexts { get; init; }
    public SyncObjectLifetimeStatus? Objects { get; init; }
    public SyncInventoryLifetimeStatus? Inventory { get; init; }
    public SyncMonitorLifetimeStatus? Monitors { get; init; }
    public string Describe()
    {
        var note = Enabled
            ? $"Recent activity keeps up to {OrdinaryDays:N0} days, {MaximumEvents:N0} events or {MaximumEncodedBytes / 1_048_576:N0} MiB of saved detail."
            : "Automatic activity expiry is off.";
        if (Enabled && (RetainedEvents > MaximumEvents || EncodedBytes > MaximumEncodedBytes ||
                DiagnosticEncodedBytes > MaximumDiagnosticBytes))
            note += " Cleanup is catching up with recent activity; the current retained size is above that window.";
        if (ExpiredEvents != 0)
            note += $" {ExpiredEvents:N0} older events have expired; exports include only retained events.";
        if (RetainedFromUtc is not null && DateTimeOffset.TryParse(RetainedFromUtc, out var since))
            note += $" Recent retained activity starts {since.LocalDateTime:g}.";
        if (LegacyHeld || SafetyHeld) note += " Older recovery or legacy evidence is also retained.";
        if (IncompleteRunHistory) note += " Some run history is incomplete after an interrupted final log flush.";
        if (Actions is not null) note += " " + Actions.Describe();
        if (Consents is not null) note += " " + Consents.Describe();
        if (RunSummaries is not null) note += " " + RunSummaries.Describe();
        if (Contexts is not null) note += " " + Contexts.Describe();
        if (Objects is not null) note += " " + Objects.Describe();
        if (Inventory is not null) note += " " + Inventory.Describe();
        if (Monitors is not null) note += " " + Monitors.Describe();
        return note;
    }
}

public sealed record SyncActivityRetirementResult(int DeletedEvents, long DeletedPayloadBytes,
    double ElapsedMilliseconds, string Disposition, long FreePages, long DatabaseBytes, long WalBytes)
{
    public SyncActionRetirementResult? Actions { get; init; }
    public SyncConsentRetirementResult? Consents { get; init; }
    public SyncRunSummaryRetirementResult? RunSummaries { get; init; }
    public SyncContextRetirementResult? Contexts { get; init; }
    public SyncObjectRetirementResult? Objects { get; init; }
    public SyncInventoryRetirementResult? Inventory { get; init; }
    public SyncMonitorRetirementResult? Monitors { get; init; }
}

internal sealed record SyncActivityRetentionPolicy(int OrdinaryDays = 365, long MaximumEvents = 1_000_000,
    long MaximumEncodedBytes = 1_073_741_824, int DiagnosticDays = 7,
    long MaximumDiagnosticBytes = 33_554_432)
{
    internal static SyncActivityRetentionPolicy Default { get; } = new();
    internal void Validate()
    {
        if (OrdinaryDays <= 0 || MaximumEvents <= 0 || MaximumEncodedBytes <= 0 ||
            DiagnosticDays <= 0 || MaximumDiagnosticBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(SyncActivityRetentionPolicy));
    }
}

public sealed class SyncRunActivityWriter
{
    private readonly object _gate = new();
    private long _next;
    private bool _closed;
    internal SyncAuthorityDatabase Owner { get; }
    internal SyncAuthorityCommandAdmission Admission { get; }
    internal string? PairId { get; }
    internal string PayloadJson { get; }
    public string CommandId => Admission.CommandRequestId;

    internal SyncRunActivityWriter(SyncAuthorityDatabase owner, SyncAuthorityCommandAdmission admission,
        string? pairId, string payloadJson, long next)
    { Owner = owner; Admission = admission; PairId = pairId; PayloadJson = payloadJson; _next = next; }

    public SyncPendingActivityEvent Enqueue(SyncActivityEvent item)
    {
        if (item.EventId.Length != 0 || item.RunId != CommandId)
            throw new InvalidDataException("A run event needs its exact writer and an unassigned event identity.");
        item = SyncAuthorityDatabase.NormalizeActivityEvent(item, requireIdentity: false);
        lock (_gate)
        {
            if (_closed || _next == long.MaxValue)
                throw new InvalidOperationException("This run history writer is closed or exhausted.");
            var ordinal = _next++;
            return new SyncPendingActivityEvent(this, ordinal,
                item with { EventId = EventIdentity(Admission, ordinal) });
        }
    }

    internal void MarkClosed() { lock (_gate) _closed = true; }
    internal static string EventIdentity(SyncAuthorityCommandAdmission admission, long ordinal) =>
        "activity-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            admission.DatabaseGeneration + "\u001f" + admission.CommandRequestId + "\u001f" +
            ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToLowerInvariant();
}

public sealed class SyncPendingActivityEvent
{
    internal SyncRunActivityWriter Writer { get; }
    public long Ordinal { get; }
    public SyncActivityEvent Event { get; }
    internal SyncPendingActivityEvent(SyncRunActivityWriter writer, long ordinal, SyncActivityEvent item)
    { Writer = writer; Ordinal = ordinal; Event = item; }
}

public enum SyncActivityAppendDisposition { Committed, AlreadyCommitted, AlreadyCommittedExpired }
public sealed record SyncActivityAppendResult(long Ordinal, SyncActivityAppendDisposition Disposition);

public sealed partial class SyncAuthorityDatabase
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SyncRunActivityWriter> _runActivityWriters = new(StringComparer.Ordinal);

    public async Task<SyncRunActivityWriter> OpenRunActivityWriterAsync(SyncAuthorityCommandAdmission admission,
        CancellationToken cancellationToken = default)
    {
        SyncRunActivityWriter? writer = null;
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireCurrentCommandAdmissionCoreAsync(connection, transaction, admission,
                [admission.Purpose], cancellationToken);
            var id = admission.CommandRequestId;
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT activity_closed FROM command_request WHERE command_request_id=$id;",
                    cancellationToken, ("$id", id)) != 0)
                throw new InvalidOperationException("The command's activity writer is already closed.");
            if (_runActivityWriters.TryGetValue(id, out writer))
            {
                if (writer.Admission != admission)
                    throw new InvalidOperationException("The command's activity writer has a different admission.");
                return;
            }
            var pair = await ScalarStringAsync(connection, transaction,
                "SELECT pair_id FROM command_request WHERE command_request_id=$id;", cancellationToken, ("$id", id));
            var payload = await ScalarStringAsync(connection, transaction,
                "SELECT payload_json FROM command_request WHERE command_request_id=$id;", cancellationToken, ("$id", id));
            var next = await ScalarLongAsync(connection, transaction,
                "SELECT next_activity_ordinal FROM command_request WHERE command_request_id=$id;", cancellationToken, ("$id", id));
            await ExecuteAsync(connection, transaction,
                "UPDATE command_request SET activity_opened=1 WHERE command_request_id=$id;",
                cancellationToken, ("$id", id));
            writer = new SyncRunActivityWriter(this, admission, pair, payload!, next);
            if (!_runActivityWriters.TryAdd(id, writer))
                throw new InvalidOperationException("A second history writer raced this command.");
        }, cancellationToken);
        return writer!;
    }

    public async Task<IReadOnlyList<SyncActivityAppendResult>> AppendRunActivityAsync(
        IReadOnlyList<SyncPendingActivityEvent> events, CancellationToken cancellationToken = default)
    {
        var result = new List<SyncActivityAppendResult>(events.Count);
        var canonical = events.Select(item => (Pending: item,
            Safe: NormalizeActivityEvent(item.Event))).ToArray();
        if (ActivityAppendBoundaryForTest is { } before)
            await before("before", canonical.Select(item => item.Safe).ToArray());
        await WriteAsync(async (connection, transaction) =>
        {
            using var commands = new ObservationCommands(connection, transaction);
            var contexts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (pending, safe) in canonical)
            {
                var writer = pending.Writer;
                if (!ReferenceEquals(writer.Owner, this) || pending.Ordinal <= 0 ||
                    safe.RunId != writer.CommandId ||
                    safe.EventId != SyncRunActivityWriter.EventIdentity(writer.Admission, pending.Ordinal))
                    throw new InvalidDataException("An activity event does not belong to its exact writer and ordinal.");
                if (!ActivityPairInCommandScope(writer, safe.PairId) &&
                    (safe.OperationId is null || await ScalarLongAsync(connection, transaction, """
                        SELECT count(*) FROM operation o
                        WHERE o.operation_id=$operation
                          AND (o.origin_command_id=$command OR o.execution_command_id=$command)
                          AND EXISTS(SELECT 1 FROM operation_member m
                              WHERE m.operation_id=o.operation_id AND m.pair_id=$pair);
                        """, cancellationToken, ("$operation", safe.OperationId),
                        ("$command", writer.CommandId), ("$pair", safe.PairId)) != 1))
                    throw new InvalidDataException("The activity event names a pair outside its exact command and operation.");
                var retained = await LoadRetainedCommandAdmissionAsync(connection, transaction,
                    writer.CommandId, cancellationToken);
                var position = await ReadCommandAdmissionPositionCoreAsync(connection, transaction, cancellationToken);
                if (retained != writer.Admission || retained.DatabaseGeneration != position.Generation)
                    throw new InvalidOperationException("The run history writer's command or generation is no longer retained.");
                var binding = await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM command_request WHERE command_request_id=$id AND pair_id IS $pair AND payload_json=$payload;",
                    cancellationToken, ("$id", writer.CommandId), ("$pair", writer.PairId), ("$payload", writer.PayloadJson));
                if (binding != 1) throw new InvalidDataException("The activity writer's command scope changed.");
                var next = await ScalarLongAsync(connection, transaction,
                    "SELECT next_activity_ordinal FROM command_request WHERE command_request_id=$id;",
                    cancellationToken, ("$id", writer.CommandId));
                if (pending.Ordinal < next)
                {
                    var existing = await commands.ScalarStringAsync(
                        "SELECT detail_json FROM history_event WHERE producer_command_id=$command AND producer_kind='run' AND producer_ordinal=$ordinal;",
                        cancellationToken, ("$command", writer.CommandId), ("$ordinal", pending.Ordinal));
                    if (existing is not null && !string.Equals(
                            await ExpandStoredActivityContextAsync(commands, existing, cancellationToken),
                            JsonSerializer.Serialize(safe), StringComparison.Ordinal))
                        throw new InvalidDataException("A committed activity ordinal cannot replace its retained payload.");
                    result.Add(new(pending.Ordinal, existing is null
                        ? SyncActivityAppendDisposition.AlreadyCommittedExpired
                        : SyncActivityAppendDisposition.AlreadyCommitted));
                    continue;
                }
                if (pending.Ordinal != next || next == long.MaxValue ||
                    await ScalarLongAsync(connection, transaction,
                        "SELECT activity_closed FROM command_request WHERE command_request_id=$id;",
                        cancellationToken, ("$id", writer.CommandId)) != 0)
                    throw new InvalidOperationException("A new activity ordinal is missing, exhausted or belongs to a closed writer.");
                await InsertWindowActivityAsync(commands, contexts, safe, "ordinary", writer.CommandId,
                    pending.Ordinal, cancellationToken);
                await commands.ExecuteAsync(
                    "UPDATE command_request SET next_activity_ordinal=next_activity_ordinal+1 WHERE command_request_id=$id AND next_activity_ordinal=$ordinal;",
                    cancellationToken, ("$id", writer.CommandId), ("$ordinal", pending.Ordinal));
                result.Add(new(pending.Ordinal, SyncActivityAppendDisposition.Committed));
            }
        }, cancellationToken);
        if (ActivityAppendBoundaryForTest is { } after)
            await after("after", canonical.Select(item => item.Safe).ToArray());
        return result;
    }

    public async Task AppendDiagnosticActivityAsync(SyncActivityEvent item,
        CancellationToken cancellationToken = default)
    {
        if (item.EventId.Length != 0 || item.RunId is not null)
            throw new InvalidDataException("A fresh diagnostic needs no supplied event identity or run writer.");
        var safe = NormalizeActivityEvent(item with { EventId = "diagnostic-" + Guid.NewGuid().ToString("N") });
        await WriteAsync(async (connection, transaction) =>
        {
            using var commands = new ObservationCommands(connection, transaction);
            await InsertWindowActivityAsync(commands, new Dictionary<string, string>(StringComparer.Ordinal),
                safe, "diagnostic", null, null, cancellationToken);
        }, cancellationToken);
    }

    private async Task InsertWindowActivityAsync(ObservationCommands commands, Dictionary<string, string> contexts,
        SyncActivityEvent safe, string retentionClass, string? commandId, long? ordinal, CancellationToken token,
        string producerKind = "run")
    {
        string? key = null;
        if (safe.EndpointContext is { } context)
        {
            key = ActivityContextKey(safe.PairId, safe.RunId, context);
            await RetainVerifiedActivityContextAsync(commands, key, safe.PairId, safe.RunId, context, contexts, token);
        }
        var payload = StoredActivityPayload(safe, key);
        var policy = ActivityRetentionPolicy;
        policy.Validate();
        var occurred = Utc(safe.OccurredUtc);
        var expires = Utc(safe.OccurredUtc.AddDays(retentionClass == "diagnostic"
            ? policy.DiagnosticDays : policy.OrdinaryDays));
        // Budget every stored text scalar and JSON in UTF-8, plus a conservative nine bytes
        // per non-null integer (sequence, encoded_bytes, optional ordinal). Context payload
        // is charged once by the reference counters. This is not a SQLite page/index cap.
        var bytes = Encoding.UTF8.GetByteCount(payload) + Encoding.UTF8.GetByteCount(safe.EventId) +
            Encoding.UTF8.GetByteCount(occurred) + Encoding.UTF8.GetByteCount(expires) +
            Encoding.UTF8.GetByteCount(key ?? "") + Encoding.UTF8.GetByteCount(commandId ?? "") +
            Encoding.UTF8.GetByteCount(retentionClass) + Encoding.UTF8.GetByteCount("presentation") +
            Encoding.UTF8.GetByteCount(producerKind) +
            (ordinal.HasValue ? 27L : 18L);
        await commands.ExecuteAsync("""
            INSERT INTO history_event(rowid,history_event_id,operation_id,event_kind,detail_json,occurred_utc,
                activity_context_key,producer_command_id,producer_ordinal,retention_class,encoded_bytes,expires_utc,producer_kind)
            VALUES((SELECT next_history_sequence FROM installation),$id,NULL,'presentation',$payload,$utc,
                $context,$command,$ordinal,$class,$bytes,$expires,$producer);
            """, token, ("$id", safe.EventId), ("$payload", payload), ("$utc", occurred),
            ("$context", key), ("$command", commandId), ("$ordinal", ordinal), ("$class", retentionClass),
            ("$bytes", bytes), ("$expires", expires), ("$producer", producerKind));
    }

    private static bool ActivityPairInCommandScope(SyncRunActivityWriter writer, string? pairId)
    {
        if (pairId is null) return true; // A run-wide summary does not name a foreign pair.
        if (writer.PairId == pairId) return true;
        if (writer.PairId is null)
        {
            using var payload = JsonDocument.Parse(writer.PayloadJson);
            if (payload.RootElement.TryGetProperty("pairIds", out var pairs) &&
                pairs.ValueKind == JsonValueKind.Array && pairs.EnumerateArray().Any(value =>
                    value.ValueKind == JsonValueKind.String && value.GetString() == pairId)) return true;
        }
        return false;
    }

    public async Task CloseRunActivityWriterAsync(SyncRunActivityWriter writer, bool incomplete = false,
        CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(writer.Owner, this)) throw new InvalidDataException("The activity writer has another owner.");
        writer.MarkClosed();
        await WriteAsync(async (connection, transaction) =>
        {
            if (await LoadRetainedCommandAdmissionAsync(connection, transaction, writer.CommandId, cancellationToken)
                    != writer.Admission || (await ReadCommandAdmissionPositionCoreAsync(connection, transaction,
                    cancellationToken)).Generation != writer.Admission.DatabaseGeneration)
                throw new InvalidOperationException("The activity writer's retained admission changed before closure.");
            await ExecuteAsync(connection, transaction,
                "UPDATE command_request SET activity_closed=1,activity_incomplete=max(activity_incomplete,$incomplete) WHERE command_request_id=$id;",
                cancellationToken, ("$incomplete", incomplete ? 1 : 0), ("$id", writer.CommandId));
        }, cancellationToken);
        _runActivityWriters.TryRemove(writer.CommandId, out _);
    }

    internal async Task CloseInterruptedActivityWritersAsync(CancellationToken token)
    {
        long interrupted = 0;
        await WriteAsync(async (connection, transaction) =>
        {
            // Startup already owns command recovery. A prior process's in-memory pending prefix cannot
            // be reconstructed or claimed complete, even when the command itself reached terminal state.
            await ExecuteAsync(connection, transaction, """
                UPDATE command_request SET activity_closed=1,activity_incomplete=1
                WHERE activity_opened=1 AND activity_closed=0 AND admission_sequence IS NOT NULL;
                """, token);
            interrupted = await ScalarLongAsync(connection, transaction, "SELECT changes();", token);
            if (interrupted != 0)
            {
                using var commands = new ObservationCommands(connection, transaction);
                var diagnostic = NormalizeActivityEvent(new SyncActivityEvent(
                    "diagnostic-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                    [SyncActivityCategories.Diagnostics], "Warning", "Application", "Interrupted activity history",
                    $"{interrupted} prior run history writer(s) ended without a verified final drain. Their logs are incomplete; file outcomes and recovery evidence remain retained."));
                await InsertWindowActivityAsync(commands, new Dictionary<string, string>(StringComparer.Ordinal),
                    diagnostic, "diagnostic", null, null, token);
            }
        }, token);
    }

    internal Task EnableActivityRetentionForTestAsync(CancellationToken token = default) =>
        WriteAsync((connection, transaction) => ExecuteAsync(connection, transaction,
            "UPDATE installation SET history_retention_enabled=1;", token), token);

    // Connection-local, default-deny capability. SQL receives only a Boolean exact-row answer.
    // No SQL statement can populate these sets or enable retirement for another connection/table.
    private sealed class HistoryRetirementScope
    {
        internal readonly HashSet<(long Sequence, string EventId)> Events = [];
        internal readonly HashSet<string> Contexts = new(StringComparer.Ordinal);
        internal readonly HashSet<(string Table,string Id)> Objects = [];
        internal void Clear() { Events.Clear(); Contexts.Clear(); Objects.Clear(); }
    }

    private static readonly ConditionalWeakTable<SqliteConnection, HistoryRetirementScope>
        HistoryRetirementScopes = new();
    internal SyncActivityRetentionPolicy? ActivityRetentionPolicyForTest { get; set; }
    internal Func<DateTimeOffset>? ActivityRetentionClockForTest { get; set; }
    internal Action<string>? ActivityRetirementBoundaryForTest { get; set; }
    private SyncActivityRetentionPolicy ActivityRetentionPolicy =>
        ActivityRetentionPolicyForTest ?? SyncActivityRetentionPolicy.Default;
    private DateTimeOffset ActivityRetentionNow => (ActivityRetentionClockForTest?.Invoke() ??
        DateTimeOffset.UtcNow).ToUniversalTime();

    private static void ConfigureHistoryRetirementFunctions(SqliteConnection connection)
    {
        HistoryRetirementScopes.Remove(connection);
        var scope = new HistoryRetirementScope();
        HistoryRetirementScopes.Add(connection, scope);
        ConfigureObjectRetirementFunction(connection, scope);
        // SQLite's INNOCUOUS bit allows this side-effect-free predicate in a verified trigger while
        // trusted_schema remains OFF. https://www.sqlite.org/c3ref/c_deterministic.html
        const int innocuous = 0x00200000;
        var flags = SQLitePCL.raw.SQLITE_UTF8 | innocuous;
        var result = SQLitePCL.raw.sqlite3_create_function(connection.Handle!,
            "syncshark_history_retirement_allowed", 2, flags, scope,
            (context, state, values) =>
            {
                var allowed = values.Length == 2 && state is HistoryRetirementScope permission &&
                    permission.Events.Contains((SQLitePCL.raw.sqlite3_value_int64(values[0]),
                        SQLitePCL.raw.sqlite3_value_text(values[1]).utf8_to_string() ?? ""));
                SQLitePCL.raw.sqlite3_result_int(context, allowed ? 1 : 0);
            });
        if (result != SQLitePCL.raw.SQLITE_OK)
            throw new InvalidDataException("The exact-row history retirement predicate could not be registered.");
        result = SQLitePCL.raw.sqlite3_create_function(connection.Handle!,
            "syncshark_history_context_retirement_allowed", 1, flags, scope,
            (context, state, values) =>
            {
                var allowed = values.Length == 1 && state is HistoryRetirementScope permission &&
                    permission.Contexts.Contains(SQLitePCL.raw.sqlite3_value_text(values[0]).utf8_to_string() ?? "");
                SQLitePCL.raw.sqlite3_result_int(context, allowed ? 1 : 0);
            });
        if (result != SQLitePCL.raw.SQLITE_OK)
            throw new InvalidDataException("The exact-context retirement predicate could not be registered.");
    }

    public async Task<SyncActivityRetentionWindow> LoadActivityRetentionWindowAsync(
        CancellationToken cancellationToken = default, string? runId = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var policy = ActivityRetentionPolicy;
        policy.Validate();
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT history_retention_enabled,history_ordinary_count,history_ordinary_bytes,
                   history_diagnostic_count,history_diagnostic_bytes,history_expired_count,
                   history_expired_through_sequence,next_history_sequence-1,
                   (SELECT min(sequence) FROM (
                       SELECT min(rowid) sequence FROM history_event WHERE retention_class='ordinary'
                       UNION ALL
                       SELECT min(rowid) FROM history_event WHERE retention_class='diagnostic')),
                   EXISTS(SELECT 1 FROM history_event WHERE retention_class='legacy'),
                   EXISTS(SELECT 1 FROM history_event WHERE retention_class='safety'),
                   EXISTS(SELECT 1 FROM command_request WHERE activity_incomplete=1
                       AND ($run IS NULL OR command_request_id=$run))
            FROM installation;
            """;
        query.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        long? first;
        SyncActivityRetentionWindow value;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidDataException("History retention has no installation identity.");
            first = reader.IsDBNull(8) ? null : reader.GetInt64(8);
            value = new SyncActivityRetentionWindow(reader.GetInt64(0) == 1,
                policy.OrdinaryDays, policy.MaximumEvents, policy.MaximumEncodedBytes,
                policy.DiagnosticDays, policy.MaximumDiagnosticBytes, reader.GetInt64(1),
                reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt64(7), first, null,
                reader.GetInt64(9) == 1, reader.GetInt64(10) == 1, reader.GetInt64(11) == 1);
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidDataException("History retention has multiple installation identities.");
        }
        value = value with {
            Actions = await LoadActionJournalStatusCoreAsync(connection, null, null, cancellationToken),
            Consents = await LoadConsentLifetimeStatusCoreAsync(connection, null, cancellationToken),
            RunSummaries = await LoadRunSummaryStatusCoreAsync(connection, null, cancellationToken),
            Contexts = await LoadContextLifetimeStatusCoreAsync(connection, null, cancellationToken),
            Objects = await LoadObjectLifetimeStatusCoreAsync(connection, null, cancellationToken),
            Inventory = await LoadInventoryLifetimeStatusCoreAsync(connection, null, cancellationToken),
            Monitors = await LoadMonitorLifetimeStatusCoreAsync(connection,null,cancellationToken) };
        if (first is not null)
            value = value with { RetainedFromUtc = await ScalarStringAsync(connection, null,
                "SELECT occurred_utc FROM history_event WHERE rowid=$sequence;", cancellationToken,
                ("$sequence", first.Value)) };
        return value;
    }

    // Existing owner maintenance calls this only after acquiring its idle/run gate. It owns one
    // independent writer transaction; no effect transaction or pending progress queue is borrowed.
    internal async Task<SyncActivityRetirementResult> RetireOrdinaryActivityBatchAsync(
        CancellationToken cancellationToken = default)
    {
        if (_executionConnectionScope.Value is { Active: true })
            throw new InvalidOperationException("History maintenance cannot borrow an active execution scope.");
        var policy = ActivityRetentionPolicy;
        policy.Validate();
        await _writer.WaitAsync(cancellationToken);
        var clock = Stopwatch.StartNew();
        var deleted = 0;
        long payloadBytes = 0;
        var disposition = "within_window";
        SyncContextRetirementResult contexts = new(0,0,0);
        SyncObjectRetirementResult objects = new(0,0,0);
        SyncInventoryRetirementResult inventory = new(0,0,0);
        SyncMonitorRetirementResult monitors = new(0,0,0);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken, reuseConnection: false);
            var scope = HistoryRetirementScopes.TryGetValue(connection, out var retained)
                ? retained : throw new InvalidDataException("The default-deny retirement scope is missing.");
            try
            {
                await using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    if (await ScalarLongAsync(connection, transaction,
                            "SELECT history_retention_enabled FROM installation;", cancellationToken) != 1)
                        disposition = "disabled";
                    else
                    {
                        contexts = await RetireUnreferencedContextsCoreAsync(connection, transaction, scope,
                            clock, cancellationToken);
                        objects = await RetireUnreferencedObjectDetailsCoreAsync(connection, transaction, scope,
                            clock, cancellationToken, contexts.RetiredBytes);
                        inventory = await RetireInventoryDetailsCoreAsync(connection, transaction, scope,
                            clock, cancellationToken, contexts.RetiredBytes + objects.RetiredBytes);
                        monitors = await RetireMonitorDetailsCoreAsync(connection,transaction,scope,clock,cancellationToken,
                            contexts.ExaminedRows+objects.ExaminedRows+inventory.ExaminedRows,
                            contexts.RetiredBytes+objects.RetiredBytes+inventory.RetiredBytes);
                        while (deleted + contexts.ExaminedRows + objects.ExaminedRows + inventory.ExaminedRows + monitors.ExaminedRows < 200)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (deleted != 0 && clock.ElapsedMilliseconds >= 50)
                            { disposition = "cooperative_time_budget"; break; }
                            var row = await SelectRetirementCandidateAsync(connection, transaction, policy,
                                ActivityRetentionNow, cancellationToken);
                            if (row is null) break;
                            if (row.Value.Bytes > 1_048_576)
                            { disposition = "oversized_event_held"; break; }
                            if (payloadBytes + contexts.RetiredBytes + objects.RetiredBytes + inventory.RetiredBytes + monitors.RetiredBytes + row.Value.Bytes > 1_048_576)
                            { disposition = "payload_budget"; break; }
                            var item = row.Value;
                            scope.Events.Add((item.Sequence, item.EventId));
                            ActivityRetirementBoundaryForTest?.Invoke("exact-row-bound-before-delete");
                            await ExecuteAsync(connection, transaction,
                                "DELETE FROM history_event WHERE rowid=$sequence AND history_event_id=$id;",
                                cancellationToken, ("$sequence", item.Sequence), ("$id", item.EventId));
                            if (await ScalarLongAsync(connection, transaction, "SELECT changes();",
                                    cancellationToken) != 1)
                                throw new InvalidDataException("The exact retirement row changed.");
                            deleted++;
                            payloadBytes += item.Bytes;
                        }
                    }
                    ActivityRetirementBoundaryForTest?.Invoke("before-retirement-commit");
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            finally { scope.Clear(); }
            // A cached planning fingerprint may name an object detail just retired.
            // Invalidate the derived cache after commit; never rebind it to a later row.
            if (objects.RetiredRows != 0 || inventory.RetiredRows != 0)
            {
                _completeExcludedPaths.Clear();
                _planningContentFingerprints.Clear();
            }
            // Fresh databases opt into incremental auto-vacuum; predecessors are never rewritten here.
            if ((deleted != 0 || contexts.RetiredContexts != 0 || objects.RetiredRows != 0 || inventory.RetiredRows != 0 || monitors.RetiredRows != 0) && await ScalarLongAsync(connection, null, "PRAGMA auto_vacuum;",
                    cancellationToken) == 2)
                await ExecuteAsync(connection, null, "PRAGMA incremental_vacuum(128);", cancellationToken);
            var free = await ScalarLongAsync(connection, null, "PRAGMA freelist_count;", cancellationToken);
            return new SyncActivityRetirementResult(deleted, payloadBytes, clock.Elapsed.TotalMilliseconds,
                deleted + contexts.ExaminedRows + objects.ExaminedRows + inventory.ExaminedRows + monitors.ExaminedRows == 200 ? "row_budget" : disposition, free, FileLength(_databasePath),
                FileLength(_databasePath + "-wal")) { Contexts = contexts, Objects = objects, Inventory = inventory, Monitors = monitors };
        }
        finally { _writer.Release(); }

        static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private readonly record struct RetirableActivity(long Sequence, string EventId, long Bytes,
        string? ContextKey);

    private static async Task<RetirableActivity?> SelectRetirementCandidateAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncActivityRetentionPolicy policy, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Indexed oldest-by-class and expiry candidates only. No COUNT/history JSON scan per tick.
        // Counters are maintained by the same INSERT/DELETE transactions that own the events.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidates(sequence) AS (
                SELECT rowid FROM history_event
                WHERE rowid=(SELECT min(rowid) FROM history_event WHERE retention_class='ordinary')
                  AND EXISTS(SELECT 1 FROM installation WHERE history_ordinary_count>$count OR history_ordinary_bytes>$bytes)
                UNION
                SELECT rowid FROM history_event
                WHERE rowid=(SELECT min(rowid) FROM history_event WHERE retention_class='diagnostic')
                  AND EXISTS(SELECT 1 FROM installation WHERE history_ordinary_count>$count
                      OR history_ordinary_bytes>$bytes OR history_diagnostic_bytes>$diagnosticBytes)
                UNION
                SELECT rowid FROM (
                    SELECT rowid FROM history_event INDEXED BY history_event_expiry
                    WHERE retention_class IN ('ordinary','diagnostic') AND expires_utc<=$now
                    ORDER BY expires_utc,rowid LIMIT 1)
            )
            SELECT h.rowid,h.history_event_id,h.encoded_bytes +
                coalesce((SELECT a.encoded_bytes FROM activity_context a
                    WHERE a.context_key=h.activity_context_key AND NOT EXISTS(
                        SELECT 1 FROM history_event follower
                        WHERE follower.activity_context_key=a.context_key AND follower.rowid<>h.rowid)),0),
                h.activity_context_key
            FROM candidates c JOIN history_event h ON h.rowid=c.sequence
            ORDER BY h.rowid LIMIT 1;
            """;
        command.Parameters.AddWithValue("$count", policy.MaximumEvents);
        command.Parameters.AddWithValue("$bytes", policy.MaximumEncodedBytes);
        command.Parameters.AddWithValue("$diagnosticBytes", policy.MaximumDiagnosticBytes);
        command.Parameters.AddWithValue("$now", Utc(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RetirableActivity(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
    }
}

