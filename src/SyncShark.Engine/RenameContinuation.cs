namespace Massdex.SyncShark.Engine;

/// <summary>Historical destination and exact clash retained by incoming work across removal and rename settlement.</summary>
public sealed record RenameContinuation(SyncedEntry Destination, Issue? Clash);
