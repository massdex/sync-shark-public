namespace Massdex.SyncShark.V2;

public sealed record StorageFolderSummary(string ObjectId, string Path, long Items, long Hydrated, long Dehydrated)
{
    public long Unknown => Items - Hydrated - Dehydrated;
}
