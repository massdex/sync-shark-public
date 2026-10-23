namespace Massdex.SyncShark.V2;

public sealed partial class V2SyncHost
{
    private CloudFilesLifetime? _cloudFiles;

    private void ReconnectCloudFiles(CancellationToken ct)
    {
        if (Composition is null) return;
        _cloudFiles ??= new CloudFilesLifetime(Composition.Endpoints.CreateCloudFiles,
            pair => CloudFilesLifetime.IsRegisteredPair(pair, _settings),
            _store as SqliteStateStore, Composition.Endpoints.ObserveStorageRemoteAsync);
        Composition.Endpoints.NewPointerProvider = (pair, token) => _cloudFiles.RequireProvider(pair, token);
        // Includes disabled pairs: stopping automatic sync must not destroy retained pointer ownership.
        // Hydration pause policy remains a separate later-slice decision.
        var failures = _cloudFiles.Reconnect(_store.Pairs(), ct, (pair, error) =>
            _store.AddNotices(new[] { new Notice(pair.PairId, 38, NoticeKind.Info, string.Empty,
                "Cloud Files could not reconnect to the saved binding: " + error.Message +
                " No registration was rebound or removed. Options: review and rebind this pair to the verified new path; " +
                "pause the pair; or leave it as is. Rebinding requires an explicit reviewed change.",
                "cloud-files-binding-needs-review") }));
        if (failures.Count > 0) _progressError = string.Join(Environment.NewLine, failures);
    }
}

/// <summary>Host-owned connections, never run-owned. Native registration is the durable reconnect record.</summary>
internal sealed class CloudFilesLifetime(
    Func<PairDef, WindowsCloudFilesBinding, SyncDirectoryStorage, ICloudFilesConnection> createConnection,
    Func<PairDef, bool> hasRegistration, SqliteStateStore? noticeStore = null,
    Func<PairDef, string, CancellationToken, Task<PilotRemoteEntry?>>? observeRemote = null) : IDisposable
{
    internal Func<PairDef, string, CancellationToken, Task<PilotRemoteEntry?>>? ObserveRemote { get; } = observeRemote;
    private readonly object _gate = new();
    private readonly Dictionary<string, (WindowsCloudFilesBinding Binding, ICloudFilesConnection Connection)> _open = new();
    private HydrationNoticeWriter? _notices;
    private bool _disposed;

    /// <summary>
    /// Native eligibility for reconnecting a pair's registered root: the live local root exists, Windows reports a
    /// registration, and it is not the Explorer status-only shape. Durable storage ownership and explicit intent are
    /// a separate store gate: callers must not reconnect merely because this returns true.
    /// </summary>
    internal static bool IsRegisteredPair(PairDef pair, V2Settings settings)
    {
        if (pair.A.Provider != SyncProviders.Local || pair.B.Provider != SyncProviders.Dropbox) return false;
        if (!Directory.Exists(pair.A.RootPath)) return false;
        if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(pair.A.RootPath) is null) return false;
        return !WindowsCloudFilesProvider.IsStatusOnlySyncRoot(PairMapping.ToSyncPair(pair, false, settings));
    }

    internal IReadOnlyList<string> Reconnect(IEnumerable<PairDef> pairs, CancellationToken ct,
        Action<PairDef, Exception>? report = null)
    {
        var failures = new List<string>();
        // No durable v2 registration means no native probe at all. Legacy, orphaned or foreign registrations are
        // never adopted by startup merely because they exist.
        IReadOnlyList<StorageRegistration>? owned = null;
        if (noticeStore is not null)
        {
            owned = noticeStore.StorageRegistrations();
            if (owned.Count == 0) return failures;
        }
        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();
            if (pair.A.Provider != SyncProviders.Local || pair.B.Provider != SyncProviders.Dropbox) continue;
            if (owned is not null && !owned.Any(entry => entry.RootId == pair.A.RootId &&
                    SamePath(entry.Path, pair.A.RootPath))) continue;
            try { Open(pair, false, false, SyncDirectoryStorage.AlwaysAvailable, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add(pair.Name + ": Cloud Files needs attention: " + error.Message);
                report?.Invoke(pair, error);
            }
        }
        return failures;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    internal void Open(PairDef pair, bool create, bool preview, SyncDirectoryStorage defaultStorage,
        CancellationToken ct)
    {
        // Preview must not even probe native registration or open the provider.
        if (preview) return;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            // A never-registered pair has no provider to reconnect, even if its saved
            // remote account is incomplete. Existing connections still validate binding below.
            if (!create && !_open.ContainsKey(pair.PairId) && !hasRegistration(pair)) return;
            var binding = PairMapping.CloudFilesBinding(pair);
            // Already open: validate the exact binding only. No registry or native probe per pointer/action.
            if (_open.TryGetValue(pair.PairId, out var prior))
            {
                prior.Binding.RequireSame(binding);
                return;
            }
            // Native identity + recorded path, never Explorer registry publication, proves v2 ownership.
            string? expectedRegistrationId = null;
            if (noticeStore is not null && Directory.Exists(pair.A.RootPath))
                expectedRegistrationId = WindowsCloudFilesProvider.RequireRecordedRegistration(pair.A.RootPath,
                    WindowsCloudFilesProvider.ReadRootRegistration,
                    (path, token) => noticeStore.OwnsStorageRegistration(pair.A.RootId, path, token));
            var connection = createConnection(pair, binding, defaultStorage);
            if (connection is WindowsCloudFilesProvider ownedProvider)
                ownedProvider.ExpectedRegistrationId = expectedRegistrationId;
            if (noticeStore is not null && connection is WindowsCloudFilesProvider native)
            {
                // Everything that opens the placeholder or touches SQLite runs on the writer's thread. The native
                // callback thread only enqueues (and only after Windows I/O has been completed).
                var writer = _notices ??= new HydrationNoticeWriter(noticeStore);
                writer.Register(pair);
                StorageNotice? Item(string path)
                {
                    var full = LocalItems.FullPath(pair.A.RootPath, path);
                    if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var pointer) || pointer is null ||
                        !pointer.Identity.IsBoundTo(pair.PairId, pair.B.Provider)) return null;
                    var (volume, _) = LocalItems.ParseObjectId(pair.A.RootObjectId);
                    return new(pair.A.RootId, SyncLocalEndpointStore.ObjectId(volume, pointer.FileId), pair.B.RootId, "hydration");
                }
                // The callback carries the placeholder's local object id, so a renamed pointer's success closes the
                // notice opened under its old path without a placeholder open or a database write per range.
                native.FetchFailed += (path, localObjectId, error) => writer.Report(pair, path, error, Item, localObjectId);
                native.FetchSucceeded += (path, localObjectId) => writer.Succeeded(pair.PairId, path, Item, localObjectId);
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                connection.RegisterAndConnect();
                ct.ThrowIfCancellationRequested();
                // Re-read native identity to close a replacement race during connection.
                if (expectedRegistrationId is not null && connection is WindowsCloudFilesProvider reconnected &&
                    !string.Equals(reconnected.RegistrationId, expectedRegistrationId, StringComparison.Ordinal))
                    throw new IOException("The native registration changed while connecting; it was not adopted.");
                // Only fresh creation can write ownership. No Explorer publication is needed.
                if (noticeStore is not null && connection is WindowsCloudFilesProvider created && created.CreatedRegistration)
                    noticeStore.RecordStorageRegistration(pair.A.RootId, pair.A.RootPath,
                        created.RegistrationId ?? throw new IOException(
                            "Windows did not return the v2 sync-root ownership GUID; the ownership record was not written."));
                _open.Add(pair.PairId, (binding, connection));
            }
            catch (Exception original)
            {
                // Native implementation independently proves new ownership and no dependent pointers.
                try { connection.RollBackNewRegistration(); }
                catch (Exception rollback) { throw new AggregateException(original, rollback); }
                finally { connection.Dispose(); }
                throw;
            }
        }
    }

    internal WindowsCloudFilesProvider RequireProvider(PairDef pair, CancellationToken ct)
    {
        Open(pair, true, false, SyncDirectoryStorage.AlwaysAvailable, ct);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _open[pair.PairId].Connection as WindowsCloudFilesProvider
                ?? throw new InvalidOperationException("This connection cannot settle files.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _open.Values) entry.Connection.Dispose();
            _open.Clear();
        }
        // Flushes failure notices already observed before the last connection closed. Never on a callback thread.
        _notices?.Dispose();
    }
}

/// <summary>
/// Persists hydration notices and their closures away from the Cloud Files callback thread.
/// </summary>
/// <remarks>
/// Windows I/O completion must never wait on SQLite: the provider calls <see cref="Report"/> or
/// <see cref="Succeeded"/> only after it has completed the transfer, and both only enqueue. Both queues are bounded and
/// coalesce by item identity (or by pair cause for a pair-level failure), so a burst of callbacks cannot grow memory
/// with the item count. A single worker drains them in bounded batches. Pair-level causes (network, account auth, a
/// disconnected provider) are reported once per pair, never once per file. The open-notice cache is keyed by the
/// callback's local object identity when available, so a rename still closes the old notice without a per-range
/// placeholder open or database write. A success also closes the pair's hydration cause family once, so a recovery
/// closes a notice left by a previous process.
/// </remarks>
internal sealed class HydrationNoticeWriter : IDisposable
{
    private const int BatchLimit = 128;
    private static readonly TimeSpan PairBackoff = TimeSpan.FromMinutes(1);
    internal const int MaximumQueuedEvents = 4096;
    internal const int MaximumOpenCache = 16_384;
    private const int MaximumOpenOrder = MaximumOpenCache * 4;
    private readonly SqliteStateStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<(string PairId, string Key), StorageNotice> _open = new();
    private readonly Queue<(string PairId, string Key)> _openOrder = new();
    private readonly Dictionary<(string PairId, string Key), NoticeEvent> _pendingEvents = new();
    private readonly Queue<(string PairId, string Key)> _eventOrder = new();
    private readonly HashSet<(string PairId, string Key)> _queued = new();
    private readonly HashSet<(string PairId, string Key)> _inFlight = new();
    private readonly HashSet<string> _primedPairs = new();
    private readonly HashSet<string> _incompletePairs = new();
    private readonly HashSet<string> _hydratedPairs = new();
    private readonly HashSet<string> _pairHydrationCauses = new();
    private readonly Dictionary<string, PairDef> _pairDefs = new();
    private readonly HashSet<string> _overflowPairs = new();
    private readonly Dictionary<string, PairDef> _overflowPairDefs = new();
    private readonly HashSet<(string PairId, string Path)> _probed = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private Exception? _workerFailure;
    private Exception? _firstPersistenceError;
    private List<NoticeEvent>? _retryBatch;
    private FailureRetryOwner.State _deliveryRetry;
    internal Action? BeforePersistForTest { get; set; }
    internal Func<DateTimeOffset> DeliveryClock { get; set; } = () => DateTimeOffset.UtcNow;
    private long _barrierSequence;
    private bool _disposed;

    internal HydrationNoticeWriter(SqliteStateStore store, bool startWorker = true)
    {
        _store = store;
        if (startWorker) _worker = Task.Run(RunAsync);
    }

    /// <summary>Queued events and cache sizes; used by focused checks to prove the bounds hold.</summary>
    internal int PendingEvents { get { lock (_gate) return _pendingEvents.Count; } }
    internal int OpenCacheCount { get { lock (_gate) return _open.Count; } }
    internal int DroppedEvents { get; private set; }
    internal int PersistedFailures { get; private set; }
    internal int ClosedNotices { get; private set; }
    internal int ClosedPairCauses { get; private set; }
    /// <summary>Persistence attempts that failed. Delivery remains pending and retries automatically;
    /// the counter is diagnostic, never acknowledgement of a durable write.</summary>
    internal int PersistenceErrors { get; private set; }
    /// <summary>The first swallowed store failure, one bounded reference, so a check can show the real error and
    /// stack instead of only a counter.</summary>
    internal Exception? FirstPersistenceError { get { lock (_gate) return _firstPersistenceError; } }

    /// <summary>Records the pair shape once per open so a later success can build its pair notice family.</summary>
    internal void Register(PairDef pair)
    {
        lock (_gate) _pairDefs[pair.PairId] = pair;
    }

    /// <summary>Queues one failed hydration; never opens a file or touches the store on the calling thread.</summary>
    internal void Report(PairDef pair, string path, Exception error, Func<string, StorageNotice?> resolve,
        string? localObjectId = null)
    {
        // Positive type evidence only, and only after Windows I/O completion. A pair-level cause is coalesced by
        // (pair, cause) so an offline burst of thousands of reads queues one event, not one per file.
        var pairError = StorageNotices.ClassifyFailure(error);
        lock (_gate)
        {
            if (_disposed) return;
            _pairDefs[pair.PairId] = pair;
            if (pairError is not null)
            {
                _pairHydrationCauses.Add(pair.PairId);
                EnqueueLocked(NoticeKind.PairFailure, pair.PairId, string.Empty, pair, pairError, null, pairError.Cause);
            }
            else
                EnqueueLocked(NoticeKind.Failure, pair.PairId, path, pair, error, resolve, localObjectId);
        }
        Signal();
    }

    /// <summary>
    /// Records a successful hydration. Returns false, without any work, when the pair's durable notice state is
    /// already loaded, no pair cause is outstanding and no notice is open for the item identity or path.
    /// </summary>
    internal bool Succeeded(string pairId, string path, Func<string, StorageNotice?>? resolve,
        string? localObjectId = null)
    {
        var key = ItemKey(localObjectId, path);
        bool queued;
        lock (_gate)
        {
            if (_disposed) return false;
            if (!_primedPairs.Contains(pairId))
            {
                // Prime before this success so a durable notice from a previous process can be closed.
                queued = EnqueueLocked(NoticeKind.Prime, pairId, string.Empty, null, null, null, null);
                queued |= EnqueueLocked(NoticeKind.Success, pairId, path, null, null, resolve, localObjectId);
            }
            else if (!_incompletePairs.Contains(pairId) && !_pairHydrationCauses.Contains(pairId) &&
                     !_pendingEvents.ContainsKey((pairId, key)) && !_inFlight.Contains((pairId, key)) &&
                     !_open.ContainsKey((pairId, key)) &&
                     !_pendingEvents.ContainsKey((pairId, "path:" + path)) &&
                     !_inFlight.Contains((pairId, "path:" + path)) &&
                     !_open.ContainsKey((pairId, "path:" + path)))
            {
                // First success per process still needs one bounded close of a prior-process pair cause.
                if (!_hydratedPairs.Contains(pairId))
                    queued = EnqueueLocked(NoticeKind.Success, pairId, path, null, null, resolve, localObjectId);
                else
                    return false;
            }
            else
                queued = EnqueueLocked(NoticeKind.Success, pairId, path, null, null, resolve, localObjectId);
        }
        if (queued) Signal();
        return queued;
    }

    /// <summary>Loads a pair's durable hydration notices once. Bounded: overflow marks the cache incomplete, and a
    /// later success closes its notice by identity instead of trusting the cache.</summary>
    internal void Prime(string pairId)
    {
        List<Notice> notices;
        try { notices = _store.HydrationNoticesPage(pairId, MaximumOpenCache + 1).ToList(); }
        catch (Exception error)
        {
            if (FailureRetryOwner.ScopeOf(error, _stop.Token) is FailureRetryOwner.FailureScope.State or FailureRetryOwner.FailureScope.Stop)
                throw;
            RecordPersistenceError(error);
            lock (_gate)
            {
                _primedPairs.Add(pairId);
                _incompletePairs.Add(pairId);
            }
            return;
        }
        lock (_gate)
        {
            _primedPairs.Add(pairId);
            foreach (var notice in notices)
            {
                if (notice.Storage is not { Operation: "hydration" } item) continue;
                var key = ItemKey(item.ObjectId, notice.Path);
                if (_open.ContainsKey((notice.PairId, key))) continue;
                if (!RememberLocked((notice.PairId, key), item, (notice.PairId, "path:" + notice.Path)))
                {
                    _incompletePairs.Add(pairId);
                    break;
                }
            }
            // The bounded page is deliberately one row over the cache: overflow means the cache is incomplete.
            if (notices.Count > MaximumOpenCache) _incompletePairs.Add(pairId);
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= Task.Run(RunAsync);
        }
    }

    /// <summary>Drains every queued event on the calling thread. Focused checks use a writer without its worker and
    /// assert deterministic counters instead of polling a scheduled task on a loaded CI thread pool.</summary>
    internal void Drain()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        Process();
        ThrowPersistenceFailure();
    }

    /// <summary>
    /// Queues a barrier and completes only after the real background worker has applied every event queued before it.
    /// Focused checks use this to exercise the Task.Run drain without sleeps; a bounded wait surfaces a stopped or
    /// faulted worker instead of hanging the suite.
    /// </summary>
    internal async Task DrainAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed) return;
            var key = ("", "\0barrier:" + (++_barrierSequence).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _pendingEvents[key] = new NoticeEvent(NoticeKind.Barrier, "", "", null, null, null, null, done);
            _eventOrder.Enqueue(key);
        }
        Signal();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Exception? failure;
            lock (_gate) failure = _workerFailure ?? _firstPersistenceError;
            throw new IOException("The hydration notice worker did not reach the test barrier.", failure);
        }
        ThrowPersistenceFailure();
    }

    /// <summary>Test seam: a swallowed store failure is rethrown after a drain so checks show the real error and
    /// stack; production retains pending delivery and retries on the shared failure deadline.</summary>
    private void ThrowPersistenceFailure()
    {
        Exception? error;
        int count;
        lock (_gate)
        {
            error = _firstPersistenceError;
            count = PersistenceErrors;
        }
        if (error is not null)
            throw new IOException("The hydration notice writer could not persist a notice (" + count +
                " failure(s)); the first cause is attached.", error);
    }

    private void RecordPersistenceError(Exception error)
    {
        lock (_gate)
        {
            PersistenceErrors++;
            _firstPersistenceError ??= error;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stop.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* Pending delivery is best effort at shutdown; durable storage work remains unsettled. */ }
        _stop.Dispose();
    }

    private void Signal()
    {
        try { _signal.Release(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                try
                {
                    var wait = _retryBatch is null ? Timeout.InfiniteTimeSpan
                        : TimeSpan.FromMilliseconds(Math.Max(1, (_deliveryRetry.RetryAfter - DeliveryClock()).TotalMilliseconds));
                    await _signal.WaitAsync(wait, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                Process();
                if (_workerFailure is not null) break;
            }
        }
        catch (Exception error)
        {
            // Surface an unexpected worker fault through DrainAsync instead of silently stopping persistence.
            lock (_gate) _workerFailure = error;
        }
        finally
        {
            // Best-effort drain of events already observed before shutdown.
            try { Process(); }
            catch (Exception error) { lock (_gate) _workerFailure ??= error; }
        }
    }

    // Item events coalesce by local object identity when the callback supplied it (rename-safe), otherwise by path.
    private static string ItemKey(string? localObjectId, string path) =>
        string.IsNullOrEmpty(localObjectId) ? "path:" + path : "id:" + localObjectId;

    private static (string PairId, string Key) EventKey(NoticeKind kind, string pairId, string path,
        string? localObjectId, string? cause) => kind switch
    {
        NoticeKind.PairFailure => (pairId, "\0pair:" + cause),
        NoticeKind.Prime => (pairId, "\0prime"),
        _ => (pairId, ItemKey(localObjectId, path))
    };

    /// <summary>Latest event per item (or per pair cause); a replacement keeps the item's queue position. Never
    /// grows past the bound.</summary>
    private bool EnqueueLocked(NoticeKind kind, string pairId, string path, PairDef? pair, Exception? error,
        Func<string, StorageNotice?>? resolve, string? localObjectId)
    {
        var key = EventKey(kind, pairId, path, localObjectId, error is StoragePairException classified ? classified.Cause : null);
        if (_pendingEvents.ContainsKey(key))
        {
            _pendingEvents[key] = new NoticeEvent(kind, pairId, path, localObjectId, pair, error, resolve);
            return true;
        }
        if (_pendingEvents.Count >= MaximumQueuedEvents)
        {
            DroppedEvents++;
            // Never lose failure reporting entirely: remember one pair-level backlog notice for the worker.
            if ((kind is NoticeKind.Failure or NoticeKind.PairFailure) && pair is { } failed)
            {
                _overflowPairs.Add(failed.PairId);
                _overflowPairDefs[failed.PairId] = failed;
            }
            return false;
        }
        _pendingEvents[key] = new NoticeEvent(kind, pairId, path, localObjectId, pair, error, resolve);
        if (_queued.Add(key)) _eventOrder.Enqueue(key);
        return true;
    }

    private void Process()
    {
        if (_workerFailure is not null) return; // State/stop scope must never issue another write.
        while (true)
        {
            if (_retryBatch is not null && DeliveryClock() < _deliveryRetry.RetryAfter) return;
            var batch = _retryBatch ?? new List<NoticeEvent>(BatchLimit);
            lock (_gate)
            {
                while (_retryBatch is null && _eventOrder.Count > 0 && batch.Count < BatchLimit)
                {
                    var key = _eventOrder.Dequeue();
                    _queued.Remove(key);
                    if (!_pendingEvents.Remove(key, out var next)) continue;
                    if (next.Kind is NoticeKind.Failure or NoticeKind.PairFailure) _inFlight.Add(key);
                    batch.Add(next);
                }
            }
            if (batch.Count == 0)
            {
                _retryBatch = null;
                _deliveryRetry = default;
                if (FlushOverflow()) continue;
                return;
            }
            try
            {
                // Preserve event order across retries, including aliases for the same object. A later
                // failure must not be closed by an earlier success whose write had to be retried.
                while (batch.Count > 0)
                {
                    var next = batch[0];
                    if (next.Kind == NoticeKind.Barrier)
                    {
                        next.Barrier!.TrySetResult();
                        batch.RemoveAt(0);
                        continue;
                    }
                    // Pair removal retires its notice families. A queued callback cannot resurrect them
                    // or become a permanent poison event ahead of another pair's deliveries.
                    if (_store.Pair(next.PairId) is null)
                    {
                        lock (_gate) _inFlight.Remove(EventKey(next.Kind, next.PairId, next.Path, next.LocalObjectId,
                            next.Error is StoragePairException removedCause ? removedCause.Cause : null));
                        batch.RemoveAt(0);
                        continue;
                    }
                    var closures = new List<(string PairId, StorageNotice Item)>();
                    var pairClosures = new HashSet<string>();
                    BeforePersistForTest?.Invoke();
                    Apply(next, closures, pairClosures);
                    FlushClosures(closures);
                    FlushPairClosures(pairClosures);
                    lock (_gate)
                    {
                        _inFlight.Remove(EventKey(next.Kind, next.PairId, next.Path, next.LocalObjectId,
                            next.Error is StoragePairException classified ? classified.Cause : null));
                        if (next.Kind == NoticeKind.Success)
                        {
                            _open.Remove((next.PairId, ItemKey(next.LocalObjectId, next.Path)));
                            _open.Remove((next.PairId, "path:" + next.Path));
                            _hydratedPairs.Add(next.PairId);
                            // A newer callback may have queued another failure while we committed this success.
                            if (!_pendingEvents.Values.Any(e => e.PairId == next.PairId &&
                                e.Kind == NoticeKind.PairFailure)) _pairHydrationCauses.Remove(next.PairId);
                            if (_probed.Count > MaximumOpenCache * 2) _probed.Clear();
                            _probed.Add((next.PairId, next.Path));
                        }
                    }
                    batch.RemoveAt(0);
                }
                _retryBatch = null;
                _deliveryRetry = default;
            }
            catch (Exception error)
            {
                RecordPersistenceError(error);
                if (FailureRetryOwner.ScopeOf(error, _stop.Token) is
                    FailureRetryOwner.FailureScope.State or FailureRetryOwner.FailureScope.Stop)
                {
                    lock (_gate) _workerFailure = error;
                    return;
                }
                // Removal may race the retained-pair check above. Drop that retired event and
                // immediately continue with the remainder rather than waiting on an impossible retry.
                var removed = false;
                try { removed = batch.Count > 0 && _store.Pair(batch[0].PairId) is null; }
                catch (Exception lookupError)
                {
                    if (FailureRetryOwner.ScopeOf(lookupError, _stop.Token) is
                        FailureRetryOwner.FailureScope.State or FailureRetryOwner.FailureScope.Stop)
                    {
                        lock (_gate) _workerFailure = lookupError;
                        return;
                    }
                    // A transient lookup failure is not evidence of removal. Retain and retry normally.
                }
                if (removed)
                {
                    var retired = batch[0];
                    lock (_gate) _inFlight.Remove(EventKey(retired.Kind, retired.PairId, retired.Path, retired.LocalObjectId,
                        retired.Error is StoragePairException retiredCause ? retiredCause.Cause : null));
                    batch.RemoveAt(0);
                    _retryBatch = batch;
                    _deliveryRetry = default;
                    continue;
                }
                _retryBatch = batch;
                _deliveryRetry = FailureRetryOwner.Failed(_deliveryRetry.Attempts, DeliveryClock());
                return;
            }
        }
    }

    private void Apply(NoticeEvent next, List<(string PairId, StorageNotice Item)> closures,
        HashSet<string> pairClosures)
    {
        switch (next.Kind)
        {
            case NoticeKind.Prime:
                Prime(next.PairId);
                return;
            case NoticeKind.PairFailure:
            {
                lock (_gate) _pairHydrationCauses.Add(next.PairId);
                if (next.Pair is { } pair && next.Error is { } pairError)
                {
                    {
                        StorageNotices.Report(_store, pair, string.Empty,
                            new StorageNotice(pair.A.RootId, string.Empty, pair.B.RootId, "hydration"), pairError);
                        PersistedFailures++;
                        // Durable root backoff: the durable offline lane's page query skips a backed-off root, so a
                        // pair outage cannot turn every offline hydration job into its own per-item notice. The lane
                        // must NOT blanket-close offline item notices: a disk/hash/sharing cause is item-specific.
                        // Only connectivity causes pause the other lanes for this root; a local notice backlog (or
                        // an item-specific cause) must never stall settlement, refresh or offline hydration.
                        if (pairError is StoragePairException { Cause: "network" or "auth" or "provider" })
                            _store.SetStorageRootBackoff(pair.A.RootId, PairBackoff);
                    }
                }
                return;
            }
            case NoticeKind.Failure:
            {
                StorageNotice? notice = null;
                try { notice = next.Resolve?.Invoke(next.Path); }
                catch { /* Fall back to one pair cause rather than silently losing the failure. */ }
                lock (_gate)
                {
                    if (notice is not null)
                        RememberLocked((next.PairId, ItemKey(next.LocalObjectId, next.Path)), notice,
                            (next.PairId, "path:" + next.Path));
                }
                if (next.Pair is { } pair && next.Error is { } error)
                {
                    if (notice is not null) CloseOrReport(pair, next.Path, notice, error);
                    else
                    {
                        lock (_gate) _pairHydrationCauses.Add(next.PairId);
                        StorageNotices.Report(_store, pair, string.Empty,
                            new StorageNotice(pair.A.RootId, "", pair.B.RootId, StorageNotices.HydrationPairOperation),
                            new StoragePairException("backlog", "A hydration failed and its item identity could not be read. " +
                                "Retry the read or rescan the pair; Ignore dismisses this cause. " + error.Message));
                        PersistedFailures++;
                    }
                }
                return;
            }
            default:
            {
                StorageNotice? known;
                bool incomplete;
                bool closePair;
                lock (_gate)
                {
                    _open.TryGetValue((next.PairId, ItemKey(next.LocalObjectId, next.Path)), out known);
                    // Read the path alias too; remove neither key until durable closure succeeds.
                    if (_open.TryGetValue((next.PairId, "path:" + next.Path), out var pathKnown)) known ??= pathKnown;
                    incomplete = _incompletePairs.Contains(next.PairId);
                    // A real successful read proves the pair's network/auth/provider path works. Close its pair cause
                    // family once per process (prior-process notice included) and after any new pair cause report.
                    var first = !_hydratedPairs.Contains(next.PairId);
                    var caused = _pairHydrationCauses.Contains(next.PairId);
                    closePair = first || caused;
                }
                if (known is not null) closures.Add((next.PairId, known));
                else if (incomplete && next.Resolve is { } resolve)
                {
                    // The bounded cache dropped or could not hold this item: close by exact identity once per path,
                    // so an incomplete cache never means a placeholder open or database write on every range.
                    bool first;
                    lock (_gate)
                    {
                        if (_probed.Count > MaximumOpenCache * 2) _probed.Clear();
                        first = !_probed.Contains((next.PairId, next.Path));
                    }
                    if (!first) return;
                    // A transient resolver failure is also unacknowledged work: retry it, not a false close.
                    var probed = resolve(next.Path);
                    if (probed is not null) closures.Add((next.PairId, probed));
                }
                if (closePair) pairClosures.Add(next.PairId);
                return;
            }
        }
    }

    private void CloseOrReport(PairDef pair, string path, StorageNotice notice, Exception error)
    {
        StorageNotices.Report(_store, pair, path, notice, error);
        PersistedFailures++;
    }

    /// <summary>Persistence exceptions propagate to the retained delivery batch, never to the callback.</summary>
    private void FlushClosures(List<(string PairId, StorageNotice Item)> closures)
    {
        foreach (var group in closures.GroupBy(entry => entry.PairId))
        {
            var items = group.Select(entry => entry.Item).ToList();
            _store.CloseStorageNoticeBatch(group.Key, items);
            ClosedNotices += items.Count;
        }
    }

    /// <summary>Closes only the recovered pair's hydration cause family.</summary>
    private void FlushPairClosures(HashSet<string> pairClosures)
    {
        foreach (var pairId in pairClosures)
        {
            PairDef? pair;
            lock (_gate) pair = _pairDefs.TryGetValue(pairId, out var found) ? found : null;
            if (pair is null) continue;
            StorageNotices.CompleteHydrationPair(_store, pair);
            _store.ClearStorageRootBackoff(pair.A.RootId);
            ClosedPairCauses++;
        }
    }

    /// <summary>One deduplicated pair-level notice for a burst that overflowed the bounded queue. Written after the
    /// queue drains, never from a callback; Retry or Ignore applies to the cause and no failed read is hidden.</summary>
    private bool FlushOverflow()
    {
        bool queued = false;
        lock (_gate)
        {
            foreach (var pairId in _overflowPairs.ToArray())
            {
                if (!_overflowPairDefs.TryGetValue(pairId, out var pair)) continue;
                _pairHydrationCauses.Add(pairId);
                if (!EnqueueLocked(NoticeKind.PairFailure, pairId, "", pair,
                    new StoragePairException("backlog",
                        "Some hydration reads failed while the notice queue was busy. Retry the read, or Ignore this cause."),
                    null, "backlog")) break;
                _overflowPairs.Remove(pairId);
                _overflowPairDefs.Remove(pairId);
                queued = true;
            }
        }
        return queued;
    }

    /// <summary>Caches a notice under its primary identity key and, when different, the path alias so a success
    /// without a callback identity (or a prior-process prime) still closes it. Both entries share the cache bound.</summary>
    private bool RememberLocked((string PairId, string Key) primary, StorageNotice item,
        (string PairId, string Key) alias)
    {
        var stored = StoreLocked(primary, item);
        if (stored && alias != primary) StoreLocked(alias, item);
        return stored;
    }

    private bool StoreLocked((string PairId, string Key) key, StorageNotice item)
    {
        if (_open.ContainsKey(key))
        {
            _open[key] = item;
            return true;
        }
        while (_open.Count >= MaximumOpenCache)
        {
            if (!_openOrder.TryDequeue(out var oldest))
            {
                _incompletePairs.Add(key.PairId);
                return false;
            }
            if (_open.Remove(oldest)) _incompletePairs.Add(oldest.PairId);
        }
        _open[key] = item;
        _openOrder.Enqueue(key);
        if (_openOrder.Count > MaximumOpenOrder)
        {
            // Drop stale (already closed) entries without changing which live entries are cached.
            _openOrder.Clear();
            foreach (var live in _open.Keys) _openOrder.Enqueue(live);
        }
        return true;
    }

    private enum NoticeKind { Failure, Success, Prime, PairFailure, Barrier }

    private sealed record NoticeEvent(NoticeKind Kind, string PairId, string Path, string? LocalObjectId, PairDef? Pair,
        Exception? Error, Func<string, StorageNotice?>? Resolve, TaskCompletionSource? Barrier = null);
}
