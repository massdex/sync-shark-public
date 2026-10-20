using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class DropboxPilotClient
{
    /// <summary>Read-only, capped inventory of exactly one requested subtree. Never reads tombstones or history.</summary>
    internal async Task<(IReadOnlyList<PilotRemoteEntry> Entries, bool Complete)> ListPreviewSubtreeAsync(
        string relative, int maxEntries, CancellationToken ct)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        var prefix = PilotPath.NormalizeRelative(relative);
        var selected = await FindEntryAtPathAsync(prefix, ct).ConfigureAwait(false);
        if (selected is null) return (Array.Empty<PilotRemoteEntry>(), true);
        var entries = new List<PilotRemoteEntry> { selected };
        if (selected.Kind != PilotEntryKind.Directory) return (entries, true);
        var canonical = new HashSet<string>(StringComparer.Ordinal)
            { PilotPath.CanonicalComparisonKey(selected.RelativePath) };
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        JsonDocument? page = await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder", new
        {
            path = selected.Id,
            recursive = true,
            include_deleted = false,
            include_mounted_folders = true,
            include_non_downloadable_files = true,
            limit = Math.Min(maxEntries, 2000)
        }, ct).ConfigureAwait(false);
        try
        {
            for (var pages = 1; ; pages++)
            {
                foreach (var metadata in page.RootElement.GetProperty("entries").EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    if (entries.Count >= maxEntries) return (entries, false);
                    var entry = RemoteEntryFromMetadata(metadata, inventoryOnly: true);
                    if (!entry.RelativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Dropbox returned an item outside the requested preview subtree.");
                    if (!canonical.Add(PilotPath.NormalizeInventoryPath(entry.RelativePath)
                        .Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant()))
                        throw new IOException("Dropbox returned duplicate or colliding preview names.");
                    entries.Add(entry);
                }
                if (!page.RootElement.GetProperty("has_more").GetBoolean())
                {
                    await VerifyRootIdentityAsync(ct).ConfigureAwait(false);
                    return (entries, true);
                }
                if (entries.Count >= maxEntries || pages >= MaximumContinuationPages) return (entries, false);
                var cursor = RequireCursor(page.RootElement, "Dropbox preview cursor is absent.");
                if (!cursors.Add(CursorFingerprint(cursor))) return (entries, false);
                page.Dispose();
                page = await PostJsonAsync("https://api.dropboxapi.com/2/files/list_folder/continue",
                    new { cursor }, ct).ConfigureAwait(false);
            }
        }
        finally { page?.Dispose(); }
    }
}
