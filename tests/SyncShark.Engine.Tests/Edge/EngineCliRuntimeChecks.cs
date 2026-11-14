using System.Net;
using System.Reflection;
using System.Text.Json;
using Massdex.SyncShark.DropboxCli;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Edge;

/// <summary>Bug #115: native Dropbox verbs run on the engine surface. A standalone EngineCliRuntime opens the one
/// product authority only on first Dropbox access and releases it on dispose, without starting a host or AutoSync;
/// a host-bound instance borrows the host's authority and owns only its own native client.</summary>
public static class EngineCliRuntimeChecks
{
    private const string AccountId = "account-fixture";
    private const string RootNamespace = "root-namespace-fixture";
    private const string HomeNamespace = "home-namespace-fixture";
    private const string EntryName = "a.txt";

    private const string TokenJson =
        "{\"access_token\":\"token-fixture\",\"expires_in\":14400,\"account_id\":\"" + AccountId + "\"}";

    // The refreshing token source verifies the refreshed identity against the retained connection before any call.
    private const string IdentityJson =
        "{\"account_id\":\"" + AccountId + "\",\"root_info\":{\".tag\":\"user\",\"root_namespace_id\":\"" +
        RootNamespace + "\",\"home_namespace_id\":\"" + HomeNamespace + "\"}}";

    private const string PageJson =
        "{\"entries\":[{\".tag\":\"file\",\"name\":\"" + EntryName + "\",\"path_display\":\"/" + EntryName +
        "\",\"path_lower\":\"/" + EntryName + "\",\"id\":\"id-fixture\",\"rev\":\"r1\",\"size\":5," +
        "\"content_hash\":\"hash-fixture\",\"client_modified\":\"2026-10-01T00:00:00Z\"," +
        "\"server_modified\":\"2026-10-02T00:00:00Z\"}],\"cursor\":\"c1\",\"has_more\":false}";

    public static void Run()
    {
        WithProductRoot(NativeAccessNeedsNoHost);
        WithProductRoot(ConstructionTakesNoLock);
        WithProductRoot(HeldAuthorityIsBusy);
        WithProductRoot(FailedConstructionReleasesOwnedResources);
        WithProductRoot(HostBoundBorrowsAuthority);
        WithProductRoot(ObsoleteLegacyStoreRefusesBeforeAnyProviderCall);
        WithProductRoot(StandaloneStatusCarriesRetainedNotice);
    }

    // B-227 R1: an absent-host status reports the retained terminal-startup notice in human and JSON form.
    private static void StandaloneStatusCarriesRetainedNotice(string root)
    {
        foreach (var exit in new[]
                 {
                     (int)SyncCliExitCode.StartupReleaseRecoveryRequired,
                     (int)SyncCliExitCode.StartupInvalidDataLocation,
                     (int)SyncCliExitCode.StartupLegacyFootprintRefused
                 })
        {
            AutoSyncProcess.RecordExistingOwnerNotice(AutoSyncProcess.PersistentStartupNoticePath,
                SyncCli.PersistentStartupRefusalNoticeKey(exit));
            var expected = File.ReadAllText(AutoSyncProcess.PersistentStartupNoticePath);
            // The one read the app edge performs before constructing the standalone runtime.
            var notice = AutoSyncProcess.ReadPersistentStartupNotice();
            var output = new StringWriter();
            var error = new StringWriter();
            var exitCode = SyncCli.ExecuteAsync(["--cli", "engine", "host", "status"], output, error,
                () => new EngineCliRuntime(80, notice)).GetAwaiter().GetResult();
            Check.Require(exitCode == (int)SyncCliExitCode.Success,
                "absent-host status stays a successful read for exit " + exit);
            Check.Require(output.ToString().Contains(expected, StringComparison.Ordinal),
                "absent-host status prints the retained notice for exit " + exit);

            var json = new StringWriter();
            exitCode = SyncCli.ExecuteAsync(["--cli", "engine", "host", "status", "--json"], json, error,
                () => new EngineCliRuntime(80, notice)).GetAwaiter().GetResult();
            using var parsed = JsonDocument.Parse(json.ToString());
            var data = parsed.RootElement.GetProperty("data");
            Check.Require(exitCode == (int)SyncCliExitCode.Success &&
                data.GetProperty("state").GetString() == "StatusUnavailable" &&
                data.GetProperty("message").GetString()!.Contains(expected, StringComparison.Ordinal),
                "absent-host JSON status also carries the retained notice for exit " + exit);
        }
    }

    // 1A's admission owner refuses an obsolete legacy store on the standalone path, before any provider call.
    private static void ObsoleteLegacyStoreRefusesBeforeAnyProviderCall(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "v2"));
        File.WriteAllText(Path.Combine(root, "v2", "state.db"), string.Empty);
        using var wire = RouteWire();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = SyncCli.ExecuteAsync(
            ["--cli", "dropbox", "ls", "--account", AccountId, "--path", "/", "--json"],
            output, error, () => new EngineCliRuntime
            {
                DropboxHandlerFactory = () => wire,
                DropboxCredentialsFactory = () => new SyntheticCredentials()
            }).GetAwaiter().GetResult();
        Check.Require(exit == (int)SyncCliExitCode.NeedsReview,
            "an obsolete legacy store refuses the standalone Dropbox path: " + error);
        Check.Require(error.ToString().Contains(EngineStartupAdmission.RefusalMessage, StringComparison.Ordinal),
            "the refusal is the typed admission message, not a provider failure");
        Check.Require(wire.Requests.Count == 0, "the typed refusal precedes every provider call");
        Check.Require(!LockHeld(), "the refused standalone access releases the product authority");
    }

    private static void NativeAccessNeedsNoHost(string root)
    {
        var binding = SeedBinding(root);
        var before = Entries(root);
        using var wire = RouteWire();
        var tracking = new TrackingHandler(wire);
        var runtime = new EngineCliRuntime
        {
            DropboxHandlerFactory = () => tracking,
            DropboxCredentialsFactory = () => new SyntheticCredentials()
        };

        var first = runtime.DropboxAccounts;
        Check.Require(ReferenceEquals(first, runtime.DropboxAccounts),
            "repeated Dropbox access reuses the one native client");
        Check.Require(LockHeld(), "the first Dropbox access takes the exclusive product authority");

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = SyncCli.ExecuteAsync(
            ["--cli", "dropbox", "ls", "--account", binding.ConnectionId, "--path", "/", "--json"],
            output, error, () => runtime).GetAwaiter().GetResult();
        Check.Require(exit == (int)SyncCliExitCode.Success, "a standalone Dropbox ls succeeds with no host: " + error);
        using var parsed = JsonDocument.Parse(output.ToString());
        var page = parsed.RootElement.GetProperty("data");
        Check.Require(page.GetProperty("entries")[0].GetProperty("name").GetString() == EntryName,
            "the native ls returns the provider page");
        Check.Require(wire.Requests.Count == 3 && wire.Requests[0].Route == "/oauth2/token" &&
            wire.Requests[1].Route == "/2/users/get_current_account" &&
            wire.Requests[2].Route == "/2/files/list_folder",
            "one protected-credential refresh, its identity verification, then one route call");
        Check.Require(tracking.Disposed, "disposing the runtime releases its own native client");
        Check.Require(!LockHeld(), "disposing the runtime releases the exclusive product authority");
        Check.Require(Entries(root).SequenceEqual(before),
            "native access starts no host or AutoSync and opens no engine store");
    }

    private static void ConstructionTakesNoLock(string root)
    {
        _ = new EngineCliRuntime();
        Check.Require(!LockHeld(), "constructing the engine runtime takes no product authority");
        using var held = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = SyncCli.ExecuteAsync(["--cli", "dropbox", "routes"], output, error,
            () => new EngineCliRuntime()).GetAwaiter().GetResult();
        Check.Require(exit == (int)SyncCliExitCode.Success,
            "a catalog-only Dropbox verb needs no authority even while another owner holds it: " + error);
        Check.Require(output.ToString().Contains("files/list_folder", StringComparison.Ordinal),
            "the route table still renders from the catalog");
    }

    private static void HeldAuthorityIsBusy(string root)
    {
        var binding = SeedBinding(root);
        using var held = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        using var wire = RouteWire();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = SyncCli.ExecuteAsync(
            ["--cli", "dropbox", "ls", "--account", binding.ConnectionId, "--path", "/", "--json"],
            output, error, () => new EngineCliRuntime
            {
                DropboxHandlerFactory = () => wire,
                DropboxCredentialsFactory = () => new SyntheticCredentials()
            }).GetAwaiter().GetResult();
        Check.Require(exit == (int)SyncCliExitCode.Busy, "a held authority returns the truthful Busy refusal");
        Check.Require(error.ToString().Contains("exclusive product state lock", StringComparison.Ordinal),
            "the Busy refusal explains the exclusive lock instead of hiding it");
        Check.Require(wire.Requests.Count == 0, "a Busy refusal sends no Dropbox request");
    }

    private static void FailedConstructionReleasesOwnedResources(string root)
    {
        var handlerFailure = new EngineCliRuntime
        {
            DropboxHandlerFactory = () => throw new InvalidOperationException("handler-failure")
        };
        Check.Require(Throws(() => _ = handlerFailure.DropboxAccounts),
            "a handler construction failure surfaces instead of a silent fallback");
        handlerFailure.DisposeAsync().GetAwaiter().GetResult();
        Check.Require(!LockHeld(), "a handler construction failure still releases the owned authority");

        var tracking = new TrackingHandler(RouteWire());
        var credentialFailure = new EngineCliRuntime
        {
            DropboxHandlerFactory = () => tracking,
            DropboxCredentialsFactory = () => throw new InvalidOperationException("credential-failure")
        };
        Check.Require(Throws(() => _ = credentialFailure.DropboxAccounts),
            "a credential construction failure surfaces instead of a silent fallback");
        Check.Require(!tracking.Disposed, "the owned client stays alive until the runtime is disposed");
        credentialFailure.DisposeAsync().GetAwaiter().GetResult();
        Check.Require(tracking.Disposed, "a credential construction failure still releases the owned client");
        Check.Require(!LockHeld(), "a credential construction failure still releases the owned authority");
    }

    private static void HostBoundBorrowsAuthority(string root)
    {
        var binding = SeedBinding(root);
        using var borrowed = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        using var wire = RouteWire();
        var tracking = new TrackingHandler(wire);
        var operations = new EngineOperations(root, static _ => { });
        var runtime = new EngineCliRuntime(operations, borrowed.StateAuthority)
        {
            DropboxHandlerFactory = () => tracking,
            DropboxCredentialsFactory = () => new SyntheticCredentials()
        };

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = SyncCli.ExecuteAsync(
            ["--cli", "dropbox", "ls", "--account", binding.ConnectionId, "--path", "/", "--json"],
            output, error, () => runtime).GetAwaiter().GetResult();
        Check.Require(exit == (int)SyncCliExitCode.Success,
            "the host-bound runtime answers through the borrowed authority: " + error);
        Check.Require(tracking.Disposed, "the host-bound runtime still owns and disposes its native client");
        Check.Require(LockHeld(), "the host-bound runtime never releases the borrowed authority");
        Check.Require(new DropboxConnectionStore(borrowed.StateAuthority).Load(binding.ConnectionId) is not null,
            "the borrowed host authority stays usable after the runtime is disposed");

        Check.Require(runtime.DropboxCliSettings.HttpTimeout == TimeSpan.FromMinutes(10),
            "the Dropbox timeout stays the shared native default");
        using var defaults = (HttpClientHandler)new EngineCliRuntime().DropboxHandlerFactory();
        Check.Require(!defaults.AllowAutoRedirect, "the native handler still refuses redirects");
    }

    private static DropboxConnectionBinding SeedBinding(string root)
    {
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, AccountId,
            RootNamespace, HomeNamespace, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        using var instance = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding).GetAwaiter().GetResult();
        return binding;
    }

    private static DropboxRouteWire RouteWire()
    {
        var wire = new DropboxRouteWire();
        wire.Respond("/oauth2/token", HttpStatusCode.OK, TokenJson);
        wire.Respond("users/get_current_account", HttpStatusCode.OK, IdentityJson);
        wire.Respond("files/list_folder", HttpStatusCode.OK, PageJson);
        return wire;
    }

    private static bool LockHeld()
    {
        try
        {
            using var probe = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
            return false;
        }
        catch (IOException) { return true; }
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception) { return true; }
    }

    private static string[] Entries(string root) =>
        Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    private static void WithProductRoot(Action<string> body)
    {
        var root = Directory.CreateTempSubdirectory("sync-shark-engine-cli-runtime-").FullName;
        try
        {
            using var productRoot = OverrideProductRoot(root);
            body(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Core's test hook redirects the product root to a temporary root; Engine.Tests is not on Core's
    // InternalsVisibleTo list.
    private static IDisposable OverrideProductRoot(string root) =>
        (IDisposable)typeof(SyncSharkRuntimePaths)
            .GetMethod("OverrideProductRootForTest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { root })!;

    /// <summary>Lets the runtime dispose its client chain while the check keeps reading the recorded wire.</summary>
    private sealed class TrackingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
