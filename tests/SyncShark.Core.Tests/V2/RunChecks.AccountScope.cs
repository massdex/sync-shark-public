using System.Net;

namespace Massdex.SyncShark.V2;

// One execution batch shares exactly one /2/users/get_current_account flight per adapter+binding+token, however
// many concurrent operations the run performs: the run owner begins the verification scope before any worker starts.
internal static partial class RunChecks
{
    private static async Task AccountVerificationDedupeAsync()
    {
        using var wire = new DropboxWire();
        using var http = new HttpClient(wire);
        using var store = new InMemoryStateStore();
        var settings = V2Settings.Default with { MaxWorkers = 6, ProgressInterval = TimeSpan.Zero };
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, DropboxWire.RootId);
        var pair = new PairDef("p1", "p1",
            new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/Pilot", DropboxWire.RootId), [], [], true);
        store.SavePair(pair);
        var provider = new ConcurrentVerifyProbe(new FakeEndpointProvider()
            .Add(pair.A, new FakeEndpoint(Side.A, "ra"))
            .Add(pair.B, new FakeEndpoint(Side.B, "rb")), client, ConcurrentOperations);
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission()), provider,
            settings, TimeProvider.System);

        await run.PreviewFirstSyncAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);

        var verifications = wire.Calls.Count(call => call == "/2/users/get_current_account");
        Require(verifications == 1,
            ConcurrentOperations + " concurrent v2 operations share one get_current_account flight per token; observed " +
            verifications);
        // A transient failure must not pin the flight for the whole batch: the next caller verifies once and succeeds.
        await using var retryScope = SyncProviderAccountVerificationScope.Begin(CancellationToken.None);
        var beforeFailure = wire.Calls.Count(call => call == "/2/users/get_current_account");
        // A refusal the read-retry policy does not retry, so the flight itself faults.
        wire.Script("/2/users/get_current_account", HttpStatusCode.BadRequest, 1);
        var failed = false;
        try { await client.ReadRootFolderIdAsync(CancellationToken.None); }
        catch (Exception) { failed = true; }
        Require(failed, "the scripted first verification failure surfaces to its caller");
        var afterFailure = wire.Calls.Count(call => call == "/2/users/get_current_account");
        Require(afterFailure - beforeFailure == 1, "the failed verification issued exactly one request");
        var rootId = await client.ReadRootFolderIdAsync(CancellationToken.None);
        var afterRetry = wire.Calls.Count(call => call == "/2/users/get_current_account");
        Require(afterRetry - afterFailure == 1,
            "the next caller starts exactly one new verification after a failed flight; observed " +
            (afterRetry - afterFailure));
        Require(rootId == DropboxWire.RootId, "the retried verification succeeds");
        Console.WriteLine("PASS AccountVerificationDedupe");
    }

    private const int ConcurrentOperations = 12;

    // Runs the concurrent provider operations inside the run's prepare, so they are started by the run owner's body
    // exactly as a worker task would be. Each call would verify the account on its own without the run's scope.
    private sealed class ConcurrentVerifyProbe : IEndpointProvider
    {
        private readonly IEndpointProvider _inner;
        private readonly DropboxPilotClient _client;
        private readonly int _operations;
        private int _probed;

        public ConcurrentVerifyProbe(IEndpointProvider inner, DropboxPilotClient client, int operations)
        {
            _inner = inner;
            _client = client;
            _operations = operations;
        }

        public IInventorySource Inventory(RootDef root) => _inner.Inventory(root);
        public IEndpointWriter Writer(RootDef root) => _inner.Writer(root);

        public async Task PrepareAsync(PairDef pair, CancellationToken ct)
        {
            await _inner.PrepareAsync(pair, ct).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _probed, 1) != 0) return;
            await Task.WhenAll(Enumerable.Range(0, _operations).Select(_ =>
                Task.Run(() => _client.VerifyRootIdentityAsync(ct), ct))).ConfigureAwait(false);
        }
    }
}
