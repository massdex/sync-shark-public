namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Records and releases a protecting Local member through the Store, as Repair and a proved walk do.</summary>
internal static class UnreadableMember
{
    internal const string Id = "12345";

    public static void Record(IStateStore store, string pairId, string path, string id = Id) =>
        store.ObserveSkipped(pairId, Generation(store, pairId), Side.Local, path,
            (TextKey.SkipUnreadable, new IssueSubject([new IssueItem(Side.Local, path, id)], null)));

    public static void Release(IStateStore store, string pairId, string path, string id = Id) =>
        Check.Require(store.ReleaseUnreadable(pairId, Generation(store, pairId), PathNames.Key(path), id),
            nameof(Release));

    private static long Generation(IStateStore store, string pairId) =>
        store.ReadPairExecution(pairId)!.Value.Generation;
}
