namespace Massdex.SyncShark.V2;

/// <summary>A single inventory change at a cursor position.</summary>
public sealed record Change(ChangeKind Kind, string ObjectId, string Path, ItemState? State, DateTimeOffset? DeletedUtc);
