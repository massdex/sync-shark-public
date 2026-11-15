namespace Massdex.SyncShark.Engine;

/// <summary>Split-folder custody phases, persisted by name. Only a proven create leaves Armed.</summary>
public enum FolderMovePhase { Armed, Filling, Filled, Relabelled }
