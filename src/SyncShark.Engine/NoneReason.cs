namespace Massdex.SyncShark.Engine;

/// <summary>Why a row needs no effect (section 6.1). Unfinished: a temporary, proven no-effect write left its work
/// retained for a timed retry; it is not success.</summary>
public enum NoneReason { InSync, OutOfScope, Skipped, Held, RemovedWithFolder, Unfinished }
