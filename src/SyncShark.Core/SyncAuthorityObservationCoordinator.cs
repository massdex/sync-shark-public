using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

public sealed record SyncAuthorityObservationResult(
    SyncAuthorityScanGeneration LocalScan,
    SyncAuthorityScanGeneration DropboxScan,
    int LocalEntries,
    int DropboxEntries,
    int DeferredLocalEntries,
    int LocalFilesHashed,
    int LocalHashesReused,
    TimeSpan LocalInventoryElapsed);

public sealed class SyncAuthorityObservationCoordinator(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority,
    string localRootBindingId,
    string dropboxRootBindingId,
    Func<SyncComparisonProgress, Task>? onInventory = null,
    SyncAuthorityCloudEndpointSet? cloudEndpoints = null)
{
    internal SyncTransferResourceController? InventoryResources { get; init; }
    // The Host synchronously orders/adopts a notification before returning its
    // delivery task. Public arbitrary callbacks retain their serialized contract.
    internal bool ConcurrentProgressRetention { get; init; }
    // Set only by explicit retained-copy recovery. Normal observations retain
    // their effect-authority rejection, including two accepted incarnations.
    internal IReadOnlyList<SyncCompletedCopyMove>? CompletedCopyIdentityRecovery { get; init; }
    internal string? IdentityRecoveryCommandId { get; init; }
    internal string? IdentityRecoveryResultJson { get; init; }
    internal string IdentityRecoveryCompletionStatus { get; init; } = "completed";
    internal Action? IdentityRecoveryPublished { get; init; }
    /// <summary>
    /// True only when this coordinator was handed an explicit cloud/cloud typed port set. The optional set
    /// is the last member so every existing caller keeps byte-identical physical path semantics, and only an
    /// explicit cloud/cloud pair is ever routed to the generic two-cloud component.
    /// </summary>
    public bool UsesCloudEndpointRouting => cloudEndpoints is not null;

    public Task<SyncAuthorityObservationResult> ObserveAsync(string triggerKind,
        CancellationToken cancellationToken = default) =>
        cloudEndpoints is null
            ? ObserveCoreAsync(triggerKind, null, cancellationToken)
            : ObserveCloudAsync(triggerKind, cancellationToken);

    internal Task<SyncAuthorityObservationResult> ObserveBoundAsync(string triggerKind,
        SyncAuthorityObservationBindingAuthority bindingAuthority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindingAuthority);
        // A logical-object observation binding fences a physical local root. A cloud/cloud pair has no such
        // root, so the fenced route is refused rather than reinterpreted against a cloud endpoint.
        if (cloudEndpoints is not null)
            throw new InvalidOperationException(
                "A bound observation requires a physical local root; a cloud/cloud pair has no selected local root.");
        return ObserveCoreAsync(triggerKind, bindingAuthority, cancellationToken);
    }

    private Task<SyncAuthorityObservationResult> ObserveCloudAsync(string triggerKind,
        CancellationToken cancellationToken)
    {
        var set = cloudEndpoints!;
        if (!string.Equals(set.PairId, pair.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The typed cloud endpoint set belongs to another pair.");
        return new SyncAuthorityCloudObservation(pair, set, authority, onInventory)
            .ObserveAsync(triggerKind, cancellationToken);
    }

    private async Task<SyncAuthorityObservationResult> ObserveCoreAsync(string triggerKind,
        SyncAuthorityObservationBindingAuthority? bindingAuthority,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var endpointContext = SyncEndpointContext.Capture(pair);
        var localTitle = SyncEndpointPresentation.PortTitle(endpointContext, "local");
        var remoteTitle = SyncEndpointPresentation.PortTitle(endpointContext, "dropbox");
        var lastReported = DateTimeOffset.MinValue;
        string? lastPhase = null;
        string? lastPath = null, lastDetail = null;
        long inventoryFeedbackTicks = 0;
        var inventoryFeedbackCount = 0;
        long hashFeedbackTicks = 0, feedbackGateWaitTicks = 0;
        var hashFeedbackCount = 0;
        using var feedbackGate = new SemaphoreSlim(1, 1);
        async Task EmitAsync(SyncComparisonProgress progress)
        {
            if (onInventory is null) return;
            var waitStarted = Stopwatch.GetTimestamp();
            await feedbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref feedbackGateWaitTicks, Stopwatch.GetTimestamp() - waitStarted);
            var feedbackStarted = Stopwatch.GetTimestamp();
            var held = true;
            try
            {
                var notification = onInventory(progress);
                if (ConcurrentProgressRetention) { feedbackGate.Release(); held = false; }
                await notification.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Add(ref hashFeedbackTicks, Stopwatch.GetTimestamp() - feedbackStarted);
                Interlocked.Increment(ref hashFeedbackCount);
                if (held) feedbackGate.Release();
            }
        }
        async Task ReportAsync(string phase, string path, int found = 0, string detail = "")
        {
            if (onInventory is null) return;
            var waitStarted = Stopwatch.GetTimestamp();
            await feedbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref feedbackGateWaitTicks, Stopwatch.GetTimestamp() - waitStarted);
            var held = true;
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (phase == lastPhase && path == lastPath && detail == lastDetail &&
                    now - lastReported < TimeSpan.FromMilliseconds(200)) return;
                lastPhase = phase; lastPath = path; lastDetail = detail; lastReported = now;
                var localInventory = phase == "Reading Local Inventory";
                // Diagnostic only: measure the real awaited feedback path separately from file I/O.
                // Every inspected file still has its own progress event.
                if (localInventory && found > 0 && found % 128 == 0)
                    detail += $" Diagnostic: awaited inventory feedback has used " +
                        $"{TimeSpan.FromSeconds((double)inventoryFeedbackTicks / Stopwatch.Frequency).TotalMilliseconds:F1} ms " +
                        $"across {inventoryFeedbackCount:N0} completed callbacks; file inspection time is separate.";
                var feedbackStarted = localInventory ? Stopwatch.GetTimestamp() : 0;
                try
                {
                    var notification = onInventory(new SyncComparisonProgress(phase, path, found, 0, started, now, detail));
                    if (ConcurrentProgressRetention) { feedbackGate.Release(); held = false; }
                    await notification.ConfigureAwait(false);
                }
                finally
                {
                    if (localInventory)
                    {
                        Interlocked.Add(ref inventoryFeedbackTicks, Stopwatch.GetTimestamp() - feedbackStarted);
                        Interlocked.Increment(ref inventoryFeedbackCount);
                    }
                }
            }
            finally { if (held) feedbackGate.Release(); }
        }
        string FeedbackTiming() =>
            $"Diagnostic: metadata callbacks {TimeSpan.FromSeconds((double)Interlocked.Read(ref inventoryFeedbackTicks) / Stopwatch.Frequency).TotalSeconds:F3}s/{inventoryFeedbackCount:N0}; " +
            $"hash/process callbacks {TimeSpan.FromSeconds((double)Interlocked.Read(ref hashFeedbackTicks) / Stopwatch.Frequency).TotalSeconds:F3}s/{hashFeedbackCount:N0}; " +
            $"successful-admission callback gate waits {TimeSpan.FromSeconds((double)Interlocked.Read(ref feedbackGateWaitTicks) / Stopwatch.Frequency).TotalSeconds:F3}s " +
            "(concurrent callers overlap; these sums are not additional wall-clock time).";
        await ReportAsync("Checking " + localTitle + " Folder", pair.LocalRoot);
        using var localRoot = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        await ReportAsync("Checking " + remoteTitle + " Folder", pair.DropboxRoot);
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        var selection = SyncSelectionPolicy.Compile(pair);
        // The pair's provider selects the content-identity algorithm for this run, and the stamp binds
        // retained observations to that algorithm so a foreign or stale identity is never reused.
        var identityStamp = SyncContentIdentity.IdentityStampFor(pair.Provider);
        // A hashed file's byte-level progress lines are dropped below the bounded size and otherwise
        // kept to at most one line per second per file. Every other progress phase passes through.
        var hashProgressThrottle = new Stopwatch();
        // The hash pass runs several workers at once, so these byte-progress fields are shared state: the
        // once-per-second path/time decision is made under a lock instead of racing on them.
        var hashProgressGate = new object();
        string? hashProgressPath = null;
        using var hashProgress = SyncByteProgressStream.Begin(pair.Name, progress =>
        {
            if (progress.Phase == "Hashing file contents")
            {
                if ((progress.Total ?? 0) < 8L * 1024 * 1024) return Task.CompletedTask;
                lock (hashProgressGate)
                {
                    if (!string.Equals(progress.Path, hashProgressPath, StringComparison.Ordinal))
                    {
                        hashProgressPath = progress.Path;
                        hashProgressThrottle.Restart();
                    }
                    else if (hashProgressThrottle.Elapsed < TimeSpan.FromSeconds(1))
                        return Task.CompletedTask;
                    else
                        hashProgressThrottle.Restart();
                }
            }
            return EmitAsync(new(progress.Phase, progress.Path,
                0, 0, progress.StartedUtc, progress.ReportedUtc, progress.Detail));
        });
        // These are independent read-only inventories. Overlap provider latency with local inspection,
        // but retain nothing until BOTH finish. On either failure the shared helper cancels and drains
        // its sibling before this method can release the held local root or report completion.
        PilotLocalScan local;
        IReadOnlyList<PilotRemoteEntry> remote;
        try
        {
            (local, remote) = await SyncEndpointInventoryRead.BothAsync(
                // Phase one reads verified local metadata only; qualifying paths are hashed below.
                token => SyncLocalInventoryIndex.ScanAsync(pair, localRoot, triggerKind,
                    (path, found) => ReportAsync("Reading Local Inventory", path, found,
                        "Inspecting local file metadata and its content identity."), token, EmitAsync, InventoryResources),
                async token =>
                {
                    await ReportAsync("Reading " + remoteTitle + " Inventory", pair.DropboxRoot);
                    var listed = await remoteStore.ListEntriesAsync(item => ReportAsync(item.Operation,
                        string.IsNullOrWhiteSpace(item.Target) ? pair.DropboxRoot : item.Target,
                        item.ObservedCount, item.Detail), token)
                        ?? throw new InvalidDataException("The provider returned no inventory; missing is not empty.");
                    await ReportAsync("Verifying " + remoteTitle + " Inventory", pair.DropboxRoot, listed.Count);
                    await remoteStore.VerifyRootIdentityAsync(token);
                    return listed;
                }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            local = await HashQualifyingLocalFilesAsync(local, remote, localRoot, identityStamp,
                (path, found) => ReportAsync("Reading Local Inventory", path, found,
                    "Hashing a local file whose content can change a sync decision."), cancellationToken);
        }
        catch
        {
            // BothAsync has drained the siblings and scanner-held file handles.
            // Retain the measured failure too; a diagnostic sink never replaces
            // the original failure or claims an incomplete inventory is complete.
            if (onInventory is not null)
                try
                {
                    await onInventory(new("Reading Local Inventory", pair.LocalRoot, 0, 0,
                        started, DateTimeOffset.UtcNow, "Inventory did not complete. " + FeedbackTiming()));
                }
                catch { /* The original operational failure remains authoritative. */ }
            throw;
        }
        var localIncluded = local.Entries.Values.Where(entry => selection.IsIncluded(entry.RelativePath, entry.Kind == PilotEntryKind.Directory)).ToArray();
        var remoteIncluded = remote.Where(entry => selection.IsIncluded(entry.RelativePath, entry.Kind == PilotEntryKind.Directory)).ToArray();
        foreach (var blocked in remoteIncluded.Where(entry => entry.TransferBlockedReason is not null))
            await ReportAsync("Cloud Item Blocked", blocked.RelativePath, 0, blocked.TransferBlockedReason!);
        var remoteByPath = remoteIncluded.ToDictionary(entry => entry.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var retainedRepresentations = (await authority.LoadLatestLocalCloudRepresentationsAsync(pair.Id,
                cancellationToken))
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var retainedIgnored = await authority.LoadIgnoredPathsAsync(pair.Id, pair.IgnoreRules, cancellationToken);
        var excludedPaths = local.Entries.Values.Where(entry => !selection.IsIncluded(entry.RelativePath, entry.Kind == PilotEntryKind.Directory))
            .Select(entry => entry.RelativePath)
            .Concat(remote.Where(entry => !selection.IsIncluded(entry.RelativePath, entry.Kind == PilotEntryKind.Directory)).Select(entry => entry.RelativePath))
            .Concat(retainedIgnored)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await ReportAsync("Saving Folder Inventories", pair.Name, localIncluded.Length + remoteIncluded.Length,
            "Creating the local and remote inventory snapshots in Sync Shark's database. No files are being transferred. " +
            $"Fresh local inventory took {local.Elapsed?.TotalSeconds:F3}s. " + FeedbackTiming());
        var localScope = local.Decisions.Count == 0
            ? "{\"scope\":\"complete\"}"
            : JsonSerializer.Serialize(new { scope = "complete_with_uncertainty", uncertainPaths = local.Decisions.Count });
        // One exact retained-root read, taken once AFTER both inventories are complete and BEFORE either scan
        // generation is created. An explicit typed cloud root must emit its own provider/account/namespace
        // identity; a verified legacy root keeps the legacy fileId/revision identity. A missing, stale or
        // misbound retained root is an error and is never treated as a legacy identity.
        var retainedRemoteRoot = await SyncAuthorityRemoteIdentity.LoadAsync(authority, pair,
            dropboxRootBindingId, cancellationToken);
        var localScan = await authority.BeginScanGenerationAsync(pair.Id, localRootBindingId, "local",
            triggerKind, localScope, cancellationToken);
        SyncAuthorityScanGeneration? remoteScan = null;
        try
        {
            remoteScan = await authority.BeginScanGenerationAsync(pair.Id, dropboxRootBindingId, "dropbox",
                triggerKind, "{\"scope\":\"complete\"}", cancellationToken);
    
            var localObservations = localIncluded.Select(entry => BuildLocalObservation(entry, remoteByPath,
                    retainedRepresentations, identityStamp))
                .Select(item => ApplyObjectBinding(item, "local", bindingAuthority)).ToArray();
            // A cloud-native item is present but cannot enter the byte-file planner. Retain explicit
            // path evidence below so neither absence nor a local replacement can be inferred.
            var remoteObservations = remoteIncluded.Where(entry => entry.TransferBlockedReason is null)
                .Select(entry => BuildRemoteObservation(entry, identityStamp, retainedRemoteRoot))
                .Select(item => ApplyObjectBinding(item, "dropbox", bindingAuthority)).ToArray();
            // The complete evidence arrays are built before any write so that one bounded preparation per side
            // receives the whole supplied inventory: the normal entries, the evidence rows and the existing
            // optional fenced binding or identity-recovery proof.
            var localEvidence = local.Decisions
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(decision =>
                {
                    var evidence = JsonSerializer.Serialize(new
                    {
                        path = PilotPath.NormalizeRelative(decision.RelativePath),
                        state = decision.Kind == PilotDecisionKind.OnlineOnly ? "online_only" : "unavailable",
                        reason = decision.Reason
                    });
                    return (DeterministicId("observation", localScan.ScanGenerationId,
                        decision.RelativePath, decision.Kind.ToString()), evidence);
                })
                .Concat(excludedPaths.Select(excludedPath =>
                {
                    var evidence = JsonSerializer.Serialize(new
                    {
                        path = excludedPath,
                        state = "selective_sync_excluded",
                        reason = "The saved folder selection or ignore rules exclude this path. Existing contents are retained."
                    });
                    return (DeterministicId("observation", localScan.ScanGenerationId,
                        excludedPath, "selection"), evidence);
                }))
                .ToArray();
            var remoteEvidence = excludedPaths.Select(excludedPath =>
            {
                var evidence = JsonSerializer.Serialize(new
                {
                    path = excludedPath,
                    state = "selective_sync_excluded",
                    reason = "The saved folder selection or ignore rules exclude this path. Existing contents are retained."
                });
                return (DeterministicId("observation", remoteScan.ScanGenerationId,
                    excludedPath, "selection"), evidence);
            }).Concat(remoteIncluded.Where(entry => entry.TransferBlockedReason is not null).Select(entry =>
            {
                var evidence = JsonSerializer.Serialize(new
                {
                    path = PilotPath.NormalizeInventoryPath(entry.RelativePath),
                    state = "cloud_native",
                    reason = entry.TransferBlockedReason,
                    fileId = entry.Id,
                    revision = entry.Revision
                });
                return (DeterministicId("observation", remoteScan.ScanGenerationId,
                    entry.RelativePath, "cloud_native"), evidence);
            })).ToArray();
    
            if (CompletedCopyIdentityRecovery is { Count: > 0 } identityRecovery)
            {
                if (bindingAuthority is not null)
                    throw new InvalidOperationException("Identity recovery cannot replace fenced cross-pair authority.");
                await authority.VerifyIdentityRecoveryInventoriesAsync(localScan, remoteScan,
                    localObservations, remoteObservations, identityRecovery, cancellationToken);
                await ReportAsync("Repairing File Identities", pair.Name, identityRecovery.Count,
                    "Separating verified distinct copied files in the new inventory. Both files and all accepted history remain unchanged; no transfer is running.");
                await authority.PrepareCompactScanAsync(localScan, localObservations, localEvidence, null,
                    identityRecovery, cancellationToken, onInventory, localTitle, ConcurrentProgressRetention);
                await authority.PrepareCompactScanAsync(remoteScan, remoteObservations, remoteEvidence, null,
                    identityRecovery, cancellationToken, onInventory, remoteTitle, ConcurrentProgressRetention);
            }
            else if (bindingAuthority is null)
            {
                await ReportAsync("Saving Folder Inventories", pair.LocalRoot, localObservations.Length,
                    $"Writing {localObservations.Length:N0} {localTitle} file/folder metadata records to the inventory database.");
                await authority.PrepareCompactScanAsync(localScan, localObservations, localEvidence, null, null,
                    cancellationToken, onInventory, localTitle, ConcurrentProgressRetention);
                await ReportAsync("Saving Folder Inventories", pair.DropboxRoot, remoteObservations.Length,
                    $"Writing {remoteObservations.Length:N0} {remoteTitle} file/folder metadata records to the inventory database.");
                await authority.PrepareCompactScanAsync(remoteScan, remoteObservations, remoteEvidence, null, null,
                    cancellationToken, onInventory, remoteTitle, ConcurrentProgressRetention);
            }
            else
            {
                await ReportAsync("Saving Folder Inventories", pair.LocalRoot, localObservations.Length,
                    $"Writing {localObservations.Length:N0} identity-checked {localTitle} metadata records to the inventory database.");
                await authority.PrepareCompactScanAsync(localScan, localObservations, localEvidence, bindingAuthority,
                    null, cancellationToken, onInventory, localTitle, ConcurrentProgressRetention);
                await ReportAsync("Saving Folder Inventories", pair.DropboxRoot, remoteObservations.Length,
                    $"Writing {remoteObservations.Length:N0} identity-checked {remoteTitle} metadata records to the inventory database.");
                await authority.PrepareCompactScanAsync(remoteScan, remoteObservations, remoteEvidence, bindingAuthority,
                    null, cancellationToken, onInventory, remoteTitle, ConcurrentProgressRetention);
            }
    
            await ReportAsync("Saving Folder Inventories", pair.Name, localEvidence.Length + remoteEvidence.Length,
                "Recording unavailable, online-only and excluded paths without transferring their contents.");
    
            await ReportAsync("Saving Folder Inventories", pair.Name, localIncluded.Length + remoteIncluded.Length,
                "Committing the completed inventory snapshots to Sync Shark's database.");
            if (CompletedCopyIdentityRecovery is { Count: > 0 } publicationProofs)
            {
                await authority.PublishIdentityRecoveryAsync(localScan, remoteScan, localObservations, remoteObservations,
                    publicationProofs, pair.PolicyRevision,
                    IdentityRecoveryCommandId ?? throw new InvalidDataException("Identity recovery has no accepted command."),
                    IdentityRecoveryCompletionStatus,
                    IdentityRecoveryResultJson ?? throw new InvalidDataException("Identity recovery has no retained mapping result."),
                    cancellationToken);
                IdentityRecoveryPublished?.Invoke();
            }
            else
            {
                await authority.CompleteScanGenerationAsync(localScan, cancellationToken);
                await authority.CompleteScanGenerationAsync(remoteScan, cancellationToken);
            }
            await ReportAsync("Updating Folder Index", pair.Name, localIncluded.Length + remoteIncluded.Length,
                "Replacing the saved path index with the verified folder inventories; no file transfer is running.");
            await authority.ReplacePathIndexAsync(pair.Id, localScan.ScanGenerationId,
                remoteScan.ScanGenerationId, BuildPathIndex(local, remote, selection), selection,
                cancellationToken, onInventory);
            return new SyncAuthorityObservationResult(localScan, remoteScan, localIncluded.Length, remoteIncluded.Length,
                local.Decisions.Count, local.FilesHashed, local.HashesReused, local.Elapsed ?? TimeSpan.Zero);
        }
        catch (Exception failure)
        {
            try
            {
                await authority.CloseFailedScanGenerationsAsync(
                    new[] { localScan, remoteScan }.OfType<SyncAuthorityScanGeneration>().ToArray());
            }
            catch (Exception closureFailure)
            {
                throw new AggregateException("Inventory failed and its incomplete state could not be closed.",
                    failure, closureFailure);
            }
            throw;
        }
    }

    /// <summary>
    /// Phase two of the local inventory. A file is hashed unless a reuse rule applies. A file whose size,
    /// last write, file id and content algorithm match a saved version reuses that version's hash; an
    /// equal-size both-sides pair whose local last write equals Dropbox client_modified (whole seconds)
    /// adopts the listed hash. Every other local file is hashed, including a one-side-only file that is
    /// about to be uploaded or removed (its bytes are read either way). Directories keep no hash.
    /// </summary>
    private async Task<PilotLocalScan> HashQualifyingLocalFilesAsync(PilotLocalScan local,
        IReadOnlyList<PilotRemoteEntry> remote, PilotDirectoryLease localRoot, string? identityStamp,
        Func<string, int, Task> onInventory, CancellationToken cancellationToken)
    {
        var remoteFiles = new Dictionary<string, PilotRemoteEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in remote.Where(entry => entry.Kind == PilotEntryKind.File))
            remoteFiles.TryAdd(entry.RelativePath, entry);
        var algorithm = SyncContentIdentity.AlgorithmFor(pair.Provider);
        var savedHashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in await authority.LoadLatestCompleteObservationAsync(pair.Id, "local", cancellationToken))
        {
            if (item.ObjectKind != "file") continue;
            using var evidence = JsonDocument.Parse(item.EvidenceJson);
            if (evidence.RootElement.ValueKind != JsonValueKind.Object ||
                !evidence.RootElement.TryGetProperty("identity", out var identity) ||
                identity.ValueKind != JsonValueKind.String) continue;
            var key = HashlessKey(identity.GetString()!, item.Size);
            if (!savedHashes.TryAdd(key, item.ContentHash) &&
                !string.Equals(savedHashes[key], item.ContentHash, StringComparison.OrdinalIgnoreCase))
                savedHashes[key] = null;
        }
        var entries = new Dictionary<string, PilotLocalEntry>(local.Entries, StringComparer.OrdinalIgnoreCase);
        var toHash = new List<string>();
        var reused = 0;
        foreach (var entry in local.Entries.Values)
        {
            if (entry.Kind != PilotEntryKind.File || entry.ContentHash is not null ||
                entry.VolumeSerialNumber is null || entry.FileId is null) continue;
            savedHashes.TryGetValue(HashlessKey(SyncAuthorityLocalVersionIdentity.Serialize(
                entry.Kind, entry.VolumeSerialNumber.Value, entry.FileId.Value, entry.LastWriteUtc,
                entry.CloudFileId, entry.CloudRevision, identityStamp), entry.Size), out var savedHash);
            remoteFiles.TryGetValue(entry.RelativePath, out var peer);
            // The Dropbox app stamps client_modified with the uploader's mtime: an equal-size pair whose
            // times agree to the second is treated as identical and adopts the listed native hash unread.
            savedHash ??= peer is { ClientModifiedUtc: { } clientModified, ContentHash: { } peerHash } &&
                peer.Size == entry.Size && SyncContentIdentity.IdentityMatchesAlgorithm(algorithm, peerHash) &&
                clientModified.UtcTicks / TimeSpan.TicksPerSecond ==
                entry.LastWriteUtc.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond ? peerHash : null;
            if (savedHash is not null)
            {
                entries[entry.RelativePath] = entry with { ContentHash = savedHash };
                reused++;
            }
            else
                toHash.Add(entry.RelativePath);
        }
        local = local with { Entries = entries, HashesReused = reused };
        if (toHash.Count == 0) return local;
        // The ordinary held-handle scanner hashes exactly these paths. The paths are split into disjoint
        // slices hashed at once, so the only change is how many files are in flight; every file keeps the
        // same exclusive open, held-identity check and blocked-on-failure decision, and a failed read still
        // becomes the same blocked uncertainty a full hashing scan would record, never absence or equality.
        var hashed = await HashQualifyingSlicesAsync(localRoot, toHash, onInventory, cancellationToken);
        var merged = PilotLocalScanner.MergeIncremental(local, hashed, toHash, cancellationToken, out _);
        return merged with { Decisions = local.Decisions.Concat(merged.Decisions).ToArray(),
            Elapsed = local.Elapsed, PathsInspected = local.PathsInspected, Incremental = local.Incremental };
    }

    /// <summary>
    /// Hashes the qualifying paths with a bounded number of concurrent workers. Each worker runs the
    /// ordinary held-handle scanner over a disjoint slice and writes its own result slot, so collection is
    /// race-free; the caller merges the concatenated entries in one deterministic path order. One shared
    /// progress gate keeps the once-per-second report cadence and the running inspected count identical
    /// whether one worker or the maximum is running.
    /// </summary>
    private async Task<PilotLocalScan> HashQualifyingSlicesAsync(PilotDirectoryLease localRoot,
        IReadOnlyList<string> toHash, Func<string, int, Task> onInventory, CancellationToken cancellationToken)
    {
        var slices = Math.Min(Math.Clamp(Environment.ProcessorCount, 2, 8), toHash.Count);
        // Each worker publishes its own latest inspected count with Interlocked; the gate itself holds the
        // shared once-per-second time decision so concurrent workers cannot both report or corrupt it.
        var inspected = new int[slices];
        var progressClock = Stopwatch.StartNew();
        var reportedAt = TimeSpan.FromSeconds(-1);
        var reportGate = new object();
        Func<string, int, Task> ProgressFor(int slice)
        {
            return (path, found) =>
            {
                Interlocked.Exchange(ref inspected[slice], found);
                lock (reportGate)
                {
                    var elapsed = progressClock.Elapsed;
                    if (elapsed - reportedAt < TimeSpan.FromSeconds(1)) return Task.CompletedTask;
                    reportedAt = elapsed;
                }
                return onInventory(path, inspected.Sum());
            };
        }
        var scans = new PilotLocalScan[slices];
        await Task.WhenAll(Enumerable.Range(0, slices).Select(slice => Task.Run(async () =>
        {
            var start = (int)((long)toHash.Count * slice / slices);
            var end = (int)((long)toHash.Count * (slice + 1) / slices);
            scans[slice] = await new PilotLocalScanner { InventoryScheduling = InventoryResources?.ForEndpoints([]) }
                .ScanAsync(localRoot, cancellationToken: cancellationToken, traverseDirectory: _ => false,
                    onInventory: ProgressFor(slice), refreshPaths: toHash.Skip(start).Take(end - start).ToArray(),
                    contentProvider: pair.Provider, expectedPairId: pair.Id).ConfigureAwait(false);
        }, cancellationToken))).ConfigureAwait(false);
        var refreshedEntries = new List<KeyValuePair<string, PilotLocalEntry>>();
        var refreshedDecisions = new List<PilotDecision>();
        var filesHashed = 0;
        foreach (var scan in scans)
        {
            refreshedEntries.AddRange(scan.Entries);
            refreshedDecisions.AddRange(scan.Decisions);
            filesHashed += scan.FilesHashed;
        }
        // One deterministic path order, independent of the order the workers happened to finish in.
        refreshedEntries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        refreshedDecisions.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        var entries = new Dictionary<string, PilotLocalEntry>(refreshedEntries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in refreshedEntries) entries[item.Key] = item.Value;
        return new PilotLocalScan(entries, refreshedDecisions, 0, filesHashed, 0);
    }

    private static string HashlessKey(string identityJson, long? size)
    {
        if (JsonNode.Parse(identityJson) is not JsonObject node) return string.Empty;
        node.Remove("contentHash");
        node.Remove("sizeBytes");
        return node.ToJsonString() + "\n" + size?.ToString(CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<SyncPathIndexEntry> BuildPathIndex(PilotLocalScan local,
        IReadOnlyList<PilotRemoteEntry> remote, SyncSelectionIndex selection)
    {
        var localEntries = local.Entries.Values.ToDictionary(item => item.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        // Unsupported names are retained in the authoritative evidence/review, not in the
        // Windows path index used by device storage actions.
        var remoteEntries = remote.Where(item => PilotPath.IsWindowsTransferPath(item.RelativePath))
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var paths = localEntries.Keys.Concat(remoteEntries.Keys)
            .Concat(SyncSelectionPolicy.Normalize(pair.SelectionRules).Select(rule => rule.FolderPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        var result = new List<SyncPathIndexEntry>();
        foreach (var path in paths)
        {
            localEntries.TryGetValue(path, out var localEntry);
            remoteEntries.TryGetValue(path, out var remoteEntry);
            var kind = localEntry?.Kind ?? remoteEntry?.Kind ?? PilotEntryKind.Directory;
            if (localEntry is not null && remoteEntry is not null && localEntry.Kind != remoteEntry.Kind)
                kind = PilotEntryKind.Directory;
            var included = selection.IsIncluded(path, kind == PilotEntryKind.Directory);
            result.Add(new SyncPathIndexEntry(path, kind, localEntry is not null, localEntry is not null,
                remoteEntry is not null,
                localEntry?.CloudAvailability == SyncCloudFileAvailability.OnlineOnly,
                localEntry?.VolumeSerialNumber is not null && localEntry.FileId is not null
                    ? $"local:{localEntry.VolumeSerialNumber.Value:x8}:{localEntry.FileId.Value:x16}"
                    : null,
                remoteEntry?.Id, included));
        }
        return result;
    }

    private static SyncAuthorityObservationInput BuildLocalObservation(PilotLocalEntry entry,
        IReadOnlyDictionary<string, PilotRemoteEntry> remoteByPath,
        IReadOnlyDictionary<string, SyncAuthorityLocalCloudRepresentation> retainedRepresentations,
        string? identityStamp)
    {
        if (entry.VolumeSerialNumber is null || entry.FileId is null)
            throw new InvalidDataException("A local observation has no held NTFS identity.");
        // Availability is mutable local materialization state, not content/version
        // identity. It remains in observation evidence for policy and UI projections.
        var identity = SyncAuthorityLocalVersionIdentity.Serialize(entry.Kind,
            entry.VolumeSerialNumber.Value, entry.FileId.Value, entry.LastWriteUtc,
            entry.CloudFileId, entry.CloudRevision, identityStamp, entry.ContentHash, entry.Size);
        // Adoption is committed before native first-use conversion. If execution is
        // interrupted in that interval, carry the exact adopted Dropbox representation
        // only while both observed sides still match the retained two-sided point. This
        // makes the interruption an availability/representation change, never content.
        if (entry.Kind == PilotEntryKind.File && entry.CloudAvailability is null &&
            entry.CloudFileId is null && entry.CloudRevision is null && entry.ContentHash is not null &&
            remoteByPath.TryGetValue(entry.RelativePath, out var remote) && remote.Id is not null &&
            remote.Revision is not null && remote.ContentHash is not null &&
            retainedRepresentations.TryGetValue(entry.RelativePath, out var retained))
        {
            var adoptedIdentity = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File,
                entry.VolumeSerialNumber.Value, entry.FileId.Value, entry.LastWriteUtc,
                remote.Id, remote.Revision, identityStamp, entry.ContentHash, entry.Size);
            if (SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(retained.LocalIdentityJson,
                    adoptedIdentity, entry.ContentHash, entry.Size) &&
                string.Equals(retained.LocalContentHash, entry.ContentHash, StringComparison.OrdinalIgnoreCase) &&
                retained.LocalSize == entry.Size &&
                string.Equals(retained.DropboxContentHash, remote.ContentHash,
                    StringComparison.OrdinalIgnoreCase) && retained.DropboxSize == remote.Size &&
                RemoteIdentityMatches(retained.DropboxIdentityJson, remote.Id, remote.Revision))
                identity = adoptedIdentity;
        }
        var evidence = JsonSerializer.Serialize(new
        {
            path = PilotPath.NormalizeRelative(entry.RelativePath),
            kind = entry.Kind.ToString().ToLowerInvariant(),
            identity,
            entry.ContentHash,
            entry.Size,
            contentAlgorithm = identityStamp,
            cloudAvailability = entry.CloudAvailability?.ToString()
        });
        return new SyncAuthorityObservationInput(PilotPath.NormalizeRelative(entry.RelativePath),
            entry.Kind == PilotEntryKind.File ? "file" : "directory", identity, entry.ContentHash,
            entry.Size, evidence);
    }

    private static bool RemoteIdentityMatches(string identityJson, string dropboxId, string dropboxRevision)
    {
        using var identity = JsonDocument.Parse(identityJson);
        var root = identity.RootElement;
        return root.TryGetProperty("fileId", out var fileId) && fileId.ValueKind == JsonValueKind.String &&
               string.Equals(fileId.GetString(), dropboxId, StringComparison.Ordinal) &&
               root.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.String &&
               string.Equals(revision.GetString(), dropboxRevision, StringComparison.Ordinal);
    }

    private static SyncAuthorityObservationInput BuildRemoteObservation(PilotRemoteEntry entry,
        string? identityStamp, SyncAuthorityRetainedRootAuthority retainedRoot)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
            throw new InvalidDataException("A Dropbox observation has no stable file or folder ID.");
        // Remote modification time is comparison evidence, not physical/version
        // identity. Keeping it outside identity_json prevents a later metadata-only
        // observation from fabricating a changed remote revision.
        //
        // Use the existing file-observation serializer, not the root serializer: folderId and arbitrary
        // root members must not change the file's version identity. A verified legacy root keeps its bytes.
        var identity = SyncAuthorityRemoteIdentity.Serialize(retainedRoot, entry.Id, entry.Revision);
        var contentAlgorithm = identityStamp;
        if (retainedRoot.IsExplicitCloudIdentity)
        {
            if (!SyncEndpointProviders.TryGet(retainedRoot.IdentityProvider, out var provider))
                throw new InvalidDataException(
                    "The retained explicit cloud root names no registered provider; the observation is refused.");
            contentAlgorithm = provider!.ContentHashAlgorithm;
        }
        var evidence = JsonSerializer.Serialize(new
        {
            path = PilotPath.NormalizeRelative(entry.RelativePath),
            kind = entry.Kind.ToString().ToLowerInvariant(),
            identity,
            entry.ContentHash,
            entry.Size,
            contentAlgorithm,
            modifiedUtc = entry.ModifiedUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
        return new SyncAuthorityObservationInput(PilotPath.NormalizeRelative(entry.RelativePath),
            entry.Kind == PilotEntryKind.File ? "file" : "directory", identity, entry.ContentHash,
            entry.Size, evidence);
    }

    private static SyncAuthorityObservationInput ApplyObjectBinding(SyncAuthorityObservationInput observation,
        string side, SyncAuthorityObservationBindingAuthority? authority)
    {
        if (authority is null) return observation;
        var matches = authority.Bindings.Where(item => item.Side == side &&
                                                       string.Equals(PilotPath.NormalizeRelative(item.RelativePath),
                                                           PilotPath.NormalizeRelative(observation.RelativePath),
                                                           StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            0 => observation,
            1 => observation with { BoundObjectId = matches[0].ObjectId },
            _ => throw new InvalidDataException("One observed path received multiple logical-object bindings.")
        };
    }

    private static string DeterministicId(string role, params string[] parts)
    {
        var material = role + "\n" + string.Join("\n", parts);
        return role + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..32];
    }
}

/// <summary>One bounded two-endpoint read primitive, shared by local/cloud and cloud/cloud inventories.
/// It changes scheduling only: callers still validate identities and publish only complete observations.</summary>
internal static class SyncEndpointInventoryRead
{
    internal static async Task<(TLeft Left, TRight Right)> BothAsync<TLeft, TRight>(
        Func<CancellationToken, Task<TLeft>> readLeft, Func<CancellationToken, Task<TRight>> readRight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Local metadata can complete synchronously. Start both on the pool so that it cannot delay
        // dispatch of the other endpoint or block the UI's caller thread. There are exactly two tasks.
        var left = Task.Run(() => readLeft(lifetime.Token), lifetime.Token);
        var right = Task.Run(() => readRight(lifetime.Token), lifetime.Token);
        var first = await Task.WhenAny(left, right).ConfigureAwait(false);
        ExceptionDispatchInfo? failure = null;
        if (!first.IsCompletedSuccessfully)
        {
            try { await first.ConfigureAwait(false); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            // A callback fault must not skip the drain or replace the original endpoint failure.
            try { lifetime.Cancel(); }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        try { await Task.WhenAll(left, right).ConfigureAwait(false); }
        catch
        {
            failure?.Throw();
            throw;
        }
        failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return (await left.ConfigureAwait(false), await right.ConfigureAwait(false));
    }
}
