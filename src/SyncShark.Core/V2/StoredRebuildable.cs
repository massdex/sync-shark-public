namespace Massdex.SyncShark.V2;

/// <summary>One frozen rebuildable folder: its category and the exact pair-relative path the prepare detected.</summary>
internal sealed record StoredRebuildable(string Category, string Path);
