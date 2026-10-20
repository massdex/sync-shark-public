using System.Net;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>
/// on the wire: a folder delete_batch entry removes its subtree and journals one deleted entry per
/// item (as the 2026-10-17 live probe showed Dropbox does), and files/restore brings a buried file revision back.
/// </summary>
internal sealed partial class DropboxWire
{
    private readonly Dictionary<string, Node> _buried = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<object> _folderJournal = [];

    /// <summary>The change-feed cursor whose page reports the whole-folder deletes (the end of the pre-delete read).</summary>
    public string FolderDeleteFeedCursor { get; set; } = "cursor-latest-end";

    /// <summary>When true, list_folder/continue fails once any whole folder was deleted.</summary>
    public bool FailFeedAfterFolderDelete { get; set; }

    /// <summary>How many whole folders delete_batch removed.</summary>
    public int WholeFolderDeletes { get; private set; }

    /// <summary>Every path files/restore brought back, in order.</summary>
    public List<string> Restores { get; } = [];

    /// <summary>Changes a live file's content in place: same identity, new revision.</summary>
    public string EditFile(string relative, string content) => Store(relative, Encoding.UTF8.GetBytes(content)).Rev;

    /// <summary>A change-feed entry carrying a live item's current metadata.</summary>
    public object ChangeOf(string relative) => Metadata(_items[relative]);

    public string? UnknownRevisionPathError { get; set; }
    public bool RestoreDifferentRevision { get; set; }
    public Action? BeforeRestore { get; set; }

    private static HttpResponseMessage FolderError(string tag, string? pathTag = null)
    {
        var error = new Dictionary<string, object> { [".tag"] = tag };
        if (pathTag is not null) error["path"] = new Dictionary<string, object> { [".tag"] = pathTag };
        var response = Json(new { error });
        response.StatusCode = HttpStatusCode.Conflict;
        return response;
    }

    public Action? BeforeBuryFolder { get; set; }
    public Action? AfterBuriedRevisionRead { get; set; }

    private Dictionary<string, object> BuryFolder(Node folder)
    {
        BeforeBuryFolder?.Invoke();
        WholeFolderDeletes++;
        var prefix = folder.Relative + "/";
        var removed = _items.Values.Where(node => node.Relative.Equals(folder.Relative, StringComparison.OrdinalIgnoreCase) ||
                node.Relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(node => node.Relative.Length).ToArray();
        foreach (var node in removed)
        {
            _items.Remove(node.Relative);
            _buried[node.Relative] = node;
            _folderJournal.Add(ChangeDeleted(Display(node.Relative)));
        }
        SeedChangePage(FolderDeleteFeedCursor, _folderJournal.ToArray(), false, FolderDeleteFeedCursor + "-after");
        return Metadata(folder);
    }

    private object? BuriedRevisions(string path)
    {
        var relative = ToRelative(path);
        if (relative is null || !_buried.TryGetValue(relative, out var node) || node.Folder ||
            _items.ContainsKey(relative)) return null;
        var result = new { is_deleted = true, server_deleted = "2026-10-17T00:00:00Z", entries = new object[] { FileMetadata(node) } };
        AfterBuriedRevisionRead?.Invoke();
        return result;
    }

    private async Task<HttpResponseMessage> RestoreAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var relative = ToRelative(document.RootElement.GetProperty("path").GetString()!);
        var rev = document.RootElement.GetProperty("rev").GetString();
        if (relative is null || !_buried.TryGetValue(relative, out var node) || node.Folder ||
            !string.Equals(node.Rev, rev, StringComparison.Ordinal))
            return Conflict();
        var parent = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
        if (parent.Length > 0 && !(_items.TryGetValue(parent, out var folder) && folder.Folder))
            return NotFound();
        BeforeRestore?.Invoke();
        if (RestoreDifferentRevision) node = node with { Rev = node.Rev + "-different" };
        _items[relative] = node;
        _folderJournal.Add(Metadata(node));
        SeedChangePage(FolderDeleteFeedCursor, _folderJournal.ToArray(), false, FolderDeleteFeedCursor + "-after");
        Restores.Add(relative);
        return Json(FileMetadata(node));
    }
}
