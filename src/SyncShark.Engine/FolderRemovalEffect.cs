namespace Massdex.SyncShark.Engine;

/// <summary>A frozen selected effect identified by its durable operation member, not its wire ordinal.</summary>
public sealed record FolderRemovalEffect(string OperationId, long ItemOrdinal, long EffectGeneration,
    FolderEffectKind Kind, Entry? Expected);
