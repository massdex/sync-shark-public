namespace Massdex.SyncShark.V2;

/// <summary>Operational discovery position; file facts stay on entity-keyed work rows.</summary>
internal sealed record LocalGapEpoch(string Epoch, string JournalCursor, long Boundary, bool TrustFileUsn,
    string AfterObject, bool DiscoveryDone, string Reason = "journal history discontinuity");
