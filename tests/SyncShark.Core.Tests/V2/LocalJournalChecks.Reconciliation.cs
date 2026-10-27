namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static Task ReconcileWalkFallbackUsesFileUsn(string folder) =>
        RunChecks.ReconciliationLocalSelection(folder, discovery: false);
}
