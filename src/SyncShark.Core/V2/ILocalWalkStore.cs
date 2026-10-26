namespace Massdex.SyncShark.V2;

internal interface ILocalWalkStore
{
    string BeginLocalWalk(RootDef root, string options, NtfsUsnCheckpoint? current);
    bool HasLocalWalk(string root);
    string? LocalWalkPage(string root, int ordinal);
    int LocalWalkPageCount(string root);
    void SaveLocalWalkPages(string root, IReadOnlyList<(int Ordinal, string Payload)> pages);
    SemaphoreSlim LocalWalkGate(string root);
    void ForgetLocalWalk(string root);
    void RetireLocalWalk(string root) => ForgetLocalWalk(root);
    bool GapInventoryPending(string rootId);
}
