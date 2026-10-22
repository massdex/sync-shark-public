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
    private readonly V2Settings _settings;
    private readonly TimeProvider _clock;
    private readonly uint _volume;
    private readonly ulong _rootFileId;
    private readonly PilotDirectoryIdentity _rootIdentity;
    private readonly long _gapThrough;
    private readonly NtfsUsnCheckpoint? _gapRunStart;
    // Set before inventory reads; previews may persist recommendations only in their isolated store.
    internal bool ObserveOnly { get; set; }
    internal Func<string, long[]?>? CapacityQueryForTest { get; set; }
    internal Action<SqliteStateStore, SqliteStateStore.JournalPending, SyncDiscoveryRoot>? CapacityQueueForTest { get; set; }
    private void ObserveCapacity(string? cursor, bool overflow = false) =>
        JournalSizing.TryObserve(_known, _pairId!, _root.RootPath, _volume, _rootFileId, cursor,
            _clock.GetUtcNow(), overflow, ObserveOnly, CapacityQueryForTest, CapacityQueueForTest);

    internal LocalGapWorker? GapWorker { get; set; }
    internal ILocalGapIo? GapIo { get; set; }

    /// <summary>Binds one local root; the saved root object id must parse into its NTFS identity.</summary>
    public LocalInventory(RootDef root, Side side, IStateStore known,
        IReadOnlySet<string> productOwnedPaths, V2Settings settings, TimeProvider clock, IDefenderHistory? history = null, string? pairId = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _side = side;
        _history = history;
        _pairId = pairId;
        _known = known ?? throw new ArgumentNullException(nameof(known));
        _expectedPairId = pairId is not null && _known is SqliteStateStore store &&
            (store.HasOwnedPointers(root.RootId) || store.StorageRegistrations().Any(entry => entry.RootId == root.RootId))
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
        // Production constructs one inventory per run: worker results newer than this cut wait for the next run.
        _gapThrough = (known as ILocalGapStore)?.GapResultWatermark(root.RootId) ?? 0;
        if (_gapThrough > 0 && NtfsUsnJournal.TryReadCheckpoint(root.RootPath, out var gapStart))
            _gapRunStart = gapStart;
    }

    public async Task ReadSubtreeAsync(string path, Func<IReadOnlyList<ItemState>, Task> accept, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        using var lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        var scan = await ScanAsync(lease, ct, p => PairPath.IsSelfOrUnder(p, path) || PairPath.IsUnder(path, p), [path], progress);
        var snapshot = BuildSnapshot(0, _root.RootId, "", scan, p => PairPath.IsSelfOrUnder(p, path));
        var incomplete = scan.Decisions.GroupBy(d => PairPath.IsSelfOrUnder(d.RelativePath, path) ? d.RelativePath : path,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join("; ", g.Select(d => d.Reason).Distinct()), StringComparer.OrdinalIgnoreCase);
        foreach (var owned in _productOwnedRelative.Where(p => PairPath.IsSelfOrUnder(p, path)))
            incomplete[owned] = "product-owned path";
        var missing = incomplete.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? Incomplete(string itemPath)
        {
            if (incomplete.TryGetValue(itemPath, out var reason)) return reason;
            foreach (var parent in PairPath.Ancestors(itemPath))
                if (incomplete.TryGetValue(parent, out reason)) return reason;
            return null;
        }
        long done = 0, hashed = 0, reused = 0;
        foreach (var page in snapshot.Items.Chunk(256))
        {
            ct.ThrowIfCancellationRequested();
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unknown = new List<ItemState>();
            foreach (var item in page.Where(i => i.Kind == ItemKind.File && i.Flags == ItemFlags.None))
            {
                if (Incomplete(item.Path) is not null) continue;
                var prior = _known.ByObject(_root.RootId, item.ObjectId);
                if (prior is { Hash: not null, Flags: ItemFlags.None } && prior.ObjectId == item.ObjectId &&
                    prior.Size == item.Size && prior.Mtime.UtcTicks == item.Mtime.UtcTicks)
                { hashes[item.Path] = prior.Hash; reused++; }
                else unknown.Add(item);
            }
            if (unknown.Count != 0)
            {
                ProgressGuard.Report(progress, $"Hashing {hashed:N0} attempted; {unknown.Count:N0} pending in page; {reused:N0} reused", ct);
                var refused = await HashSharesAsync(lease, unknown, hashes, ct);
                if (refused.Count != 0) Accept(refused, await HashScanAsync(lease, refused, ct), hashes);
                hashed += unknown.Count;
            }
            await accept(page.Select(i =>
            {
                missing.Remove(i.Path);
                var reason = Incomplete(i.Path);
                if (reason is null && i.Kind == ItemKind.File && i.Flags == ItemFlags.None && !hashes.ContainsKey(i.Path))
                    reason = "content could not be verified (unreadable or changed while hashing)";
                return i with { Hash = i.Kind == ItemKind.File ? hashes.GetValueOrDefault(i.Path) : i.Hash,
                    Flags = reason is null ? i.Flags : i.Flags | ItemFlags.Unreadable, IncompleteReason = reason };
            }).ToArray());
            done += page.Length;
            ProgressGuard.Report(progress, $"Review {done:N0}/{snapshot.Items.Count:N0} items; hashed {hashed:N0}, reused {reused:N0}", ct);
        }
        // Enumeration can fail before emitting an entry. Keep an explicit sentinel at that path;
        // its parents are protected, but independent readable siblings remain reviewable (RQ-032/034).
        foreach (var page in incomplete.Where(p => missing.Contains(p.Key)).Chunk(256))
            await accept(page.Select(p => new ItemState(p.Key, ItemKind.Folder, "", null, null, 0,
                DateTimeOffset.UnixEpoch, ItemFlags.Unreadable) { IncompleteReason = p.Value, ObservationKindUnknown = true }).ToArray());
    }

    /// <inheritdoc/>
    public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
    {
        var gate = (_known as SqliteStateStore)?.LocalWalkGate(_root.RootId);
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
        var sql = _known as SqliteStateStore;
        if (checkpoint is not null && sql?.GapInventoryPending(rootId) == true &&
            NtfsUsnCheckpoint.TryParseCursor(_known.Cursor(rootId), out var savedJournal, out var savedNext) &&
            checkpoint.RequiresFullReconciliation(_known.Cursor(rootId)))
            BeginJournalGap(rootId, savedJournal, savedNext, checkpoint);
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
            sql?.ForgetLocalWalk(rootId);
            return snapshot;
        }
    }

    /// <inheritdoc/>
    public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
    {
        var rootId = _root.RootId;
        if (_known is SqliteStateStore sql && sql.HasLocalWalk(rootId))
            return Invalid(gen, rootId, cursor, "unfinished local full walk takes precedence");
        if (_pairId is not null && (WalkCheckpointForTest is null || CapacityQueryForTest is not null)) ObserveCapacity(cursor);
        if (!NtfsUsnCheckpoint.TryParseCursor(cursor, out var journalId, out var next)) return Invalid(gen, rootId, cursor, "saved cursor cannot be parsed");
        PilotDirectoryLease lease;
        try
        {
            lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            return Invalid(gen, rootId, cursor, "root lease failed: " + error.Message);
        }
        using (lease)
        {
            return await DeltaAsync(lease, cursor, gen, rootId, journalId, next, include, ct).ConfigureAwait(false);
        }
    }

    private async Task<Delta> DeltaAsync(PilotDirectoryLease lease, string cursor, long gen, string rootId,
        ulong journalId, long next, Func<string, bool> include, CancellationToken ct)
    {
        var (through, journalDeleted) = ReadWalkCheckpointState();
        if (through is null)
        {
            if (!journalDeleted) return RetryJournal(gen, rootId, cursor, "journal checkpoint temporarily unavailable");
            (_known as ILocalGapStore)?.BeginGap(rootId, "", 0, false, "NTFS journal deleted or disabled");
            return Invalid(gen, rootId, cursor, "journal deleted");
        }
        var from = new NtfsUsnCheckpoint(journalId, through.FirstUsn, next, through.LowestValidUsn);
        if (through.RequiresFullReconciliation(cursor))
        {
            BeginJournalGap(rootId, journalId, next, through);
            return Invalid(gen, rootId, cursor,
                journalId != through.JournalId ? "journal replaced" : next > through.NextUsn ? "cursor ahead of journal" : "journal gap: cursor precedes retained range");
        }
        if (!(JournalReadForTest ?? NtfsUsnJournal.TryReadChanges)(_root.RootPath, from, through, out var changes, out var readFailure, out var readFailureKind, ct))
        {
            return JournalReadFailed(gen, rootId, cursor, through, next, readFailure ?? "journal read failed", readFailureKind);
        }
        var names = new LocalJournalNames(_root, _known, changes, _settings.LocalDeltaMaxPaths);
        var fallbackReason = "resolved path limit exceeded";
        var context = new ResolveContext(_rootFileId, _volume,
            id => _known.ByObject(rootId, SyncLocalEndpointStore.ObjectId(_volume, id)),
            _settings.LocalDeltaMaxPaths, names.Find, names.Ambiguous, reason => fallbackReason = names.LastFailure ?? reason);
        var paths = await LocalDeltaResolver.ResolveAsync(changes, context, ct).ConfigureAwait(false);
        if (paths is null || paths.Count > _settings.LocalDeltaMaxPaths)
        {
            // Unlike a gap, this batch contains authoritative write evidence. Keep it through fallback.
            PreserveWrites();
            return Invalid(gen, rootId, cursor, fallbackReason);
        }
        var watched = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        paths = WithDefenderDeferredPaths(paths, rootId);
        var exact = paths.Where(path => !UnderAny(path, watched)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var delta = paths.Count == 0
            ? new Delta(rootId, _side, gen, cursor, through.Cursor, Array.Empty<Change>(), false, [])
            : await RefreshAsync(lease, gen, cursor, through.Cursor, include, paths, ct, exact).ConfigureAwait(false);
        // A moved-in directory may expose files with no individual journal record in this interval.
        // Validate those refreshed identities too, before upserts can mistake aliases for old move positions.
        AfterRefreshScan?.Invoke();
        foreach (var change in delta.Changes)
        {
            if (change.State is not { Kind: ItemKind.File } state) continue;
            ct.ThrowIfCancellationRequested();
            if (!names.Ambiguous(LocalItems.ParseObjectId(state.ObjectId).FileId, state, requirePresent: true)) continue;
            PreserveWrites();
            return Invalid(gen, rootId, cursor, names.LastFailure ?? "refreshed file has ambiguous hard-link identity");
        }
        var written = changes.Where(change => (change.Reason & 0x7) != 0)
            .Select(change => SyncLocalEndpointStore.ObjectId(_volume, change.FileId)).ToHashSet(StringComparer.Ordinal);
        return await RehashAsync(lease, delta, written, ct).ConfigureAwait(false);

        void PreserveWrites()
        {
            var writtenKnown = changes.Where(record => (record.Reason & 0x7) != 0).DistinctBy(record => record.FileId)
                .Select(record => _known.ByObject(rootId, SyncLocalEndpointStore.ObjectId(_volume, record.FileId)))
                .Where(item => item is { Kind: ItemKind.File } && !item.Flags.HasFlag(ItemFlags.OwnedPointer)).Select(item => item!);
            _known.RequireRehashBatch(rootId, writtenKnown, "journal data write requires full reconciliation before hashing");
        }
    }

    internal async Task<Delta> RehashAsync(PilotDirectoryLease lease, Delta delta, HashSet<string> written,
        CancellationToken ct)
    {
        // Metadata-only cloud-aware scanning emits hydrated files as ordinary items (CloudAvailability null).
        // Placeholder states from this path are online-only: never hydrate them just to verify content (decision 57).
        const ItemFlags blocked = ItemFlags.Locked | ItemFlags.Unreadable | ItemFlags.Ignored | ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.OwnedPointer;
        var candidates = delta.Changes.Where(change => change.State is { Kind: ItemKind.File } state &&
            (state.Flags & blocked) == 0 && _known.ByObject(_root.RootId, state.ObjectId) is { } prior &&
            ((prior.Flags & ItemFlags.NeedsRehash) != 0 ||
             written.Contains(state.ObjectId) && prior.Size == state.Size && prior.Mtime == state.Mtime))
            .Select(change => change.State!).ToArray();
        if (candidates.Length == 0) return delta;
        // Persist before opening; even cancellation/crash must not discard authoritative journal evidence.
        _known.RequireRehashBatch(_root.RootId, candidates, "content verification pending after journal write");
        candidates = candidates.Where(item => !item.Flags.HasFlag(ItemFlags.Quarantined) &&
            _known.ByObject(_root.RootId, item.ObjectId)?.Flags.HasFlag(ItemFlags.Quarantined) != true).ToArray();
        if (candidates.Length == 0) return delta;
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scan = await HashScanAsync(lease, candidates, ct).ConfigureAwait(false);
        foreach (var decision in scan.Decisions.Where(d => d.IsQuarantine)) PersistQuarantine(decision);
        Accept(candidates, scan, hashes);
        var completed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in candidates)
        {
            if (hashes.TryGetValue(item.Path, out var hash) && _known.CompleteRehash(_root.RootId, item, hash))
            {
                completed[item.Path] = hash;
                CompletePointerVerification(item);
            }
            else
            {
                var reason = scan.Decisions.FirstOrDefault(decision => decision.RelativePath == item.Path)?.Reason
                    ?? "content or stored identity changed during verification; retry on next sync";
                _known.RequireRehash(_root.RootId, item, reason);
                RunLog.Trace($"local rehash pending: {item.Path}: {reason}");
            }
        }
        var attempted = candidates.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return delta with { Changes = delta.Changes.Select(change => change.State is { } state && attempted.Contains(state.Path)
            ? change with { State = completed.TryGetValue(state.Path, out var hash)
                ? state with { Hash = hash, Flags = state.Flags & ~ItemFlags.NeedsRehash }
                : state with { Hash = null, Flags = state.Flags | ItemFlags.NeedsRehash } } : change).ToArray() };
    }

    /// <inheritdoc/>
    public async Task VerifyPendingAsync(Func<string, bool> include, Action<string> touched, CancellationToken ct)
    {
        var through = _known.RehashWatermark(_root.RootId);
        if (_known is ILocalGapStore gaps && GapIo is { } gapIo)
        {
            gaps.RetryGapFailures(_root.RootId);
            LocalGapWorker.Promote(gaps, _known, gapIo, _root, _gapThrough, _gapRunStart, include, touched, ct);
            if (!ObserveOnly && gaps.HasGapProtection(_root.RootId)) GapWorker?.Start(_root);
        }
        if (through == 0) return;
        PilotDirectoryLease lease;
        try { lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity); }
        catch (Exception error) when (IsRootUnavailable(error)) { return; }
        using (lease)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = _known.RehashCandidates(_root.RootId, 128, through);
                if (page.Count == 0) break;
                // Verified obligations replay their planning touches, not a foreground content read.
                foreach (var verified in page.Where(item => item.Hash is not null && (item.Flags & ItemFlags.NeedsRehash) == 0))
                    for (var offset = 0; ; offset += 128)
                    {
                        ct.ThrowIfCancellationRequested();
                        var aliases = _known.ObjectPaths(_root.RootId, verified.ObjectId, offset, 128);
                        foreach (var path in aliases) touched(path);
                        if (aliases.Count < 128) break;
                    }
                // Rotation occurs before filtering: excluded/blocked entries keep their reason and cannot spin.
                // Bit128 is the parallel quarantine lane's reserved item hold; it never auto-retries.
                var candidates = page.Where(item => (item.Hash is null || (item.Flags & ItemFlags.NeedsRehash) != 0) &&
                    item.Kind == ItemKind.File && include(item.Path) &&
                    !LocalItems.IsExcluded(item.Path, _productOwnedRelative) &&
                    (item.Flags & (ItemFlags.Ignored | ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.Quarantined)) == 0).ToArray();
                if (candidates.Length == 0) continue;
                PilotLocalScan scan;
                try { scan = await HashScanAsync(lease, candidates, ct).ConfigureAwait(false); }
                catch (Exception error) when (IsRootUnavailable(error)) { return; }
                foreach (var decision in scan.Decisions.Where(d => d.IsQuarantine)) PersistQuarantine(decision);
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Accept(candidates, scan, hashes);
                foreach (var item in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (hashes.TryGetValue(item.Path, out var hash) && _known.CompleteRehash(_root.RootId, item, hash))
                    {
                        CompletePointerVerification(item);
                        // Completion unholds an entity, not just its selected name. Report every alias in pages.
                        for (var offset = 0; ; offset += 128)
                        {
                            ct.ThrowIfCancellationRequested();
                            var aliases = _known.ObjectPaths(_root.RootId, item.ObjectId, offset, 128);
                            foreach (var path in aliases) touched(path);
                            if (aliases.Count < 128) break;
                        }
                    }
                    else
                    {
                        var reason = scan.Decisions.FirstOrDefault(decision => decision.RelativePath == item.Path)?.Reason
                            ?? "content or stored identity changed during verification; retry on next sync";
                        _known.RequireRehash(_root.RootId, item, reason);
                    }
                }
            }
        }
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
        using var lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        return await RefreshAsync(lease, gen, cursor, cursor, include, paths, ct).ConfigureAwait(false);
    }

    private async Task<Delta> RefreshAsync(PilotDirectoryLease lease, long gen, string cursor, string throughCursor,
        Func<string, bool> include, IReadOnlyList<string> paths, CancellationToken ct, IReadOnlySet<string>? exactPaths = null)
    {
        paths = paths.Where(path => include(path) && !LocalItems.IsExcluded(path, _productOwnedRelative)).ToArray();
        // The ancestors stay held until the listing completes, so none can be swapped after it was validated.
        using var guard = LocalItems.GuardRefreshPaths(_root.RootPath, _root.RootId, _known, paths);
        // The deletion time is the scan time it is first seen missing, so it is sampled before the scan.
        var scanTime = _clock.GetUtcNow();
        BeforeRefreshScan?.Invoke();
        PilotLocalScan scan;
        try
        {
            scan = await ScanAsync(lease, ct, include, guard.Safe, null, exactPaths: exactPaths).ConfigureAwait(false);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            return Invalid(gen, _root.RootId, cursor, "root unavailable during guarded refresh");
        }
        scan = scan with { Decisions = scan.Decisions.Concat(guard.Quarantine).ToArray() };
        var exactSafe = paths.Where(path => exactPaths?.Contains(path) == true &&
            !guard.Incomplete.Any(parent => PairPath.IsSelfOrUnder(path, parent))).ToArray();
        scan = await ApplyHistoryAsync(scan, include, ct,
            guard.Safe.Where(path => exactPaths?.Contains(path) != true).ToArray(), exactSafe).ConfigureAwait(false);
        var quarantined = scan.Decisions.Where(d => d.IsQuarantine).Select(d => d.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incomplete = guard.Incomplete.Where(path => !quarantined.Contains(path)).ToArray();
        // Observing only a queued folder itself is not evidence that its unseen children disappeared.
        var deletionScopes = RefreshDeletionScopes(scan, paths, exactPaths);
        return BuildDelta(gen, _root.RootId, cursor, throughCursor, scan, deletionScopes, include, scanTime, incomplete);
    }

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
        var rows = _known.Live(_root.RootId, paths);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rows.Count == 0) return result;
        PilotDirectoryLease lease;
        try
        {
            lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            return result;
        }
        using (lease)
        {
            try
            {
                var refused = await HashSharesAsync(lease, rows, result, ct).ConfigureAwait(false);
                // Two names of one physical file in different shares can collide on the exclusive open. Like the
                // serial scan's single sharing retry, a row refused in the parallel pass is retried once, alone.
                if (refused.Count > 0)
                {
                    var retryScan = await HashScanAsync(lease, refused, ct).ConfigureAwait(false);
                    foreach (var decision in retryScan.Decisions.Where(d => d.IsQuarantine)) PersistQuarantine(decision);
                    Accept(refused, retryScan, result);
                }
            }
            catch (Exception error) when (IsRootUnavailable(error))
            {
                return result;
            }
        }
        return result;
    }

    /// <summary>
    /// Hashes the rows with a bounded number of workers, each a serial verified scan over an interleaved share,
    /// accepts every unchanged result, and returns the rows the scans refused (blocked) without a hash.
    /// </summary>
    private async Task<IReadOnlyList<ItemState>> HashSharesAsync(PilotDirectoryLease lease, IReadOnlyList<ItemState> rows,
        Dictionary<string, string> result, CancellationToken ct)
    {
        var workers = Math.Clamp(_settings.LocalHashWorkers, 1, rows.Count);
        var shares = Enumerable.Range(0, workers)
            .Select(worker => rows.Where((_, index) => index % workers == worker).ToArray()).ToArray();
        var scans = await Task.WhenAll(shares.Select(share => Task.Run(() => HashScanAsync(lease, share, ct), ct)))
            .ConfigureAwait(false);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var worker = 0; worker < workers; worker++)
        {
            Accept(shares[worker], scans[worker], result);
            foreach (var decision in scans[worker].Decisions)
                if (decision.IsQuarantine) PersistQuarantine(decision);
                else blocked.Add(decision.RelativePath);
        }
        if (workers == 1) return Array.Empty<ItemState>(); // a lone serial scan already made its own retry
        return rows.Where(row => !result.ContainsKey(row.Path) && blocked.Contains(row.Path)).ToArray();
    }

    internal Func<IReadOnlyList<ItemState>, PilotLocalScan>? HashScanOverride { get; set; }

    private Task<PilotLocalScan> HashScanAsync(PilotDirectoryLease lease, IReadOnlyList<ItemState> rows,
        CancellationToken ct) =>
        HashScanOverride is { } scanOverride ? Task.FromResult(scanOverride(rows)) :
        new PilotLocalScanner { HashBufferBytes = _settings.LocalHashBufferBytes, CloudFilesAware = true,
            ReplaceOnlineOnlyStubs = _settings.ReplaceOnlineOnlyStubs,
            OwnedPlaceholderProbeForTest = OwnedPlaceholderProbeForTest,
            ClassifyOwnedPointer = OwnedPointerClassifier(),
            KnownOwnedPointerPath = KnownOwnedPointerPath,
            ReadOwnedPlaceholderForTest = ReadOwnedPlaceholderForTest }
            .ScanAsync(lease, _productOwnedPaths, ct, refreshPaths: rows.Select(row => row.Path).ToArray(),
                expectedPairId: _expectedPairId, contentProvider: OwnedContentProvider);

    /// <summary>Accepts a scanned hash only for a file whose identity, size and mtime still match its row.</summary>
    private static void Accept(IReadOnlyList<ItemState> rows, PilotLocalScan scan, Dictionary<string, string> result)
    {
        var byPath = new Dictionary<string, PilotLocalEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in scan.Entries.Values) byPath[entry.RelativePath] = entry;
        foreach (var row in rows)
        {
            if (!byPath.TryGetValue(row.Path, out var entry) || entry.Kind != PilotEntryKind.File ||
                entry.ContentHash is null)
                continue;
            var state = LocalItems.FromScan(entry);
            if (state.ObjectId == row.ObjectId && state.Size == row.Size && state.Mtime.UtcTicks == row.Mtime.UtcTicks)
                result[row.Path] = entry.ContentHash;
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
        IReadOnlyList<string>? refreshPaths, IProgress<string>? p, LocalWalkCheckpoint? walk = null, IReadOnlySet<string>? exactPaths = null)
    {
        PairPause.Check();
        // Empty is the root-coverage retry sentinel, not a child path. Passing it to
        // explicit-child normalization rejects the retry before any directory is read.
        if (refreshPaths?.Contains(string.Empty) == true) refreshPaths = null;
        return new PilotLocalScanner { MetadataOnly = true, CloudFilesAware = true,
            ReplaceOnlineOnlyStubs = _settings.ReplaceOnlineOnlyStubs, WalkCheckpoint = walk,
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
        // A handled quarantine is not a run-wide incomplete scan, but its unobserved subtree
        // must remain untouched in storage until an explicit retry obtains fresh evidence.
        return new Snapshot(rootId, _side, gen, cursor, items, scan.Complete ? DecisionPaths(scan, include) : [string.Empty])
            { IncludesPath = path => include(path) && !UnderHeldDescendant(path, heldPaths) && !unobservedPointers.Contains(path), IncompleteReasons = DecisionReasons(scan, include),
                VerifyAbsence = HasOnlyHistoryHolds(scan) ? _ => FileAbsence.Gone : null,
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
        var historyOnly = extraIncomplete.Count == 0 && HasOnlyHistoryHolds(scan);
        var absence = new Snapshot(rootId, _side, gen, throughCursor, [], decisionPaths)
            { VerifyAbsence = historyOnly ? _ => FileAbsence.Gone : null,
                OpenAbsenceVerifier = historyOnly ? null : () => new LocalFileAbsence(_root.RootPath, _rootIdentity) };
        using var verifier = decisionPaths.Count == 0 ? null : absence.OpenAbsenceVerifier?.Invoke();
        var checking = verifier is null ? absence : absence with { VerifyAbsence = verifier.Check };
        var seenIds = changes.Select(change => change.ObjectId).ToHashSet(StringComparer.Ordinal);
        changes.AddRange(LocalDeletes.Build(_known, rootId, paths, scan,
            path => include(path) && !LocalItems.IsExcluded(path, _productOwnedRelative) &&
                !UnderAny(path, heldPaths) && !UnderAny(path, incompletePaths),
            scanTime, row => seenIds.Contains(row.ObjectId) || checking.CanCloseAbsent(row)));
        return new Delta(rootId, _side, gen, cursor, throughCursor, changes, false, decisionPaths)
            { IncludesPath = include, IncompleteReasons = DecisionReasons(scan, include),
                PendingDeletionEvidence = absence.PendingDeletionEvidence, DisplacementEvidence = absence };
    }

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
        RunLog.Trace($"local journal retry next sync ({rootId}): {reason}");
        // Preserve the exact cursor, and mark this run unknown rather than inventing deletion evidence.
        return new(rootId, _side, gen, cursor, cursor, Array.Empty<Change>(), false, [string.Empty]);
    }

    private Delta Invalid(long gen, string rootId, string cursor, string reason)
    {
        LastFallbackReason = reason;
        RunLog.Trace($"local full reconciliation ({rootId}): {reason}");
        return new(rootId, _side, gen, cursor, cursor, Array.Empty<Change>(), true, Array.Empty<string>());
    }

    private static bool IsRootUnavailable(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception;
}
