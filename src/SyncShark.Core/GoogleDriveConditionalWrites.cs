using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class GoogleDrivePilotClient
{
    // V2 exposes the server ETag. This path remains capability-gated until the real
    // provider proof establishes both accepted current writes and rejected stale writes.
    // V3 remains the inventory/content identity API; no separate credential is used.
    private async Task<EntityTagHeaderValue> ReadConditionalGuardAsync(GoogleDriveInventoryEntry reviewed,
        CancellationToken cancellationToken)
    {
        if (!_capabilities.AtomicConditionalWrites) throw new GoogleDriveBlockedException(DriveUpdateUnsupported);
        var url = "https://www.googleapis.com/drive/v2/files/" + Uri.EscapeDataString(reviewed.FileId) +
            "?fields=id,etag,version,labels(trashed),mimeType,parents(id)&supportsAllDrives=" + (SharedDrives ? "true" : "false");
        using var request = await AuthorizedAsync(HttpMethod.Get, url, cancellationToken);
        using var response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ThrowIfFailed(response);
        using var document = await ReadBoundedJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        var parents = root.TryGetProperty("parents", out var parentList)
            ? parentList.EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        if (root.GetProperty("id").GetString() != reviewed.FileId ||
            root.GetProperty("version").GetString() != reviewed.Version ||
            root.GetProperty("mimeType").GetString() != reviewed.MimeType ||
            root.GetProperty("labels").GetProperty("trashed").GetBoolean() != reviewed.Trashed ||
            !parents.SetEquals(reviewed.Parents))
            throw new GoogleDriveBlockedException("The reviewed Google item changed before its conditional guard was acquired. Scan again.");
        if (!root.TryGetProperty("etag", out var field) ||
            !EntityTagHeaderValue.TryParse(field.GetString(), out var etag) || etag.IsWeak || etag.Tag == "*")
            throw new GoogleDriveBlockedException("Google returned no strong version guard; the existing item was not changed.");
        return etag;
    }

    private async Task<PilotUploadResult> UploadConditionalAsync(GoogleDriveInventoryEntry target, Stream source,
        string expectedHash, CancellationToken cancellationToken)
    {
        var guard = await ReadConditionalGuardAsync(target, cancellationToken);
        var url = "https://www.googleapis.com/upload/drive/v2/files/" + Uri.EscapeDataString(target.FileId) +
            "?uploadType=media&newRevision=true&fields=id&supportsAllDrives=" + (SharedDrives ? "true" : "false");
        using var request = await AuthorizedAsync(HttpMethod.Put, url, cancellationToken);
        request.Headers.IfMatch.Add(guard);
        request.Content = new StreamContent(new ThrottledLeaveOpenReadStream(source, _transferLimiter));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var result = await SendEffectJsonAsync(request, cancellationToken);
        if (result.RootElement.GetProperty("id").GetString() != target.FileId)
            throw new GoogleDriveEffectUncertainException("Google returned a different identity after the guarded upload.");
        return await VerifyUploadedContentAsync(target, expectedHash, source.Length, target, cancellationToken);
    }

    private async Task<GoogleDriveInventoryEntry> PatchConditionalAsync(GoogleDriveInventoryEntry reviewed,
        object patch, string? newParentId, CancellationToken cancellationToken)
    {
        var guard = await ReadConditionalGuardAsync(reviewed, cancellationToken);
        var url = "https://www.googleapis.com/drive/v2/files/" + Uri.EscapeDataString(reviewed.FileId) +
            "?fields=id&supportsAllDrives=" + (SharedDrives ? "true" : "false");
        if (newParentId is not null && !reviewed.Parents.Contains(newParentId, StringComparer.Ordinal))
        {
            url += "&addParents=" + Uri.EscapeDataString(newParentId);
            if (reviewed.Parents.Count > 0)
                url += "&removeParents=" + Uri.EscapeDataString(string.Join(',', reviewed.Parents));
        }
        using var request = await AuthorizedAsync(HttpMethod.Patch, url, cancellationToken);
        request.Headers.IfMatch.Add(guard);
        request.Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json");
        using var result = await SendEffectJsonAsync(request, cancellationToken);
        if (result.RootElement.GetProperty("id").GetString() != reviewed.FileId)
            throw new GoogleDriveEffectUncertainException("Google returned a different identity after the guarded metadata change.");
        return await GetFileAsync(reviewed.FileId, cancellationToken);
    }
}
