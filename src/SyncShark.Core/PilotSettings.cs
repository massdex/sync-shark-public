namespace Massdex.SyncShark;

public sealed record PilotSettings(
    string LocalRoot,
    uint LocalVolumeSerialNumber,
    ulong LocalRootFileId,
    string DropboxRoot,
    string DropboxFolderId);
