namespace Massdex.SyncShark.Engine.Executor;

/// <summary>The preparation decision, optional operation result and its observed row.</summary>
internal readonly record struct CheckedFolderRemoval(FolderRemovalCheck Check, FolderRemovalResult? Removal, Row Observed);
