namespace Massdex.SyncShark.Engine;

/// <summary>What the Planner decided for one row (section 6.1). Root, because SyncOutcome reports it.</summary>
public enum ActionKind { None, Record, Upload, Download, EnsureDropboxFolder, EnsureLocalFolder, DeleteDropbox,
    DeleteLocal, MoveDropbox, MoveLocal, MoveUnlinked, NeedLocalHash, KeepBoth, ReplaceKind, ForgetOnly, Notice,
    DeferFolder, NeedDeletionEvidence }
