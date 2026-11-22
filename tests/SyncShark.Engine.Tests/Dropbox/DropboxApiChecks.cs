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
        await CheckClockPublicationAsync(settings);
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

    /// <summary>Checks transport binding, receiver timing, storage replies and classified refusals.</summary>
    public static async Task SendPathAsync()
    {
        var settings = SettingsParser.Load();

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
        var api = new DropboxApi(http, TimeProvider.System, settings with { InitialLimiterLevel = 1 }, _ => { },
            _ => ValueTask.FromResult<string?>("a"), "root");
        using (await api.CallJsonAsync("files/get_metadata", "{}", default)) { }
        await using (var abandoned = await api.DownloadAsync("files/download", "{}", default))
        {
            await CheckAdmissionAsync(api, wire, 0);
            Check.Require(await abandoned.ReadAsync(new byte[1]) == 1, "prefix was not delivered");
            await CheckAdmissionAsync(api, wire, 0);
        }
        await CheckAdmissionAsync(api, wire, 1);
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
        internal Task? ReadGate { get; set; }
        internal bool HoldSend { get; set; }
        internal bool FailRead { get; set; }
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
            if (HoldSend) await Task.Delay(Timeout.Infinite, cancellationToken);
            var current = request.Content;
            Bodies.Add(current is null ? string.Empty : await current.ReadAsStringAsync(cancellationToken));
            if (request.Headers.Authorization is { } authorization)
                Authorizations.Add(authorization.Scheme + " " + (authorization.Parameter ?? string.Empty));
            if (request.Headers.TryGetValues("Dropbox-API-Path-Root", out var roots)) PathRoots.AddRange(roots);
            Clock?.Advance(Advance);
            var response = new HttpResponseMessage((HttpStatusCode)Status)
            {
                Content = new StreamContent(new SplitReply(Encoding.UTF8.GetBytes(Body), ReadGate, FailRead))
            };
            if (HeaderDate is { } expires) response.Headers.RetryAfter = new RetryConditionHeaderValue(expires);
            if (HeaderWait is { } wait) response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
            if (Date is { } date) response.Headers.Date = date;
            return response;
        }
    }

    private sealed class SplitReply(byte[] bytes, Task? gate, bool fail) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
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
