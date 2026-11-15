namespace Massdex.SyncShark.Engine;

public partial interface IStateStore
{
    /// <summary>Inserts, replaces or retires one split row, fencing execution separately from its row generation.</summary>
    void SetFolderMove(string pairId, long expectedGeneration, FolderMove? expected, FolderMove? next) =>
        throw new NotSupportedException();

    /// <summary>Loads one root's split rows once for the fill owner to maintain during its claim.</summary>
    IReadOnlyList<FolderMove> FolderMoves(string pairId, string rootId) => throw new NotSupportedException();

    /// <summary>Streams roots by identity, releasing the reader before each yield.</summary>
    IEnumerable<FolderMove> FolderMoveRoots(string pairId, CancellationToken ct);

    /// <summary>The move hold for an identity, including a dismissed hold.</summary>
    Issue? MoveResponsibility(string pairId, string dropboxId) => throw new NotSupportedException();

    /// <summary>Restores missing split responsibility before any work selection, without provider effects.</summary>
    void HoldFolderMoves(string pairId);

    /// <summary>Whether current source work at or below the path, or an undischarged descendant notice, blocks cleanup.</summary>
    bool HasPendingUnder(string pairId, string path) => throw new NotSupportedException();
}
