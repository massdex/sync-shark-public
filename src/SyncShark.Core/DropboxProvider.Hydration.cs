using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class DropboxPilotClient
{
    // Uncapped, one provider page at a time. The caller retains the ORIGINAL ROOT change cursor;
    // this listing cursor is only a continuation for this exact subtree.
    internal async Task HydratePrefixAsync(string prefix, Func<IReadOnlyList<DropboxChange>, Task> accept,
        CancellationToken ct)
    {
        await VerifyRootIdentityAsync(ct);
        using var metadata = await PostJsonAllowingMissingPathAsync(
            "https://api.dropboxapi.com/2/files/get_metadata", new
            { path = prefix.Length == 0 ? RequiredRootId() : RequiredRootId() + "/" + prefix,
                include_deleted = true, include_media_info = false }, ct);
        if (metadata is null) { await VerifyRootIdentityAsync(ct); return; }
        var first = Parse(metadata.RootElement);
        if (!string.Equals(first.Path, prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dropbox returned a different requested prefix.");
        await accept([first]);
        if (first.Entry?.Kind != PilotEntryKind.Directory) { await VerifyRootIdentityAsync(ct); return; }
        string? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var pageNumber = 0; pageNumber < MaximumContinuationPages; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = cursor is null
                ? await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder", new
                {
                    path = first.Entry.Id, recursive = true, include_deleted = true, limit = 2000,
                    include_mounted_folders = true, include_non_downloadable_files = true
                }, ct)
                : await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder/continue", new { cursor }, ct);
            var batch = new List<DropboxChange>(256);
            foreach (var row in page.RootElement.GetProperty("entries").EnumerateArray())
            {
                batch.Add(Parse(row));
                if (batch.Count == 256) { await accept(batch); batch = new(256); }
            }
            if (batch.Count > 0) await accept(batch);
            cursor = RequireCursor(page.RootElement, "Dropbox subtree cursor is absent.");
            if (!page.RootElement.GetProperty("has_more").GetBoolean()) { await VerifyRootIdentityAsync(ct); return; }
            if (!seen.Add(cursor)) throw new InvalidDataException("Dropbox subtree cursor cycled.");
        }
        throw new InvalidDataException("Dropbox subtree listing exceeded its page safety limit.");

        DropboxChange Parse(JsonElement row)
        {
            var tag = row.GetProperty(".tag").GetString();
            if (tag is not ("file" or "folder" or "deleted")) throw new InvalidDataException("Unknown Dropbox item type.");
            var display = row.TryGetProperty("path_display", out var value) ? value.GetString() :
                row.GetProperty("path_lower").GetString();
            var path = ToInventoryRelative(display ?? throw new InvalidDataException("Dropbox path is absent."));
            if (!V2.PairPath.IsSelfOrUnder(path, prefix)) throw new InvalidDataException("Dropbox subtree escaped its prefix.");
            return new(path, tag == "deleted" ? null : RemoteEntryFromMetadata(row, inventoryOnly: true));
        }
    }
}
