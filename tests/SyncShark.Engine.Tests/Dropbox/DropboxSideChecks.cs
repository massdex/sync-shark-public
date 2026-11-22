using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks the pair-bound Dropbox adapter through its side contract.</summary>
public static class DropboxSideChecks
{
    public static async Task RunAsync()
    {
        var pair = ItemChecks.Pair();
        ISide side = new DropboxSide(pair, () => throw new InvalidOperationException(), null!, null!);
        var same = await side.ReadChangesAsync("cursor", null, ("cursor", null), default);
        Check.Require(same.CaughtUp && !same.ContinuityLost && same.Changes.Count == 0, "Equal boundaries must finish without a read");
        var file = ItemChecks.File(Side.Dropbox, "item", 1, "id:selected") with { Revision = "" };
        try { await side.DeleteAsync(file, null, ActionKind.ReplaceKind, default); }
        catch (ChangedUnderneathException) { return; }
        catch (NotImplementedException e) { throw new InvalidDataException("The origin-aware delete did not reach selected revision validation", e); }
        throw new InvalidDataException("An origin-aware file delete omitted the selected revision");
    }

    /// <summary>Actual pending launch retains the origin supplied at each selected ordinal before Pending is released.</summary>
    public static async Task DeleteOriginsAsync()
    {
        var pair = ItemChecks.Pair();
        using var http = new HttpClient(new ReplyWire((request, ct) =>
        {
            Check.Require(request.RequestUri!.AbsolutePath.EndsWith("/files/delete_batch", StringComparison.Ordinal), "Expected one batch for ready members");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{".tag":"async_job_id","async_job_id":"token"}""") });
        }));
        using var store = new InMemoryStateStore();
        var api = BoundApi(http, TimeProvider.System);
        ISide side = new DropboxSide(pair, () => throw new InvalidOperationException(), api, new Dispatcher(api, store));
        ActionKind[] origins = [ActionKind.DeleteDropbox, ActionKind.ReplaceKind, ActionKind.MoveDropbox];
        var ready = new ConcurrentExclusiveSchedulerPair();
        var removed = await Task.Factory.StartNew(() => Task.WhenAll(origins.Select((origin,i) =>
            side.DeleteAsync(ItemChecks.File(Side.Dropbox, $"item-{i}", 1, $"id:item-{i}"), null, origin, default))),
            default, TaskCreationOptions.None, ready.ExclusiveScheduler).Unwrap();
        ready.Complete();
        var jobs = new List<AcceptedJob>(); await foreach (var job in store.JobsAsync(pair.PairId, default)) jobs.Add(job);
        var launch = jobs.Single().Launch;
        Check.Require(removed.All(result => result.Outcome == RemovalOutcome.Pending && result.Accepted == jobs[0]) &&
            launch.Count == origins.Length && launch.Select((m,i) => m.Id == $"id:item-{i}" && m.Origin == origins[i]).All(x => x),
            "Accepted launch changed a selected delete's identity, position or originating action");
    }

    /// <summary>Account-root identity requires exactly one successful response decoded by the shared provider shape.</summary>
    public static async Task AccountRootAsync()
    {
        var calls = 0;
        var clock = new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var http = new HttpClient(new ReplyWire((request, ct) =>
        {
            Check.Require(request.RequestUri!.AbsolutePath.EndsWith("/users/get_current_account", StringComparison.Ordinal), "Unexpected root probe");
            calls++;
            if (calls == 1) return Task.FromResult(Throttled(clock.GetUtcNow()));
            if (calls == 2) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
                { Content = new StringContent("""{"error":{"retry_after":23}}""") });
            var body = calls == 3 ? """{"account_id":"acct","root_info":{"root_namespace_id":"root","home_namespace_id":"home"}}""" :
                """{"account_id":"acct","root_info":{"root_namespace_id":"root","home_namespace_id":""}}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        var api = BoundApi(http, clock);
        var side = new DropboxSide(ItemChecks.Pair() with { DropboxRoot = "" }, () => throw new InvalidOperationException(), api, null!);
        await ExpectThrottleAsync(async () => { await side.RootAsync(default); }, clock);
        await ExpectThrottleAsync(async () => { await side.RootAsync(default); }, clock, 23);
        var root = (await side.RootAsync(default)).Dropbox;
        Check.Require(root == new DropboxRootIdentity("acct", "root", "home", "ns:root") && calls == 3, "Namespace root identity or call count changed");
        try { await side.RootAsync(default); }
        catch (System.Text.Json.JsonException) when (calls == 4) { return; }
        throw new InvalidDataException("Accepted an account with missing home namespace");
    }

    /// <summary>F1: a quiet continuation covers a different opaque capture; fetching it must not longpoll.</summary>
    public static async Task FinitePageAsync()
    {
        var calls = 0;
        using var http = new HttpClient(new ReplyWire(async (req, ct) =>
        {
            Check.Require(++calls == 1 && req.RequestUri!.AbsolutePath.EndsWith("/files/list_folder/continue", StringComparison.Ordinal),
                "Finite catch-up attempted to longpoll or made a second request");
            using var args = System.Text.Json.JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
            Check.Require(args.RootElement.GetProperty("cursor").GetString() == "saved", "Discarded saved cursor");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"entries":[],"cursor":"saved","has_more":false}""") };
        }));
        var side = new DropboxSide(ItemChecks.Pair(), () => throw new InvalidOperationException(), BoundApi(http,TimeProvider.System), null!);
        var page = await side.ReadChangesAsync("saved", null, ("capture", null), default);
        Check.Require(calls == 1 && page.CaughtUp && page.Cursor == "saved" && !page.ContinuityLost, "Quiet finite page did not settle");
    }

    internal static HttpResponseMessage Throttled(DateTimeOffset? at = null)
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
        response.Headers.RetryAfter = at is { } when ? new(when.AddSeconds(17)) : new(TimeSpan.FromSeconds(17));
        return response;
    }

    internal static async Task ExpectThrottleAsync(Func<Task> call, TimeProvider clock, int seconds = 17)
    {
        try { await call(); }
        catch (ItemFailedException error) when (error.Disposition == FailureDisposition.TemporaryNoEffect &&
            error.ProviderWait == TimeSpan.FromSeconds(seconds) && error.ProviderWaitStarted == clock.GetTimestamp()) { return; }
        throw new InvalidDataException("Read did not preserve the HTTP 429 disposition and provider wait");
    }

    internal static DropboxApi BoundApi(HttpClient http, TimeProvider clock) => new(http, clock, ItemChecks.Settings, _ => { },
        _ => ValueTask.FromResult<string?>("test-token"), "root");
    internal sealed class ReplyWire(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request, ct);
    }
}
