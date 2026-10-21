using System.Net;
using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task UploadPipeline_Safety()
    {
        foreach (var status in new[] { HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.RequestTimeout, HttpStatusCode.TooManyRequests })
            await UploadFinishFailureAsync(status, false);
        await UploadFinishFailureAsync(null, true);
        foreach (var retries in new[] { 0, 1 })
            foreach (var rejectedFinishes in new[] { 1, 2 })
                await UploadWriteRetryAsync(retries, rejectedFinishes);
        await UploadRejectionJournalWriteFailsAsync();
        await UploadCommitTransportTimeoutIsUncertainAsync();
        await UploadRejectionReasonIsReportedAsync();
    }

    // The refusal is a known, harmless outcome, but it is only diagnosable if the provider's WriteError tag
    // reaches the operator. A path conflict arrives nested (failure.path.conflict); the message must name the
    // resolved tag instead of dropping it.
    private static async Task UploadRejectionReasonIsReportedAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash) { RejectOne = true, NestedPathConflict = true };
        using var http = new HttpClient(wire);
        var client = UploadSafetyClient(http, new FixedTokenSource());
        var settings = Settings(1) with { UploadBatchSize = 1 };
        using var stream = new MemoryStream(bytes);
        Exception? failure = null;
        try { await client.UploadBatchedAsync("f", stream, hash, null, settings, default).WaitAsync(HeldEffectTimeout); }
        catch (Exception exception) { failure = exception; }
        Require(failure is DropboxApiException, "a refused batch entry stays a known rejection");
        var message = (failure as DropboxApiException)?.Message ?? "";
        Require(message.Contains("reason: conflict", StringComparison.Ordinal),
            "the refusal message carries the resolved provider reason tag");
        Require(message.Contains("no effect was applied", StringComparison.Ordinal),
            "the refusal message still states that nothing was applied");
    }

    // A journal write that fails while recording an upload rejection must surface as an uncertain outcome: the
    // provider may have applied the upload, so a plain I/O failure must never reach the caller as the effect result.
    private static async Task UploadRejectionJournalWriteFailsAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash) { WriteRejectionFinishes = 1 };
        using var http = new HttpClient(wire);
        var client = UploadSafetyClient(http, new FixedTokenSource());
        var settings = Settings(1) with { UploadBatchSize = 1 };
        var previous = ProviderEffectContext.Current.Value;
        using var stream = new MemoryStream(bytes);
        ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
            checkpoint.Kind == "upload-rejected"
                ? Task.FromException(new IOException("upload journal unavailable"))
                : Task.CompletedTask);
        try
        {
            var upload = client.UploadBatchedAsync("f", stream, hash, null, settings, default);
            Exception? failure = null;
            try { await upload.WaitAsync(HeldEffectTimeout); }
            catch (Exception exception) { failure = exception; }
            Require(failure is DropboxEffectUncertainException,
                "a journal write failure while recording a rejection is an uncertain outcome, not a raw I/O failure");
        }
        finally { ProviderEffectContext.Current.Value = previous; }
    }

    private static async Task UploadFinishFailureAsync(HttpStatusCode? status, bool failToken)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash) { FinishStatus = status, HoldFirstCommit = true };
        using var http = new HttpClient(wire);
        var token = new UploadFailingToken();
        var client = UploadSafetyClient(http, token);
        var settings = Settings(1) with { UploadBatchSize = 1 };
        var checkpoints = new List<string>();
        var previous = ProviderEffectContext.Current.Value;
        var previousSource = SyncUploadSource.Current.Value;
        var stream = new UploadTrackedStream(bytes);
        await using var owner = new SyncUploadSource(stream);
        ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
        {
            checkpoints.Add(checkpoint.Kind);
            if (failToken && checkpoint.Kind == "upload-commit") token.Fail = true;
            return Task.CompletedTask;
        });
        SyncUploadSource.Current.Value = owner;
        try
        {
            var upload = client.UploadBatchedAsync("f", stream, hash, null, settings, default);
            if (!failToken)
            {
                await wire.FirstCommit.Task.WaitAsync(HeldEffectTimeout);
                Require(stream.Disposals == 1, "source closes before held finish result");
                wire.ReleaseCommit.TrySetResult();
            }
            Exception? failure = null;
            try { await upload.WaitAsync(HeldEffectTimeout); }
            catch (Exception exception) { failure = exception; }
            var rejected = failToken || status == HttpStatusCode.Conflict;
            Require(rejected ? failure is not null and not DropboxEffectUncertainException
                : failure is DropboxEffectUncertainException, "send boundary classifies known rejection versus unknown");
            Require(checkpoints.SequenceEqual(rejected ? new[] { "upload-commit", "upload-rejected" }
                : new[] { "upload-commit" }), "only commit checkpoint is durable before send; known rejection clears it");
            Require(wire.Finishes == (failToken ? 0 : 1), "pre-send auth failure sends nothing; HTTP errors are never replayed");
            await owner.DisposeAsync();
            Require(stream.Disposals == 1, "copy owner disposes staged source exactly once");
        }
        finally
        {
            wire.ReleaseCommit.TrySetResult();
            ProviderEffectContext.Current.Value = previous;
            SyncUploadSource.Current.Value = previousSource;
        }
    }

    // Inject HttpClient's timeout shape after the finish body reached the wire. The real pacing seam
    // must leave it recognizable to the upload send-boundary classifier, not turn it into Failed.
    private static async Task UploadCommitTransportTimeoutIsUncertainAsync()
    {
        var source = new FakeEndpoint(Side.A, "ra");
        var action = Act(ActionKind.CopyNew, Side.B, "f", source: source.Seed("f", new string('x', 16)), key: "k");
        using var wire = new UploadBatchWire(action.Source!.Hash!)
        {
            FinishFault = new OperationCanceledException("scripted HttpClient timeout",
                new TimeoutException("scripted transport deadline"))
        };
        using var http = new HttpClient(wire);
        var client = UploadSafetyClient(http, new FixedTokenSource());
        var settings = Settings(1) with { UploadBatchSize = 1 };
        using var store = new InMemoryStateStore();
        store.SavePair(Pair());
        var writer = new DropboxWriter(new RootDef("rb", "Dropbox", "acc", "/Pilot", DropboxWire.RootId),
            Side.B, client, "unused", settings);
        var results = await Apply(new Executor(store, settings, Admission()), Plan(action), source, writer)
            .WaitAsync(HeldEffectTimeout);
        Require(results.Count == 1 && results[0].Outcome == Outcome.Uncertain,
            "post-send HttpClient timeout through pacing is Uncertain, never Failed");
        Require(wire.Starts == 1 && wire.Finishes == 1 && wire.BatchSizes.SequenceEqual(new[] { 1 }),
            "timeout follows a received finish body and never replays the visible commit");
        var intents = store.UnresolvedIntents(Pair().PairId);
        Require(intents.Count == 1 && intents[0].ProviderEffect is not null,
            "post-send timeout retains the unresolved provider checkpoint for reconciliation");
    }

    private static async Task UploadWriteRetryAsync(int retries, int rejectedFinishes)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash) { WriteRejectionFinishes = rejectedFinishes };
        using var http = new HttpClient(wire);
        var client = UploadSafetyClient(http, new FixedTokenSource());
        var settings = Settings(2) with { UploadBatchSize = 2, DropboxWriteOperationRetryCount = retries };
        var tasks = Enumerable.Range(0, 2).Select(async index =>
        {
            using var stream = new MemoryStream(bytes);
            try { await client.UploadBatchedAsync("f" + index, stream, hash, null, settings, default); return true; }
            catch (DropboxApiException) { return false; }
        }).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(HeldEffectTimeout);
        Require(wire.BatchSizes.SequenceEqual(retries == 0 ? new[] { 2 } : new[] { 2, 1 }),
            "only explicit write-operation rejection is retried once, never successful siblings");
        Require(results.Count(x => x) == (retries == 1 && rejectedFinishes == 1 ? 2 : 1),
            "retry success and exhaustion remain isolated per entry");
        Require(wire.Starts == 2 && wire.PeakFinishes == 1, "retry reuses closed session under the same commit gate");
    }

    private static DropboxPilotClient UploadSafetyClient(HttpClient http, IDropboxAccessTokenSource token) =>
        new(http, token, "/Pilot", new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes), DropboxWire.RootId);

    private sealed class UploadFailingToken : IDropboxAccessTokenSource
    {
        internal bool Fail;
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Fail
            ? throw new InvalidOperationException("scripted token unavailable") : Task.FromResult("test-token");
    }

    private sealed class UploadTrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int Disposals;
        public override ValueTask DisposeAsync()
        {
            Disposals++;
            return base.DisposeAsync();
        }
    }
}
