namespace Massdex.SyncShark.V2;

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
