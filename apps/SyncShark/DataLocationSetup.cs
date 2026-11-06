using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark;

/// <summary>Explicit first-install store creation, separate from normal startup resolution.</summary>
internal static class DataLocationSetup
{
    internal static void Initialize(string root) => SyncDataLocation.InitializeNewStore(root, static location =>
    {
        using var store = StateStore.Open(StateStore.DatabasePath(location), static (_, _, _, _, _) => false);
    });
}
