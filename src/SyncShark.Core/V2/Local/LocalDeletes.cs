namespace Massdex.SyncShark.V2;

/// <summary>Decides a local refresh's Delete changes from the stored rows and the refresh scan.</summary>
/// <remarks>
/// Storage removes a deleted folder with its whole stored subtree, so a folder is deleted in one Delete only when
/// every stored row under it is itself gone; then nothing under it is listed again (one Delete per removed tree,
/// never one per nested folder). A folder holding a stored row this refresh may not decide on (outside the
/// selection, a product artifact, inside an incomplete subtree) is kept, and only its gone descendants are deleted.
/// </remarks>
internal static class LocalDeletes
{
    /// <param name="decidable">Whether this refresh may decide on a path (selection, artifacts, incomplete subtrees).</param>
    public static IReadOnlyList<Change> Build(IStateStore known, string rootId, IReadOnlyList<string> paths,
        PilotLocalScan scan, Func<string, bool> decidable, DateTimeOffset scanTime, Func<ItemState, bool>? confirmAbsence = null,
        IReadOnlySet<string>? observedObjects = null)
    {
        var present = new HashSet<string>(scan.Entries.Keys, StringComparer.OrdinalIgnoreCase);
        var folders = scan.Entries.Values.Where(entry => entry.Kind == PilotEntryKind.Directory)
            .Select(entry => entry.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stored = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        var gone = new HashSet<string>(StringComparer.Ordinal);
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Ancestors first, so a subtree already read for an enclosing path is never read again.
        foreach (var requested in paths.Order(StringComparer.Ordinal))
        {
            if (!decidable(requested)) continue;
            // B-126: identity from the exact stored spelling only; a replay (that row already gone) must never
            // name a surviving case-clash spelling (RQ-039). The resolver requests stored spellings for known ids.
            var rows = known.Live(rootId, new[] { requested });
            var row = rows.Count > 0 && string.Equals(rows[0].Path, requested, StringComparison.Ordinal) ? rows[0] : null;
            // Gone: absent from the scan, or a stored folder the scan now finds as a file (its file is upserted).
            var rowGone = row is not null &&
                (!present.Contains(requested) || (row.Kind == ItemKind.Folder && !folders.Contains(requested)));
            if (rowGone) Add(row!, true);
            // A refreshed folder was scanned with its whole subtree, and a gone folder's subtree is gone with it,
            // so either way the scan decides every stored row under the path (e.g. a child the resolver never
            // requested after a case-only rename then delete collapsed into a parent refresh).
            if ((folders.Contains(requested) || (rowGone && row!.Kind == ItemKind.Folder)) &&
                !PairPath.Ancestors(requested).Any(loaded.Contains) && loaded.Add(requested))
                foreach (var under in known.LiveUnder(rootId, new[] { requested }))
                    if (PairPath.IsUnder(under.Path, requested))
                        Add(under, decidable(under.Path) && !present.Contains(under.Path));
        }
        // Resolve consent in bounded pages, not one statement per missing descendant.
        foreach (var page in gone.ToArray().Chunk(512))
        {
            var consent = known.DefenderDeferralConsent(page.Select(path => stored[path].ObjectId).ToArray());
            foreach (var path in page)
                if (consent.TryGetValue(stored[path].ObjectId, out var state) &&
                    (state == DefenderConsent.Keep || state == DefenderConsent.Pending && !ReleasedByFolder(path)))
                    gone.Remove(path);
        }
        return Emit(stored, gone, scanTime, observedObjects);

        void Add(ItemState row, bool isGone)
        {
            stored.TryAdd(row.Path, row);
            if (!isGone || (row.Flags & ItemFlags.Quarantined) != 0) return;
            if (confirmAbsence?.Invoke(row) ?? true) gone.Add(row.Path);
        }

        bool ReleasedByFolder(string path) =>
            scan.DefenderHistoryClearFolders is { } cleared && LocalInventory.UnderAny(path, cleared);
    }

    private static List<Change> Emit(Dictionary<string, ItemState> stored, HashSet<string> gone, DateTimeOffset scanTime, IReadOnlySet<string>? observedObjects)
    {
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in stored.Keys.Where(path => !gone.Contains(path)))
            foreach (var ancestor in PairPath.Ancestors(path))
                kept.Add(ancestor);
        bool Cascades(string path) =>
            gone.Contains(path) && stored[path].Kind == ItemKind.Folder && !kept.Contains(path);
        var changes = new List<Change>();
        foreach (var path in gone.Order(StringComparer.Ordinal))
        {
            // Explicit alias retirement is path-bound even when a parent also disappeared.
            // Storage must not infer alias absence merely from another name's upsert.
            if (PairPath.Ancestors(path).Any(Cascades) &&
                !(stored[path].Kind == ItemKind.File && stored[path].ObjectId.StartsWith("id:local:", StringComparison.Ordinal) && observedObjects?.Contains(stored[path].ObjectId) == true)) continue;
            if (stored[path].Kind == ItemKind.Folder && kept.Contains(path)) continue;
            changes.Add(new Change(ChangeKind.Delete, stored[path].ObjectId, path, null, scanTime));
        }
        return changes;
    }
}
