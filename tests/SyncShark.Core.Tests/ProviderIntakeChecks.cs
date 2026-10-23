using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// C30: every provider error-body read is bounded by size, by the caller's cancellation and by an idle
// deadline, and pacing leaves the body readable for the adapter that still has to classify the failure.
internal static class ProviderIntakeChecks
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(200);

    internal static async Task RunAsync()
    {
        await BoundedErrorBodyReaderAsync();
        await SharedJsonReaderIdleAsync();
        await ProviderJsonReaderSeamsAsync();
        await PacingKeepsTheErrorBodyAsync();
        await PacingHonoursCallerCancellationAsync();
        await PacingUnreadableBodyAsync(stalled: false);
        await PacingUnreadableBodyAsync(stalled: true);
        Console.WriteLine("--provider-intake-checks passed.");
    }

    // The one bounded error-body reader: it returns the body, keeps it readable, refuses an oversized body,
    // and aborts a body that sends headers and then no bytes.
    private static async Task BoundedErrorBodyReaderAsync()
    {
        const string throttle = """{"error":{".tag":"too_many_write_operations"},"error_summary":"too_many_write_operations/..."}""";
        using (var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
               { Content = new StringContent(throttle, Encoding.UTF8, "application/json") })
        {
            var text = await SyncResponseBody.ReadAndReplaceAsync(response, SyncResponseBody.MaximumErrorBodyBytes, Idle, "The provider returned an oversized error response.", CancellationToken.None);
            Require(text == throttle, "the bounded error reader returns the exact body");
            Require(await response.Content.ReadAsStringAsync() == throttle,
                "the bounded error reader leaves the body readable for the adapter");
        }

        using (var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
               { Content = new ByteArrayContent(new byte[SyncResponseBody.MaximumErrorBodyBytes + 1]) })
        {
            Exception? failure = null;
            try { await SyncResponseBody.ReadAndReplaceAsync(response, SyncResponseBody.MaximumErrorBodyBytes, Idle, "The provider returned an oversized error response.", CancellationToken.None); }
            catch (Exception error) { failure = error; }
            Require(failure is InvalidDataException,
                $"an error body past {SyncResponseBody.MaximumErrorBodyBytes} bytes is refused at the size bound ({failure?.GetType().Name})");
        }

        using (var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
               { Content = new StreamContent(new StallingBody()) })
        {
            var started = TimeProvider.System.GetTimestamp();
            Exception? failure = null;
            try { await SyncResponseBody.ReadAndReplaceAsync(response, SyncResponseBody.MaximumErrorBodyBytes, Idle, "The provider returned an oversized error response.", CancellationToken.None); }
            catch (Exception error) { failure = error; }
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            Require(failure is ProviderTransportTimeoutException,
                $"a stalled error body is aborted by the idle bound ({failure?.GetType().Name}: {failure?.Message})");
            Require(elapsed < TimeSpan.FromSeconds(5), $"the stalled error body failed promptly ({elapsed.TotalSeconds:N1}s)");
        }
    }

    // The idle deadline the provider JSON seams inherit from the shared reader, exercised at a test-sized
    // bound so the production 60-second ceiling is never waited for.
    private static async Task SharedJsonReaderIdleAsync()
    {
        using var stalled = new StallingBody();
        Exception? failure = null;
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            using var document = await SyncResponseBody.ReadJsonAsync(stalled,
                SyncResponseBody.MaximumErrorBodyBytes, Idle,
                "The provider returned an oversized JSON body.", CancellationToken.None);
        }
        catch (Exception error) { failure = error; }
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        Require(failure is ProviderTransportTimeoutException,
            $"a stalled JSON body is aborted by the shared idle bound ({Describe(failure)})");
        Require(elapsed < TimeSpan.FromSeconds(5),
            $"the stalled JSON body failed promptly ({elapsed.TotalSeconds:N1}s)");
    }

    // Finding 7: the last two copied provider JSON readers now delegate to the one neutral bounded reader.
    // Each seam keeps its private signature, its own ceiling and its oversized text, and still ends a
    // stalled body on the caller's cancellation. Every case is in-memory: no request, socket or sleep.
    private static async Task ProviderJsonReaderSeamsAsync()
    {
        var google = JsonReaderSeam(typeof(GoogleDrivePilotClient));
        var oauth = JsonReaderSeam(typeof(DropboxOAuth));
        await AssertJsonReaderSeamAsync(google, GoogleDrivePilotClient.MaximumJsonResponseBytes,
            "Google Drive returned an oversized JSON response.",
            """{"id":"file-1","name":"a.txt"}""");
        await AssertJsonReaderSeamAsync(oauth, OAuthJsonCeiling(),
            "Dropbox returned an oversized OAuth or account response.",
            """{"access_token":"token","expires_in":14400}""");

        // The two behaviours the shared reader now parameterises stay owned by their callers: Google keeps
        // its empty-body "{}" acceptance and the OAuth/account reader keeps its 32-level nesting ceiling.
        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new ByteArrayContent([]) })
        using (var document = await google(response, CancellationToken.None))
            Require(document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.GetRawText() == "{}",
                "the Google Drive seam still accepts an empty body as an empty JSON object");

        var deeplyNested = new StringBuilder();
        for (var level = 0; level < 40; level++) deeplyNested.Append("{\"a\":");
        deeplyNested.Append('0');
        for (var level = 0; level < 40; level++) deeplyNested.Append('}');
        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StringContent(deeplyNested.ToString(), Encoding.UTF8, "application/json") })
        {
            var failure = await CaptureFailureAsync(() => oauth(response, CancellationToken.None));
            Require(failure is JsonException,
                $"the OAuth seam still refuses JSON nesting past 32 levels ({Describe(failure)})");
        }
    }

    private static Func<HttpResponseMessage, CancellationToken, Task<JsonDocument>> JsonReaderSeam(Type owner)
    {
        var method = owner.GetMethod("ReadBoundedJsonAsync", BindingFlags.NonPublic | BindingFlags.Static, null,
            [typeof(HttpResponseMessage), typeof(CancellationToken)], null)
            ?? throw new InvalidOperationException(
                $"{owner.Name} no longer has the private bounded JSON reader this check covers.");
        return method.CreateDelegate<Func<HttpResponseMessage, CancellationToken, Task<JsonDocument>>>();
    }

    // The ceiling stays owned by DropboxOAuth; the fixture reads it so a changed limit cannot leave a test
    // body under the bound, where it would stall instead of proving the size check.
    private static int OAuthJsonCeiling()
    {
        var field = typeof(DropboxOAuth).GetField("MaximumOAuthJsonBytes",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DropboxOAuth no longer declares its OAuth JSON ceiling.");
        return (int)field.GetRawConstantValue()!;
    }

    private static async Task AssertJsonReaderSeamAsync(
        Func<HttpResponseMessage, CancellationToken, Task<JsonDocument>> readJson, int maximumBytes,
        string oversizedMessage, string validJson)
    {
        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StringContent(validJson, Encoding.UTF8, "application/json") })
        using (var document = await readJson(response, CancellationToken.None))
            Require(document.RootElement.ValueKind == JsonValueKind.Object,
                "a provider JSON body is parsed through the shared reader");

        // A declared length past the seam's own ceiling is refused before a byte is read.
        using (var response = new HttpResponseMessage(HttpStatusCode.OK))
        {
            response.Content = new StreamContent(new StallingBody());
            response.Content.Headers.ContentLength = maximumBytes + 1L;
            var failure = await CaptureFailureAsync(() => readJson(response, CancellationToken.None));
            Require(failure is InvalidDataException && failure.Message == oversizedMessage,
                $"a declared body over the seam ceiling is refused at the shared size check ({Describe(failure)})");
        }

        // A body with no declared length is refused by the shared streaming bound.
        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StreamContent(new OverlongBody(maximumBytes + 1L)) })
        {
            var failure = await CaptureFailureAsync(() => readJson(response, CancellationToken.None));
            Require(failure is InvalidDataException && failure.Message == oversizedMessage,
                $"a streamed body over the seam ceiling is refused by the shared bound ({Describe(failure)})");
        }

        // A stalled body still ends on the caller's cancellation, promptly.
        using (var response = new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StreamContent(new StallingBody()) })
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            var started = TimeProvider.System.GetTimestamp();
            var failure = await CaptureFailureAsync(() => readJson(response, cancel.Token));
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            Require(failure is OperationCanceledException,
                $"caller cancellation still ends a stalled provider JSON body ({Describe(failure)})");
            Require(elapsed < TimeSpan.FromSeconds(5),
                $"the cancelled provider JSON read ended promptly ({elapsed.TotalSeconds:N1}s)");
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static string Describe(Exception? failure) =>
        failure is null ? "no exception" : $"{failure.GetType().Name}: {failure.Message}";

    // A Dropbox write-throttle answer is still classified through the bounded reader, the read lane is never
    // throttled for a write-only refusal, and the response body remains readable after classification.
    private static async Task PacingKeepsTheErrorBodyAsync()
    {
        // The flat write-throttle spelling the classifier already tests: an exact too_many_write_operations tag.
        var body = """{"error":{".tag":"too_many_write_operations"}}""";
        var limiter = new SyncTransferRateLimiter(new());
        var handler = new FixedHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://fixture.invalid/2/files/create_folder_batch");
        using var received = await SyncProviderRequestPacing.SendAsync(limiter,
            new("error-body-retention", SyncProviderOperation.Write), false, http, request,
            HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        Require(limiter.Resources.ProviderDelay(new("error-body-retention", SyncProviderOperation.Write)) > TimeSpan.Zero,
            "a write-only throttle still throttles the write lane");
        Require(limiter.Resources.ProviderDelay(new("error-body-retention", SyncProviderOperation.Read)) == TimeSpan.Zero,
            "a write-only throttle never closes the read lane");
        Require(await received.Content.ReadAsStringAsync() == body,
            "pacing leaves the error body readable for the adapter after classification");
    }

    // A stalled classification body must not outlive the caller's own cancellation.
    private static async Task PacingHonoursCallerCancellationAsync()
    {
        var limiter = new SyncTransferRateLimiter(new());
        var handler = new FixedHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StreamContent(new StallingBody()) };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://fixture.invalid/2/files/create_folder_batch");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var started = TimeProvider.System.GetTimestamp();
        Exception? failure = null;
        try
        {
            await SyncProviderRequestPacing.SendAsync(limiter,
                new("error-body-cancel", SyncProviderOperation.Write), false, http, request,
                HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        }
        catch (Exception error) { failure = error; }
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        Require(failure is OperationCanceledException,
            $"caller cancellation wins over a stalled classification body ({failure?.GetType().Name})");
        Require(elapsed < TimeSpan.FromSeconds(5), $"the cancelled classification ended promptly ({elapsed.TotalSeconds:N1}s)");
    }

    private static async Task PacingUnreadableBodyAsync(bool stalled)
    {
        var limiter = new SyncTransferRateLimiter(new());
        using var http = new HttpClient(new FixedHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = stalled ? new StreamContent(new StallingBody()) :
                    new ByteArrayContent(new byte[SyncResponseBody.MaximumErrorBodyBytes + 1])
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        }));
        var key = "unreadable-" + stalled;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://fixture.invalid/request");
        using var received = await SyncProviderRequestPacing.SendAsync(limiter,
            new(key, SyncProviderOperation.Write), false, http, request,
            HttpCompletionOption.ResponseHeadersRead, CancellationToken.None, errorBodyIdleTimeout: Idle);
        Require(await received.Content.ReadAsStringAsync() == string.Empty,
            "a failed classification leaves a bounded readable fallback body");
        Require(limiter.Resources.ProviderDelay(new(key, SyncProviderOperation.Read)) > TimeSpan.Zero &&
            limiter.Resources.ProviderDelay(new(key, SyncProviderOperation.Write)) > TimeSpan.Zero,
            "an unreadable throttle body conservatively pauses both lanes");
    }

    private sealed class FixedHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(response());
    }

    // A body that sends nothing after its headers and only ends when its read is cancelled.
    private sealed class StallingBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A body with no declared length that keeps producing bytes past any caller ceiling, without
    // allocating them up front: the shared reader must stop it at the size bound.
    private sealed class OverlongBody(long length) : Stream
    {
        private long _remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (_remaining == 0) return ValueTask.FromResult(0);
            var count = (int)Math.Min(_remaining, buffer.Length);
            buffer.Span[..count].Fill((byte)' ');
            _remaining -= count;
            return ValueTask.FromResult(count);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
