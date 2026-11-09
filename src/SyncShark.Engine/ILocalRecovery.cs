namespace Massdex.SyncShark.Engine;

/// <summary>Local crash recovery and restore detection; implemented by LocalSide (sections 4.1, 4.6).</summary>
public interface ILocalRecovery
{
    /// <summary>What an armed allocation's exact path holds; a present object is never owned without a saved StageProof.</summary>
    enum AllocationOccupancy { Absent, PresentOwnershipUnknown, Unreadable }

    /// <summary>Applies the choice to the pair's pending slot; Check never resends an Armed or Unknown removal and a legacy note is only observed.</summary>
    Task<RemovalResult> ResolvePendingAsync(PendingRead pending, RecoveryChoice choice, CancellationToken ct);
    /// <summary>Removes owned stale stages except the named pending stages; returns the caught operational cleanup failures.</summary>
    IReadOnlyList<(string Path, Exception Error)> RemoveStaleStages(IReadOnlySet<string> pendingStageNames);
    /// <summary>The root volume's drive evidence; unavailable fields are null.</summary>
    DriveEvidence Drive();
    /// <summary>Whether a new local replacement can arm the pending slot.</summary>
    bool CanArmReplace();
    /// <summary>Builds removal proof; a missing hash reads a file with the same id, size and time.</summary>
    Task<RemovalProof> ProveRemovalAsync(Entry expected, string? hash, CancellationToken ct);
    /// <summary>Plans a recovery stage's exact locator with no file-system effect; the locator proves no ownership.</summary>
    StageAllocation PlanRecoveryStage(string operationId, long ordinal);
    /// <summary>Creates only the persisted allocation's exact path, never adopting an occupant, and stages the selected bytes.</summary>
    Task<IRecoveryStage> StageRecoveryAsync(StageAllocation allocation, Entry selected, Stream revisionContent,
        CancellationToken ct);
    /// <summary>Observes an allocation's exact path without following links or changing bytes.</summary>
    Task<AllocationOccupancy> InspectRecoveryAllocationAsync(StageAllocation allocation, CancellationToken ct);
    /// <summary>Opens the exact recorded object after verifying its identity, size and hash; the stream is seekable and held.</summary>
    Task<Stream> OpenRecoveryStageAsync(StageProof proof, CancellationToken ct);
    /// <summary>Deletes only the exact recorded object through its held file; a substituted object is never removed.</summary>
    Task DiscardRecoveryStageAsync(StageProof proof, CancellationToken ct);
}
