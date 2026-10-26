namespace Massdex.SyncShark.V2;

internal static class FolderExpansion
{
    internal static bool Expands(SyncAction action) =>
        action.Kind == ActionKind.Delete && action.ExpectedTarget?.Kind == ItemKind.Folder &&
        !action.DescendantsPlanned;
}
