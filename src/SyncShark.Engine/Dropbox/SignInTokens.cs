using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox token exchange: posts a token form, reads a JSON reply and classifies a
/// provider refusal. Static helpers only, no state.</summary>
internal static class SignInTokens
{
    /// <summary>Posts a token form through the supplied sender and returns the parsed token facts and granted scopes.</summary>
    internal static async Task<(string AccessToken, string? RefreshToken, DateTime ExpiresUtc, string? AccountId,
            IReadOnlyList<string> Scopes)> TokenAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        IReadOnlyDictionary<string, string> form, string? connectionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(DropboxProtocol.TokenPath))
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var response = await send(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (connectionId is not null && await IsInvalidGrantAsync(response, ct))
                throw new DropboxCredentialUnavailableException(connectionId);
            throw new HttpRequestException(Text.Get(TextKey.SignInReturnRetry), null, response.StatusCode);
        }
        using var document = await ReadJsonAsync(response, ct);
        var root = document.RootElement;
        try
        {
            var accessToken = root.TryGetProperty("access_token", out var access) ? access.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
            var refreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
            // DropboxOAuth.cs (0.3.95) used four hours when the provider omitted expires_in.
            var expiresIn = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 14400;
            var accountId = root.TryGetProperty("account_id", out var account) ? account.GetString() : null;
            var scopes = root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String
                ? (scope.GetString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [];
            return (accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn), accountId, scopes);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        }
    }

    private static string? StringValue(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; } // Invalid Unicode isn't usable text.
    }

    internal static string RequiredString(JsonElement element, string name) =>
        StringValue(element, name) is { } text && !string.IsNullOrWhiteSpace(text) ? text :
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null) throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid));
        }
        catch (JsonException) { throw new InvalidDataException(Text.Get(TextKey.RootDropboxIdentityInvalid)); }
    }

    private static async Task<bool> IsInvalidGrantAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = await ReadJsonAsync(response, ct);
            return StringValue(document.RootElement, "error") == "invalid_grant";
        }
        catch (InvalidDataException) { return false; }
    }

}
