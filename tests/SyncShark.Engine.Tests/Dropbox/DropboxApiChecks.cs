using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxApi.</summary>
public static class DropboxApiChecks
{
    /// <summary>Verifies the single send owner's catalog guards before any request is built.</summary>
    public static async Task RunAsync()
    {
        var settings = SettingsParser.Load();
        var wire = new Wire();
        using var http = new HttpClient(wire);
        var api = new DropboxApi(http, TimeProvider.System, settings, _ => { });

        // A wrong typed style is rejected before any request; a rejected call never reaches the transport.
        await ExpectArgumentException(() => api.DownloadAsync("files/delete_v2", "{}", CancellationToken.None));
        await ExpectArgumentException(() => api.CallJsonAsync("files/download", "{}", CancellationToken.None));

        Check.Require(wire.Paths.Count == 0, "a rejected call reached the transport");
        var transport = Task.WhenAll(CheckConnectionProgressAsync(), CheckSlicesAsync(false), CheckSlicesAsync(true),
            CheckCompressedAsync("gzip"), CheckCompressedAsync("deflate"), CheckBodyProgressAsync(), CheckStalledRefusalAsync(false));
        try { await transport; }
        catch when (transport.Exception is not null) { throw transport.Exception; }
        await CheckClockPublicationAsync(settings);
        await CheckLongpollTimeoutAsync(settings);
    }

    private static async Task CheckConnectionProgressAsync()
    {
        // Use the production handler, not an HttpMessageHandler that can bypass the plaintext filter.
        using var server = Listen();
        var address = Address(server);
        var clock = new ConnectionClock();
        using var client = DropboxApi.CreateHttpClient(clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const int slices = 16;
        var upload = new byte[slices * 64 * 1024];
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = new ByteArrayContent(upload) };
        var pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        using var connection = await server.AcceptTcpClientAsync(stop.Token);
        using var reader = new StreamReader(connection.GetStream());
        while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
        Check.Require(await reader.ReadBlockAsync(new char[upload.Length].AsMemory(), stop.Token) == upload.Length,
            "loopback upload did not deliver its bytes");
        // The peer has read the request. Never return response headers. Expire pending (or next) activity.
        clock.Expire();
        bool timedOut = false;
        try { using var unexpected = await pending; }
        catch (Exception error) when (!stop.IsCancellationRequested && HasTimeout(error)) { timedOut = true; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Check.Require(timedOut, "ordinary pre-header read did not expire on its 120-second activity allowance");

    }

    private static System.Net.Sockets.TcpListener Listen()
    {
        var server = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        server.Start();
        return server;
    }

    private static Uri Address(System.Net.Sockets.TcpListener server) =>
        new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), ((IPEndPoint)server.LocalEndpoint).Port).Uri;

    private static bool HasTimeout(Exception error) => error is TimeoutException || error.InnerException is { } inner && HasTimeout(inner);

    private static async Task CheckCompressedAsync(string encoding)
    {
        using var server = Listen();
        using var client = DropboxApi.CreateHttpClient(TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var get = client.GetByteArrayAsync(Address(server), stop.Token);
        using var peer = await server.AcceptTcpClientAsync(stop.Token);
        var socket = peer.GetStream();
        using var reader = new StreamReader(socket, leaveOpen: true);
        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } header)
            if (header.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase))
                offered.UnionWith(header.Split(':', 2)[1].Split(',', StringSplitOptions.TrimEntries));
        var bytes = "one two three"u8.ToArray();
        using var compressed = new MemoryStream();
        using (Stream codec = encoding == "gzip" ? new GZipStream(compressed, CompressionLevel.Fastest, true)
            : new ZLibStream(compressed, CompressionLevel.Fastest, true)) codec.Write(bytes);
        await socket.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Encoding: {encoding}\r\nContent-Length: {compressed.Length}\r\n\r\n"), stop.Token);
        await socket.WriteAsync(compressed.ToArray(), stop.Token);
        var decoded = await get;
        Check.Require(offered.Contains(encoding) && decoded.SequenceEqual(bytes), $"factory did not negotiate and decode {encoding}");
    }

    private static async Task CheckBodyProgressAsync()
    {
        using var server = Listen();
        var clock = new ConnectionClock();
        using var client = DropboxApi.CreateHttpClient(clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, Address(server));
        var get = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        using var peer = await server.AcceptTcpClientAsync(stop.Token);
        var socket = peer.GetStream();
        using var reader = new StreamReader(socket, leaveOpen: true);
        while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
        await socket.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\na"u8.ToArray(), stop.Token);
        using var response = await get;
        var outcomes = new List<ResponseStream.BodyOutcome>();
        await using var body = new ResponseStream().Open(response, await response.Content.ReadAsStreamAsync(stop.Token), stop.Token, outcomes.Add);
        Check.Require(await body.ReadAsync(new byte[1], stop.Token) == 1, "download did not deliver its prefix");
        clock.Advance(TimeSpan.FromSeconds(121));
        var pending = body.ReadAsync(new byte[1], stop.Token).AsTask();
        Check.Require(!pending.IsCompleted, "consumer idle expired a fresh transport read");
        clock.Expire();
        try { await pending; }
        catch (Exception error) when (!stop.IsCancellationRequested && HasTimeout(error) && outcomes is [ResponseStream.BodyOutcome.Failed]) { return; }
        throw new InvalidDataException("fresh stalled transport read did not report Failed before disposal");
    }

    private static async Task CheckSlicesAsync(bool expire)
    {
        using var server = Listen();
        if (expire) server.Server.ReceiveBufferSize = 4096; // Limit the advertised window before the handshake.
        var clock = new ConnectionClock();
        using var client = DropboxApi.CreateHttpClient(clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var content = new SliceContent(clock, expire);
        using var request = new HttpRequestMessage(HttpMethod.Post, Address(server)) { Content = content };
        var post = client.SendAsync(request, stop.Token);
        using var peer = await server.AcceptTcpClientAsync(stop.Token);
        if (expire)
        {
            // Leave the socket open without reading: only the pending write's own deadline may end it.
            try { using var unexpected = await post.WaitAsync(stop.Token); }
            catch (Exception error) when (!stop.IsCancellationRequested && content.ExpiredPending && HasTimeout(error)) { return; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            throw new InvalidDataException("120-second write deadline did not cancel the already-pending socket write");
        }
        var socket = peer.GetStream();
        // Keep the first body byte: a buffered text reader could read ahead into the measured slice.
        var one = new byte[1];
        int end = 0;
        while (end < 4 && await socket.ReadAsync(one, stop.Token) == 1)
            end = one[0] == "\r\n\r\n"[end] ? end + 1 : one[0] == 13 ? 1 : 0;
        var bytes = new byte[content.Length];
        await socket.ReadExactlyAsync(bytes, stop.Token);
        await socket.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), stop.Token);
        using var result = await post;
        result.EnsureSuccessStatusCode();
        Check.Require(bytes.Take(SliceContent.Slice).All(x => x == 1) && bytes.Skip(SliceContent.Slice).All(x => x == 2),
            "wire upload slice boundary was not exactly 64 KiB");
    }

    private sealed class SliceContent(ConnectionClock clock, bool expire) : HttpContent
    {
        internal const int Slice = 64 * 1024;
        internal int Length => expire ? 1024 * Slice : 2 * Slice;
        internal bool ExpiredPending { get; private set; }
        protected override bool TryComputeLength(out long length) { length = Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, default);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            await stream.FlushAsync(ct); // Headers must not contribute to the measured body slice.
            if (expire) { await WriteUntilBlockedAsync(stream, ct); return; }
            var payload = Enumerable.Repeat((byte)1, Length).ToArray();
            var count = 0;
            clock.Writing.Value = (_, _) => { if (++count == 2) Array.Fill(payload, (byte)2); };
            try { await stream.WriteAsync(payload.AsMemory(), ct); }
            finally { clock.Writing.Value = null; }
        }

        private async Task WriteUntilBlockedAsync(Stream stream, CancellationToken ct)
        {
            var slice = new byte[Slice];
            Action? fire = null;
            clock.Writing.Value = (due, callback) => fire = due == TimeSpan.FromSeconds(120) ? callback : null;
            try
            {
                for (int sent = 0; sent < Length; sent += Slice)
                {
                    fire = null;
                    var write = stream.WriteAsync(slice.AsMemory(), ct).AsTask();
                    // Ignore scheduling yields: expire only a write still pending against the non-reading peer.
                    try { await write.WaitAsync(TimeSpan.FromSeconds(2), ct); }
                    catch (TimeoutException) when (!write.IsCompleted && fire is not null)
                    {
                        ExpiredPending = true;
                        fire();
                        await write;
                        throw new InvalidDataException("expired pending socket write succeeded");
                    }
                }
            }
            finally { clock.Writing.Value = null; }
        }
    }

    private sealed class ConnectionClock : TimeProvider
    {
        private int expired;
        internal readonly AsyncLocal<Action<TimeSpan, Action>?> Writing = new();
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref now, amount.Ticks);
        private long now;
        private event Action? OnExpiry;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref now);
        internal void Expire() { Interlocked.Exchange(ref expired, 1); OnExpiry?.Invoke(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Active(this, callback, state);
            OnExpiry += timer.Fire;
            timer.Change(dueTime, period);
            return timer;
        }
        private sealed class Active(ConnectionClock clock, TimerCallback callback, object? state) : ITimer
        {
            private int armed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var active = dueTime == TimeSpan.FromSeconds(120);
                Interlocked.Exchange(ref armed, active ? 1 : 0);
                if (dueTime != Timeout.InfiniteTimeSpan)
                    clock.Writing.Value?.Invoke(dueTime, () => { clock.Advance(dueTime); callback(state); });
                if (Volatile.Read(ref clock.expired) != 0) Fire();
                return true;
            }
            internal void Fire()
            {
                if (Interlocked.Exchange(ref armed, 0) == 0) return;
                Interlocked.Add(ref clock.now, TimeSpan.FromSeconds(120).Ticks);
                ThreadPool.QueueUserWorkItem(callback.Invoke, state);
            }
            public void Dispose() { clock.OnExpiry -= Fire; Interlocked.Exchange(ref armed, 0); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static async Task CheckStalledRefusalAsync(bool requireClassification)
    {
        await CheckDeadlineRefusalAsync(requireClassification);
        var wait = TimeSpan.FromSeconds(17);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var wire = new Wire { Status = 429, HeaderWait = wait, Body = "{", ReadGate = Task.FromException(new TimeoutException()) };
        using var http = new HttpClient(wire);
        var api = new DropboxApi(http, clock, SettingsParser.Load(), _ => { });
        try { await api.LongpollAsync("cursor", default); }
        catch (ItemFailedException error) when (error.Disposition == FailureDisposition.TemporaryNoEffect &&
            error.ProviderWait == wait && error.ProviderWaitStarted == clock.GetTimestamp()) { return; }
        catch (NotImplementedException) when (!requireClassification && wire.LastReply?.Headers.RetryAfter?.Delta == wait) { return; }
        catch (ItemFailedException) { }
        throw new InvalidDataException("stalled refusal body lost the received Retry-After delay");
    }

    private static async Task CheckDeadlineRefusalAsync(bool requireClassification)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource<CancellationToken>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = TimeSpan.FromMinutes(10); // The floor outlasts the 570-second poll.
        var wire = new Wire { Status = 429, HeaderWait = wait, PendingBody = entered, ReadGate = release.Task };
        using var http = new HttpClient(wire);
        var api = new DropboxApi(http, clock, SettingsParser.Load(), _ => { });
        using var stop = new CancellationTokenSource();
        try
        {
            var cancelled = api.LongpollAsync("cursor", stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            try { await cancelled; throw new InvalidDataException("cancelled refusal returned normally"); }
            catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { }
            wire.PendingBody = new();
            var received = clock.GetTimestamp();
            var poll = api.LongpollAsync("cursor", default);
            var bodyToken = await wire.PendingBody.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.ElapseScheduledDelay();
            Check.Require(bodyToken.IsCancellationRequested, "poll deadline did not reach the stalled refusal body");
            try { await poll.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (ItemFailedException error) when (error.Disposition == FailureDisposition.TemporaryNoEffect &&
                error.ProviderWait == wait && error.ProviderWaitStarted == received) { return; }
            catch (NotImplementedException) when (!requireClassification && wire.LastReply?.StatusCode == HttpStatusCode.TooManyRequests &&
                wire.LastReply.Headers.RetryAfter?.Delta == wait) { return; }
            catch (ItemFailedException) { }
            throw new InvalidDataException("stalled refusal body lost the received Retry-After delay at the poll deadline");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task CheckClockPublicationAsync(EngineSettings settings)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var receipt = new ReceiptClock(clock, TimeSpan.FromSeconds(1));
        var wire = new Wire { Date = DateTimeOffset.UnixEpoch.AddSeconds(10) };
        using var http = new HttpClient(wire);
        var api = new DropboxApi(http, receipt, settings, _ => { });
        using var older = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint("oauth2/token"));
        using var newer = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint("oauth2/token"));
        Task<HttpResponseMessage>? inner = null;
        (TimeSpan Offset, TimeSpan Uncertainty)? latest = null;
        receipt.AfterWall = () =>
        {
            wire.Date = DateTimeOffset.UnixEpoch.AddMinutes(1);
            inner = api.SendAsync(newer, default);
            latest = api.ClockOffset;
        };
        using var first = await api.SendAsync(older, default);
        using var second = await (inner ?? throw new InvalidDataException("newer request was not sent"));
        Check.Require(latest?.Offset == TimeSpan.FromSeconds(59) && api.ClockOffset == latest,
            "older dated receipt overwrote the newer clock sample");
        wire.Date = DateTimeOffset.UnixEpoch.AddMinutes(2);
        wire.Clock = clock;
        wire.Advance = TimeSpan.FromSeconds(3);
        using var last = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint("oauth2/token"));
        using var third = await api.SendAsync(last, default);
        Check.Require(api.ClockOffset == (TimeSpan.FromSeconds(115), TimeSpan.FromSeconds(5)),
            "later dated receipt did not replace the first published clock sample");
    }

    private static async Task CheckLongpollTimeoutAsync(EngineSettings settings)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource<CancellationToken>();
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new Wire { PendingBody = entered, ReadGate = unblock.Task };
        using var http = new HttpClient(wire) { Timeout = Timeout.InfiniteTimeSpan };
        var api = new DropboxApi(http, clock, settings, _ => { });
        using var stop = new CancellationTokenSource();
        var timed = api.LongpollAsync("cursor", stop.Token);
        using var args = System.Text.Json.JsonDocument.Parse(wire.Bodies.Single());
        var sent480 = args.RootElement.TryGetProperty("timeout", out var seconds) && seconds.GetInt32() == 480;
        try
        {
            Check.Require(!timed.IsCompleted && entered.Task.IsCompletedSuccessfully,
                "longpoll did not reach its response body after headers");
            Check.Require(clock.ScheduledDelay == TimeSpan.FromSeconds(480 + 90),
                "longpoll did not use the requested wait plus its client allowance");
            clock.ElapseScheduledDelay();
            var bodyExpired = (await entered.Task).IsCancellationRequested;
            var errors = new List<string>();
            if (!sent480) errors.Add("longpoll wire timeout was not 480 seconds");
            if (!bodyExpired) errors.Add("longpoll response body was not cancelled at 570 seconds");
            Check.Require(errors.Count == 0, string.Join("; ", errors));
            try { await timed.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidDataException("timed-out longpoll was accepted as quiet"); }
            catch (ItemFailedException error) when (error.Cause == TextKey.ReadDropboxFailed &&
                error.Disposition == FailureDisposition.TemporaryNoEffect && !stop.IsCancellationRequested) { }
            catch (Exception error) when (error is not InvalidDataException)
            { throw new InvalidDataException("expired longpoll body did not produce a retryable read failure", error); }
        }
        finally { unblock.TrySetResult(); }
        wire.PendingBody = null;
        wire.HoldSend = true;
        wire.ClientTimeout = true;
        try { await api.LongpollAsync("cursor", stop.Token); throw new InvalidDataException("client timeout was accepted as quiet"); }
        catch (ItemFailedException error) when (error.Cause == TextKey.ReadDropboxFailed &&
            error.Disposition == FailureDisposition.TemporaryNoEffect && !stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not InvalidDataException)
        { throw new InvalidDataException("client timeout did not produce a retryable read failure", error); }
        wire.ClientTimeout = false;

        var cancelled = api.LongpollAsync("cursor", stop.Token);
        stop.Cancel();
        try { await cancelled; throw new InvalidDataException("caller cancellation was swallowed"); }
        catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { }
        catch (Exception error) when (error is not InvalidDataException)
        { throw new InvalidDataException("longpoll did not preserve the caller cancellation token", error); }
    }

    /// <summary>Checks transport binding, receiver timing, storage replies and classified refusals.</summary>
    public static async Task SendPathAsync()
    {
        var settings = SettingsParser.Load();

        await CheckStalledRefusalAsync(true);
        await CheckDecoderStallAsync(settings);
        await CheckReadRepliesAsync(settings);
        await CheckGrowthAsync(settings);
        // Separate hosts: failure to start the first period must not hide a failure at expiry.
        var periods = Task.WhenAll(CheckContentionWidthAsync(settings, "start"), CheckContentionWidthAsync(settings, "inside"),
            CheckContentionWidthAsync(settings, "expiry"));
        try { await periods; }
        catch (NotImplementedException) when (periods.Exception!.InnerExceptions.All(error => error is NotImplementedException)) { throw; }
        catch when (periods.Exception is not null) { throw periods.Exception; }
        foreach (var (olderWait, reverse, expected) in new[] { (2, false, 2), (2, true, 4), (20, true, 4), (20, false, 4) })
            await CheckBusyReceiptsAsync(settings, olderWait, reverse, expected);

        // The prepared path uses the same binding: each account's own token, and the same verified root.
        var preparedWire = new Wire();
        using (var preparedHttp = new HttpClient(preparedWire))
        {
            var preparedApi = new DropboxApi(preparedHttp, TimeProvider.System, settings, _ => { },
                _ => new ValueTask<string?>("token-b"), "shared-root");
            using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint("files/delete_v2"))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            using var response = await preparedApi.SendAsync(request, CancellationToken.None);
            Check.Require(preparedWire.Authorizations.Count == 1 &&
                preparedWire.Authorizations[0] == "Bearer token-b", "prepared path missing its token");
            Check.Require(preparedWire.PathRoots.Count == 1 &&
                preparedWire.PathRoots[0].Contains("shared-root", StringComparison.Ordinal), "prepared path missing its root");
        }

        // A refused prepared upload must leave the caller-owned source open (no request disposal closes it).
        var caller = new TrackingStream(new byte[] { 1, 2, 3 });
        var refusalWire = new Wire { Status = 409, Body = "{\"error_summary\":\"path/not_found\"}" };
        using (var refusalHttp = new HttpClient(refusalWire))
        {
            var refusalApi = new DropboxApi(refusalHttp, TimeProvider.System, settings, _ => { },
                _ => new ValueTask<string?>("token"), "root-id");
            using var upload = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint("files/upload"))
            {
                Content = new StreamContent(caller)
            };
            upload.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var refusedResponse = await refusalApi.SendAsync(upload, CancellationToken.None);
            Check.Require(refusedResponse.StatusCode == HttpStatusCode.Conflict, "prepared refusal status not returned");
            Check.Require(await refusedResponse.Content.ReadAsStringAsync() == refusalWire.Body, "prepared refusal body changed");
            Check.Require(!caller.Disposed, "a refused send closed the caller-owned upload source");
        }
    }

    private static async Task CheckDecoderStallAsync(EngineSettings settings)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new Wire { ReadGate = gate.Task, DecoderRead = entered, Body = "{\"changes\":false}" };
        using var http = new HttpClient(wire) { Timeout = Timeout.InfiniteTimeSpan };
        var api = new DropboxApi(http, clock, settings, _ => { });
        var poll = api.LongpollAsync("cursor", default);
        try
        {
            // A missing decoder must stay NOT IMPLEMENTED; it cannot count as entering an open stream.
            if (await Task.WhenAny(poll, entered.Task).WaitAsync(TimeSpan.FromSeconds(5)) == poll) await poll;
            var reader = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.ElapseScheduledDelay();
            Check.Require(reader.IsCancellationRequested, "open longpoll stream decoder did not receive the expired poll token");
            try { await poll.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (ItemFailedException failure) when (failure.Disposition == FailureDisposition.TemporaryNoEffect) { return; }
            throw new InvalidDataException("open longpoll stream expiry was not a temporary failure");
        }
        finally { gate.TrySetResult(); }
    }

    private static async Task CheckReadRepliesAsync(EngineSettings settings)
    {
        // Clock: exact signed offset and full-RTT-plus-one-second uncertainty from a successful dated reply.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var gap = TimeSpan.FromSeconds(7);
        var receiptClock = new ReceiptClock(clock, gap);
        var rtt = TimeSpan.FromSeconds(5);
        var serverDate = DateTimeOffset.UnixEpoch.AddSeconds(100);
        var clockWire = new Wire
        {
            Clock = clock,
            Advance = rtt,
            Date = serverDate,
            Body = "{\"used\":1,\"allocation\":{\".tag\":\"individual\",\"allocated\":2}}"
        };
        using (var clockHttp = new HttpClient(clockWire))
        {
            var clockApi = new DropboxApi(clockHttp, receiptClock, settings, _ => { },
                _ => new ValueTask<string?>("token"), "root-id");
            var (used, allocated) = await clockApi.SpaceUsageAsync(CancellationToken.None);
            Check.Require(used == 1 && allocated == 2, "individual space usage decode");
            Check.Require(clockWire.Bodies.Count == 1 && clockWire.Bodies[0] == "null", "void wire body is not JSON null");
            var sample = clockApi.ClockOffset ?? throw new InvalidDataException("clock sample not recorded");
            Check.Require(sample.Offset == serverDate - (DateTimeOffset.UnixEpoch + rtt), "clock offset wrong");
            Check.Require(sample.Uncertainty == rtt + gap + TimeSpan.FromSeconds(1), "clock uncertainty wrong");
            await CheckHeaderReceiptAsync(clockApi, clockWire, clock);
            await CheckReadRefusalsAsync(clockApi, clockWire, serverDate);
            Check.Require(clockApi.ClockOffset == sample, "a later dated refusal replaced the sample");
            clockWire.Status = 200;
            clockWire.Body = """{"used":1,"allocation":{".tag":"team","used":7,"allocated":11}}""";
            Check.Require(await clockApi.SpaceUsageAsync(default) == (7, 11), "team usage came from the account");
            foreach (var (usedBytes, allocatedBytes) in new[] { (-1, 2), (1, -2) })
            {
                clockWire.Body = System.Text.Json.JsonSerializer.Serialize(new { used = usedBytes, allocation = new { allocated = allocatedBytes } });
                try { await clockApi.SpaceUsageAsync(default); }
                catch (InvalidDataException) { continue; }
                throw new InvalidDataException("negative storage amount accepted");
            }
        }
    }

    private static async Task CheckHeaderReceiptAsync(DropboxApi api, Wire wire, FakeTimeProvider clock)
    {
        wire.Status = 429;
        wire.Body = "{";
        var headerWall = clock.GetUtcNow() + wire.Advance;
        var stamp = headerWall.UtcTicks;
        var requested = TimeSpan.FromSeconds(30);
        wire.HeaderDate = headerWall + requested;
        try { await api.SpaceUsageAsync(default); }
        catch (ItemFailedException error) when (error.ProviderWaitStarted == stamp && error.ProviderWait == requested)
        {
            wire.HeaderDate = null;
            return;
        }
        throw new InvalidDataException("refusal did not use the paired header receipt samples");
    }

    private static async Task CheckReadRefusalsAsync(DropboxApi clockApi, Wire clockWire, DateTimeOffset serverDate)
    {
        clockWire.Status = 429;
        clockWire.Date = serverDate.AddDays(1);
        clockWire.Body = """{"error":{"retry_after":13}}""";
        clockWire.HeaderWait = TimeSpan.FromSeconds(2);
        try
        {
            await clockApi.SpaceUsageAsync(default);
            throw new InvalidDataException("busy read accepted");
        }
        catch (ItemFailedException error)
        {
            Check.Require(error.Disposition == FailureDisposition.TemporaryNoEffect &&
                error.ProviderWait == TimeSpan.FromSeconds(13) && error.ProviderWaitStarted.HasValue,
                "busy read lost its nested provider delay or retry disposition");
        }
        clockWire.Body = "{";
        try
        {
            await clockApi.SpaceUsageAsync(default);
            throw new InvalidDataException("malformed busy read accepted");
        }
        catch (ItemFailedException error) when (error.Disposition == FailureDisposition.TemporaryNoEffect &&
            error.ProviderWait == clockWire.HeaderWait) { }
        clockWire.Status = 409;
        foreach (var (payload, reset) in new[]
        {
            ("""{"error":{".tag":"reset"}}""", true),
            ("""{"error":{".tag":"other"}}""", false),
            ("""{"error":{".tag":"other"},"error_summary":"reset/..."}""", false),
            ("""{"error_summary":42}""", false)
        })
        {
            clockWire.Body = payload;
            try { using var unused = await clockApi.CallJsonAsync("files/list_folder/continue", "{}", default); }
            catch (ListingCursorResetException) when (reset) { continue; }
            catch (ItemFailedException) when (!reset) { continue; }
            throw new InvalidDataException("continuation refusal accepted");
        }
    }

    private static async Task CheckContentionWidthAsync(EngineSettings settings, string scenario)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(scenario == "start" ? 5 : 10));
        var wire = new Wire { Status = 429 };
        using var http = new HttpClient(wire);
        var api = new DropboxApi(http, clock, settings with { InitialLimiterLevel = 8 }, _ => { },
            _ => ValueTask.FromResult<string?>("a"), "root");
        async Task RefusalAsync(bool contention, int? seconds)
        {
            var cause = contention ? TextKey.WaitTooManyWrites : TextKey.DropboxRefused;
            wire.Body = contention ? """{"error":{"reason":{".tag":"too_many_write_operations"},"retry_after":20}}""" : "{}";
            wire.HeaderWait = seconds is { } delay ? TimeSpan.FromSeconds(delay) : null;
            var wait = contention ? TimeSpan.FromSeconds(20) : wire.HeaderWait;
            try { await api.SpaceUsageAsync(default); }
            catch (ItemFailedException error) when (error.Cause == cause && error.ProviderWait == wait) { return; }
            throw new InvalidDataException("unexpected busy read disposition");
        }
        if (scenario == "start")
        {
            await RefusalAsync(true, null);
            await CheckAdmissionAsync(api, wire, 8, "write contention reduced admission");
            clock.Advance(TimeSpan.FromSeconds(5));
            await RefusalAsync(false, 2);
            await CheckAdmissionAsync(api, wire, 4, "contention at 5 suppressed the first request throttle at 10");
            return;
        }
        await RefusalAsync(false, 2); // Open [10,12).
        clock.Advance(TimeSpan.FromSeconds(1));
        if (scenario == "inside")
        {
            await RefusalAsync(false, null);
            await CheckAdmissionAsync(api, wire, 4, "a request throttle inside the open period reduced admission again");
            return;
        }
        await RefusalAsync(true, null);
        clock.Advance(TimeSpan.FromSeconds(1));
        await RefusalAsync(false, 2);
        await CheckAdmissionAsync(api, wire, 2, "contention at 11 suppressed the new request throttle at expiry 12");
    }

    private static async Task CheckBusyReceiptsAsync(EngineSettings settings, int olderWait, bool reverse, int expected)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(5));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new Wire { Status = 429, HeaderWait = TimeSpan.FromSeconds(olderWait), ReadGate = release.Task };
        using var http = new HttpClient(wire);
        var api = new DropboxApi(http, clock, settings with { InitialLimiterLevel = 8 }, _ => { },
            _ => ValueTask.FromResult<string?>("a"), "root");
        async Task RefusalAsync()
        {
            try { await api.SpaceUsageAsync(default); }
            catch (ItemFailedException error) when (error.Disposition == FailureDisposition.TemporaryNoEffect) { return; }
            throw new InvalidDataException("busy reply accepted");
        }
        var first = RefusalAsync();
        Check.Require(!first.IsCompleted, "first refusal body was not delayed");
        if (!reverse) { release.SetResult(); await first; }
        clock.Advance(TimeSpan.FromSeconds(1));
        var otherEarly = reverse ? RefusalAsync() : Task.CompletedTask;
        clock.Advance(TimeSpan.FromSeconds(14));
        wire.HeaderWait = TimeSpan.FromSeconds(10);
        wire.ReadGate = null;
        await RefusalAsync();
        if (reverse) { release.SetResult(); await Task.WhenAll(first, otherEarly); }
        await CheckAdmissionAsync(api, wire, expected);
    }

    private static async Task CheckGrowthAsync(EngineSettings settings)
    {
        var wire = new Wire();
        using var http = new HttpClient(wire);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var api = new DropboxApi(http, clock, settings with { InitialLimiterLevel = 1 }, _ => { },
            _ => ValueTask.FromResult<string?>("a"), "root");
        using (await api.CallJsonAsync("files/get_metadata", "{}", default)) { }
        await using (var abandoned = await api.DownloadAsync("files/download", "{}", default))
        {
            await CheckAdmissionAsync(api, wire, 0);
            Check.Require(await abandoned.ReadAsync(new byte[1]) == 1, "prefix was not delivered");
            await CheckAdmissionAsync(api, wire, 0);
        }
        await CheckAdmissionAsync(api, wire, 1);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.ReadGate = unblock.Task;
        await using (var stalled = await api.DownloadAsync("files/download", "{}", default))
        {
            try
            {
                var reading = stalled.ReadAsync(new byte[1]).AsTask();
                await CheckAdmissionAsync(api, wire, 0, "pending ordinary read did not hold its slot");
                unblock.SetException(new TimeoutException()); // The connection activity guard terminates the read.
                try { await reading; throw new InvalidDataException("stalled download returned bytes"); }
                catch (TimeoutException) { }
                await CheckAdmissionAsync(api, wire, 1, "expired ordinary read held its slot until disposal");
            }
            finally { unblock.TrySetResult(); wire.ReadGate = null; }
        }
        wire.FailRead = true;
        await using (var failed = await api.DownloadAsync("files/download", "{}", default))
        {
            try { await failed.ReadAsync(new byte[1]); throw new InvalidDataException("failed download was readable"); }
            catch (IOException error) when (error.GetType() == typeof(IOException)) { }
        }
        wire.FailRead = false;
        await CheckAdmissionAsync(api, wire, 1);
        await using (var complete = await api.DownloadAsync("files/download", "{}", default))
        {
            Check.Require(await complete.ReadAsync(new byte[1]) == 1, "complete case did not read a prefix");
            await CheckAdmissionAsync(api, wire, 0);
            await complete.CopyToAsync(Stream.Null);
            await CheckAdmissionAsync(api, wire, 2);
        }
        await CheckAdmissionAsync(api, wire, 2);
        await using (var next = await api.DownloadAsync("files/download", "{}", default))
        {
            await next.CopyToAsync(Stream.Null);
            await CheckAdmissionAsync(api, wire, 2); // Double credit on the previous close would have grown to four.
        }
    }

    // Park ordinary requests at the wire; the ones reaching it before cancellation expose the actual admission level.
    private static async Task CheckAdmissionAsync(DropboxApi api, Wire wire, int expected, string? reason = null)
    {
        using var stop = new CancellationTokenSource();
        wire.HoldSend = true;
        var baseline = wire.Paths.Count;
        async Task ProbeAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint("files/get_metadata"));
            try { using var response = await api.SendAsync(request, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        var pending = Enumerable.Range(0, expected + 1).Select(_ => ProbeAsync()).ToArray();
        var admitted = wire.Paths.Count - baseline;
        stop.Cancel();
        await Task.WhenAll(pending);
        wire.HoldSend = false;
        Check.Require(admitted == expected, (reason is null ? string.Empty : reason + ": ") + $"expected {expected} admitted requests, observed {admitted}");
    }

    private static async Task ExpectArgumentException(Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidDataException("a rejected call was accepted");
        }
        catch (ArgumentException)
        {
        }
    }

    // A scheduler pause immediately after sampling wall time must be included in the elapsed bound.
    private sealed class ReceiptClock(FakeTimeProvider clock, TimeSpan gap) : TimeProvider
    {
        internal Action? AfterWall { get; set; }
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override long GetTimestamp() => clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow()
        {
            var wall = clock.GetUtcNow();
            clock.Advance(gap);
            var after = AfterWall;
            AfterWall = null;
            after?.Invoke();
            return wall;
        }
    }

    private sealed class Wire : HttpMessageHandler
    {
        internal TaskCompletionSource<CancellationToken>? PendingBody { get; set; }
        internal Task? ReadGate { get; set; }
        internal TaskCompletionSource<CancellationToken>? DecoderRead { get; set; }
        internal bool HoldSend { get; set; }
        internal bool ClientTimeout { get; set; }
        internal bool FailRead { get; set; }
        internal HttpResponseMessage? LastReply { get; private set; }
        internal int Status { get; set; } = 200;
        internal string Body { get; set; } = "{}";
        internal DateTimeOffset? Date { get; set; }
        internal DateTimeOffset? HeaderDate { get; set; }
        internal TimeSpan? HeaderWait { get; set; }
        internal TimeSpan Advance { get; set; }
        internal FakeTimeProvider? Clock { get; set; }
        internal List<string> Paths { get; } = [];
        internal List<string> Bodies { get; } = [];
        internal List<string> Authorizations { get; } = [];
        internal List<string> PathRoots { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (ClientTimeout) throw new TaskCanceledException("HTTP client timeout", new TimeoutException());
            var current = request.Content;
            Bodies.Add(current is null ? string.Empty : await current.ReadAsStringAsync(cancellationToken));
            if (HoldSend) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (request.Headers.Authorization is { } authorization)
                Authorizations.Add(authorization.Scheme + " " + (authorization.Parameter ?? string.Empty));
            if (request.Headers.TryGetValues("Dropbox-API-Path-Root", out var roots)) PathRoots.AddRange(roots);
            Clock?.Advance(Advance);
            var response = new HttpResponseMessage((HttpStatusCode)Status)
            {
                Content = PendingBody is { } pending
                    ? new PendingContent(pending, ReadGate!)
                    : new StreamContent(new SplitReply(Encoding.UTF8.GetBytes(Body), ReadGate, FailRead, DecoderRead))
            };
            if (HeaderDate is { } expires) response.Headers.RetryAfter = new RetryConditionHeaderValue(expires);
            if (HeaderWait is { } wait) response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
            if (Date is { } date) response.Headers.Date = date;
            return LastReply = response;
        }
    }

    // HttpContent buffers a non-streaming reply on ReadAsStreamAsync, after the handler has returned its headers.
    private sealed class PendingContent(TaskCompletionSource<CancellationToken> entered, Task release) : HttpContent
    {
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            await CopyToAsync(buffer, ct);
            return new MemoryStream(buffer.ToArray(), writable: false);
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            await stream.WriteAsync("{"u8.ToArray(), ct);
            entered.TrySetResult(ct);
            await release.WaitAsync(ct);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class SplitReply(byte[] bytes, Task? gate, bool fail, TaskCompletionSource<CancellationToken>? entered) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            entered?.TrySetResult(ct);
            if (gate != null) await gate.WaitAsync(ct);
            if (fail) throw new IOException();
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], ct);
        }
    }

    private sealed class TrackingStream : MemoryStream
    {
        internal bool Disposed { get; private set; }

        internal TrackingStream(byte[] bytes) : base(bytes, writable: false)
        {
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }
}
