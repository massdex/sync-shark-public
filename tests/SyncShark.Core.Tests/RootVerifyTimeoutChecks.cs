using System.Net;
using System.Text;
using Massdex.SyncShark;

// A first-sync prepare supplies no deadline and no ambient verification scope, so
// DropboxProvider.VerifyRootIdentityAsync bounds each account/root verification attempt with
// Task.WaitAsync(RootVerifyTimeout, ct) and retries. A half-open verification that never answers -
// and never observes cancellation - must not hold a listing open forever; a definite mismatch or
// refusal from a completed attempt must still propagate; the caller's own cancellation must still
// surface as cancellation; and a wedged retry notice must not stall recovery.
internal static class RootVerifyTimeoutChecks
{
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan NoticeTimeout = TimeSpan.FromMilliseconds(200);

    internal static async Task RunAsync()
    {
        await RetriesAfterOneHangAsync();
        await NeverAnswersTimesOutAfterThreeAttemptsAsync();
        await IgnoresCancellationCompletelyAsync();
        await PropagatesCallerCancellationAsync();
        await RootHangRetriesWithFreshRequestAsync();
        await MismatchPropagatesWithoutRetryAsync();
        await DefiniteRefusalPropagatesWithoutRetryAsync();
        await HangingRetryNoticeDoesNotStallRecoveryAsync();
        Console.WriteLine("PASS root verify timeout: a hung verification is retried with a fresh request, a " +
            "verification that never answers (even ignoring cancellation) fails after three bounded attempts, " +
            "caller cancellation still wins, a completed mismatch or refusal is never retried, a wedged retry " +
            "notice does not stall recovery, and the root read is bounded too");
    }

    // An account verification that hangs once, then answers: the timed-out attempt is abandoned and the
    // retry issues a brand-new account request that succeeds.
    private static async Task RetriesAfterOneHangAsync()
    {
        using var wire = new HangWire(hangAccounts: 1);
        using var http = new HttpClient(wire);
        var client = Client(http);
        await client.VerifyRootIdentityAsync();
        Check(wire.AccountCalls == 2, $"the hung verification was retried with a fresh account request ({wire.AccountCalls})");
        Check(wire.RootCalls == 1, $"the completed retry performed the root read exactly once ({wire.RootCalls})");
    }

    // An account verification that never answers: bounded attempts fail with a clear timeout, never a hang,
    // and every attempt issued a new request rather than reusing the stuck one.
    private static async Task NeverAnswersTimesOutAfterThreeAttemptsAsync()
    {
        using var wire = new HangWire(hangAccounts: int.MaxValue);
        using var http = new HttpClient(wire);
        var client = Client(http);
        Exception? failure = null;
        var started = TimeProvider.System.GetTimestamp();
        try { await client.VerifyRootIdentityAsync(); }
        catch (Exception error) { failure = error; }
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        Check(failure is TimeoutException,
            $"an always-hanging verification fails with a clear timeout ({failure?.GetType().Name}: {failure?.Message})");
        Check(failure!.Message.Contains("did not answer within", StringComparison.Ordinal),
            "the failure states the account/root identity check did not answer");
        Check(wire.AccountCalls == 3, $"three fresh attempts were made, never a hang ({wire.AccountCalls})");
        Check(elapsed < TimeSpan.FromSeconds(10), $"it failed promptly ({elapsed.TotalSeconds:N1}s)");
    }

    // The strongest case: each attempt awaits a task created with no token at all, so the inner
    // verification ignores cancellation completely. The WaitAsync bound must still fire, giving three
    // bounded attempts and a clear timeout rather than a hang.
    private static async Task IgnoresCancellationCompletelyAsync()
    {
        using var wire = new HangWire(hangAccounts: int.MaxValue, hangIgnoresCancellation: true);
        using var http = new HttpClient(wire);
        var client = Client(http);
        Exception? failure = null;
        var started = TimeProvider.System.GetTimestamp();
        try { await client.VerifyRootIdentityAsync(); }
        catch (Exception error) { failure = error; }
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        Check(failure is TimeoutException,
            $"a verification that ignores cancellation still times out ({failure?.GetType().Name}: {failure?.Message})");
        Check(wire.AccountCalls == 3, $"three bounded attempts were made ({wire.AccountCalls})");
        Check(wire.HangsCompleted == 0, "the abandoned, cancellation-ignoring verifications never completed");
        Check(elapsed < TimeSpan.FromSeconds(10), $"it failed promptly ({elapsed.TotalSeconds:N1}s)");
    }

    // The caller's own cancellation during a hang still propagates as cancellation and never triggers a
    // retry: the cancellation check runs before the attempt's own bound is consulted.
    private static async Task PropagatesCallerCancellationAsync()
    {
        using var wire = new HangWire(hangAccounts: int.MaxValue);
        using var http = new HttpClient(wire);
        using var cancel = new CancellationTokenSource();
        var client = Client(http);
        Exception? failure = null;
        var work = client.VerifyRootIdentityAsync(cancel.Token);
        await wire.AccountEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        try { await work; }
        catch (Exception error) { failure = error; }
        Check(failure is OperationCanceledException,
            $"caller cancellation surfaces as cancellation ({failure?.GetType().Name}: {failure?.Message})");
        Check(wire.AccountCalls == 1, $"cancellation did not trigger a retry ({wire.AccountCalls})");
    }

    // The root read itself is bounded too: a hung get_metadata is abandoned and retried with a fresh
    // request. There is no scope to reuse a completed account verdict, so the account is re-checked.
    private static async Task RootHangRetriesWithFreshRequestAsync()
    {
        using var wire = new HangWire(hangRoots: 1);
        using var http = new HttpClient(wire);
        var client = Client(http);
        await client.VerifyRootIdentityAsync();
        Check(wire.RootCalls == 2, $"the hung root read was retried with a fresh metadata request ({wire.RootCalls})");
        Check(wire.AccountCalls == 2, $"each attempt re-checked the account ({wire.AccountCalls})");
    }

    // A definite account mismatch answered by a completed attempt propagates at once and is never retried.
    private static async Task MismatchPropagatesWithoutRetryAsync()
    {
        using var wire = new HangWire
        {
            AccountBody = "{\"account_id\":\"other\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}"
        };
        using var http = new HttpClient(wire);
        var client = Client(http);
        Exception? failure = null;
        try { await client.VerifyRootIdentityAsync(); }
        catch (Exception error) { failure = error; }
        Check(failure is InvalidOperationException,
            $"a completed account mismatch propagates unchanged ({failure?.GetType().Name}: {failure?.Message})");
        Check(wire.AccountCalls == 1, $"a definite mismatch is never retried ({wire.AccountCalls})");
    }

    // A definite HTTP refusal (an ordinary failure, not a timeout) also propagates at once and never
    // retries, and its attempt's linked source is released rather than leaked.
    private static async Task DefiniteRefusalPropagatesWithoutRetryAsync()
    {
        using var wire = new HangWire { AccountStatus = HttpStatusCode.Unauthorized };
        using var http = new HttpClient(wire);
        var client = Client(http);
        Exception? failure = null;
        try { await client.VerifyRootIdentityAsync(); }
        catch (Exception error) { failure = error; }
        Check(failure is DropboxApiException,
            $"a definite account refusal propagates unchanged ({failure?.GetType().Name}: {failure?.Message})");
        Check(wire.AccountCalls == 1, $"a definite refusal is never retried ({wire.AccountCalls})");
    }

    // The retry notice is best-effort: a progress sink that never answers must not stall recovery. The
    // bounded notice lets the retry proceed and the verification still completes.
    private static async Task HangingRetryNoticeDoesNotStallRecoveryAsync()
    {
        using var wire = new HangWire(hangAccounts: 1);
        using var http = new HttpClient(wire);
        var client = Client(http);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Sink(SyncByteProgress progress)
        {
            if (progress.Detail.Contains("Dropbox account check timed out; retrying", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await wedged.Task.ConfigureAwait(false);
            }
        }
        var started = TimeProvider.System.GetTimestamp();
        using (SyncByteProgressStream.Begin("Checking Dropbox account", Sink))
            await client.VerifyRootIdentityAsync();
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        Check(entered.Task.IsCompleted, "the retry notice was reported before recovery continued");
        Check(wire.AccountCalls == 2 && wire.RootCalls == 1,
            $"recovery continued past the wedged notice ({wire.AccountCalls} account, {wire.RootCalls} root)");
        Check(elapsed < TimeSpan.FromSeconds(10), $"the wedged notice did not stall recovery ({elapsed.TotalSeconds:N1}s)");
    }

    private static DropboxPilotClient Client(HttpClient http) =>
        new(http, new Tokens(), "/Pilot", Binding(), "id:pilot-folder")
        {
            RootVerifyTimeout = VerifyTimeout,
            RootVerifyNoticeTimeout = NoticeTimeout
        };

    private static DropboxConnectionBinding Binding() => new(DropboxProviderConfiguration.RegisteredClientId,
        "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
        DropboxProviderConfiguration.RequiredScopes);

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }

    private sealed class Tokens : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("fixture-generation-a");
    }

    // Dropbox endpoints whose first `hangAccounts` account calls and `hangRoots` root calls never answer.
    // By default the hang observes cancellation (a cooperating half-open body); with
    // `hangIgnoresCancellation` it awaits a task created with no token at all and ignores cancellation.
    private sealed class HangWire(int hangAccounts = 0, int hangRoots = 0, bool hangIgnoresCancellation = false)
        : HttpMessageHandler
    {
        private int _remainingAccounts = hangAccounts;
        private int _remainingRoots = hangRoots;
        internal readonly TaskCompletionSource AccountEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? AccountBody;
        internal HttpStatusCode? AccountStatus;
        internal int AccountCalls;
        internal int RootCalls;
        internal int HangsCompleted;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("get_current_account", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref AccountCalls);
                AccountEntered.TrySetResult();
                if (AccountStatus is { } status)
                    return Task.FromResult(new HttpResponseMessage(status)
                        { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
                if (Interlocked.Decrement(ref _remainingAccounts) >= 0)
                    return hangIgnoresCancellation ? HangIgnoringCancellation() : HangObservingCancellation(cancellationToken);
                return Reply(AccountBody ?? "{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}");
            }
            if (path.EndsWith("get_metadata", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref RootCalls);
                if (Interlocked.Decrement(ref _remainingRoots) >= 0)
                    return hangIgnoresCancellation ? HangIgnoringCancellation() : HangObservingCancellation(cancellationToken);
                return Reply("{\".tag\":\"folder\",\"name\":\"Pilot\",\"id\":\"id:pilot-folder\",\"path_lower\":\"/pilot\",\"path_display\":\"/Pilot\"}");
            }
            throw new InvalidOperationException("Unexpected fixture HTTP route: " + path);
        }

        // A response that never completes and was created with no token: it cannot observe cancellation.
        // The continuation records whether it ever completed, proving the abandonment was ignored.
        private Task<HttpResponseMessage> HangIgnoringCancellation()
        {
            var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            _ = never.ContinueWith(_ => Interlocked.Increment(ref HangsCompleted), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return never;
        }

        private static async Task<HttpResponseMessage> HangObservingCancellation(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A hang must not answer.");
        }

        private static Task<HttpResponseMessage> Reply(string body) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
