using System.ComponentModel;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Reads one local NTFS root's complete or incremental inventory and its content hashes.</summary>
public sealed partial class LocalInventory : IInventorySource
{
    private readonly RootDef _root;
    private readonly IDefenderHistory? _history;
    private readonly string? _pairId;
    // N10-a: probe only after a recorded v2 registration or an indexed owned pointer exists.
    // Registration permits discovery after conversion even before ownership reaches the index.
    // Both store queries short-circuit before schema access when storage has never been enabled.
    private readonly string? _expectedPairId;
    private readonly Side _side;
    private readonly IStateStore _known;
    private readonly IReadOnlySet<string> _productOwnedPaths;
    private readonly IReadOnlySet<string> _productOwnedRelative;
    private readonly Func<Exception, bool> _isStateDamage;
    private readonly V2Settings _settings;
    private readonly TimeProvider _clock;
    private readonly uint _volume;
    private readonly ulong _rootFileId;
    private readonly PilotDirectoryIdentity _rootIdentity;
    // Set before inventory reads; previews may persist recommendations only in their isolated store.
    internal bool ObserveOnly { get; set; }
    internal Func<string, long[]?>? CapacityQueryForTest { get; set; }
    internal Action<IJournalStore, JournalPending, SyncDiscoveryRoot>? CapacityQueueForTest { get; set; }
    private void ObserveCapacity(string? cursor, bool overflow = false) =>
        JournalSizing.TryObserve(_known, _pairId!, _root.RootPath, _volume, _rootFileId, cursor,
            _clock.GetUtcNow(), overflow, ObserveOnly, CapacityQueryForTest, CapacityQueueForTest);

    internal LocalGapWorker? GapWorker { get; set; }
    internal ILocalGapIo? GapIo { get; set; }
    internal const int VerificationPublicationPages = 4;
    internal const long MaximumReplayBytes = 64L * 1024 * 1024;
    internal Action<long, long>? VerificationProgress { get; set; }
    internal static bool ReplayTooLarge(long from, long through) => through > from && through - from > MaximumReplayBytes;

    /// <summary>Binds one local root; the saved root object id must parse into its NTFS identity.</summary>
    public LocalInventory(RootDef root, Side side, IStateStore known,
        IReadOnlySet<string> productOwnedPaths, V2Settings settings, TimeProvider clock, Func<Exception, bool> isStateDamage, IDefenderHistory? history = null, string? pairId = null)
    {
        ArgumentNullException.ThrowIfNull(isStateDamage);
        _isStateDamage = isStateDamage;
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _side = side;
        _history = history;
        _pairId = pairId;
        _known = known ?? throw new ArgumentNullException(nameof(known));
        _expectedPairId = pairId is not null &&
            (_known.HasOwnedPointers(root.RootId) || _known.StorageRegistrations().Any(entry => entry.RootId == root.RootId))
            ? pairId : null;
        _productOwnedPaths = productOwnedPaths ?? throw new ArgumentNullException(nameof(productOwnedPaths));
        // The relative form the shared exclusion test needs; the scanner keeps the absolute set for enumeration.
        _productOwnedRelative = LocalItems.ProductOwnedRelative(_root.RootPath, productOwnedPaths);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        // Refused before any work: zero workers would hash nothing, a zero read chunk would read nothing.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.LocalHashWorkers, nameof(settings.LocalHashWorkers));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.LocalHashBufferBytes,
            nameof(settings.LocalHashBufferBytes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        (_volume, _rootFileId) = LocalItems.ParseObjectId(root.RootObjectId);
        _rootIdentity = new PilotDirectoryIdentity(_volume, _rootFileId);
    }

    public async Task ReadSubtreeAsync(string path, Func<IReadOnlyList<ItemState>, Task> accept, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        using var lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        var budget = new VerificationBudget(VerificationElapsedForTest);
        long done = 0, reused = 0;
        if (path.Length == 0)
        {
            await accept([new ItemState("", ItemKind.Folder, _root.RootObjectId, null, null, 0,
                DateTimeOffset.UnixEpoch, ItemFlags.None)]).ConfigureAwait(false);
            await WalkAsync("", _rootIdentity).ConfigureAwait(false);
        }
        else
        {
            var roots = await ReadPageAsync([path], null).ConfigureAwait(false);
            foreach (var folder in roots.Where(CanTraverse))
                await WalkAsync(folder.Path, Identity(folder)).ConfigureAwait(false);
        }
        lease.VerifyBinding();

        static bool CanTraverse(ItemState item) => item.Kind == ItemKind.Folder &&
            item.Flags == ItemFlags.None && item.IncompleteReason is null;
        static PilotDirectoryIdentity Identity(ItemState item)
        {
            var (volume, id) = LocalItems.ParseObjectId(item.ObjectId);
            return new(volume, id);
        }
        async Task WalkAsync(string directory, PilotDirectoryIdentity identity)
        {
            try { await WalkCoreAsync(directory, identity).ConfigureAwait(false); }
            catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, isStateDamage: _isStateDamage) == FailureRetryOwner.FailureScope.Item)
            {
                await accept([new ItemState(directory, ItemKind.Folder, "", null, null, 0,
                    DateTimeOffset.UnixEpoch, ItemFlags.Unreadable) { IncompleteReason = error.Message }]).ConfigureAwait(false);
            }
        }
        async Task WalkCoreAsync(string directory, PilotDirectoryIdentity identity)
        {
            ct.ThrowIfCancellationRequested();
            lease.VerifyBinding();
            var absolute = Path.Combine(_root.RootPath, directory.Replace('/', Path.DirectorySeparatorChar));
            // Native enumeration retains one 64-KiB buffer. DFS retains only a bounded page per
            // ancestor, never the whole tree or a breadth-first million-directory frontier.
            using var entries = WindowsDirectoryListing.List(absolute, identity).GetEnumerator();
            while (true)
            {
                var listed = new Dictionary<string, DirectoryEntryInfo>(StringComparer.OrdinalIgnoreCase);
                var paths = new List<string>(256);
                while (paths.Count < 256 && entries.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = entries.Current;
                    var full = Path.Combine(absolute, entry.Name);
                    listed.Add(full, entry);
                    paths.Add(PilotPath.NormalizeRelative(Path.GetRelativePath(_root.RootPath, full)));
                }
                if (paths.Count == 0) break;
                var page = await ReadPageAsync(paths, listed).ConfigureAwait(false);
                foreach (var folder in page.Where(CanTraverse))
                    await WalkAsync(folder.Path, Identity(folder)).ConfigureAwait(false);
            }
        }
        async Task<IReadOnlyList<ItemState>> ReadPageAsync(IReadOnlyList<string> paths,
            IReadOnlyDictionary<string, DirectoryEntryInfo>? listed)
        {
            var exact = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var scan = await ScanAsync(lease, ct, exact.Contains, paths, progress,
                exactPaths: exact, listedMetadata: listed).ConfigureAwait(false);
            var snapshot = BuildSnapshot(0, _root.RootId, "", scan, exact.Contains);
            var rows = snapshot.Items.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var decision in scan.Decisions)
            {
                var key = PairPath.IsSelfOrUnder(decision.RelativePath, path) ? decision.RelativePath : path;
                var item = rows.GetValueOrDefault(key) ?? new ItemState(key, ItemKind.Folder, "", null, null, 0,
                    DateTimeOffset.UnixEpoch, ItemFlags.None) { ObservationKindUnknown = true };
                rows[key] = item with { Flags = item.Flags | ItemFlags.Unreadable, IncompleteReason = decision.Reason };
            }
            foreach (var owned in paths.Where(p => LocalItems.IsExcluded(p, _productOwnedRelative)))
                rows[owned] = new ItemState(owned, ItemKind.Folder, "", null, null, 0,
                    DateTimeOffset.UnixEpoch, ItemFlags.Unreadable) { IncompleteReason = "product-owned path", ObservationKindUnknown = true };
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unknown = new List<ItemState>();
            foreach (var item in rows.Values.Where(item => item.Kind == ItemKind.File && item.Flags == ItemFlags.None))
            {
                var prior = _known.ByObject(_root.RootId, item.ObjectId);
                if (prior is { Hash: not null, Flags: ItemFlags.None } && prior.Size == item.Size &&
                    prior.Mtime.UtcTicks == item.Mtime.UtcTicks) { hashes[item.Path] = prior.Hash; reused++; }
                else
                {
                    var previous = _known.Live(_root.RootId, [item.Path]).FirstOrDefault();
                    if (_known.DeferIndexObservation(_root.RootId, _side, item, previous,
                        "review content proof pending; independent indexing continues beyond review budget")) unknown.Add(item);
                }
            }
            // VERIFY owns all reads/publication, including previously unseen children. Its budget
            // leaves durable indexing work; no review path opens content or hydrates a pointer.
            await HashProofsAsync(unknown, budget, hashes, ct).ConfigureAwait(false);
            var result = rows.Values.Select(item => item.Kind == ItemKind.File && item.Flags == ItemFlags.None
                ? hashes.TryGetValue(item.Path, out var hash) ? item with { Hash = hash } :
                    item with { Flags = ItemFlags.Unreadable, IncompleteReason = "Content proof unavailable; Retry after indexing or Review again." }
                : item).ToArray();
            foreach (var page in result.Chunk(256)) await accept(page).ConfigureAwait(false);
            done += result.Length;
            ProgressGuard.Report(progress, $"Review {done:N0} items; hashed {budget.Items:N0}, reused {reused:N0}", ct);
            return result;
        }
    }

    /// <inheritdoc/>
    public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
    {
        var gate = (_known as ILocalWalkStore)?.LocalWalkGate(_root.RootId);
        if (gate is not null) await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await FullCoreAsync(gen, include, p, ct).ConfigureAwait(false); }
        finally { gate?.Release(); }
    }

    private async Task<Snapshot> FullCoreAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
    {
        var rootId = _root.RootId;
        if (_pairId is not null && (WalkCheckpointForTest is null || CapacityQueryForTest is not null)) ObserveCapacity(null);
        PairPause.Check();
        var (checkpoint, journalDeleted) = ReadWalkCheckpointState();
        var sql = _known as ILocalWalkStore;
        if (checkpoint is not null && sql?.GapInventoryPending(rootId) == true &&
            NtfsUsnCheckpoint.TryParseCursor(_known.Cursor(rootId), out var savedJournal, out var savedNext) &&
            checkpoint.RequiresFullReconciliation(_known.Cursor(rootId)))
            BeginJournalGap(rootId, savedJournal, savedNext, checkpoint);
        if (checkpoint is not null && sql?.GapInventoryPending(rootId) == true &&
            NtfsUsnCheckpoint.TryParseCursor(_known.Cursor(rootId), out var replayJournal, out var replayNext) &&
            replayJournal == checkpoint.JournalId && ReplayTooLarge(replayNext, checkpoint.NextUsn))
        {
            // Re-protect the skipped interval BEFORE the one fresh metadata walk. A new
            // epoch invalidates old discharges; the store preserves the oldest proof boundary.
            ((ILocalGapStore)sql).BeginGap(rootId, checkpoint.Cursor, replayNext, true,
                "catch-up replay byte budget exceeded; fresh inventory with per-file protection");
            sql.ForgetLocalWalk(rootId);
            RunTrace.Emit($"local catch-up bounded ({rootId}): fresh protected walk, no oversized journal replay");
        }
        if (checkpoint is null) { sql?.ForgetLocalWalk(rootId); sql = null; }
        var options = JsonSerializer.Serialize(new { _settings.ReplaceOnlineOnlyStubs,
            ProductOwned = _productOwnedPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray() });
        var cursor = sql?.BeginLocalWalk(_root, options, checkpoint) ?? checkpoint?.Cursor ?? _known.Cursor(rootId) ?? string.Empty;
        BeginFullGap(rootId, cursor, journalDeleted);
        PilotDirectoryLease lease;
        try
        {
            lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            return WholeIncomplete(gen, rootId);
        }
        using (lease)
        {
            PilotLocalScan scan;
            try
            {
                try
                {
                    scan = await ScanAsync(lease, ct, include, refreshPaths: null, p,
                        sql is null ? null : new LocalWalkCheckpoint(sql, rootId, lease.Path)).ConfigureAwait(false);
                }
                catch (Exception error) when (sql is not null && error is LocalWalkPolicyChangedException or LocalWalkReplayInvalidException)
                {
                    ProgressGuard.Report(p, "Local walk checkpoint discarded; retrying fresh: " + error.Message, ct);
                    System.Diagnostics.Trace.WriteLine("Local walk checkpoint discarded: " + error.Message);
                    sql.ForgetLocalWalk(rootId);
                    checkpoint = ReadWalkCheckpoint();
                    cursor = checkpoint is null ? _known.Cursor(rootId) ?? string.Empty : sql.BeginLocalWalk(_root, options, checkpoint);
                    scan = await ScanAsync(lease, ct, include, refreshPaths: null, p,
                        checkpoint is null ? null : new LocalWalkCheckpoint(sql, rootId, lease.Path)).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (IsRootUnavailable(error))
            {
                return WholeIncomplete(gen, rootId);
            }
            scan = await ApplyHistoryAsync(scan, include, ct).ConfigureAwait(false);
            var snapshot = BuildSnapshot(gen, rootId, cursor, scan, include);
            sql?.RetireLocalWalk(rootId);
            return snapshot;
        }
    }

    internal Task<Delta> RehashAsync(PilotDirectoryLease lease, Delta delta, HashSet<string> written,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var candidates = delta.Changes.Where(change => change.State is { } state &&
            VerificationBudget.NeedsWriteProof(state, () => CatchUpByObject(_root.RootId, state.ObjectId), written.Contains(state.ObjectId)))
            .Select(change => change.State!).ToArray();
        // Journal evidence is durable BEFORE returning a cursor. Sync never opens contents here.
        _known.RequireRehashBatch(_root.RootId, candidates, "content verification pending after journal write");
        var pending = candidates.Select(item => item.ObjectId).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(delta with { Changes = delta.Changes.Select(change => change.State is { } state && pending.Contains(state.ObjectId)
            ? change with { State = state with { Hash = null, Flags = state.Flags | ItemFlags.NeedsRehash } } : change).ToArray() });
    }

    /// <inheritdoc/>
    public Task VerifyPendingAsync(Func<string, bool> include, Action<string> touched, CancellationToken ct)
    {
        // The host indexer publishes directly to planning_deferred. Sync reads that queue only.
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Independent indexing pass; never called from sync's foreground verification stage.
    /// Admission is bounded between files. One oversized/slow file may finish; cancellation restarts it
    /// from zero next pass. No partial hash is evidence and no timer abandons an active reader.</summary>
    internal Task<int> VerifyIndexingPassAsync(CancellationToken ct) => VerifyIndexingPassAsync(_ => true, ct);
    internal Func<TimeSpan>? VerificationElapsedForTest { get; set; }

    internal async Task<int> VerifyIndexingPassAsync(Func<string, bool> include, CancellationToken ct)
    {
        var budget = new VerificationBudget(VerificationElapsedForTest);
        var backlog = _known.RehashPendingCount(_root.RootId);
        VerificationProgress?.Invoke(0, backlog);
        var through = _known.RehashWatermark(_root.RootId);
        if (through == 0) return 0;
        var io = GapIo ??= new LocalGapIo(_known, _settings, _productOwnedPaths, time: _clock);
        while (budget.CanStart)
        {
            ct.ThrowIfCancellationRequested();
            if (io.Paused) break;
            var page = _known.RehashCandidates(_root.RootId, 1, through, needsVerification: true);
            if (page.Count == 0) break;
            var item = page[0];
            if (!budget.Admit(item.Size)) break;
            VerificationProgress?.Invoke(budget.Items, backlog);
            var revision = _known.RehashRevision(_root.RootId, item.ObjectId);
            if (!include(item.Path) || io.Excluded(_root, item))
            {
                _known.FailIndexingRehash(_root.RootId, item, revision, "content verification deferred by current selection or product-owned exclusion");
                continue;
            }
            LocalGapHashResult result;
            try { result = await io.HashAsync(_root, item, ct).ConfigureAwait(false); }
            catch (Exception error) when (IsRootUnavailable(error))
            {
                // Root authority is unknown: retain the already-durable queue and stop this pass.
                _known.RecordIndexFailure(_root.RootId, error.Message);
                break;
            }
            ct.ThrowIfCancellationRequested();
            if (result.Outcome is LocalGapHashOutcome.Paused or LocalGapHashOutcome.Disabled) break;
            if (result.QuarantineCode is { } code)
                PersistQuarantine(new PilotDecision(item.Path, PilotDecisionKind.Blocked, result.Reason ?? "content reader reported quarantine")
                    { QuarantineCode = code, QuarantineFile = true });
            if (result is { Outcome: LocalGapHashOutcome.Verified, Hash: { } hash, Proof: { } proof } &&
                LocalGapWorker.Matches(item, proof.File) &&
                _known.CompleteIndexingRehash(_root.RootId, item, hash, revision))
                CompletePointerVerification(item);
            else if (result.Outcome != LocalGapHashOutcome.IdentityChanged)
                _known.FailIndexingRehash(_root.RootId, item, revision,
                    result.Reason ?? "content or stored identity changed during verification; retry on next indexing pass");
        }
        return budget.Items;
    }

    internal static IReadOnlyList<string> RefreshDeletionScopes(PilotLocalScan scan, IReadOnlyList<string> paths,
        IReadOnlySet<string>? exactPaths) => paths.Where(path => exactPaths?.Contains(path) != true ||
            !scan.Entries.TryGetValue(path, out var entry) || entry.Kind != PilotEntryKind.Directory).ToArray();

    internal IReadOnlyList<string> WithDefenderDeferredPaths(IReadOnlyList<string> watched, string rootId) =>
        watched.Concat(_known.DefenderDeferredPaths(rootId, Math.Max(0, _settings.LocalDeltaMaxPaths - watched.Count)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Runs the refresh and delta construction for explicit paths; exposed for checks without a live journal.</summary>
    internal async Task<Delta> ChangesForPathsAsync(IReadOnlyList<string> paths, string cursor, long gen,
        Func<string, bool> include, CancellationToken ct)
    {
        PilotDirectoryLease lease;
        try
        {
            // The seam models the lease open itself, so it stays inside the guarded region.
            BeforeTargetedRefreshLeaseForTest?.Invoke();
            lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            // Finding 4: a root that cannot be opened for a targeted re-read is read debt, exactly like a
            // failed listing: report the scopes incomplete so the caller keeps their retry and notice
            // instead of failing the whole run. Follow-up 3: carry the real cause (exception kind and
            // message) so the notice explains why instead of the generic "could not be completely read".
            var cause = error.GetType().Name +
                (string.IsNullOrWhiteSpace(error.Message) ? "" : ": " + error.Message);
            return Invalid(gen, _root.RootId, cursor,
                "The folder could not be opened for a targeted re-read (" + cause + ").");
        }
        using (lease)
            return await RefreshAsync(lease, gen, cursor, cursor, include, paths, ct).ConfigureAwait(false);
    }

    /// <summary>Check seam: runs in <see cref="ChangesForPathsAsync"/> immediately before the root lease is
    /// opened, so a check can model a root that becomes unavailable after the run's scan succeeded. Null in
    /// production.</summary>
    internal Action? BeforeTargetedRefreshLeaseForTest { get; set; }

    /// <summary>Check seam: runs after the refresh ancestors are held and the scan time is sampled, before listing.
    /// Its only reader is LocalDeltaChecks (ancestor-swap refusal and deletion-time sampling order); null in production.</summary>
    internal Action? BeforeRefreshScan { get; set; }
    internal Action? AfterRefreshScan { get; set; }

    /// <summary>
    /// Always empty: a local deletion time is the scan time it was first seen missing (decision 4), which needs an
    /// earlier scan that a first-sync preview never persists; an unknown time keeps and restores the survivor.
    /// </summary>
    // Backlog: first-sync local deletion evidence (pending owner decision).
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(new Dictionary<string, DateTimeOffset>());

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await HashProofsAsync(_known.Live(_root.RootId, paths), new VerificationBudget(), result, ct).ConfigureAwait(false);
        return result;
    }

    // The only ordinary content-proof surface. Actual content IO is owned by LocalGapIo, shared
    // with background indexing. The bounded request is an effect proof, not a full inventory drain.
    private async Task HashProofsAsync(IReadOnlyList<ItemState> rows, VerificationBudget budget,
        Dictionary<string, string> hashes, CancellationToken ct)
    {
        var io = GapIo ??= new LocalGapIo(_known, _settings, _productOwnedPaths, time: _clock);
        foreach (var item in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind != ItemKind.File || (item.Flags & VerificationBudget.BlockedFlags) != 0 || io.Excluded(_root, item)) continue;
            // Persist before reading. Missing/paused/over-budget proof is durable indexing work;
            // the caller owns its per-effect failure notice and may proceed with unrelated effects.
            _known.RequireRehash(_root.RootId, item, "content proof pending");
            if (!budget.Admit(item.Size)) continue;
            var revision = _known.RehashRevision(_root.RootId, item.ObjectId);
            LocalGapHashResult proof;
            try { proof = await io.HashAsync(_root, item, ct).ConfigureAwait(false); }
            catch (Exception error) when (IsRootUnavailable(error))
            {
                _known.FailIndexingRehash(_root.RootId, item, revision, error.Message);
                continue;
            }
            ct.ThrowIfCancellationRequested();
            if (proof.QuarantineCode is { } code)
                PersistQuarantine(new PilotDecision(item.Path, PilotDecisionKind.Blocked, proof.Reason ?? "content reader reported quarantine")
                    { QuarantineCode = code, QuarantineFile = true });
            if (proof is { Outcome: LocalGapHashOutcome.Verified, Hash: { } hash, Proof: { } evidence })
            {
                if (!LocalGapWorker.Matches(item, evidence.File))
                    RunTrace.Emit($"content proof rejected ({_root.RootId}): {item.Path}: native identity or metadata changed");
                else if (_known.CompleteIndexingRehash(_root.RootId, item, hash, revision))
                {
                    hashes[item.Path] = hash;
                    CompletePointerVerification(item);
                }
                else
                    RunTrace.Emit($"content proof publication rejected ({_root.RootId}): {item.Path}: captured-revision={revision}; current-revision={_known.RehashRevision(_root.RootId, item.ObjectId)}; newer obligation or observation");
            }
            else
            {
                RunTrace.Emit($"content proof unavailable ({_root.RootId}): {item.Path}: outcome={proof.Outcome}; reason={proof.Reason ?? "not supplied"}");
                if (proof.Outcome == LocalGapHashOutcome.Unverifiable)
                    _known.FailIndexingRehash(_root.RootId, item, revision, proof.Reason ?? "content proof unavailable or changed");
            }
        }
    }

    internal delegate bool JournalRead(string root, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
        out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct);
    internal JournalRead? JournalReadForTest { get; set; }
    internal Func<(NtfsUsnCheckpoint? Checkpoint, bool Deleted)>? WalkCheckpointForTest { get; set; }
    internal Action<string>? DirectoryCompletedForTest { get; set; }
    private NtfsUsnCheckpoint? ReadWalkCheckpoint() => ReadWalkCheckpointState().Checkpoint;
    private (NtfsUsnCheckpoint? Checkpoint, bool Deleted) ReadWalkCheckpointState()
    {
        if (WalkCheckpointForTest is { } read) return read();
        NtfsUsnJournal.TryReadCheckpoint(_root.RootPath, out var checkpoint, out var deleted);
        return (checkpoint, deleted);
    }

    private Task<PilotLocalScan> ScanAsync(PilotDirectoryLease lease, CancellationToken ct, Func<string, bool> include,
        IReadOnlyList<string>? refreshPaths, IProgress<string>? p, LocalWalkCheckpoint? walk = null, IReadOnlySet<string>? exactPaths = null,
        IReadOnlyDictionary<string, DirectoryEntryInfo>? listedMetadata = null)
    {
        PairPause.Check();
        // Empty is the root-coverage retry sentinel, not a child path. Passing it to
        // explicit-child normalization rejects the retry before any directory is read.
        if (refreshPaths?.Contains(string.Empty) == true) refreshPaths = null;
        return new PilotLocalScanner { MetadataOnly = true, CloudFilesAware = true,
            ReplaceOnlineOnlyStubs = _settings.ReplaceOnlineOnlyStubs, WalkCheckpoint = walk, ListedMetadata = listedMetadata,
            BetweenDirectories = refreshPaths is null ? PairPause.Check : null,
            DirectoryCompletedForTest = DirectoryCompletedForTest,
            OwnedPlaceholderProbeForTest = OwnedPlaceholderProbeForTest,
            ClassifyOwnedPointer = OwnedPointerClassifier(),
            KnownOwnedPointerPath = KnownOwnedPointerPath,
            ReadOwnedPlaceholderForTest = ReadOwnedPlaceholderForTest }.ScanAsync(lease, _productOwnedPaths, ct,
            includePath: path => include(path) && !LocalItems.IsExcluded(path, _productOwnedRelative),
            traverseDirectory: path => include(path) && !LocalItems.IsExcluded(path, _productOwnedRelative) && exactPaths?.Contains(path) != true,
            onInventory: p is null
                ? null
                : (path, found) => { ProgressGuard.Report(p, $"{found:N0} items: {path}", ct); return Task.CompletedTask; },
            refreshPaths: refreshPaths, expectedPairId: _expectedPairId, contentProvider: OwnedContentProvider);
    }

    private string? OwnedContentProvider => _expectedPairId is { } pairId && _known.Pair(pairId) is { } pair
        ? (pair.A.RootId == _root.RootId ? pair.B.Provider : pair.A.Provider) : null;

    /// <summary>Observes each owned-placeholder probe the scanner would take; null in production.</summary>
    internal Action<string>? OwnedPlaceholderProbeForTest { get; set; }
    internal Func<string, WindowsCloudPlaceholderInfo?>? ReadOwnedPlaceholderForTest { get; set; }

    private static bool UnderHeldDescendant(string path, IReadOnlySet<string> heldPaths)
    {
        if (path.Length == 0) return false;
        var slash = path.LastIndexOf('/');
        return UnderAny(slash < 0 ? "" : path[..slash], heldPaths);
    }

    internal Snapshot BuildSnapshot(long gen, string rootId, string cursor, PilotLocalScan scan, Func<string, bool> include)
    {
        var items = new List<ItemState>();
        foreach (var entry in scan.Entries.Values)
            if (include(entry.RelativePath) && !LocalItems.IsExcluded(entry.RelativePath, _productOwnedRelative))
                items.Add(LocalItems.FromScan(entry));
        var heldPaths = scan.Decisions.Where(d => d.IsQuarantine).Select(d => d.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        items.RemoveAll(i => heldPaths.Contains(i.Path));
        foreach (var decision in scan.Decisions.Where(d => d.IsQuarantine)) items.Add(PersistQuarantine(decision));
        items.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        var unobservedPointers = scan.Decisions.Where(d => d.Reason == OwnedPointerProof.ObservationMessage)
            .Select(d => d.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var complete = scan.Complete; // The absence callback must not retain the scan's entry graph.
        var incomplete = complete ? DecisionPaths(scan, include) : [string.Empty];
        // A handled quarantine is not a run-wide incomplete scan, but its unobserved subtree
        // must remain untouched in storage until an explicit retry obtains fresh evidence.
        return new Snapshot(rootId, _side, gen, cursor, items, incomplete)
            { IncludesPath = path => include(path) && !UnderHeldDescendant(path, heldPaths) && !unobservedPointers.Contains(path), IncompleteReasons = DecisionReasons(scan, include),
                VerifyAbsence = HasOnlyHistoryHolds(scan)
                    ? item => HistoryListingAbsence(item, complete, include, incomplete) : null,
                OpenAbsenceVerifier = HasOnlyHistoryHolds(scan) ? null : () => new LocalFileAbsence(_root.RootPath, _rootIdentity) };
    }

    internal Delta BuildDelta(long gen, string rootId, string cursor, string throughCursor, PilotLocalScan scan,
        IReadOnlyList<string> paths, Func<string, bool> include, DateTimeOffset scanTime, IReadOnlyList<string> extraIncomplete)
    {
        var decisionPaths = extraIncomplete.Count == 0
            ? DecisionPaths(scan, include)
            : DecisionPaths(scan, include).Concat(extraIncomplete.Where(path => path.Length == 0 || include(path))).Distinct(StringComparer.Ordinal).ToArray();
        var changes = new List<Change>();
        foreach (var entry in scan.Entries.Values)
        {
            if (!include(entry.RelativePath) || LocalItems.IsExcluded(entry.RelativePath, _productOwnedRelative) ||
                LocalItems.InsideAny(entry.RelativePath, decisionPaths)) continue;
            var state = LocalItems.FromScan(entry);
            changes.Add(new Change(ChangeKind.Upsert, state.ObjectId, state.Path, state, null));
        }
        var heldPaths = scan.Decisions.Where(d => d.IsQuarantine).Select(d => d.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changes.RemoveAll(c => heldPaths.Contains(c.Path));
        foreach (var decision in scan.Decisions.Where(d => d.IsQuarantine))
        {
            var held = PersistQuarantine(decision);
            changes.Add(new Change(ChangeKind.Upsert, held.ObjectId, held.Path, held, null));
        }
        var incompletePaths = decisionPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var complete = scan.Complete;
        var historyOnly = extraIncomplete.Count == 0 && HasOnlyHistoryHolds(scan);
        var absence = new Snapshot(rootId, _side, gen, throughCursor, [], decisionPaths)
            { VerifyAbsence = historyOnly
                    ? item => HistoryListingAbsence(item, complete,
                        path => include(path) && paths.Any(scope => PairPath.IsSelfOrUnder(path, scope)), decisionPaths) : null,
                OpenAbsenceVerifier = historyOnly ? null : () => new LocalFileAbsence(_root.RootPath, _rootIdentity) };
        using var verifier = decisionPaths.Count == 0 ? null : absence.OpenAbsenceVerifier?.Invoke();
        var checking = verifier is null ? absence : absence with { VerifyAbsence = verifier.Check };
        var seenIds = changes.Select(change => change.ObjectId).ToHashSet(StringComparer.Ordinal);
        changes.AddRange(LocalDeletes.Build(_known, rootId, paths, scan,
            path => include(path) && !LocalItems.IsExcluded(path, _productOwnedRelative) &&
                !UnderAny(path, heldPaths) && !UnderAny(path, incompletePaths),
            scanTime, row => seenIds.Contains(row.ObjectId) || checking.CanCloseAbsent(row), seenIds));
        return new Delta(rootId, _side, gen, cursor, throughCursor, changes, false, decisionPaths)
            { IncludesPath = include, IncompleteReasons = DecisionReasons(scan, include),
                PendingDeletionEvidence = absence.PendingDeletionEvidence, DisplacementEvidence = absence };
    }

    // History availability is not identity evidence. Listing omission proves absence only in
    // the complete, included, observed part of this scan, never within a held/incomplete subtree.
    private static FileAbsence HistoryListingAbsence(ItemState item, bool complete,
        Func<string, bool> include, IReadOnlyList<string> incomplete) =>
        complete && include(item.Path) && !LocalItems.InsideAny(item.Path, incomplete)
            ? FileAbsence.Gone : FileAbsence.Unknown;

    // History holds describe missing known items, not an unenumerated directory.
    private static bool HasOnlyHistoryHolds(PilotLocalScan scan) => scan.Complete &&
        scan.Decisions.All(d => d.DefenderHistoryUnknown && d.QuarantineCode is null && !d.DefenderHistoryMatch);

    private static IReadOnlyDictionary<string, string> DecisionReasons(PilotLocalScan scan, Func<string, bool> include) =>
        scan.Decisions.Where(decision => decision.RelativePath.Length == 0 || include(decision.RelativePath))
            .GroupBy(decision => decision.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => string.Join("; ", group.Select(decision => decision.Reason).Distinct()), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> DecisionPaths(PilotLocalScan scan, Func<string, bool> include) =>
        scan.Decisions.Where(decision => !decision.IsQuarantine).Select(decision => decision.RelativePath).Where(path => path.Length == 0 || include(path)).Distinct(StringComparer.Ordinal).ToArray();

    private ItemState PersistQuarantine(PilotDecision decision)
    {
        var item = _known.Live(_root.RootId, [decision.RelativePath]).FirstOrDefault() ??
            new ItemState(decision.RelativePath, decision.QuarantineFile ? ItemKind.File : ItemKind.Folder, "quarantine-" + ShortId.Hex16(_root.RootId, decision.RelativePath),
                null, null, 0, DateTimeOffset.MinValue, ItemFlags.None);
        item = item with { Flags = item.Flags | ItemFlags.Quarantined };
        foreach (var pair in _known.Pairs().Where(p => p.A.RootId == _root.RootId || p.B.RootId == _root.RootId))
        {
            var side = pair.A.RootId == _root.RootId ? Side.A : Side.B;
            _known.AddNotices([new Notice(pair.PairId, 30, NoticeKind.UserDecision, item.Path,
                decision.DefenderHistoryMatch || decision.DefenderHistoryUnknown ? decision.Reason : QuarantineFailure.Describe(item.Path, new Win32Exception(decision.QuarantineCode!.Value)),
                QuarantineFailure.NoticeKey, Quarantine: new(side,
                    _known.Live(side == Side.A ? pair.B.RootId : pair.A.RootId, [item.Path]).FirstOrDefault(), item, true))]);
        }
        _known.HoldQuarantine(_root.RootId, item);
        return item;
    }

    private Snapshot WholeIncomplete(long gen, string rootId) =>
        new(rootId, _side, gen, string.Empty, Array.Empty<ItemState>(), new[] { string.Empty });

    internal string? LastFallbackReason { get; private set; }

    // Shared by the journal branch and its synthetic wrap regression. The cursor is the
    // next UNPROCESSED USN: equality is a candidate, and journal sizing is not identity.
    internal void BeginJournalGap(string rootId, ulong journalId, long next, NtfsUsnCheckpoint through)
    {
        if (_pairId is not null && (WalkCheckpointForTest is null || CapacityQueryForTest is not null) && journalId == through.JournalId && next < Math.Max(through.FirstUsn, through.LowestValidUsn))
            ObserveCapacity(through.Cursor, overflow: true);
        (_known as ILocalGapStore)?.BeginGap(rootId, through.Cursor, next,
            journalId == through.JournalId && next <= through.NextUsn,
            journalId != through.JournalId ? "journal replaced" : next > through.NextUsn ? "cursor ahead of journal" : "journal wrapped past saved cursor");
    }

    internal void BeginFullGap(string rootId, string cursor, bool journalDeleted)
    {
        if (_known is ILocalGapStore gaps && (journalDeleted ||
            !NtfsUsnCheckpoint.TryParseCursor(_known.Cursor(rootId), out _, out _)) && !gaps.IsIncompleteInitialInventory(rootId))
            gaps.BeginGap(rootId, journalDeleted ? "" : cursor, 0, trustFileUsn: false,
                journalDeleted ? "NTFS journal deleted or disabled" : "saved journal cursor missing or unreadable");
    }

    internal Delta JournalReadFailed(long gen, string rootId, string cursor, NtfsUsnCheckpoint through, long next, string reason,
        NtfsUsnReadFailure kind = NtfsUsnReadFailure.Transient)
    {
        if (kind == NtfsUsnReadFailure.Permanent)
        {
            // No safely decoded scope exists. Protect the root before abandoning unavailable history;
            // the normal full-walk publication supplies the fresh cursor, never this failed read.
            (_known as ILocalGapStore)?.BeginGap(rootId, through.Cursor, next, trustFileUsn: false, reason);
            _known.RecordIndexFailure(rootId, reason);
            return Invalid(gen, rootId, cursor, "permanent journal record failure: " + reason);
        }
        if (kind == NtfsUsnReadFailure.OverLimit)
        {
            if (_pairId is not null && (WalkCheckpointForTest is null || CapacityQueryForTest is not null)) ObserveCapacity(cursor, overflow: true);
            // This range will only grow: retrying the same cursor can never shrink it.
            (_known as ILocalGapStore)?.BeginGap(rootId, through.Cursor, next, trustFileUsn: true,
                "journal read exceeded bounded range; journal intact, metadata rescan required");
            return Invalid(gen, rootId, cursor, "journal range over limit: metadata rescan and trusted per-file verification");
        }
        return RetryJournal(gen, rootId, cursor, reason);
    }

    internal Delta RetryJournal(long gen, string rootId, string cursor, string reason)
    {
        LastFallbackReason = reason;
        RunTrace.Emit($"local journal retry next sync ({rootId}): {reason}");
        // Preserve the exact cursor, and mark this run unknown rather than inventing deletion evidence.
        return new(rootId, _side, gen, cursor, cursor, Array.Empty<Change>(), false, [string.Empty]);
    }

    private Delta Invalid(long gen, string rootId, string cursor, string reason)
    {
        LastFallbackReason = reason;
        RunTrace.Emit($"local full reconciliation ({rootId}): {reason}");
        // A caller that keeps the scopes as read debt (RetryScanScopesAsync) reads this to explain the
        // failure; callers that rescan ignore it.
        return new(rootId, _side, gen, cursor, cursor, Array.Empty<Change>(), true, Array.Empty<string>())
        { IncompleteReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = reason } };
    }

    private static bool IsRootUnavailable(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception;
}
