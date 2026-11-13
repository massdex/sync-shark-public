using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>The engine's error mapping reads Dropbox's real `error_summary`; the in-memory account only writes the
/// structured error. This adds the summary the real service always sends, leaving success responses untouched.</summary>
internal sealed class DropboxErrorSummaryHandler(Massdex.SyncShark.V2.DropboxWire wire) : DelegatingHandler(wire)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/2/files/list_folder/longpoll")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException();
        }
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error_summary", out _) ||
            !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return response;

        var tag = String(error, ".tag");
        var summary = tag;
        if (tag is not null && error.TryGetProperty(tag, out var detail) && detail.ValueKind == JsonValueKind.Object &&
            String(detail, ".tag") is { } inner)
            summary = tag + "/" + inner;
        if (summary is null) return response;

        response.Content.Dispose();
        response.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["error_summary"] = summary + "/",
            ["error"] = JsonSerializer.Deserialize<object>(error.GetRawText())!
        }), Encoding.UTF8, "application/json");
        return response;
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
