namespace Massdex.SyncShark.V2;

public sealed partial class ProductionEndpoints
{
    internal void BindEligibility(SyncRun run)
    {
        if (_gapIo is null) return;
        _gapIo.EligibilityHeld = run.EligibilityHold;
        _gapIo.EligibilityPass = run.ProofEligibilityHolds;
    }

}
