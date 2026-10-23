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
    // Set before inventory reads; previews may persist recommendations only in their isolated store.
    internal bool ObserveOnly { get; set; }
    internal Func<string, long[]?>? CapacityQueryForTest { get; set; }
    internal Action<SqliteStateStore, SqliteStateStore.JournalPending, SyncDiscoveryRoot>? CapacityQueueForTest { get; set; }
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
        var reviewBudget = new VerificationBudget(VerificationElapsedForTest);
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
                var admitted = new List<ItemState>();
                foreach (var item in unknown)
                {
                    ct.ThrowIfCancellationRequested();
                    var prior = _known.Live(_root.RootId, [item.Path]).FirstOrDefault();
                    if (_known.DeferIndexObservation(_root.RootId, _side, item, prior,
                        "review content proof pending; independent indexing continues beyond review budget")) admitted.Add(item);
                }
                await HashProofsAsync(admitted, reviewBudget, hashes, ct).ConfigureAwait(false);
                hashed = reviewBudget.Items;
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
        if (checkpoint is not null && sql?.GapInventoryPending(rootId) == true &&
            NtfsUsnCheckpoint.TryParseCursor(_known.Cursor(rootId), out var replayJournal, out var replayNext) &&
            replayJournal == checkpoint.JournalId && ReplayTooLarge(replayNext, checkpoint.NextUsn))
        {
            // Re-protect the skipped interval BEFORE the one fresh metadata walk. A new
            // epoch invalidates old discharges; the store preserves the oldest proof boundary.
            ((ILocalGapStore)sql).BeginGap(rootId, checkpoint.Cursor, replayNext, true,
                "catch-up replay byte budget exceeded; fresh inventory with per-file protection");
            sql.ForgetLocalWalk(rootId);
            RunLog.Trace($"local catch-up bounded ({rootId}): fresh protected walk, no oversized journal replay");
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
        if (ReplayTooLarge(next, through.NextUsn))
            return JournalReadFailed(gen, rootId, cursor, through, next,
                "journal byte range exceeds replay budget", NtfsUsnReadFailure.OverLimit);
        if (!(JournalReadForTest ?? NtfsUsnJournal.TryReadChanges)(_root.RootPath, from, through, out var changes, out var readFailure, out var readFailureKind, ct))
        {
            return JournalReadFailed(gen, rootId, cursor, through, next, readFailure ?? "journal read failed", readFailureKind);
        }
        var names = new LocalJournalNames(_root, _known, changes, _settings.LocalDeltaMaxPaths);
        var scopedParents = new HashSet<(ulong Object, ulong Parent)>();
        var ambiguous = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        var scoped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scopeOverflow = false;
        long scopeCharacters = 0;
        void Scope(string path, string reason)
        {
            if (scopeOverflow) return;
            if (!scoped.ContainsKey(path)) scopeCharacters += path.Length;
            scoped[path] = reason;
            scopeOverflow = scoped.Count > Math.Min(_settings.LocalDeltaMaxPaths, PathProtectionIndex.DefaultPathLimit) || scopeCharacters > PathProtectionIndex.DefaultCharacterLimit;
        }
        void Defer(NtfsUsnChange record, ItemState? known, string? scope, string reason)
        {
            scopeOverflow |= names.ScopeOverflow;
            if (scopeOverflow) return;
            var resolveNames = record.Name.Length == 0 && (record.Reason & 0x13100) != 0;
            if (scope is not null && !(resolveNames && known is null)) Scope(scope, reason);
            if (resolveNames && scopedParents.Add((record.FileId, record.ParentFileId)))
            {
                var parent = record.ParentFileId == _rootFileId ? "" :
                    _known.ByObject(rootId, SyncLocalEndpointStore.ObjectId(_volume, record.ParentFileId)) is { Kind: ItemKind.Folder } folder
                        ? folder.Path : null;
                if (parent is not null)
                {
                    var aliases = names.FindAmbiguous(record, parent, ct);
                    if (aliases is null) Scope(parent, "ambiguous journal parent could not be read; scoped retry required");
                    else foreach (var alias in aliases) Scope(alias, reason);
                    scopeOverflow |= names.ScopeOverflow;
                }
            }
            if (scopeOverflow || known is null) return; // Unknown parent/object is outside the retained inventory.
            if (!ambiguous.TryAdd(known.ObjectId, known)) return;
            // Entity-owned rehash protects all aliases; named scopes also protect opposite-side
            // absence decisions and names not yet present in our inventory.
            for (var offset = 0; ; offset += 128)
            {
                var aliases = _known.ObjectPaths(rootId, known.ObjectId, offset, 128);
                foreach (var alias in aliases) Scope(alias, reason);
                if (aliases.Count < 128) break;
                if (scopeOverflow) break;
            }
            Scope(known.Path, reason);
        }
        var fallbackReason = "resolved path limit exceeded";
        var context = new ResolveContext(_rootFileId, _volume,
            id => _known.ByObject(rootId, SyncLocalEndpointStore.ObjectId(_volume, id)),
            _settings.LocalDeltaMaxPaths, names.Find, names.Ambiguous, reason => fallbackReason = names.LastFailure ?? reason, Defer, () => scopeOverflow);
        var paths = await LocalDeltaResolver.ResolveAsync(changes, context, ct).ConfigureAwait(false);
        if (scopeOverflow || paths is null || paths.Count > _settings.LocalDeltaMaxPaths)
        {
            // Unlike a gap, this batch contains authoritative write evidence. Keep it through fallback.
            PreserveWrites();
            return Invalid(gen, rootId, cursor, fallbackReason);
        }
        // Persist the hold before any cursor can advance. Do not hash or infer a delete
        // from a journal record whose name/identity is ambiguous.
        _known.RequireRehashBatch(rootId, ambiguous.Values.Where(item => item.Kind == ItemKind.File && !item.Flags.HasFlag(ItemFlags.OwnedPointer)),
            "ambiguous journal identity; scoped verification required");
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
            if (scopeOverflow) break;
            if (change.State is not { Kind: ItemKind.File } state) continue;
            ct.ThrowIfCancellationRequested();
            if (!names.Ambiguous(LocalItems.ParseObjectId(state.ObjectId).FileId, state, requirePresent: true)) continue;
            RunLog.Trace("local journal post-refresh retry: " + names.LastFailure);
            Defer(new(LocalItems.ParseObjectId(state.ObjectId).FileId, 0, 0, 0, FileAttributes.Normal, ""),
                _known.ByObject(rootId, state.ObjectId), state.Path,
                names.LastFailure ?? "refreshed file has ambiguous hard-link identity");
        }
        if (scopeOverflow) { PreserveWrites(); return Invalid(gen, rootId, cursor, "ambiguous journal scope exceeds bounded path budget"); }
        if (scoped.Count > 0)
        {
            _known.RequireRehashBatch(rootId, ambiguous.Values.Where(item => item.Kind == ItemKind.File && !item.Flags.HasFlag(ItemFlags.OwnedPointer)),
                "ambiguous journal identity; scoped verification required");
            var scopeIndex = PathProtectionIndex.TryCreate(scoped.Keys);
            if (scopeIndex is null) { PreserveWrites(); return Invalid(gen, rootId, cursor, "ambiguous journal scope exceeds bounded path budget"); }
            bool Protected(string path) => scoped.ContainsKey("") ||
                scopeIndex.Touches(path, false) || scopeIndex.Touches(path, true);
            var retained = new List<Change>();
            foreach (var change in delta.Changes)
                if (Protected(change.Path) || ambiguous.ContainsKey(change.ObjectId))
                    Scope(change.Path, scoped.GetValueOrDefault(change.Path) ?? "change overlaps ambiguous journal identity; reread required");
                else retained.Add(change);
            if (scopeOverflow) { PreserveWrites(); return Invalid(gen, rootId, cursor, "ambiguous journal scope exceeds bounded path budget"); }
            delta = delta with
            {
                Changes = retained.ToArray(),
                IncompleteSubtrees = delta.IncompleteSubtrees.Concat(scoped.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                IncompleteReasons = delta.IncompleteReasons.Concat(scoped).GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase)
            };
            RunLog.Trace($"local journal scoped retry ({rootId}): {scoped.Count} ambiguous path(s); remaining replay retained");
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

    internal Task<Delta> RehashAsync(PilotDirectoryLease lease, Delta delta, HashSet<string> written,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var candidates = delta.Changes.Where(change => change.State is { } state &&
            VerificationBudget.NeedsWriteProof(state, _known.ByObject(_root.RootId, state.ObjectId), written.Contains(state.ObjectId)))
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
        var io = GapIo ??= new LocalGapIo(_known, _settings, _productOwnedPaths);
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
            else
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
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await HashProofsAsync(_known.Live(_root.RootId, paths), new VerificationBudget(), result, ct).ConfigureAwait(false);
        return result;
    }

    // The only ordinary content-proof surface. Actual content IO is owned by LocalGapIo, shared
    // with background indexing. The bounded request is an effect proof, not a full inventory drain.
    private async Task HashProofsAsync(IReadOnlyList<ItemState> rows, VerificationBudget budget,
        Dictionary<string, string> hashes, CancellationToken ct)
    {
        var io = GapIo ??= new LocalGapIo(_known, _settings, _productOwnedPaths);
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
                    RunLog.Trace($"content proof rejected ({_root.RootId}): {item.Path}: native identity or metadata changed");
                else if (_known.CompleteIndexingRehash(_root.RootId, item, hash, revision))
                {
                    hashes[item.Path] = hash;
                    CompletePointerVerification(item);
                }
                else
                    RunLog.Trace($"content proof publication rejected ({_root.RootId}): {item.Path}: captured-revision={revision}; current-revision={_known.RehashRevision(_root.RootId, item.ObjectId)}; newer obligation or observation");
            }
            else
            {
                RunLog.Trace($"content proof unavailable ({_root.RootId}): {item.Path}: outcome={proof.Outcome}; reason={proof.Reason ?? "not supplied"}");
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
        var incomplete = scan.Complete ? DecisionPaths(scan, include) : [string.Empty];
        // A handled quarantine is not a run-wide incomplete scan, but its unobserved subtree
        // must remain untouched in storage until an explicit retry obtains fresh evidence.
        return new Snapshot(rootId, _side, gen, cursor, items, incomplete)
            { IncludesPath = path => include(path) && !UnderHeldDescendant(path, heldPaths) && !unobservedPointers.Contains(path), IncompleteReasons = DecisionReasons(scan, include),
                VerifyAbsence = HasOnlyHistoryHolds(scan)
                    ? item => HistoryListingAbsence(item, scan, include, incomplete) : null,
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
            { VerifyAbsence = historyOnly
                    ? item => HistoryListingAbsence(item, scan,
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
    private static FileAbsence HistoryListingAbsence(ItemState item, PilotLocalScan scan,
        Func<string, bool> include, IReadOnlyList<string> incomplete) =>
        scan.Complete && include(item.Path) && !LocalItems.InsideAny(item.Path, incomplete)
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
            if (_known is SqliteStateStore failed) failed.RecordIndexFailure(rootId, reason);
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
        RunLog.Trace($"local journal retry next sync ({rootId}): {reason}");
        // Preserve the exact cursor, and mark this run unknown rather than inventing deletion evidence.
        return new(rootId, _side, gen, cursor, cursor, Array.Empty<Change>(), false, [string.Empty]);
    }

    private Delta Invalid(long gen, string rootId, string cursor, string reason)
    {
        LastFallbackReason = reason;
        RunLog.Trace($"local full reconciliation ({rootId}): {reason}");
        // A caller that keeps the scopes as read debt (RetryScanScopesAsync) reads this to explain the
        // failure; callers that rescan ignore it.
        return new(rootId, _side, gen, cursor, cursor, Array.Empty<Change>(), true, Array.Empty<string>())
        { IncompleteReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = reason } };
    }

    private static bool IsRootUnavailable(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception;
}
