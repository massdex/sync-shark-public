using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// GCLI-G3/AUTH1: the real shared SyncAuthorityRuntimeHost pending-authorization lifecycle, its safe
// readonly snapshot, the exact-target accounts cancel command and the real command registry/dispatch.
//
// Every provider, browser and token-store input is synthetic: the Dropbox and Google token/identity
// endpoints are served by one in-process HttpMessageHandler and the browser callback is a real GET to
// the product's own registered 127.0.0.1 loopback listener. No provider is contacted, no real account
// is touched, no credential is read or written outside the isolated synthetic stores, and no user data
// is involved. The lifecycle and cancellation decision being proved are the production ones.
internal static class AuthorizationCancellationChecks
{
    private const string FirstCode = "synthetic-code-one";
    private static int _passed;
    private static SyncCliOperationsBase _runtime = null!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Authorization cancellation: " + message);
        _passed++;
        Console.WriteLine("PASS AUTH " + message);
    }

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = instance.StateAuthority;
        var credentials = new SyntheticCredentialStore();
        var googleCredentials = new SyntheticGoogleCredentials();
        using var provider = new SyntheticProvider();
        using var http = new HttpClient(provider);
        var connections = new DropboxConnectionStore(authority);
        await connections.DeleteAsync();
        var googleConnections = new GoogleDriveConnectionStore(authority);
        await googleConnections.DeleteAsync();
        using var endpoint = new GoogleDriveOAuthEndpoint(http, GoogleDriveProviderConfiguration.RegisteredClientId,
            "synthetic-client-secret");
        using var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
            new SyncRuntimeStateStore(authority), connections, credentials,
            new SyncAuthorityDatabase(Path.Combine(root, "authority.db")),
            googleDriveCredentialStore: googleCredentials, googleDriveTokenEndpoint: endpoint);
        await using var runtime = new FixtureRuntime(host, authority);
        _runtime = runtime;

        RegistryMetadata();
        ParserRejections();
        await MissingKeyRefusalAsync();
        await DropboxLifecycleAsync(host, runtime, connections, credentials);
        await CrossProviderAndStaleTargetAsync(host, runtime, connections, credentials);
        await ReauthorizeAsync(host, runtime, connections, credentials, googleConnections, googleCredentials);
        await CancellationWinsRaceAsync(host, runtime, provider, connections, credentials);
        await CommitWinsRaceAsync(host, runtime, provider, connections, credentials);
        await RollbackKeepsStateAsync(host, provider, connections, credentials);
        await CancellationTimeoutAsync(host, runtime);

        Check(_passed >= 40, "the shared lifecycle proof recorded " + _passed + " real assertions");
        Console.WriteLine("PASS AUTH1 GCLI-G3 one pending attempt per host, safe readonly snapshot, exact-target" +
                          " accounts cancel and real registry dispatch");
    }

    // ---- registry metadata: CLIMETA-1 and the two new running-application rows -------------------
    private static void RegistryMetadata()
    {
        var pending = SyncCliRegistry.Rows.Single(row => row.Command == "accounts pending");
        Check(pending.Approval == "none" && pending.Mode == "running-application" && pending.NeedsApplication &&
              !pending.Mutating && !pending.RequiresYes && !pending.LiveVerificationRequired,
            "accounts pending is registered readonly and running-application-only");
        var cancel = SyncCliRegistry.Rows.Single(row => row.Command == "accounts cancel");
        Check(cancel.Approval == "yes" && cancel.Mode == "running-application" && cancel.NeedsApplication &&
              cancel.Mutating && cancel.RequiresYes &&
              cancel.Usage.Contains("--key <attempt-id>", StringComparison.Ordinal) &&
              cancel.Options.Contains("--key", StringComparison.Ordinal),
            "accounts cancel is registered as an exact-target running-application mutation requiring --yes");
        var connect = SyncCliRegistry.Rows.Single(row => row.Command == "accounts connect");
        Check(connect.Approval == "yes" &&
              connect.Effect.Contains("authorization", StringComparison.OrdinalIgnoreCase) &&
              connect.Effect.Contains("verified connection", StringComparison.OrdinalIgnoreCase),
            "accounts connect puts --yes in the approval position and states the verified connection effect");
        var reauthorize = SyncCliRegistry.Rows.Single(row => row.Command == "accounts reauthorize");
        Check(reauthorize.Approval == "yes" &&
              reauthorize.Effect.Contains("verified connection", StringComparison.OrdinalIgnoreCase) &&
              !reauthorize.Effect.Contains("Dropbox", StringComparison.Ordinal) &&
              !reauthorize.Requires.Contains("Dropbox", StringComparison.Ordinal) &&
              !reauthorize.Mode.Contains("Dropbox", StringComparison.Ordinal),
            "accounts reauthorize metadata is provider-neutral because Google Drive is supported too");
        Check(connect.Requires == reauthorize.Requires && connect.Mode == reauthorize.Mode &&
              connect.Mode == "direct|running-application" &&
              connect.Requires == "the product state or the running application and browser consent",
            "both account authorization rows describe their actual prerequisite and runtime");
        var help = SyncCli.HelpText();
        Check(help.Contains("accounts pending", StringComparison.Ordinal) &&
              help.Contains("accounts cancel --key <attempt-id> --yes", StringComparison.Ordinal) &&
              SyncCli.RunningApplicationCommands.Contains("accounts pending") &&
              SyncCli.RunningApplicationCommands.Contains("accounts cancel"),
            "help and the published running-application list carry both new commands");
        var rows = SyncCliRegistry.Capabilities().Commands
            .Where(row => row.Command is "accounts pending" or "accounts cancel").ToArray();
        Check(rows.Length == 2 && rows.All(row => row.Mode == "running-application") &&
              rows.Single(row => row.Command == "accounts pending").Kind == "read" &&
              rows.Single(row => row.Command == "accounts cancel").Kind == "mutating",
            "the capability inventory publishes the two new rows with their real class and mode");
    }

    private static void ParserRejections()
    {
        Check(Refused(["--cli", "accounts", "cancel", "--key", "0123456789abcdef0123456789abcdef"]),
            "accounts cancel without --yes is refused before any product state is opened");
        Check(Refused(["--cli", "accounts", "cancel", "--key", "someone", "--yes", "--account", "an-account"]),
            "accounts cancel refuses an account alias as a cancellation target");
    }

    private static bool Refused(string[] args)
    {
        try { _ = SyncCliRegistry.Parse(args); }
        catch (SyncCliUsageException) { return true; }
        return false;
    }

    // The registry requires --key at dispatch, exactly like the existing cancel family. No attempt and
    // no product state is touched, and the refusal keeps the established usage exit code.
    private static async Task MissingKeyRefusalAsync() =>
        await ExpectCommandRefusalAsync("accounts cancel without an exact attempt key is refused before dispatch",
            "--cli", "accounts", "cancel", "--yes");

    // ---- Dropbox: one pending attempt, safe observation, negatives, exact CLI cancellation, retry --
    private static async Task DropboxLifecycleAsync(SyncAuthorityRuntimeHost host, SyncCliOperationsBase runtime,
        DropboxConnectionStore connections, SyntheticCredentialStore credentials)
    {
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = host.ConnectDropboxAsync(uri => { opened.TrySetResult(uri); return Task.CompletedTask; },
            addAccount: true);
        var authorization = await opened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var snapshot = host.PendingAuthorization;
        Check(snapshot is not null, "the interactive Dropbox authorization registers one pending attempt");
        var attemptId = snapshot!.AttemptId;
        Check(attemptId.Length == 32 && Guid.TryParseExact(attemptId, "N", out _),
            "the pending attempt exposes one ephemeral opaque GUID identity");
        Check(snapshot.Provider == SyncProviders.Dropbox && snapshot.Action == SyncAuthorizationAction.Connect &&
              snapshot.AccountId is null && snapshot.StartedUtc.Offset == TimeSpan.Zero &&
              snapshot.Status == SyncPendingAuthorizationSnapshot.AwaitingAuthorizationPhase,
            "the safe snapshot carries only provider, action, account, UTC start and phase");
        var state = QueryValue(authorization, "state");
        Check(state.Length > 0 && !authorization.AbsoluteUri.Contains(attemptId, StringComparison.Ordinal) &&
              !attemptId.Contains(state, StringComparison.Ordinal),
            "the attempt identity is independent of the OAuth state and the authorization URL");
        Check(!connect.IsCompleted, "the original connect command stays pending while the callback is held");

        var readsBefore = credentials.Reads;
        var requestsBefore = providerRequests(host);
        var view = (SyncCliPendingAuthorizationData)await DispatchAsync(runtime, "--cli", "accounts", "pending", "--json");
        Check(view.Count == 1 && view.Pending is not null && view.Pending.AttemptId == attemptId &&
              view.Pending.Provider == SyncProviders.Dropbox && view.Pending.Action == SyncAuthorizationAction.Connect,
            "accounts pending returns the exact attempt while the original command remains pending");
        Check(credentials.Reads == readsBefore && providerRequests(host) == requestsBefore,
            "accounts pending reads no credential and contacts no provider");
        var projected = JsonSerializer.Serialize(view);
        Check(!projected.Contains(state, StringComparison.Ordinal) &&
              !projected.Contains("code_verifier", StringComparison.OrdinalIgnoreCase) &&
              !projected.Contains("access_token", StringComparison.OrdinalIgnoreCase) &&
              !projected.Contains("refresh_token", StringComparison.OrdinalIgnoreCase) &&
              !projected.Contains(authorization.AbsoluteUri, StringComparison.Ordinal),
            "the pending projection exposes no URL, state, code, verifier or credential value");

        await ExpectCommandRefusalAsync("an unknown attempt identity is refused",
            "--cli", "accounts", "cancel", "--key", "ffffffffffffffffffffffffffffffff", "--yes");
        Check(host.PendingAuthorization?.AttemptId == attemptId,
            "an unknown cancellation target leaves the real pending attempt untouched");
        await ExpectCommandRefusalAsync("a case-aliased attempt identity is not accepted as the exact target",
            "--cli", "accounts", "cancel", "--key", attemptId.ToUpperInvariant(), "--yes");
        Check(host.PendingAuthorization?.AttemptId == attemptId,
            "the mismatched target leaves the real pending attempt untouched");

        var report = (SyncCliControlData)await DispatchAsync(runtime, "--cli", "accounts", "cancel", "--key",
            attemptId, "--yes");
        Check(report.Accepted && report.Result.Contains("was cancelled", StringComparison.Ordinal) &&
              report.Result.Contains("Existing accounts were kept", StringComparison.Ordinal),
            "the exact-target CLI cancellation requests and reports terminal cancellation");
        Check(await CancelledAsync(connect), "the cancelled Dropbox authorization ends with cancellation");
        Check(host.PendingAuthorization is null, "the finally path released the cancelled attempt");
        Check(connections.Load() is null && credentials.Writes == 0,
            "a precommit cancellation keeps existing accounts and credentials unchanged");

        var retry = await CompleteDropboxConnectAsync(host, FirstCode);
        Check(retry.ConnectionId.Length > 0 && connections.LoadActiveBindings().Count == 1 &&
              credentials.Writes == 1 && credentials.Values.Count == 1,
            "a fresh attempt after cancellation succeeds and persists the verified account exactly once");
        Check(host.PendingAuthorization is null, "the successful attempt released its identity");
        await ExpectCommandRefusalAsync("a finished attempt identity is refused",
            "--cli", "accounts", "cancel", "--key", attemptId, "--yes");
        Check(connections.LoadActiveBindings().Count == 1 && credentials.Writes == 1,
            "a stale finished identity cannot disconnect or undo the completed account");
    }

    // ---- one attempt per host across providers, and a late cancel that cannot reach the successor --
    private static async Task CrossProviderAndStaleTargetAsync(SyncAuthorityRuntimeHost host,
        SyncCliOperationsBase runtime, DropboxConnectionStore connections, SyntheticCredentialStore credentials)
    {
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = host.ConnectDropboxAsync(uri => { opened.TrySetResult(uri); return Task.CompletedTask; },
            addAccount: true);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var firstId = host.PendingAuthorization!.AttemptId;
        await ExpectRefusalAsync(() => host.ConnectDropboxAsync(_ => Task.CompletedTask, addAccount: true),
            "a second Dropbox authorization is refused while the first is waiting");
        await ExpectRefusalAsync(() => host.ConnectGoogleDriveAsync(_ => Task.CompletedTask),
            "a Google authorization is refused by the same one-pending-attempt controller");
        Check(host.PendingAuthorization?.AttemptId == firstId,
            "the refused second attempt left the first attempt untouched");
        var cancelFirst = (SyncCliControlData)await DispatchAsync(runtime, "--cli", "accounts", "cancel",
            "--key", firstId, "--yes");
        Check(cancelFirst.Accepted && await CancelledAsync(first) && host.PendingAuthorization is null,
            "the held attempt cancels terminally through the shared command");

        var googleOpened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = host.ConnectGoogleDriveAsync(uri => { googleOpened.TrySetResult(uri); return Task.CompletedTask; });
        await googleOpened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var secondId = host.PendingAuthorization!.AttemptId;
        Check(secondId != firstId, "the successor attempt has its own identity");
        await ExpectCommandRefusalAsync("a late cancel naming the finished attempt A is refused",
            "--cli", "accounts", "cancel", "--key", firstId, "--yes");
        Check(host.PendingAuthorization?.AttemptId == secondId && !second.IsCompleted,
            "the late cancel for A cannot cancel or disturb attempt B");
        var view = (SyncCliPendingAuthorizationData)await DispatchAsync(runtime, "--cli", "accounts", "pending");
        Check(view.Count == 1 && view.Pending!.AttemptId == secondId &&
              view.Pending.Provider == SyncProviders.GoogleDrive,
            "the pending view reports the successor Google attempt while it waits");
        Check(host.RequestAuthorizationAttemptCancellation(secondId),
            "the Connections panel route cancels the exact CLI-started attempt through the shared operation");
        Check(await CancelledAsync(second) && host.PendingAuthorization is null,
            "the CLI-started Google attempt ends cancelled and releases its identity");
        Check(connections.LoadActiveBindings().Count == 1 && credentials.Writes == 1,
            "the second provider's cancellation left the existing account state unchanged");
    }

    // ---- connect and reauthorize for both providers, including a committed reauthorization ---------
    private static async Task ReauthorizeAsync(SyncAuthorityRuntimeHost host, SyncCliOperationsBase runtime,
        DropboxConnectionStore connections, SyntheticCredentialStore credentials,
        GoogleDriveConnectionStore googleConnections, SyntheticGoogleCredentials googleCredentials)
    {
        var existing = connections.LoadActiveBindings().Single();
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reauthorize = host.ReauthorizeDropboxAsync(uri => { opened.TrySetResult(uri); return Task.CompletedTask; },
            connectionId: existing.ConnectionId);
        var authorization = await opened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var snapshot = host.PendingAuthorization;
        Check(snapshot is not null && snapshot.Action == SyncAuthorizationAction.Reauthorize &&
              snapshot.AccountId == existing.ConnectionId,
            "a reauthorization names its exact existing account and the reauthorize action");
        await CallbackAsync(authorization, "synthetic-code-two");
        var rebound = await reauthorize.WaitAsync(TimeSpan.FromSeconds(30));
        Check(rebound.ConnectionId == existing.ConnectionId && credentials.Writes == 2 &&
              connections.LoadActiveBindings().Count == 1,
            "the committed reauthorization kept the same account identity and wrote its credential once");

        var googleBinding = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey("synthetic-permission-one"), "synthetic-permission-one",
            "synthetic@example.invalid", "Synthetic", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        await googleConnections.SaveAsync(googleBinding);
        await googleCredentials.WriteAsync(googleBinding.ConnectionId, "synthetic-google-refresh");
        var googleOpened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var googleReauthorize = host.ConnectGoogleDriveAsync(
            uri => { googleOpened.TrySetResult(uri); return Task.CompletedTask; },
            connectionId: googleBinding.ConnectionId);
        await googleOpened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var googleSnapshot = host.PendingAuthorization;
        Check(googleSnapshot is not null && googleSnapshot.Provider == SyncProviders.GoogleDrive &&
              googleSnapshot.Action == SyncAuthorizationAction.Reauthorize &&
              googleSnapshot.AccountId == googleBinding.ConnectionId,
            "the Google reauthorization names its exact account through the same shared snapshot");
        var cancelled = (SyncCliControlData)await DispatchAsync(runtime, "--cli", "accounts", "cancel", "--key",
            googleSnapshot!.AttemptId, "--yes");
        Check(cancelled.Accepted && await CancelledAsync(googleReauthorize) &&
              googleConnections.Load(googleBinding.ConnectionId) is not null,
            "cancelling the Google reauthorization kept the retained Google account identity");
    }

    // ---- the cancel-versus-commit race, cancellation first -----------------------------------------
    private static async Task CancellationWinsRaceAsync(SyncAuthorityRuntimeHost host, SyncCliOperationsBase runtime,
        SyntheticProvider provider, DropboxConnectionStore connections, SyntheticCredentialStore credentials)
    {
        provider.AccountId = "dbid:AASYNTHETICRACE";
        provider.BlockTokenExchange = true;
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = host.ConnectDropboxAsync(uri => { opened.TrySetResult(uri); return Task.CompletedTask; },
            addAccount: true);
        var authorization = await opened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var attemptId = host.PendingAuthorization!.AttemptId;
        await CallbackAsync(authorization, "synthetic-code-race");
        await provider.TokenExchangeReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var accountsBefore = connections.LoadActiveBindings().Count;
        var writesBefore = credentials.Writes;
        var report = (SyncCliControlData)await DispatchAsync(runtime, "--cli", "accounts", "cancel", "--key",
            attemptId, "--yes");
        provider.ReleaseTokenExchange.TrySetResult(true);
        var cancelled = await CancelledAsync(connect);
        Check(cancelled == report.Accepted,
            "the race report matches the actual terminal outcome of the racing authorization");
        Check(report.Accepted && report.Result.Contains("was cancelled", StringComparison.Ordinal),
            "a cancellation that wins the exchange race is reported as cancellation");
        Check(connections.LoadActiveBindings().Count == accountsBefore && credentials.Writes == writesBefore,
            "the lost race wrote no credential and no account");
        provider.BlockTokenExchange = false;
        provider.AccountId = "dbid:AASYNTHETICONE";
    }

    // ---- the cancel-versus-commit race, commit first -----------------------------------------------
    private static async Task CommitWinsRaceAsync(SyncAuthorityRuntimeHost host, SyncCliOperationsBase runtime,
        SyntheticProvider provider, DropboxConnectionStore connections, SyntheticCredentialStore credentials)
    {
        provider.AccountId = "dbid:AASYNTHETICCOMMIT";
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = host.ConnectDropboxAsync(uri => { opened.TrySetResult(uri); return Task.CompletedTask; },
            addAccount: true);
        var authorization = await opened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var lease = host.CaptureAuthorizationAttempt(host.PendingAuthorization!.AttemptId);
        await CallbackAsync(authorization, "synthetic-code-commit");
        var committed = await connect.WaitAsync(TimeSpan.FromSeconds(30));
        Check(await lease!.Terminal, "a committed authorization publishes a true terminal result to its lease");
        Check(connections.LoadActiveBindings().Any(binding => binding.ConnectionId == committed.ConnectionId),
            "the committed account is retained exactly once");
        Check(SyntheticProvider.RequestCount > 0 && credentials.Writes == 3,
            "the committed authorization wrote its credential once");

        var before = connections.LoadActiveBindings().Count;
        // The exact-target command captures its lease synchronously, requests cancellation and then awaits
        // the captured terminal outcome. A commit that wins that race must be reported as completed.
        var late = host.BeginAuthorizationAttempt(SyncProviders.Dropbox, SyncAuthorizationAction.Reauthorize,
            committed.ConnectionId, CancellationToken.None);
        var lateCommand = DispatchAsync(runtime, "--cli", "accounts", "cancel", "--key", late.AttemptId, "--yes");
        late.MarkCommitted();
        late.Dispose();
        var report = (SyncCliControlData)await lateCommand.WaitAsync(TimeSpan.FromSeconds(30));
        Check(!report.Accepted && report.Result.Contains("completed before the cancellation took effect",
                  StringComparison.Ordinal) &&
              report.Result.Contains("verified connection was kept", StringComparison.Ordinal) &&
              !report.Result.Contains("was cancelled", StringComparison.Ordinal),
            "a commit that wins the race is reported as completed and never as cancelled");
        Check(connections.LoadActiveBindings().Count == before,
            "a cancellation that lost the commit race disconnected and undid nothing");
    }

    // ---- existing rollback behaviour on a failed credential write -----------------------------------
    private static async Task RollbackKeepsStateAsync(SyncAuthorityRuntimeHost host, SyntheticProvider provider,
        DropboxConnectionStore connections, SyntheticCredentialStore credentials)
    {
        provider.AccountId = "dbid:AASYNTHETICROLLBACK";
        credentials.FailNextWrite = true;
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = host.ConnectDropboxAsync(uri => { opened.TrySetResult(uri); return Task.CompletedTask; },
            addAccount: true);
        var authorization = await opened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await CallbackAsync(authorization, "synthetic-code-rollback");
        var accountsBefore = connections.LoadActiveBindings().Count;
        Check(await ExpectRefusalAsync(() => connect) && host.PendingAuthorization is null &&
              connections.LoadActiveBindings().Count == accountsBefore && credentials.Values.Count == accountsBefore,
            "a failed credential write rolls back to the existing accounts and releases the attempt");
        Check(credentials.FailNextWrite == false, "the synthetic failure was consumed exactly once");
        provider.AccountId = "dbid:AASYNTHETICONE";
    }

    // ---- the existing thirty-second command bound, reported as requested and still finishing --------
    private static async Task CancellationTimeoutAsync(SyncAuthorityRuntimeHost host, SyncCliOperationsBase runtime)
    {
        using var stuck = host.BeginAuthorizationAttempt(SyncProviders.Dropbox, SyncAuthorizationAction.Connect,
            null, CancellationToken.None);
        var started = DateTimeOffset.UtcNow;
        var report = (SyncCliControlData)await DispatchAsync(runtime, "--cli", "accounts", "cancel", "--key",
            stuck.AttemptId, "--yes");
        var elapsed = DateTimeOffset.UtcNow - started;
        Check(elapsed >= TimeSpan.FromSeconds(29) && elapsed < TimeSpan.FromSeconds(90),
            "the exact-target cancellation waits the existing thirty-second command pattern");
        Check(report.Accepted && report.Result.Contains("still finishing", StringComparison.Ordinal),
            "a cancellation timeout is reported as cancellation requested and still finishing");
        var observed = host.PendingAuthorization;
        Check(observed?.AttemptId == stuck.AttemptId &&
              observed.Status == SyncPendingAuthorizationSnapshot.CancellationRequestedPhase,
            "the shared snapshot reports the cancellation-requested phase while that attempt is still finishing");
        Check(!report.Result.Contains("was cancelled", StringComparison.Ordinal) &&
              !report.Result.Contains("Existing accounts were kept", StringComparison.Ordinal),
            "a timeout never reports a completed cancellation or a claim about accounts");
    }

    // ---- helpers -----------------------------------------------------------------------------------
    private static Task<object> DispatchAsync(SyncCliOperationsBase runtime, params string[] args) =>
        SyncCliRegistry.DispatchAsync(runtime, SyncCliRegistry.Parse(args), TextWriter.Null, CancellationToken.None);

    private static async Task<bool> ExpectRefusalAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is InvalidOperationException or SyncCliTargetException or
                                          SyncCliUsageException or SyncCliOperationException or
                                          OperationCanceledException or TimeoutException or IOException or
                                          InvalidDataException or SocketException)
        {
            return true;
        }
        return false;
    }

    private static async Task<bool> ExpectRefusalAsync(Func<Task> action, string message)
    {
        var refused = await ExpectRefusalAsync(action);
        Check(refused, message);
        return refused;
    }

    // The real command registry refuses the named command; the refusal is a PASS with its message.
    private static async Task<bool> ExpectCommandRefusalAsync(string message, params string[] args)
    {
        var refused = await ExpectRefusalAsync(async () => { _ = await DispatchAsync(_runtime, args); });
        Check(refused, message);
        return refused;
    }

    private static async Task<bool> CancelledAsync(Task connect)
    {
        try
        {
            await connect;
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private static async Task<DropboxConnectionBinding> CompleteDropboxConnectAsync(SyncAuthorityRuntimeHost host,
        string code)
    {
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = host.ConnectDropboxAsync(uri => { opened.TrySetResult(uri); return Task.CompletedTask; },
            addAccount: true);
        var authorization = await opened.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await CallbackAsync(authorization, code);
        return await connect.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // A real signed callback to the product's own registered loopback listener: the synthetic browser.
    private static async Task CallbackAsync(Uri authorization, string code)
    {
        var redirect = new Uri(QueryValue(authorization, "redirect_uri"));
        var state = QueryValue(authorization, "state");
        var target = redirect.AbsolutePath + "?code=" + Uri.EscapeDataString(code) +
                     "&state=" + Uri.EscapeDataString(state);
        using var client = new TcpClient();
        await client.ConnectAsync(redirect.Host, redirect.Port);
        var request = "GET " + target + " HTTP/1.1\r\nHost: " + redirect.Authority +
                      "\r\nConnection: close\r\n\r\n";
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        await stream.FlushAsync();
        var buffer = new byte[4096];
        try { _ = await stream.ReadAsync(buffer); } catch (IOException) { }
    }

    private static string QueryValue(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(pieces[0]), name, StringComparison.Ordinal))
                return pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }
        throw new InvalidDataException("The authorization URI is missing " + name + ".");
    }

    private static int providerRequests(SyncAuthorityRuntimeHost host) => SyntheticProvider.RequestCount;

    private sealed class FixtureRuntime(SyncAuthorityRuntimeHost host, PilotStateAuthority state) : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => host;
        protected override PilotStateAuthority StateAuthority => state;
        // The running application's own operations surface: the CLI reaches this process through the bridge.
        public override string Mode => "running-application";
    }

    private sealed class SyntheticProvider : HttpMessageHandler
    {
        internal static int RequestCount;
        internal string AccountId = "dbid:AASYNTHETICONE";
        internal readonly string RootNamespaceId = "10000001";
        internal readonly string HomeNamespaceId = "10000002";
        internal readonly TaskCompletionSource<bool> TokenExchangeReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> ReleaseTokenExchange =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool BlockTokenExchange;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Interlocked.Increment(ref RequestCount);
            if (uri.Host == "api.dropboxapi.com" && uri.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal))
            {
                TokenExchangeReached.TrySetResult(true);
                if (BlockTokenExchange) await ReleaseTokenExchange.Task.WaitAsync(cancellationToken);
                return Json(new Dictionary<string, object?>
                {
                    ["access_token"] = "synthetic-access-token",
                    ["refresh_token"] = "synthetic-refresh-token",
                    ["token_type"] = "Bearer",
                    ["expires_in"] = 14400,
                    ["scope"] = string.Join(' ', DropboxProviderConfiguration.RequestedScopes(true)),
                    ["account_id"] = AccountId
                });
            }
            if (uri.Host == "api.dropboxapi.com" &&
                uri.AbsolutePath.EndsWith("/users/get_current_account", StringComparison.Ordinal))
                return Json(new Dictionary<string, object?>
                {
                    ["account_id"] = AccountId,
                    ["name"] = new Dictionary<string, object?> { ["display_name"] = "Synthetic Holder" },
                    ["email"] = "synthetic@example.invalid",
                    ["account_type"] = new Dictionary<string, object?> { [".tag"] = "basic" },
                    ["root_info"] = new Dictionary<string, object?>
                    {
                        ["root_namespace_id"] = RootNamespaceId,
                        ["home_namespace_id"] = HomeNamespaceId
                    }
                });
            if (uri.Host == "oauth2.googleapis.com")
                return Json(new Dictionary<string, object?>
                {
                    ["access_token"] = "synthetic-google-access",
                    ["refresh_token"] = "synthetic-google-refresh-new",
                    ["token_type"] = "Bearer",
                    ["expires_in"] = 3600,
                    ["scope"] = GoogleDriveProviderConfiguration.DriveScope
                });
            if (uri.Host == "www.googleapis.com" && uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal))
                return Json(new Dictionary<string, object?>
                {
                    ["user"] = new Dictionary<string, object?>
                    {
                        ["permissionId"] = "synthetic-permission-one",
                        ["emailAddress"] = "synthetic@example.invalid",
                        ["displayName"] = "Synthetic"
                    },
                    ["storageQuota"] = new Dictionary<string, object?> { ["limit"] = "1000", ["usage"] = "10" }
                });
            throw new InvalidOperationException("No synthetic provider response is registered for " +
                                                uri.GetLeftPart(UriPartial.Path) + ".");
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

    private sealed class SyntheticCredentialStore : IDropboxRefreshTokenStore
    {
        private readonly Store _store;
        private readonly string _scope;

        internal SyntheticCredentialStore() : this(new Store(), "primary") { }
        private SyntheticCredentialStore(Store store, string scope) { _store = store; _scope = scope; }

        internal Dictionary<string, string> Values => _store.Values;
        internal int Reads => _store.Reads;
        internal int Writes => _store.Writes;
        internal bool FailNextWrite { get => _store.FailNextWrite; set => _store.FailNextWrite = value; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _store.Reads++;
            return Task.FromResult(_store.Values.TryGetValue(_scope, out var value) ? value : null);
        }

        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
            if (_store.FailNextWrite)
            {
                _store.FailNextWrite = false;
                throw new IOException("Synthetic credential write failure.");
            }
            _store.Values[_scope] = refreshToken;
            _store.Writes++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            _store.Values.Remove(_scope);
            return Task.CompletedTask;
        }

        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) =>
            new SyntheticCredentialStore(_store, binding.CredentialReference ?? string.Empty);

        private sealed class Store
        {
            internal readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
            internal int Reads;
            internal int Writes;
            internal bool FailNextWrite;
        }
    }

    private sealed class SyntheticGoogleCredentials : IGoogleDriveRefreshTokenStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string?> ReadAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.TryGetValue(connectionId, out var value) ? value : null);
        }

        public Task WriteAsync(string connectionId, string refreshToken, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
            _values[connectionId] = refreshToken;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            _values.Remove(connectionId);
            return Task.CompletedTask;
        }
    }
}
