namespace Massdex.SyncShark.V2;

public interface IDeferredDeletionEvidence
{
    /// <summary>Classifies folders from already-loaded tombstones only; never probes folder revision history.</summary>
    IReadOnlyList<string> FolderDeletionCandidates(IReadOnlyList<string> paths) => [];

    /// <summary>True only for a current exact tombstone at this path or an ancestor; false when listing is unavailable.</summary>
    bool IsAtOrUnderExactTombstone(string path) => false;

    Task<DeletionEvidencePreparation> PrepareDeletionEvidenceAsync(IReadOnlyList<string> paths, CancellationToken ct);
    Task ResolveDeletionEvidenceAsync(IReadOnlyList<string> paths,
        Action<string, SyncRemoteDeletionEvidence> resolved, CancellationToken ct);
}
