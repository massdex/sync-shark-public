using Draft = System.Collections.Generic.List<(Massdex.SyncShark.V2.PlanRow Row, Massdex.SyncShark.V2.SyncAction Action)>;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Planner handling of the user's rebuildable-folder choice (docs/design/rebuildables.md, rule 32): on a first sync
/// nothing under a chosen folder is planned on either side (Dispatch skips those rows), and one folder Delete removes
/// the cloud copy on the pair's own cloud side. The local copy is never uploaded, removed or otherwise touched.
/// </summary>
public static partial class Planner
{
    /// <summary>
    /// The chosen pair-relative folder paths reduced to their outermost roots, deduplicated ignoring case and
    /// ordinally sorted. The inventory spelling is kept exactly (no trimming); only a trailing '/' is accepted and
    /// dropped, which the inventory never produces.
    /// </summary>
    private static IReadOnlyList<string> NormalizeRebuildables(RebuildableSelection? selection)
    {
        if (selection is null || selection.Folders.Count == 0) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>(selection.Folders.Count);
        foreach (var folder in selection.Folders)
        {
            var path = (folder ?? string.Empty).TrimEnd('/');
            if (path.Length > 0 && seen.Add(path)) kept.Add(path);
        }
        // Outermost roots only, across every category: a folder under a kept one is covered by that folder's delete.
        kept.Sort((left, right) =>
        {
            var depth = PairPath.Depth(left).CompareTo(PairPath.Depth(right));
            return depth != 0 ? depth : string.CompareOrdinal(left, right);
        });
        var roots = new List<string>(kept.Count);
        foreach (var path in kept)
        {
            var covered = false;
            foreach (var root in roots)
                if (PairPath.IsUnder(path, root))
                {
                    covered = true;
                    break;
                }
            if (!covered) roots.Add(path);
        }
        return roots;
    }

    /// <summary>
    /// Appends one folder Delete per chosen rebuildable folder the cloud still holds as a folder, after every table
    /// pass and move so nothing rebases or collapses it: the Executor expands it over everything live under the folder
    /// (<see cref="FolderDelete.Expands"/>), the same one-delete-per-folder shape as rule 23. A path whose cloud
    /// counterpart is not a folder is left to ordinary planning and never deleted here. The delete is planned on the
    /// row that holds the local copy, so it is never mistaken for a one-sided removal (OppositeAbsent stays false).
    /// </summary>
    private static Draft AppendRebuildableDeletes(Context ctx)
    {
        if (ctx.Rebuildables.Count == 0 || ctx.RebuildableCloud is not { } cloud) return ctx.Draft;
        var cloudItems = ctx.Occupancy(cloud);
        foreach (var folder in ctx.Rebuildables)
        {
            if (!cloudItems.TryGetValue(folder, out var target) || target.Item.Kind != ItemKind.Folder) continue;
            var row = ctx.Occupancy(Opposite(cloud)).TryGetValue(folder, out var local)
                ? ctx.Rows[local.Row]
                : ctx.Rows[target.Row];
            Emit(ctx, row, 32, ActionKind.Delete, cloud, target.Item.Path, null, null, target.Item, false, null,
                rebuildableRemove: true, firstSyncChoice: true);
        }
        return ctx.Draft;
    }
}
