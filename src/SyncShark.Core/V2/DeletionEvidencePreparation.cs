namespace Massdex.SyncShark.V2;

/// <summary>First-sync evidence classification is cheap; only the unresolved probe candidates are held.</summary>
public sealed record DeletionEvidencePreparation(IReadOnlyDictionary<string, SyncRemoteDeletionEvidence> Resolved,
    IReadOnlyList<string> Pending);
