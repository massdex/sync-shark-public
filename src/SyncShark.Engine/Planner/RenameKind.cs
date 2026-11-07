namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Rename outcome; None means a rebase or vanished saved link leaves the ordinary row pass to decide.</summary>
public enum RenameKind { None, FinishCaseRename, Follow, Adopt, Split, Leave, Hold, Notice, NeedsFresh }
