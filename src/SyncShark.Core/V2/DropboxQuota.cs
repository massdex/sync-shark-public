namespace Massdex.SyncShark.V2;

// One durable pair-level hold. It only gates uploads, never downloads, moves or deletions.
internal sealed class DropboxQuota(PairDef? pair, bool suspended)
{
    internal const string NoticeKey = "dropbox-storage-full";
    internal const string Detail = "Dropbox storage full. Free Dropbox storage or upgrade its quota, then Retry. Uploads for this pair are suspended; downloads and other work continue.";
    private int _suspended = suspended ? 1 : 0;
    internal static string Id(string pairId) => ShortId.Make("n", pairId, "40", "", NoticeKey);
    internal static Notice Notice(string pairId, Side side, long? needBytes) => new(pairId, 40, NoticeKind.Info, "", Detail, NoticeKey, UploadSuspended: true, UploadQuotaSide: side, UploadQuotaNeedBytes: needBytes);
    internal bool Suspend() => Interlocked.Exchange(ref _suspended, 1) == 0;
    internal void CheckUploadStart(SyncAction action)
    {
        if (Blocks(action))
            throw new ProviderWriteBlockedException("insufficient_space", Detail,
                new InvalidOperationException("The upload quota hold is active; no bytes were sent."));
    }
    internal bool Blocks(SyncAction action) => pair is not null &&
        string.Equals((action.Target == Side.A ? pair.A : pair.B).Provider,
            SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase) &&
        action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth &&
        Volatile.Read(ref _suspended) != 0;
}
