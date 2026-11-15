namespace Massdex.SyncShark.Engine;

/// <summary>One endpoint's shared execution-admission request (LIVE 3): its pair at a generation, its account
/// controller, whether the operation is priority, and every lock key of its complete footprint.</summary>
internal sealed record ExecutionRequest(
    string PairId, long PairGeneration, string AccountOrderKey, IAdmission Admission,
    bool Priority, IReadOnlyList<PathLockScope> Footprint);
