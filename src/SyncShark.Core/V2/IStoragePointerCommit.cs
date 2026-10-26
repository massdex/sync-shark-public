namespace Massdex.SyncShark.V2;

internal interface IStoragePointerCommit
{
    bool StoragePointerUpdateIsStale(PairDef pair, SyncAction action);
}
