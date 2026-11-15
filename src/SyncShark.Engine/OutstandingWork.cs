namespace Massdex.SyncShark.Engine;

/// <summary>Recovery preparation results needed by Repair's listing and completion summary.</summary>
public sealed record OutstandingWork(bool FirstRun, bool RecoveredPending, bool NeedsDeletedNames);
