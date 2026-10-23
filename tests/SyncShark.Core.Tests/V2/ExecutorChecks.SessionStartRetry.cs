using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task UploadSessionStart_BoundedRetryAsync()
    {
        await UploadStartOutcomeAsync("throttle", succeed: true);
        await UploadStartOutcomeAsync("lost", succeed: true);
        await UploadStartOutcomeAsync("malformed", succeed: true);
        await UploadStartOutcomeAsync("identity", succeed: true);
        await UploadStartOutcomeAsync("timeout", succeed: true);
        await UploadStartOutcomeAsync("http", succeed: false);
        await UploadStartOutcomeAsync("lost", succeed: false);
        await UploadStartOutcomeAsync("malformed", succeed: false);
        await UploadStartOutcomeAsync("rejected", succeed: false);
        // A zero configured retry count still disables transport retries; the 5xx path keeps its own ceiling.
        await UploadStartOutcomeAsync("lost", succeed: false, retries: 0);
        await UploadStartOutcomeAsync("long-backoff", succeed: false);
        await UploadSessionStart_ServerErrorScheduleAsync();
        await UploadStartCancellationAsync();
        await UploadStartAccessRefusalAsync();
    }

    private static async Task UploadStartOutcomeAsync(string fault, bool succeed, int retries = 2)
    {
        var source = new FakeEndpoint(Side.A, "ra");
        var action = Act(ActionKind.CopyNew, Side.B, "f", source: source.Seed("f", new string('x', 16)), key: "k");
        var retryAfter = TimeSpan.FromMilliseconds(80);
        var lastFault = TimeSpan.Zero;
        var retryAt = TimeSpan.Zero;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var wire = new UploadBatchWire(action.Source!.Hash!);
        wire.StartResponse = (attempt, _) =>
        {
            if (succeed && attempt > 1)
            {
                retryAt = clock.Elapsed;
                return Task.FromResult<HttpResponseMessage?>(null);
            }
            lastFault = clock.Elapsed;
            if (fault == "lost") throw new HttpRequestException("scripted session-start connection lost");
            if (fault == "timeout") throw new TaskCanceledException("scripted session-start transport timeout");
            var response = new HttpResponseMessage(fault switch
            {
                "throttle" or "long-backoff" => HttpStatusCode.TooManyRequests,
                "http" => HttpStatusCode.ServiceUnavailable,
                "rejected" => HttpStatusCode.Conflict,
                _ => HttpStatusCode.OK
            }) { Content = new StringContent(fault == "identity" ? "{}" : "not-json", Encoding.UTF8) };
            if (fault is "throttle" or "long-backoff")
                response.Headers.RetryAfter = new RetryConditionHeaderValue(fault == "long-backoff" ? TimeSpan.FromDays(60) : retryAfter);
            return Task.FromResult<HttpResponseMessage?>(response);
        };
        using var http = new HttpClient(wire);
        var client = UploadSafetyClient(http, new FixedTokenSource());
        // Server-error retries use their own fixed schedule; capture the waits instead of sleeping through them.
        var delays = new List<TimeSpan>();
        if (fault == "http")
            client.SessionStartDelay = (delay, _) => { delays.Add(delay); return Task.CompletedTask; };
        var settings = Settings(1) with { UploadBatchSize = 1, EnableRunInstrumentation = true,
            DropboxSessionStartRetryCount = retries, DropboxSessionStartRetryBaseDelayMs = fault == "lost" ? 20 : 0 };
        using var metrics = RunInstrumentation.Begin(settings, TimeProvider.System)!;
        using var store = new InMemoryStateStore(); store.SavePair(Pair());
        var writer = new DropboxWriter(new RootDef("rb", "Dropbox", "acc", "/Pilot", DropboxWire.RootId),
            Side.B, client, "unused", isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
        var results = await Apply(new Executor(store, settings, Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), Plan(action), source, writer)
            .WaitAsync(HeldEffectTimeout);
        var diagnostics = metrics.Snapshot();
        var expectedAttempts = fault switch
        {
            "rejected" or "long-backoff" => 1,
            // A 5xx start earns four retries (five attempts), independent of the configured transport count.
            "http" => 5,
            _ => succeed ? 2 : retries + 1
        };
        Require(wire.Starts == expectedAttempts && wire.SequentialClosed, "bounded retries repeat only closed sequential staging");
        Require(diagnostics.RetryCount == expectedAttempts - 1, "every actual session-start retry is measured");
        Require(diagnostics.Timings[RunTimingStage.UploadSessionStart].Count == expectedAttempts,
            "each session-start attempt is measured, including failures");
        Require(results.Count == 1 && results[0].Outcome == (succeed ? Outcome.Done : Outcome.Failed),
            "session-start recovery is Done; exhausted or permanent start failure is Failed, never Uncertain/Skipped");
        Require(wire.Finishes == (succeed ? 1 : 0), "failed staging never submits a visible commit");
        Require(succeed ? diagnostics.Timings.GetValueOrDefault(RunTimingStage.BatchWait)?.Count == 1
            : !diagnostics.Timings.ContainsKey(RunTimingStage.BatchWait),
            "failed staging never enters the commit queue wait");
        var intents = store.UnresolvedIntents(Pair().PairId);
        Require(succeed ? intents.Count == 0 : intents.Count == 1 && intents[0].ProviderEffect is null,
            "successful staging closes its intent; Failed retains a retryable intent without a commit checkpoint");
        if (fault == "throttle")
        {
            Require(diagnostics.ThrottleCount == 1, "429 uses shared pacing throttle instrumentation");
            Require(retryAt - lastFault >= retryAfter - TimeSpan.FromMilliseconds(5), "Retry-After is not shortened");
        }
        if (fault == "http")
        {
            Require(diagnostics.ServerErrorCount == 5 && diagnostics.ThrottleCount == 0,
                "each 5xx start response is counted as a server error, apart from 429 throttles");
            var schedule = new[] { 1000d, 3000d, 8000d, 20000d };
            Require(delays.Count == 4, "four waits precede the exhausted fifth 5xx attempt");
            for (var index = 0; index < delays.Count; index++)
                Require(WithinJitter(delays[index], schedule[index]),
                    $"5xx backoff {index} is about {schedule[index]:N0} ms with jitter: {delays[index].TotalMilliseconds:N0} ms");
        }
        if (fault == "long-backoff")
        {
            Require(diagnostics.ThrottleCount == 1 && clock.Elapsed < TimeSpan.FromSeconds(2),
                "oversized Retry-After is recorded but fails promptly without sleeping or another request");
            Require(results[0].Reason?.Contains("five-minute", StringComparison.Ordinal) == true &&
                results[0].Reason?.Contains(TimeSpan.FromDays(60).TotalSeconds.ToString("N0"), StringComparison.Ordinal) == true,
                "bounded retry refusal retains the required backoff");
        }
        if (fault == "lost" && succeed)
            Require(retryAt - lastFault >= TimeSpan.FromMilliseconds(15), "configured retry backoff is observed");
        if (!succeed)
        {
            var reason = fault switch
            {
                "lost" => "scripted session-start connection lost",
                "malformed" => "invalid JSON literal",
                "rejected" => "HTTP 409",
                "long-backoff" => "HTTP 429",
                _ => "HTTP 503"
            };
            Require(results[0].Reason?.Contains(reason, StringComparison.OrdinalIgnoreCase) == true,
                "definite start failure retains its original reason: " + results[0].Reason);
        }
    }

    // A 5xx that clears within the raised ceiling succeeds; one that never clears fails after four retries.
    private static async Task UploadSessionStart_ServerErrorScheduleAsync()
    {
        await UploadStartServerSequenceAsync(failures: 2, succeed: true);
        await UploadStartServerSequenceAsync(failures: int.MaxValue, succeed: false);
    }

    private static async Task UploadStartServerSequenceAsync(int failures, bool succeed)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash)
        {
            StartResponse = (attempt, _) => Task.FromResult<HttpResponseMessage?>(attempt <= failures
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null)
        };
        using var http = new HttpClient(wire);
        var client = UploadSafetyClient(http, new FixedTokenSource());
        var delays = new List<TimeSpan>();
        client.SessionStartDelay = (delay, _) => { delays.Add(delay); return Task.CompletedTask; };
        var settings = Settings(1) with { UploadBatchSize = 1 };
        using var stream = new MemoryStream(bytes);
        if (succeed)
        {
            await client.UploadBatchedAsync("f", stream, hash, null, settings, default).WaitAsync(HeldEffectTimeout);
            Require(wire.Starts == 3 && delays.Count == 2 && wire.Finishes == 1,
                "two 500s are retried and the third start commits");
            Require(WithinJitter(delays[0], 1000) && WithinJitter(delays[1], 3000),
                "the recovered sequence follows the 1 s then 3 s schedule");
            return;
        }
        Exception? failure = null;
        try { await client.UploadBatchedAsync("f", stream, hash, null, settings, default).WaitAsync(HeldEffectTimeout); }
        catch (Exception error) { failure = error; }
        Require(failure is IOException { Message: var message } &&
            message.Contains("after 5 attempt(s)", StringComparison.Ordinal),
            "a persistent 500 fails after four retries with a clear reason: " + failure?.Message);
        Require(wire.Starts == 5 && delays.Count == 4 && wire.Finishes == 0,
            "the raised ceiling allows exactly four retries before the fifth attempt fails");
        var schedule = new[] { 1000d, 3000d, 8000d, 20000d };
        for (var index = 0; index < delays.Count; index++)
            Require(WithinJitter(delays[index], schedule[index]),
                $"5xx backoff {index} is about {schedule[index]:N0} ms with jitter");
    }

    private static bool WithinJitter(TimeSpan value, double milliseconds) =>
        value.TotalMilliseconds >= milliseconds * 0.8 - 1 && value.TotalMilliseconds <= milliseconds * 1.2 + 1;

    private static async Task UploadStartAccessRefusalAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        var source = new FakeEndpoint(Side.A, "ra");
        var action = Act(ActionKind.CopyNew, Side.B, "f", source: source.Seed("f", bytes), key: "k");
        using var wire = new UploadBatchWire(hash)
        {
            StartResponse = (_, _) => Task.FromResult<HttpResponseMessage?>(
                new HttpResponseMessage(HttpStatusCode.Unauthorized))
        };
        using var http = new HttpClient(wire);
        var client = UploadSafetyClient(http, new FixedTokenSource());
        var settings = Settings(1) with { EnableRunInstrumentation = true, DropboxSessionStartRetryBaseDelayMs = 0 };
        using var metrics = RunInstrumentation.Begin(settings, TimeProvider.System)!;
        var writer = new DropboxWriter(new RootDef("rb", "Dropbox", "acc", "/Pilot", DropboxWire.RootId),
            Side.B, client, "unused", isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
        using var stream = new MemoryStream(bytes);
        Exception? failure = null;
        try { await writer.PutAsync(action, stream, default).WaitAsync(HeldEffectTimeout); }
        catch (Exception error) { failure = error; }
        Require(failure is EndpointAccessException { InnerException: DropboxApiException
            { StatusCode: HttpStatusCode.Unauthorized } },
            "session-start 401 preserves the account-access reconnect path and original HTTP refusal");
        var diagnostics = metrics.Snapshot();
        Require(wire.Starts == 1 && wire.Finishes == 0 && diagnostics.RetryCount == 0 &&
            !diagnostics.Timings.ContainsKey(RunTimingStage.BatchWait),
            "account-access refusal is neither retried nor staged for a visible commit");
    }

    private static async Task UploadStartCancellationAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash);
        using var stop = new CancellationTokenSource();
        wire.StartResponse = (_, _) =>
        {
            stop.Cancel();
            throw new OperationCanceledException("scripted user cancellation", stop.Token);
        };
        using var http = new HttpClient(wire);
        var client = UploadSafetyClient(http, new FixedTokenSource());
        using var stream = new MemoryStream(bytes);
        var cancelled = false;
        try
        {
            await client.UploadBatchedAsync("f", stream, hash, null, Settings(1) with
            { DropboxSessionStartRetryBaseDelayMs = 0 }, stop.Token).WaitAsync(HeldEffectTimeout);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { cancelled = true; }
        Require(cancelled && wire.Starts == 1 && wire.Finishes == 0,
            "user cancellation remains cancellation without retry or visible commit");
    }
}
