namespace Massdex.SyncShark.Engine;

/// <summary>The resolution choices of D29. Edge maps the CLI values to these, so no other module compares CLI text.</summary>
public enum ResolutionChoice { UseLocal, UseDropbox, KeepBoth, Dismiss, RenameIncoming, ReplaceExisting, KeepLocal,
    KeepDropbox, RenameLocal, RenameDropbox, Retry, FirstSync, SyncNormally, KeepFolderAlways, BindRoots, Ignore, Unignore }
