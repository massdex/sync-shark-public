namespace Massdex.SyncShark.V2;

internal static class RemoteEntries
{
    internal static PilotRemoteEntry ToEntry(ItemState state) => new(
        state.Path,
        state.Kind == ItemKind.Folder ? PilotEntryKind.Directory : PilotEntryKind.File,
        state.ObjectId, state.VersionId, state.Size, state.Hash, state.Mtime);
}
