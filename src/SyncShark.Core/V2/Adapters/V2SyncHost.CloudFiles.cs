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
            var binding = PairMapping.CloudFilesBinding(pair);
            // Already open: validate the exact binding only. No registry or native probe per pointer/action.
            if (_open.TryGetValue(pair.PairId, out var prior))
            {
                prior.Binding.RequireSame(binding);
                return;
            }
            if (!create && !hasRegistration(pair)) return;
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
                StorageNotice? Item(string path)
                {
                    var full = LocalItems.FullPath(pair.A.RootPath, path);
                    if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var pointer) || pointer is null ||
                        !pointer.Identity.IsBoundTo(pair.PairId, pair.B.Provider)) return null;
                    var (volume, _) = LocalItems.ParseObjectId(pair.A.RootObjectId);
                    return new(pair.A.RootId, SyncLocalEndpointStore.ObjectId(volume, pointer.FileId), pair.B.RootId, "hydration");
                }
                native.FetchFailed += (path, error) => writer.Report(pair, path, error, Item);
                // A success costs one in-memory lookup once the pair's durable notices are primed: no placeholder
                // open and no database write unless a hydration notice is open for this exact path.
                native.FetchSucceeded += path => writer.Succeeded(pair.PairId, path, Item);
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
/// coalesce by item, so a burst of callbacks cannot grow memory with the item count. A single worker drains them in
/// bounded batches. On the first success for a pair it loads the durable hydration notices once, so a notice left by a
/// previous process still closes; after that a success for a path without an open notice costs no placeholder open and
/// no database write.
/// </remarks>
internal sealed class HydrationNoticeWriter : IDisposable
{
    private const int BatchLimit = 128;
    internal const int MaximumQueuedEvents = 4096;
    internal const int MaximumOpenCache = 16_384;
    private const int MaximumOpenOrder = MaximumOpenCache * 4;
    private readonly SqliteStateStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<(string PairId, string Path), StorageNotice> _open = new();
    private readonly Queue<(string PairId, string Path)> _openOrder = new();
    private readonly Dictionary<(string PairId, string Path), NoticeEvent> _pendingEvents = new();
    private readonly Queue<(string PairId, string Path)> _eventOrder = new();
    private readonly HashSet<(string PairId, string Path)> _queued = new();
    private readonly HashSet<(string PairId, string Path)> _inFlight = new();
    private readonly HashSet<string> _primedPairs = new();
    private readonly HashSet<string> _incompletePairs = new();
    private readonly HashSet<string> _overflowPairs = new();
    private readonly Dictionary<string, PairDef> _overflowPairDefs = new();
    private readonly HashSet<(string PairId, string Path)> _probed = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
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

    /// <summary>Queues one failed hydration; never opens a file or touches the store on the calling thread.</summary>
    internal void Report(PairDef pair, string path, Exception error, Func<string, StorageNotice?> resolve)
    {
        lock (_gate)
        {
            if (_disposed) return;
            EnqueueLocked(new NoticeEvent(NoticeKind.Failure, pair.PairId, path, pair, error, resolve));
        }
        Signal();
    }

    /// <summary>
    /// Records a successful hydration. Returns false, without any work, when the pair's durable notice state is
    /// already loaded and no notice is open for the path. The first success for a pair loads that state once.
    /// </summary>
    internal bool Succeeded(string pairId, string path, Func<string, StorageNotice?>? resolve)
    {
        var key = (pairId, path);
        bool queued;
        lock (_gate)
        {
            if (_disposed) return false;
            if (!_primedPairs.Contains(pairId))
            {
                // Prime before this success so a durable notice from a previous process can be closed.
                queued = EnqueueLocked(new NoticeEvent(NoticeKind.Prime, pairId, string.Empty, null, null, null));
                queued |= EnqueueLocked(new NoticeEvent(NoticeKind.Success, pairId, path, null, null, resolve));
            }
            else if (!_incompletePairs.Contains(pairId) &&
                     !_pendingEvents.ContainsKey(key) && !_inFlight.Contains(key) && !_open.ContainsKey(key))
                return false;
            else
                queued = EnqueueLocked(new NoticeEvent(NoticeKind.Success, pairId, path, null, null, resolve));
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
        catch
        {
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
                var key = (notice.PairId, notice.Path);
                if (_open.ContainsKey(key)) continue;
                if (!RememberLocked(key, item))
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

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stop.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* Store failures are recounted by the next failed read. */ }
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
                try { await _signal.WaitAsync(_stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                Process();
            }
        }
        finally
        {
            // Best-effort drain of events already observed before shutdown.
            Process();
        }
    }

    /// <summary>Latest event per item; a replacement keeps the item's queue position. Never grows past the bound.</summary>
    private bool EnqueueLocked(NoticeEvent next)
    {
        var key = (next.PairId, next.Path);
        if (_pendingEvents.ContainsKey(key))
        {
            _pendingEvents[key] = next;
            return true;
        }
        if (_pendingEvents.Count >= MaximumQueuedEvents)
        {
            DroppedEvents++;
            // Never lose failure reporting entirely: remember one pair-level backlog notice for the worker.
            if (next.Kind == NoticeKind.Failure && next.Pair is { } pair)
            {
                _overflowPairs.Add(next.PairId);
                _overflowPairDefs[next.PairId] = pair;
            }
            return false;
        }
        _pendingEvents[key] = next;
        if (_queued.Add(key)) _eventOrder.Enqueue(key);
        return true;
    }

    private void Process()
    {
        while (true)
        {
            var batch = new List<NoticeEvent>(BatchLimit);
            lock (_gate)
            {
                while (_eventOrder.Count > 0 && batch.Count < BatchLimit)
                {
                    var key = _eventOrder.Dequeue();
                    _queued.Remove(key);
                    if (!_pendingEvents.Remove(key, out var next)) continue;
                    if (next.Kind == NoticeKind.Failure) _inFlight.Add(key);
                    batch.Add(next);
                }
            }
            if (batch.Count == 0)
            {
                FlushOverflow();
                return;
            }
            var closures = new List<(string PairId, StorageNotice Item)>();
            foreach (var next in batch) Apply(next, closures);
            FlushClosures(closures);
        }
    }

    private void Apply(NoticeEvent next, List<(string PairId, StorageNotice Item)> closures)
    {
        var key = (next.PairId, next.Path);
        switch (next.Kind)
        {
            case NoticeKind.Prime:
                Prime(next.PairId);
                return;
            case NoticeKind.Failure:
            {
                StorageNotice? notice = null;
                try { notice = next.Resolve?.Invoke(next.Path); }
                catch { /* An unreadable placeholder cannot name the item; a later read reports again. */ }
                lock (_gate)
                {
                    _inFlight.Remove(key);
                    if (notice is not null) RememberLocked(key, notice);
                }
                if (notice is not null && next.Pair is { } pair && next.Error is { } error) CloseOrReport(pair, next.Path, notice, error);
                return;
            }
            default:
            {
                StorageNotice? known;
                bool incomplete;
                lock (_gate)
                {
                    _open.Remove(key, out known);
                    incomplete = _incompletePairs.Contains(next.PairId);
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
                        first = _probed.Add(key);
                    }
                    if (!first) return;
                    StorageNotice? probed = null;
                    try { probed = resolve(next.Path); }
                    catch { }
                    if (probed is not null) closures.Add((next.PairId, probed));
                }
                return;
            }
        }
    }

    private void CloseOrReport(PairDef pair, string path, StorageNotice notice, Exception error)
    {
        try
        {
            StorageNotices.Report(_store, pair, path, notice, error);
            PersistedFailures++;
        }
        catch
        {
            // The item is still unchanged; a later failed read queues the notice again.
        }
    }

    /// <summary>One indexed SQL transaction closes a bounded batch of families (the store caps the list).</summary>
    private void FlushClosures(List<(string PairId, StorageNotice Item)> closures)
    {
        if (closures.Count == 0) return;
        foreach (var group in closures.GroupBy(entry => entry.PairId))
        {
            var items = group.Select(entry => entry.Item).ToList();
            try
            {
                _store.CloseStorageNoticeBatch(group.Key, items);
                ClosedNotices += items.Count;
            }
            catch
            {
                // A notice left open is recoverable with Retry; the hydration itself already succeeded.
            }
        }
        closures.Clear();
    }

    /// <summary>One deduplicated pair-level notice for a burst that overflowed the bounded queue. Written after the
    /// queue drains, never from a callback; Retry or Ignore applies to the cause and no failed read is hidden.</summary>
    private void FlushOverflow()
    {
        List<(string PairId, PairDef Pair)> overflow;
        lock (_gate)
        {
            if (_overflowPairs.Count == 0) return;
            overflow = _overflowPairs
                .Where(pairId => _overflowPairDefs.ContainsKey(pairId))
                .Select(pairId => (pairId, _overflowPairDefs[pairId]))
                .ToList();
            _overflowPairs.Clear();
            _overflowPairDefs.Clear();
        }
        foreach (var (pairId, pair) in overflow)
        {
            try
            {
                StorageNotices.Report(_store, pair, string.Empty,
                    new StorageNotice(pair.A.RootId, string.Empty, pair.B.RootId, "hydration"),
                    new StoragePairException("hydration-backlog",
                        "Some hydration reads failed while the notice queue was busy. Retry reading those files, or Ignore this cause."));
            }
            catch { /* Ordinary failed reads still report their own per-item notice after the burst. */ }
        }
    }

    private bool RememberLocked((string PairId, string Path) key, StorageNotice item)
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

    private enum NoticeKind { Failure, Success, Prime }

    private sealed record NoticeEvent(NoticeKind Kind, string PairId, string Path, PairDef? Pair, Exception? Error,
        Func<string, StorageNotice?>? Resolve);
}
