namespace Massdex.SyncShark.V2;

internal interface ILocalGapProbe : IDisposable
{
    LocalGapProof? Probe(ItemState item);
    string? UnavailableReason => null;
    string? LastFailureReason => null;
    int? LastOpenError => null;
}
