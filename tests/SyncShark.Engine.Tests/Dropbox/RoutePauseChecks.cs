using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>A stated too_many_requests wait pauses that route: calls inside it are answered with the same 429, unsent.</summary>
public static class RoutePauseChecks
{
    private const string Paused = DropboxProtocol.GetMetadata;
    private const string Other = DropboxProtocol.ListFolder;

    public static async Task RunAsync()
    {
        await SameReplyUnsentAsync();
        await LaterShorterAsync();
        await WriteContentionOpensNoPauseAsync();
        await ReasonlessDoesNotPauseAsync("");
        await ReasonlessDoesNotPauseAsync("not json");
        await ReasonlessDoesNotPauseAsync("{}");
        await ReasonedPausesAsync($$"""{"error_summary":"{{DropboxProtocol.TooManyRequests}}/.."}""");
        await ReasonedPausesAsync($$$$"""{"error":{"reason":{".tag":"{{{{DropboxProtocol.TooManyRequests}}}}"}}}""");
        // One readable record of the reason is enough when the other cannot be read.
        await ReasonedPausesAsync($$$$"""{"error_summary":"\uD800","error":{"reason":{".tag":"{{{{DropboxProtocol.TooManyRequests}}}}"}}}""");
        await ReasonedPausesAsync($$$$"""{"error_summary":"{{{{DropboxProtocol.TooManyRequests}}}}/..","error":{"reason":{".tag":"\uD800"}}}""");
        await BeyondDeltaHeaderAsync(false);
        await BeyondDeltaHeaderAsync(true);
        await DispatcherFactAsync();
        await StagingFactAsync();
        await PausedWhileQueuedAsync();
        await LongpollAsync();
    }

    // The real fixture Dropbox sends for too_many_requests; a local reply must carry the same status, header and body.
    private static string Body(string reason, long seconds) =>
        $$$"""{"error_summary":"{{{reason}}}/..","error":{"reason":{".tag":"{{{reason}}}"},"retry_after":{{{seconds}}}}}""";

    private static HttpResponseMessage Refusal(string reason, long seconds)
    {
        var reply = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(Body(reason, seconds), Encoding.UTF8, "application/json") };
        reply.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return reply;
    }

    private static HttpResponseMessage Ok(string body = "{}") => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static (HeldWire Wire, FakeTimeProvider Clock, DropboxApi Api) Create() => Create(new FakeTimeProvider(DateTimeOffset.UnixEpoch));

    private static (HeldWire Wire, T Clock, DropboxApi Api) Create<T>(T clock) where T : TimeProvider
    {
        var settings = SettingsParser.Load() with { InitialLimiterLevel = 2 };
        var wire = new HeldWire();
        var host = new DropboxApi(new HttpClient(wire) { Timeout = Timeout.InfiniteTimeSpan }, clock, settings, _ => { });
        return (wire, clock, host.CreateAccountScope("acct-a", _ => ValueTask.FromResult<string?>("token"), "root"));
    }

    private static async Task Call(DropboxApi api, string route) => (await api.CallJsonAsync(route, "{}", default)).Dispose();

    private static async Task<ItemFailedException> Refused(Task call)
    {
        try { await call; }
        catch (ItemFailedException error) { return error; }
        throw new InvalidDataException("call was not refused");
    }

    // Callers classify a local reply like the real 429; only its run-log provider fact says it was paused locally.
    private static void Same(ItemFailedException real, ItemFailedException local, TimeSpan wait, string what,
        long stated = 300, string route = Paused)
    {
        Check.Require(local.Cause == real.Cause && local.Disposition == real.Disposition &&
            local.Disposition == FailureDisposition.TemporaryNoEffect && local.ProviderWait == wait && local.ProviderWaitStarted.HasValue,
            what + ": local reply was not classified like the real 429");
        Check.Require(real.Provider == DropboxErrors.Fact(route, HttpStatusCode.TooManyRequests, DropboxProtocol.TooManyRequests + "/.."),
            what + ": a real 429 lost its provider fact");
        Check.Require(local.Provider == Text.Get(TextKey.ProviderPausedLocally, (TextArg.Operation, route),
            (TextArg.Value, stated.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (TextArg.Seconds, ((long)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture))),
            what + ": a local reply was logged as a real 429");
    }

    private static async Task SameReplyUnsentAsync()
    {
        var (wire, clock, api) = Create();
        // Two in-flight refusals: a later, longer stated end wins; a later, shorter one does not shorten it.
        var first = Call(api, Paused);
        await wire.Arrivals(1);
        var second = Call(api, Paused);
        await wire.Reply(0, Refusal(DropboxProtocol.TooManyRequests, 10));
        await Refused(first);
        await wire.Reply(1, Refusal(DropboxProtocol.TooManyRequests, 300));
        var real = await Refused(second);
        var third = Call(api, Paused);
        Check.Require(await wire.Settled(third, 3) == 2, "a paused route reached Dropbox");
        Same(real, await Refused(third), TimeSpan.FromSeconds(300), "longest end");

        // N concurrent calls inside the pause: zero sends, each sees the real 429.
        var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() => Call(api, Paused))).ToArray();
        foreach (var call in calls) Same(real, await Refused(call), TimeSpan.FromSeconds(300), "concurrent");
        Check.Require(wire.Count == 2, "a paused call reached Dropbox");
        // The raw reply a raw caller reads is the real one: status, header and body.
        using (var raw = await api.SendAsync(new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(Paused)), default))
            Check.Require(raw.StatusCode == HttpStatusCode.TooManyRequests && raw.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(300) &&
                await raw.Content.ReadAsStringAsync() == Body(DropboxProtocol.TooManyRequests, 300) && raw.RequestMessage is not null,
                "raw local reply differs from the real 429");

        // Other routes keep sending.
        var other = Call(api, Other);
        Check.Require(await wire.Settled(other, 3) == 3, "an unpaused route was held");
        await wire.Reply(2, Ok());
        await other;

        // Local replies never extend the pause: callers wait exactly the remaining time (only the wire copies and the log
        // round up to whole seconds) and the pause ends at the stated time.
        clock.Advance(TimeSpan.FromSeconds(100));
        Same(real, await Refused(Call(api, Paused)), TimeSpan.FromSeconds(200), "remaining");
        clock.Advance(TimeSpan.FromSeconds(199.5));
        Same(real, await Refused(Call(api, Paused)), TimeSpan.FromSeconds(0.5), "exact remainder");
        clock.Advance(TimeSpan.FromSeconds(0.5));
        var after = Call(api, Paused);
        Check.Require(await wire.Settled(after, 4) == 4, "the pause outlived its stated end");
        await wire.Reply(3, Ok());
        await after;
    }

    private static async Task LaterShorterAsync()
    {
        var (wire, _, api) = Create();
        var first = Call(api, Paused);
        await wire.Arrivals(1);
        var second = Call(api, Paused);
        await wire.Reply(0, Refusal(DropboxProtocol.TooManyRequests, 300));
        var real = await Refused(first);
        await wire.Reply(1, Refusal(DropboxProtocol.TooManyRequests, 10));
        await Refused(second);
        Same(real, await Refused(Call(api, Paused)), TimeSpan.FromSeconds(300), "later shorter");
    }

    // Write contention never pauses, whether its wait is in the header and body or the header alone (summary-only body).
    private static async Task WriteContentionOpensNoPauseAsync()
    {
        var (wire, _, api) = Create();
        var summaryOnly = Refusal(DropboxProtocol.TooManyWriteOperations, 300);
        summaryOnly.Content = new StringContent($$"""{"error_summary":"{{DropboxProtocol.TooManyWriteOperations}}/.."}""");
        // Contradictory: the summary names too_many_requests but the structured reason is write contention.
        var mixed = Refusal(DropboxProtocol.TooManyWriteOperations, 300);
        mixed.Content = new StringContent($$$"""{"error_summary":"{{{DropboxProtocol.TooManyRequests}}}/..","error":{"reason":{".tag":"{{{DropboxProtocol.TooManyWriteOperations}}}"},"retry_after":300}}""");
        var index = 0;
        foreach (var refusal in new[] { Refusal(DropboxProtocol.TooManyWriteOperations, 7), summaryOnly, mixed })
        {
            var first = Call(api, Paused);
            await wire.Reply(index++, refusal);
            Check.Require((await Refused(first)).Cause == TextKey.WaitTooManyWrites, "fixture is not write contention");
        }
        var next = Call(api, Paused);
        Check.Require(await wire.Settled(next, 4) == 4, "too_many_write_operations paused the route");
        await wire.Reply(3, Ok());
        await next;
    }

    // .NET's delta Retry-After holds at most Int32.MaxValue seconds. A longer stated wait (body retry_after, or an HTTP-date
    // header) still gets a local 429 carrying the whole wait, and no second send.
    private static async Task BeyondDeltaHeaderAsync(bool date)
    {
        var (wire, clock, api) = Create();
        var seconds = (long)int.MaxValue + 1;
        const string reason = DropboxProtocol.TooManyRequests;
        var refusal = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(date
            ? $$$$"""{"error_summary":"{{{{reason}}}}/..","error":{"reason":{".tag":"{{{{reason}}}}"}}}""" : Body(reason, seconds)) };
        if (date) refusal.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow() + TimeSpan.FromSeconds(seconds));
        var first = Call(api, Paused);
        await wire.Reply(0, refusal);
        var real = await Refused(first);
        Check.Require(real.ProviderWait == TimeSpan.FromSeconds(seconds), "beyond-delta fixture lost its stated wait");
        var next = Call(api, Paused);
        Check.Require(await wire.Settled(next, 2) == 1, "a pause beyond the delta header reached Dropbox");
        Same(real, await Refused(next), TimeSpan.FromSeconds(seconds), date ? "date beyond delta" : "body beyond delta", seconds);
        using var raw = await api.SendAsync(new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(Paused)), default);
        Check.Require(raw.Headers.RetryAfter is { Delta: null, Date: not null },
            "a wait beyond the delta header did not keep an HTTP-date");
    }

    // A write refused through the dispatcher logs the local provider fact for its unsent member.
    private static async Task DispatcherFactAsync()
    {
        var (wire, _, api) = Create();
        using var store = new InMemoryStateStore();
        var dispatcher = new Dispatcher(api, store);
        Task Submit() => dispatcher.SubmitAsync("pair", DropboxProtocol.Delete, new JobMember("id:selected", "item", null), "rev", false, null, default);
        var first = Submit();
        await wire.Reply(0, Refusal(DropboxProtocol.TooManyRequests, 300));
        var real = await Refused(first);
        var next = Submit();
        Check.Require(await wire.Settled(next, 2) == 1, "a paused delete reached Dropbox");
        Same(real, await Refused(next), TimeSpan.FromSeconds(300), "dispatcher", 300, DropboxProtocol.Delete);
    }

    // A session slice refused inside its route's pause logs the local provider fact, like every other caller.
    private static async Task StagingFactAsync()
    {
        var (wire, _, api) = Create();
        var source = Scenarios.ItemChecks.File(Side.Local, "item", 42, "source");
        Task Stage() { var bytes = new MemoryStream([42]); return new UploadContent().StageAsync(api, source.Path, bytes, source, source with { IsFolder = true }, default); }
        var first = Stage();
        await wire.Reply(0, Refusal(DropboxProtocol.TooManyRequests, 300));
        var real = await Refused(first);
        var next = Stage();
        Check.Require(await wire.Settled(next, 2) == 1, "a paused session slice reached Dropbox");
        Same(real, await Refused(next), TimeSpan.FromSeconds(300), "staging", 300, DropboxProtocol.UploadSessionStart);
    }

    // A 429 that names no reason is not confirmed to bind its route: Dropbox kept answering get_metadata inside such
    // 300 s waits. Its caller gets the stated wait and logs "429 -"; every other caller still reaches Dropbox.
    private static async Task ReasonlessDoesNotPauseAsync(string body)
    {
        var (wire, _, api) = Create();
        var reasonless = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) };
        reasonless.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
        var first = Call(api, Paused);
        await wire.Reply(0, reasonless);
        var real = await Refused(first);
        Check.Require(real.Disposition == FailureDisposition.TemporaryNoEffect && real.ProviderWait == TimeSpan.FromSeconds(300) &&
            real.Provider == DropboxErrors.Fact(Paused, HttpStatusCode.TooManyRequests, null),
            "a reasonless 429 lost its caller's wait or its provider fact");
        var next = Call(api, Paused);
        Check.Require(await wire.Settled(next, 2) == 2, "a reasonless 429 paused other callers of its route");
        await wire.Reply(1, Ok());
        await next;
    }

    // Either record of the reason Dropbox gives (error_summary tag or error.reason tag) confirms the route pause.
    private static async Task ReasonedPausesAsync(string body)
    {
        var (wire, _, api) = Create();
        var reasoned = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) };
        reasoned.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
        var first = Call(api, Paused);
        await wire.Reply(0, reasoned);
        await Refused(first);
        var next = Call(api, Paused);
        Check.Require(await wire.Settled(next, 2) == 1, "a 429 naming too_many_requests left its route unpaused");
        Check.Require((await Refused(next)).ProviderWait == TimeSpan.FromSeconds(300), "a reasoned pause lost its stated wait");
    }

    // A call that passed the first check and queued for a slot rechecks after admission and releases the slot unsent.
    private static async Task PausedWhileQueuedAsync()
    {
        var (wire, _, api) = Create();
        var holder = Call(api, Other);
        await wire.Arrivals(1);
        var refused = Call(api, Paused);
        await wire.Arrivals(2);
        var next = Call(api, Other); // queued first
        var queued = Call(api, Paused);
        await wire.Reply(1, Refusal(DropboxProtocol.TooManyRequests, 300)); // the busy period also halves the level to one
        var real = await Refused(refused);
        await wire.Reply(0, Ok());
        await holder;
        await wire.Reply(2, Ok());
        await next;
        Same(real, await Refused(queued), TimeSpan.FromSeconds(300), "queued");
        Check.Require(wire.Count == 3, "a queued call reached Dropbox inside the pause");
    }

    // Longpoll carries the same route pause; its callers already handle a 429. Its refusal read needs a resettable timer.
    private static async Task LongpollAsync()
    {
        var (wire, _, api) = Create(TimeProvider.System);
        var first = api.LongpollAsync("a", default);
        await wire.Arrivals(1);
        var second = api.LongpollAsync("b", default);
        await wire.Arrivals(2);
        await wire.Reply(0, Refusal(DropboxProtocol.TooManyRequests, 60));
        var real = await Refused(first);
        var third = api.LongpollAsync("c", default);
        Check.Require(await wire.Settled(third, 3) == 2, "a paused longpoll reached Dropbox");
        // The system clock moves, so the local wait is whatever remains of the stated 60 s, never more.
        var local = await Refused(third);
        Check.Require(local.ProviderWait is { } left && left > TimeSpan.Zero && left <= TimeSpan.FromSeconds(60), "a paused longpoll outlived its stated wait");
        Same(real, local, local.ProviderWait!.Value, "longpoll", 60, DropboxProtocol.ListFolderLongpoll);
        await wire.Reply(1, Ok("""{"changes":false}"""));
        await second;
    }

    private sealed class HeldWire : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<HttpResponseMessage>> _calls = [];
        private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Count { get { lock (_gate) return _calls.Count; } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource arrived;
            lock (_gate) { _calls.Add(reply); arrived = _arrived; _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            arrived.TrySetResult();
            return reply.Task.WaitAsync(ct);
        }

        private Task Next() { lock (_gate) return _arrived.Task; }

        internal async Task Arrivals(int count)
        {
            while (true)
            {
                var next = Next();
                if (Count >= count) return;
                await next;
            }
        }

        // The wire count once the call either made it <paramref name="sent"/> or finished.
        internal async Task<int> Settled(Task call, int sent)
        {
            while (true)
            {
                var next = Next();
                if (Count >= sent || call.IsCompleted) return Count;
                await Task.WhenAny(next, call);
            }
        }

        internal async Task Reply(int index, HttpResponseMessage response)
        {
            await Arrivals(index + 1);
            TaskCompletionSource<HttpResponseMessage> call;
            lock (_gate) call = _calls[index];
            call.SetResult(response);
        }
    }
}
