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
