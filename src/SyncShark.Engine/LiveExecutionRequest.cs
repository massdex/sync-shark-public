namespace Massdex.SyncShark.Engine;

/// <summary>One live execution request (LIVE 2): the pair's finite selected work and its complete declared effect
/// footprint, with no root-proof evidence.</summary>
internal sealed record LiveExecutionRequest(
    WorkSelection Selection, LiveCoverage Coverage, IReadOnlyList<LiveEffectScope> Footprint);
