namespace Massdex.SyncShark.V2;

/// <summary>
/// One rebuildable folder found in a local inventory: its category id, its pair-relative '/'-path, and how many files
/// and bytes it holds (recursively), which is what the recommendation and the choice effects count.
/// </summary>
public sealed record RebuildableFolder(string Category, string Path, int Files, long Bytes);
