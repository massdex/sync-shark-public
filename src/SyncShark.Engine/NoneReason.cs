namespace Massdex.SyncShark.Engine;

/// <summary>Why a row needs no effect (section 6.1).</summary>
public enum NoneReason { InSync, OutOfScope, Skipped, Held, RemovedWithFolder }
