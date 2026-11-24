using JobStore = Massdex.SyncShark.Engine.Storage.Store;
using StorageSchema = Massdex.SyncShark.Engine.Storage.Schema;
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
        var failures = new List<string>();
        try
        {
            var same = await side.ReadChangesAsync("cursor", null, ("cursor", null), default);
            if (!same.CaughtUp || same.ContinuityLost || same.Changes.Count != 0 || same.Cursor != "cursor")
                failures.Add("Equal boundaries must finish without a read");
        }
        catch (Exception e) { failures.Add($"Equal boundaries must finish without a read ({e.GetType().Name})"); }
        var missingRevision = ItemChecks.File(Side.Dropbox, "item", 1, "id:selected") with { Revision = "" };
        try
        {
            await side.DeleteAsync(missingRevision, null, ActionKind.ReplaceKind, default);
            failures.Add("Origin-aware delete bypassed selected revision validation");
        }
        catch (ChangedUnderneathException e) when (e.Path == missingRevision.Path) { }
        catch (Exception e) { failures.Add($"Origin-aware delete bypassed selected revision validation ({e.GetType().Name})"); }
        Check.Require(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>Actual pending launch retains the origin supplied at each selected ordinal before Pending is released.</summary>
    public static async Task DeleteOriginsAsync()
    {
        var pair = ItemChecks.Pair();
        ActionKind?[] origins = [ActionKind.DeleteDropbox, ActionKind.ReplaceKind, ActionKind.MoveDropbox, null];
        var entries = origins.Select((_, i) => ItemChecks.File(Side.Dropbox, $"item-{i}", 1, $"id:item-{i}")).ToArray();
        var wireIds = new List<string>();
        using var http = new HttpClient(new ReplyWire(async (request, ct) =>
        {
            Check.Require(request.RequestUri!.AbsolutePath.EndsWith("/files/delete_batch", StringComparison.Ordinal), "Expected one batch for ready members");
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
            wireIds.AddRange(body.RootElement.GetProperty("entries").EnumerateArray().Select(m => m.GetProperty("path").GetString()!));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{".tag":"async_job_id","async_job_id":"token"}""") };
        }));
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "fixture.sqlite");
        var failures = new List<string>();
        try
        {
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                db.Open(); db.CreateFunction<string,string>("pathkey", PathNames.Key, isDeterministic: true);
                using var schema = db.CreateCommand();
                schema.CommandText = StorageSchema.Create + $"; PRAGMA user_version={StorageSchema.Version};";
                schema.ExecuteNonQuery();
            }
            using (var store = new JobStore(path, false, ItemChecks.Settings))
            {
                await store.SavePairAsync(pair, default);
                var api = BoundApi(http, TimeProvider.System);
                ISide side = new DropboxSide(pair, () => throw new InvalidOperationException(), api, new Dispatcher(api, store));
                var ready = new ConcurrentExclusiveSchedulerPair();
                var removed = await Task.Factory.StartNew(() => Task.WhenAll(entries.Select((entry,i) => origins[i] is { } origin
                    ? side.DeleteAsync(entry, null, origin, default) : side.DeleteAsync(entry, null, default))),
                    default, TaskCreationOptions.None, ready.ExclusiveScheduler).Unwrap();
                ready.Complete();
                if (!wireIds.SequenceEqual(entries.Select(e => e.Id)) || removed.Any(x => x.Outcome != RemovalOutcome.Pending || x.Accepted?.JobId != "token"))
                    failures.Add("Delete overload bypassed submission or did not return Pending for every selected ID");
            }
            using var reopened = new JobStore(path, true, ItemChecks.Settings);
            var jobs = new List<AcceptedJob>(); await foreach (var job in reopened.JobsAsync(pair.PairId, default)) jobs.Add(job);
            Check.Require(jobs.Count == 1 && jobs[0].JobId == "token" && jobs[0].Launch.Select(m => m.Id).SequenceEqual(wireIds),
                "Restart lost the accepted token or changed its submitted ID order");
            if (jobs[0].Launch.Any(member => member.Origin != origins[Array.FindIndex(entries, e => e.Id == member.Id)]))
                failures.Add("Reopened accepted job lost the originating action of a submitted delete");
            Check.Require(failures.Count == 0, string.Join("; ", failures));
        }
        finally { dir.Delete(true); }
    }

    /// <summary>Account-root identity requires exactly one successful response decoded by the shared provider shape.</summary>
    public static async Task AccountRootAsync()
    {
        await RecoveredFileAsync();
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

    // All replies complete inline: a pending probe is waiting for the unread recovery's permit.
    private static async Task RecoveredFileAsync()
    {
        var pair = ItemChecks.Pair();
        pair = pair with { Roots = new(pair.LocalRoot, pair.DropboxRoot, pair.ConnectionId, new(1, 1, null), new("acct", "root", "home", "folder")) };
        var pin = ItemChecks.File(Side.Dropbox, "item", 1, "id:item");
        var actual = new IdentityLocation("root", pair.DropboxRoot + "/item", pin);
        UploadContentChecks.Sequential? downloaded = null;
        using var http = new HttpClient(new ReplyWire((req, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = req.RequestUri!.AbsolutePath.EndsWith("/files/download", StringComparison.Ordinal)
                ? new StreamContent(downloaded = new UploadContentChecks.Sequential([1])) : new StringContent("{}")
        })));
        var api = new DropboxApi(http, TimeProvider.System, ItemChecks.Settings with { InitialLimiterLevel = 1 }, _ => { },
            _ => ValueTask.FromResult<string?>("test-token"), "root");
        var side = new DropboxSide(pair, () => throw new InvalidOperationException(), api, null!);
        var recovery = (Recovery.File)(await side.RecoverAsync(actual, pin, default))!;
        await using var bytes = recovery.Bytes;
        Check.Require(downloaded?.LargestRead == EngineSettings.StreamCopyBufferBytes, "Recovery did not request a stream-sized read");
        using var cancel = new CancellationTokenSource();
        var probe = api.CallJsonAsync("users/get_current_account", "null", cancel.Token);
        try
        {
            Check.Require(probe.IsCompleted, "Unread recovered file held the Dropbox request permit");
            using var reply = await probe;
        }
        finally { cancel.Cancel(); }
        Check.Require(bytes.CanSeek && bytes.Position == 0 && bytes.ReadByte() == 1 && bytes.ReadByte() == -1, "Recovered pin was not rewound with its bytes");
        await MissingSpoolAsync(pair, actual, pin, false);
        await MissingSpoolAsync(pair, actual, pin, true);
        try
        {
            if (await side.RecoverAsync(actual, pin with { ContentHash = new string('0', pin.ContentHash.Length) }, default) is Recovery.File bad)
                await bad.Bytes.DisposeAsync();
        }
        catch (ItemFailedException e) when (e.Cause == TextKey.DownloadHashMismatch && e.Path == pin.Path) { return; }
        throw new InvalidDataException("Recovery accepted bytes with the wrong pinned hash");
    }

    private static async Task MissingSpoolAsync(PairConfig pair, IdentityLocation actual, Entry pin, bool failClose)
    {
        var scratch = Directory.CreateTempSubdirectory();
        var missing = Path.Combine(scratch.FullName, "missing");
        var tmp = Environment.GetEnvironmentVariable("TMP");
        var temp = Environment.GetEnvironmentVariable("TEMP");
        try
        {
            using var backing = new MemoryStream();
            var source = new BufferedStream(backing);
            if (failClose)
            {
                source.WriteByte(1); // Pending output makes disposal flush to the now-closed backing stream.
                backing.Dispose();
            }
            using var http = new HttpClient(new ReplyWire((req, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = req.RequestUri!.AbsolutePath.EndsWith("/files/download", StringComparison.Ordinal)
                    ? new StreamContent(source) : new StringContent("{}")
            })));
            var api = new DropboxApi(http, TimeProvider.System, ItemChecks.Settings with { InitialLimiterLevel = 1 }, _ => { },
                _ => ValueTask.FromResult<string?>("test-token"), "root");
            ISide side = new DropboxSide(pair, () => throw new InvalidOperationException(), api, null!);
            Environment.SetEnvironmentVariable("TMP", missing);
            Environment.SetEnvironmentVariable("TEMP", missing);
            Exception? failure = null;
            try { if (await side.RecoverAsync(actual, pin, default) is Recovery.File file) await file.Bytes.DisposeAsync(); }
            catch (Exception e) when (e is ItemFailedException or IOException or ObjectDisposedException) { failure = e; }
            bool classified = failure is ItemFailedException { InnerException: DirectoryNotFoundException io } item &&
                item.Path == pin.Path && item.Cause == Massdex.SyncShark.Engine.Local.LocalErrors.Cause(io, TextKey.DownloadIncomplete);
            if (failClose) Check.Require(classified, "Source disposal masked the classified recovery spool failure");
            else Check.Require(classified, "Missing recovery spool did not report the classified item failure");
            Check.Require(!scratch.EnumerateFileSystemInfos().Any(), "Failed recovery left a spool behind");
            using var cancel = new CancellationTokenSource();
            var probe = api.CallJsonAsync("users/get_current_account", "null", cancel.Token);
            try { Check.Require(probe.IsCompleted, "Failed recovery held the Dropbox request permit"); using var reply = await probe; }
            finally { cancel.Cancel(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", temp);
            Environment.SetEnvironmentVariable("TMP", tmp);
            scratch.Delete(true);
        }
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
