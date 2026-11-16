namespace Massdex.SyncShark;

public sealed class PilotSettingsStore
{
    private readonly MigratingMutableJsonStore<PilotSettings> _store;

    public PilotSettingsStore(PilotStateAuthority authority) =>
        _store = new MigratingMutableJsonStore<PilotSettings>(authority, SyncSharkRuntimePaths.SettingsFileName);

    public PilotSettings? Load()
    {
        var settings = _store.Load();
        return settings is null ? null : Normalize(settings);
    }

    public Task SaveAsync(PilotSettings settings, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(Normalize(settings), cancellationToken);

    private static PilotSettings Normalize(PilotSettings settings)
    {
        if (settings.LocalVolumeSerialNumber == 0 || settings.LocalRootFileId == 0)
            throw new InvalidDataException("The saved local folder identity is incomplete.");
        if (!PilotPath.IsDropboxFolderIdentity(settings.DropboxFolderId))
            throw new InvalidDataException("The saved Dropbox folder ID is incomplete.");
        return settings with
        {
            LocalRoot = PilotPath.NormalizeLocalRoot(settings.LocalRoot),
            DropboxRoot = PilotPath.NormalizeDropboxRoot(settings.DropboxRoot)
        };
    }
}
