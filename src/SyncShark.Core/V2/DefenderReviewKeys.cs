namespace Massdex.SyncShark.V2;

internal static class DefenderReviewKeys
{
    internal static string SharedReleaseKey(string rootId, string objectId) => "defender-held-release:" + rootId + ":" + objectId;

    internal static string HeldBatch(string rootId) => "held:" + rootId;

    internal static string ReviewKey(string rootId, string batch) => "defender-history-review:" + rootId + ":" + batch;

    internal static string HeldKey(string rootId) => "defender-held:" + rootId;

    internal static readonly TimeSpan KeepRecheck = TimeSpan.FromDays(7);
}
