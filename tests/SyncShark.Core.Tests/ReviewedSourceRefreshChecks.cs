using System.Text.Json;
using Massdex.SyncShark;

internal static class ReviewedSourceRefreshChecks
{
    internal static async Task RunAsync(string root)
    {
        foreach (var scenario in new[] { "success", "identity", "destination", "second-change" })
        {
            var fixture = Path.Combine(root, scenario);
            var local = Path.Combine(fixture, "local");
            Directory.CreateDirectory(Path.Combine(local, "folder"));
            var remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
            remote.PutDirectory("folder");
            remote.Put("folder/a.json", "original-a");
            remote.Put("folder/b.json", "original-b");
            remote.Put("peer.zip", "unchanged-peer");
            using var instance = PilotInstanceLock.Acquire(Path.Combine(fixture, "state", "instance.lock"));
            var state = instance.StateAuthority;
            var connections = new DropboxConnectionStore(state);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "dbid:source-refresh", "refresh-root", "refresh-home", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes).Normalize();
            await connections.SaveNewAsync(binding);
            var database = new SyncAuthorityDatabase(Path.Combine(fixture, "authority.db"));
            using var http = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
            using var host = new SyncAuthorityRuntimeHost(state, http, new SyncPairStore(state),
                new SyncRuntimeStateStore(state), connections, new FirstSyncEffectChecks.FixtureCredentialStore(),
                database, (_, _) => remote);
            await host.LoadPairsAsync();
            using var rootLease = PilotDirectoryLease.Open(local);
            var pair = SyncPair.FromPilot(new PilotSettings(local, rootLease.Identity.VolumeSerialNumber,
                rootLease.Identity.FileId, "/SourceRefresh", "id:source-refresh"), "Source refresh", binding.RootNamespaceId)
                with { ConnectionId = binding.ConnectionId, InitialReviewApproved = false, PropagateDeletions = false };
            await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
                "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
            await database.RetainPairDefinitionAsync(pair, select: true);
            var operations = new FirstSyncEffectChecks.FixtureOperations(host, state);
            async Task<(int Exit, JsonDocument Result, string Progress)> Cli(params string[] args)
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                var exit = await SyncCli.ExecuteAsync(args, output, error, () => operations);
                return (exit, JsonDocument.Parse(output.ToString()), error.ToString());
            }
            var scan = await Cli("--cli", "--json", "scan", "--pair", pair.Id, "--yes");
            using var scanResult = scan.Result;
            if (scan.Exit != 0) throw new InvalidOperationException(scan.Progress);
            var choices = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["__plan"] = scanResult.RootElement.GetProperty("data").GetProperty("localScanGenerationId").GetString(),
                ["__remote_plan"] = scanResult.RootElement.GetProperty("data").GetProperty("dropboxScanGenerationId").GetString()
            });
            var readCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            remote.BeforeReadEntryForTest = async (path, token) =>
            {
                var count = readCounts.GetValueOrDefault(path) + 1;
                readCounts[path] = count;
                if (path is "folder/a.json" or "folder/b.json" && count == 1)
                {
                    if (path == "folder/a.json" && scenario == "identity")
                        remote.PutWithId(path, "identity-replaced", "id:not-approved");
                    else remote.Put(path, "refreshed-" + path);
                    if (path == "folder/a.json" && scenario == "destination")
                        await File.WriteAllTextAsync(Path.Combine(local, "folder", "a.json"), "keep-new-local", token);
                }
                if (path == "folder/a.json" && scenario == "second-change" && count == 3)
                    remote.Put(path, "changed-again");
            };
            // Completion concerns the finite approved work, not a new whole-pair
            // inventory of future arrivals. Refresh must use targeted metadata;
            // completed effect/readback evidence allows approval without rescanning.
            remote.BeforeListEntriesForTest = _ =>
            {
                throw new InvalidOperationException("A reviewed source refresh or its completion enumerated the whole pair again.");
            };
            var applied = await Cli("--cli", "--json", "first-sync", "apply", "--pair", pair.Id,
                "--choices", choices, "--allow-recommended", "--yes", "--timeout-seconds", "30");
            using var appliedResult = applied.Result;
            var expectedExit = scenario == "success" ? 0 : (int)SyncCliExitCode.NeedsReview;
            if (applied.Exit != expectedExit)
                throw new InvalidOperationException(scenario + ": expected CLI exit " + expectedExit +
                    ", actual " + applied.Exit + ": " + applied.Progress);
            var data = appliedResult.RootElement.GetProperty("data");
            var retainedPair = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
            var peerFinished = File.Exists(Path.Combine(local, "peer.zip")) &&
                File.ReadAllText(Path.Combine(local, "peer.zip")) == "unchanged-peer";
            var refreshedPeerFinished = File.Exists(Path.Combine(local, "folder", "b.json")) &&
                File.ReadAllText(Path.Combine(local, "folder", "b.json")) == "refreshed-folder/b.json";
            Console.WriteLine(JsonSerializer.Serialize(new { scenario, peerFinished, refreshedPeerFinished,
                remote.ListEntriesCount, remote.DownloadCount, readCounts,
                firstSyncComplete = data.GetProperty("firstSyncComplete").GetBoolean(),
                persistedApproval = retainedPair.InitialReviewApproved,
                refreshProgress = applied.Progress.Contains("Same-identity source version", StringComparison.Ordinal) }));
            if (!peerFinished || !refreshedPeerFinished || remote.ListEntriesCount != 1 ||
                remote.DownloadCount != (scenario == "success" ? 3 : 2) ||
                data.GetProperty("firstSyncComplete").GetBoolean() != (scenario == "success") ||
                retainedPair.InitialReviewApproved != (scenario == "success") ||
                !applied.Progress.Contains("Same-identity source version", StringComparison.Ordinal))
                throw new InvalidOperationException("Scoped refresh/count/boundary proof failed for " + scenario + ": " + applied.Progress);
            if (scenario == "success" && File.ReadAllText(Path.Combine(local, "folder", "a.json")) != "refreshed-folder/a.json")
                throw new InvalidOperationException("Refreshed bytes were not committed.");
            if (scenario == "destination" && File.ReadAllText(Path.Combine(local, "folder", "a.json")) != "keep-new-local")
                throw new InvalidOperationException("A changed destination was overwritten.");
            if (scenario is "identity" or "second-change" && File.Exists(Path.Combine(local, "folder", "a.json")))
                throw new InvalidOperationException("An unapproved identity or second source change was applied.");
            Console.WriteLine("PASS actual CLI scoped source refresh " + scenario +
                ": exactly one initial inventory, targeted refresh and no whole-pair rescan; exact successful peer bytes and persisted approval; unchanged identity/destination/one-refresh fences");
        }
    }
}
