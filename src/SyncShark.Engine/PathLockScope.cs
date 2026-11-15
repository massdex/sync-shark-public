namespace Massdex.SyncShark.Engine;

/// <summary>A canonical lock root and whether its descendants are included.</summary>
public sealed record PathLockScope(string Key, bool IncludesDescendants);
