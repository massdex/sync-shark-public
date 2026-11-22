using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks the side's staging selection; the upload wire is covered by the A08 scenario.</summary>
public static class DropboxSideChecks
{
    public static async Task RunAsync()
    {
        var pair = ItemChecks.Pair();
        var file = ItemChecks.File(Side.Local, "item", 42, "source");
        var folder = ItemChecks.File(Side.Dropbox, file.Path, 0, "folder", true);
        using var bytes = new MemoryStream([42]);
        // An opaque marker: this check only verifies which configured provider reaches the stage owner.
        var api = (DropboxApi)RuntimeHelpers.GetUninitializedObject(typeof(DropboxApi));
        ISide side = new DropboxSide(pair, () => throw new InvalidOperationException(), api, null!,
            (configured, path, input, source, victim, ct) =>
            {
                Check.Require(configured == api && input == bytes && source == file && path == file.Path, "Staged a different source or account");
                return Task.FromResult(victim == folder ? DropboxStaged.Session(path, "session", source.Size, source, source.ContentHash) : DropboxStaged.Single(path, input, source));
            });
        await using var stage = await side.StageAsync(file.Path, bytes, file, folder, default);
        Check.Require(stage is DropboxStaged { SessionId: "session", Content: null } && !stage.ConsumesSourceOnPublish,
            "The selected folder could be removed before its one-byte replacement was in a closed session");
        await using var ordinary = await side.StageAsync(file.Path, bytes, file, null, default);
        Check.Require(ordinary.ConsumesSourceOnPublish, "Invented a folder victim for a free target");
        var same = await side.ReadChangesAsync("cursor", null, ("cursor", null), default);
        Check.Require(same.CaughtUp && !same.ContinuityLost && same.Changes.Count == 0, "Equal boundaries must finish without a read");
        Check.Require(CallsIn(nameof(DropboxSide), "PageAsync").Contains("listing.ReadChangePageAsync"),
            "Finite catch-up must read a single continuation, never wait for future changes");
        var rootCalls = CallsIn(nameof(DropboxSide), nameof(ISide.RootAsync));
        Check.Require(rootCalls.Contains("DropboxReplyShapes.AccountRoot") && !rootCalls.Any(c => c.EndsWith(".GetProperty", StringComparison.Ordinal)),
            "RootAsync bypassed the typed account-root decoder or parsed its provider fields a second time");
        RequireReadFailureHandoff(rootCalls);
        var jobCalls = CallsIn(nameof(DropboxSide), nameof(ISide.ReadJobAsync));
        Check.Require(jobCalls.Contains("Dispatcher.MemberReply") && jobCalls.Contains("dispatcher.ResolveCreateClashAsync"),
            "Deferred create did not use the selected ordinal and the same clash resolver as an immediate create");
    }

    internal static void RequireReadFailureHandoff(string[] calls)
    {
        Check.Require(calls.Any(c => c.EndsWith("ReadFailureAsync", StringComparison.Ordinal)),
            "A provider throttle must pass transient evidence and its retry-after through the shared read failure path");
    }

    // The sibling decoder/transport is a stub on a writer lane. This checks W13 ownership only.
    internal static string[] CallsIn(string type, string method)
    {
        var root = CheckArguments.Parse(Environment.GetCommandLineArgs()[1..]).Root;
        var file = Path.Combine(root, "src", "SyncShark.Engine", "Dropbox", type + ".cs");
        var body = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == method);
        return body.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(call => call.Expression.ToString()).ToArray();
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
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"entries":[],"cursor":"after","has_more":false}""") };
        }));
        var side = new DropboxSide(ItemChecks.Pair(), () => throw new InvalidOperationException(), BoundApi(http,TimeProvider.System), null!);
        var page = await side.ReadChangesAsync("saved", null, ("capture", null), default);
        Check.Require(calls == 1 && page.CaughtUp && page.Cursor == "after" && !page.ContinuityLost, "Quiet finite page did not settle");
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
