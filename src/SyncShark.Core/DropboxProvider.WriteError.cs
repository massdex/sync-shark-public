using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class DropboxPilotClient
{
    // Only structured write unions are interpreted. In particular, malformed_path is not a name refusal:
    // it can describe a bad request/root/path, so it must retain normal error handling and retry behavior.
    internal static string? WriteErrorTag(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return null;
        var tag = ReadUnionTag(error);
        if (tag is "path" or "to" or "path_lookup" or "path_write")
        {
            if (error.TryGetProperty("reason", out var reason)) return ReadUnionTag(reason);
            if (error.TryGetProperty(tag, out var nested))
            {
                if (nested.ValueKind == JsonValueKind.Object && nested.TryGetProperty("reason", out reason))
                    return ReadUnionTag(reason);
                return ReadUnionTag(nested);
            }
            return null;
        }
        return tag;
    }

    private async Task<string?> ReadWriteErrorTagAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Conflict) return null;
        try
        {
            using var document = await ReadBoundedJsonAsync(response, ct).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) ? WriteErrorTag(error) : null;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or HttpRequestException or TimeoutException)
        {
            // An unreadable rejection body cannot authorize ignoring an item.
            return null;
        }
    }
}
