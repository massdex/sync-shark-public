namespace Massdex.SyncShark.Engine.Executor;

/// <summary>The work row a stage-progress fact names: its notice side, durable work id and path.</summary>
internal readonly record struct RowStage(Side Side, long WorkId, string Path);
