using System.Net;
using System.Text;
using Massdex.SyncShark;

// A Dropbox read whose response body stalls half-open must not hang forever: the idle timeout
// (ReadStallTimeout) aborts the body read as a transient interruption, which the shared read retry
// (ReadWithRetryAsync) retries within its budget, and the caller's own cancellation still wins.
internal static class ReadStallChecks
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMilliseconds(200);

    internal static async Task RunAsync()
    {
        await RetriesAfterOneStallAsync();
        await FailsWithinBudgetWhenAlwaysStallingAsync();
        await PropagatesCallerCancellationAsync();
        await CreateOnlyRejectionStallIsUncertainAsync();
        Console.WriteLine("PASS read stall: retried after one stalled body, budget-bounded failure, caller cancellation, " +
            "stalled create-only rejection classified uncertain");
    }

    // A body that stalls once, then succeeds: the stalled read is retried and the listing completes.
    private static async Task RetriesAfterOneStallAsync()
    {
        using var wire = new StallWire(stallBodies: 1);
        using var http = new HttpClient(wire);
        var client = Client(http, TimeSpan.FromSeconds(30));
        var entries = await client.ListEntriesAsync(CancellationToken.None);
        Check(entries.Count == 0, "the listing completed after one stalled body");
        Check(wire.Stalls == 1, $"exactly one body stalled ({wire.Stalls})");
        Check(wire.Listings >= 2, $"the listing was issued again ({wire.Listings})");
    }

    // A body that never sends bytes fails within the read budget with a clear reason, never a hang.
    private static async Task FailsWithinBudgetWhenAlwaysStallingAsync()
    {
        using var wire = new StallWire(stallBodies: int.MaxValue);
        using var http = new HttpClient(wire);
        var client = Client(http, TimeSpan.FromSeconds(2));
        var started = TimeProvider.System.GetTimestamp();
        Exception? failure = null;
        try { await client.ListEntriesAsync(CancellationToken.None); }
        catch (Exception error) { failure = error; }
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        Check(failure is not null, "the always-stalling read failed instead of hanging");
        Check(failure!.Message.Contains("budget exhausted", StringComparison.OrdinalIgnoreCase),
            $"the failure states a clear reason ({failure.Message})");
        Check(elapsed < TimeSpan.FromSeconds(30), $"it failed promptly ({elapsed.TotalSeconds:N1}s)");
    }

    // The caller's own cancellation during a stall propagates as cancellation, never as a stall timeout.
    private static async Task PropagatesCallerCancellationAsync()
    {
        using var wire = new StallWire(stallBodies: int.MaxValue);
        using var http = new HttpClient(wire);
        var client = Client(http, TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Exception? failure = null;
        try { await client.ListEntriesAsync(cancel.Token); }
        catch (Exception error) { failure = error; }
        Check(failure is OperationCanceledException,
            $"caller cancellation propagates ({failure?.GetType().Name}: {failure?.Message})");
    }

    // A create-only upload rejection whose body stalls half-open is never read and never proves no effect:
    // it is classified as an unproven (uncertain) effect and the upload is never retried.
    private static async Task CreateOnlyRejectionStallIsUncertainAsync()
    {
        using var wire = new CreateOnlyRejectionStallWire();
        using var http = new HttpClient(wire);
        var client = Client(http, TimeSpan.FromSeconds(30));
        var payload = Encoding.UTF8.GetBytes("exact-file-upload-payload");
        using var source = new MemoryStream(payload);
        var dispatched = false;
        Exception? failure = null;
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            await client.CommitCreateOnlyExactFileAsync("report.csv", source, DropboxContentHash.Compute(payload),
                () => dispatched = true, CancellationToken.None);
        }
        catch (Exception error) { failure = error; }
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        Check(failure is DropboxEffectUncertainException,
            $"a stalled create-only rejection body is classified uncertain ({failure?.GetType().Name})");
        Check(dispatched, "the create-only effect was dispatched before its rejection body stalled");
        Check(wire.Uploads == 1, $"the create-only upload was transmitted exactly once and never retried ({wire.Uploads})");
        Check(elapsed < TimeSpan.FromSeconds(15), $"the stalled rejection failed promptly ({elapsed.TotalSeconds:N1}s)");
    }

    private static DropboxPilotClient Client(HttpClient http, TimeSpan budget) =>
        new(http, new Tokens(), "/Pilot", Binding(), "id:pilot-folder")
        {
            ReadTransientBudget = budget,
            ReadStallTimeout = StallTimeout,
            // Immediate retries: the budget is measured in real time, so a stall-only outage stays bounded by it.
            ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };

    // A Dropbox endpoint whose listing bodies stall (never send a byte) for the first `stallBodies` calls.
    private sealed class StallWire(int stallBodies) : HttpMessageHandler
    {
        private int _remaining = stallBodies;
        internal int Stalls;
        internal int Listings;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("get_current_account", StringComparison.Ordinal))
                return Reply("{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}");
            if (path.EndsWith("get_metadata", StringComparison.Ordinal))
                return Reply("{\".tag\":\"folder\",\"id\":\"id:pilot-folder\",\"name\":\"Pilot\",\"path_lower\":\"/pilot\",\"path_display\":\"/Pilot\"}");
            if (path.EndsWith("get_latest_cursor", StringComparison.Ordinal))
                return Reply("{\"cursor\":\"cursor-initial\"}");
            if (path.EndsWith("list_folder", StringComparison.Ordinal) ||
                path.EndsWith("/list_folder/continue", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Listings);
                if (Interlocked.Decrement(ref _remaining) >= 0)
                {
                    Interlocked.Increment(ref Stalls);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StreamContent(new StallingBody()) });
                }
                return Reply("{\"entries\":[],\"cursor\":\"cursor-end\",\"has_more\":false}");
            }
            return Reply("{\"entries\":[],\"cursor\":\"cursor-end\",\"has_more\":false}");
        }

        private static Task<HttpResponseMessage> Reply(string body) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    // A Dropbox endpoint that accepts the create-only upload request and answers with a rejection whose
    // body never sends a byte.
    private sealed class CreateOnlyRejectionStallWire : HttpMessageHandler
    {
        internal int Uploads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("get_current_account", StringComparison.Ordinal))
                return Reply("{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}");
            if (path.EndsWith("/files/upload", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Uploads);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                    { Content = new StreamContent(new StallingBody()) });
            }
            return Reply("{\"entries\":[],\"cursor\":\"cursor-end\",\"has_more\":false}");
        }

        private static Task<HttpResponseMessage> Reply(string body) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    // A response body that never sends bytes; it completes only when its read is cancelled.
    private sealed class StallingBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class Tokens : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("synthetic-token");
    }

    private static DropboxConnectionBinding Binding() => new(DropboxProviderConfiguration.RegisteredClientId,
        "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
        DropboxProviderConfiguration.RequiredScopes);

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
