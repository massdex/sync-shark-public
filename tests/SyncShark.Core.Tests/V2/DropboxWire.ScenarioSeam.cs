namespace Massdex.SyncShark.V2;

// Arrangement seams for the end-to-end scenario checks (linked into the engine tests). Read-only snapshot plus the
// two user actions the scenarios need: rename (id preserved, like Dropbox) and delete.
internal sealed partial class DropboxWire
{
    /// <summary>Every live item as (relative path, is folder, bytes), sorted by path.</summary>
    public IReadOnlyList<(string Path, bool Folder, byte[] Bytes)> Snapshot() =>
        _items.Values.Select(node => (node.Relative, node.Folder, node.Bytes))
            .OrderBy(item => item.Relative, StringComparer.Ordinal).ToArray();

    /// <summary>Renames a live item the way a user's Dropbox rename would, keeping its id.</summary>
    public void Rename(string from, string to) => Rebase(from, to);

    /// <summary>Deletes a live item the way a user's Dropbox delete would.</summary>
    public void Delete(string relative) => RemoveSubtree(relative);
}
