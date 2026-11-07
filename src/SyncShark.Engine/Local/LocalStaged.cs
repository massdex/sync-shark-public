namespace Massdex.SyncShark.Engine.Local;

/// <summary>A local stage file (section 4.4). Dispose: a Staged stage is deleted only when no durable record owns it,
/// PendingOwned leaves it for recovery, Published does nothing; every case calls released(StagePath).</summary>
public sealed class LocalStaged(string path, Entry source, StageProof proof, Func<bool> durablyOwned,
    Action<string> released) : IRecoveryStage
{
    /// <summary>The pair-relative destination path.</summary>
    public string Path { get; } = path;
    /// <summary>The stage's exact file identity and content proof; publication and recovery compare against it.</summary>
    public StageProof Proof { get; } = proof;
    /// <summary>The full path of the temporary stage file.</summary>
    public string StagePath => Proof.Path;
    /// <summary>The entry whose content the stage holds.</summary>
    public Entry Source { get; } = source;
    /// <summary>A stage is fully written before it exists, so publication never reads its source.</summary>
    public bool ConsumesSourceOnPublish => false;
    /// <summary>Whether the stage is still owned by the transfer, left for recovery, or published.</summary>
    public LocalStageState State { get; internal set; } = LocalStageState.Staged;

    /// <summary>Deletes an unowned stage, leaves a durably or pending owned one for recovery, and always reports
    /// the stage path released.</summary>
    public ValueTask DisposeAsync()
    {
        try
        {
            if (State == LocalStageState.Staged && !durablyOwned()) DeleteOwnedStage();
        }
        catch (Exception error) when (LocalErrors.IsManagedIoFailure(error))
        {
            // Best-effort cleanup must not mask the transfer failure; the next walk retries it.
        }
        finally
        {
            released(StagePath);
        }
        return ValueTask.CompletedTask;
    }

    // The proof's identity, not the pathname, authorizes the delete: a replacement occupant survives.
    private void DeleteOwnedStage()
    {
        using var held = SyncLocalFileAuthority.OpenExisting(StagePath);
        if (!LocalSide.Names(held.ReadIdentity(), Proof)) return;
        held.DeleteOnClose();
    }
}
