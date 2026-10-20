using static Massdex.SyncShark.V2.RunLog;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public static class DropboxProviderConfiguration
{
    public const string PendingClientId = "PENDING_PROVIDER_REGISTRATION";
    public const string RegisteredClientId = "fxzcfcfkv6f7gt6";
    public const string RegisteredRedirectUri = "http://127.0.0.1:47821/oauth/dropbox/callback/";
    public const string ContentAccess = "full_dropbox";
    public static readonly IReadOnlyList<string> RequiredScopes =
    [
        "account_info.read",
        "files.content.read",
        "files.content.write",
        "files.metadata.read",
        "files.metadata.write",
        "sharing.read",
        "sharing.write"
    ];

    public static bool IsLiveProviderBound => !RegisteredClientId.Equals(PendingClientId, StringComparison.Ordinal);
    public static readonly IReadOnlyList<string> FileRequestScopes = ["file_requests.read", "file_requests.write"];
    public static IReadOnlyList<string> RequestedScopes(bool includeFileRequests) => includeFileRequests
        ? RequiredScopes.Concat(FileRequestScopes).Order(StringComparer.Ordinal).ToArray() : RequiredScopes;
    public static bool HasFileRequestAccess(IReadOnlyList<string>? scopes) =>
        FileRequestScopes.All(scope => scopes?.Contains(scope, StringComparer.Ordinal) == true);
    public static bool IsSupportedGrant(IReadOnlyList<string>? scopes) => scopes is not null &&
        (scopes.Order(StringComparer.Ordinal).SequenceEqual(RequiredScopes.Order(StringComparer.Ordinal)) ||
         scopes.Order(StringComparer.Ordinal).SequenceEqual(RequestedScopes(true)));

    public static void ValidateConnection(DropboxConnectionBinding binding)
    {
        if (!IsLiveProviderBound || !binding.ClientId.Equals(RegisteredClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("The registered Dropbox application is not bound in this build.");
        if (!binding.ContentAccess.Equals(ContentAccess, StringComparison.Ordinal))
            throw new InvalidOperationException("Dropbox content access differs from the registered Full Dropbox boundary.");
        if (!IsSupportedGrant(binding.Scopes))
            throw new InvalidOperationException("Dropbox scopes differ from the required Sync Shark grant.");
        if (string.IsNullOrWhiteSpace(binding.AccountId) || string.IsNullOrWhiteSpace(binding.RootNamespaceId) || string.IsNullOrWhiteSpace(binding.HomeNamespaceId))
            throw new InvalidOperationException("Dropbox account identity is incomplete.");
    }
}

public sealed record DropboxConnectionBinding(
    string ClientId,
    string AccountId,
    string RootNamespaceId,
    string HomeNamespaceId,
    string ContentAccess,
    IReadOnlyList<string> Scopes,
    string ConnectionId = "",
    string DisplayName = "",
    string Email = "",
    string AccountType = "",
    DateTimeOffset? ConnectedUtc = null,
    DateTimeOffset? LastVerifiedUtc = null,
    string CredentialReference = "",
    IReadOnlyList<ProviderConnectionRecord>? RetainedConnections = null,
    IReadOnlyList<DropboxConnectionBinding>? AdditionalConnections = null)
{
    public static string CreateConnectionId(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return "connection-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("dropbox\n" + accountId)))
            .ToLowerInvariant()[..24];
    }

    public DropboxConnectionBinding Normalize() => this with
    {
        ConnectionId = string.IsNullOrWhiteSpace(ConnectionId) ? CreateConnectionId(AccountId) : ConnectionId.Trim(),
        DisplayName = DisplayName?.Trim() ?? string.Empty,
        Email = Email?.Trim() ?? string.Empty,
        AccountType = AccountType?.Trim() ?? string.Empty,
        CredentialReference = string.IsNullOrWhiteSpace(CredentialReference)
            ? WindowsDropboxRefreshTokenStore.RegisteredTarget
            : CredentialReference.Trim(),
        Scopes = Scopes?.Order(StringComparer.Ordinal).ToArray() ?? [],
        RetainedConnections = NormalizeRetainedConnections(RetainedConnections)
    };

    public ProviderConnectionRecord ToProviderRecord(bool active = true) =>
        new(ConnectionId, "dropbox", AccountId, RootNamespaceId, HomeNamespaceId,
            DisplayName, Email, AccountType, CredentialReference, active,
            new ProviderCapabilityFlags(ContentRead: true, ContentWrite: true, MetadataRead: true,
                MetadataWrite: true, SharingRead: Scopes.Contains("sharing.read", StringComparer.Ordinal),
                SharingWrite: Scopes.Contains("sharing.write", StringComparer.Ordinal),
                StableObjectIds: true, DeltaCursor: true, NativeMove: true, Revisions: true,
                DeletedItems: true, ProgrammaticRewind: false,
                FileRequestsRead: Scopes.Contains("file_requests.read", StringComparer.Ordinal),
                FileRequestsWrite: Scopes.Contains("file_requests.write", StringComparer.Ordinal)));

    private static IReadOnlyList<ProviderConnectionRecord>? NormalizeRetainedConnections(
        IReadOnlyList<ProviderConnectionRecord>? connections)
    {
        if (connections is null) return null;
        var normalized = connections.Select(ProviderConnectionRecord.Normalize)
            .GroupBy(item => item.ConnectionId, StringComparer.Ordinal)
            .Select(group => group.Single())
            .OrderBy(item => item.Provider, StringComparer.Ordinal)
            .ThenBy(item => item.ConnectionId, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Any(item => item.IsActive && !SyncProviders.IsKnown(item.Provider)))
            throw new InvalidDataException("This provider is not supported for active synchronization yet.");
        return normalized;
    }
}

public sealed record ProviderCapabilityFlags(
    bool ContentRead,
    bool ContentWrite,
    bool MetadataRead,
    bool MetadataWrite,
    bool SharingRead,
    bool SharingWrite,
    bool StableObjectIds,
    bool DeltaCursor,
    bool NativeMove,
    bool Revisions,
    bool DeletedItems,
    bool ProgrammaticRewind,
    bool FileRequestsRead = false,
    bool FileRequestsWrite = false,
    bool NativeDocuments = false,
    bool Shortcuts = false,
    bool SharedDrives = false,
    bool ConditionalWrites = true);

public sealed record ProviderConnectionRecord(
    string ConnectionId,
    string Provider,
    string AccountId,
    string RootNamespaceId,
    string HomeNamespaceId,
    string DisplayName,
    string Email,
    string AccountType,
    string CredentialReference,
    bool IsActive,
    ProviderCapabilityFlags Capabilities)
{
    public static ProviderConnectionRecord Normalize(ProviderConnectionRecord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var identity in new[]
                 {
                     value.ConnectionId, value.Provider, value.AccountId, value.RootNamespaceId,
                     value.HomeNamespaceId
                 })
            if (string.IsNullOrWhiteSpace(identity))
                throw new InvalidDataException("A retained provider connection identity is incomplete.");
        if (value.Capabilities is null)
            throw new InvalidDataException("A retained provider connection has no capability profile.");
        return value with
        {
            ConnectionId = value.ConnectionId.Trim(),
            Provider = value.Provider.Trim().ToLowerInvariant(),
            AccountId = value.AccountId.Trim(),
            RootNamespaceId = value.RootNamespaceId.Trim(),
            HomeNamespaceId = value.HomeNamespaceId.Trim(),
            DisplayName = value.DisplayName?.Trim() ?? string.Empty,
            Email = value.Email?.Trim() ?? string.Empty,
            AccountType = value.AccountType?.Trim() ?? string.Empty,
            CredentialReference = value.CredentialReference?.Trim() ?? string.Empty
        };
    }
}

public interface IDropboxAccessTokenSource
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class DropboxApiException(HttpStatusCode statusCode, string safeReason) : HttpRequestException(safeReason, null, statusCode)
{
    public string SafeReason { get; } = safeReason;

    /// <summary>A structured Dropbox write-error tag, never inferred from message text.</summary>
    public string? WriteErrorTag { get; init; }

    /// <summary>The typed OAuth error Dropbox's token endpoint returned, or None for a non-token failure.</summary>
    public DropboxOAuthError OAuthError { get; init; }
}

public sealed partial class DropboxPilotClient(
    HttpClient httpClient,
    IDropboxAccessTokenSource tokenSource,
    string remoteRoot,
    DropboxConnectionBinding expectedConnection,
    string? expectedFolderId = null,
    SyncTransferRateLimiter? transferLimiter = null,
    long? uploadSessionThresholdBytes = null,
    int? uploadSessionChunkSizeBytes = null) : ISyncRemoteStore, ISyncRemoteChangeFeed,
    ISyncCrossRootRemoteMove, ISyncResumableRemoteStore, ISyncRemoteRangeStore, ISyncTransferResourceEndpoint,
    ISyncRemoteObjectIdentityLookup, ISyncUploadSessionPolicySource
{
    public SyncUploadSessionPolicy UploadSessionPolicy => new(_singleRequestUploadLimit, _singleRequestUploadLimit);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private const int UploadSessionChunkSize = 8 * 1024 * 1024;
    private const string RecoverySpoolPrefix = "syncshark-recovery-spool-v1-";
    private const long RecoverySpoolSafetyReserve = 64L * 1024 * 1024;
    private const int MaximumJsonResponseBytes = 16 * 1024 * 1024;
    private const int MaximumContinuationPages = 100_000;
    private const int MaximumCursorBytes = 16 * 1024;
    // A prepare run has no deadline of its own, so each account/root identity verification attempt is
    // bounded by RootVerifyTimeout and retried this many times before it refuses.
    private const int RootVerifyAttempts = 3;
    // One bounded deleted-item inventory verifies the selected root once before the first page and once
    // after the last, and asks for Dropbox's supported upper page size, matching the main recursive
    // listing rather than a small per-page request that multiplies whole-tree round trips.
    private const int DeletedListingPageSize = 2_000;
    // At most this many revision-history reads run at once; the phase then writes its results in path
    // order so the outcome is identical to a strictly sequential pass.
    // Four: Dropbox serves list_revisions at about 4/s per account, and these reads share the account pacer with
    // the apply's downloads and metadata reads; more workers only race the foreground work for the same turns.
    private const int DeletionHistoryConcurrency = 4;

    // A detailed trace always goes to the run's log file; on the console it shows only with --verbose (RQ-130).
    private readonly string _root = PilotPath.NormalizeDropboxRoot(remoteRoot);
    private readonly string? _rootId = string.IsNullOrWhiteSpace(expectedFolderId) ? null : expectedFolderId;
    private readonly SyncTransferRateLimiter _transferLimiter = transferLimiter ??
                                                               new SyncTransferRateLimiter(new SyncTransferSettings());
    private readonly long _singleRequestUploadLimit = ValidateUploadSessionThreshold(uploadSessionThresholdBytes);
    private readonly int _uploadSessionChunkSize = ValidateUploadSessionChunkSize(uploadSessionChunkSizeBytes);
    private long _providerCallCount;

    string ISyncTransferResourceEndpoint.TransferResourceKey => TransferResourceKey;
    private string TransferResourceKey => "dropbox:" + expectedConnection.Normalize().ConnectionId;
    private Task<HttpResponseMessage> SendResourceRequestAsync(HttpRequestMessage request,
        HttpCompletionOption option, CancellationToken token, Action? onSending = null,
        SyncProviderOperation operation = SyncProviderOperation.Write) =>
        SyncProviderRequestPacing.SendAsync(_transferLimiter, new(TransferResourceKey, operation), false, httpClient, request, option, token, onSending);

    internal static string? RecoverySpoolRootForTest { get; set; }

    public long ProviderCallCount => Interlocked.Read(ref _providerCallCount);

    public Task<IReadOnlyList<DropboxRevisionEntry>> ListRevisionsAsync(string relativePath,
        CancellationToken cancellationToken = default) => ListFolderDeleteRevisionsAsync(relativePath, false, cancellationToken);

    internal async Task<IReadOnlyList<DropboxRevisionEntry>> ListFolderDeleteRevisionsAsync(string relativePath,
        bool unknownKind, CancellationToken cancellationToken)
    {
        var path = PilotPath.NormalizeDropboxRelative(relativePath);
        using var document = unknownKind
            ? await PostJsonCoreAsync("https://api.dropboxapi.com/2/files/list_revisions",
                new { path = RequiredRootId() + "/" + path, mode = "path", limit = 100 }, false, cancellationToken,
                allowedConflict: error => ReadUnionTag(error) == "path")
            : await ReadRevisionHistoryAsync(path, cancellationToken);
        var result = new List<DropboxRevisionEntry>();
        if (document is null) return result;
        foreach (var item in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var entry = RemoteEntryFromMetadata(item);
            result.Add(new DropboxRevisionEntry(path, entry.Id!, entry.Revision!, entry.Size,
                entry.ContentHash, entry.ModifiedUtc));
        }
        return result;
    }

    private Task<JsonDocument?> ReadRevisionHistoryAsync(string normalizedRelativePath,
        CancellationToken cancellationToken) =>
        PostJsonAllowingMissingPathAsync("https://api.dropboxapi.com/2/files/list_revisions", new
        {
            path = RequiredRootId() + "/" + normalizedRelativePath,
            mode = "path",
            limit = 100
        }, cancellationToken);

    public Task<IReadOnlyDictionary<string, SyncRemoteDeletionEvidence>> LookupDeletionEvidenceAsync(
        IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken = default) =>
        LookupDeletionEvidenceCoreAsync(relativePaths, cancellationToken, propagateAccessRefusal: false);

    /// <summary>The core-v2 form: an account-access refusal escapes instead of becoming unknown evidence.</summary>
    internal Task<IReadOnlyDictionary<string, SyncRemoteDeletionEvidence>> LookupDeletionEvidenceForV2Async(
        IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken = default) =>
        LookupDeletionEvidenceCoreAsync(relativePaths, cancellationToken, propagateAccessRefusal: true);

    /// <summary>
    /// The same lookup when this run's full listing already reported which paths Dropbox holds as deleted: the
    /// requested paths are probed against those sets and only the probes reach revision history, with no second
    /// whole-tree deleted listing. A null set falls back to reading one (a delta run that did not list in full).
    /// A stored probe result (from an earlier run's cache) replaces a new read for a path that is still a probe,
    /// and every fresh probe is handed to <paramref name="onProbed"/> so the caller can persist it.
    /// </summary>
    internal Task<IReadOnlyDictionary<string, SyncRemoteDeletionEvidence>> LookupDeletionEvidenceForV2Async(
        IReadOnlyCollection<string> relativePaths, IReadOnlySet<string>? knownDeletedPaths,
        IReadOnlySet<string>? knownDeletedAncestors, IReadOnlyDictionary<string, V2.DeletionEvidenceRecord>? cached,
        Action<IReadOnlyList<V2.DeletionEvidenceRecord>>? onProbed, CancellationToken cancellationToken = default,
        Action<string, SyncRemoteDeletionEvidence>? onResolved = null) =>
        LookupDeletionEvidenceCoreAsync(relativePaths, cancellationToken, propagateAccessRefusal: true,
            knownDeletedPaths: knownDeletedPaths, knownDeletedAncestors: knownDeletedAncestors, cached: cached,
            onProbed: onProbed, onResolved: onResolved);

    private async Task<IReadOnlyDictionary<string, SyncRemoteDeletionEvidence>> LookupDeletionEvidenceCoreAsync(
        IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken, bool propagateAccessRefusal,
        IReadOnlySet<string>? knownDeletedPaths = null, IReadOnlySet<string>? knownDeletedAncestors = null,
        IReadOnlyDictionary<string, V2.DeletionEvidenceRecord>? cached = null,
        Action<IReadOnlyList<V2.DeletionEvidenceRecord>>? onProbed = null,
        Action<string, SyncRemoteDeletionEvidence>? onResolved = null)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        cancellationToken.ThrowIfCancellationRequested();
        var result = relativePaths.Select(PilotPath.NormalizeDropboxRelative)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, _ => new SyncRemoteDeletionEvidence(null,
                "Dropbox returned no retained deleted entry at this exact path; remote absence alone does not prove deletion."),
                StringComparer.OrdinalIgnoreCase);
        if (result.Count == 0) return result;

        IReadOnlySet<string> exactPaths, ancestorPaths;
        try
        {
            // This run's own full listing may already have reported the deleted paths, in which case the
            // whole-tree deleted inventory is not read again; only probed paths merit a revision-history
            // request. A null set (a delta run that did not list in full) reads one bounded deleted inventory.
            (exactPaths, ancestorPaths) = await DeletedPathsAsync(knownDeletedPaths, knownDeletedAncestors,
                cancellationToken);
        }
        catch (Exception exception) when (IsDeletionLookupFailure(exception, cancellationToken) &&
                !(propagateAccessRefusal && IsAccessRefusal(exception)))
        {
            // A batch-wide listing failure is not proof that each path's history is permanently unreadable.
            var failure = DeletionLookupFailure(exception) with { PermanentFailure = false };
            foreach (var path in result.Keys.ToArray()) result[path] = failure;
            return result;
        }

        // A path is a probe when it is an exact deleted path, or when any proper ancestor folder is in the
        // deleted ancestors: a deleted folder owns its descendants, and Dropbox does not let us list it to
        // enumerate them. The test walks the path's own segments - O(depth) case-insensitive hash lookups. A
        // stored result is reused in place of a new read for a path that is still a probe.
        var probes = result.Keys.Where(path => IsDeletionProbe(path, exactPaths, ancestorPaths))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var probeSet = probes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in result.Keys)
            if (!probeSet.Contains(path))
                onResolved?.Invoke(path, new SyncRemoteDeletionEvidence(null,
                    "Dropbox returned no retained deleted entry at this exact path; remote absence alone does not prove deletion."));
        var exactHits = probes.Count(exactPaths.Contains);
        var toProbe = new List<string>(probes.Length);
        foreach (var path in probes)
        {
            if (cached is not null && cached.TryGetValue(path, out var stored) && stored.HasRequiredContentHash)
            {
                var evidence = StoredEvidence(stored);
                result[path] = evidence;
                onResolved?.Invoke(path, evidence);
            }
            else toProbe.Add(path);
        }
        Trace($"deletion history probes: {exactHits} exact, {probes.Length - exactHits} under a deleted ancestor");
        Trace($"deletion history: {probes.Length - toProbe.Count} reused, {toProbe.Count} to probe");
        var historyPaths = toProbe.ToArray();
        var historyWatch = System.Diagnostics.Stopwatch.StartNew();
        Trace($"deletion history start ({historyWatch.ElapsedMilliseconds} ms); {historyPaths.Length} paths");
        var historyCompleted = 0;
        var lastHistoryReportTick = 0L;
        // A bounded worker pool avoids one Task per held path. The original escaping failure,
        // never a sibling cancellation, is rethrown after the active reads settle.
        using (var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var workToken = work.Token;
            Exception? failure = null;
            var checkpointLock = new object();
            var checkpoint = new List<V2.DeletionEvidenceRecord>(200);
            SyncRemoteDeletionEvidence CompleteProbe(string path, SyncRemoteDeletionEvidence evidence)
            {
                if (!evidence.LookupFailed && onProbed is not null)
                {
                    lock (checkpointLock)
                    {
                        checkpoint.Add(new(path, evidence.DeletedUtc, ContentHash: evidence.ContentHash));
                        if (checkpoint.Count >= 200)
                        {
                            try { onProbed(checkpoint.ToArray()); }
                            catch (Exception error) { Trace("deletion evidence checkpoint failed: " + error.GetType().Name); }
                            checkpoint.Clear();
                        }
                    }
                }
                // A permanent failure answers this pass as Unknown, with its failure flags intact.
                // It is never cached as successful history; transient failures remain unanswered for retry.
                if (!evidence.LookupFailed || evidence.PermanentFailure) onResolved?.Invoke(path, evidence);
                return evidence;
            }
            var completedReads = new System.Collections.Concurrent.ConcurrentBag<(string Path, SyncRemoteDeletionEvidence? Evidence)>();
            await Parallel.ForEachAsync(historyPaths, new ParallelOptions { MaxDegreeOfParallelism = DeletionHistoryConcurrency }, async (path, _) =>
            {
                try
                {
                    workToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var history = await ReadRevisionHistoryAsync(path, workToken);
                        var evidence = history is null
                            ? new SyncRemoteDeletionEvidence(null, "Dropbox confirms this exact revision-history path was not found; no retained deletion evidence exists.")
                            : DeletionEvidenceFromHistory(history.RootElement);
                        completedReads.Add((path, CompleteProbe(path, evidence)));
                    }
                    catch (Exception exception) when (IsDeletionLookupFailure(exception, workToken) &&
                        !(propagateAccessRefusal && IsAccessRefusal(exception)))
                    {
                        completedReads.Add((path, DeletionLookupFailure(exception)));
                    }
                }
                catch (OperationCanceledException) when (workToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // A sibling already failed and cancelled the batch: this queued read is abandoned, not an outcome.
                    return;
                }
                catch (Exception exception)
                {
                    // The first escaping failure stops the queued reads and is remembered so that it - never a
                    // sibling's cancellation - is what the caller sees.
                    if (Interlocked.CompareExchange(ref failure, exception, null) is null) work.Cancel();
                    return;
                }
                finally
                {
                    var completed = Interlocked.Increment(ref historyCompleted);
                    try
                    {
                        if (ClaimProgressReport(ref lastHistoryReportTick))
                            await SyncByteProgressStream.ReportProcessAsync("Checking Dropbox deletion history", _root,
                                $"{completed:N0}/{historyPaths.Length:N0} paths");
                    }
                    catch (Exception) { /* Progress is best-effort: a throwing sink never replaces the outcome. */ }
                }
            });
            // Flush the partial checkpoint on completion or failure; full checkpoints were committed
            // while probes were still running, so a process crash loses at most the partial batch.
            lock (checkpointLock)
                if (checkpoint.Count > 0)
                    try { onProbed?.Invoke(checkpoint.ToArray()); }
                    catch (Exception error) { Trace("deletion evidence checkpoint failed: " + error.GetType().Name); }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            foreach (var read in completedReads)
                if (read.Evidence is { } evidence) result[read.Path] = evidence;
        }
        Trace($"deletion history done ({historyWatch.ElapsedMilliseconds} ms); {historyPaths.Length} paths");
        return result;
    }

    // The two deleted-path sets for one evidence batch: the run's own full listing when it reported them, else a
    // fresh bounded whole-tree deleted inventory (a delta run did not list in full), whose tombstones are both
    // exact and ancestors. `Exact` is tombstones minus live paths; `Ancestors` keeps every tombstone, including
    // one that is live again, so a folder deleted then re-created still names its still-deleted children.
    private async Task<(IReadOnlySet<string> Exact, IReadOnlySet<string> Ancestors)> DeletedPathsAsync(
        IReadOnlySet<string>? knownDeletedPaths, IReadOnlySet<string>? knownDeletedAncestors,
        CancellationToken cancellationToken)
    {
        if (knownDeletedPaths is not null && knownDeletedAncestors is not null)
            return (knownDeletedPaths, knownDeletedAncestors);
        var listed = (await ListDeletedEntriesAsync(cancellationToken))
            .Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (listed, listed);
    }

    // The evidence a stored probe result stands for: a server deletion time, or the same "no evidence" shape a
    // fresh read of an undeleted path returns.
    private static SyncRemoteDeletionEvidence StoredEvidence(V2.DeletionEvidenceRecord stored) =>
        stored.DeletedUtc is { } at
            ? new(at, "Dropbox revision history confirms is_deleted and supplies the server_deleted timestamp for this exact path.", ContentHash: stored.ContentHash)
            : new(null, "Dropbox returned no retained deleted entry at this exact path; remote absence alone does not prove deletion.", ContentHash: stored.ContentHash);

    // True when the path is an exact deleted path, or when any PROPER ancestor folder of it is in the deleted
    // ancestors (a deleted folder owns its descendants). The walk is over the path's own segments, so it is
    // O(depth) case-insensitive hash lookups and never a listing.
    internal static bool IsDeletionProbe(string path, IReadOnlySet<string> exactPaths, IReadOnlySet<string> ancestorPaths)
    {
        if (exactPaths.Contains(path)) return true;
        var separator = path.LastIndexOf('/');
        while (separator > 0)
        {
            if (ancestorPaths.Contains(path[..separator])) return true;
            separator = path.LastIndexOf('/', separator - 1);
        }
        return false;
    }

    // Claims the next progress-report slot atomically so concurrent workers emit at most one line per
    // interval. A zero last tick means no line has been emitted yet.
    private static bool ClaimProgressReport(ref long lastReportTick, long intervalMilliseconds = 10_000)
    {
        var now = Environment.TickCount64;
        while (true)
        {
            var last = Volatile.Read(ref lastReportTick);
            if (last != 0 && now - last < intervalMilliseconds) return false;
            if (Interlocked.CompareExchange(ref lastReportTick, now, last) == last) return true;
        }
    }

    private static SyncRemoteDeletionEvidence DeletionEvidenceFromHistory(JsonElement history)
    {
        if (history.ValueKind != JsonValueKind.Object ||
            !history.TryGetProperty("is_deleted", out var deleted) ||
            deleted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new(null, "Dropbox revision history did not provide a valid is_deleted flag; deletion time is unproven.", LookupFailed: true, PermanentFailure: true);
        if (!deleted.GetBoolean())
            return new(null, "Dropbox revision history no longer marks this path deleted; deletion time is unproven.");
        if (!history.TryGetProperty("server_deleted", out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParseExact(timestamp.GetString(),
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var deletedUtc))
            return new(null, "Dropbox marks this path deleted but supplies no valid server_deleted timestamp; file modification time is not deletion evidence.", LookupFailed: true, PermanentFailure: true);
        // server_modified belongs to a file revision and must never substitute for server_deleted.
        return new(deletedUtc, "Dropbox revision history confirms is_deleted and supplies the server_deleted timestamp for this exact path.",
            ContentHash: LastRevisionContentHash(history));
    }

    // Dropbox list_revisions returns revisions newest first, so the first entry is the last revision before
    // deletion. Only that entry can prove identical content; a missing, non-string or malformed hash there
    // means the last revision's hash is unknown, and an older entry must never substitute (decision 69).
    private static string? LastRevisionContentHash(JsonElement history)
    {
        if (!history.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
            entries.GetArrayLength() == 0)
            return null;
        var newest = entries[0];
        if (newest.ValueKind != JsonValueKind.Object ||
            !newest.TryGetProperty("content_hash", out var hashValue) ||
            hashValue.ValueKind != JsonValueKind.String)
            return null;
        var hash = hashValue.GetString();
        return hash is { Length: 64 } && hash.All(Uri.IsHexDigit) ? hash : null;
    }

    private static bool IsDeletionLookupFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is
            (HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or
             KeyNotFoundException or ArgumentException or OperationCanceledException);

    private static SyncRemoteDeletionEvidence DeletionLookupFailure(Exception exception)
    {
        // Do not expose response bodies, credentials, request URLs or arbitrary exception text.
        var reason = exception switch
        {
            HttpRequestException { StatusCode: { } status } =>
                "Dropbox returned HTTP " + ((int)status).ToString(CultureInfo.InvariantCulture) + ".",
            HttpRequestException => "Dropbox could not be reached.",
            OperationCanceledException => "The Dropbox read timed out.",
            _ => "Dropbox deletion history could not be read or verified."
        };
        return new(null, "Deletion check failed: " + reason + " Review this item before choosing a copy or deletion.",
            LookupFailed: true, PermanentFailure: IsPermanentDeletionLookupFailure(exception));
    }

    internal static bool IsPermanentDeletionLookupFailure(Exception error) => !IsAccessRefusal(error) &&
        (error is InvalidDataException or JsonException or KeyNotFoundException or FormatException or ArgumentException ||
        error is HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or
            HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity });

    public async Task<IReadOnlyList<DropboxDeletedEntry>> ListDeletedEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, DropboxDeletedEntry>(StringComparer.OrdinalIgnoreCase);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var pages = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Trace($"deleted listing start ({watch.ElapsedMilliseconds} ms)");
        // One paginated deleted inventory for the whole batch: the selected root is verified once before
        // the first page and once after the last - the same shape as the main recursive inventory - instead
        // of once per continuation, which would pay a full account/root round trip on every page.
        await VerifyRootIdentityAsync(cancellationToken);
        var nextReportTick = 0L;
        do
        {
            if (++pages > MaximumContinuationPages)
                throw new InvalidDataException(
                    $"Dropbox exceeded the bounded deleted-item continuation limit of {MaximumContinuationPages:N0} pages.");
            var page = await ListDeletedEntriesPageCoreAsync(cursor, DeletedListingPageSize, cancellationToken,
                verifyRoot: false);
            foreach (var entry in page.Entries) result[entry.RelativePath] = entry;
            cursor = page.ContinueCursor;
            if (cursor is not null && !cursors.Add(CursorFingerprint(cursor)))
                throw new InvalidDataException("Dropbox cycled a deleted-item cursor without completion.");
            var now = Environment.TickCount64;
            if (now >= nextReportTick)
            {
                nextReportTick = now + 10_000;
                // A no-op outside an ambient byte-progress scope; throttled so a fast walk does not flood.
                // Best-effort: a throwing sink never replaces the listing.
                try
                {
                    await SyncByteProgressStream.ReportProcessAsync("Reading Dropbox deletions", _root,
                        $"{result.Count:N0} entries");
                }
                catch (Exception) { /* Progress is best-effort and never the operation's result. */ }
            }
        } while (cursor is not null);
        await VerifyRootIdentityAsync(cancellationToken);
        Trace($"deleted listing done ({watch.ElapsedMilliseconds} ms); {result.Count} entries");
        return result.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<DropboxDeletedPage> ListDeletedEntriesPageAsync(string? cursor = null, int pageSize = 200,
        CancellationToken cancellationToken = default) =>
        ListDeletedEntriesPageCoreAsync(cursor, pageSize, cancellationToken, verifyRoot: true);

    // verifyRoot is false only when a caller already verified once for a whole multi-page walk, so a long
    // deleted listing pays the account/root check at its two ends rather than on every continuation.
    private async Task<DropboxDeletedPage> ListDeletedEntriesPageCoreAsync(string? cursor, int pageSize,
        CancellationToken cancellationToken, bool verifyRoot)
    {
        if (pageSize is < 1 or > 2_000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (verifyRoot) await VerifyRootIdentityAsync(cancellationToken);
        using var document = cursor is null
            ? await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder", new
            {
                path = RequiredRootId(),
                recursive = true,
                include_deleted = true,
                include_mounted_folders = true,
                include_non_downloadable_files = true,
                limit = pageSize
            }, cancellationToken)
            : await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder/continue",
                new { cursor = RequireOpaqueCursor(cursor) }, cancellationToken);
        var entries = new List<DropboxDeletedEntry>();
        foreach (var item in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (item.GetProperty(".tag").GetString() != "deleted") continue;
            var displayed = item.TryGetProperty("path_display", out var display)
                ? display.GetString()
                : item.GetProperty("path_lower").GetString();
            if (displayed is null) continue;
            var relative = ToRelative(displayed);
            entries.Add(new DropboxDeletedEntry(relative,
                item.TryGetProperty("name", out var name) ? name.GetString() ?? Path.GetFileName(relative) :
                Path.GetFileName(relative)));
        }
        entries.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        var hasMore = document.RootElement.GetProperty("has_more").GetBoolean();
        var next = hasMore
            ? RequireCursor(document.RootElement, "Dropbox deleted-item cursor is absent.")
            : null;
        return new DropboxDeletedPage(entries, next);
    }

    public async Task<PilotRemoteEntry> RestoreRevisionAsync(string relativePath, string revision,
        CancellationToken cancellationToken = default)
    {
        var path = PilotPath.NormalizeDropboxRelative(relativePath);
        if (string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("A Dropbox revision is required.", nameof(revision));
        using var document = await PostEffectJsonAsync("https://api.dropboxapi.com/2/files/restore", new
        {
            path = RequiredRootId() + "/" + path,
            rev = revision
        }, cancellationToken);
        return RemoteEntryFromMetadata(document.RootElement);
    }

    public async Task<PilotRemoteEntry> CopyRecoveryToPathAsync(SyncRemoteRecoveryRootBinding recoveryRoot,
        SyncArchiveResult expected, string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        await VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
        var current = await GetRecoveryAsync(recoveryRoot, expected.RecoveryPath, cancellationToken);
        if (current is null || current.FileId != expected.FileId || current.Revision != expected.Revision ||
            current.Size != expected.Size || !string.Equals(current.ContentHash, expected.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected recovery copy no longer matches its retained identity.");
        return await CopyRecoveryToPathAsync(recoveryRoot, expected.FileId, destinationRelativePath, cancellationToken);
    }

    public async Task<PilotRemoteEntry> CopyRecoveryToPathAsync(SyncRemoteRecoveryRootBinding recoveryRoot,
        string recoveryFileId, string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        await VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
        if (string.IsNullOrWhiteSpace(recoveryFileId))
            throw new InvalidDataException("The recovery file identity is unavailable.");
        var destination = PilotPath.NormalizeDropboxRelative(destinationRelativePath);
        if ((await ListEntriesAsync(cancellationToken)).Any(item => string.Equals(item.RelativePath,
                destination, StringComparison.OrdinalIgnoreCase)))
            throw new DropboxApiException(HttpStatusCode.Conflict, "The restore destination is occupied.");
        await EnsureRemoteParentFoldersAsync(destination, cancellationToken);
        using var document = await PostEffectJsonAsync("https://api.dropboxapi.com/2/files/copy_v2", new
        {
            from_path = recoveryFileId,
            to_path = RequiredRootId() + "/" + destination,
            autorename = false,
            allow_ownership_transfer = false
        }, cancellationToken);
        return RemoteEntryFromMetadata(document.RootElement.GetProperty("metadata"));
    }

    public async Task VerifyControlNamespaceAsync(SyncRemoteControlNamespaceBinding binding, string installationId,
        string namespaceId,
        CancellationToken cancellationToken = default)
    {
        installationId = SyncControlNamespace.NormalizeInstallationId(installationId);
        namespaceId = SyncControlNamespace.NormalizeNamespaceId(namespaceId);
        await VerifyAccountAsync(cancellationToken);
        var root = await GetFolderIdentityAsync(binding.RootFolderId, cancellationToken);
        if (!string.Equals(root.Path, binding.RootPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(root.FolderId, binding.RootFolderId, StringComparison.Ordinal))
            throw new IOException("The Dropbox recovery namespace moved or changed identity.");
        var marker = await GetFileMetadataByPathAsync(binding.MarkerPath, cancellationToken);
        if (!string.Equals(marker.Id, binding.MarkerFileId, StringComparison.Ordinal) ||
            !string.Equals(marker.Revision, binding.MarkerRevision, StringComparison.Ordinal) ||
            !string.Equals(marker.ContentHash, binding.MarkerContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Dropbox recovery ownership record changed identity or content.");
        await VerifyMarkerBytesAsync(marker, binding.MarkerPath, installationId, namespaceId, cancellationToken);
    }

    public async Task<PilotRemoteRootBinding> BindEmptyRootAsync(CancellationToken cancellationToken = default)
    {
        DropboxProviderConfiguration.ValidateConnection(expectedConnection);
        await VerifyAccountAsync(cancellationToken);
        var firstIdentity = await GetFolderIdentityAsync(_rootId ?? _root, cancellationToken);
        if (_rootId is not null && (!string.Equals(firstIdentity.FolderId, _rootId, StringComparison.Ordinal) ||
                                    !string.Equals(firstIdentity.Path, _root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected Dropbox folder ID no longer matches its displayed path.");
        using var listing = await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder", new
        {
            path = firstIdentity.FolderId,
            recursive = false,
            include_deleted = false,
            include_mounted_folders = true,
            include_non_downloadable_files = true
        }, cancellationToken);
        if (listing.RootElement.GetProperty("has_more").GetBoolean() || listing.RootElement.GetProperty("entries").GetArrayLength() != 0)
            throw new InvalidDataException("The selected Dropbox folder must be empty when first bound by this legacy workflow.");
        var secondIdentity = await GetFolderIdentityAsync(firstIdentity.FolderId, cancellationToken);
        if (!string.Equals(firstIdentity.FolderId, secondIdentity.FolderId, StringComparison.Ordinal) ||
            !string.Equals(firstIdentity.Path, secondIdentity.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected Dropbox folder identity changed while it was being bound.");
        return secondIdentity;
    }

    public Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default) =>
        VerifyRootPathAsync(cancellationToken);

    /// <summary>
    /// Verifies the bound root and returns the pair root's current root-namespace path from its own verified
    /// metadata (<c>path_display</c>/<c>path_lower</c>). Batch commit entries use this path instead of the folder
    /// id: id-prefixed entry paths make Dropbox's batch commit roughly an order of magnitude slower. The
    /// namespace root itself returns an empty string, keeping its existing leading-only "/relative" entries.
    /// </summary>
    internal async Task<string> VerifyRootPathAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Trace($"verify attempt {attempt}/{RootVerifyAttempts} start ({watch.ElapsedMilliseconds} ms)");
            cancellationToken.ThrowIfCancellationRequested();
            var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var verification = VerifyRootIdentityOnceAsync(attemptCts.Token, watch);
            try
            {
                // A hard wall-clock bound on this attempt: WaitAsync returns even when the verification
                // ignores cancellation, so a half-open account or root check cannot hold the caller open.
                var verifiedPath = await verification.WaitAsync(RootVerifyTimeout, cancellationToken).ConfigureAwait(false);
                Trace($"verify attempt {attempt}/{RootVerifyAttempts} done ({watch.ElapsedMilliseconds} ms)");
                return verifiedPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled: cancellation always wins over this attempt's own bound.
                Trace($"verify attempt {attempt}/{RootVerifyAttempts} cancelled ({watch.ElapsedMilliseconds} ms)");
                throw;
            }
            catch (TimeoutException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (verification.IsCompleted)
                {
                    // The verification finished at the bound: honour its exact result instead of the
                    // bound's timeout, so a definite mismatch from a completed attempt still propagates.
                    var verifiedPath = await verification.ConfigureAwait(false);
                    Trace($"verify attempt {attempt}/{RootVerifyAttempts} done ({watch.ElapsedMilliseconds} ms)");
                    return verifiedPath;
                }
                // This attempt's own bound elapsed with the verification still in flight. Cancel its token
                // so a cooperating HTTP read stops; the finally below hands the abandoned task to Abandon.
                Trace($"verify attempt {attempt}/{RootVerifyAttempts} timed out ({watch.ElapsedMilliseconds} ms)");
                attemptCts.Cancel();
            }
            finally
            {
                // Every exit releases the linked source exactly once, whatever exception left the block: a
                // verification already finished is disposed here, while one still running past its bound is
                // handed to Abandon, which observes its fault and disposes the source when it ends. When an
                // ambient verification scope is present a retry may reuse an unfinished account flight
                // instead of issuing a fresh request, so it is this per-attempt bound - not a fresh request
                // each time - that keeps the caller from being held open.
                if (verification.IsCompleted) attemptCts.Dispose();
                else Abandon(verification, attemptCts);
            }
            if (attempt >= RootVerifyAttempts)
                throw new TimeoutException(
                    $"Dropbox account/root identity check did not answer within {RootVerifyAttempts} attempts of " +
                    $"{RootVerifyTimeout.TotalSeconds:N0} seconds each; no listing was authorized.");
            await ReportRetryNoticeAsync(attempt, cancellationToken);
        }
    }

    // The retry notice is best-effort: a slow or wedged progress sink must not stall recovery, so it is
    // bounded and any late fault is observed rather than awaited forever.
    private async Task ReportRetryNoticeAsync(int attempt, CancellationToken cancellationToken)
    {
        var notice = SyncByteProgressStream.ReportProcessAsync("Checking Dropbox account", _root,
            $"Dropbox account check timed out; retrying ({attempt + 1}/{RootVerifyAttempts})");
        try
        {
            await notice.WaitAsync(RootVerifyNoticeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveFault(notice);
        }
    }

    // Observes an abandoned attempt's outcome so its fault is never unobserved, and releases the linked
    // attempt source once that task actually finishes - it may ignore cancellation and never finish.
    private static void Abandon(Task verification, CancellationTokenSource attemptCts) =>
        verification.ContinueWith(static (task, state) =>
        {
            _ = task.Exception;
            ((CancellationTokenSource)state!).Dispose();
        }, attemptCts, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // Observes a finished task's fault without awaiting it, so a fire-and-forget failure is never unobserved.
    private static void ObserveFault(Task task) =>
        task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async Task<string> VerifyRootIdentityOnceAsync(CancellationToken cancellationToken, System.Diagnostics.Stopwatch watch)
    {
        DropboxProviderConfiguration.ValidateConnection(expectedConnection);
        Trace($"verify resolve token start ({watch.ElapsedMilliseconds} ms)");
        var token = await ResolveAccessTokenAsync(cancellationToken);
        Trace($"verify resolve token done ({watch.ElapsedMilliseconds} ms)");
        Trace($"verify account start ({watch.ElapsedMilliseconds} ms)");
        await VerifyResolvedAccountAsync(token, cancellationToken);
        Trace($"verify account done ({watch.ElapsedMilliseconds} ms)");
        if (PilotPath.IsDropboxNamespaceRootIdentity(_rootId))
        {
            var expectedIdentity = PilotPath.DropboxNamespaceRootIdentity(expectedConnection.RootNamespaceId);
            if (!string.Equals(_root, "/", StringComparison.Ordinal) ||
                !string.Equals(_rootId, expectedIdentity, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The Dropbox account root no longer matches the saved namespace identity.");
            // The namespace root's entries keep their existing leading-only "/relative" spelling.
            return string.Empty;
        }
        var rootId = RequiredRootId();
        if (SyncProviderAccountVerificationScope.Current is { } scope)
        {
            // Coalesce a simultaneous check of this exact folder only while its
            // HTTP read is unfinished. No completed folder verdict is reused.
            var binding = JsonSerializer.Serialize(new { kind = "root-read", expectedConnection, rootId, path = _root });
            await scope.VerifyAsync(this, binding, token,
                owner => VerifyRootTokenAsync(rootId, token, owner, watch), cancellationToken, reuseCompleted: false);
            // The coalesced flight read this exact root by id and proved its current metadata path equals
            // the saved displayed path, so the saved path is the verified current path for this batch.
            return _root;
        }
        return await VerifyRootTokenAsync(rootId, token, cancellationToken, watch);
    }

    private async Task<string> VerifyRootTokenAsync(string rootId, string token, CancellationToken cancellationToken, System.Diagnostics.Stopwatch watch)
    {
        Trace($"verify root read start ({watch.ElapsedMilliseconds} ms)");
        var identity = await GetFolderIdentityAsync(rootId, cancellationToken, token);
        Trace($"verify root read done ({watch.ElapsedMilliseconds} ms)");
        if (!string.Equals(identity.FolderId, rootId, StringComparison.Ordinal) ||
            !string.Equals(identity.Path, _root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected Dropbox folder moved, changed identity, or no longer matches the saved pair.");
        return identity.Path;
    }

    // A batch's entry paths were built from this verified root path. One cheap read after the commit detects a
    // persistent move or replacement of the root: any disagreement makes every entry of the batch uncertain,
    // never replayable. An ABA swap (move away and back) during the batch can still misplace a copy under the
    // old path; that is accepted as non-destructive under .
    private async Task RequireBatchRootUnmovedAsync(string verifiedRootPath)
    {
        // The namespace root's entries carry no root-name prefix, so its existing behaviour is unchanged.
        if (verifiedRootPath.Length == 0) return;
        bool unchanged;
        // The post-commit read is hard-bounded exactly like a root verification: a half-open account must not
        // hold the batch open, and an expired bound leaves the applied paths unproven rather than replayable.
        var attempt = new CancellationTokenSource();
        var read = GetFolderIdentityAsync(RequiredRootId(), attempt.Token);
        try
        {
            var identity = await read.WaitAsync(RootVerifyTimeout, CancellationToken.None).ConfigureAwait(false);
            unchanged = string.Equals(identity.FolderId, RequiredRootId(), StringComparison.Ordinal) &&
                string.Equals(identity.Path, verifiedRootPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (TimeoutException)
        {
            attempt.Cancel();
            throw new DropboxEffectUncertainException(
                $"The Dropbox pair root re-read did not answer within {RootVerifyTimeout.TotalSeconds:N0} seconds; " +
                "the batch outcome is unknown.");
        }
        catch (Exception error)
        {
            // A failed post-commit read leaves the applied paths unproven, exactly like a moved root.
            throw new DropboxEffectUncertainException(
                "The Dropbox pair root could not be re-read after the batch commit; the outcome is unknown.", error);
        }
        finally
        {
            if (read.IsCompleted) attempt.Dispose();
            else Abandon(read, attempt);
        }
        if (unchanged) return;
        await ReportPairRootMovedAsync().ConfigureAwait(false);
        throw new DropboxEffectUncertainException(
            "Dropbox pair root moved during upload; verifying. Every entry in this batch is uncertain until reconciled.");
    }

    private async Task ReportPairRootMovedAsync()
    {
        var notice = SyncByteProgressStream.ReportProcessAsync("Uploading to Dropbox", _root,
            "Dropbox pair root moved during upload; verifying", ProcessMessageKind.Notice);
        try { await notice.WaitAsync(RootVerifyNoticeTimeout).ConfigureAwait(false); }
        catch (Exception) { ObserveFault(notice); }
    }

    public async Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(CancellationToken cancellationToken = default) =>
        (await ListEntriesCoreAsync(null, cancellationToken)).Entries;

    public async Task<PilotRemoteEntry> ReadEntryAsync(string relativePath, string providerObjectId,
        CancellationToken cancellationToken = default)
    {
        var path = PilotPath.NormalizeDropboxRelative(relativePath);
        await VerifyRootIdentityAsync(cancellationToken);
        return await ReadExactItemMetadataAsync(path, providerObjectId, cancellationToken);
    }

    // One exact-item metadata read by stable identity. The identity/path disagreement keeps its own
    // exception shape so a caller can classify a changed source without relabelling a root, connection,
    // permission or transport failure as a content change.
    private async Task<PilotRemoteEntry> ReadExactItemMetadataAsync(string path, string providerObjectId,
        CancellationToken cancellationToken)
    {
        if (!providerObjectId.StartsWith("id:", StringComparison.Ordinal) || providerObjectId.Length <= 3)
            throw new InvalidDataException("An exact Dropbox object ID is required for an item check.");
        await SyncByteProgressStream.ReportProcessAsync("Checking remote item", path,
            "Reading current Dropbox metadata for this exact path; no recursive folder scan.");
        using var document = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
        {
            path = providerObjectId,
            include_deleted = false,
            include_media_info = false
        }, cancellationToken);
        var current = RemoteEntryFromMetadata(document.RootElement);
        if (!string.Equals(current.Id, providerObjectId, StringComparison.Ordinal) ||
            !string.Equals(PilotPath.DropboxComparisonKey(current.RelativePath),
                PilotPath.DropboxComparisonKey(path), StringComparison.Ordinal))
            throw new IOException("Dropbox returned metadata for a different selected path.");
        return current;
    }

    public async Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(
        Func<SyncInventoryProgress, Task> onProgress, CancellationToken cancellationToken = default) =>
        (await ListEntriesCoreAsync(onProgress, cancellationToken)).Entries;

    /// <summary>
    /// The complete listing together with the set of relative paths Dropbox reported as deleted during it, so a
    /// first-sync deletion-evidence step can intersect without reading a second whole-tree deleted inventory.
    /// </summary>
    internal Task<(IReadOnlyList<PilotRemoteEntry> Entries, IReadOnlySet<string> DeletedPaths,
        IReadOnlySet<string> DeletedAncestors, string BasisCursor)>
        ListEntriesWithDeletedPathsAsync(Func<SyncInventoryProgress, Task>? onProgress,
            CancellationToken cancellationToken, Func<string, bool>? include = null, V2.ListingCheckpoint? checkpoint = null) =>
        ListEntriesCoreAsync(onProgress, cancellationToken, include, checkpoint);

    public async Task<PilotRemoteEntry?> FindEntryAtPathAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        await VerifyRootIdentityAsync(cancellationToken);
        return await FindEntryInVerifiedRootAsync(relativePath, cancellationToken);
    }

    // Read-only bounded-page API: account/root checked once, exact item identity/path still checked per response.
    internal async Task<IReadOnlyDictionary<string, PilotRemoteEntry?>> ReadMetadataPageAsync(
        IReadOnlyList<string> paths, CancellationToken cancellationToken, Action<string, Exception>? onFailure = null)
    {
        if (paths.Count > 128) throw new ArgumentOutOfRangeException(nameof(paths));
        await using var verification = SyncProviderAccountVerificationScope.Begin(cancellationToken);
        await VerifyRootIdentityAsync(cancellationToken);
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, PilotRemoteEntry?>();
        await Parallel.ForEachAsync(paths.Distinct(), new ParallelOptions
            { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (path, token) =>
            {
                try { result[path] = await FindEntryInVerifiedRootAsync(path, token); }
                catch (Exception error) when (onFailure is not null && error is not OperationCanceledException and not OutOfMemoryException)
                { onFailure(path, error); }
            });
        return result;
    }

    private async Task<PilotRemoteEntry?> FindEntryInVerifiedRootAsync(string relativePath, CancellationToken cancellationToken)
    {
        var path = PilotPath.NormalizeDropboxRelative(relativePath);
        await SyncByteProgressStream.ReportProcessAsync("Checking remote item", path,
            "Reading current Dropbox metadata for this exact path; no recursive folder scan.");
        using var document = await PostJsonAllowingMissingPathAsync(
            "https://api.dropboxapi.com/2/files/get_metadata", new
            {
                path = RequiredRootId() + "/" + path,
                include_deleted = false,
                include_media_info = false
            }, cancellationToken);
        if (document is null) return null;
        var current = RemoteEntryFromMetadata(document.RootElement);
        if (!IsExactDropboxObjectIdentity(current.Id) ||
            !string.Equals(PilotPath.DropboxComparisonKey(current.RelativePath),
                PilotPath.DropboxComparisonKey(path), StringComparison.Ordinal))
            throw new IOException("Dropbox returned metadata for a different selected path.");
        return current;
    }

    // SS-D10: one bounded existence lookup for an exact provider object identity. Existence is decided by
    // Dropbox's own object ID and its file/folder discriminator, never by a relative path: an object that
    // moved elsewhere inside the authorized namespace still exists, so a path disagreement here is not
    // absence. Only Dropbox's own classified absent-path response answers false; authentication,
    // permission, rate, transport and malformed responses stay errors. No content, path or body value is
    // read into output, logged or retained by this call.
    public async Task<bool> ObjectExistsAsync(string providerObjectId,
        CancellationToken cancellationToken = default)
    {
        if (!IsExactDropboxObjectIdentity(providerObjectId))
            throw new InvalidDataException("An exact Dropbox object ID is required for an identity check.");
        await VerifyRootIdentityAsync(cancellationToken);
        await SyncByteProgressStream.ReportProcessAsync("Checking remote item", _root,
            "Reading the current Dropbox identity for this exact object; no recursive folder scan.");
        using var document = await PostJsonAllowingMissingPathAsync(
            "https://api.dropboxapi.com/2/files/get_metadata", new
            {
                path = providerObjectId,
                include_deleted = false,
                include_media_info = false
            }, cancellationToken);
        if (document is null) return false;
        var metadata = document.RootElement;
        var id = metadata.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var tag = metadata.TryGetProperty(".tag", out var tagElement) ? tagElement.GetString() : null;
        if (!string.Equals(id, providerObjectId, StringComparison.Ordinal) ||
            !string.Equals(tag, "file", StringComparison.Ordinal) &&
            !string.Equals(tag, "folder", StringComparison.Ordinal))
            throw new IOException(
                "Dropbox returned metadata that does not prove the requested object identity.");
        return true;
    }

    private async Task<(IReadOnlyList<PilotRemoteEntry> Entries, IReadOnlySet<string> DeletedPaths,
        IReadOnlySet<string> DeletedAncestors, string BasisCursor)>
        ListEntriesCoreAsync(Func<SyncInventoryProgress, Task>? onProgress, CancellationToken cancellationToken,
            Func<string, bool>? include = null, V2.ListingCheckpoint? checkpoint = null)
    {
        Task<JsonDocument> ReadListingPageAsync(string route, object body, CancellationToken token) =>
            checkpoint is null ? PostJsonAsync(route, body, token) :
            checkpoint.PageAsync(route, body, () => PostJsonAsync(route, body, token));

        async Task Report(string detail, int count)
        {
            try
            {
                if (onProgress is not null)
                    await onProgress(new("Reading Dropbox Inventory", _root, count, detail));
            }
            catch (Exception) { /* Inventory display is best-effort, never inventory evidence. */ }
        }
        var listWatch = System.Diagnostics.Stopwatch.StartNew();
        Trace($"list entries start ({listWatch.ElapsedMilliseconds} ms)");
        await Report("Verifying the selected Dropbox folder before listing its contents.", 0);
        var entries = new Dictionary<string, PilotRemoteEntry>(StringComparer.OrdinalIgnoreCase);
        var canonicalPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var pages = 0;
        var received = 0;
        // Every deleted path this walk reports; a path that is also live wins and is dropped when the set
        // is finalized below, so a deleted-then-re-created path never remains in the deleted set.
        var deletedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // One listing for a single path, parsed into fresh local dictionaries through ReadPage. Page
        // counting and the progress total are shared running totals so concurrent listings stay inside
        // the same bound. The same cursor-cycle and has_more termination rules apply to every listing.
        async Task<(Dictionary<string, PilotRemoteEntry> Entries, Dictionary<string, string> CanonicalPaths,
            HashSet<string> Deleted)>
            ListPathAsync(string path, bool recursive)
        {
            var localEntries = new Dictionary<string, PilotRemoteEntry>(StringComparer.OrdinalIgnoreCase);
            var localCanonicalPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var localDeleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localCount = 0;
            var pageNumber = Interlocked.Increment(ref pages);
            if (pageNumber > MaximumContinuationPages)
                throw new InvalidDataException(
                    $"Dropbox exceeded the bounded recursive-listing continuation limit of {MaximumContinuationPages:N0} pages.");
            await Report($"Requesting Dropbox folder listing — page {pageNumber:N0}; waiting for Dropbox's response.",
                Volatile.Read(ref received));
            JsonDocument? page = await ReadListingPageAsync("https://api.dropboxapi.com/2/files/list_folder", new
            {
                path,
                recursive,
                // Ask for deleted entries alongside the live ones, so this one walk yields the deleted-path
                // set the first-sync deletion-evidence step needs and no second whole-tree listing is issued.
                include_deleted = true,
                // Ask for Dropbox's supported upper page size during a complete inventory.
                // The server may return fewer (or slightly more); only has_more ends the scan.
                limit = 2_000,
                // Match the browser and change feed: omitting mounted descendants is not a complete
                // inventory and must never become deletion/absence authority for the selected root.
                include_mounted_folders = true,
                include_non_downloadable_files = true
            }, cancellationToken);
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                while (true)
                {
                    ReadPage(page.RootElement, localEntries, localCanonicalPaths, localDeleted, include);
                    Interlocked.Add(ref received, localEntries.Count - localCount);
                    localCount = localEntries.Count;
                    var combined = Volatile.Read(ref received);
                    await Report(
                        $"Read Dropbox folder listing page {pageNumber:N0} ({combined:N0} entries received).", combined);
                    if (!page.RootElement.GetProperty("has_more").GetBoolean()) break;
                    var cursor = RequireCursor(page.RootElement, "Dropbox cursor is absent.");
                    if (!seenCursors.Add(CursorFingerprint(cursor)))
                        throw new InvalidDataException("Dropbox cycled a listing cursor without completing the recursive inventory.");
                    page.Dispose();
                    pageNumber = Interlocked.Increment(ref pages);
                    if (pageNumber > MaximumContinuationPages)
                        throw new InvalidDataException(
                            $"Dropbox exceeded the bounded recursive-listing continuation limit of {MaximumContinuationPages:N0} pages.");
                    await Report(
                        $"Requesting Dropbox folder listing — page {pageNumber:N0}; waiting for Dropbox's response.", combined);
                    page = await ReadListingPageAsync("https://api.dropboxapi.com/2/files/list_folder/continue", new { cursor }, cancellationToken);
                }
            }
            finally { page?.Dispose(); }
            return (localEntries, localCanonicalPaths, localDeleted);
        }

        // Merge one listing's local dictionaries into the shared inventory in a fixed order, applying the
        // same case/Unicode-normalization collision rule ReadPage enforces. An exact duplicate of a path
        // already present (for example a folder's own entry) is skipped.
        void MergeLocal(Dictionary<string, PilotRemoteEntry> localEntries, Dictionary<string, string> localCanonicalPaths)
        {
            foreach (var pair in localCanonicalPaths.OrderBy(item => item.Value, StringComparer.Ordinal))
            {
                if (canonicalPaths.TryGetValue(pair.Key, out var existingPath) &&
                    !string.Equals(existingPath, pair.Value, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Case- or Unicode-normalization-colliding Dropbox names are not accepted.");
                if (string.Equals(existingPath, pair.Value, StringComparison.Ordinal) && entries.ContainsKey(pair.Value))
                    continue;
                canonicalPaths[pair.Key] = pair.Value;
                entries[pair.Value] = localEntries[pair.Value];
            }
        }

        // Every deleted path one listing reported is a candidate; nothing is removed here, because the live
        // snapshot is authoritative and a path that is also live is dropped when the set is finalized below.
        void MergeDeleted(HashSet<string> localDeleted)
        {
            foreach (var relative in localDeleted) deletedCandidates.Add(relative);
        }

        // A change cursor taken before the walk closes the window the concurrent subtree listings open:
        // a file moved from a not-yet-listed subtree into an already-listed one arrives as a change and
        // is replayed into the merged inventory below.
        var changeCursor = checkpoint is null ? await GetLatestCursorAsync(cancellationToken) :
            await checkpoint.BasisAsync(() => GetLatestCursorAsync(cancellationToken));

        // The root's own top-level listing, non-recursively, straight into the shared inventory.
        var rootListing = await ListPathAsync(RequiredRootId(), recursive: false);
        MergeLocal(rootListing.Entries, rootListing.CanonicalPaths);
        MergeDeleted(rootListing.Deleted);
        var folders = entries.Values
            .Where(item => item.Kind == PilotEntryKind.Directory)
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();

        // Each top-level folder is listed recursively, at most four listings in flight at once; results
        // are merged afterwards in the fixed folder order so the outcome is deterministic.
        using var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(folders.Select(folder => ListFolderAsync(folder)));
        foreach (var result in results) MergeLocal(result.Entries, result.CanonicalPaths);
        foreach (var result in results) MergeDeleted(result.Deleted);

        // Replay the changes that landed during the walk so a move across subtrees cannot be missed.
        JsonDocument? changePage = await ReadListingPageAsync(
            "https://api.dropboxapi.com/2/files/list_folder/continue", new { cursor = changeCursor }, cancellationToken);
        try
        {
            var changeSeenCursors = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                var changePageNumber = Interlocked.Increment(ref pages);
                if (changePageNumber > MaximumContinuationPages)
                    throw new InvalidDataException(
                        $"Dropbox exceeded the bounded recursive-listing continuation limit of {MaximumContinuationPages:N0} pages.");
                ApplyChangePage(changePage.RootElement);
                var nextChangeCursor = RequireCursor(changePage.RootElement, "Dropbox cursor is absent.");
                changeCursor = nextChangeCursor;
                if (!changePage.RootElement.GetProperty("has_more").GetBoolean()) break;
                if (!changeSeenCursors.Add(CursorFingerprint(nextChangeCursor)))
                    throw new InvalidDataException("Dropbox cycled a listing cursor without completing the recursive inventory.");
                changePage.Dispose();
                changePage = await ReadListingPageAsync(
                    "https://api.dropboxapi.com/2/files/list_folder/continue", new { cursor = nextChangeCursor }, cancellationToken);
            }
        }
        finally { changePage?.Dispose(); }

        await Report("Rechecking the Dropbox root identity after the complete folder listing.", entries.Count);
        await VerifyRootIdentityAsync(cancellationToken);
        Trace($"list entries root recheck done ({listWatch.ElapsedMilliseconds} ms); {entries.Count} entries");
        // Exact deleted paths exclude any path that is live again; the ancestors set keeps every tombstone, so a
        // folder deleted then re-created still names its still-deleted children for the evidence probe.
        var deletedExact = deletedCandidates.Where(path => !entries.ContainsKey(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Trace($"list entries returning ({listWatch.ElapsedMilliseconds} ms); {deletedExact.Count} deleted, " +
            $"{deletedCandidates.Count} deleted-or-ancestor");
        return (entries.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            deletedExact, (IReadOnlySet<string>)deletedCandidates, changeCursor);

        async Task<(Dictionary<string, PilotRemoteEntry> Entries, Dictionary<string, string> CanonicalPaths,
            HashSet<string> Deleted)>
            ListFolderAsync(PilotRemoteEntry folder)
        {
            await gate.WaitAsync(cancellationToken);
            try { return await ListPathAsync(folder.Id!, recursive: true); }
            finally { gate.Release(); }
        }

        // Remove one path and every path beneath it, matching keys and values case-insensitively.
        void RemoveSubtree(string relative)
        {
            var prefix = relative + "/";
            foreach (var victim in entries.Keys
                .Where(key => key.Equals(relative, StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                entries.Remove(victim);
                canonicalPaths.Remove(victim.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant());
            }
        }

        // Apply one change page: a deleted item removes its subtree, any other item replaces the exact
        // path (parsed as ReadPage parses) while still rejecting a different case-insensitive collision.
        void ApplyChangePage(JsonElement root)
        {
            foreach (var item in root.GetProperty("entries").EnumerateArray())
            {
                var tag = item.GetProperty(".tag").GetString();
                if (string.Equals(tag, "deleted", StringComparison.Ordinal))
                {
                    var deletedDisplay = item.TryGetProperty("path_display", out var deletedPath)
                        ? deletedPath.GetString()
                        : item.TryGetProperty("path_lower", out var deletedLower) ? deletedLower.GetString() : null;
                    var deletedRelative = ToInventoryRelative(
                        deletedDisplay ?? throw new InvalidDataException("Dropbox path is absent."));
                    if (include is not null && !include(deletedRelative)) continue;
                    // The replayed deletion takes this path and everything the snapshot still holds beneath
                    // it; each removed descendant was deleted with it, so every one is a deletion candidate.
                    // Read here, at the call site, so RemoveSubtree stays a pure snapshot edit - a live file
                    // replacing a folder also calls it, and that replacement is not a deletion.
                    var victimPrefix = deletedRelative + "/";
                    foreach (var victim in entries.Keys.Where(key =>
                        key.Equals(deletedRelative, StringComparison.OrdinalIgnoreCase) ||
                        key.StartsWith(victimPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                        deletedCandidates.Add(victim);
                    RemoveSubtree(deletedRelative);
                    deletedCandidates.Add(deletedRelative);
                    continue;
                }
                if (tag is not ("file" or "folder"))
                    throw new InvalidDataException("The Dropbox folder contains an unsupported item type.");
                var display = item.TryGetProperty("path_display", out var pathDisplay)
                    ? pathDisplay.GetString()
                    : item.GetProperty("path_lower").GetString();
                if (tag == "folder" && _rootId is not null &&
                    string.Equals(item.TryGetProperty("id", out var rootId) ? rootId.GetString() : null,
                        _rootId, StringComparison.Ordinal) &&
                    string.Equals(display, _root, StringComparison.OrdinalIgnoreCase))
                    continue;
                var relative = ToInventoryRelative(display ?? throw new InvalidDataException("Dropbox path is absent."));
                if (include is not null && !include(relative)) continue;
                var key = relative.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();
                if (canonicalPaths.TryGetValue(key, out var existingPath) &&
                    !string.Equals(existingPath, relative, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Case- or Unicode-normalization-colliding Dropbox names are not accepted.");
                if (tag == "file") RemoveSubtree(relative);
                canonicalPaths[key] = relative;
                entries[relative] = RemoteEntryFromMetadata(item, inventoryOnly: true);
            }
        }
    }

    public async Task<string> GetLatestCursorAsync(CancellationToken cancellationToken = default)
    {
        await VerifyRootIdentityAsync(cancellationToken);
        using var document = await PostJsonAsync(
            "https://api.dropboxapi.com/2/files/list_folder/get_latest_cursor", new
            {
                path = RequiredRootId(),
                recursive = true,
                include_deleted = true,
                include_mounted_folders = true,
                include_non_downloadable_files = true
            }, cancellationToken);
        return RequireCursor(document.RootElement, "Dropbox returned no latest cursor.");
    }

    public async Task<SyncRemoteChangePage> ContinueChangesAsync(string cursor,
        CancellationToken cancellationToken = default)
    {
        cursor = RequireOpaqueCursor(cursor);
        using var document = await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder/continue",
            new { cursor }, cancellationToken);
        var root = document.RootElement;
        var nextCursor = RequireCursor(root, "Dropbox returned no continuation cursor.");
        var hasMore = root.GetProperty("has_more").GetBoolean();
        if (hasMore && string.Equals(nextCursor, cursor, StringComparison.Ordinal))
            throw new InvalidDataException("Dropbox repeated a change-feed cursor while more changes were declared.");
        var entries = new List<SyncRemoteChangeEntry>();
        foreach (var item in root.GetProperty("entries").EnumerateArray())
        {
            var tag = item.GetProperty(".tag").GetString() ?? "unknown";
            var path = item.TryGetProperty("path_display", out var display)
                ? display.GetString()
                : item.TryGetProperty("path_lower", out var lower) ? lower.GetString() : null;
            string? relative = null;
            if (path is not null)
            {
                try { relative = ToRelative(path); }
                catch (InvalidDataException) { relative = null; }
            }
            var evidence = JsonSerializer.Serialize(new
            {
                tag,
                relativePath = relative,
                id = item.TryGetProperty("id", out var id) ? id.GetString() : null,
                revision = item.TryGetProperty("rev", out var revision) ? revision.GetString() : null
            });
            entries.Add(new SyncRemoteChangeEntry(relative, tag, evidence));
        }
        await VerifyRootIdentityAsync(cancellationToken);
        return new SyncRemoteChangePage(nextCursor, hasMore, entries);
    }

    public async Task<PilotUploadResult> UploadCreateOnlyAsync(string relativePath, Stream source, string expectedContentHash, CancellationToken cancellationToken = default)
        => await UploadAsync(relativePath, source, expectedContentHash, null, cancellationToken);

    public async Task<PilotRemoteEntry> CreateDirectoryAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = PilotPath.NormalizeDropboxRelative(relativePath);
        await EnsureRemoteParentFoldersAsync(normalized, cancellationToken);
        using var document = await PostEffectJsonAsync("https://api.dropboxapi.com/2/files/create_folder_v2",
            new { path = RequiredRootId() + "/" + normalized, autorename = false }, cancellationToken);
        try
        {
            var metadata = document.RootElement.GetProperty("metadata");
            var display = metadata.TryGetProperty("path_display", out var displayed)
                ? displayed.GetString()
                : metadata.GetProperty("path_lower").GetString();
            var returned = ToRelative(display ?? throw new InvalidDataException("Dropbox folder path is absent."));
            var id = metadata.GetProperty("id").GetString();
            // create_folder_v2 returns untagged FolderMetadata; a tag, when present, must still say folder.
            if (metadata.TryGetProperty(".tag", out var tag) &&
                !string.Equals(tag.GetString(), "folder", StringComparison.Ordinal) ||
                !string.Equals(returned, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(id))
                throw new DropboxEffectUncertainException(
                    "Dropbox accepted the folder create but returned mismatched metadata.");
            return new PilotRemoteEntry(returned, PilotEntryKind.Directory, id, null, 0, null);
        }
        catch (Exception exception) when (IsEffectBodyShapeException(exception))
        {
            throw new DropboxEffectUncertainException(
                "Dropbox accepted the folder create but returned an unexpected success-body shape.", exception);
        }
    }

    public async Task<PilotRemoteEntry> MoveAsync(PilotRemoteEntry expected, string destinationRelativePath,
        CancellationToken cancellationToken = default) =>
        await MoveRevisionAsync(expected, destinationRelativePath, false, cancellationToken);

    internal async Task<PilotRemoteEntry> MoveRevisionAsync(PilotRemoteEntry expected, string destinationRelativePath,
        bool requireRevision, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expected.Id))
            throw new InvalidDataException("The Dropbox move source has no stable identity.");
        var destination = PilotPath.NormalizeDropboxRelative(destinationRelativePath);
        if (string.Equals(destination, expected.RelativePath, StringComparison.Ordinal))
            throw new InvalidDataException("The Dropbox move destination must differ from the source path.");
        await VerifyRootIdentityAsync(cancellationToken);
        await EnsureRemoteParentFoldersAsync(destination, cancellationToken);
        var occupant = await FindEntryAtPathAsync(destination, cancellationToken);
        if (occupant is not null && !string.Equals(occupant.Id, expected.Id, StringComparison.Ordinal))
            throw new DropboxApiException(HttpStatusCode.Conflict, "Dropbox move destination is occupied.") { WriteErrorTag = "conflict" };
        // This exact-identity metadata read is the final provider observation before the single move
        // request, and it is what remains between the last identity/location check and the effect. It
        // requires the retained stable ID, the exact intended source path and the kind while accepting
        // whatever revision, size and content hash Dropbox currently reports, because a stable-ID native
        // move carries the object's latest contents. Dropbox's move accepts one from_path and cannot
        // atomically condition both identity and location, so the narrow interval here is an accepted
        // provider limitation rather than something this client claims to close. No inventory traversal,
        // parent creation or unrelated provider work may be inserted between this read and the move.
        var moveSource = await RequireMoveSourceAsync(expected, cancellationToken);
        if (requireRevision && (moveSource.Revision != expected.Revision || moveSource.ContentHash != expected.ContentHash ||
            moveSource.Size != expected.Size))
            throw new V2.ExpectedTargetMismatchException("The rename source revision changed. Refresh the notice.");
        using var moved = await PostEffectJsonAsync("https://api.dropboxapi.com/2/files/move_v2", new
        {
            from_path = expected.Id,
            to_path = RequiredRootId() + "/" + destination,
            autorename = false,
            allow_ownership_transfer = false
        }, cancellationToken);
        try
        {
            var metadata = moved.RootElement.GetProperty("metadata");
            var result = RemoteEntryFromMetadata(metadata);
            // The returned revision, size and content hash are the moved object's current contents and are
            // accepted as such. Only a different identity, path or kind is an uncertain outcome.
            if (!string.Equals(result.Id, expected.Id, StringComparison.Ordinal) ||
                !string.Equals(result.RelativePath, destination, StringComparison.Ordinal) ||
                result.Kind != expected.Kind)
                throw new DropboxEffectUncertainException(
                    "Dropbox accepted the move but returned a different object identity or version.");
            return result;
        }
        catch (Exception exception) when (IsEffectBodyShapeException(exception))
        {
            throw new DropboxEffectUncertainException(
                "Dropbox accepted the move but returned an unexpected success-body shape.", exception);
        }
    }

    // One exact-identity, exact-path, exact-kind source check shared by both native move paths. A source
    // that changed identity, path or kind is refused before any request is sent, so no effect is issued for
    // an object that is no longer the retained one. The provider's current content is deliberately not part
    // of this comparison, and a root, connection or permission failure keeps its own classification.
    private async Task<PilotRemoteEntry> RequireMoveSourceAsync(PilotRemoteEntry expected,
        CancellationToken cancellationToken)
    {
        var expectedId = expected.Id;
        if (string.IsNullOrWhiteSpace(expectedId))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox move source changed after inventory.") { WriteErrorTag = "conflict" };
        PilotRemoteEntry current;
        try
        {
            current = await ReadExactItemMetadataAsync(expected.RelativePath, expectedId, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox move source changed after inventory.") { WriteErrorTag = "conflict" };
        }
        if (current.Kind != expected.Kind)
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox move source changed after inventory.") { WriteErrorTag = "conflict" };
        return current;
    }

    public async Task<PilotRemoteEntry> MoveToAsync(PilotRemoteEntry expected,
        ISyncRemoteStore destinationStore, string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (destinationStore is not DropboxPilotClient destinationClient)
            throw new NotSupportedException("A Dropbox object can only be moved to another verified Dropbox root.");
        var sourceConnection = ExpectedConnection;
        if (!string.Equals(sourceConnection.ConnectionId, destinationClient.ExpectedConnection.ConnectionId,
                StringComparison.Ordinal) ||
            !string.Equals(sourceConnection.AccountId, destinationClient.ExpectedConnection.AccountId,
                StringComparison.Ordinal) ||
            !string.Equals(sourceConnection.RootNamespaceId,
                destinationClient.ExpectedConnection.RootNamespaceId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Cross-root Dropbox moves are restricted to the same verified connection and namespace.");
        if (string.IsNullOrWhiteSpace(expected.Id))
            throw new InvalidDataException("The Dropbox cross-root move source has no stable identity.");
        var destination = PilotPath.NormalizeDropboxRelative(destinationRelativePath);
        await VerifyRootIdentityAsync(cancellationToken);
        await destinationClient.VerifyRootIdentityAsync(cancellationToken);
        await destinationClient.EnsureRemoteParentFoldersAsync(destination, cancellationToken);
        if (await destinationClient.FindEntryAtPathAsync(destination, cancellationToken) is not null)
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox cross-root move destination is occupied.");
        // The same final exact-identity source check as the single-root move: the retained stable ID, the
        // exact source path and the kind are re-proved immediately before the one request, and the provider's
        // current content is accepted as the moved object's latest contents.
        _ = await RequireMoveSourceAsync(expected, cancellationToken);
        using var moved = await PostEffectJsonAsync("https://api.dropboxapi.com/2/files/move_v2", new
        {
            from_path = expected.Id,
            to_path = destinationClient.RequiredRootId() + "/" + destination,
            autorename = false,
            allow_ownership_transfer = false
        }, cancellationToken);
        try
        {
            var result = destinationClient.RemoteEntryFromMetadata(
                moved.RootElement.GetProperty("metadata"));
            if (!string.Equals(result.Id, expected.Id, StringComparison.Ordinal) ||
                !string.Equals(result.RelativePath, destination, StringComparison.OrdinalIgnoreCase) ||
                result.Kind != expected.Kind)
                throw new DropboxEffectUncertainException(
                    "Dropbox accepted the cross-root move but returned a different object identity or version.");
            return result;
        }
        catch (Exception exception) when (IsEffectBodyShapeException(exception))
        {
            throw new DropboxEffectUncertainException(
                "Dropbox accepted the cross-root move but returned an unexpected success-body shape.", exception);
        }
    }

    private DropboxConnectionBinding ExpectedConnection => expectedConnection.Normalize();

    private static long ValidateUploadSessionThreshold(long? value)
    {
        var threshold = value ?? SyncUploadLimits.DropboxSingleRequestBytes;
        if (threshold < 1 || threshold > SyncUploadLimits.DropboxSingleRequestBytes)
            throw new ArgumentOutOfRangeException(nameof(value),
                "The Dropbox upload-session threshold must be positive and no larger than the provider limit.");
        return threshold;
    }

    private static int ValidateUploadSessionChunkSize(int? value)
    {
        var size = value ?? UploadSessionChunkSize;
        if (size < 1 || size > UploadSessionChunkSize)
            throw new ArgumentOutOfRangeException(nameof(value),
                "The Dropbox upload-session chunk size must be positive and no larger than 8 MiB.");
        return size;
    }

    public async Task<PilotUploadResult> UploadAsync(string relativePath, Stream source, string expectedContentHash,
        string? expectedRemoteRevision, CancellationToken cancellationToken = default) =>
        await UploadWithMtimeAsync(relativePath, source, expectedContentHash, expectedRemoteRevision, null, cancellationToken);

    internal async Task<PilotUploadResult> UploadWithMtimeAsync(string relativePath, Stream source,
        string expectedContentHash, string? expectedRemoteRevision, DateTimeOffset? clientModified,
        CancellationToken cancellationToken)
    {
        if (!source.CanSeek) throw new IOException("Upload source must be seekable.");
        var normalizedPath = PilotPath.NormalizeDropboxRelative(relativePath);
        await EnsureRemoteParentFoldersAsync(normalizedPath, cancellationToken);
        source.Position = 0;
        return source.Length <= _singleRequestUploadLimit
            ? await UploadSingleRequestAsync(normalizedPath, source, expectedContentHash,
                expectedRemoteRevision, cancellationToken, clientModified)
            : await UploadSessionAsync(normalizedPath, source, expectedContentHash,
                expectedRemoteRevision, null, static (_, _) => Task.CompletedTask, cancellationToken,
                clientModified: clientModified);
    }

    public async Task<PilotUploadResult> UploadResumableAsync(string relativePath, Stream source,
        string expectedContentHash, string? expectedRemoteRevision,
        SyncUploadSessionCheckpoint? checkpoint,
        Func<SyncUploadSessionCheckpoint, CancellationToken, Task> persistCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistCheckpoint);
        if (!source.CanSeek) throw new IOException("Upload source must be seekable.");
        // An explicit session request must use the real session mechanism. Ordinary
        // UploadAsync retains automatic small-file selection, while a lower admitted
        // target limit may legitimately reserve a session for a smaller source.
        var normalizedPath = PilotPath.NormalizeDropboxRelative(relativePath);
        await EnsureRemoteParentFoldersAsync(normalizedPath, cancellationToken);
        return await UploadSessionAsync(normalizedPath, source, expectedContentHash,
            expectedRemoteRevision, checkpoint, persistCheckpoint, cancellationToken);
    }

    private async Task<PilotUploadResult> UploadSingleRequestAsync(string normalizedPath, Stream source,
        string expectedContentHash, string? expectedRemoteRevision, CancellationToken cancellationToken,
        DateTimeOffset? clientModified = null)
    {
        using var request = await AuthorizedAsync(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload", cancellationToken);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(
            CommitInfo(normalizedPath, expectedRemoteRevision, clientModified: clientModified), JsonOptions));
        request.Content = new UploadPayloadContent(new ThrottledLeaveOpenReadStream(source, _transferLimiter));

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var document = await SendUploadEffectAsync(request,
            "The upload response was interrupted after request transmission.", cancellationToken,
            () => SyncUploadSource.Current.Value?.BeforeUpload());
        return ParseUploadResult(document.RootElement, expectedContentHash, source.Length);
    }

    private async Task<PilotUploadResult> UploadSessionAsync(string normalizedPath, Stream source,
        string expectedContentHash, string? expectedRemoteRevision,
        SyncUploadSessionCheckpoint? checkpoint,
        Func<SyncUploadSessionCheckpoint, CancellationToken, Task> persistCheckpoint,
        CancellationToken cancellationToken, bool exactPath = false, DateTimeOffset? clientModified = null)
    {
        var sessionId = checkpoint?.SessionId;
        var offset = checkpoint?.ConfirmedOffset ?? 0;
        if (checkpoint is not null && (string.IsNullOrWhiteSpace(sessionId) || offset < 0 || offset > source.Length))
            throw new InvalidDataException("The retained Dropbox upload-session checkpoint is outside the source.");
        // The decided shared progress budget is consumed exactly as specified by the google-protocol
        // contract; this provider never re-implements it. A retained checkpoint stays tentative until
        // the provider itself acknowledges an offset for this attempt, so the per-session high-water is
        // bound from that first authoritative response and one valid correction may still rewind.
        var progress = new SyncUploadProgressBudget();
        var sessionBound = false;

        void BindSession(long acknowledgedOffset)
        {
            try
            {
                progress.BeginSession(sessionId!, acknowledgedOffset);
                sessionBound = true;
            }
            catch (IOException exception)
            {
                throw new DropboxEffectUncertainException(
                    "Dropbox upload-session replacements are exhausted; the retained checkpoint must be reconciled before another session is started.",
                    exception);
            }
        }

        void ObserveOffset(long acknowledgedOffset)
        {
            if (!sessionBound)
            {
                BindSession(acknowledgedOffset);
                return;
            }
            try
            {
                progress.Observe(acknowledgedOffset);
            }
            catch (IOException exception)
            {
                throw new DropboxEffectUncertainException(
                    "Dropbox stopped advancing the upload-session offset; the retained checkpoint must be reconciled before this transfer continues.",
                    exception);
            }
        }

        if (sessionId is null)
        {
            (sessionId, offset) = await StartUploadSessionAsync(source, persistCheckpoint,
                cancellationToken);
            BindSession(offset);
        }
        else
            source.Position = offset;

        while (true)
        {
            while (source.Length - offset > _uploadSessionChunkSize)
            {
                source.Position = offset;
                var count = (int)Math.Min(_uploadSessionChunkSize, source.Length - offset);
                if (count <= 0)
                    throw new EndOfStreamException("The upload source ended before its retained length.");
                using var appendRequest = await AuthorizedAsync(HttpMethod.Post,
                    "https://content.dropboxapi.com/2/files/upload_session/append_v2", cancellationToken);
                appendRequest.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new
                {
                    cursor = new { session_id = sessionId, offset },
                    close = false
                }, JsonOptions));
                appendRequest.Content = SessionContent(source, count);
                try
                {
                    await SendUploadSessionAppendAsync(appendRequest, cancellationToken);
                    offset += count;
                    await persistCheckpoint(new SyncUploadSessionCheckpoint(sessionId, offset),
                        cancellationToken);
                    ObserveOffset(offset);
                }
                catch (DropboxUploadSessionOffsetException exception)
                {
                    offset = RequireCorrectOffset(exception.CorrectOffset, source.Length);
                    source.Position = offset;
                    await persistCheckpoint(new SyncUploadSessionCheckpoint(sessionId, offset),
                        cancellationToken);
                    ObserveOffset(offset);
                }
                catch (DropboxUploadSessionUnavailableException)
                {
                    (sessionId, offset) = await StartUploadSessionAsync(source,
                        persistCheckpoint, cancellationToken);
                    BindSession(offset);
                }
            }

            source.Position = offset;
            var finalCount = (int)(source.Length - offset);
            using var finishRequest = await AuthorizedAsync(HttpMethod.Post,
                "https://content.dropboxapi.com/2/files/upload_session/finish", cancellationToken);
            finishRequest.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new
            {
                cursor = new { session_id = sessionId, offset },
                commit = CommitInfo(normalizedPath, expectedRemoteRevision, exactPath, clientModified: clientModified)
            }, JsonOptions));
            finishRequest.Content = SessionContent(source, finalCount);
            try
            {
                using var finished = await SendUploadSessionFinishAsync(finishRequest, cancellationToken);
                if (source.Position != source.Length)
                    throw new IOException("The upload-session source length changed during transmission.");
                return ParseUploadResult(finished.RootElement, expectedContentHash, source.Length);
            }
            catch (DropboxUploadSessionOffsetException exception)
            {
                offset = RequireCorrectOffset(exception.CorrectOffset, source.Length);
                source.Position = offset;
                await persistCheckpoint(new SyncUploadSessionCheckpoint(sessionId, offset),
                    cancellationToken);
                ObserveOffset(offset);
            }
            catch (DropboxUploadSessionUnavailableException exception)
            {
                throw new DropboxEffectUncertainException(
                    "The retained Dropbox upload session disappeared during final commit; the destination must be reconciled before a new session is started.",
                    exception);
            }
        }
    }

    private async Task<(string SessionId, long Offset)> StartUploadSessionAsync(Stream source,
        Func<SyncUploadSessionCheckpoint, CancellationToken, Task> persistCheckpoint,
        CancellationToken cancellationToken)
    {
        source.Position = 0;
        var firstCount = (int)Math.Min(_uploadSessionChunkSize, source.Length);
        if (firstCount <= 0)
            throw new EndOfStreamException("The large-file upload source ended before its retained length.");
        using var startRequest = await AuthorizedAsync(HttpMethod.Post,
            "https://content.dropboxapi.com/2/files/upload_session/start", cancellationToken);
        startRequest.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { close = false }, JsonOptions));
        startRequest.Content = SessionContent(source, firstCount);
        using var started = await SendUploadEffectAsync(startRequest,
            "The upload-session start response was interrupted after request transmission.", cancellationToken,
            () => SyncUploadSource.Current.Value?.BeforeUpload());
        string sessionId;
        try
        {
            sessionId = started.RootElement.GetProperty("session_id").GetString()
                        ?? throw new InvalidDataException("Dropbox omitted the upload session identity.");
        }
        catch (Exception exception) when (IsEffectBodyShapeException(exception))
        {
            throw new DropboxEffectUncertainException(
                "Dropbox accepted the upload-session start but returned an unexpected success-body shape.",
                exception);
        }
        await persistCheckpoint(new SyncUploadSessionCheckpoint(sessionId, firstCount), cancellationToken);
        return (sessionId, firstCount);
    }

    private async Task SendUploadSessionAppendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        Interlocked.Increment(ref _providerCallCount);
        try
        {
            response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new DropboxEffectUncertainException(
                "The upload-session append response was interrupted after request transmission.", exception);
        }
        using (response)
        {
            if (response.IsSuccessStatusCode) return;
            await ThrowUploadSessionFailureAsync(response,
                "Dropbox did not confirm the upload-session append.", cancellationToken);
        }
    }

    private async Task<JsonDocument> SendUploadSessionFinishAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        Interlocked.Increment(ref _providerCallCount);
        try
        {
            response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new DropboxEffectUncertainException(
                "The upload-session finish response was interrupted after request transmission.", exception);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                await ThrowUploadSessionFailureAsync(response,
                    "Dropbox did not confirm the upload-session finish.", cancellationToken);
            return await ReadEffectJsonAsync(response,
                "The upload-session finish success body was unavailable or malformed after request transmission.",
                cancellationToken);
        }
    }

    private static async Task ThrowUploadSessionFailureAsync(HttpResponseMessage response, string message,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                             OperationCanceledException)
        {
            throw new DropboxEffectUncertainException(message + " Its response body was unavailable.",
                exception);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            try
            {
                using var error = JsonDocument.Parse(bytes);
                var root = error.RootElement;
                if (root.TryGetProperty("error", out var detail) && WriteErrorTag(detail) == "disallowed_name")
                    throw new DropboxApiException(response.StatusCode, "Dropbox refused the upload destination name.")
                    { WriteErrorTag = "disallowed_name" };
                var summary = root.TryGetProperty("error_summary", out var summaryElement)
                    ? summaryElement.GetString() ?? string.Empty
                    : string.Empty;
                if (TryReadCorrectOffset(root, out var correctOffset))
                    throw new DropboxUploadSessionOffsetException(correctOffset);
                if (summary.Contains("not_found", StringComparison.OrdinalIgnoreCase) ||
                    summary.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
                    summary.Contains("lookup_failed", StringComparison.OrdinalIgnoreCase))
                    throw new DropboxUploadSessionUnavailableException();
            }
            catch (DropboxUploadSessionOffsetException)
            {
                throw;
            }
            catch (DropboxUploadSessionUnavailableException)
            {
                throw;
            }
            catch (JsonException)
            {
                // The failure remains effect-unknown below.
            }
        }
        throw new DropboxEffectUncertainException(
            $"{message} HTTP {(int)response.StatusCode} did not prove whether the session effect was accepted.");
    }

    private static bool TryReadCorrectOffset(JsonElement root, out long correctOffset)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.TryGetProperty("correct_offset", out var direct) && direct.TryGetInt64(out correctOffset))
                return true;
            foreach (var property in error.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object &&
                    property.Value.TryGetProperty("correct_offset", out var nested) &&
                    nested.TryGetInt64(out correctOffset))
                    return true;
            }
        }
        correctOffset = 0;
        return false;
    }

    private static long RequireCorrectOffset(long offset, long sourceLength)
    {
        if (offset < 0 || offset > sourceLength)
            throw new DropboxEffectUncertainException(
                "Dropbox returned an upload-session offset outside the retained source length.");
        return offset;
    }

    private async Task<JsonDocument> SendUploadEffectAsync(HttpRequestMessage request, string interruptionMessage,
        CancellationToken cancellationToken, Action? onSending = null)
    {
        HttpResponseMessage response;
        Interlocked.Increment(ref _providerCallCount);
        try
        {
            response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken, onSending);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new DropboxEffectUncertainException(interruptionMessage, exception);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if (await ReadWriteErrorTagAsync(response, cancellationToken) == "disallowed_name")
                    throw new DropboxApiException(response.StatusCode, "Dropbox refused the upload destination name.")
                    { WriteErrorTag = "disallowed_name" };
                throw new DropboxEffectUncertainException(
                    $"Dropbox returned HTTP {(int)response.StatusCode} after upload transmission and did not prove whether the effect was accepted.");
            }
            return await ReadEffectJsonAsync(response,
                "The Dropbox upload success body was unavailable or malformed after request transmission.",
                cancellationToken);
        }
    }

    private static PilotUploadResult ParseUploadResult(JsonElement root, string expectedContentHash,
        long expectedSize, string? verifiedRootPath = null, string? expectedRelativePath = null)
    {
        try
        {
            var returnedHash = root.TryGetProperty("content_hash", out var hash) ? hash.GetString() : null;
            var size = root.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : -1;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var revision = root.TryGetProperty("rev", out var revElement) ? revElement.GetString() : null;
            if (!string.Equals(returnedHash, expectedContentHash, StringComparison.OrdinalIgnoreCase) ||
                size != expectedSize || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(revision))
                throw new DropboxEffectUncertainException(
                    "Dropbox accepted the upload but returned incomplete or mismatched metadata.");
            // A batch entry's returned path must sit at the exact path this commit named under the verified root.
            // A disagreement is never a plain failure: the effect may already be applied elsewhere.
            if (verifiedRootPath is { Length: > 0 } rootPath && expectedRelativePath is not null &&
                !IsBatchResultAtExpectedPath(root, rootPath, expectedRelativePath))
                throw new DropboxEffectUncertainException(
                    "Dropbox accepted the upload but returned a result path outside the selected pair root.");
            return new PilotUploadResult(id, revision, size, returnedHash!)
            {
                ClientModifiedUtc = root.TryGetProperty("client_modified", out var modified) &&
                    modified.TryGetDateTimeOffset(out var timestamp) ? timestamp.ToUniversalTime() : null
            };
        }
        catch (Exception exception) when (IsEffectBodyShapeException(exception))
        {
            throw new DropboxEffectUncertainException(
                "Dropbox accepted the upload but returned an unexpected success-body shape.", exception);
        }
    }

    private static bool IsBatchResultAtExpectedPath(JsonElement metadata, string rootPath, string relativePath)
    {
        var returned = metadata.TryGetProperty("path_display", out var display) ? display.GetString()
            : metadata.TryGetProperty("path_lower", out var lower) ? lower.GetString() : null;
        if (string.IsNullOrWhiteSpace(returned)) return false;
        var expected = rootPath + "/" + PilotPath.NormalizeDropboxRelative(relativePath);
        return string.Equals(PilotPath.NormalizeDropboxRoot(returned), expected, StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, object?> CommitInfo(string normalizedPath, string? expectedRemoteRevision,
        bool exactPath = false, string? verifiedRootPath = null, DateTimeOffset? clientModified = null)
    {
        object mode = string.IsNullOrWhiteSpace(expectedRemoteRevision)
            ? "add"
            : new Dictionary<string, string> { [".tag"] = "update", ["update"] = expectedRemoteRevision };
        // A verified root path comes from the bound root's own current metadata, so a rename or move follows it;
        // an empty string is the namespace root, whose existing "/relative" spelling is unchanged.
        var rootPath = verifiedRootPath ?? RequiredRootId();
        var commit = new Dictionary<string, object?>
        {
            ["path"] = exactPath
                ? PilotPath.NormalizeDropboxRoot(normalizedPath)
                : rootPath + "/" + PilotPath.NormalizeDropboxRelative(normalizedPath),
            ["mode"] = mode,
            ["autorename"] = false,
            ["mute"] = false,
            ["strict_conflict"] = true
        };
        if (clientModified is { } modified)
            commit["client_modified"] = modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture);
        return commit;
    }

    public async Task<SyncDownloadResult> DownloadAsync(PilotRemoteEntry expected, Stream destination,
        CancellationToken cancellationToken = default) =>
        await DownloadRevisionAsync(expected, destination, requireCurrentMembership: true, cancellationToken);

    public Task<SyncDownloadResult> DownloadRecoveryAsync(SyncRemoteRecoveryRootBinding binding,
        SyncArchiveResult expected, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var prefix = PilotPath.NormalizeDropboxRoot(binding.RootPath) + "/";
        if (!expected.RecoveryPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The retained file is outside its exact Recovery root.");
        var name = NormalizeBoundRecoveryName(expected.RecoveryPath[prefix.Length..]);
        return DownloadRevisionAsync(new PilotRemoteEntry(name, PilotEntryKind.File,
            expected.FileId, expected.Revision, expected.Size, expected.ContentHash), destination,
            requireCurrentMembership: false, cancellationToken, binding);
    }

    internal async Task<SyncDownloadResult> DownloadHistoricalRevisionAsync(PilotRemoteEntry expected,
        Stream destination, CancellationToken cancellationToken = default)
    {
        await VerifyCurrentMembershipOrDeletedAsync(expected, cancellationToken);
        var result = await DownloadRevisionAsync(expected, destination, requireCurrentMembership: false,
            cancellationToken);
        await VerifyCurrentMembershipOrDeletedAsync(expected, cancellationToken);
        return result;
    }

    private async Task<SyncDownloadResult> DownloadRevisionAsync(PilotRemoteEntry expected, Stream destination,
        bool requireCurrentMembership, CancellationToken cancellationToken,
        SyncRemoteRecoveryRootBinding? recoveryBinding = null)
    {
        if (!destination.CanWrite || !destination.CanSeek) throw new IOException("Download destination must be writable and seekable.");
        if (expected.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(expected.Id) ||
            string.IsNullOrWhiteSpace(expected.Revision) || string.IsNullOrWhiteSpace(expected.ContentHash))
            throw new InvalidDataException("The expected Dropbox file identity is incomplete.");
        if (recoveryBinding is null) await VerifyRootIdentityAsync(cancellationToken);
        else await VerifyArchiveAsync();
        // The request names the exact immutable revision ("rev:<rev>"), so Dropbox can return only those bytes.
        // The response's own FileMetadata therefore proves the source identity and membership in this one call;
        // no separate metadata read is needed before or after it.
        using var response = await OpenRevisionDownloadResponseAsync(expected, cancellationToken);
        await EnsureDownloadSuccessAsync(response, cancellationToken);
        if (!response.Headers.TryGetValues("Dropbox-API-Result", out var metadataValues))
            throw new IOException("Dropbox download metadata is absent.");
        using var metadata = JsonDocument.Parse(metadataValues.Single());
        var root = metadata.RootElement;
        var returned = RemoteDownloadEntryFromMetadata(root, expected.RelativePath, recoveryBinding?.RootPath);
        if (!SameRemoteIdentity(returned, expected))
            throw new IOException("Dropbox download metadata or selected-root membership changed after inventory.");
        destination.Position = 0;
        destination.SetLength(0);
        await using (var input = V2.RunOperationStream.Track(await response.Content.ReadAsStreamAsync(cancellationToken), response))
        {
            var buffer = new byte[128 * 1024];
            using var idle = new ReadIdleBound(cancellationToken, ReadStallTimeout);
            while (true)
            {
                var read = await idle.ReadAsync(input, buffer);
                if (read == 0) break;
                if (destination.Length > returned.Size - read)
                    throw new IOException("Dropbox download exceeded its exact reviewed length.");
                await _transferLimiter.ThrottleAsync(SyncTransferDirection.Download, read, cancellationToken);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        if (destination.Length != returned.Size) throw new IOException("Dropbox download length differs from inventory.");
        destination.Position = 0;
        var observedHash = await DropboxContentHash.ComputeAsync(destination, cancellationToken);
        if (!string.Equals(observedHash, returned.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Dropbox download bytes differ from the advertised content hash.");
        destination.Position = 0;
        // A source change cannot reach the transferred bytes: the request is revision-pinned and the response
        // metadata was checked above. Only the pair root itself is re-verified after the transfer.
        if (requireCurrentMembership) await VerifyRootIdentityAsync(cancellationToken);
        if (recoveryBinding is not null) await VerifyArchiveAsync();
        return new SyncDownloadResult(returned.Id!, returned.Revision!, returned.Size, observedHash);

        async Task VerifyArchiveAsync()
        {
            var archive = await GetRecoveryAsync(recoveryBinding!, expected.RelativePath, cancellationToken);
            if (archive is null || archive.FileId != expected.Id || archive.Revision != expected.Revision ||
                archive.Size != expected.Size || !string.Equals(archive.ContentHash, expected.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The exact retained Dropbox recovery copy changed or is unavailable.");
        }
    }

    public async Task<SyncRangeDownloadResult> DownloadRangeAsync(PilotRemoteEntry expected, long offset,
        long length, Stream destination, CancellationToken cancellationToken = default)
    {
        if (!destination.CanWrite) throw new IOException("Range download destination must be writable.");
        if (expected.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(expected.Id) ||
            string.IsNullOrWhiteSpace(expected.Revision) || string.IsNullOrWhiteSpace(expected.ContentHash))
            throw new InvalidDataException("The expected Dropbox range identity is incomplete.");
        if (offset < 0 || length <= 0 || offset > expected.Size || length > expected.Size - offset)
            throw new ArgumentOutOfRangeException(nameof(length),
                "The Dropbox range is outside the reviewed object boundary.");

        await VerifyRootIdentityAsync(cancellationToken);
        var currentBefore = await GetFileMetadataAsync(expected.Id, cancellationToken);
        if (!SameRemoteIdentity(currentBefore, expected))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox range source changed or left its selected pair before hydration.");
        using var response = await OpenRevisionDownloadResponseAsync(expected, cancellationToken,
            new RangeHeaderValue(offset, checked(offset + length - 1)));
        await EnsureDownloadSuccessAsync(response, cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new IOException("Dropbox did not honor the exact requested hydration range.");
        var range = response.Content.Headers.ContentRange;
        if (range?.From != offset || range.To != offset + length - 1 || range.Length != expected.Size)
            throw new IOException("Dropbox returned a different hydration content range.");
        if (!response.Headers.TryGetValues("Dropbox-API-Result", out var metadataValues))
            throw new IOException("Dropbox range metadata is absent.");
        using var metadata = JsonDocument.Parse(metadataValues.Single());
        var returned = RemoteDownloadEntryFromMetadata(metadata.RootElement, expected.RelativePath);
        if (!SameRemoteIdentity(returned, expected))
            throw new IOException("Dropbox range metadata or selected-root membership changed after inventory.");

        var remaining = length;
        await using (var input = V2.RunOperationStream.Track(await response.Content.ReadAsStreamAsync(cancellationToken), response))
        {
            var buffer = new byte[128 * 1024];
            using var idle = new ReadIdleBound(cancellationToken, ReadStallTimeout);
            while (remaining > 0)
            {
                var read = await idle.ReadAsync(input, buffer.AsMemory(0,
                    checked((int)Math.Min(buffer.Length, remaining))));
                if (read == 0) throw new EndOfStreamException("Dropbox hydration range ended early.");
                await _transferLimiter.ThrottleAsync(SyncTransferDirection.Download, read, cancellationToken);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
            if (await idle.ReadAsync(input, buffer.AsMemory(0, 1)) != 0)
                throw new IOException("Dropbox hydration range exceeded its advertised length.");
        }
        await VerifyRootIdentityAsync(cancellationToken);
        var currentAfter = await GetFileMetadataAsync(expected.Id, cancellationToken);
        if (!SameRemoteIdentity(currentAfter, expected))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox range source changed or left its selected pair during hydration.");
        return new SyncRangeDownloadResult(returned.Id!, returned.Revision!, returned.Size,
            returned.ContentHash!, offset, length);
    }

    public async Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected, string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        if (expected.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(expected.Id) ||
            string.IsNullOrWhiteSpace(expected.Revision) || string.IsNullOrWhiteSpace(expected.ContentHash))
            throw new InvalidDataException("The Dropbox recovery source identity is incomplete.");
        await VerifyRootIdentityAsync(cancellationToken);
        var current = await GetFileMetadataAsync(expected.Id, cancellationToken);
        if (!SameRemoteIdentity(current, expected))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox content changed or left its selected pair after inventory; its old revision was not copied.");

        var exactRecoveryPath = NormalizeRecoveryPath(recoveryPath);
        if (!exactRecoveryPath.StartsWith(SyncControlNamespace.RemoteContainerPath + "/", StringComparison.Ordinal))
            throw new InvalidDataException("The Dropbox recovery destination is outside the product recovery root.");
        await using var spool = await DownloadToBoundedSpoolAsync(expected, exactRecoveryPath,
            cancellationToken);
        var bytes = spool.Stream;
        var downloaded = VerifiedSpoolDownload(expected, bytes);
        if (!string.Equals(downloaded.FileId, expected.Id, StringComparison.Ordinal) ||
            !string.Equals(downloaded.Revision, expected.Revision, StringComparison.Ordinal))
            throw new IOException("The exact Dropbox predecessor revision was not downloaded.");
        await VerifyRootIdentityAsync(cancellationToken);
        var sourceAfterDownload = await GetFileMetadataAsync(expected.Id, cancellationToken);
        if (!SameRemoteIdentity(sourceAfterDownload, expected))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox recovery source changed or left its selected pair after download.");
        bytes.Position = 0;
        var copied = await UploadExactPathAsync(exactRecoveryPath, bytes, expected.ContentHash!, cancellationToken);
        return new SyncArchiveResult(copied.FileId, copied.Revision, exactRecoveryPath, copied.Size,
            copied.ContentHash);
    }

    public async Task VerifyRecoveryRootAsync(SyncRemoteRecoveryRootBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        SyncDropboxArtifactPaths.RequireRecoveryContainer(binding.RootPath);
        if (!string.Equals(binding.RootNamespaceId, expectedConnection.RootNamespaceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The recovery folder is bound to a different Dropbox root namespace.");
        await VerifyAccountAsync(cancellationToken);
        var current = await GetFolderIdentityAsync(binding.RootFolderId, cancellationToken);
        if (!string.Equals(current.FolderId, binding.RootFolderId, StringComparison.Ordinal) ||
            !string.Equals(PilotPath.NormalizeDropboxRoot(current.Path),
                PilotPath.NormalizeDropboxRoot(binding.RootPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The dedicated Dropbox recovery folder identity or membership changed.");
    }

    public async Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected,
        SyncRemoteRecoveryRootBinding binding, string deterministicName,
        CancellationToken cancellationToken = default)
    {
        if (expected.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(expected.Id) ||
            string.IsNullOrWhiteSpace(expected.Revision) || string.IsNullOrWhiteSpace(expected.ContentHash))
            throw new InvalidDataException("The Dropbox recovery source identity is incomplete.");
        var name = NormalizeBoundRecoveryName(deterministicName);
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        var current = await GetFileMetadataAsync(expected.Id, cancellationToken);
        if (!SameRemoteIdentity(current, expected))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox content changed before exact-revision recovery retention.");
        var recoveryPath = PilotPath.NormalizeDropboxRoot(binding.RootPath) + "/" + name;
        await using var spool = await DownloadToBoundedSpoolAsync(expected, recoveryPath,
            cancellationToken);
        var bytes = spool.Stream;
        var downloaded = VerifiedSpoolDownload(expected, bytes);
        if (!string.Equals(downloaded.FileId, expected.Id, StringComparison.Ordinal) ||
            !string.Equals(downloaded.Revision, expected.Revision, StringComparison.Ordinal))
            throw new IOException("The exact Dropbox predecessor revision was not downloaded for recovery.");
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        var sourceAfterDownload = await GetFileMetadataAsync(expected.Id, cancellationToken);
        if (!SameRemoteIdentity(sourceAfterDownload, expected))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox recovery source changed or left its selected pair after download.");
        bytes.Position = 0;
        var copied = await UploadExactPathAsync(recoveryPath, bytes, expected.ContentHash, cancellationToken);
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        return new SyncArchiveResult(copied.FileId, copied.Revision, recoveryPath, copied.Size,
            copied.ContentHash);
    }

    public async Task<SyncArchiveResult> CopyRevisionToRecoveryAsync(PilotRemoteEntry expected,
        SyncRemoteRecoveryRootBinding binding, string deterministicName,
        CancellationToken cancellationToken = default)
    {
        if (expected.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(expected.Id) ||
            string.IsNullOrWhiteSpace(expected.Revision) || string.IsNullOrWhiteSpace(expected.ContentHash))
            throw new InvalidDataException("The Dropbox historical recovery source identity is incomplete.");
        var name = NormalizeBoundRecoveryName(deterministicName);
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        await VerifyCurrentMembershipOrDeletedAsync(expected, cancellationToken);
        var recoveryPath = PilotPath.NormalizeDropboxRoot(binding.RootPath) + "/" + name;
        await using var spool = await DownloadToBoundedSpoolAsync(expected, recoveryPath,
            cancellationToken, allowDeletedRevision: true);
        var bytes = spool.Stream;
        var downloaded = VerifiedSpoolDownload(expected, bytes);
        if (!string.Equals(downloaded.FileId, expected.Id, StringComparison.Ordinal) ||
            !string.Equals(downloaded.Revision, expected.Revision, StringComparison.Ordinal) ||
            downloaded.Size != expected.Size ||
            !string.Equals(downloaded.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The historical Dropbox predecessor revision did not verify for recovery.");
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        await VerifyCurrentMembershipOrDeletedAsync(expected, cancellationToken);
        bytes.Position = 0;
        var copied = await UploadExactPathAsync(recoveryPath, bytes, expected.ContentHash, cancellationToken);
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        return new SyncArchiveResult(copied.FileId, copied.Revision, recoveryPath, copied.Size,
            copied.ContentHash);
    }

    public async Task<SyncArchiveResult?> GetRecoveryAsync(SyncRemoteRecoveryRootBinding binding,
        string deterministicName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deterministicName);
        var prefix = PilotPath.NormalizeDropboxRoot(binding.RootPath) + "/";
        string exactPath;
        if (deterministicName.StartsWith('/'))
        {
            // Retained identities contain the full path; allocation lookups supply only the name.
            // Validate containment before accepting either current flat or strict legacy paths.
            var retainedPath = PilotPath.NormalizeDropboxRoot(deterministicName);
            if (!retainedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The retained file is outside its exact Recovery root.");
            var name = retainedPath[prefix.Length..];
            exactPath = name.Contains('/')
                ? SyncDropboxArtifactPaths.NormalizeRecoveryOperationPath(retainedPath)
                : prefix + NormalizeBoundRecoveryName(name);
        }
        else exactPath = prefix + NormalizeBoundRecoveryName(deterministicName);
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        JsonDocument document;
        try
        {
            document = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
            {
                path = exactPath,
                include_deleted = false,
                include_media_info = false
            }, cancellationToken);
        }
        catch (DropboxApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty(".tag").GetString(), "file", StringComparison.Ordinal))
                throw new InvalidDataException("The bound recovery object is not a file.");
            var path = root.GetProperty("path_display").GetString() ?? root.GetProperty("path_lower").GetString();
            if (!string.Equals(path, exactPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The bound recovery object moved outside its retained membership.");
            return new SyncArchiveResult(root.GetProperty("id").GetString()!,
                root.GetProperty("rev").GetString()!, exactPath, root.GetProperty("size").GetInt64(),
                root.GetProperty("content_hash").GetString()!);
        }
    }

    public async Task<SyncArchiveResult?> GetRecoveryAsync(string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        var exactRecoveryPath = NormalizeRecoveryPath(recoveryPath);
        JsonDocument document;
        try
        {
            document = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
            {
                path = exactRecoveryPath,
                include_deleted = false,
                include_media_info = false
            }, cancellationToken);
        }
        catch (DropboxApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty(".tag").GetString(), "file", StringComparison.Ordinal))
                throw new InvalidDataException("The prebound Dropbox recovery destination is not a file.");
            var path = root.GetProperty("path_display").GetString() ?? root.GetProperty("path_lower").GetString();
            var id = root.GetProperty("id").GetString();
            var revision = root.GetProperty("rev").GetString();
            var size = root.GetProperty("size").GetInt64();
            var hash = root.GetProperty("content_hash").GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(revision) || string.IsNullOrWhiteSpace(hash) ||
                !string.Equals(path, exactRecoveryPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Dropbox returned incomplete recovery metadata for the exact prebound destination.");
            return new SyncArchiveResult(id, revision, exactRecoveryPath, size, hash);
        }
    }

    public async Task<PilotUploadResult> CopyCreateOnlyAsync(PilotRemoteEntry expected,
        string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        if (expected.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(expected.Id) ||
            string.IsNullOrWhiteSpace(expected.Revision) || string.IsNullOrWhiteSpace(expected.ContentHash))
            throw new InvalidDataException("The visible-copy source identity is incomplete.");
        var destination = PilotPath.NormalizeDropboxRelative(destinationRelativePath);
        if (string.Equals(destination, expected.RelativePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The visible conflict copy requires a distinct destination.");

        await VerifyRootIdentityAsync(cancellationToken);
        var current = await GetFileMetadataAsync(expected.Id, cancellationToken);
        if (!SameRemoteIdentity(current, expected))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox content changed before the exact-revision visible copy.");
        var destinations = (await ListEntriesAsync(cancellationToken)).Where(item =>
            string.Equals(item.RelativePath, destination, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (destinations.Length != 0)
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "The deterministic visible conflict-copy destination already exists.");

        await using var spool = await DownloadToBoundedSpoolAsync(expected, destination,
            cancellationToken);
        var bytes = spool.Stream;
        var downloaded = VerifiedSpoolDownload(expected, bytes);
        if (!string.Equals(downloaded.FileId, expected.Id, StringComparison.Ordinal) ||
            !string.Equals(downloaded.Revision, expected.Revision, StringComparison.Ordinal))
            throw new IOException("The exact Dropbox conflict revision was not downloaded.");
        await VerifyRootIdentityAsync(cancellationToken);
        var sourceAfterDownload = await GetFileMetadataAsync(expected.Id, cancellationToken);
        if (!SameRemoteIdentity(sourceAfterDownload, expected))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "The visible-copy source changed or left its selected pair after download.");
        bytes.Position = 0;
        return await UploadAsync(destination, bytes, expected.ContentHash, null, cancellationToken);
    }

    public async Task DeleteAsync(PilotRemoteEntry expected, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expected.Id) || expected.Kind == PilotEntryKind.File &&
            (string.IsNullOrWhiteSpace(expected.Revision) || string.IsNullOrWhiteSpace(expected.ContentHash)))
            throw new InvalidDataException("The conditional Dropbox deletion source identity is incomplete.");
        if (expected.Kind == PilotEntryKind.Directory)
            throw new NotSupportedException(
                "Dropbox cannot remove a folder atomically only when it is empty. " +
                "The folder was not deleted and no provider request was sent.");
        await VerifyRootIdentityAsync(cancellationToken);
        var current = expected.Kind == PilotEntryKind.File
            ? await GetFileMetadataAsync(expected.Id, cancellationToken)
            : (await ListEntriesAsync(cancellationToken)).SingleOrDefault(item =>
                string.Equals(item.Id, expected.Id, StringComparison.Ordinal));
        if (current is null || !SameEntryIdentity(current, expected))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox content changed after inventory; the conditional deletion was not attempted.");
        await VerifyRootIdentityAsync(cancellationToken);
        var currentAtEffectBoundary = expected.Kind == PilotEntryKind.File
            ? await GetFileMetadataAsync(expected.Id, cancellationToken)
            : (await ListEntriesAsync(cancellationToken)).SingleOrDefault(item =>
                string.Equals(item.Id, expected.Id, StringComparison.Ordinal));
        if (currentAtEffectBoundary is null || !SameEntryIdentity(currentAtEffectBoundary, expected))
            throw new DropboxApiException(HttpStatusCode.Conflict,
                "Dropbox content moved or changed before the conditional deletion effect boundary.");
        var payload = new Dictionary<string, object?>
        {
            ["path"] = RequiredRootId() + "/" + PilotPath.NormalizeDropboxRelative(expected.RelativePath)
        };
        if (expected.Kind == PilotEntryKind.File) payload["parent_rev"] = expected.Revision;
        using var deleted = await PostConditionalEffectJsonAsync(
            "https://api.dropboxapi.com/2/files/delete_v2", payload, cancellationToken);
        try
        {
            var metadata = deleted.RootElement.GetProperty("metadata");
            if (!string.Equals(metadata.GetProperty(".tag").GetString(),
                    expected.Kind == PilotEntryKind.File ? "file" : "folder", StringComparison.Ordinal) ||
                !string.Equals(metadata.GetProperty("id").GetString(), expected.Id, StringComparison.Ordinal))
                throw new DropboxEffectUncertainException("Dropbox accepted conditional deletion but returned mismatched source metadata.");
        }
        catch (Exception exception) when (IsEffectBodyShapeException(exception))
        {
            throw new DropboxEffectUncertainException(
                "Dropbox accepted conditional deletion but returned an unexpected success-body shape.", exception);
        }
    }

    private static string NormalizeRecoveryPath(string recoveryPath)
    {
        return SyncDropboxArtifactPaths.NormalizeRecoveryOperationPath(recoveryPath);
    }

    private static string NormalizeBoundRecoveryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 16 or > 96 ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new InvalidDataException("The deterministic recovery-object name is not accepted.");
        return value;
    }

    private async Task<PilotUploadResult> UploadExactPathAsync(string exactPath, Stream source,
        string expectedContentHash, CancellationToken cancellationToken)
    {
        if (!source.CanSeek) throw new IOException("Upload source must be seekable.");
        var path = PilotPath.NormalizeDropboxRoot(exactPath);
        source.Position = 0;
        if (source.Length > _singleRequestUploadLimit)
            return await UploadSessionAsync(path, source, expectedContentHash, null, null,
                static (_, _) => Task.CompletedTask, cancellationToken, exactPath: true);
        using var request = await AuthorizedAsync(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload",
            cancellationToken);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["path"] = path,
            ["mode"] = "add",
            ["autorename"] = false,
            ["mute"] = false,
            ["strict_conflict"] = true
        }, JsonOptions));
        request.Content = new UploadPayloadContent(new ThrottledLeaveOpenReadStream(source, _transferLimiter));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response;
        try { response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new DropboxEffectUncertainException("The recovery-copy response was interrupted after request transmission.", exception);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new DropboxEffectUncertainException(
                    $"Dropbox returned HTTP {(int)response.StatusCode} after recovery-copy transmission and did not prove whether it was accepted.");
            using var document = await ReadEffectJsonAsync(response,
                "The recovery-copy response body was unavailable or malformed after request transmission.",
                cancellationToken);
            try
            {
                var root = document.RootElement;
                var result = new PilotUploadResult(root.GetProperty("id").GetString()!, root.GetProperty("rev").GetString()!,
                    root.GetProperty("size").GetInt64(), root.GetProperty("content_hash").GetString()!);
                if (result.Size != source.Length || string.IsNullOrWhiteSpace(result.FileId) ||
                    string.IsNullOrWhiteSpace(result.Revision) ||
                    !string.Equals(result.ContentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new DropboxEffectUncertainException("Dropbox returned incomplete recovery-copy metadata.");
                return result;
            }
            catch (Exception exception) when (IsEffectBodyShapeException(exception))
            {
                throw new DropboxEffectUncertainException(
                    "Dropbox accepted the recovery copy but returned an unexpected success-body shape.", exception);
            }
        }
    }

    public async Task DeleteRecoveryAsync(SyncRemoteRecoveryRootBinding binding, SyncArchiveResult expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(expected);
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        var rootPath = PilotPath.NormalizeDropboxRoot(binding.RootPath);
        var expectedPath = PilotPath.NormalizeDropboxRoot(expected.RecoveryPath);
        if (!expectedPath.StartsWith(rootPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected recovery copy is outside this pair's Recovery folder.");

        async Task VerifyExpectedAsync()
        {
            using var document = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
            {
                path = expected.FileId,
                include_deleted = false,
                include_media_info = false
            }, cancellationToken);
            var value = document.RootElement;
            var path = value.TryGetProperty("path_display", out var displayed)
                ? displayed.GetString()
                : value.GetProperty("path_lower").GetString();
            if (!string.Equals(value.GetProperty(".tag").GetString(), "file", StringComparison.Ordinal) ||
                !string.Equals(value.GetProperty("id").GetString(), expected.FileId, StringComparison.Ordinal) ||
                !string.Equals(value.GetProperty("rev").GetString(), expected.Revision, StringComparison.Ordinal) ||
                value.GetProperty("size").GetInt64() != expected.Size ||
                !string.Equals(value.GetProperty("content_hash").GetString(), expected.ContentHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(PilotPath.NormalizeDropboxRoot(path ?? string.Empty), expectedPath,
                    StringComparison.OrdinalIgnoreCase))
                throw new DropboxApiException(HttpStatusCode.Conflict,
                    "The selected Dropbox Recovery copy changed or moved, so it was not removed.");
        }

        await VerifyExpectedAsync();
        await VerifyRecoveryRootAsync(binding, cancellationToken);
        await VerifyExpectedAsync();
        using var deleted = await PostConditionalEffectJsonAsync("https://api.dropboxapi.com/2/files/delete_v2",
            new { path = expected.FileId, parent_rev = expected.Revision }, cancellationToken);
        try
        {
            var metadata = deleted.RootElement.GetProperty("metadata");
            if (!string.Equals(metadata.GetProperty(".tag").GetString(), "file", StringComparison.Ordinal) ||
                !string.Equals(metadata.GetProperty("id").GetString(), expected.FileId, StringComparison.Ordinal))
                throw new DropboxEffectUncertainException(
                    "Dropbox accepted the Recovery deletion but returned mismatched metadata.");
        }
        catch (Exception exception) when (IsEffectBodyShapeException(exception))
        {
            throw new DropboxEffectUncertainException(
                "Dropbox accepted the Recovery deletion but returned an unexpected success body.", exception);
        }
    }

    private async Task<RecoverySpoolLease> DownloadToBoundedSpoolAsync(PilotRemoteEntry expected, string destinationKey,
        CancellationToken cancellationToken, bool allowDeletedRevision = false)
    {
        var root = Path.GetFullPath(RecoverySpoolRootForTest ?? SyncSharkRuntimePaths.ReplacementStagingRoot);
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The recovery spool root cannot be a reparse point.");
        CleanupAbandonedRecoverySpools(root);
        var driveRoot = Path.GetPathRoot(root) ?? throw new IOException("The recovery spool has no drive root.");
        var material = string.Join("\n", expected.Id, expected.Revision,
            expected.Size.ToString(CultureInfo.InvariantCulture), expected.ContentHash, destinationKey);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant()[..32];
        var reservation = await SyncRecoverySpoolAdmission.ReserveAsync(driveRoot, expected.Size, identity,
            new DriveInfo(driveRoot).AvailableFreeSpace, RecoverySpoolSafetyReserve, cancellationToken);
        var path = Path.Combine(root,
            RecoverySpoolPrefix + identity + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            await DownloadRevisionAsync(expected, stream, requireCurrentMembership: !allowDeletedRevision,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            _ = VerifiedSpoolDownload(expected, stream);
            return new RecoverySpoolLease(stream, reservation);
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync();
            await reservation.DisposeAsync();
            throw;
        }
    }

    private sealed class RecoverySpoolLease(FileStream stream,
        SyncRecoverySpoolAdmission.Reservation reservation) : IAsyncDisposable
    {
        private int _disposed;

        internal FileStream Stream { get; } = stream;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await Stream.DisposeAsync();
            }
            finally
            {
                await reservation.DisposeAsync();
            }
        }
    }

    private static SyncDownloadResult VerifiedSpoolDownload(PilotRemoteEntry expected, FileStream stream)
    {
        if (stream.Length != expected.Size)
            throw new IOException("The bounded recovery spool length differs from the reviewed Dropbox source.");
        stream.Position = 0;
        return new SyncDownloadResult(expected.Id!, expected.Revision!, expected.Size, expected.ContentHash!);
    }

    private async Task VerifyCurrentMembershipOrDeletedAsync(PilotRemoteEntry expected,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await GetFileMetadataAsync(expected.Id!, cancellationToken);
            if (!SameRemoteMembership(current, expected))
                throw new InvalidDataException(
                    "The Dropbox historical source moved outside its reviewed pair membership.");
        }
        catch (DropboxApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            // A deleted object has no current membership. The immutable revision response must
            // still prove the exact reviewed in-root path, revision, size and content hash.
        }
    }

    private static void CleanupAbandonedRecoverySpools(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, RecoverySpoolPrefix + "*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
            }
            catch (IOException)
            {
                // An active no-share spool remains owned by its live transfer.
            }
            catch (UnauthorizedAccessException)
            {
                // A protected predecessor is retained for a later bounded cleanup pass.
            }
        }
    }

    private async Task VerifyMarkerBytesAsync(PilotRemoteEntry marker, string markerPath, string installationId,
        string namespaceId, CancellationToken cancellationToken)
    {
        var expectedBytes = SyncControlNamespace.MarkerBytes(installationId, namespaceId);
        if (marker.Size != expectedBytes.LongLength ||
            !string.Equals(marker.ContentHash, DropboxContentHash.Compute(expectedBytes),
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Dropbox recovery ownership record metadata changed.");
        await using var stream = new MemoryStream(expectedBytes.Length);
        await DownloadExactPathRevisionAsync(marker, markerPath, stream, cancellationToken);
        if (!stream.GetBuffer().AsSpan(0, checked((int)stream.Length)).SequenceEqual(expectedBytes))
            throw new IOException("The Dropbox recovery ownership record bytes changed.");
    }

    private async Task DownloadExactPathRevisionAsync(PilotRemoteEntry expected, string exactPath, Stream destination,
        CancellationToken cancellationToken)
    {
        if (!destination.CanWrite || !destination.CanSeek)
            throw new IOException("Download destination must be writable and seekable.");
        if (expected.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(expected.Id) ||
            string.IsNullOrWhiteSpace(expected.Revision) || string.IsNullOrWhiteSpace(expected.ContentHash) ||
            expected.Size < 0)
            throw new InvalidDataException("The expected Dropbox recovery ownership record is incomplete.");
        var normalizedPath = PilotPath.NormalizeDropboxRoot(exactPath);
        using var request = await AuthorizedAsync(HttpMethod.Post,
            "https://content.dropboxapi.com/2/files/download", cancellationToken);
        request.Headers.Add("Dropbox-API-Arg",
            JsonSerializer.Serialize(new { path = "rev:" + expected.Revision }, JsonOptions));
        using var response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken, operation: SyncProviderOperation.Read);
        EnsureSuccess(response);
        if (!response.Headers.TryGetValues("Dropbox-API-Result", out var metadataValues))
            throw new IOException("Dropbox recovery ownership download metadata is absent.");
        using var metadata = JsonDocument.Parse(metadataValues.Single());
        var root = metadata.RootElement;
        if (root.TryGetProperty(".tag", out var tag) &&
            (tag.ValueKind != JsonValueKind.String ||
             !string.Equals(tag.GetString(), "file", StringComparison.Ordinal)))
            throw new InvalidDataException("Dropbox returned a non-file recovery ownership record.");
        var returnedPath = root.TryGetProperty("path_display", out var display) &&
                           display.ValueKind == JsonValueKind.String
            ? display.GetString()
            : root.TryGetProperty("path_lower", out var lower) && lower.ValueKind == JsonValueKind.String
                ? lower.GetString()
                : null;
        var returnedId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
        var returnedRevision = root.TryGetProperty("rev", out var revision) ? revision.GetString() : null;
        var returnedHash = root.TryGetProperty("content_hash", out var hash) ? hash.GetString() : null;
        var returnedSize = root.TryGetProperty("size", out var size) && size.TryGetInt64(out var parsedSize)
            ? parsedSize
            : -1;
        if (string.IsNullOrWhiteSpace(returnedPath) || string.IsNullOrWhiteSpace(returnedId) ||
            string.IsNullOrWhiteSpace(returnedRevision) || string.IsNullOrWhiteSpace(returnedHash) ||
            !string.Equals(PilotPath.NormalizeDropboxRoot(returnedPath), normalizedPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(returnedId, expected.Id, StringComparison.Ordinal) ||
            !string.Equals(returnedRevision, expected.Revision, StringComparison.Ordinal) ||
            returnedSize != expected.Size ||
            !string.Equals(returnedHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                "The Dropbox recovery ownership record changed identity, revision, content, or exact namespace membership during download.");

        destination.Position = 0;
        destination.SetLength(0);
        await using (var input = V2.RunOperationStream.Track(await response.Content.ReadAsStreamAsync(cancellationToken), response))
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (destination.Length > expected.Size - read)
                    throw new IOException("The Dropbox recovery ownership record exceeded its exact expected length.");
                await _transferLimiter.ThrottleAsync(SyncTransferDirection.Download, read, cancellationToken);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        if (destination.Length != expected.Size)
            throw new IOException("The Dropbox recovery ownership record length changed during download.");
        destination.Position = 0;
        var observedHash = await DropboxContentHash.ComputeAsync(destination, cancellationToken);
        if (!string.Equals(observedHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Dropbox recovery ownership record content hash changed during download.");
        destination.Position = 0;
    }

    private async Task<PilotRemoteEntry> GetFileMetadataByPathAsync(string exactPath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = PilotPath.NormalizeDropboxRoot(exactPath);
        using var document = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
        {
            path = normalizedPath,
            include_deleted = false,
            include_media_info = false
        }, cancellationToken);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty(".tag").GetString(), "file", StringComparison.Ordinal))
            throw new InvalidDataException("The expected Dropbox object is not a file.");
        var display = root.TryGetProperty("path_display", out var pathDisplay) &&
                      pathDisplay.ValueKind == JsonValueKind.String
            ? pathDisplay.GetString()
            : root.TryGetProperty("path_lower", out var pathLower) && pathLower.ValueKind == JsonValueKind.String
                ? pathLower.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(display) ||
            !string.Equals(PilotPath.NormalizeDropboxRoot(display), normalizedPath,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Dropbox recovery ownership record moved outside its exact namespace path.");
        var id = root.GetProperty("id").GetString();
        var revision = root.GetProperty("rev").GetString();
        var contentHash = root.GetProperty("content_hash").GetString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(revision) ||
            string.IsNullOrWhiteSpace(contentHash))
            throw new InvalidDataException("Dropbox returned incomplete recovery ownership metadata.");
        return new PilotRemoteEntry(display.Split('/').Last(), PilotEntryKind.File, id, revision,
            root.GetProperty("size").GetInt64(), contentHash);
    }

    private async Task<PilotRemoteEntry> GetFileMetadataAsync(string fileId, CancellationToken cancellationToken)
    {
        using var document = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
        {
            path = fileId,
            include_deleted = false,
            include_media_info = false
        }, cancellationToken);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty(".tag").GetString(), "file", StringComparison.Ordinal))
            throw new InvalidDataException("The expected Dropbox archive source is not a file.");
        return RemoteEntryFromMetadata(root);
    }

    private static bool SameRemoteIdentity(PilotRemoteEntry left, PilotRemoteEntry right) =>
        SameRemoteMembership(left, right) &&
        string.Equals(left.Revision, right.Revision, StringComparison.Ordinal) && left.Size == right.Size &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase);

    private static bool SameRemoteMembership(PilotRemoteEntry left, PilotRemoteEntry right) =>
        left.Kind == right.Kind && string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(PilotPath.DropboxComparisonKey(left.RelativePath),
            PilotPath.DropboxComparisonKey(right.RelativePath), StringComparison.Ordinal);

    private static bool SameEntryIdentity(PilotRemoteEntry left, PilotRemoteEntry right) =>
        SameRemoteMembership(left, right) &&
        (left.Kind == PilotEntryKind.Directory || SameRemoteIdentity(left, right));

    // CC2G: FolderSharingInfo.shared_folder_id is, per the Dropbox API, "the ID of the shared folder mounted
    // at this location", so it is an ADDITIONAL provider-issued object key for this exact folder and is
    // retained beside the native id (which stays the only key a provider API call uses). Its sibling
    // parent_shared_folder_id only says this folder is CONTAINED by a shared folder; it names an ancestor and
    // is never attached as this item's own alias. A file carries FileSharingInfo, which has no
    // shared_folder_id at all. An absent or empty field is simply no alias; a present but malformed one is
    // refused rather than read as if the shared-namespace key did not exist.
    private static IReadOnlyList<SyncCloudObjectKey>? DropboxObjectAliases(JsonElement item)
    {
        if (!item.TryGetProperty("sharing_info", out var sharing) ||
            sharing.ValueKind != JsonValueKind.Object)
            return null;
        if (!sharing.TryGetProperty("shared_folder_id", out var sharedFolderId))
            return null;
        if (sharedFolderId.ValueKind == JsonValueKind.Null) return null;
        if (sharedFolderId.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(
                "Dropbox returned a malformed shared-folder mount identity; the item is refused rather than read without its shared-namespace key.");
        var value = sharedFolderId.GetString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        return new[] { SyncCloudObjectKey.SharedNamespaceMount(SyncProviders.Dropbox, value!) };
    }

    private PilotRemoteEntry RemoteEntryFromMetadata(JsonElement item, bool inventoryOnly = false,
        PilotEntryKind? assumedKind = null)
    {
        // list_folder/get_metadata return the Metadata union, whose ".tag" names the item type. A FolderMetadata
        // struct returned directly (create_folder_v2, and the metadata inside a create_folder_batch success entry)
        // is untagged, so a caller that knows the entry's kind supplies it here instead of guessing.
        string tag;
        if (item.TryGetProperty(".tag", out var tagElement))
            tag = tagElement.GetString() ?? throw new InvalidDataException("Dropbox returned a malformed metadata type.");
        else if (assumedKind is { } assumed)
            tag = assumed == PilotEntryKind.Directory ? "folder" : "file";
        else
            throw new InvalidDataException("Dropbox returned metadata with no type tag.");
        if (tag is not ("file" or "folder"))
            throw new InvalidDataException("Dropbox returned an unsupported metadata type.");
        var display = item.TryGetProperty("path_display", out var pathDisplay)
            ? pathDisplay.GetString()
            : item.TryGetProperty("path_lower", out var pathLower) ? pathLower.GetString() : null;
        var relative = inventoryOnly
            ? ToInventoryRelative(display ?? throw new InvalidDataException("Dropbox path is absent."))
            : ToRelative(display ?? throw new InvalidDataException("Dropbox path is absent."));
        var blocked = inventoryOnly && !PilotPath.IsWindowsTransferPath(V2.DropboxNameMapping.Map(relative))
            ? "This cloud name cannot be represented safely on Windows (invalid character, reserved name, or trailing dot/space). " +
              "Rename it in Dropbox to a Windows-compatible name, then scan again. No copy, overwrite or deletion is allowed for this item."
            : null;
        var requiresExport = tag == "file" && item.TryGetProperty("is_downloadable", out var downloadable) &&
            downloadable.ValueKind == JsonValueKind.False;
        if (requiresExport)
            blocked ??= "Dropbox marks this cloud-native document as not directly downloadable. " +
                "Export it to a standard file in Dropbox, then sync that exported file. " +
                "The original cloud document and any existing local file remain unchanged.";
        // export_info names exactly the formats this provider can produce for this item. The notice offers
        // those formats, never a speculative list; export_as is the default when export_options is absent.
        string? exportAs = null;
        IReadOnlyList<string>? exportOptions = null;
        if (item.TryGetProperty("export_info", out var exportInfo) && exportInfo.ValueKind == JsonValueKind.Object)
        {
            if (exportInfo.TryGetProperty("export_as", out var exportAsElement) &&
                exportAsElement.ValueKind == JsonValueKind.String)
                exportAs = exportAsElement.GetString();
            if (exportInfo.TryGetProperty("export_options", out var exportOptionsElement) &&
                exportOptionsElement.ValueKind == JsonValueKind.Array)
                exportOptions = exportOptionsElement.EnumerateArray()
                    .Where(option => option.ValueKind == JsonValueKind.String)
                    .Select(option => option.GetString())
                    .Where(format => !string.IsNullOrWhiteSpace(format))
                    .Select(format => format!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        // A missing identity is malformed metadata, never an invisible dictionary failure.
        var id = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("Dropbox returned metadata with no object identity.");
        if (tag == "file" &&
            (!item.TryGetProperty("rev", out var rev) || rev.ValueKind != JsonValueKind.String ||
             !item.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number))
            throw new InvalidDataException("Dropbox returned file metadata without its revision or size.");
        return tag == "folder"
            ? new PilotRemoteEntry(relative, PilotEntryKind.Directory, id, null,
                0, null, TransferBlockedReason: blocked, ObjectAliases: DropboxObjectAliases(item))
            : new PilotRemoteEntry(relative, PilotEntryKind.File, id,
                item.GetProperty("rev").GetString(), item.GetProperty("size").GetInt64(),
                item.TryGetProperty("content_hash", out var hash) ? hash.GetString() : null,
                DropboxModifiedUtc(item), TransferBlockedReason: blocked)
            {
                RequiresExport = requiresExport,
                ExportAs = exportAs,
                ExportOptions = exportOptions,
                ClientModifiedUtc = item.TryGetProperty("client_modified", out var client) &&
                    DateTimeOffset.TryParse(client.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var clientUtc)
                    ? clientUtc : null
            };
    }

    private PilotRemoteEntry RemoteDownloadEntryFromMetadata(JsonElement item, string expectedRelativePath,
        string? recoveryRoot = null)
    {
        // files/download returns FileMetadata directly, unlike the Metadata union
        // returned by get_metadata/list_folder. A discriminator is not required.
        if (item.TryGetProperty(".tag", out var tag) &&
            (tag.ValueKind != JsonValueKind.String ||
             !string.Equals(tag.GetString(), "file", StringComparison.Ordinal)))
            throw new InvalidDataException("Dropbox returned an unsupported download metadata type.");
        string? relative = null;
        foreach (var field in new[] { "path_display", "path_lower" })
        {
            if (!item.TryGetProperty(field, out var path) || path.ValueKind == JsonValueKind.Null) continue;
            if (path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
                throw new InvalidDataException("Dropbox returned a malformed download metadata path.");
            string observed;
            if (recoveryRoot is null) observed = ToRelative(path.GetString()!);
            else
            {
                if (!string.Equals(path.GetString(), PilotPath.NormalizeDropboxRoot(recoveryRoot) + "/" +
                        expectedRelativePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded Recovery copy left its exact root.");
                observed = expectedRelativePath;
            }
            if (relative is not null && !string.Equals(PilotPath.DropboxComparisonKey(relative),
                    PilotPath.DropboxComparisonKey(observed), StringComparison.Ordinal))
                throw new InvalidDataException("Dropbox returned conflicting download metadata paths.");
            relative ??= observed;
        }
        relative ??= PilotPath.NormalizeDropboxRelative(expectedRelativePath);
        return new PilotRemoteEntry(relative, PilotEntryKind.File, item.GetProperty("id").GetString(),
            item.GetProperty("rev").GetString(), item.GetProperty("size").GetInt64(),
            item.TryGetProperty("content_hash", out var hash) ? hash.GetString() : null,
            DropboxModifiedUtc(item));
    }

    private static DateTimeOffset? DropboxModifiedUtc(JsonElement item)
    {
        var value = item.TryGetProperty("server_modified", out var server)
            ? server.GetString()
            : item.TryGetProperty("client_modified", out var client) ? client.GetString() : null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private async Task<PilotRemoteRootBinding> GetFolderIdentityAsync(string pathOrId, CancellationToken cancellationToken,
        string? verifiedAccessToken = null)
    {
        var payload = new
        {
            path = pathOrId,
            include_deleted = false,
            include_media_info = false
        };
        using var document = verifiedAccessToken is null
            ? await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", payload, cancellationToken)
            : await PostJsonCoreAsync("https://api.dropboxapi.com/2/files/get_metadata", payload,
                allowClassifiedMissingPath: false, cancellationToken, exactAccessToken: verifiedAccessToken) ??
                throw new InvalidDataException("The exact verified Dropbox folder identity was absent.");
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty(".tag").GetString(), "folder", StringComparison.Ordinal))
            throw new InvalidDataException("The selected Dropbox root is not a folder.");
        var id = root.GetProperty("id").GetString() ?? throw new InvalidDataException("Dropbox folder ID is absent.");
        var path = root.GetProperty("path_display").GetString() ?? root.GetProperty("path_lower").GetString() ??
            throw new InvalidDataException("Dropbox folder path is absent.");
        return new PilotRemoteRootBinding(PilotPath.NormalizeDropboxRoot(path), id, DropboxObjectAliases(root));
    }

    private async Task VerifyAccountAsync(CancellationToken cancellationToken)
    {
        var token = await ResolveAccessTokenAsync(cancellationToken);
        await VerifyResolvedAccountAsync(token, cancellationToken);
    }

    private Task VerifyResolvedAccountAsync(string token, CancellationToken cancellationToken)
    {
        DropboxProviderConfiguration.ValidateConnection(expectedConnection);
        // The adapter object also binds its credential-source instance. Include
        // every authorization fact, not display names or only ConnectionId.
        var identity = JsonSerializer.Serialize(new
        {
            expectedConnection.ClientId, expectedConnection.AccountId,
            expectedConnection.RootNamespaceId, expectedConnection.HomeNamespaceId,
            expectedConnection.ContentAccess, expectedConnection.ConnectionId,
            expectedConnection.CredentialReference,
            scopes = expectedConnection.Scopes.Order(StringComparer.Ordinal).ToArray()
        });
        return SyncProviderAccountVerificationScope.Current is { } scope
            ? scope.VerifyAsync(this, identity, token, owner => VerifyAccountTokenAsync(token, owner), cancellationToken)
            : VerifyAccountTokenAsync(token, cancellationToken);
    }

    private async Task VerifyAccountTokenAsync(string token, CancellationToken cancellationToken)
    {
        using var document = await PostJsonCoreAsync("https://api.dropboxapi.com/2/users/get_current_account", null,
            allowClassifiedMissingPath: false, cancellationToken, exactAccessToken: token) ??
            throw new InvalidDataException("Dropbox account identity was absent.");
        var root = document.RootElement;
        var rootInfo = root.GetProperty("root_info");
        if (!string.Equals(root.GetProperty("account_id").GetString(), expectedConnection.AccountId, StringComparison.Ordinal) ||
            !string.Equals(rootInfo.GetProperty("root_namespace_id").GetString(), expectedConnection.RootNamespaceId, StringComparison.Ordinal) ||
            !string.Equals(rootInfo.GetProperty("home_namespace_id").GetString(), expectedConnection.HomeNamespaceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Dropbox account or namespace differs from the saved connection.");
    }

    // A deleted entry never enters the live snapshot; its relative path joins the deleted-path set the
    // first-sync deletion-evidence step intersects, in the inventory spelling so a live entry at the same path
    // (deleted then re-created) wins the final filter. Returns false for any non-deleted entry. A deleted name
    // that cannot be represented contributes no evidence and is skipped: it must never abort the live
    // inventory, which never contained it before this listing asked Dropbox for deletions.
    private bool TryAddDeletedPath(JsonElement item, ISet<string> deleted)
    {
        if (!string.Equals(item.GetProperty(".tag").GetString(), "deleted", StringComparison.Ordinal)) return false;
        var deletedDisplay = item.TryGetProperty("path_display", out var deletedPath)
            ? deletedPath.GetString()
            : item.TryGetProperty("path_lower", out var deletedLower) ? deletedLower.GetString() : null;
        if (deletedDisplay is not null)
        {
            try { deleted.Add(ToInventoryRelative(deletedDisplay)); }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                Trace("ignoring a deleted Dropbox path that cannot be represented as an inventory path");
            }
        }
        return true;
    }

    private void ReadPage(JsonElement root, IDictionary<string, PilotRemoteEntry> entries,
        IDictionary<string, string> canonicalPaths, ISet<string> deleted, Func<string, bool>? include = null)
    {
        foreach (var item in root.GetProperty("entries").EnumerateArray())
        {
            if (include is not null)
            {
                var path = item.TryGetProperty("path_display", out var displayPath)
                    ? displayPath.GetString() : item.GetProperty("path_lower").GetString();
                if (!string.Equals(path, _root, StringComparison.OrdinalIgnoreCase) &&
                    !include(ToInventoryRelative(path ?? throw new InvalidDataException("Dropbox path is absent."))))
                    continue;
            }
            if (TryAddDeletedPath(item, deleted)) continue;
            var tag = item.GetProperty(".tag").GetString();
            if (tag is not ("file" or "folder"))
                throw new InvalidDataException("The Dropbox folder contains an unsupported item type.");
            var display = item.TryGetProperty("path_display", out var pathDisplay) ? pathDisplay.GetString() : item.GetProperty("path_lower").GetString();
            if (tag == "folder" && _rootId is not null &&
                string.Equals(item.TryGetProperty("id", out var rootId) ? rootId.GetString() : null,
                    _rootId, StringComparison.Ordinal) &&
                string.Equals(display, _root, StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = ToInventoryRelative(display ?? throw new InvalidDataException("Dropbox path is absent."));
            var key = relative.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();
            if (canonicalPaths.TryGetValue(key, out var existingPath) &&
                !string.Equals(existingPath, relative, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Case- or Unicode-normalization-colliding Dropbox names are not accepted.");
            canonicalPaths[key] = relative;
            entries[relative] = RemoteEntryFromMetadata(item, inventoryOnly: true);
        }
    }

    private async Task EnsureRemoteParentFoldersAsync(string relativePath,
        CancellationToken cancellationToken)
    {
        var parts = PilotPath.NormalizeDropboxRelative(relativePath).Split('/');
        if (parts.Length < 2) return;
        // Only the nearest ancestor is probed. The structure phase creates every folder in the same plan before its
        // children, so a parent this plan creates is already present; any other parent comes from the inventory.
        // One read replaces the previous one get_metadata per level, which a large upload or move paid per file.
        if (await NearestParentFolderExistsAsync(string.Join('/', parts, 0, parts.Length - 1), cancellationToken))
            return;
        var parent = RequiredRootId();
        for (var index = 0; index < parts.Length - 1; index++)
        {
            parent += "/" + parts[index];
            try
            {
                using var metadata = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
                {
                    path = parent,
                    include_deleted = false,
                    include_media_info = false
                }, cancellationToken);
                if (!string.Equals(metadata.RootElement.GetProperty(".tag").GetString(), "folder",
                        StringComparison.Ordinal))
                    throw new IOException("A Dropbox parent path is occupied by a file.");
                continue;
            }
            catch (DropboxApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                // Absent parent; create it below the already verified pair root.
            }

            var parsedCreateAcknowledgement = false;
            try
            {
                using var created = await PostEffectJsonAsync(
                    "https://api.dropboxapi.com/2/files/create_folder_v2",
                    new { path = parent, autorename = false }, cancellationToken);
                parsedCreateAcknowledgement = true;
                if (!created.RootElement.TryGetProperty("metadata", out var acknowledged) ||
                    acknowledged.ValueKind != JsonValueKind.Object ||
                    !acknowledged.TryGetProperty("id", out var acknowledgedIdValue) ||
                    string.IsNullOrWhiteSpace(acknowledgedIdValue.GetString()) ||
                    !acknowledged.TryGetProperty("name", out var acknowledgedNameValue) ||
                    !string.Equals(acknowledgedNameValue.GetString(), parts[index], StringComparison.Ordinal) ||
                    acknowledged.TryGetProperty(".tag", out var acknowledgedTag) &&
                    !string.Equals(acknowledgedTag.GetString(), "folder", StringComparison.Ordinal))
                    throw new DropboxEffectUncertainException(
                        "Dropbox returned an unexpected folder-create success body.");
                var acknowledgedId = acknowledgedIdValue.GetString()!;
                using var readback = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
                {
                    path = acknowledgedId,
                    include_deleted = false,
                    include_media_info = false
                }, cancellationToken);
                if (!readback.RootElement.TryGetProperty(".tag", out var readbackTag) ||
                    !string.Equals(readbackTag.GetString(), "folder", StringComparison.Ordinal) ||
                    !readback.RootElement.TryGetProperty("id", out var readbackId) ||
                    !string.Equals(readbackId.GetString(), acknowledgedId, StringComparison.Ordinal) ||
                    !readback.RootElement.TryGetProperty("name", out var readbackName) ||
                    !string.Equals(readbackName.GetString(), parts[index], StringComparison.Ordinal))
                    throw new DropboxEffectUncertainException(
                        "Dropbox folder-create readback differs from its acknowledgement.");
            }
            catch (DropboxEffectUncertainException) when (!parsedCreateAcknowledgement)
            {
                using var verified = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
                {
                    path = parent,
                    include_deleted = false,
                    include_media_info = false
                }, cancellationToken);
                if (!verified.RootElement.TryGetProperty(".tag", out var verifiedTag) ||
                    !string.Equals(verifiedTag.GetString(), "folder", StringComparison.Ordinal) ||
                    !verified.RootElement.TryGetProperty("id", out var verifiedId) ||
                    string.IsNullOrWhiteSpace(verifiedId.GetString()) ||
                    !verified.RootElement.TryGetProperty("name", out var verifiedName) ||
                    !string.Equals(verifiedName.GetString(), parts[index], StringComparison.Ordinal))
                    throw;
            }
        }
    }

    // The nearest-ancestor probe for parent creation: a folder present is the only outcome that lets the caller
    // skip the level walk. A file in the way is refused exactly as before; an absent path falls through to creation.
    private async Task<bool> NearestParentFolderExistsAsync(string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var metadata = await PostJsonAsync("https://api.dropboxapi.com/2/files/get_metadata", new
            {
                path = RequiredRootId() + "/" + relativePath,
                include_deleted = false,
                include_media_info = false
            }, cancellationToken);
            if (!string.Equals(metadata.RootElement.GetProperty(".tag").GetString(), "folder",
                    StringComparison.Ordinal))
                throw new IOException("A Dropbox parent path is occupied by a file.");
            return true;
        }
        catch (DropboxApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    // Only the immutable-revision READ request is repeated, before handing a
    // response/body to the caller. Destination bytes, file effects and uploads
    // are never replayed here; all existing identity/hash/range guards remain.
    private async Task<HttpResponseMessage> OpenRevisionDownloadResponseAsync(PilotRemoteEntry expected,
        CancellationToken token, RangeHeaderValue? range = null)
    {
        return await ReadWithRetryAsync(async ct =>
        {
            var request = await AuthorizedAsync(HttpMethod.Post,
                "https://content.dropboxapi.com/2/files/download", ct);
            request.Headers.Add("Dropbox-API-Arg",
                JsonSerializer.Serialize(new { path = "rev:" + expected.Revision }, JsonOptions));
            request.Headers.Range = range;
            return request;
        }, (response, _) => Task.FromResult(response), expected.RelativePath, token, handOffResponse: true);
    }

    private async Task<JsonDocument> PostJsonAsync(string uri, object? payload, CancellationToken cancellationToken) =>
        await PostJsonCoreAsync(uri, payload, allowClassifiedMissingPath: false, cancellationToken) ??
        throw new InvalidDataException(
            "Dropbox reported an absent path for a read that requires the exact item to be present.");

    // files/get_metadata is the only read here whose own response can prove that the exact
    // bound-root path is absent. Nothing else - not another endpoint family's "not_found",
    // not a permission, authentication, unavailability, transport-loss or malformed
    // response - may be read as absence, so that single classification stays inside the
    // provider adapter and is never inferred from a status code alone.
    private async Task<JsonDocument?> PostJsonAllowingMissingPathAsync(string uri, object? payload,
        CancellationToken cancellationToken) =>
        await PostJsonCoreAsync(uri, payload, allowClassifiedMissingPath: true, cancellationToken);

    private async Task<JsonDocument?> PostJsonCoreAsync(string uri, object? payload,
        bool allowClassifiedMissingPath, CancellationToken cancellationToken, string? exactAccessToken = null,
        Func<JsonElement, bool>? allowedConflict = null)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        return await ReadWithRetryAsync<JsonDocument?>(async ct =>
        {
            var request = exactAccessToken is null
                ? await AuthorizedAsync(HttpMethod.Post, uri, ct)
                : AuthorizedWithToken(HttpMethod.Post, uri, exactAccessToken);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }, async (response, ct) =>
        {
            if (allowedConflict is not null && response.StatusCode == HttpStatusCode.Conflict)
            {
                using var error = await ReadBoundedJsonAsync(response, ct);
                if (error.RootElement.TryGetProperty("error", out var reason) && allowedConflict(reason))
                    return null;
            }
            if (allowClassifiedMissingPath && response.StatusCode == HttpStatusCode.Conflict &&
                await ClassifiesAbsentExactPathAsync(response, ct))
                return null;
            EnsureSuccess(response);
            return await ReadBoundedJsonAsync(response, ct);
        }, _root, cancellationToken, operation: new Uri(uri).AbsolutePath);
    }

    private async Task<bool> ClassifiesAbsentExactPathAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            // Preserve streamed byte/depth limits and cancellation even without Content-Length.
            using var error = await ReadBoundedJsonAsync(response, cancellationToken);
            return ClassifiesAbsentExactPath(error.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Only Dropbox's own exact path lookup classification proves absence. The structured
    // tag is authoritative; the human-readable summary is accepted only when it is exactly
    // this path lookup result. A bare "not_found" from another endpoint family, a conflict
    // classification, or any unparsable body is never absence.
    private static bool ClassifiesAbsentExactPath(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("error", out var error))
            return error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty(".tag", out var tag) && tag.ValueKind == JsonValueKind.String &&
                   string.Equals(tag.GetString(), "path", StringComparison.Ordinal) &&
                   error.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Object &&
                   path.TryGetProperty(".tag", out var pathTag) && pathTag.ValueKind == JsonValueKind.String &&
                   string.Equals(pathTag.GetString(), "not_found", StringComparison.Ordinal);
        // Summary text must never override a contradictory structured error.
        var summary = root.TryGetProperty("error_summary", out var summaryElement) &&
                      summaryElement.ValueKind == JsonValueKind.String
            ? summaryElement.GetString()
            : null;
        return summary is not null &&
               (string.Equals(summary, "path/not_found", StringComparison.Ordinal) ||
                summary.StartsWith("path/not_found/", StringComparison.Ordinal));
    }

    // An exact item looked up by path must come back as one well-formed Dropbox object
    // identity; a missing, empty or foreign key is refused rather than treated as the item.
    private static bool IsExactDropboxObjectIdentity(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.StartsWith("id:", StringComparison.Ordinal) && id.Length > 3;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null) ??
            TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt, 5)));
        if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
        return delay;
    }

    private async Task<JsonDocument> PostEffectJsonAsync(string uri, object? payload, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = await AuthorizedAsync(HttpMethod.Post, uri, cancellationToken);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            Interlocked.Increment(ref _providerCallCount);
            if (attempt > 0) V2.RunInstrumentation.Current?.RecordRetry();
            try { response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                throw new DropboxEffectUncertainException("The Dropbox effect acknowledgement was interrupted after request transmission.", exception);
            }
            using (response)
            {
                // Dropbox does not perform a request it answers with 429 (rate limit) or another 4xx;
                // only 5xx and timeouts leave the outcome unknown.
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 5)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                        throw new DropboxEffectUncertainException(
                            $"Dropbox returned HTTP {(int)response.StatusCode} after the effect request was transmitted and did not prove whether it was accepted.");
                    throw new DropboxApiException(response.StatusCode,
                        $"Dropbox rejected the effect with HTTP {(int)response.StatusCode}.")
                    { WriteErrorTag = await ReadWriteErrorTagAsync(response, cancellationToken) };
                }
                return await ReadEffectJsonAsync(response,
                    "The Dropbox effect success body was unavailable or malformed after request transmission.",
                    cancellationToken);
            }
        }
    }

    private async Task<JsonDocument> PostConditionalEffectJsonAsync(string uri, object? payload,
        CancellationToken cancellationToken, Action? onSending = null)
    {
        using var request = await AuthorizedAsync(HttpMethod.Post, uri, cancellationToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8,
            "application/json");
        return await SendConditionalEffectAsync(request, cancellationToken, onSending).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendConditionalEffectAsync(HttpRequestMessage request,
        CancellationToken cancellationToken, Action? onSending = null)
    {
        HttpResponseMessage response;
        Interlocked.Increment(ref _providerCallCount);
        try { response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken, onSending); }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new DropboxEffectUncertainException("The conditional Dropbox effect acknowledgement was interrupted after request transmission.", exception);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.TooManyRequests)
                    throw new DropboxEffectUncertainException(
                        $"Dropbox returned HTTP {(int)response.StatusCode} after the conditional effect request was transmitted and did not prove whether it was accepted.");
                throw new DropboxApiException(response.StatusCode,
                    $"Dropbox rejected the revision-conditional effect with HTTP {(int)response.StatusCode}.")
                { WriteErrorTag = await ReadWriteErrorTagAsync(response, cancellationToken) };
            }
            return await ReadEffectJsonAsync(response,
                "The conditional Dropbox effect success body was unavailable or malformed after request transmission.",
                cancellationToken);
        }
    }

    private async Task<JsonDocument> ReadEffectJsonAsync(HttpResponseMessage response, string message,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadBoundedJsonAsync(response, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or
                                             OperationCanceledException or TimeoutException or JsonException)
        {
            throw new DropboxEffectUncertainException(message, exception);
        }
    }

    private static bool IsEffectBodyShapeException(Exception exception) =>
        exception is KeyNotFoundException or InvalidOperationException or InvalidDataException or
            FormatException or OverflowException;

    private async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string uri, CancellationToken cancellationToken)
    {
        var token = await ResolveAccessTokenAsync(cancellationToken);
        // Token resolution and authorization use exactly the same generation.
        // Rotation cannot slip between an earlier root check and this request.
        if (SyncProviderAccountVerificationScope.Current is not null)
            await VerifyResolvedAccountAsync(token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return AuthorizedWithToken(method, uri, token);
    }

    private async Task<string> ResolveAccessTokenAsync(CancellationToken cancellationToken)
    {
        var token = await tokenSource.GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Dropbox access token is unavailable.");
        return token;
    }

    private HttpRequestMessage AuthorizedWithToken(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (uri.Contains("/2/files/", StringComparison.Ordinal))
        {
            var pathRoot = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [".tag"] = "root",
                ["root"] = expectedConnection.RootNamespaceId
            });
            if (!request.Headers.TryAddWithoutValidation("Dropbox-API-Path-Root", pathRoot))
                throw new InvalidOperationException("Dropbox root-namespace binding could not be applied.");
        }
        return request;
    }

    private string RequiredRootId()
    {
        if (_rootId is null)
            throw new InvalidOperationException("The exact Dropbox folder ID has not been bound.");
        return PilotPath.IsDropboxNamespaceRootIdentity(_rootId) ? string.Empty : _rootId;
    }

    private static string RequireOpaqueCursor(string cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        if (Encoding.UTF8.GetByteCount(cursor) > MaximumCursorBytes)
            throw new InvalidDataException($"Dropbox returned a cursor larger than {MaximumCursorBytes:N0} bytes.");
        return cursor;
    }

    private static string RequireCursor(JsonElement root, string message)
    {
        if (!root.TryGetProperty("cursor", out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException(message);
        return RequireOpaqueCursor(property.GetString()!);
    }

    private static string CursorFingerprint(string cursor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RequireOpaqueCursor(cursor))));

    // The body arrives after the response headers (HttpCompletionOption.ResponseHeadersRead), which
    // HttpClient.Timeout does not cover, so each chunk read is idle-bounded by ReadStallTimeout through the
    // shared ReadIdleBound: no bytes for that long aborts the read as a transient interruption the read
    // retry (ReadWithRetryAsync) can retry. The bound is disarmed between reads, the caller's own
    // cancellation still surfaces as cancellation (never as a stall), and the response is disposed by the
    // caller on every exit.
    private async Task<JsonDocument> ReadBoundedJsonAsync(HttpResponseMessage response,
        CancellationToken cancellationToken, int maximumBytes = MaximumJsonResponseBytes)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Dropbox returned an oversized JSON response.");
        await using var input = V2.RunOperationStream.Track(await response.Content.ReadAsStreamAsync(cancellationToken), response);
        using var bounded = new MemoryStream();
        using var idle = new ReadIdleBound(cancellationToken, ReadStallTimeout);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await idle.ReadAsync(input, buffer);
            if (read == 0) break;
            if (bounded.Length > maximumBytes - read)
                throw new InvalidDataException("Dropbox returned an oversized JSON response.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return JsonDocument.Parse(bounded.GetBuffer().AsMemory(0, checked((int)bounded.Length)),
            new JsonDocumentOptions { MaxDepth = 64 });
    }

    private string ToRelative(string remotePath)
    {
        if (remotePath.Contains('\\'))
            throw new InvalidDataException("Dropbox returned an ambiguous path separator.");
        var path = remotePath;
        if (string.Equals(_root, "/", StringComparison.Ordinal))
            return PilotPath.NormalizeDropboxRelative(path.TrimStart('/'));
        if (!path.StartsWith(_root + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dropbox returned an item outside the selected folder.");
        return PilotPath.NormalizeDropboxRelative(path[(_root.Length + 1)..]);
    }

    private string ToInventoryRelative(string remotePath)
    {
        // Do not rewrite separators or escape illegal Windows characters into fake paths.
        if (remotePath.Contains('\\'))
            throw new InvalidDataException("Dropbox inventory contains an ambiguous path separator.");
        var prefix = _root == "/" ? "/" : _root + "/";
        if (!remotePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dropbox returned an item outside the selected folder.");
        return PilotPath.NormalizeDropboxRelative(remotePath[prefix.Length..]);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new DropboxApiException(response.StatusCode, $"Dropbox API returned HTTP {(int)response.StatusCode} without confirming an accepted effect.");
    }

    private async Task EnsureDownloadSuccessAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var code = "unclassified";
        var detail = "Dropbox refused this download without a recognized structured reason.";
        try
        {
            using var body = await ReadBoundedJsonAsync(response, cancellationToken, 16 * 1024);
            if (body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty(".tag", out var tag) && tag.ValueKind == JsonValueKind.String)
            {
                var classification = tag.GetString();
                if (classification == "path" && error.TryGetProperty("path", out var path) &&
                    path.ValueKind == JsonValueKind.Object && path.TryGetProperty(".tag", out var pathTag) &&
                    pathTag.ValueKind == JsonValueKind.String)
                    classification = "path/" + pathTag.GetString();
                // Only fixed, recognized classifications enter logs. Never echo a provider's
                // arbitrary summary, path, account fields, body or unknown tag as diagnostics.
                (code, detail) = classification switch
                {
                    "unsupported_file" => ("unsupported_file", "This cloud-native document requires export to a standard file; direct download is unavailable."),
                    "path/not_found" => ("path/not_found", "The reviewed file or revision is no longer available. Scan again to read its current state."),
                    "path/not_file" => ("path/not_file", "The selected Dropbox item is not a directly downloadable file."),
                    "path/restricted_content" => ("path/restricted_content", "Dropbox restricts downloading this content. Check its access in Dropbox."),
                    "path/locked" => ("path/locked", "Dropbox reports that this file is locked. Resolve the lock in Dropbox before retrying."),
                    _ => (code, detail)
                };
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or HttpRequestException)
        {
            // A missing/malformed/bounded-out error body must not obscure the original HTTP
            // refusal or turn it into a retry. Cancellation is deliberately not swallowed.
        }
        throw new DropboxApiException(response.StatusCode,
            $"Dropbox download refused (HTTP {(int)response.StatusCode}; {code}). {detail} " +
            "No download bytes were accepted from this response.")
        {
            // Preserve the structured missing-source proof for the adapter's narrow mismatch mapping.
            // Unknown, restricted and malformed refusals must remain failures, never inferred from HTTP 409 alone.
            WriteErrorTag = code == "path/not_found" ? "not_found" : null
        };
    }

    private sealed class LeaveOpenReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }

    private sealed class DropboxUploadSessionOffsetException(long correctOffset) : Exception
    {
        public long CorrectOffset { get; } = correctOffset;
    }

    private sealed class DropboxUploadSessionUnavailableException : Exception { }

}
