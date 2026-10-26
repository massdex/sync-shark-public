namespace Massdex.SyncShark.V2;

/// <summary>
/// One kind of rebuildable folder the first sync can offer to stop syncing: the stable id used in the first-sync
/// choices document and the plain-English explanation shown next to its recommendation in the prepare report.
/// </summary>
internal sealed record RebuildableCategory(string Id, string Explanation);
