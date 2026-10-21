namespace Massdex.SyncShark.V2;

/// <summary>Reads a root's id by observing the fake endpoint's root, as the CLI did before IRootProbe.</summary>
internal sealed class EndpointRootProbe(IEndpointProvider endpoints) : IRootProbe
{
    public async Task<string> ObjectIdAsync(string provider, string? accountId, string path, CancellationToken ct)
    {
        var root = new RootDef(V2CliRuntime.RootId(provider, accountId, path), provider, accountId, path, string.Empty);
        var observed = await endpoints.Writer(root).ObserveAsync(string.Empty, ct);
        return observed?.ObjectId ?? throw new SyncCliTargetException("The folder has no observable root identity.");
    }
}

/// <summary>Maps each root to its fake endpoint for both inventory reads and writes.</summary>
internal sealed class FakeEndpointProvider : IEndpointProvider
{
    private readonly Dictionary<string, FakeEndpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IEndpointWriter> _writers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IInventorySource> _inventories = new(StringComparer.Ordinal);

    public FakeEndpointProvider Add(RootDef root, FakeEndpoint endpoint)
    {
        _endpoints[root.RootId] = endpoint;
        return this;
    }

    public FakeEndpointProvider AddWriter(RootDef root, IEndpointWriter writer)
    {
        _writers[root.RootId] = writer;
        return this;
    }

    public FakeEndpointProvider AddInventory(RootDef root, IInventorySource inventory)
    {
        _inventories[root.RootId] = inventory;
        return this;
    }

    public IInventorySource Inventory(RootDef root) =>
        _inventories.TryGetValue(root.RootId, out var inventory) ? inventory : Endpoint(root);

    public IEndpointWriter Writer(RootDef root) =>
        _writers.TryGetValue(root.RootId, out var writer) ? writer : Endpoint(root);

    private FakeEndpoint Endpoint(RootDef root) => _endpoints.TryGetValue(root.RootId, out var endpoint)
        ? endpoint
        : throw new InvalidOperationException("No fake endpoint for root " + root.RootId + ".");
}

/// <summary>A provider whose root observation completes only when released, to prove callers await it.</summary>
internal sealed class GatedObserveProvider : IEndpointProvider, IEndpointWriter
{
    public const string RootObjectId = "id:gated-root";

    // A fallback release so a caller that blocks instead of awaiting fails its check rather than hanging forever.
    private static readonly TimeSpan FallbackRelease = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GatedObserveProvider() => _ = Task.Delay(FallbackRelease).ContinueWith(_ => Release());

    public void Release() => _gate.TrySetResult();

    public IInventorySource Inventory(RootDef root) => throw new NotSupportedException();

    public IEndpointWriter Writer(RootDef root) => this;

    public async Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
    {
        await _gate.Task.WaitAsync(ct);
        return new ItemState(path, ItemKind.Folder, RootObjectId, null, null, 0, DateTimeOffset.UnixEpoch,
            ItemFlags.None);
    }

    public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();

    public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();

    public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Counts writer calls while delegating to a fake endpoint.</summary>
internal sealed class CountingWriter : IEndpointWriter, IRecoveryAvailability, ISourceAbsenceProbe
{
    public string? RecoveryUnavailable { get; set; }
    private readonly FakeEndpoint _inner;
    private int _effects;

    public CountingWriter(FakeEndpoint inner) => _inner = inner;

    private int _puts;
    public int Puts => Volatile.Read(ref _puts);
    private int _moves;
    public int Moves => Volatile.Read(ref _moves);
    private int _deletes;
    public int Deletes => Volatile.Read(ref _deletes);
    private int _mkDirs;
    public int MkDirs => Volatile.Read(ref _mkDirs);
    private int _preserves;
    public int Preserves => Volatile.Read(ref _preserves);
    public int Calls => Puts + Moves + Deletes + MkDirs + Preserves;
    public Action<string>? OnRead { get; set; }

    /// <summary>
    /// When set, every effect after the first waits here until the run is cancelled, and the first such wait completes
    /// <see cref="FirstEffectCommitted"/>. A stop check sets this and waits on that instead of sleeping a fixed time, so
    /// it always stops a run that has committed one effect, never one still scanning.
    /// </summary>
    public bool ParkAfterFirstEffect { get; set; }

    /// <summary>
    /// Completed once a second effect begins. With one worker the first effect's commit reaches the store before the
    /// next effect can start, so a waiter here knows the run already has one finished, committed effect to keep.
    /// </summary>
    public TaskCompletionSource FirstEffectCommitted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, every Put throws this instead of writing (an account that lost access mid-run).</summary>
    public Exception? PutError { get; set; }
    public Func<SyncAction, Exception?>? PutFailure { get; set; }

    private async Task ParkUnlessFirstAsync(CancellationToken ct)
    {
        if (!ParkAfterFirstEffect) return;
        if (Interlocked.Increment(ref _effects) == 1) return;
        FirstEffectCommitted.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    public async Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct)
    {
        await ParkUnlessFirstAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _puts);
        if ((PutFailure?.Invoke(a) ?? PutError) is { } error) throw error;
        return await _inner.PutAsync(a, content, ct).ConfigureAwait(false);
    }

    public async Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct)
    {
        await ParkUnlessFirstAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _moves);
        return await _inner.MoveAsync(a, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(SyncAction a, CancellationToken ct)
    {
        await ParkUnlessFirstAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _deletes);
        await _inner.DeleteAsync(a, ct).ConfigureAwait(false);
    }

    public async Task<ItemState> MkDirAsync(string path, CancellationToken ct)
    {
        await ParkUnlessFirstAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _mkDirs);
        return await _inner.MkDirAsync(path, ct).ConfigureAwait(false);
    }

    public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct)
    {
        Interlocked.Increment(ref _preserves);
        return _inner.PreserveAsync(victim, actionId, ct);
    }

    /// <summary>When set, every observation fails, so an interrupted effect cannot be verified.</summary>
    public bool ObserveFails { get; set; }

    /// <summary>When set, every observation throws this (an account that lost access before reconcile).</summary>
    public Exception? ObserveError { get; set; }

    /// <summary>Optional delay applied before each observation, so a check can observe overlap.</summary>
    public TimeSpan ObserveLatency { get; set; }

    private int _observing;
    private int _maxObserving;

    /// <summary>The most observations this writer ever held in flight at once.</summary>
    public int MaxConcurrentObservations => Volatile.Read(ref _maxObserving);

    public async Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
    {
        OnRead?.Invoke(path);
        if (ObserveFails) throw new IOException("CountingWriter: the endpoint cannot be observed.");
        if (ObserveError is { } error) return await Task.FromException<ItemState?>(error).ConfigureAwait(false);
        var active = Interlocked.Increment(ref _observing);
        try
        {
            int observed;
            do { observed = Volatile.Read(ref _maxObserving); }
            while (active > observed && Interlocked.CompareExchange(ref _maxObserving, active, observed) != observed);
            if (ObserveLatency > TimeSpan.Zero) await Task.Delay(ObserveLatency, ct).ConfigureAwait(false);
            return await _inner.ObserveAsync(path, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _observing);
        }
    }

    /// <summary>Simulates unavailable historical identity evidence without changing the endpoint's actual state.</summary>
    public bool AbsenceUnknown { get; set; }

    public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) => ObserveAsync(path, ct);

    public async Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct)
    {
        // Keep scripted access failures/latency and prove the fake's real identity, including moved objects.
        if (await ObserveAsync(path, ct).ConfigureAwait(false) is not null) return false;
        if (AbsenceUnknown) return null;
        return await _inner.IsSourceAbsentAsync(historical, path, ct).ConfigureAwait(false);
    }

    public async Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct)
    {
        await ObserveAsync(historical.Path, ct).ConfigureAwait(false);
        if (AbsenceUnknown) return null;
        return await _inner.IsHistoricalSourcePresentAsync(historical, ct).ConfigureAwait(false);
    }

    public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct)
    {
        OnRead?.Invoke(s.Path);
        return _inner.OpenReadAsync(s, ct);
    }
}

/// <summary>
/// Wraps a fake endpoint's inventory for the run checks that need a scan the fake cannot produce: a listing without
/// hashes (so the real Planner asks for them) or a whole-root-incomplete listing.
/// </summary>
internal sealed class ScriptedInventory : IInventorySource
{
    private readonly FakeEndpoint _inner;

    public ScriptedInventory(FakeEndpoint inner) => _inner = inner;

    /// <summary>Full listings carry no hashes; HashAsync still answers from the fake.</summary>
    public bool Hashless { get; init; }

    /// <summary>Full listings and deltas report the whole root as one incomplete subtree.</summary>
    public bool Incomplete { get; init; }
    public IReadOnlyList<string> Unreadable { get; set; } = [];
    public string? LinkPath { get; init; }

    /// <summary>While set, HashAsync answers nothing, as when hashing fails.</summary>
    public bool FailHashes { get; set; }

    /// <summary>How many paths were asked for in HashAsync.</summary>
    public int HashedPaths { get; private set; }
    /// <summary>The largest single HashAsync request, i.e. the largest hash checkpoint.</summary>
    public int MaxHashChunk { get; private set; }
    public int HashCalls { get; private set; }

    /// <summary>Per-path HashAsync calls issued by the bulk-failure isolation fallback.</summary>
    public int SinglePathHashCalls { get; private set; }

    /// <summary>
    /// When set, every HashAsync call fails with <see cref="HashError"/>, so a transport outage is not
    /// limited to one path (the sustained-outage circuit-breaker check).
    /// </summary>
    public bool HashErrorAlways { get; set; }
    public int? CrashOnHashCall { get; set; }
    public string? ThrowHashPath { get; set; }
    public Exception? HashError { get; set; }
    public Exception? FullError { get; set; }
    private sealed class UnknownHashFailure : Exception { }
    public Action? OnHash { get; set; }

    public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p,
        CancellationToken ct)
    {
        if (FullError is { } error) throw error;
        var snapshot = await _inner.FullAsync(gen, include, p, ct);
        return snapshot with
        {
            Items = snapshot.Items.Where(item => !Unreadable.Any(path => PairPath.IsSelfOrUnder(item.Path, path)))
                .Select(item => item.Path == LinkPath ? item with { Flags = ItemFlags.Link } : Hashless ? item with { Hash = null } : item).ToArray(),
            IncompleteSubtrees = Incomplete ? [string.Empty] : Unreadable.Concat(snapshot.IncompleteSubtrees).ToArray()
        };
    }

    /// <summary>When set, ChangesSinceAsync throws this (an account that lost access before the scan).</summary>
    public Exception? ChangesError { get; set; }

    public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
    {
        if (ChangesError is { } error) throw error;
        var delta = await _inner.ChangesSinceAsync(cursor, gen, include, ct);
        return delta with
        {
            Changes = delta.Changes.Where(item => !Unreadable.Any(path => PairPath.IsSelfOrUnder(item.Path, path))).ToArray(),
            IncompleteSubtrees = Incomplete ? [string.Empty] : Unreadable.Concat(delta.IncompleteSubtrees).ToArray()
        };
    }

    public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        HashCalls++;
        if (paths.Count == 1) SinglePathHashCalls++;
        if (HashErrorAlways) throw HashError ?? new UnknownHashFailure();
        if (ThrowHashPath is not null && paths.Contains(ThrowHashPath)) throw HashError ?? new UnknownHashFailure();
        OnHash?.Invoke();
        if (HashCalls == CrashOnHashCall) throw new SimulatedCrash();
        if (paths.Count > MaxHashChunk) MaxHashChunk = paths.Count;
        HashedPaths += paths.Count;
        return FailHashes
            ? Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>())
            : _inner.HashAsync(paths, ct);
    }

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct) => _inner.DeletionTimesAsync(paths, ct);
}
/// <summary>An exception used to simulate a process crash in the middle of an apply.</summary>
internal sealed class SimulatedCrash : Exception
{
    public SimulatedCrash() : base("Simulated crash.") { }
}

/// <summary>Wraps a store to count writes and to throw a simulated crash at a chosen commit boundary.</summary>
internal sealed class RecordingStore : IStateStore
{
    private readonly IStateStore _inner;
    private int _commits;
    private int _intents;

    public RecordingStore(IStateStore inner) => _inner = inner;
    public IEnumerable<SyncRetry> ReadSyncRetries(string pairId) => _inner.ReadSyncRetries(pairId);
    public Action? OnRetryPathsRead { get; set; }
    public IEnumerable<string> ReadSyncRetryPaths(string pairId)
    {
        OnRetryPathsRead?.Invoke();
        return _inner.ReadSyncRetryPaths(pairId);
    }
    public bool SyncRetryExists(string pairId, string path) => _inner.SyncRetryExists(pairId, path);
    public SyncRetry? SyncRetryAt(string pairId, string path) => _inner.SyncRetryAt(pairId, path);
    public IReadOnlyList<SyncRetry> DueSyncRetries(string pairId, DateTimeOffset now, int limit) => _inner.DueSyncRetries(pairId, now, limit);
    public int CountSyncRetries(string pairId) => _inner.CountSyncRetries(pairId);
    public int RetryProtectionCalls { get; private set; }
    public bool HasActiveRestoredRetries(string pairId) => _inner.HasActiveRestoredRetries(pairId);
    public bool RetryProtected(string pairId, string path, IReadOnlySet<string> active, bool descendants, bool restoredOnly = false)
    {
        RetryProtectionCalls++;
        if (restoredOnly) Interlocked.Increment(ref _restoredProtectionReads);
        return _inner.RetryProtected(pairId, path, active, descendants, restoredOnly);
    }
    private int _restoredProtectionReads;
    /// <summary>Per-item restored-hold protection lookups this run made; zero when no restored hold exists.</summary>
    public int RestoredProtectionReads => Volatile.Read(ref _restoredProtectionReads);
    public void SaveSyncRetry(string pairId, SyncRetry retry) => _inner.SaveSyncRetry(pairId, retry);
    public void ClearSyncRetry(string pairId, string path) => _inner.ClearSyncRetry(pairId, path);
    public void SuspendHeldRetry(string pairId, SyncRetry retry) => _inner.SuspendHeldRetry(pairId, retry);
    public void RequireRehash(string rootId, ItemState item, string reason) => _inner.RequireRehash(rootId, item, reason);
    public void RequireRehashBatch(string rootId, IEnumerable<ItemState> items, string reason) => _inner.RequireRehashBatch(rootId, items, reason);
    public bool CompleteRehash(string rootId, ItemState item, string hash) => _inner.CompleteRehash(rootId, item, hash);
    public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit) => _inner.RehashCandidates(rootId, limit);
    public long RehashWatermark(string rootId) => _inner.RehashWatermark(rootId);
    public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit, long through) => _inner.RehashCandidates(rootId, limit, through);
    public IReadOnlyList<string> ObjectPaths(string rootId, string objectId, int offset, int limit) => _inner.ObjectPaths(rootId, objectId, offset, limit);
    public void AcknowledgeRehash(string rootId, long through, Func<string, bool> settled, CancellationToken ct = default) => _inner.AcknowledgeRehash(rootId, through, settled, ct);
    public bool HasMultipleNames(string rootId, string objectId) => _inner.HasMultipleNames(rootId, objectId);
    public bool RehashTouches(string rootId, string path, bool descendants) => _inner.RehashTouches(rootId, path, descendants);
    public IReadOnlyList<string> RehashReasons(string rootId, int limit) => _inner.RehashReasons(rootId, limit);

    public int ChangeRows { get; private set; }
    private int _flaggedReads;
    private int _allBaselineReads;
    public int FlaggedReads => Volatile.Read(ref _flaggedReads);
    public int AllBaselineReads => Volatile.Read(ref _allBaselineReads);
    public System.Collections.Concurrent.ConcurrentQueue<(Side Side, string[] Prefixes)> BaselinePrefixReads { get; } = new();

    public IReadOnlyList<ItemState> LiveFlagged(string rootId, ItemFlags flags)
    {
        Interlocked.Increment(ref _flaggedReads);
        return _inner.LiveFlagged(rootId, flags);
    }

    public IReadOnlyList<ItemState> LiveFlaggedFolders(string rootId, ItemFlags flags) =>
        _inner.LiveFlaggedFolders(rootId, flags);

    public (long Count, IReadOnlyList<ItemState> Examples) LiveFlaggedSummary(string rootId, ItemFlags flags, int limit) =>
        _inner.LiveFlaggedSummary(rootId, flags, limit);

    /// <summary>Clears the write counter so a single run can be measured.</summary>
    public void ResetCounters() => ChangeRows = 0;

    /// <summary>Throw <see cref="SimulatedCrash"/> instead of committing the Nth commit (after the effect).</summary>
    public int CrashOnCommit { get; set; } = -1;

    /// <summary>Throw <see cref="SimulatedCrash"/> after recording the Nth intent (before the effect).</summary>
    public int CrashAfterIntent { get; set; } = -1;

    public Action<string>? AfterBeginGeneration { get; set; }

    public long BeginGeneration(string rootId, string kind)
    {
        var gen = _inner.BeginGeneration(rootId, kind);
        AfterBeginGeneration?.Invoke(kind);
        return gen;
    }

    public void WriteSnapshot(Snapshot s)
    {
        ChangeRows += s.Items.Count;
        _inner.WriteSnapshot(s);
    }

    public void WriteDelta(Delta d)
    {
        ChangeRows += d.Changes.Count;
        _inner.WriteDelta(d);
    }

    public void CompleteGeneration(long gen, string cursor, bool complete) =>
        _inner.CompleteGeneration(gen, cursor, complete);

    public string? Cursor(string rootId) => _inner.Cursor(rootId);

    public void ForgetCursor(string rootId) => _inner.ForgetCursor(rootId);

    public bool FirstSyncDone(string pairId) => _inner.FirstSyncDone(pairId);

    public void MarkFirstSyncDone(string pairId) => _inner.MarkFirstSyncDone(pairId);

    public IReadOnlyList<ItemState> Live(string rootId, IEnumerable<string>? paths = null) => _inner.Live(rootId, paths);

    public IReadOnlyList<ItemState> LiveUnder(string rootId, IEnumerable<string> pathPrefixes) =>
        _inner.LiveUnder(rootId, pathPrefixes);

    public List<string> ChildDirectories { get; } = [];
    public IReadOnlyList<ItemState> LiveChildren(string rootId, string directory)
    {
        ChildDirectories.Add(directory);
        return _inner.LiveChildren(rootId, directory);
    }
    public IReadOnlyList<string> DirectorySpellings(string rootId, string path) => _inner.DirectorySpellings(rootId, path);

    public ItemState? ByObject(string rootId, string objectId) => _inner.ByObject(rootId, objectId);

    public IReadOnlyList<PairDef> Pairs() => _inner.Pairs();

    public PairDef? Pair(string pairId) => _inner.Pair(pairId);

    public void SavePair(PairDef p) => _inner.SavePair(p);

    public IReadOnlyList<Baseline> Baselines(string pairId, IEnumerable<string>? objectKeys = null)
    {
        if (objectKeys is null) Interlocked.Increment(ref _allBaselineReads);
        return _inner.Baselines(pairId, objectKeys);
    }

    public IReadOnlyList<Baseline> BaselinesAt(string pairId, Side side, IEnumerable<string> paths) =>
        _inner.BaselinesAt(pairId, side, paths);

    public IReadOnlyList<Baseline> BaselinesTouching(string pairId, Side side, IEnumerable<string> objectIds,
        IEnumerable<string> pathPrefixes)
    {
        var prefixes = pathPrefixes.ToArray();
        BaselinePrefixReads.Enqueue((side, prefixes));
        return _inner.BaselinesTouching(pairId, side, objectIds, prefixes);
    }

    public void CommitResult(string pairId, long gen, SyncAction a, ActionResult r)
    {
        _commits++;
        if (_commits == CrashOnCommit) throw new SimulatedCrash();
        _inner.CommitResult(pairId, gen, a, r);
        Outcomes[a.ActionId] = r.Outcome;
    }

    /// <summary>The last committed outcome of each action.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, Outcome> Outcomes { get; } = new(StringComparer.Ordinal);

    public void RecordIntent(string pairId, SyncAction a)
    {
        _intents++;
        _inner.RecordIntent(pairId, a);
        if (_intents == CrashAfterIntent) throw new SimulatedCrash();
    }

    public IReadOnlyList<SyncAction> UnresolvedIntents(string pairId) => _inner.UnresolvedIntents(pairId);
    public int CountUnresolvedIntents(string pairId) => _inner.CountUnresolvedIntents(pairId);
    public void SetIntentHeld(string pairId, string actionId, bool held) => _inner.SetIntentHeld(pairId, actionId, held);
    public void SetIntentsHeld(string pairId, IReadOnlyList<string> actionIds, bool held) =>
        _inner.SetIntentsHeld(pairId, actionIds, held);
    public int CountHeldIntents(string pairId) => _inner.CountHeldIntents(pairId);
    public bool IsIntentHeld(string pairId, string actionId) => _inner.IsIntentHeld(pairId, actionId);
    public IntentPage HeldIntentsPage(string pairId, string? cursor, int limit) =>
        _inner.HeldIntentsPage(pairId, cursor, limit);
    public IEnumerable<SyncAction> StreamUnresolvedIntents(string pairId) => _inner.StreamUnresolvedIntents(pairId);
    public IEnumerable<IntentRow> StreamUnresolvedIntentRows(string pairId) =>
        _inner.StreamUnresolvedIntentRows(pairId);
    public string? PairForIntent(string actionId) => _inner.PairForIntent(actionId);

    public Exception? RefusalRefreshError { get; set; }
    public void RefreshProviderNameNotices(PairDef pair)
    {
        if (RefusalRefreshError is { } error) throw error;
        var store = (IStateStore)this;
        foreach (var notice in ProviderNameNotices.Read(pair,
            (root, after) => store.LiveFlaggedSummary(root.RootId, ItemFlags.ProviderNameRefused, 0).Count == 0
                ? Array.Empty<ItemState>() : store.LiveFlaggedPage(root.RootId, ItemFlags.ProviderNameRefused, after, 100)))
            AddNotices([notice]);
    }

    public string? FailNoticePath { get; set; }
    public Action<IReadOnlyList<Notice>>? NoticesAdded { get; set; }
    public (Notice Notice, bool Closed)? NoticeById(string pairId, string noticeId) => _inner.NoticeById(pairId, noticeId);
    public void AddNotices(IEnumerable<Notice> n)
    {
        var notices = n.ToArray();
        if (FailNoticePath is not null && notices.Any(notice => notice.Path == FailNoticePath))
            throw new InvalidOperationException("injected notice write failure");
        _inner.AddNotices(notices);
        NoticesAdded?.Invoke(notices);
    }

    public IReadOnlyList<Notice> OpenNotices(string? pairId) => _inner.OpenNotices(pairId);

    public IReadOnlyList<RecoveryRecord> Recoveries(string pairId) => _inner.Recoveries(pairId);

    public void CloseNotice(string noticeId, string? choice) => _inner.CloseNotice(noticeId, choice);
    public void RecordNoticeChoiceFailure(Notice original, string failure) => _inner.RecordNoticeChoiceFailure(original, failure);

    public void RecordRunSummary(RunSummary summary) => _inner.RecordRunSummary(summary);

    public IReadOnlyList<RunSummary> ReadRunHistory(string? pairId = null, int limit = 50, long before = 0) =>
        _inner.ReadRunHistory(pairId, limit, before);

    public IReadOnlyList<RunSummary> ReadRunHistoryAfter(long after, int limit = 50) =>
        _inner.ReadRunHistoryAfter(after, limit);

    public IReadOnlyList<RunSummary> ReadRunHistoryPage(string? pairId, int limit, int offset) =>
        _inner.ReadRunHistoryPage(pairId, limit, offset);

    public RunSummary? RunSummaryById(string runId) => _inner.RunSummaryById(runId);

    public void StoreDeletionEvidence(string pairId, string rootId, IReadOnlyList<DeletionEvidenceRecord> results) =>
        _inner.StoreDeletionEvidence(pairId, rootId, results);

    public void Prune() => _inner.Prune();

    public void Dispose() => _inner.Dispose();
}

/// <summary>Records every progress report for throttle assertions.</summary>
internal sealed class RecordingProgress : IProgress<RunProgress>
{
    // The concurrent scan reports from two sides at once, so the sink must tolerate concurrent Report calls.
    private readonly object _gate = new();
    private readonly List<RunProgress> _reports = [];

    public IReadOnlyList<RunProgress> Reports
    {
        get { lock (_gate) return _reports.ToArray(); }
    }

    public void Report(RunProgress value)
    {
        lock (_gate) _reports.Add(value);
    }
}

/// <summary>
/// An inventory that publishes one provider process message while its full read runs, exactly as the provider's read
/// retry does through the shared byte-progress seam, so a run check can prove the message reaches the run's sink.
/// </summary>
internal sealed class ProcessMessageInventory(FakeEndpoint inner) : IInventorySource
{
    public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p,
        CancellationToken ct)
    {
        await SyncByteProgressStream.ReportProcessAsync("Waiting for network; retry Dropbox read", "a.txt",
            "list_folder: read-only retry 1 in 1.0 seconds; 60 seconds remain in the transient budget.", ProcessMessageKind.Notice);
        return await inner.FullAsync(gen, include, p, ct);
    }

    public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
        inner.ChangesSinceAsync(cursor, gen, include, ct);

    public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        inner.HashAsync(paths, ct);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);
}

/// <summary>An inventory that publishes a scripted list of process messages during its full read.</summary>
internal sealed class ScriptedMessageInventory(FakeEndpoint inner, IReadOnlyList<string> phases) : IInventorySource
{
    public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p,
        CancellationToken ct)
    {
        foreach (var phase in phases)
            await SyncByteProgressStream.ReportProcessAsync(phase, string.Empty, string.Empty,
                phase == "Checking remote item" ? ProcessMessageKind.Progress : ProcessMessageKind.Notice);
        return await inner.FullAsync(gen, include, p, ct);
    }

    public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
        inner.ChangesSinceAsync(cursor, gen, include, ct);

    public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        inner.HashAsync(paths, ct);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);
}

/// <summary>An inventory whose full read waits on a gate, so a run stays quiet long enough for its heartbeat to fire.</summary>
internal sealed class GatedScanInventory(FakeEndpoint inner) : IInventorySource
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed once the full read has begun and is about to wait on the gate.</summary>
    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p,
        CancellationToken ct)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(ct);
        return await inner.FullAsync(gen, include, p, ct);
    }

    public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
        inner.ChangesSinceAsync(cursor, gen, include, ct);

    public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        inner.HashAsync(paths, ct);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);
}
