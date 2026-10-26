using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Massdex.SyncShark;

public sealed record SyncActivityProgress(long Completed, long? Total, string Unit, long ElapsedMilliseconds);

// Presentation records share the existing append-only history. They never authorize effects.
public sealed record SyncActivityEvent(string EventId, DateTimeOffset OccurredUtc,
    IReadOnlyList<string> Categories, string Severity, string Entity, string Action, string Message,
    string? ConnectionId = null, string? PairId = null, string? RunId = null,
    string? OperationId = null, string RelativePath = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SyncEndpointContext? EndpointContext = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    SyncAuthorityOperationKind? OperationKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    SyncActivityPhase? Phase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SyncActivityProgress? Progress = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RelatedRunId = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyncActivityOperationSnapshot? OperationSnapshot { get; init; }
}

// Display facts captured while the authoritative action still exists. These
// aliases never grant effect/retry permission and never require current paths.
public sealed record SyncActivityOperationSnapshot(string EventKind, string Status,
    long? SizeBytes, string? LocalRoot, string? RemoteRoot);


// Detailed logs default to ascending retained-sequence cursors for gap-free CLI,
// retry and export consumers. The bounded GUI live window and an explicit CLI
// --order newest request can read the tail without changing forward cursors.
public sealed record SyncActivityQuery(IReadOnlyList<string>? Categories = null,
    string? Severity = null, string? Entity = null, string? RunId = null, bool OldestFirst = false,
    long? AfterSequence = null, bool DetailedLog = false, long? ThroughSequence = null,
    bool NewestLogFirst = false);

public static class SyncActivityCategories
{
    public const string Files = "File activity", Sync = "Sync log", Connections = "Connections",
        Pairs = "Folder pairs", Settings = "App settings", Diagnostics = "Diagnostics";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Files, Sync, Pairs, Connections, Settings, Diagnostics });
}

public static class SyncActivityText
{
    public static string Failure(Exception error) => Safe(string.Join(" → ",
        ExceptionChain(error).Select(part => part.Error.GetType().Name +
            (part.Error is HttpRequestException { StatusCode: { } code } ? $" (HTTP {(int)code})" : "") +
            (part.Error is HttpRequestException request and not (DropboxApiException or GoogleDriveApiException)
                ? $" (transport {request.HttpRequestError})" : "") +
            (part.Error is System.Net.Sockets.SocketException socket
                ? $" (socket {socket.SocketErrorCode}; native {socket.NativeErrorCode})" : "") +
            $" [0x{part.Error.HResult:X8}]: " +
            (part.PrivateDetail ? "Provider transport detail withheld." : FailureMessage(part.Error)) +
            ApplicationLocation(part.Error))));

    private static string FailureMessage(Exception error) => error switch
    {
        // These sealed application exceptions carry an explicit safe reason, not a raw
        // HTTP message/body. Preserve the actionable cause; Safe still redacts credentials.
        DropboxApiException api => api.SafeReason,
        GoogleDriveApiException api => api.SafeReason,
        HttpRequestException => "The HTTP request failed.",
        _ => error.Message
    };

    private static string ApplicationLocation(Exception error)
    {
        // Method identity only: never serialize a raw stack, source path, arguments,
        // exception Data or provider response. The same safe diagnostic serves CLI and GUI.
        var methods = new System.Diagnostics.StackTrace(error, false).GetFrames()?
            .Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType?.Namespace?.StartsWith("Massdex.SyncShark", StringComparison.Ordinal) == true)
            .Select(method => method!.DeclaringType!.FullName + "." + method.Name)
            .Distinct(StringComparer.Ordinal).Take(4).ToArray() ?? [];
        return methods.Length == 0 ? "" : " [application: " + string.Join(" <- ", methods) + "]";
    }

    private static IEnumerable<(Exception Error, bool PrivateDetail)> ExceptionChain(Exception error)
    {
        var seen = new HashSet<Exception>();
        var privateDetail = false;
        for (Exception? item = error; item is not null && seen.Add(item); item = item.InnerException)
        {
            yield return (item, privateDetail);
            // These application-owned messages are safe and actionable. Their inner provider,
            // transport or parser exception may contain arbitrary response/credential bytes.
            privateDetail |= item is DropboxEffectUncertainException or GoogleDriveEffectUncertainException or HttpRequestException;
        }
    }

    // Only presentation text reaches this boundary. Do not pass provider payloads, redirects,
    // stacks or credentials. Redaction is additional defence, not permission to read secrets.
    public static string Safe(string? value, bool omitPrivateDetails = false)
    {
        var text = value is { Length: > 4096 } ? value[..4096] : value ?? "";
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s,;]+", "Bearer [redacted]");
        text = Regex.Replace(text, @"(?i)\b(access_token|refresh_token|id_token|client_secret|authorization|password|code|state)\s*[=:]\s*[^\s,;&]+", "$1=[redacted]");
        text = Regex.Replace(text, @"https?://[^\s<>]+", match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "[link]");
        if (omitPrivateDetails)
        {
            // Lifecycle stderr is not structured activity: paths and identifiers add no useful
            // shutdown evidence. Drop the remainder of a path-bearing line (paths may contain spaces).
            text = Regex.Replace(text, @"(?i)(?:[a-z]:[\\/]|\\\\|(?<!\w)/(?!/)).*", "[path omitted]");
            text = Regex.Replace(text, @"(?i)https?://\S+", "[link omitted]");
            text = Regex.Replace(text, @"[^\s<>]+@[^\s<>]+", "[identifier omitted]");
            text = Regex.Replace(text, @"(?i)\b(?:pair|account|user|connection)[_-]?id\s*[=:]\s*\S+", "[identifier omitted]");
        }
        // Detailed retained records have their own bounded budget. The default 400-character
        // one-line CLI summary budget must not silently erase nested failure causes here.
        return SyncCliSanitize.Message(text, maximumLength: 4096) ?? "";
    }
}

public sealed partial class SyncAuthorityDatabase
{
    internal Func<string, IReadOnlyList<SyncActivityEvent>, Task>? ActivityAppendBoundaryForTest { get; set; }

    public async Task<long> LoadActivitySequenceAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null, "SELECT next_history_sequence-1 FROM installation;", cancellationToken);
    }

    public Task AppendActivityEventAsync(SyncActivityEvent item, CancellationToken cancellationToken = default) =>
        AppendActivityEventsAsync([item], cancellationToken);

    internal static SyncActivityEvent NormalizeActivityEvent(SyncActivityEvent item, bool requireIdentity = true)
    {
        if (requireIdentity) RequireIdentifier(item.EventId, nameof(item.EventId));
        if (item.Categories.Count == 0 || item.Categories.Any(category => !SyncActivityCategories.All.Contains(category)) ||
            item.Categories.Distinct(StringComparer.Ordinal).Count() != item.Categories.Count)
            throw new ArgumentException("Select one or more supported activity categories.", nameof(item));
        if (item.Severity is not ("Information" or "Success" or "Warning" or "Error") ||
            item.Entity is not ("File" or "Folder" or "Folder pair" or "Run" or "Account" or "Application"))
            throw new ArgumentException("Unsupported activity severity or item type.", nameof(item));
        foreach (var id in new[] { item.PairId, item.ConnectionId, item.RunId, item.OperationId, item.RelatedRunId })
            if (id is not null) RequireIdentifier(id, nameof(item));
        if (item.RelativePath.Length > 32768 || item.RelativePath.Any(char.IsControl))
            throw new ArgumentException("Unsupported activity path.", nameof(item));
        // A retained presentation record validates its own endpoint context before anything is written, so
        // foreign, malformed or locally assigned cloud metadata can never reach the append-only history.
        SyncEndpointContext.RequireRetainable(item.EndpointContext, item.PairId);
        if (item.Progress is { } progress && (progress.Completed < 0 || progress.Total < 0 ||
            progress.ElapsedMilliseconds < 0 || progress.Unit.Length > 80 || progress.Unit.Any(char.IsControl)))
            throw new ArgumentException("Invalid measured activity progress.", nameof(item));
        var safe = item with { OccurredUtc = item.OccurredUtc.ToUniversalTime(),
            Action = SyncActivityText.Safe(item.Action), Message = SyncActivityText.Safe(item.Message) };
        return safe;
    }

    // Compatibility replay only. An arbitrary historical EventId is never permission for a new insert.
    // Fresh producers must hold a run writer or request a newly minted runless diagnostic.
    public async Task AppendActivityEventsAsync(IReadOnlyList<SyncActivityEvent> items,
        CancellationToken cancellationToken = default)
    {
        var records = items.Select(NormalizeActivityEventForReplay).ToArray();
        await WriteAsync(async (connection, transaction) =>
        {
            using var commands = new ObservationCommands(connection, transaction);
            foreach (var safe in records)
            {
                var existing = await commands.ScalarStringAsync(
                    "SELECT detail_json FROM history_event WHERE history_event_id=$id;",
                    cancellationToken, ("$id", safe.EventId));
                if (existing is null || !string.Equals(
                        await ExpandStoredActivityContextAsync(commands, existing, cancellationToken),
                        JsonSerializer.Serialize(safe), StringComparison.Ordinal))
                    throw new InvalidDataException("A raw activity identity may only replay its exact retained event.");
            }
        }, cancellationToken);
    }

    private static SyncActivityEvent NormalizeActivityEventForReplay(SyncActivityEvent item) =>
        NormalizeActivityEvent(item);

    // S10: one immutable detached snapshot per distinct validated-context/pair/run group. A new record stores
    // one internal reference to that snapshot plus the exact activity_context_key column, so the complete
    // logical event is unchanged while no record needs the lifetime of a neighbouring presentation row.
    // Nothing about the logical event is dropped: only the duplicated presentation context member is
    // replaced, and the shared projection hydrates it back.
    private static string StoredActivityPayload(SyncActivityEvent safe, string? contextKey)
    {
        if (contextKey is null || safe.EndpointContext is null) return JsonSerializer.Serialize(safe);
        // EndpointContext is an internal stored union: the complete snapshot, the legacy tagged event
        // reference, or this tagged detached-snapshot reference. Every variant remains covered by the
        // EXISTING partial context/operation index. A top-level pointer with EndpointContext=null would
        // exclude referenced operations from that index and lose attribution. Public producers and returned
        // projections still use only the complete typed snapshot.
        var stored = JsonSerializer.SerializeToNode(safe)!.AsObject();
        stored[nameof(SyncActivityEvent.EndpointContext)] = new JsonObject
        {
            [ActivityContextKeyMember] = contextKey
        };
        return stored.ToJsonString();
    }

    private const string ActivityContextReferenceMember = "EndpointContextEventId";

    // S10: the internal member a schema 10 record uses to name its detached immutable activity_context
    // snapshot, and the legacy member a CCS2 record used to name an inline anchor event.
    private const string ActivityContextKeyMember = "EndpointContextKey";

    // S10: the detached snapshot identity. The key is the lowercase-hex SHA-256 over the EXACT UTF-8 group
    // text, which is the same pair/optional-run/complete-canonical-context grouping the retained store always
    // used. A different name, root, account, endpoint order, provider, pair, run or absent run therefore
    // produces a different key, and a colliding key is never equal on its digest alone: the writer always
    // compares the retained snapshot's own pair, run and complete serialization before it reuses anything.
    private static string ActivityContextKey(string? pairId, string? runId, SyncEndpointContext context) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ActivityContextGroup(pairId, runId, context))))
            .ToLowerInvariant();

    // S10: retain the complete canonical snapshot for one key inside the SAME writer transaction as the event
    // that references it, or verify an already retained snapshot against the context being stored. A verified
    // key is cached only for the remainder of this transaction, so one snapshot serves any number of awaited
    // appends without a second serialized context body and without any cross-transaction cache.
    //
    // Digest equality is never sufficient. A retained row for the same key must agree on the pair, the
    // optional run, its own snapshot PairId and the complete canonical serialization; a collision, a foreign
    // pair or run, a snapshot naming another pair, a malformed snapshot or a row that cannot be read exactly
    // rejects the whole append and rolls the entire batch back rather than reusing, replacing or ignoring
    // retained presentation data.
    private static async Task RetainVerifiedActivityContextAsync(ObservationCommands commands, string contextKey,
        string? pairId, string? runId, SyncEndpointContext context, Dictionary<string, string> verified,
        CancellationToken cancellationToken)
    {
        var canonical = JsonSerializer.Serialize(context);
        if (verified.TryGetValue(contextKey, out var cached))
        {
            if (!string.Equals(cached, canonical, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Two endpoint contexts share one retained activity-context identity but disagree in content.");
            return;
        }
        var storedPairId = pairId ?? "";
        var retained = await commands.ScalarStringAsync(
            "SELECT json_array(snapshot.pair_id,snapshot.run_id,snapshot.context_json) FROM activity_context snapshot WHERE snapshot.context_key=$key;",
            cancellationToken, ("$key", contextKey));
        if (retained is not null)
        {
            if (!ActivityContextSnapshotAgrees(retained, storedPairId, runId, canonical))
                throw new InvalidDataException(
                    "A retained activity context cannot be reused for a different pair, run, context or snapshot.");
            verified[contextKey] = canonical;
            return;
        }
        await commands.ExecuteAsync(
            "INSERT INTO activity_context(context_key,pair_id,run_id,context_json,encoded_bytes,created_utc) VALUES($key,$pair,$run,$json,$bytes,$utc);",
            cancellationToken, ("$key", contextKey), ("$pair", storedPairId), ("$run", runId),
            ("$json", canonical), ("$bytes", Encoding.UTF8.GetByteCount(canonical)),
            ("$utc", Utc(DateTimeOffset.UtcNow)));
        verified[contextKey] = canonical;
    }

    // S10: the exact readback agreement test for one retained snapshot, as the three-value JSON array the
    // single keyed read returns. Only an exact agreement on the pair, the optional run, the snapshot's own
    // PairId and the complete canonical serialization is reusable; anything else is refused, never repaired
    // and never silently rewritten.
    private static bool ActivityContextSnapshotAgrees(string retained, string pairId, string? runId,
        string canonical)
    {
        JsonArray? row;
        try { row = JsonNode.Parse(retained) as JsonArray; }
        catch (JsonException) { return false; }
        if (row is not { Count: 3 }) return false;
        if (!string.Equals(StoredJsonText(row[0]), pairId, StringComparison.Ordinal)) return false;
        if (!string.Equals(StoredJsonText(row[1]), runId, StringComparison.Ordinal)) return false;
        if (StoredJsonText(row[2]) is not { } retainedJson ||
            !string.Equals(retainedJson, canonical, StringComparison.Ordinal)) return false;
        try
        {
            return JsonNode.Parse(retainedJson) is JsonObject snapshot &&
                string.Equals(StoredJsonText(snapshot[nameof(SyncEndpointContext.PairId)]), pairId,
                    StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    // One stored JSON string member, or null when the value is absent, null or another JSON kind. A stored
    // reference whose member is not a string is therefore unusable rather than coerced.
    private static string? StoredJsonText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // CCS2/CCS5/S10: grouping is by the exact validated context serialization plus the pair and the optional
    // run. A different name, root, account, order, provider, context or run therefore gets its own immutable
    // snapshot, and the writer never chooses latest-by-pair.
    private static string ActivityContextGroup(string? pairId, string? runId, SyncEndpointContext context) =>
        (pairId ?? "") + "\u001f" + (runId ?? "") + "\u001f" + JsonSerializer.Serialize(context);

    // CCS4/CCS5/S10: expand one stored INTERNAL reference for the idempotency comparison only, so that a
    // replayed identity is compared against the complete logical record rather than the presentation form
    // this build chose to store.
    //
    // The schema 10 detached form resolves through activity_context by its exact primary key and is accepted
    // only when the retained snapshot itself agrees: the snapshot's pair and optional run must equal the
    // event's and the snapshot context must name that same pair. The legacy form keeps its unchanged one-hop
    // behaviour: one primary-key read to a retained presentation row whose own inline context is present and
    // whose pair and optional run are the same as the referencing row.
    //
    // A missing, foreign, malformed, inconsistent or recursive reference leaves the stored payload unchanged,
    // so a replay never accepts a different logical record and a corrupt reference is never silently rewritten.
    private static async Task<string> ExpandStoredActivityContextAsync(ObservationCommands commands,
        string storedPayload, CancellationToken cancellationToken)
    {
        JsonObject? stored;
        try { stored = JsonNode.Parse(storedPayload) as JsonObject; }
        catch (JsonException) { return storedPayload; }
        if (stored?[nameof(SyncActivityEvent.EndpointContext)] is not JsonObject contextReference)
            return storedPayload;
        // A row that names BOTH members is not a form this build ever writes; it expands none of them, so a
        // malformed double reference can never make a different logical record look like a replay.
        var detached = ReadStoredContextReference(contextReference, ActivityContextKeyMember);
        var legacy = ReadStoredContextReference(contextReference, ActivityContextReferenceMember);
        if (detached is not null && legacy is not null) return storedPayload;
        if (detached is not null)
        {
            var snapshot = await commands.ScalarStringAsync(
                "SELECT json_array(snapshot.pair_id,snapshot.run_id,snapshot.context_json) FROM activity_context snapshot WHERE snapshot.context_key=$key;",
                cancellationToken, ("$key", detached));
            return ExpandDetachedActivityContext(stored, snapshot) ?? storedPayload;
        }
        if (legacy is null) return storedPayload;
        var anchorPayload = await commands.ScalarStringAsync(
            "SELECT detail_json FROM history_event WHERE history_event_id=$id AND event_kind='presentation';",
            cancellationToken, ("$id", legacy));
        var anchor = anchorPayload is null ? null : DeserializeActivityEvent(anchorPayload);
        if (anchor?.EndpointContext is not { } anchorContext ||
            !string.Equals(anchor.EventId, legacy, StringComparison.Ordinal)) return storedPayload;
        stored[nameof(SyncActivityEvent.EndpointContext)] = JsonSerializer.SerializeToNode(anchorContext);
        var expanded = DeserializeActivityEvent(stored.ToJsonString());
        if (expanded is null ||
            !string.Equals(expanded.PairId, anchor.PairId, StringComparison.Ordinal) ||
            !string.Equals(expanded.PairId, anchorContext.PairId, StringComparison.Ordinal) ||
            !string.Equals(expanded.RunId, anchor.RunId, StringComparison.Ordinal)) return storedPayload;
        return JsonSerializer.Serialize(expanded);
    }

    // One internal reference member of a stored EndpointContext object, or null when that member is absent,
    // null, empty or not a string.
    private static string? ReadStoredContextReference(JsonObject contextReference, string member)
    {
        if (!contextReference.TryGetPropertyValue(member, out var node)) return null;
        return StoredJsonText(node) is { Length: > 0 } reference ? reference : null;
    }

    // S10: substitute one verified detached snapshot for its internal reference and return the canonical
    // logical record, or null when the snapshot is missing or disagrees with the referencing event.
    private static string? ExpandDetachedActivityContext(JsonObject stored, string? retained)
    {
        if (retained is null) return null;
        JsonArray? row;
        try { row = JsonNode.Parse(retained) as JsonArray; }
        catch (JsonException) { return null; }
        if (row is not { Count: 3 }) return null;
        var snapshotPairId = StoredJsonText(row[0]);
        var snapshotRunId = StoredJsonText(row[1]);
        if (snapshotPairId is null || StoredJsonText(row[2]) is not { } contextJson) return null;
        JsonNode? contextNode;
        try { contextNode = JsonNode.Parse(contextJson); }
        catch (JsonException) { return null; }
        if (contextNode is not JsonObject snapshotContext) return null;
        stored[nameof(SyncActivityEvent.EndpointContext)] = snapshotContext.DeepClone();
        var expanded = DeserializeActivityEvent(stored.ToJsonString());
        if (expanded?.EndpointContext is not { } context) return null;
        return string.Equals(expanded.PairId, snapshotPairId, StringComparison.Ordinal) &&
            string.Equals(expanded.RunId, snapshotRunId, StringComparison.Ordinal) &&
            string.Equals(expanded.PairId, context.PairId, StringComparison.Ordinal)
            ? JsonSerializer.Serialize(expanded) : null;
    }

    private static SyncActivityEvent? DeserializeActivityEvent(string payload)
    {
        try
        {
            // A stored record must carry an actual full context, never an internal reference in either form
            // (even a null or badly typed one): a reference is not a snapshot and is never deserialized as one.
            if (JsonNode.Parse(payload) is not JsonObject node ||
                node[nameof(SyncActivityEvent.EndpointContext)] is JsonObject context &&
                (context.ContainsKey(ActivityContextReferenceMember) ||
                 context.ContainsKey(ActivityContextKeyMember))) return null;
            return JsonSerializer.Deserialize<SyncActivityEvent>(payload);
        }
        catch (JsonException) { return null; }
    }
}

public sealed partial class SyncAuthorityRuntimeHost
{
    private long _ordinaryHistoryMaintenanceWindow;
    private int _ordinaryHistoryMaintenanceBatches;

    // Reuses the existing runtime maintenance/run gate. The Activity refresh/follow path supplies
    // the idle opportunity; no timer, service or default activation is introduced.
    public async Task<SyncActivityRetirementResult?> MaintainOrdinaryActivityHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        if (!AuthorityStateExists || !await _runGate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            lock (_activityLogGate)
                if (_activityProgressBatch.Count != 0 || _activityFlushTask is not null) return null;
            var now = Environment.TickCount64;
            if (now - _ordinaryHistoryMaintenanceWindow >= 60_000)
            {
                _ordinaryHistoryMaintenanceWindow = now;
                _ordinaryHistoryMaintenanceBatches = 0;
            }
            if (_ordinaryHistoryMaintenanceBatches >= 4) return null;
            var window = await _authority.LoadActivityRetentionWindowAsync(cancellationToken);
            if (!window.Enabled && window.Actions?.AccountingPending != true && window.Consents?.AccountingPending != true && window.RunSummaries?.AccountingPending != true)
            {
                _ordinaryHistoryMaintenanceBatches = 4;
                return null;
            }
            _ordinaryHistoryMaintenanceBatches++;
            // Share the existing four idle opportunities. Each opportunity runs
            // one bounded transaction, not two independent 50 ms budgets.
            if (window.Enabled && _ordinaryHistoryMaintenanceBatches == 3 ||
                !window.Enabled && window.Actions?.AccountingPending != true &&
                window.Consents?.AccountingPending != true && window.RunSummaries?.AccountingPending == true)
            {
                var summaries = await _authority.RetireRunSummaryBatchAsync(cancellationToken);
                return new SyncActivityRetirementResult(0, 0, summaries.ElapsedMilliseconds,
                    summaries.Disposition, summaries.FreePages, new FileInfo(_authority.DatabasePath).Length,
                    File.Exists(_authority.DatabasePath + "-wal")
                        ? new FileInfo(_authority.DatabasePath + "-wal").Length : 0) { RunSummaries = summaries };
            }
            if (window.Enabled && _ordinaryHistoryMaintenanceBatches == 4 ||
                !window.Enabled && window.Actions?.AccountingPending != true && window.Consents?.AccountingPending == true)
            {
                var consents = await _authority.RetireInactiveConsentBatchAsync(cancellationToken);
                return new SyncActivityRetirementResult(0, 0, consents.ElapsedMilliseconds,
                    consents.Disposition, consents.FreePages, new FileInfo(_authority.DatabasePath).Length,
                    File.Exists(_authority.DatabasePath + "-wal")
                        ? new FileInfo(_authority.DatabasePath + "-wal").Length : 0) { Consents = consents };
            }
            if (!window.Enabled || (_ordinaryHistoryMaintenanceBatches & 1) == 0)
            {
                var actions = await _authority.RetireSettledActionBatchAsync(cancellationToken);
                return new SyncActivityRetirementResult(0, 0, actions.ElapsedMilliseconds,
                    actions.Disposition, actions.FreePages, new FileInfo(_authority.DatabasePath).Length,
                    File.Exists(_authority.DatabasePath + "-wal")
                        ? new FileInfo(_authority.DatabasePath + "-wal").Length : 0) { Actions = actions };
            }
            return await _authority.RetireOrdinaryActivityBatchAsync(cancellationToken);
        }
        finally { _runGate.Release(); }
    }

    private readonly Dictionary<string, SyncRunActivityWriter> _activityRunWriters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activityWritersFinishing = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activityIncompleteWriters = new(StringComparer.Ordinal);

    private async Task RegisterRunActivityWriterAsync(SyncAuthorityCommandAdmission admission, string kind,
        CancellationToken token)
    {
        if (kind is not ("run_pair" or "run_all" or "reconcile" or "resolve_conflict" or "backup")) return;
        var writer = await _authority.OpenRunActivityWriterAsync(admission, token).ConfigureAwait(false);
        lock (_activityLogGate) _activityRunWriters[admission.CommandRequestId] = writer;
    }

    private async Task RetainRunActivityAsync(SyncActivityEvent item)
    {
        Task<bool>? flush = null;
        lock (_activityLogGate)
        {
            if (item.RunId is not null && _activityRunWriters.TryGetValue(item.RunId, out var writer))
            {
                _activityProgressBatch.Add(writer.Enqueue(item));
                flush = PersistPendingActivityAsync();
            }
        }
        if (flush is not null) { await flush.ConfigureAwait(false); return; }
        // A preparation failure may precede command acceptance. It is a fresh diagnostic linked to
        // the failed proposed run, not a writer opened retrospectively for an absent/terminal command.
        if (item.RunId is not null && await _authority.LoadRetainedCommandAdmissionAsync(item.RunId) is not null)
            throw new InvalidOperationException("The retained run has no open activity writer.");
        await _authority.AppendDiagnosticActivityAsync(item with { RelatedRunId = item.RunId, RunId = null });
    }

    private async Task FinishRunActivityAsync(PilotRunControl control)
    {
        string? id;
        bool pruneRecovery;
        lock (_runControlGate)
        {
            id = ReferenceEquals(_activeRunControl, control) ? _activeRunSnapshot?.Id : null;
            pruneRecovery = id is not null && _activeRunSnapshot?.Kind is
                "run_pair" or "run_all" or "reconcile" or "resolve_conflict";
        }
        if (id is not null)
            lock (_activityLogGate)
                if (_activityRunWriters.TryGetValue(id, out var writer))
                {
                    writer.MarkClosed();
                    _activityWritersFinishing.Add(id);
                }
        if (id is not null)
            await _authority.CloseOrdinaryCommandIssuanceAsync(id).ConfigureAwait(false);
        await FlushActivityProgressAsync().ConfigureAwait(false);
        await CloseDrainedActivityWritersAsync().ConfigureAwait(false);
        if (pruneRecovery && !control.IsStopRequested)
        {
            try { await PruneLocalRecoveryAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                // Maintenance must not turn a completed sync into a failed command.
                System.Diagnostics.Trace.TraceWarning("Local recovery retention deferred: {0}", error.Message);
            }
        }
    }

    private async Task CloseDrainedActivityWritersAsync()
    {
        SyncRunActivityWriter[] drained;
        lock (_activityLogGate)
            drained = _activityWritersFinishing.Where(id =>
                !_activityProgressBatch.Any(item => item.Writer.CommandId == id))
                .Select(id => _activityRunWriters[id]).ToArray();
        foreach (var writer in drained)
        {
            try
            {
                bool incomplete;
                lock (_activityLogGate) incomplete = _activityIncompleteWriters.Contains(writer.CommandId);
                await _authority.CloseRunActivityWriterAsync(writer, incomplete: incomplete).ConfigureAwait(false);
                lock (_activityLogGate)
                {
                    _activityRunWriters.Remove(writer.CommandId);
                    _activityWritersFinishing.Remove(writer.CommandId);
                    _activityIncompleteWriters.Remove(writer.CommandId);
                }
            }
            catch (Exception error) { RecordActivityLogFailure(error); }
        }
    }

    private readonly object _activityLogGate = new();
    private readonly object _progressDeliveryGate = new();
    private Task _progressDeliveryTail = Task.CompletedTask;
    private Task<bool>? _activityFlushTask;
    // Presentation-only phase/path timing. It carries the started-at tick that a retained event reports as its
    // measured duration and nothing else: it never authorises an effect, never controls a transfer and is never
    // itself durable. The three fixed bounds below keep it inside a latest-ten-minute, at-most-1000-entry and
    // at-most-2MiB window. A single entry whose own conservative charge exceeds the byte bound stays uncached
    // while its live callback still receives the complete phase, path and content; no key, phase or filename is
    // ever truncated. Least-recently-updated transient entries are evicted first.
    private readonly Dictionary<string, ActivityProgressTiming> _activityProgress = new(StringComparer.Ordinal);
    private long _activityProgressBytes;
    private long _activityProgressSweptTick;
    private const int MaximumProgressCacheEntries = 1000;
    private const long MaximumProgressCacheBytes = 2 * 1024 * 1024;
    private const long ProgressCacheEntryOverheadBytes = 64;
    // G1: the progress clock is Environment.TickCount64 milliseconds, so the age and sweep thresholds must be
    // milliseconds too; TimeSpan.Ticks would have compared 100-nanosecond units against milliseconds.
    private static readonly long MaximumProgressCacheAgeMilliseconds = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    private static readonly long ProgressCacheSweepIntervalMilliseconds = (long)TimeSpan.FromSeconds(1).TotalMilliseconds;
    private readonly record struct ActivityProgressTiming(string Phase, string Path, long StartedTick,
        long UpdatedTick, bool Transient, long Bytes);
    // One monotonic clock for cache timing. Tests may supply an offset-free clock so the age bound can be
    // proved without waiting ten real minutes; production keeps the existing process tick.
    internal Func<long>? ActivityProgressClockForTest { get; set; }
    private long ActivityProgressTick() => ActivityProgressClockForTest?.Invoke() ?? Environment.TickCount64;
    internal (int Entries, long Bytes) ActivityProgressCacheForTest
    { get { lock (_activityLogGate) return (_activityProgress.Count, _activityProgressBytes); } }
    private readonly List<SyncPendingActivityEvent> _activityProgressBatch = [];
    private long _activityLastFlush;
    private const int MaximumPendingLogEvents = 8192;
    // Informational inventory events are lossless, but do not require one durable
    // transaction every few files. Keep the same half-second live durability
    // boundary, with a bounded prefix large enough to amortize SQLite commits.
    private const int InventoryLogBatchEvents = 256;
    private bool _activityPermanentGap;
    private int _activityOmittedEvents;
    public bool ActivityLogIncomplete { get; private set; }
    public string? ActivityLogError { get; private set; }
    public int ActivityLogPendingCount { get { lock (_activityLogGate) return _activityProgressBatch.Count; } }

    private void RecordActivityLogFailure(Exception error, bool permanent = false)
    {
        ActivityLogIncomplete = true;
        ActivityLogError = SyncActivityText.Failure(error);
        _activityPermanentGap |= permanent;
    }

    // Called under the short state lock. One owned flush drains ordered prefixes;
    // concurrent producers can join the next durable batch without holding up all
    // other producers on a database commit. Every flush-requesting caller awaits it.
    private Task<bool> PersistPendingActivityAsync()
    {
        if (_activityFlushTask is not null) return _activityFlushTask;
        if (_activityProgressBatch.Count == 0) return Task.FromResult(true);
        return _activityFlushTask = Task.Run(DrainPendingActivityAsync);
    }

    private async Task<bool> DrainPendingActivityAsync()
    {
        try
        {
            while (true)
            {
                SyncPendingActivityEvent[] batch;
                lock (_activityLogGate) batch = _activityProgressBatch.ToArray();
                await _authority.AppendRunActivityAsync(batch).ConfigureAwait(false);
                lock (_activityLogGate)
                {
                    // New arrivals are after this exact prefix. Never clear them,
                    // and never remove an ambiguously committed/failed batch.
                    _activityProgressBatch.RemoveRange(0, batch.Length);
                    _activityLastFlush = Environment.TickCount64;
                    ActivityLogIncomplete = _activityPermanentGap || _activityOmittedEvents != 0;
                    if (!ActivityLogIncomplete) ActivityLogError = null;
                    if (_activityProgressBatch.Count != 0) continue;
                    _activityFlushTask = null;
                    return true;
                }
            }
        }
        catch (Exception error)
        {
            lock (_activityLogGate)
            {
                RecordActivityLogFailure(error);
                _activityFlushTask = null;
            }
            return false;
        }
    }

    // One progress submission's batch outcome/phase identity and whether it is transient. Inventory/
    // comparison scanning, hashing and checking callbacks and the informational Planned/Running/Internal
    // action phases are transient: their live callback already carries the exact phase, path, unit and
    // counts, so they never enqueue a presentation record, never call the append path, never block behind
    // the durable writer and never count as pending or omitted. A batch Pending start, a terminal batch
    // state, an explicit result, failure type or failure message and every other per-action phase -
    // including an unproved null or Unknown phase - keeps its durable record: an unproved phase is never
    // treated as informational. A batch outcome is happening now, so a comparison attached for last-target
    // context is not a new reading/hashing callback and must not backdate or relabel that outcome.
    private static (bool BatchOutcome, string Phase, bool Transient) ClassifyProgress(
        SyncAuthorityBatchProgress progress)
    {
        var step = progress.Comparison;
        var action = progress.Action;
        var batchOutcome = action is null && (progress.Result is not null ||
            progress.FailureType is not null || progress.State is
                "Completed" or "Stopped" or "Needs attention" or "Blocked" or "Failed");
        var phase = batchOutcome ? progress.State : action is not null ? action.Stage : step?.Phase ?? progress.State;
        var transient = progress.Result is null && progress.FailureType is null &&
            progress.FailureMessage is null && progress.State is not ("Pending" or "Completed" or "Stopped" or
                "Needs attention" or "Blocked" or "Failed") &&
                (action is null && step is not null ||
             action?.Phase is SyncActivityPhase.Planned or SyncActivityPhase.Running or
                 SyncActivityPhase.Internal);
        return (batchOutcome, phase, transient);
    }

    private Task RetainProgressAsync(SyncAuthorityBatchProgress progress, string? runId)
    {
        lock (_activityLogGate)
        {
            try
            {
                var step = progress.Comparison;
                var action = progress.Action;
                var (batchOutcome, phase, transient) = ClassifyProgress(progress);
                var key = (runId ?? "") + "|" + progress.Pair.Id + "|" + (action?.OperationId ?? "inventory");
                var tick = ActivityProgressTick();
                var target = action?.RelativePath ?? step?.RelativePath ?? "";
                // Batch persistence, not the operations themselves. Different committed counts/bytes
                // on the same target remain independently observable even within one second.
                var (startedTick, stageChanged) = UpdateActivityProgressTiming(key, phase, target, tick, transient);
                if (transient) return Task.CompletedTask;
                var terminal = progress.Result is not null || progress.FailureType is not null ||
                               progress.State is "Completed" or "Stopped" or "Needs attention" or "Blocked" or "Failed";
                var severity = progress.FailureType is not null || progress.State is "Blocked" or "Failed" ||
                    action?.Phase is SyncActivityPhase.Blocked or SyncActivityPhase.EffectUnknown ? "Error" :
                    progress.State is "Needs attention" or "Stopped" || action?.Phase is SyncActivityPhase.Conflict or SyncActivityPhase.Cancelled ? "Warning" :
                    progress.State == "Completed" || action?.Phase == SyncActivityPhase.Completed ? "Success" : "Information";
                // AppendActivityEventsAsync applies the same credential/URL redaction as every retained event.
                // Keep the actual failed operation and reason retrievable, not a circular 'needs attention' label.
                var title = action is not null ? action.Action + " — " + action.Stage :
                    !batchOutcome && step is not null ? step.Phase : (progress.State == "Pending" ? "Sync started" :
                        progress.State == "Completed" ? "Sync finished" : progress.State);
                // A retained reason does not require a .NET exception type. Scan cancellation and
                // other safe refusals supply the concrete explanation in FailureMessage alone.
                // Never replace it with the generic state or a stale comparison callback.
                var message = progress.FailureType is not null ? progress.FailureType + ": " +
                        (progress.FailureMessage ?? "The operation failed without an additional provider explanation.") :
                    !string.IsNullOrWhiteSpace(progress.FailureMessage) ? progress.FailureMessage :
                    action is not null ? action.Detail :
                    progress.Result is { } result ? $"Uploaded: {result.Uploaded}. Downloaded: {result.Downloaded}. Unchanged: {result.Identical}. Conflicts: {result.Conflicts}. Blocked: {result.Blocked}." :
                    step is not null ? SyncProgressDescription.Describe(step) :
                    title;
                if (_activityProgressBatch.Count >= MaximumPendingLogEvents)
                {
                    // Bound memory if the durable store is unavailable. Stop admitting
                    // new effects at the existing pause boundary, retain the pending
                    // records for retry, and disclose every omitted event explicitly.
                    lock (_runControlGate) _activeRunControl?.Pause();
                    _activityOmittedEvents++;
                    if (runId is not null) _activityIncompleteWriters.Add(runId);
                    ActivityLogIncomplete = true;
                    ActivityLogError = $"Log storage is unavailable; {_activityOmittedEvents:N0} additional events could not be buffered. Sync is paused before the next file. " + ActivityLogError;
                    return PersistPendingActivityAsync();
                }
                var retainedEvent = new SyncActivityEvent("",
                    batchOutcome ? DateTimeOffset.UtcNow : action?.OccurredUtc ?? step?.ReportedUtc ?? DateTimeOffset.UtcNow, phase.StartsWith("reconcile", StringComparison.OrdinalIgnoreCase)
                        ? [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics] : [SyncActivityCategories.Sync], severity,
                    action is null ? "Folder pair" : action.ItemKind is "Folder" or "Directory" ? "Folder" : "File",
                    title, message, string.IsNullOrWhiteSpace(progress.Pair.ConnectionId) ? null : progress.Pair.ConnectionId, progress.Pair.Id, runId,
                    action?.OperationId, action?.RelativePath ?? step?.RelativePath ?? "",
                    SyncEndpointContext.Capture(progress.Pair), action?.OperationKind, action?.Phase,
                    action?.Progress ?? (step is null ? null : new SyncActivityProgress(step.CompletedChecks,
                        step.TotalChecks > 0 ? step.TotalChecks : null,
                        step.ProgressUnit, tick - startedTick)));
                if (runId is null)
                    return RetainRunActivityAsync(retainedEvent);
                if (!_activityRunWriters.TryGetValue(runId, out var historyWriter))
                    throw new InvalidOperationException("This progress event has no exact admitted run history writer.");
                _activityProgressBatch.Add(historyWriter.Enqueue(retainedEvent));
                // Keep every distinct target/operation, batching only database commits. A per-file Completed
                // action is presentation history, not an effect boundary: its row is still retained but it
                // rides the ordinary batched flush (count, half-second age, terminal or run-end) instead of
                // committing on its own. Terminal outcomes, effect/action and non-completed outcomes flush
                // immediately; a presentation failure never authorizes or replays an effect.
                var completedFileAction = action is not null && action.Phase == SyncActivityPhase.Completed &&
                    action.ItemKind is not ("Folder" or "Directory");
                if (terminal || (stageChanged && action is not null && !completedFileAction) ||
                    (step is null && !completedFileAction) ||
                    _activityProgressBatch.Count >= InventoryLogBatchEvents || tick - _activityLastFlush >= 500)
                {
                    return CompleteProgressRetentionAsync(PersistPendingActivityAsync(), terminal,
                        (runId ?? "") + "|" + progress.Pair.Id + "|");
                }
                return Task.CompletedTask;
            }
            catch (Exception error)
            {
                // An exception before enqueue omitted this event; a later successful drain
                // cannot claim that this particular run has complete history.
                if (runId is not null) _activityIncompleteWriters.Add(runId);
                RecordActivityLogFailure(error, permanent: true);
                return Task.CompletedTask;
            }
        }
    }

    // Called under the state lock. Returns this key's started-at tick and stage-change flag and keeps the
    // cache inside its age, count and byte bounds. A key, phase or filename is never truncated: an entry whose
    // own conservative charge alone exceeds the byte bound simply stays uncached and is re-derived next time.
    private (long StartedTick, bool StageChanged) UpdateActivityProgressTiming(string key, string phase,
        string target, long tick, bool transient)
    {
        var bytes = (long)System.Text.Encoding.UTF8.GetByteCount(key) +
            System.Text.Encoding.UTF8.GetByteCount(phase) + System.Text.Encoding.UTF8.GetByteCount(target) +
            ProgressCacheEntryOverheadBytes;
        var found = _activityProgress.TryGetValue(key, out var prior);
        var stageChanged = !found || !string.Equals(prior.Phase, phase, StringComparison.Ordinal);
        var phaseChanged = stageChanged || !string.Equals(prior.Path, target, StringComparison.Ordinal);
        var startedTick = phaseChanged ? tick : prior.StartedTick;
        if (bytes > MaximumProgressCacheBytes) return (tick, stageChanged);
        RemoveActivityProgressTiming(key);
        _activityProgress[key] = new ActivityProgressTiming(phase, target, startedTick, tick, transient, bytes);
        _activityProgressBytes += bytes;
        TrimActivityProgressCache(tick);
        return (startedTick, stageChanged);
    }

    // Called under the state lock. Age is swept on a bounded interval; the count and byte bounds are enforced on
    // every insert, with least-recently-updated transient entries evicted first. A trim continues to a water mark
    // below each bound so the whole cache is never re-sorted for every single insert, and an eviction only ever
    // changes which timing row remains: it can never authorise, suppress or replay an effect.
    private void TrimActivityProgressCache(long tick)
    {
        if (tick - _activityProgressSweptTick >= ProgressCacheSweepIntervalMilliseconds)
        {
            _activityProgressSweptTick = tick;
            foreach (var aged in _activityProgress
                         .Where(item => tick - item.Value.UpdatedTick >= MaximumProgressCacheAgeMilliseconds)
                         .Select(item => item.Key).ToArray())
                RemoveActivityProgressTiming(aged);
        }
        if (_activityProgress.Count <= MaximumProgressCacheEntries &&
            _activityProgressBytes <= MaximumProgressCacheBytes) return;
        var entryFloor = MaximumProgressCacheEntries * 3 / 4;
        var byteFloor = MaximumProgressCacheBytes * 3 / 4;
        bool AboveWaterMark() => _activityProgress.Count > entryFloor || _activityProgressBytes > byteFloor;
        foreach (var expired in _activityProgress.Where(item => item.Value.Transient)
                     .OrderBy(item => item.Value.UpdatedTick).ThenBy(item => item.Key, StringComparer.Ordinal)
                     .Select(item => item.Key).ToArray())
        {
            if (!AboveWaterMark()) return;
            RemoveActivityProgressTiming(expired);
        }
        foreach (var remaining in _activityProgress.OrderBy(item => item.Value.UpdatedTick)
                     .ThenBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key).ToArray())
        {
            if (!AboveWaterMark()) return;
            RemoveActivityProgressTiming(remaining);
        }
    }

    private void RemoveActivityProgressTiming(string key)
    {
        if (_activityProgress.Remove(key, out var removed)) _activityProgressBytes -= removed.Bytes;
    }

    private async Task CompleteProgressRetentionAsync(Task<bool> flush, bool terminal, string keyPrefix)
    {
        if (!await flush.ConfigureAwait(false) || !terminal) return;
        lock (_activityLogGate)
            foreach (var old in _activityProgress.Keys.Where(value => value.StartsWith(
                         keyPrefix, StringComparison.Ordinal)).ToArray())
                RemoveActivityProgressTiming(old);
    }

    internal Task RetainProgressForTestAsync(SyncAuthorityBatchProgress progress,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null) => NotifyProgressAsync(onProgress, progress);

    // Retrying persistence never re-executes a file/provider operation. The same
    // immutable event IDs make an ambiguous prior database commit idempotent.
    public async Task RetryPendingActivityPersistenceAsync()
    {
        await FlushActivityProgressAsync().ConfigureAwait(false);
        await CloseDrainedActivityWritersAsync().ConfigureAwait(false);
    }

    private async Task FlushActivityProgressAsync()
    {
        Task<bool> flush;
        lock (_activityLogGate) flush = PersistPendingActivityAsync();
        await flush.ConfigureAwait(false);
        lock (_activityLogGate)
        {
            _activityProgress.Clear();
            _activityProgressBytes = 0;
        }
    }
}
