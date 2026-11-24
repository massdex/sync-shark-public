using System.Net;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

// HTTP wire representation and refusal evidence, independent of admission and permit ownership.
internal static class DropboxHttp
{
    private const int ConnectAllowanceSeconds = 20;
    internal static readonly HttpRequestOptionsKey<(long Timestamp, DateTimeOffset Wall)> ReceiptKey = new(nameof(ReceiptKey));

    internal static HttpClient CreateHttpClient(Func<SocketsHttpPlaintextStreamFilterContext, CancellationToken, ValueTask<Stream>> filter)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(ConnectAllowanceSeconds),
            PlaintextStreamFilter = filter
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    internal static HttpRequestMessage BuildRequest(string route, string argumentJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(route))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (Routes.StyleOf(route) == Routes.Style.Rpc)
        {
            var payload = Routes.VoidArg(route) ? "null" : argumentJson;
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }
        else
        {
            request.Headers.Add("Dropbox-API-Arg", Routes.ArgHeader(argumentJson));
        }
        return request;
    }

    internal static (long Used, long Allocated) SpaceUsage(JsonElement root)
    {
        var allocation = root.GetProperty("allocation");
        var team = allocation.TryGetProperty(".tag", out var tag) &&
            string.Equals(tag.GetString(), "team", StringComparison.Ordinal);
        var used = team ? allocation.GetProperty("used").GetInt64() : root.GetProperty("used").GetInt64();
        var allocated = allocation.GetProperty("allocated").GetInt64();
        if (used < 0 || allocated < 0) throw new InvalidDataException(Text.Get(TextKey.FailureDetail));
        return (used, allocated);
    }

    internal static async Task ThrowFailureAsync(DropboxApi api, HttpResponseMessage response, string route, CancellationToken ct,
        string? readPath = null)
    {
        var received = ReceivedAt(response, api.Clock);
        var status = response.StatusCode;
        var header = RetryAfter(response, api.Clock);
        JsonElement envelope;
        try { envelope = await ReadErrorAsync(response, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (route == DropboxProtocol.ListFolderLongpoll && ct.IsCancellationRequested)
        {
            // The poll owns caller cancellation; its deadline cannot erase an already received refusal.
            envelope = default;
        }
        var read = Routes.PolicyOf(route) == Routes.Policy.Read;
        var failure = new DropboxErrors().Classify(status, envelope, route, false,
            new(read, true, read || status == HttpStatusCode.TooManyRequests, IsBusy(status)));
        var providerWait = DropboxProtocol.ProviderWait(header, envelope);
        if (route != DropboxProtocol.ListFolderLongpoll) api.ReportBusy(status, failure, providerWait, received);
        var wait = failure.Disposition == FailureDisposition.TemporaryNoEffect ? providerWait : null;
        if (readPath is not null && failure.Absent) return;
        var refusal = new HttpRequestException(Text.Get(TextKey.DropboxRefused), null, status);
        if (failure.CursorReset) throw new ListingCursorResetException(refusal);
        throw new ItemFailedException(readPath ?? string.Empty, failure.Cause, refusal)
        {
            Disposition = failure.Disposition, ProviderWait = wait, ProviderWaitStarted = wait.HasValue ? received : null
        };
    }

    internal static long ReceivedAt(HttpResponseMessage response, TimeProvider clock) =>
        response.RequestMessage?.Options.TryGetValue(ReceiptKey, out var receipt) == true
            ? receipt.Timestamp : clock.GetTimestamp();

    internal static TimeSpan? RetryAfter(HttpResponseMessage response, TimeProvider clock)
    {
        var retry = response.Headers.RetryAfter;
        if (retry is null) return null;
        if (retry.Delta is { } delta) return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        if (retry.Date is { } date)
        {
            var wall = response.RequestMessage?.Options.TryGetValue(ReceiptKey, out var receipt) == true
                ? receipt.Wall : clock.GetUtcNow();
            var remaining = date - wall;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    internal static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            // Preserve UTF-8 replacement decoding and do not treat a BOM as a change of encoding.
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            using var document = JsonDocument.Parse(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException or TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            return default; // Unusable body; the caller still owns the status and header.
        }
    }

    internal static bool IsBusy(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    internal static string PathRootJson(string namespaceId) => JsonSerializer.Serialize(
        new Dictionary<string, string>(StringComparer.Ordinal) { [".tag"] = "root", ["root"] = namespaceId });
}
