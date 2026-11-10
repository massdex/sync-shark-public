namespace Massdex.SyncShark.V2;

public sealed partial class DropboxWriter
{
    // Only exceptions survive in the durable window. Ordinary frozen-victim tombstones are implicit;
    // absence of evidence is explicit, so compaction can never manufacture deletion evidence.
    internal static (List<DropboxChange> Exceptions, string[] Unobserved) CompactFolderWindow(
        IReadOnlyList<FolderTree> trees, IReadOnlyList<DropboxChange> before, IReadOnlyList<DropboxChange> after)
    {
        var planned = trees.SelectMany(tree => tree.Victims.Append(tree.Folder))
            .ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in before.Concat(after))
            if (change.Entry is { } entry && planned.TryGetValue(change.Path, out var victim) &&
                (entry.Id != victim.ObjectId || (entry.Kind == PilotEntryKind.Directory) != (victim.Kind == ItemKind.Folder) ||
                 (victim.Kind == ItemKind.File && entry.Revision != victim.VersionId)))
                changed.Add(change.Path);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exceptions = new List<DropboxChange>();
        foreach (var change in after)
        {
            if (planned.ContainsKey(change.Path))
            {
                observed.Add(change.Path);
                if (change.Entry is not null || changed.Contains(change.Path)) exceptions.Add(change);
            }
            else if (trees.Any(tree => PairPath.IsSelfOrUnder(change.Path, tree.Folder.Path)))
                exceptions.Add(change);
        }
        return (exceptions, planned.Keys.Where(path => !observed.Contains(path)).ToArray());
    }
}
