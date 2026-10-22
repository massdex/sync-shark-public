using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Only HTTP input is simulated. Account/root/item authorization, the CLI, Host,
// PairRunner, native file effects and retained receipts are production paths.
internal static class ProviderAccountScopeChecks
{
    internal static async Task RunAsync(string root)
    {
        await ConcurrentValidationAsync();
        await PendingRootChecksAsync();
        await IdentityRejectionsAsync();
        await RotationAsync();
        await CancellationAsync();
        await CleanupAndNestingAsync();
        await FreshMetadataAndRetryAsync();
        await HostAsync(Path.Combine(root, "success"), "success");
        await HostAsync(Path.Combine(root, "wrong-identity"), "wrong-identity");
        await HostAsync(Path.Combine(root, "owner-cancel"), "owner-cancel");
    }

    private static DropboxConnectionBinding Binding() => new(DropboxProviderConfiguration.RegisteredClientId,
        "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
        DropboxProviderConfiguration.RequiredScopes);

    private static DropboxPilotClient Client(HttpClient http, Tokens tokens) =>
        new(http, tokens, "/Pilot", Binding(), "id:pilot-folder");

    private static async Task ConcurrentValidationAsync()
    {
        using var wire = new Wire { HoldAccount = true };
        using var http = new HttpClient(wire);
        var tokens = new Tokens();
        var client = Client(http, tokens);
        await using (SyncProviderAccountVerificationScope.Begin(CancellationToken.None))
        {
            var work = Enumerable.Range(0, 64).Select(_ => client.VerifyRootIdentityAsync()).ToArray();
            await wire.AccountEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(tokens.ScopedCalls == 64 && wire.AccountCalls == 1 && wire.RootCalls == 0,
                "64 real adapter waiters share one held account request before any root request");
            wire.AccountRelease.TrySetResult();
            await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(10));
            Require(wire.AccountCalls == 1 && wire.RootCalls is > 0 and <= 64,
                "account verification is shared while completed folder verdicts are not cached");
            var roots = wire.RootCalls;
            await client.VerifyRootIdentityAsync();
            Require(wire.RootCalls == roots + 1,
                "the next check after completion performs a fresh root HTTP read in the same batch");
        }
        wire.HoldAccount = false;
        await client.VerifyRootIdentityAsync();
        await client.VerifyRootIdentityAsync();
        Require(wire.AccountCalls == 3, "outside a batch two checks each validate the account freshly");
        await using (SyncProviderAccountVerificationScope.Begin(CancellationToken.None))
            await client.VerifyRootIdentityAsync();
        Require(wire.AccountCalls == 4, "a subsequent batch revalidates the same adapter and token");
    }

    private static async Task PendingRootChecksAsync()
    {
        foreach (var scenario in new[] { "concurrent-64", "wrong-root", "rotated-token", "owner-cancel", "waiter-cancel" })
        {
            using var wire = new Wire { HoldRoot = true };
            using var http = new HttpClient(wire);
            using var owner = new CancellationTokenSource();
            using var waiter = new CancellationTokenSource();
            var tokens = new Tokens(); var client = Client(http, tokens);
            await using (SyncProviderAccountVerificationScope.Begin(owner.Token))
            {
                var first = client.VerifyRootIdentityAsync(waiter.Token);
                await wire.RootEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (scenario == "rotated-token") tokens.Value = "fixture-generation-b";
                var others = Enumerable.Range(0, scenario == "concurrent-64" ? 63 : 1)
                    .Select(_ => client.VerifyRootIdentityAsync()).ToArray();
                Require(wire.RootCalls == (scenario == "rotated-token" ? 2 : 1) && !others.Any(task => task.IsCompleted),
                    "pending exact-root HTTP is shared across waiters, but a rotated token owns a separate verified read");
                if (scenario == "owner-cancel")
                {
                    owner.Cancel();
                    await CancelledAsync(first); await CancelledAsync(Task.WhenAll(others));
                    Require(wire.RootCancelled == 1 && wire.ActiveRoots == 0,
                        "owner cancellation cancels and drains the actual shared root HTTP request");
                    continue;
                }
                if (scenario == "waiter-cancel") { waiter.Cancel(); await CancelledAsync(first); }
                if (scenario == "wrong-root") wire.ReplaceRoot = true;
                wire.HoldRoot = false; wire.RootRelease.TrySetResult();
                if (scenario == "wrong-root")
                {
                    await RejectAsync(() => first); await RejectAsync(() => Task.WhenAll(others));
                    Require(wire.Downloads == 0, "a shared wrong-folder response authorizes no file effect");
                    wire.ReplaceRoot = false;
                    await client.VerifyRootIdentityAsync();
                    Require(wire.RootCalls == 2, "a completed folder refusal is not reused on the next fresh check");
                }
                else
                {
                    if (scenario != "waiter-cancel") await first;
                    await Task.WhenAll(others);
                    Require(!wire.UnvalidatedRequest && wire.ActiveRoots == 0,
                        "concurrent, surviving and rotated root work uses exact validated authorization and drains all requests");
                    var roots = wire.RootCalls;
                    await client.VerifyRootIdentityAsync();
                    Require(wire.RootCalls == roots + 1, "completed root reads are never cached within the batch");
                }
            }
        }
    }

    private static async Task IdentityRejectionsAsync()
    {
        foreach (var body in new[]
        {
            "{\"account_id\":\"other\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}",
            "{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"other\",\"home_namespace_id\":\"home-namespace\"}}",
            "{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"other\"}}",
            "{}", "{\"account_id\":null,\"root_info\":null}", "{\"account_id\":17,\"root_info\":{}}"
        })
        {
            using var wire = new Wire { AccountBody = body };
            using var http = new HttpClient(wire);
            var client = Client(http, new Tokens());
            await using (SyncProviderAccountVerificationScope.Begin(CancellationToken.None))
            {
                await RejectAsync(() => client.VerifyRootIdentityAsync());
                await RejectAsync(() => client.VerifyRootIdentityAsync());
            }
            Require(wire.AccountCalls == 1 && wire.RootCalls == 0 && wire.Downloads == 0,
                "wrong account/namespace or malformed tuple never authorizes a root or file request");
            wire.AccountBody = null;
            await using (SyncProviderAccountVerificationScope.Begin(CancellationToken.None))
                await client.VerifyRootIdentityAsync();
            Require(wire.AccountCalls == 2, "failed validation cannot poison a later fresh batch");
        }
    }

    private static async Task RotationAsync()
    {
        foreach (var wrong in new[] { false, true })
        {
            using var wire = new Wire(); using var http = new HttpClient(wire);
            var tokens = new Tokens(); var client = Client(http, tokens);
            await using (SyncProviderAccountVerificationScope.Begin(CancellationToken.None))
            {
                await client.VerifyRootIdentityAsync();
                var roots = wire.RootCalls;
                tokens.Value = "fixture-generation-b";
                wire.RejectGenerationB = wrong;
                if (wrong) await RejectAsync(() => client.VerifyRootIdentityAsync());
                else await client.VerifyRootIdentityAsync();
                Require(wire.AccountCalls == 2 && (!wrong || wire.RootCalls == roots),
                    "rotated actual token validates again before authorization; wrong replacement performs no root request");
            }
            Require(!wire.UnvalidatedRequest, "all authorized requests use the exact successfully validated token");
        }
        using var during = new Wire(); using var duringHttp = new HttpClient(during);
        var rotating = new Tokens(); var duringClient = Client(duringHttp, rotating);
        during.AfterFirstRoot = () => rotating.Value = "fixture-generation-b";
        await using (SyncProviderAccountVerificationScope.Begin(CancellationToken.None))
            await duringClient.ReadEntryAsync(Wire.Name(0), "id:fixture-0");
        Require(during.AccountCalls == 2 && during.RootCalls == 1 && during.ItemCalls == 1 && !during.UnvalidatedRequest,
            "token rotation between folder and exact-file readbacks is checked before the second request");
    }

    private static async Task CancellationAsync()
    {
        using (var wire = new Wire { HoldAccount = true })
        using (var http = new HttpClient(wire))
        using (var individual = new CancellationTokenSource())
        {
            var client = Client(http, new Tokens());
            await using (SyncProviderAccountVerificationScope.Begin(CancellationToken.None))
            {
                var cancelled = client.VerifyRootIdentityAsync(individual.Token);
                var kept = client.VerifyRootIdentityAsync();
                await wire.AccountEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                individual.Cancel();
                await CancelledAsync(cancelled);
                Require(wire.AccountCancelled == 0 && !kept.IsCompleted,
                    "one cancelled waiter does not cancel another worker's shared HTTP validation");
                wire.AccountRelease.TrySetResult();
                await kept.WaitAsync(TimeSpan.FromSeconds(5));
                Require(wire.AccountCalls == 1 && wire.RootCalls == 1, "remaining worker completes exact readback");
            }
        }
        using var ownedWire = new Wire { HoldAccount = true }; using var ownedHttp = new HttpClient(ownedWire);
        using var owner = new CancellationTokenSource(); var ownedClient = Client(ownedHttp, new Tokens());
        await using (SyncProviderAccountVerificationScope.Begin(owner.Token))
        {
            var work = ownedClient.VerifyRootIdentityAsync();
            await ownedWire.AccountEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            owner.Cancel(); await CancelledAsync(work);
        }
        Require(ownedWire.AccountCancelled == 1 && ownedWire.ActiveAccounts == 0 && ownedWire.RootCalls == 0 &&
                SyncProviderAccountVerificationScope.Current is null,
            "owning batch cancellation drains the actual HTTP flight and releases the ambient scope");
    }

    private static async Task FreshMetadataAndRetryAsync()
    {
        using var wire = new Wire { RateLimitFirstAccount = true }; using var http = new HttpClient(wire);
        var client = Client(http, new Tokens());
        await using (SyncProviderAccountVerificationScope.Begin(CancellationToken.None))
        {
            await client.VerifyRootIdentityAsync();
            Require(wire.AccountCalls == 2 && wire.RootCalls == 1, "account 429 retains the existing bounded read retry");
            wire.ReplaceRoot = true;
            await RejectAsync(() => client.VerifyRootIdentityAsync());
            Require(wire.AccountCalls == 2 && wire.RootCalls == 2 && wire.Downloads == 0,
                "completed checks do not cache or trust a subsequently replaced folder");
        }
    }

    private static async Task CleanupAndNestingAsync()
    {
        using var wire = new Wire { HoldAccount = true, ThrowOnCancellation = true, HoldCancelledFlight = true };
        using var http = new HttpClient(wire);
        var client = Client(http, new Tokens());
        var outer = SyncProviderAccountVerificationScope.Begin(CancellationToken.None);
        var inner = SyncProviderAccountVerificationScope.Begin(CancellationToken.None);
        var flight = client.VerifyRootIdentityAsync();
        await wire.AccountEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cleanup = inner.DisposeAsync().AsTask();
        await wire.CancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!cleanup.IsCompleted && wire.ActiveAccounts == 1 &&
                ReferenceEquals(SyncProviderAccountVerificationScope.Current, outer),
            "throwing actual HTTP cancellation callback cannot skip flight drain; inner scope restores its outer scope");
        wire.CancellationRelease.TrySetResult();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        await CancelledAsync(flight);
        Require(wire.ThrowingCallbacks == 1 && wire.ActiveAccounts == 0 && wire.RootCalls == 0,
            "cleanup observes cancellation callback failure and drains the held HTTP flight without authorizing effects");
        wire.HoldAccount = false;
        wire.ThrowOnCancellation = false;
        await client.VerifyRootIdentityAsync();
        Require(wire.AccountCalls == 2 && wire.RootCalls == 1,
            "restored outer scope performs a fresh account check rather than reusing the disposed inner flight");
        await outer.DisposeAsync();
        Require(SyncProviderAccountVerificationScope.Current is null, "nested scope disposal releases all ambient state");
    }

    private static async Task HostAsync(string root, string scenario)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh real HTTP account-scope fixture root.");
        var local = Path.Combine(root, "local"); Directory.CreateDirectory(local);
        using var wire = new Wire { FileCount = 32 }; using var http = new HttpClient(wire);
        var tokens = new Tokens(); var remote = Client(http, tokens);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var state = instance.StateAuthority; var connections = new DropboxConnectionStore(state);
        var binding = Binding().Normalize(); await connections.SaveNewAsync(binding);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using var host = new SyncAuthorityRuntimeHost(state, http, new SyncPairStore(state),
            new SyncRuntimeStateStore(state), connections, new FirstSyncEffectChecks.FixtureCredentialStore(),
            database, (_, _) => remote);
        await host.LoadPairsAsync(); using var lease = PilotDirectoryLease.Open(local);
        var pair = SyncPair.FromPilot(new PilotSettings(local, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Pilot", "id:pilot-folder"), "Actual HTTP account scope", binding.RootNamespaceId) with
        { ConnectionId = binding.ConnectionId, InitialReviewApproved = false, PropagateDeletions = false };
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(pair, select: true);
        var operations = new FirstSyncEffectChecks.FixtureOperations(host, state);
        using var commandCancellation = new CancellationTokenSource();
        var expectedSuccess = true;
        var lastExit = -1;
        async Task<JsonDocument> Cli(params string[] args)
        {
            using var output = new StringWriter(); using var progress = new StringWriter();
            lastExit = await SyncCli.ExecuteAsync(args, output, progress, () => operations, commandCancellation.Token);
            if (expectedSuccess && lastExit != 0) throw new InvalidOperationException("Actual HTTP fixture CLI failed: " + progress + output);
            return JsonDocument.Parse(output.ToString());
        }
        using var scan = await Cli("--cli", "scan", "--pair", pair.Id, "--yes", "--json", "--timeout-seconds", "30");
        var scanData = scan.RootElement.GetProperty("data");
        var choices = JsonSerializer.Serialize(new Dictionary<string, string?>
        { ["__plan"] = scanData.GetProperty("localScanGenerationId").GetString(),
          ["__remote_plan"] = scanData.GetProperty("dropboxScanGenerationId").GetString() });
        wire.HoldScopedAccount = true;
        wire.RejectScopedIdentity = scenario == "wrong-identity";
        expectedSuccess = scenario == "success";
        using var concurrency = SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(16);
        var applied = Cli("--cli", "first-sync", "apply", "--pair", pair.Id, "--yes", "--json",
            "--choices", choices, "--allow-recommended", "--timeout-seconds", "30");
        await wire.AccountEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var admissionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (tokens.ScopedCalls < 16) { admissionDeadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        Require(wire.ScopedAccountCalls == 1 && wire.Downloads == 0,
            "actual Host/PairRunner admits 16 file workers into one held HTTP validation flight");
        if (scenario == "owner-cancel") commandCancellation.Cancel();
        else wire.AccountRelease.TrySetResult();
        using var result = await applied.WaitAsync(TimeSpan.FromSeconds(35));
        if (scenario != "success")
        {
            var completed = result.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("firstSyncComplete", out var complete) && complete.GetBoolean();
            Require(result.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object && lastExit != 0,
                "rejected Host batch returns an explicit CLI error rather than a successful empty receipt");
            Console.WriteLine("Host rejection " + scenario + ": exit=" + lastExit + "; " + error.GetRawText());
            Require(!completed && wire.ScopedAccountCalls == 1 && wire.Downloads == 0 &&
                    wire.ActiveAccounts == 0 && !Directory.EnumerateFiles(local).Any() &&
                    SyncProviderAccountVerificationScope.Current is null,
                "actual CLI/Host/PairRunner " + scenario + " drains one failed account flight and records no transfer or local effect");
            if (scenario == "owner-cancel")
                Require(lastExit == 130 && wire.AccountCancelled == 1,
                    "real owning CLI cancellation returns 130 after the actual account HTTP request is drained");
            else
            {
                // Detailed CLI failures now carry the exception type, HRESULT and safe application frames
                // (SyncActivityText.Failure). DescribeException rebuilds exactly that prefix, so the exact
                // retained refusal cause is still asserted instead of the obsolete bare text.
                var refusalPrefix = SyncCli.DescribeException(new InvalidOperationException(
                    "Dropbox account or namespace differs from the saved connection."));
                Require(lastExit == 6 && error.GetProperty("code").GetString() == "operation_failed" &&
                        error.GetProperty("message").GetString()?.StartsWith(refusalPrefix, StringComparison.Ordinal) == true,
                    "wrong identity reports the exact retained authorization refusal");
            }
            await using var rejectedRead = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await rejectedRead.OpenAsync(); await using var rejectedReceipt = rejectedRead.CreateCommand();
            // Scan/command preparation legitimately retains allocate_control.
            // It is not a file effect and must not be mistaken for a transfer.
            rejectedReceipt.CommandText = "SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair AND o.kind<>'allocate_control' AND r.certainty='known_success';";
            rejectedReceipt.Parameters.AddWithValue("$pair", pair.Id);
            Require(Convert.ToInt64(await rejectedReceipt.ExecuteScalarAsync()) == 0,
                "rejected Host batch retains no falsely successful content effect receipt");
            return;
        }
        Require(result.RootElement.GetProperty("data").GetProperty("firstSyncComplete").GetBoolean() &&
                wire.ScopedAccountCalls == 1 && wire.Downloads == 32 && !wire.UnvalidatedRequest,
            "real CLI/Host/PairRunner completes all 32 files with one scoped account check and no duplicate download");
        for (var i = 0; i < 32; i++)
            Require((await File.ReadAllBytesAsync(Path.Combine(local, Wire.Name(i)))).SequenceEqual(Wire.Payload),
                "real local file retains the exact verified HTTP bytes");
        await using var read = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await read.OpenAsync(); await using var receipt = read.CreateCommand();
        receipt.CommandText = "SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair AND o.kind='create_local' AND o.status='completed' AND r.certainty='known_success';";
        receipt.Parameters.AddWithValue("$pair", pair.Id);
        Require(Convert.ToInt64(await receipt.ExecuteScalarAsync()) == 32 && wire.ItemCalls >= 128 && wire.RootCalls >= 3,
            "all exact successful receipts and original per-file item/root readbacks remain retained");
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or KeyNotFoundException or DropboxApiException) { return; }
        throw new InvalidOperationException("An invalid identity was accepted.");
    }
    private static async Task CancelledAsync(Task work)
    { try { await work.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { return; } throw new InvalidOperationException("Cancellation was ignored."); }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }

    private sealed class Tokens : IDropboxAccessTokenSource
    {
        internal string Value = "fixture-generation-a";
        private int _scopedCalls;
        internal int ScopedCalls => Volatile.Read(ref _scopedCalls);
        public Task<string> GetAccessTokenAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); if (SyncProviderAccountVerificationScope.Current is not null) Interlocked.Increment(ref _scopedCalls); return Task.FromResult(Value); }
    }

    private sealed class Wire : HttpMessageHandler
    {
        internal static readonly byte[] Payload = Encoding.UTF8.GetBytes("immutable fixture payload");
        private static readonly string Hash = DropboxContentHash.Compute(Payload);
        internal static string Name(int i) => $"file-{i:D3}.txt";
        private readonly ConcurrentDictionary<string, bool> _validated = new(StringComparer.Ordinal);
        internal readonly TaskCompletionSource AccountEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AccountRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancellationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancellationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldAccount, HoldScopedAccount, RejectGenerationB, RateLimitFirstAccount, ReplaceRoot, UnvalidatedRequest;
        internal bool ThrowOnCancellation, HoldCancelledFlight, RejectScopedIdentity;
        internal bool HoldRoot;
        internal readonly TaskCompletionSource RootEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RootRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ActiveRoots, RootCancelled;
        internal string? AccountBody;
        internal Action? AfterFirstRoot;
        internal int FileCount, AccountCalls, ScopedAccountCalls, RootCalls, ItemCalls, Downloads, AccountCancelled, ActiveAccounts, ThrowingCallbacks;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var route = request.RequestUri!.AbsolutePath;
            var generation = request.Headers.Authorization?.Parameter ?? throw new InvalidDataException("Missing fixture authorization.");
            if (route == "/2/users/get_current_account")
            {
                var call = Interlocked.Increment(ref AccountCalls);
                var scoped = SyncProviderAccountVerificationScope.Current is not null;
                if (scoped) Interlocked.Increment(ref ScopedAccountCalls);
                Interlocked.Increment(ref ActiveAccounts);
                using var cancellationRegistration = ThrowOnCancellation ? token.Register(() =>
                {
                    Interlocked.Increment(ref ThrowingCallbacks);
                    throw new InvalidOperationException("Synthetic HTTP cancellation callback failed.");
                }) : default;
                try
                {
                    if (HoldAccount || HoldScopedAccount && scoped)
                    { AccountEntered.TrySetResult(); await AccountRelease.Task.WaitAsync(token); }
                    if (RateLimitFirstAccount && call == 1)
                    { var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return limited; }
                    var wrongAccount = RejectScopedIdentity && scoped || RejectGenerationB && generation == "fixture-generation-b";
                    var body = AccountBody ?? (wrongAccount
                        ? "{\"account_id\":\"other\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}"
                        : "{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}");
                    if (AccountBody is null && !wrongAccount) _validated[generation] = true;
                    return Json(body);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref AccountCancelled);
                    CancellationEntered.TrySetResult();
                    if (HoldCancelledFlight) await CancellationRelease.Task;
                    throw;
                }
                finally { Interlocked.Decrement(ref ActiveAccounts); }
            }
            if (!_validated.ContainsKey(generation)) { UnvalidatedRequest = true; throw new InvalidOperationException("Request used a token before actual account validation."); }
            using var pathRoot = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Path-Root").Single());
            if (pathRoot.RootElement.GetProperty("root").GetString() != "root-namespace") throw new InvalidDataException("Wrong namespace header.");
            if (route == "/2/files/get_metadata")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var path = body.RootElement.GetProperty("path").GetString()!;
                if (path is "/Pilot" or "id:pilot-folder")
                {
                    var call = Interlocked.Increment(ref RootCalls);
                    Interlocked.Increment(ref ActiveRoots);
                    try
                    {
                        if (HoldRoot) { RootEntered.TrySetResult(); await RootRelease.Task.WaitAsync(token); }
                        if (call == 1) AfterFirstRoot?.Invoke();
                        return Json(new { @event = 0, name = "Pilot", id = ReplaceRoot ? "id:replaced" : "id:pilot-folder", path_lower = "/pilot", path_display = "/Pilot" }, "folder");
                    }
                    catch (OperationCanceledException) { Interlocked.Increment(ref RootCancelled); throw; }
                    finally { Interlocked.Decrement(ref ActiveRoots); }
                }
                Interlocked.Increment(ref ItemCalls);
                var index = Index(path);
                return Json(Metadata(index), "file");
            }
            if (route == "/2/files/list_folder")
                return Json(new { entries = Enumerable.Range(0, FileCount).Select(i => TaggedMetadata(i)).ToArray(), cursor = "fixture-cursor", has_more = false });
            // The pre-listing change cursor (commit 3397ce6) and the replay of changes that landed during
            // the walk. The fixture listing is instantaneous and nothing changes underneath it, so the
            // replay is always an empty, complete page.
            if (route == "/2/files/list_folder/get_latest_cursor")
                return Json(new { cursor = "fixture-latest-cursor" });
            if (route == "/2/files/list_folder/continue")
                return Json(new { entries = Array.Empty<object>(), cursor = "fixture-change-cursor", has_more = false });
            if (route == "/2/files/download")
            {
                Interlocked.Increment(ref Downloads);
                using var args = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
                var index = Index(args.RootElement.GetProperty("path").GetString()!);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
                response.Headers.Add("Dropbox-API-Result", JsonSerializer.Serialize(TaggedMetadata(index)));
                return response;
            }
            throw new InvalidOperationException("Unexpected fixture HTTP route: " + route);
        }
        private static int Index(string path) => int.Parse(path.Replace("rev:fixture-", "", StringComparison.Ordinal)
            .Replace("id:fixture-", "", StringComparison.Ordinal).Replace("id:pilot-folder/", "", StringComparison.Ordinal)
            .Replace("/Pilot/", "", StringComparison.Ordinal).Replace("file-", "", StringComparison.Ordinal).Replace(".txt", "", StringComparison.Ordinal));
        private static object Metadata(int i) => new { name = Name(i), id = "id:fixture-" + i, rev = "fixture-" + i,
            size = Payload.Length, content_hash = Hash, path_lower = "/pilot/" + Name(i), path_display = "/Pilot/" + Name(i),
            client_modified = "2026-10-01T12:00:00Z", server_modified = "2026-10-01T12:00:00Z" };
        private static Dictionary<string, object?> TaggedMetadata(int i) =>
            JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(Metadata(i)))!
                .Append(new KeyValuePair<string, object?>(".tag", "file")).ToDictionary(item => item.Key, item => item.Value);
        private static HttpResponseMessage Json(object body, string? tag = null)
        {
            var json = body is string value ? value : JsonSerializer.Serialize(body);
            if (tag is not null)
            { var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!; fields[".tag"] = tag; json = JsonSerializer.Serialize(fields); }
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
