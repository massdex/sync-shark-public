namespace Massdex.SyncShark.V2;

public sealed partial class V2SyncHost
{
    private CloudFilesLifetime? _cloudFiles;

    private void ReconnectCloudFiles(CancellationToken ct)
    {
        if (Composition is null) return;
        _cloudFiles ??= new CloudFilesLifetime(Composition.Endpoints.CreateCloudFiles,
            pair => Directory.Exists(pair.A.RootPath) &&
                WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(pair.A.RootPath) is not null &&
                !WindowsCloudFilesProvider.IsStatusOnlySyncRoot(PairMapping.ToSyncPair(pair, false, _settings)));
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
    Func<PairDef, bool> hasRegistration) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (WindowsCloudFilesBinding Binding, ICloudFilesConnection Connection)> _open = new();
    private bool _disposed;

    internal IReadOnlyList<string> Reconnect(IEnumerable<PairDef> pairs, CancellationToken ct,
        Action<PairDef, Exception>? report = null)
    {
        var failures = new List<string>();
        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();
            if (pair.A.Provider != SyncProviders.Local || pair.B.Provider != SyncProviders.Dropbox) continue;
            try { Open(pair, false, false, SyncDirectoryStorage.AlwaysAvailable, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add(pair.Name + ": Cloud Files needs attention: " + error.Message);
                report?.Invoke(pair, error);
            }
        }
        return failures;
    }

    internal void Open(PairDef pair, bool create, bool preview, SyncDirectoryStorage defaultStorage,
        CancellationToken ct)
    {
        // Preview must not even probe native registration or open the provider.
        if (preview) return;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (!_open.ContainsKey(pair.PairId) && !create && !hasRegistration(pair)) return;
            var binding = PairMapping.CloudFilesBinding(pair);
            if (_open.TryGetValue(pair.PairId, out var prior))
            {
                prior.Binding.RequireSame(binding);
                return;
            }
            var connection = createConnection(pair, binding, defaultStorage);
            try
            {
                ct.ThrowIfCancellationRequested();
                connection.RegisterAndConnect();
                ct.ThrowIfCancellationRequested();
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

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _open.Values) entry.Connection.Dispose();
            _open.Clear();
        }
    }
}
