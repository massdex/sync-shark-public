namespace Massdex.SyncShark.V2;

/// <summary>The kind of effect the planner chooses for a row.</summary>
public enum ActionKind { CopyNew, Update, Move, Delete, MkDir, Adopt, Forget, Hold, KeepBoth }
