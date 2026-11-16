namespace Massdex.SyncShark.Engine;

/// <summary>Durable custody of one split folder. Launch preserves provider wire order until every member is accounted for.</summary>
public sealed record FolderMove(string SourceId, string RootId, string? ParentId, string SourcePath,
    string DestinationPath, string? DestinationId, FolderMovePhase Phase,
    IReadOnlyList<(string Id, string Name)>? Launch, string? JobId, long Generation);
