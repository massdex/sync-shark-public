namespace Massdex.SyncShark.Engine.Run;

/// <summary>One pair's contribution to a live root admission (LIVE 2): its session, finite selection, committed
/// source coverage and complete declared effect footprint, with no root-proof evidence.</summary>
internal sealed record LiveRootRequest(
    PairSession Session,
    WorkSelection Selection,
    LiveCoverage Coverage,
    IReadOnlyList<LiveEffectScope> Footprint);
