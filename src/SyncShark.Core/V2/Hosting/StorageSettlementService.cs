namespace Massdex.SyncShark.V2;

/// <summary>Host-owned bounded settlement lane; independent of upload and foreground hydration.</summary>
internal sealed class StorageSettlementService(SqliteStateStore store, CloudFilesLifetime connections,
    Func<PairDef, string, CancellationToken, Task<PilotRemoteEntry?>>? observeRemote = null,
    StorageNotice? target = null, Func<PairDef, bool>? paused = null,
    Func<PairDef, ItemState, Action, CancellationToken, Task<StorageObserved>>? hydrate = null,
    Func<StorageSettlement, CancellationToken, Task<StorageObserved>>? convert = null,
    Func<StorageSettlement, ItemState, CancellationToken, Task<PilotRemoteEntry?>>? refresh = null,
    Func<StorageSettlement, Action, StorageObserved>? acknowledge = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private IReadOnlyList<PairDef> _pairs = [];
    private IReadOnlyCollection<string> _activeRoots = [];
    internal const string RetryQueuedMessage = "Retry is queued; will retry when the pair resumes.";

    private void PrepareTurn()
    {
        _pairs = store.Pairs();
        _activeRoots = _pairs.Where(pair => pair.A.Provider == SyncProviders.Local &&
            pair.B.Provider == SyncProviders.Dropbox && Active(pair)).Select(pair => pair.A.RootId).ToArray();
        _providers.Clear();
        _reportedPairs.Clear();
        _claims.Clear();
        CloseObsoletePairNotices();
    }

    private void CloseObsoletePairNotices()
    {
        foreach (var pair in _pairs)
            if (!store.HasOnlineStoragePolicy(pair.A.RootId) && !store.HasStorageRootWork(pair.A.RootId))
                StorageNotices.CompletePair(store, pair, "availability");
    }

    private void Backoff(PairDef pair) => store.FailStorageRoot(pair.A.RootId);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<WindowsCloudFilesProvider>> _providers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _reportedPairs = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, StorageClaim> _claims = new();
    private WindowsCloudFilesProvider Provider(PairDef pair, CancellationToken ct) =>
        _providers.GetOrAdd(pair.PairId, _ => new Lazy<WindowsCloudFilesProvider>(() =>
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pair.A.RootPath))!);
                if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    throw new StoragePairException("volume", "Cloud Files storage requires a fixed NTFS volume. Keep offline or move the pair to a fixed NTFS volume.");
            }
            catch (StoragePairException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { throw new StoragePairException("volume", "The storage volume cannot be inspected. Reconnect the volume and Retry.", error); }
            try { return connections.RequireProvider(pair, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw new StoragePairException("binding", "Cloud Files is unavailable or a sync-root registration overlaps this pair. Review the registration or keep offline, then Retry.", error); }
        })).Value;
    internal void Start() => _loop ??= Task.WhenAll(Task.Run(LoopAsync), Task.Run(HydrationLoopAsync));

    private async Task HydrationLoopAsync()
    {
        // Offline bulk downloads must not hold up subsequent settlement/refresh pages.
        // Keep per-turn admission/provider caches separate; only entity gates and SQLite are shared.
        await using var lane = new StorageSettlementService(store, connections, observeRemote, paused: paused, hydrate: hydrate);
        long cursor = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (store.HasStorageWork())
                {
                    lane.PrepareTurn();
                    cursor = await lane.HydratePageAsync(cursor, _stop.Token).ConfigureAwait(false);
                }
                else cursor = 0;
                await Task.Delay(cursor == 0 ? TimeSpan.FromSeconds(15) : TimeSpan.FromMilliseconds(100), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                RunOutput.Observe(error);
                try { await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            }
        }
    }

    private PairDef? Pair(StorageSettlement job)
    {
        var known = _pairs.FirstOrDefault(pair => pair.A.RootId == job.RootId && pair.B.RootId == job.RemoteBinding);
        var pair = known is null ? null : store.Pair(known.PairId);
        return pair?.A.RootId == job.RootId && pair.B.RootId == job.RemoteBinding &&
            pair.A.Provider == SyncProviders.Local && pair.B.Provider == SyncProviders.Dropbox ? pair : null;
    }
    private bool Active(PairDef pair) => pair.Enabled && !store.PairPaused(pair.PairId) && !(paused?.Invoke(pair) ?? false);
    private void RequireActiveForRetry(PairDef? pair)
    {
        // Explicit Retry never silently clears a paused/disabled request: the marker stays queued and the
        // caller gets the same explanation the notice will carry when the pair resumes.
        if (pair is not null && !Active(pair)) throw new IOException(RetryQueuedMessage);
    }
    private PairDef? HydrationPair(StorageHydrationJob job)
    {
        var known = _pairs.FirstOrDefault(pair => pair.A.RootId == job.RootId);
        var pair = known is null ? null : store.Pair(known.PairId);
        return pair?.A.RootId == job.RootId ? pair : null;
    }
    private static bool Included(PairDef pair, string path) =>
        !pair.Excluded.Any(prefix => PairPath.IsSelfOrUnder(path, prefix)) &&
        !SyncIgnorePolicy.IsIgnored(path, false, PairInclude.IgnoreRules(pair));

    private bool Eligible(StorageSettlement job)
    {
        var pair = Pair(job);
        // Root admission precedes all item/policy queries; the atomic claim is the only root-backoff gate
        // (a requested row is exempt), so a pair pause is still honoured here.
        if (pair is not null && !Active(pair)) return false;
        // Skip work this turn already knows will fail with the same pair cause, unless the user explicitly
        // requested this entity; claiming a normal row here would only delay it.
        if (pair is not null && _reportedPairs.ContainsKey(pair.PairId) &&
            !store.IsStorageSettlementRequested(job)) return false;
        var claim = store.TryClaimStorageSettlement(job);
        if (claim == StorageClaim.None) return false;
        var local = store.ByObject(job.RootId, job.ObjectId);
        if (pair is null || local is null || !Included(pair, local.Path) ||
            (job.Kind != StorageSettlement.AcknowledgeMove && store.ResolveStoragePolicy(pair.A, local.Path) != "online-only"))
        {
            if (store.RetireStorageSettlement(job) && pair is not null) Complete(job);
            return false;
        }
        var proceed = Active(pair) && (claim == StorageClaim.Requested || !_reportedPairs.ContainsKey(pair.PairId));
        if (proceed) _claims[job.JobId] = claim;
        return proceed;
    }

    // Direct CLI runs do one bounded turn, not an unbounded drain of the database.
    internal async Task DrainAsync(CancellationToken ct)
    {
        // HasStorageWork() reports only currently due background work. An explicit Retry names one entity
        // whose last failed attempt is still deferred, so the target branch must admit it; pause, binding,
        // policy and current-entity checks stay in the target lane. Untargeted drains remain due-gated.
        if (target is null && !store.HasStorageWork()) return;
        PrepareTurn();
        var worker = new FilesOnDemandSettlement(store, ConvertItemAsync, Report, Eligible, Complete);
        if (target is not null)
        {
            // Persist the one explicit request before entering any entity gate. A paused/disabled pair keeps
            // this marker until it resumes; a waiting worker never re-requests after the gate, so the atomic
            // claim (future lease, or a completed row) decides which caller actually runs the effect.
            if (target.Operation == "settlement" && store.StorageSettlementFor(target.RootId, target.ObjectId, target.RemoteBinding) is { } job)
            {
                store.RequestStorageSettlement(job);
                RequireActiveForRetry(Pair(job));
                await worker.RunItemAsync(job, ct).ConfigureAwait(false);
            }
            else if (target.Operation == "remote-update" && store.StoragePointerFor(target.RootId, target.ObjectId, target.RemoteBinding) is { } pointer)
            {
                store.RequestStoragePointer(pointer);
                RequireActiveForRetry(Pair(pointer));
                await UpdatePointerAsync(pointer, ct).ConfigureAwait(false);
            }
            else if (target.Operation == "offline" && store.StorageHydrationFor(target.RootId, target.ObjectId) is { } hydration)
            {
                store.RequestStorageHydration(hydration);
                RequireActiveForRetry(HydrationPair(hydration));
                await HydrateAsync(hydration, ct).ConfigureAwait(false);
            }
            return;
        }
        store.EnqueueStoragePolicyPage(activeRoots: _activeRoots);
        await Task.WhenAll(worker.RunPageAsync(0, ct, _activeRoots), UpdatePointersPageAsync("", ct),
            HydratePageAsync(0, ct)).ConfigureAwait(false);
        CloseObsoletePairNotices();
    }

    private async Task LoopAsync()
    {
        var worker = new FilesOnDemandSettlement(store, ConvertItemAsync, Report, Eligible, Complete);
        long cursor = 0;
        string pointerCursor = "";
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var sweepVisited = 0;
                // Default-off installations do no pair, policy, settlement or pointer queries.
                if (store.HasStorageWork())
                {
                    PrepareTurn();
                    sweepVisited = store.EnqueueStoragePolicyPage(activeRoots: _activeRoots);
                    var settlements = worker.RunPageAsync(cursor, _stop.Token, _activeRoots);
                    var pointers = UpdatePointersPageAsync(pointerCursor, _stop.Token);
                    await Task.WhenAll(settlements, pointers).ConfigureAwait(false);
                    cursor = await settlements.ConfigureAwait(false);
                    pointerCursor = await pointers.ConfigureAwait(false);
                }
                else { cursor = 0; pointerCursor = ""; }
                await Task.Delay(sweepVisited == 0 && cursor == 0 && pointerCursor.Length == 0 ? TimeSpan.FromSeconds(15) : TimeSpan.FromMilliseconds(100), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                RunOutput.Observe(error);
                try { await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            }
        }
    }

    internal static bool SameRemote(PilotRemoteEntry expected, PilotRemoteEntry? actual) =>
        actual is { Kind: PilotEntryKind.File } && actual.Id == expected.Id &&
        !string.IsNullOrEmpty(expected.Id) && !string.IsNullOrEmpty(expected.Revision) &&
        !string.IsNullOrEmpty(expected.ContentHash) && actual.Revision == expected.Revision &&
        Content.SameContent(actual.Size, actual.ContentHash, expected.Size, expected.ContentHash) == true;

    private async Task<StorageObserved> ConvertItemAsync(StorageSettlement job, CancellationToken ct) =>
        convert is null ? await ConvertAsync(job, ct).ConfigureAwait(false) : await convert(job, ct).ConfigureAwait(false);

    private async Task<StorageObserved> ConvertAsync(StorageSettlement job, CancellationToken ct)
    {
        var pair = Pair(job) ?? throw new IOException("The confirmed upload binding is no longer configured.");
        var local = store.ByObject(job.RootId, job.ObjectId) ?? throw new IOException("The local item disappeared. Resync before settlement.");
        if (job.Kind == StorageSettlement.AcknowledgeMove)
            return AcknowledgeMove(job, pair, local, ct);
        var remote = job.Remote with { RelativePath = local.Path };
        var provider = Provider(pair, ct);
        var (volume, fileId) = LocalItems.ParseObjectId(pair.A.RootObjectId);
        using var root = PilotDirectoryLease.Open(pair.A.RootPath, new PilotDirectoryIdentity(volume, fileId));
        var (itemVolume, itemId) = LocalItems.ParseObjectId(job.ObjectId);
        var observe = observeRemote ?? connections.ObserveRemote ??
            throw new StoragePairException("provider", "Remote verification is unavailable. Keep offline and reconnect the provider, then Retry.");
        void Revalidate()
        {
            ct.ThrowIfCancellationRequested();
            root.VerifyBinding();
            var current = Pair(job) ?? throw new IOException("The pair binding was removed.");
            PairMapping.CloudFilesBinding(pair).RequireSame(PairMapping.CloudFilesBinding(current));
            if (!Active(current) || !store.IsCurrentStorageSettlement(job) ||
                store.ByObject(job.RootId, job.ObjectId)?.Path != local.Path ||
                !Included(current, local.Path) || store.ResolveStoragePolicy(current.A, local.Path) != "online-only")
                throw new IOException("The receipt, item path, pause state or storage policy changed. Local bytes are retained; resync or resume before Retry.");
            // The provider invokes this while holding the exact local file exclusively, immediately
            // before dehydration. Metadata-only remote observation never reads the local file.
            var confirmed = observe(current, local.Path, ct).ConfigureAwait(false).GetAwaiter().GetResult();
            if (!SameRemote(remote, confirmed) || !PairPath.Same(confirmed!.RelativePath, local.Path))
            {
                var error = new IOException("The remote id, revision or content changed. Local bytes are retained; a targeted resync is queued.");
                if (store.RetireStorageForResync(job, pair, local.Path, error.Message))
                    StorageNotices.Report(store, pair, local.Path, Metadata(job, "settlement"), error);
                throw error;
            }
            ct.ThrowIfCancellationRequested();
            var latest = Pair(job);
            if (latest is null || !Active(latest) || !store.IsCurrentStorageSettlement(job) ||
                store.ByObject(job.RootId, job.ObjectId)?.Path != local.Path ||
                !Included(latest, local.Path) || store.ResolveStoragePolicy(latest.A, local.Path) != "online-only")
                throw new IOException("The pair or storage intent changed during remote verification. Local bytes are retained; resume or resync before Retry.");
            PairMapping.CloudFilesBinding(pair).RequireSame(PairMapping.CloudFilesBinding(latest));
        }
        var result = await provider.ConvertVerifiedSyncedFileAsync(remote, ct, true,
            new PilotDirectoryIdentity(itemVolume, itemId), Revalidate).ConfigureAwait(false);
        return new(result.State.Availability.ToString(), false, result.State.IsInSync);
    }

    private StorageObserved AcknowledgeMove(StorageSettlement job, PairDef pair, ItemState local, CancellationToken ct)
    {
        void Revalidate()
        {
            ct.ThrowIfCancellationRequested();
            var latest = Pair(job);
            if (latest is null || !Active(latest) || !store.IsCurrentStorageSettlement(job))
                throw new IOException("Move acknowledgment is queued. Resume the pair and Retry.");
            var current = store.ByObject(job.RootId, job.ObjectId);
            if (current is null || current.Path != job.Remote.RelativePath || !Included(latest, current.Path))
            {
                // A later rename is not evidence of changed bytes. Retire only this stale job;
                // the next committed Move or verified scan will queue the new location.
                if (store.RetireStorageSettlement(job)) Complete(job);
                throw new IOException("The pointer acknowledgment location changed; rescan will acknowledge its current location.");
            }
            if (current.Flags != ItemFlags.OwnedPointer || Content.SameContent(current.Size, current.Hash, job.Remote.Size, job.Remote.ContentHash) != true)
                throw new IOException("The pointer observation changed; Retry after the next metadata scan.");
            PairMapping.CloudFilesBinding(pair).RequireSame(PairMapping.CloudFilesBinding(latest));
        }
        try
        {
            Revalidate();
            // Test only the native effect through this seam; admission, claim, entity gate,
            // revalidation, retirement and completion always run through the real service.
            if (acknowledge is not null) return acknowledge(job, Revalidate);
            var (volume, id) = LocalItems.ParseObjectId(job.ObjectId);
            var result = Provider(pair, ct).AcknowledgeDehydratedMove(job.Remote, new(volume, id), Revalidate);
            return new(result.State.Availability.ToString(), result.State.IsPinned, result.State.IsInSync);
        }
        catch (WindowsCloudFilesProvider.PointerAcknowledgmentChangedException)
        {
            // Protect the entity before retiring its metadata-only job. Ordinary classification/hashing owns it now.
            var changed = store.ByObject(job.RootId, job.ObjectId) ?? local;
            store.RequireRehash(job.RootId, changed, "Pointer changed before move acknowledgment");
            store.RequestPointerVerification(pair.PairId, job.RootId, job.ObjectId, changed.Path, job.Remote);
            store.RetireStorageSettlement(job);
            throw;
        }
    }

    private void Report(StorageSettlement job, Exception error)
    {
        _claims.TryRemove(job.JobId, out _);
        if (Pair(job) is not { } pair || !store.IsCurrentStorageSettlement(job)) return;
        // Schedule this item's next attempt before reporting, so a notice failure cannot turn a bounded retry
        // into a rehash on every pass.
        // Pair-scoped causes belong only to the root adapter; the item keeps pending work, not a second count.
        if (StorageNotices.ClassifyFailure(error) is StoragePairException) store.DeferStorageSettlement(job, TimeSpan.Zero);
        else store.FailStorageSettlement(job);
        ReportFailure(pair, store.ByObject(job.RootId, job.ObjectId)?.Path ?? "", Metadata(job, "settlement"), error);
    }

    // Shared by settlement and pointer refresh: a network/auth/provider failure is ONE pair notice plus a durable
    // root backoff, while item-specific causes keep their per-item notice. Never starts native work by itself.
    internal void ReportFailure(PairDef pair, string path, StorageNotice item, Exception error)
    {
        var failure = (Exception?)StorageNotices.ClassifyFailure(error) ?? error;
        if (failure is not StoragePairException || _reportedPairs.TryAdd(pair.PairId, 0))
        {
            if (failure is StoragePairException) Backoff(pair);
            StorageNotices.Report(store, pair, path, item, failure);
        }
    }
    private static StorageNotice Metadata(StorageSettlement job, string operation) =>
        new(job.RootId, job.ObjectId, job.RemoteBinding, operation, StorageNotices.ReceiptScope(job.Remote));
    private void Complete(StorageSettlement job)
    {
        if (Pair(job) is not { } pair) return;
        _claims.TryRemove(job.JobId, out var claim);
        if (claim is StorageClaim.Requested or StorageClaim.Normal)
            StorageNotices.CompleteSuccess(store, pair, claim == StorageClaim.Requested, _reportedPairs.ContainsKey(pair.PairId));
        StorageNotices.Complete(store, pair.PairId, Metadata(job, "settlement"));
    }

    internal async Task<string> UpdatePointersPageAsync(string after, CancellationToken ct)
    {
        if (!store.HasStorageWork()) return "";
        if (_pairs.Count == 0) PrepareTurn();
        var page = store.ChangedStoragePointers(after, activeRoots: _activeRoots);
        await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (job, token) => await UpdatePointerAsync(job, token).ConfigureAwait(false)).ConfigureAwait(false);
        return page.Count == 0 ? "" : SqliteStateStore.StoragePointerCursor(page[^1]);
    }

    private Task UpdatePointerAsync(StorageSettlement job, CancellationToken token) =>
        FilesOnDemandSettlement.SerializeAsync(store, job, async () =>
    {
        var pending = store.StoragePointerFor(job.RootId, job.ObjectId, job.RemoteBinding);
        if (pending is null || pending.Remote != job.Remote || pending.JobId != job.JobId) return;
        var pair = Pair(job);
        if (pair is null) { store.RetireStoragePointer(job); return; }
        if (!Active(pair)) return;
        // The claim is the admission decision (only a requested row bypasses a root backoff). A pair-level
        // cause already reported this turn skips normal work, but never an explicit request.
        if (_reportedPairs.ContainsKey(pair.PairId) && !store.IsStoragePointerRequested(job)) return;
        var claim = store.TryClaimStoragePointer(job);
        if (claim == StorageClaim.None) return;
        var item = Metadata(job, "remote-update");
        try
        {
            var local = store.ByObject(job.RootId, job.ObjectId);
            if (local is null || !Included(pair, local.Path))
            {
                store.RetireStoragePointer(job);
                StorageNotices.Complete(store, pair.PairId, item);
                return;
            }
            if ((local.Flags & (ItemFlags.Ignored | ItemFlags.Quarantined | ItemFlags.Link | ItemFlags.Unreadable)) != 0)
            {
                store.FailStoragePointer(job);
                ReportFailure(pair, local.Path, item, new IOException(
                    "Pointer refresh is blocked by the current item protection flags. Resolve the item hold, then Retry; no content was changed."));
                return;
            }
            // A remote rename may precede its metadata-only local Move. Keep the dirty entity until
            // those paths converge; observing the old path could incorrectly look like deletion.
            var indexedRemote = store.ByObject(pair.B.RootId, job.Remote.Id!);
            if (indexedRemote is not null && !PairPath.Same(indexedRemote.Path, local.Path))
            {
                store.FailStoragePointer(job);
                return;
            }
            // The optional refresh delegate is a test seam for the native read/update; production passes
            // null and keeps the owned-placeholder verification below.
            Func<PairDef, string, CancellationToken, Task<PilotRemoteEntry?>>? observe = null;
            PilotRemoteEntry? remote;
            if (refresh is not null)
                remote = await refresh(job, local, token).ConfigureAwait(false);
            else
            {
                var full = LocalItems.FullPath(pair.A.RootPath, local.Path);
                if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var current) || current is null ||
                    current.State.Availability != SyncCloudFileAvailability.OnlineOnly)
                {
                    store.RetireStoragePointer(job, new(current?.State.Availability.ToString() ?? "Unknown", false, current?.State.IsInSync ?? false));
                    StorageNotices.Complete(store, pair.PairId, item);
                    return;
                }
                observe = observeRemote ?? connections.ObserveRemote ??
                    throw new StoragePairException("provider", "Remote verification is unavailable. Reconnect the provider and Retry.");
                remote = await observe(pair, local.Path, token).ConfigureAwait(false);
            }
            if (remote is not null && !PairPath.Same(remote.RelativePath, local.Path))
            {
                store.FailStoragePointer(job);
                return;
            }
            if (remote is null || remote.Kind != PilotEntryKind.File || remote.Id != job.Remote.Id ||
                remote.Revision is null || remote.ContentHash is null)
            {
                store.RetireStoragePointer(job);
                StorageNotices.Complete(store, pair.PairId, item);
                return; // Ordinary sync handles remote absence/moves; never delete the retained pointer here.
            }
            if (SameRemote(job.Remote, remote))
            {
                store.CompleteStoragePointerUpdate(job, remote);
                StorageNotices.Complete(store, pair.PairId, item);
                StorageNotices.CompleteSuccess(store, pair, claim == StorageClaim.Requested, _reportedPairs.ContainsKey(pair.PairId));
                return;
            }
            remote = remote with { RelativePath = local.Path, ModifiedUtc = remote.ModifiedUtc ?? local.Mtime };
            item = item with { Scope = StorageNotices.ReceiptScope(remote) };
            var baseline = store.BaselinesAt(pair.PairId, Side.A, [local.Path]).FirstOrDefault(point =>
                point.A.ObjectId == local.ObjectId && point.B.ObjectId == remote.Id)
                ?? throw new IOException("The common baseline is unavailable. Resync before refreshing the pointer.");
            var updated = local with { Hash = remote.ContentHash, Size = remote.Size,
                Mtime = remote.ModifiedUtc.Value, Flags = ItemFlags.OwnedPointer };
            var remoteItem = updated with { ObjectId = remote.Id!, VersionId = remote.Revision, Flags = ItemFlags.None };
            var action = new SyncAction(ShortId.Make("ptr", pair.PairId, local.ObjectId, remote.Revision),
                42, ActionKind.Adopt, Side.A, local.Path, null, remoteItem, local, false, baseline.ObjectKey);
            if (refresh is null)
            {
                var verify = observe ?? throw new StoragePairException("provider", "Remote verification is unavailable. Reconnect the provider and Retry.");
                var provider = Provider(pair, token);
                var (volume, id) = LocalItems.ParseObjectId(job.ObjectId);
                var (rootVolume, rootId) = LocalItems.ParseObjectId(pair.A.RootObjectId);
                using var heldRoot = PilotDirectoryLease.Open(pair.A.RootPath, new(rootVolume, rootId));
                provider.UpdateDehydratedPlaceholder(job.Remote with { RelativePath = local.Path }, remote, new(volume, id), () =>
                {
                    token.ThrowIfCancellationRequested();
                    heldRoot.VerifyBinding();
                    var latest = Pair(job) ?? throw new IOException("The pair binding was removed.");
                    PairMapping.CloudFilesBinding(pair).RequireSame(PairMapping.CloudFilesBinding(latest));
                    if (!Active(latest) || store.ByObject(job.RootId, job.ObjectId)?.Path != local.Path)
                        throw new IOException("The pair is paused or the item moved. Resume or resync before Retry.");
                    if (!SameRemote(remote, verify(latest, local.Path, token).ConfigureAwait(false).GetAwaiter().GetResult()))
                        throw new IOException("The remote revision changed during pointer refresh. Resync before Retry.");
                    var finalPair = Pair(job);
                    if (finalPair is null || !Active(finalPair) ||
                        store.ByObject(job.RootId, job.ObjectId)?.Path != local.Path)
                        throw new IOException("The pair paused or changed during remote verification. Resume or resync before Retry.");
                    PairMapping.CloudFilesBinding(pair).RequireSame(PairMapping.CloudFilesBinding(finalPair));
                    // Persist only after validation, but before the native effect under the file lease.
                    store.RecordIntent(pair.PairId, action);
                });
            }
            if (!store.CommitStoragePointerBaseline(pair, baseline, action,
                new(action.ActionId, Outcome.Done, updated, remoteItem, null, null)))
                throw new IOException("The baseline changed during pointer refresh. The recorded intent is retained for resync reconciliation.");
            store.CompleteStoragePointerUpdate(job, remote);
            StorageNotices.Complete(store, pair.PairId, item);
            StorageNotices.CompleteSuccess(store, pair, claim == StorageClaim.Requested, _reportedPairs.ContainsKey(pair.PairId));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            if (StorageNotices.ClassifyFailure(error) is StoragePairException) store.DeferStoragePointer(job, TimeSpan.Zero);
            else store.FailStoragePointer(job);
            ReportFailure(pair, store.ByObject(job.RootId, job.ObjectId)?.Path ?? "", item, error);
        }
    }, token);

    private async Task<long> HydratePageAsync(long after, CancellationToken ct)
    {
        var page = store.StorageHydrationPage(after, 128, _activeRoots);
        await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (job, token) => await HydrateAsync(job, token).ConfigureAwait(false)).ConfigureAwait(false);
        return page.Count == 0 ? 0 : page[^1].JobId;
    }

    private Task HydrateAsync(StorageHydrationJob job, CancellationToken ct) =>
        FilesOnDemandSettlement.SerializeAsync(store, job.RootId, job.ObjectId, async () =>
    {
        var pair = HydrationPair(job);
        if (pair is null) { store.CompleteStorageHydration(job); return; }
        if (!Active(pair) || !store.IsCurrentStorageHydration(job)) return;
        // The claim enforces due and root admission (only a requested row bypasses a backoff); a pair-level
        // cause reported this turn still skips normal work, never an explicit request.
        if (_reportedPairs.ContainsKey(pair.PairId) && !store.IsStorageHydrationRequested(job)) return;
        var claim = store.TryClaimStorageHydration(job);
        if (claim == StorageClaim.None) return;
        var item = new StorageNotice(job.RootId, job.ObjectId, pair.B.RootId, "offline", "offline");
        try
        {
            var local = store.ByObject(job.RootId, job.ObjectId);
            var verification = local is not null && (local.Flags & ItemFlags.NeedsRehash) != 0;
            if (local is null || !Included(pair, local.Path) || (!verification && store.ResolveStoragePolicy(pair.A, local.Path) == "online-only"))
            {
                if (store.CompleteStorageHydration(job)) StorageNotices.Complete(store, pair.PairId, item);
                return;
            }
            void Revalidate()
            {
                ct.ThrowIfCancellationRequested();
                var latest = store.Pair(pair.PairId);
                if (latest is null || !Active(latest) || !store.IsCurrentStorageHydration(job) ||
                    store.ByObject(job.RootId, job.ObjectId)?.Path != local.Path ||
                    !Included(latest, local.Path) || (!verification && store.ResolveStoragePolicy(latest.A, local.Path) == "online-only"))
                    throw new InvalidDataException("The pair, item or offline policy changed. Resume or resync before Retry.");
                PairMapping.CloudFilesBinding(pair).RequireSame(PairMapping.CloudFilesBinding(latest));
            }
            StorageObserved observed;
            if (hydrate is not null) observed = await hydrate(pair, local, Revalidate, ct).ConfigureAwait(false);
            else
            {
                var provider = Provider(pair, ct);
                var (volume, id) = LocalItems.ParseObjectId(job.ObjectId);
                var result = await provider.HydrateOwnedPlaceholderAsync(local.Path, new(volume, id), Revalidate, ct, verification).ConfigureAwait(false);
                observed = new(result.State.Availability.ToString(), true, result.State.IsInSync);
            }
            if (observed.Availability != nameof(SyncCloudFileAvailability.AvailableOffline) || !observed.Pinned)
                throw new InvalidDataException("Offline hydration is not yet confirmed. Retry to finish downloading this item.");
            if (verification)
            {
                // Persist targeted metadata reread before retiring hydration, so a crash cannot strand
                // the scanner's prior Placeholder flags. Hash verification debt remains until normal sync.
                var prior = store.SyncRetryAt(pair.PairId, local.Path);
                var roots = (prior?.ReadRoots ?? []).Append(pair.A.RootId).Distinct().ToArray();
                store.SaveSyncRetry(pair.PairId, prior is null
                    ? new SyncRetry(local.Path, "scan", 0, "Hydrated pointer requires content verification", DateTimeOffset.MinValue, ReadRoots: roots)
                    : prior with { ReadRoots = roots });
            }
            if (store.CompleteStorageHydration(job, observed))
            {
                StorageNotices.Complete(store, pair.PairId, item);
                StorageNotices.CompleteSuccess(store, pair, claim == StorageClaim.Requested, _reportedPairs.ContainsKey(pair.PairId));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var failure = (Exception?)StorageNotices.ClassifyFailure(error) ?? OfflineSource(error);
            if (failure is StoragePairException) store.DeferStorageHydration(job, TimeSpan.Zero);
            else store.FailStorageHydration(job);
            // A classified pair cause takes the same deduped pair path as every other lane: one notice plus a
            // durable root backoff. Only the per-item duplicate is avoided by classification, never by the
            // presence of an older, unrelated pair notice.
            if (failure is not StoragePairException || _reportedPairs.TryAdd(pair.PairId, 0))
            {
                if (failure is StoragePairException) Backoff(pair);
                StorageNotices.Report(store, pair, store.ByObject(job.RootId, job.ObjectId)?.Path ?? "", item, failure);
            }
        }
    }, ct);

    // Keep a correlated item-specific source as the notice cause instead of the generic reader wrapper.
    private static Exception OfflineSource(Exception error) =>
        error is WindowsCloudFilesProvider.OfflineHydrationSourceException { InnerException: { } source } ? source : error;

    internal async Task StopAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _stop.Dispose(); }
}
