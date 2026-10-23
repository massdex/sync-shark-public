using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

internal static class DropboxDownloadRetryChecks
{
    internal static async Task RunAsync()
    {
        await ReadMetadataRetriesAsync();
        var bytes = Encoding.UTF8.GetBytes("exact immutable revision payload");
        var hash = DropboxContentHash.Compute(bytes);
        foreach (var mode in new[] { "transport", "rate", "server", "denied", "long-backoff", "exhausted", "body", "cancel", "range",
                     "stall", "stall-cancel",
                     "unsupported", "missing-revision", "unknown-error", "malformed-error", "oversized-error" })
        {
            var wire = new DropboxContractHandler
            { DownloadBytes = bytes, DownloadHash = hash, DownloadRevision = "revision", MetadataRevision = "revision",
                ArchiveHash = hash, ArchiveSize = bytes.Length };
            using var handler = new RetryHandler(wire, mode, bytes);
            using var http = new HttpClient(handler);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes);
            var clock = new ResilienceChecks.Clock();
            var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, "id:pilot-folder")
            {
                ReadTransientBudget = TimeSpan.FromSeconds(4), ReadRetryTime = clock,
                // A body read that sends no bytes is aborted after a short idle bound, never hung.
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                ReadRetryDelay = (delay, ct) => { ct.ThrowIfCancellationRequested(); clock.Advance(delay); return Task.CompletedTask; }
            };
            var entry = new PilotRemoteEntry("wire.txt", PilotEntryKind.File, "id:wire", "revision", bytes.Length, hash);
            using var destination = new MemoryStream();
            using var cancellation = new CancellationTokenSource(
                mode == "stall-cancel" ? TimeSpan.FromMilliseconds(50) : Timeout.InfiniteTimeSpan);
            var notices = new List<SyncByteProgress>();
            using var progress = SyncByteProgressStream.Begin("wire.txt", item =>
            {
                notices.Add(item);
                if (mode == "cancel" && item.Phase.Contains("retry", StringComparison.Ordinal)) cancellation.Cancel();
                return Task.CompletedTask;
            });
            Exception? failure = null;
            try
            {
                if (mode == "range") await client.DownloadRangeAsync(entry, 0, 4, destination, cancellation.Token);
                else await client.DownloadAsync(entry, destination, cancellation.Token);
            }
            catch (Exception error) { failure = error; }
            var success = mode is "transport" or "rate" or "server" or "range";
            Require(success ? failure is null : failure is not null, mode + ": exact success/failure outcome");
            Require(handler.Paths.All(path => path == "rev:revision"), mode + ": every retry binds the same immutable revision");
            if (success)
            {
                Require(handler.Attempts == 2, mode + ": one bounded retry only");
                Require(destination.ToArray().SequenceEqual(mode == "range" ? bytes.Take(4) : bytes), mode + ": exact bytes");
                Require(notices.Any(item => item.Path == "wire.txt" && item.Phase.Contains("retry", StringComparison.Ordinal)),
                    mode + ": live log identifies the exact waiting file");
                Require(client.ProviderCallCount >= handler.Attempts, mode + ": download requests counted");
            }
            else if (mode == "exhausted")
                Require(handler.Attempts == 3 && destination.Length == 0 && failure is HttpRequestException,
                    "bounded transport failure retains the exception and no bytes");
            else if (mode == "body")
                Require(handler.Attempts == 1 && destination.Length == 1 && failure is IOException,
                    "a partially received response is not blindly replayed");
            else
                Require(handler.Attempts == 1 && destination.Length == 0 &&
                    (mode != "cancel" || failure is OperationCanceledException),
                    mode + ": no forbidden retry or destination bytes");
            if (mode == "stall")
                Require(handler.Attempts == 1 && destination.Length == 0 && failure is TimeoutException,
                    "a half-open download body fails with a timeout instead of hanging, and is not replayed");
            if (mode == "stall-cancel")
                Require(handler.Attempts == 1 && destination.Length == 0 && failure is OperationCanceledException,
                    "caller cancellation during a download stall propagates as cancellation, not a stall timeout");
            if (mode is "unsupported" or "missing-revision" or "unknown-error" or "malformed-error" or "oversized-error")
            {
                Require(failure is DropboxApiException && failure.Message.Contains("HTTP 409") &&
                        !failure.Message.Contains("secret-sentinel", StringComparison.Ordinal),
                    mode + ": exact HTTP refusal is retained without arbitrary provider content");
                Require(mode switch
                {
                    "unsupported" => failure!.Message.Contains("unsupported_file") && failure.Message.Contains("export"),
                    "missing-revision" => failure!.Message.Contains("path/not_found") && failure.Message.Contains("Scan again"),
                    _ => failure!.Message.Contains("unclassified")
                }, mode + ": actionable fixed provider classification or honest unclassified fallback");
            }
        }
        Console.WriteLine("PASS exact-revision download retries: transport, 429/5xx, denied, backoff, exhaustion, partial body, cancellation and range");
    }

    private static async Task ReadMetadataRetriesAsync()
    {
        foreach (var mode in new[] { "transport", "body", "exhausted", "cancel", "denied", "changed", "malformed", "oversized", "missing" })
        {
            using var handler = new MetadataRetryHandler(mode);
            using var http = new HttpClient(handler);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes);
            var clock = new ResilienceChecks.Clock();
            var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, "id:pilot-folder")
            {
                ReadTransientBudget = TimeSpan.FromSeconds(4), ReadRetryTime = clock,
                ReadRetryDelay = (delay, ct) => { ct.ThrowIfCancellationRequested(); clock.Advance(delay); return Task.CompletedTask; }
            };
            using var cancellation = new CancellationTokenSource();
            var notices = new List<SyncByteProgress>();
            using var progress = SyncByteProgressStream.Begin("wire.txt", item =>
            {
                notices.Add(item);
                if (mode == "cancel" && item.Phase.Contains("retry", StringComparison.Ordinal)) cancellation.Cancel();
                return Task.CompletedTask;
            });
            Exception? failure = null;
            try { await client.VerifyRootIdentityAsync(cancellation.Token); }
            catch (Exception error) { failure = error; }
            var success = mode is "transport" or "body";
            Require(success ? failure is null : failure is not null, mode + ": real bound-root read outcome");
            Require(handler.Bodies.Distinct(StringComparer.Ordinal).Count() == 1 &&
                    handler.Bodies.All(body => body.Contains("id:pilot-folder", StringComparison.Ordinal)) &&
                    handler.EffectRequests == 0,
                mode + ": exact root read binding retained; no effect request or absent-root guess");
            Require(handler.Attempts == (success ? 2 : mode == "exhausted" ? 3 : 1),
                mode + ": bounded retry only for transport interruption");
            if (success || mode == "exhausted")
                Require(notices.Any(item => item.Phase.Contains("Waiting for network", StringComparison.Ordinal) &&
                                           item.Detail.Contains("/2/files/get_metadata", StringComparison.Ordinal)),
                    mode + ": real progress names the read being retried");
            if (mode == "cancel") Require(failure is OperationCanceledException, "read retry preserves cancellation");
            if (mode == "exhausted") Require(failure is HttpRequestException, "exhausted transport retains its cause");
        }
        Console.WriteLine("PASS bound-root metadata retries: transport/body recovery, exhaustion, cancel, denial, changed identity, malformed/oversized and missing root rejection; zero effects");
    }

    private sealed class MetadataRetryHandler(string mode) : HttpMessageHandler
    {
        internal int Attempts, EffectRequests;
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/2/users/get_current_account")
                return Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""");
            if (route != "/2/files/get_metadata")
            { EffectRequests++; throw new InvalidOperationException("Unexpected effect request in read-only fixture"); }
            Attempts++;
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (mode == "exhausted" || Attempts == 1 && mode is "transport" or "cancel")
                throw new HttpRequestException("Read transport ended prematurely", new IOException("ResponseEnded"));
            if (mode == "denied") return new(HttpStatusCode.Forbidden);
            if (mode == "missing") return new(HttpStatusCode.Conflict)
                { Content = new StringContent("""{"error_summary":"path/not_found/","error":{".tag":"path","path":{".tag":"not_found"}}}""") };
            if (mode == "malformed") return Json("{");
            var response = Json(mode == "changed"
                ? """{".tag":"folder","name":"Pilot","id":"id:different-folder","path_display":"/Pilot"}"""
                : """{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_display":"/Pilot"}""");
            if (mode == "body" && Attempts == 1)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                response.Content.Dispose(); response.Content = new StreamContent(new FailingBody(bytes));
            }
            if (mode == "oversized") response.Content.Headers.ContentLength = 100L * 1024 * 1024;
            return response;
        }
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class RetryHandler(HttpMessageHandler inner, string mode, byte[] bytes) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(inner);
        internal int Attempts { get; private set; }
        internal List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath != "/2/files/download") return await _inner.SendAsync(request, token);
            Attempts++;
            using var args = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            Paths.Add(args.RootElement.GetProperty("path").GetString()!);
            if (mode == "exhausted" || Attempts == 1 && mode is "transport" or "cancel" or "range")
                throw new HttpRequestException("Injected failure before response headers");
            if (mode == "denied") return new(HttpStatusCode.Forbidden);
            if (mode is "unsupported" or "missing-revision" or "unknown-error" or "malformed-error" or "oversized-error")
            {
                var error = mode switch
                {
                    "unsupported" => """{"error_summary":"secret-sentinel","error":{".tag":"unsupported_file"}}""",
                    "missing-revision" => """{"error_summary":"secret-sentinel","error":{".tag":"path","path":{".tag":"not_found"}}}""",
                    "malformed-error" => "{secret-sentinel",
                    _ => """{"error_summary":"secret-sentinel","error":{".tag":"secret-sentinel"}}"""
                };
                var refused = new HttpResponseMessage(HttpStatusCode.Conflict)
                    { Content = new StringContent(error, Encoding.UTF8, "application/json") };
                if (mode == "oversized-error") refused.Content.Headers.ContentLength = 20 * 1024;
                return refused;
            }
            if (Attempts == 1 && mode is "rate" or "server" or "long-backoff")
            {
                var response = new HttpResponseMessage(mode == "server" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(mode == "long-backoff" ? TimeSpan.FromHours(1) : TimeSpan.Zero);
                return response;
            }
            var result = await _inner.SendAsync(request, token);
            if (mode == "body")
            { result.Content.Dispose(); result.Content = new StreamContent(new FailingBody(bytes)); }
            if (mode is "stall" or "stall-cancel")
            { result.Content.Dispose(); result.Content = new StreamContent(new StallingBody()); }
            if (mode == "range")
            {
                Require(request.Headers.Range?.ToString() == "bytes=0-3", "retry preserves the exact range");
                result.StatusCode = HttpStatusCode.PartialContent;
                result.Content.Dispose(); result.Content = new ByteArrayContent(bytes.Take(4).ToArray());
                result.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, bytes.Length);
            }
            return result;
        }
        protected override void Dispose(bool disposing)
        { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class FailingBody(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Position == 0 ? base.ReadAsync(buffer[..1], cancellationToken)
                : ValueTask.FromException<int>(new IOException("Injected failure after one response byte"));
    }

    // A download body that never sends a byte; it completes only when its read is cancelled (the idle bound
    // that aborts it, or the caller's own cancellation).
    private sealed class StallingBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}
