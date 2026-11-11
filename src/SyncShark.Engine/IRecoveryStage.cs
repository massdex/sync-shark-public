namespace Massdex.SyncShark.Engine;

/// <summary>Exposes a recovery stage's identity and content proof without depending on its local implementation.</summary>
public interface IRecoveryStage : IStaged
{
    StageProof Proof { get; }
}
