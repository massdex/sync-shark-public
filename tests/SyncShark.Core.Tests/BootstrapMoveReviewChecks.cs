using System.Text.Json;
using Massdex.SyncShark;

// Real reviewed-host/CLI/planner/executor, real local files and retained authority.
// Only provider input is fake; the HTTP handler rejects every network request.
internal static class BootstrapMoveReviewChecks
{
    internal static async Task RunAsync(string root)
    {
        CliChecks.RunPresentationChecks();
        await CloudProAuthorityPreflight.AssertDropboxRevisionChurnMoveGuardsAsync(Path.Combine(root, "guards"));
        await RunDirectionAsync(Path.Combine(root, "remote-moved"), false);
        await RunDirectionAsync(Path.Combine(root, "local-moved"), true);
        await RunBaselineUpdatesAsync(Path.Combine(root, "baseline-updates"));
        Console.WriteLine("PASS reviewed moves in both directions preserve originals, replacement names and no-replay");
    }

    internal static async Task RunBaselineUpdatesAsync(string root)
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        var remote = new FakeSyncRemoteStore();
        string[] names = ["remote-change.txt", "local-change.txt", "late-edit.txt", "both-change.txt", "steady.txt"];
        foreach (var name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(localRoot, name), "shared original " + name);
            remote.PutWithId(name, "shared original " + name, "id:" + name);
        }
        await File.WriteAllTextAsync(Path.Combine(localRoot, "unbased.txt"), "independent local file");
        remote.PutWithId("unbased.txt", "independent cloud file", "id:unbased");
        remote.PutWithId("hold.txt", "not approved yet", "id:hold");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var state = instance.StateAuthority;
        var connections = new DropboxConnectionStore(state);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:baseline-review", "baseline-root", "baseline-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await connections.SaveNewAsync(binding);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using var http = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(state, http, new SyncPairStore(state),
            new SyncRuntimeStateStore(state), connections, new FirstSyncEffectChecks.FixtureCredentialStore(),
            database, (_, _) => remote);
        await host.LoadPairsAsync();
        using var lease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(localRoot, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/BaselineReview", "id:baseline-review"), "Baseline review", binding.RootNamespaceId) with
        {
            ConnectionId = binding.ConnectionId,
            InitialReviewApproved = false,
            // Replacements require the same retained recovery binding as a fully authored pair.
            // A migrated create-only fixture must not bypass that production safeguard.
            RecoveryRoot = new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                "id:baseline-recovery", binding.RootNamespaceId)
        };
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(pair, select: true);
        var operations = new FirstSyncEffectChecks.FixtureOperations(host, state);
        static void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Baseline review: " + label);
            Console.WriteLine("PASS " + label);
        }
        async Task<(int Exit, string Output, string Error)> Cli(params string[] arguments)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await SyncCli.ExecuteAsync(arguments, output, error, () => operations);
            return (exit, output.ToString(), error.ToString());
        }
        await host.RefreshBootstrapInventoryAsync(pair.Id);
        await host.RefreshBootstrapInventoryAsync(pair.Id);
        var points = await database.LoadLatestSynchronizationPointsAsync(pair.Id);
        Require(names.All(name => points.Any(point => point.LocalRelativePath == name && point.DropboxRelativePath == name)),
            "real initial observation establishes shared synchronized baselines");
        remote.Put("remote-change.txt", "cloud update");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "local-change.txt"), "local update");
        remote.Put("late-edit.txt", "reviewed cloud change");
        remote.Put("both-change.txt", "cloud competing edit");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "both-change.txt"), "local competing edit");
        await host.RefreshBootstrapInventoryAsync(pair.Id);
        var page = await host.LoadBootstrapReviewPageAsync(pair.Id);
        Require(page.LocalUpdatedItems == 1 && page.DropboxUpdatedItems == 2 && page.ConflictItems == 2,
            "actual review separates one-sided baseline updates from both-edited and unbased conflicts");
        foreach (var (path, group, action) in new[]
        {
            ("remote-change.txt", SyncBootstrapActions.RemoteUpdatedGroup, "Download"),
            ("local-change.txt", SyncBootstrapActions.LocalUpdatedGroup, "Upload"),
            ("late-edit.txt", SyncBootstrapActions.RemoteUpdatedGroup, "Download")
        })
        {
            var row = page.Items.Single(item => item.RelativePath == path);
            Require(row.Group == group && row.Recommendation == action && row.Effect.StartsWith("Update on ") &&
                    SyncBootstrapActions.For(row.Group).SequenceEqual([action, "Skip for now"]),
                "shared GUI recommendation/action policy admits only the proven update direction: " + path);
        }
        var emptyPage = await host.LoadBootstrapReviewPageAsync(pair.Id, search: "no-such-file");
        Require(emptyPage.Items.Count == 0 && emptyPage.LocalUpdatedItems == 1 &&
                emptyPage.DropboxUpdatedItems == 2 && emptyPage.ConflictItems == 2,
            "the empty-page summary preserves update and conflict totals");
        var filtered = await host.LoadBootstrapReviewPageAsync(pair.Id, group: SyncBootstrapActions.RemoteUpdatedGroup);
        Require(filtered.Items.Count == 2 && filtered.Items.All(row => row.Recommendation == "Download"),
            "update filter returns the actual admitted rows");
        var plan = await Cli("--cli", "plan", "--pair", pair.Id, "--json");
        using (var doc = JsonDocument.Parse(plan.Output))
        {
            var data = doc.RootElement.GetProperty("data");
            Require(plan.Exit == 0 && data.GetProperty("localUpdatedItems").GetInt32() == 1 &&
                    data.GetProperty("dropboxUpdatedItems").GetInt32() == 2 &&
                    data.GetProperty("conflictItems").GetInt32() == 2,
                "real CLI and shared GUI page expose identical baseline-update totals");
        }
        var invalid = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id, "--yes", "--json",
            "--allow-recommended", "--choices", "{\"remote-change.txt\":\"upload\"}");
        Require(invalid.Exit != 0 && remote.UploadCount == 0 && remote.DownloadCount == 0,
            "wrong-direction overwrite rejected before any effect");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "late-edit.txt"), "new local work after review");
        var choices = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["__plan"] = page.LocalScanGenerationId!, ["__remote_plan"] = page.DropboxScanGenerationId!,
            ["hold.txt"] = "no-change"
        });
        var applied = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id, "--yes", "--json",
            "--allow-recommended", "--choices", choices);
        using (var doc = JsonDocument.Parse(applied.Output))
        {
            var data = doc.RootElement.GetProperty("data");
            Require(data.GetProperty("appliedItems").GetInt32() == 2 &&
                    data.GetProperty("uploaded").GetInt32() == 1 && data.GetProperty("downloaded").GetInt32() == 1,
                "real executor completes both independent safe updates despite a late competing edit: " + applied.Error);
        }
        Require(await File.ReadAllTextAsync(Path.Combine(localRoot, "remote-change.txt")) == "cloud update" &&
                remote.ReadText("local-change.txt") == "local update" && remote.UploadCount == 1 && remote.DownloadCount == 1,
            "both directions use the real conditional update executors exactly once");
        Require(await File.ReadAllTextAsync(Path.Combine(localRoot, "late-edit.txt")) == "new local work after review" &&
                remote.ReadText("late-edit.txt") == "reviewed cloud change" &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "both-change.txt")) == "local competing edit" &&
                remote.ReadText("both-change.txt") == "cloud competing edit" &&
                !File.Exists(Path.Combine(localRoot, "hold.txt")),
            "concurrent edits, genuine conflicts and explicit skips preserve both sides");
        var before = (remote.UploadCount, remote.DownloadCount);
        await Cli("--cli", "first-sync", "apply", "--pair", pair.Id, "--yes", "--json", "--allow-recommended",
            "--choices", "{\"hold.txt\":\"no-change\"}");
        Require(before == (remote.UploadCount, remote.DownloadCount),
            "continuing first sync never replays the successful updates or resolves retained conflicts");
    }

    private static async Task RunDirectionAsync(string root, bool localMoved)
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        const string original = "Original file, moved without changing its contents.";
        const string replacement = "Different file now occupying the original name.";
        await File.WriteAllTextAsync(Path.Combine(localRoot, "old.txt"), original);
        var remote = new FakeSyncRemoteStore();
        remote.PutWithId("old.txt", original, "id:original");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var state = instance.StateAuthority;
        var connections = new DropboxConnectionStore(state);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:move-review", "move-root", "move-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        await connections.SaveNewAsync(binding);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using var http = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(state, http, new SyncPairStore(state),
            new SyncRuntimeStateStore(state), connections, new FirstSyncEffectChecks.FixtureCredentialStore(),
            database, (_, _) => remote);
        await host.LoadPairsAsync();
        using var lease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(localRoot, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/MoveReview", "id:move-review"), "Move review", binding.RootNamespaceId) with
        { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
        await database.RetainPairDefinitionAsync(pair, select: true);
        var operations = new FirstSyncEffectChecks.FixtureOperations(host, state);
        static void Require(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Reviewed move: " + label);
            Console.WriteLine("PASS " + label);
        }
        async Task<(int Exit, string Output, string Error)> Cli(params string[] arguments)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await SyncCli.ExecuteAsync(arguments, output, error, () => operations);
            return (exit, output.ToString(), error.ToString());
        }
        await host.RefreshBootstrapInventoryAsync(pair.Id);
        await host.RefreshBootstrapInventoryAsync(pair.Id);
        Require((await database.LoadLatestSynchronizationPointsAsync(pair.Id)).Count == 1,
            "initial inventories establish one real identical-file baseline");
        if (localMoved)
        {
            File.Move(Path.Combine(localRoot, "old.txt"), Path.Combine(localRoot, "moved.txt"));
            await File.WriteAllTextAsync(Path.Combine(localRoot, "old.txt"), replacement);
        }
        else
        {
            remote.Move("old.txt", "moved.txt", churnRevision: true);
            remote.PutWithId("old.txt", replacement, "id:replacement");
        }
        await host.RefreshBootstrapInventoryAsync(pair.Id);
        var page = await host.LoadBootstrapReviewPageAsync(pair.Id);
        var move = page.Items.Single(item => item.RelativePath == "moved.txt");
        Require((await database.LoadLatestSynchronizationPointsAsync(pair.Id)).Count == 1 &&
                (await database.LoadOpenConflictIdsAsync(pair.Id)).Count == 0 &&
                remote.UploadCount == 0 && remote.DownloadCount == 0 && remote.MoveCount == 0,
            "a replacement at the old name does not abort review, merge identities or create a cross-object conflict/effect");
        var kind = localMoved ? SyncAuthorityOperationKind.MoveRemote : SyncAuthorityOperationKind.MoveLocal;
        Require(move.Move?.Kind == kind && move.Move.FromPath == "old.txt" &&
                move.Recommendation == move.Move.Action && move.Effect.Contains("old.txt → moved.txt"),
            "real review names the exact original, replacement location and move direction");
        Require(SyncBootstrapActions.For(move.Group, move.Move).SequenceEqual([move.Move!.Action, "Skip for now"]),
            "the shared GUI choice policy offers the reviewed move or skip, not an accidental copy");
        var oldCopy = new SyncBootstrapActionPlan(pair.Id, page.LocalScanGenerationId!, page.DropboxScanGenerationId!,
            [new(move.RelativePath, move.Group, localMoved ? "Upload" : "Download")]);
        var oldResult = await host.RunBootstrapActionPlanAsync(oldCopy);
        Require(oldResult.Result.Blocked > 0 && oldResult.AppliedItems == 0 && remote.UploadCount == 0 && remote.DownloadCount == 0 &&
                remote.MoveCount == 0,
            "old copy consent cannot become move authority and retains a per-file refusal");
        Require(!oldResult.FirstSyncComplete && oldResult.Result.Decisions.Any(item =>
                item.RelativePath == "moved.txt" && item.Kind == SyncDecisionKind.Blocked &&
                item.Reason.Contains("explicit move", StringComparison.Ordinal)) &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "old.txt")) == (localMoved ? replacement : original),
            "the refused move row explains the required choice and preserves the predecessor without approving first sync");
        var invalid = oldCopy with
        { Choices = [new(move.RelativePath, move.Group, move.Move.Action, move.Move with { FromPath = "wrong.txt" })] };
        var refused = false;
        try { await host.RunBootstrapActionPlanAsync(invalid); }
        catch (InvalidDataException) { refused = true; }
        Require(refused && remote.MoveCount == 0, "a forged old path cannot grant a move");
        var recommended = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id,
            "--yes", "--allow-recommended", "--json");
        Require(recommended.Exit != 0 && recommended.Error.Contains("move", StringComparison.OrdinalIgnoreCase) &&
                remote.MoveCount == 0 && remote.UploadCount == 0 && remote.DownloadCount == 0,
            "the existing CLI copy recommendation flag cannot silently consent to a move");
        var choices = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["__plan"] = page.LocalScanGenerationId!, ["__remote_plan"] = page.DropboxScanGenerationId!,
            ["moved.txt"] = "move", ["old.txt"] = "no-change"
        });
        var choicesFile = Path.Combine(root, "reviewed choices.json");
        // The document is deliberately larger than a Windows command line; only
        // its short, space-containing file path crosses the CLI argument boundary.
        await File.WriteAllTextAsync(choicesFile, new string(' ', 9 * 1024 * 1024) + choices);
        var malformedFile = Path.Combine(root, "malformed-choices.json");
        await File.WriteAllTextAsync(malformedFile, "{\"unfinished\":");
        var duplicateFile = Path.Combine(root, "duplicate-choices.json");
        await File.WriteAllTextAsync(duplicateFile, "{\"moved.txt\":\"no-change\",\"moved.txt\":\"move\"}");
        var oversizedFile = Path.Combine(root, "oversized-choices.json");
        await using (var oversized = File.Create(oversizedFile)) oversized.SetLength(SyncCliOperationsBase.MaximumFirstSyncChoicesBytes + 1L);
        foreach (var rejectedInput in new[] { Path.Combine(root, "missing-choices.json"), root, malformedFile, duplicateFile, oversizedFile })
        {
            var rejected = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id,
                "--yes", "--choices", rejectedInput, "--json");
            Require(rejected.Exit != 0 && remote.MoveCount == 0 && remote.UploadCount == 0 &&
                    remote.DownloadCount == 0 &&
                    await File.ReadAllTextAsync(Path.Combine(localRoot, "old.txt")) == (localMoved ? replacement : original),
                "invalid choices file is rejected before any selected file effect: " + Path.GetFileName(rejectedInput));
        }
        var choicesArgument = localMoved ? choices : choicesFile;
        var applied = await Cli("--cli", "first-sync", "apply", "--pair", pair.Id,
            "--yes", "--choices", choicesArgument, "--json");
        Require(applied.Exit == 0,
            localMoved ? "inline JSON move approval remains compatible" : "large choices file with spaces reaches the real CLI move executor");
        using (var appliedJson = JsonDocument.Parse(applied.Output))
        {
            var receipt = appliedJson.RootElement.GetProperty("data");
            Require(receipt.GetProperty("appliedItems").GetInt32() == 1 &&
                    receipt.GetProperty("uploaded").GetInt32() == 0 &&
                    receipt.GetProperty("downloaded").GetInt32() == 0 &&
                    receipt.GetProperty("blocked").GetInt32() == 0 &&
                    receipt.GetProperty("files").GetArrayLength() == 1 &&
                    receipt.GetProperty("files")[0].GetProperty("kind").GetInt32() == (int)SyncDecisionKind.Recovered,
                "real CLI counts the successful reviewed move as one applied action without inventing a transfer");
        }
        Require(File.Exists(Path.Combine(localRoot, "moved.txt")) && remote.Exists("moved.txt") &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "moved.txt")) == original &&
                remote.ReadText("moved.txt") == original && remote.UploadCount == 0 && remote.DownloadCount == 0 &&
                remote.MoveCount == (localMoved ? 1 : 0),
            "explicit CLI move uses the real conditional executor without copying either file: " + applied.Error);
        Require(localMoved ? !remote.Exists("old.txt") : !File.Exists(Path.Combine(localRoot, "old.txt")),
            "move removes only the predecessor membership and does not invent replacement consent");
        var approvedPair = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        Require(approvedPair.InitialReviewApproved == true,
            "the completed finite move review saves approval without an implicit full rescan");
        await host.RefreshBootstrapInventoryAsync(pair.Id);
        var following = await host.LoadBootstrapReviewPageAsync(pair.Id);
        var next = following.Items.Single(item => item.RelativePath == "old.txt");
        Require(next.Move is null && next.Group == (localMoved ? "Local only" : "Dropbox only"),
            "fresh inventory offers the separate replacement as its own copy");
        var copy = await host.RunPairsAsync([approvedPair], "Explicit ordinary sync after move review");
        var copyResult = copy.Items.Single().Result ?? throw new InvalidOperationException(
            "The ordinary replacement sync did not return a retained result.");
        Require(copyResult is { Blocked: 0, Conflicts: 0, Failed: 0, NeedsVerification: 0 } &&
                await File.ReadAllTextAsync(Path.Combine(localRoot, "old.txt")) == replacement &&
                remote.ReadText("old.txt") == replacement && remote.UploadCount + remote.DownloadCount == 1,
            "explicit ordinary sync copies the replacement while keeping moved original bytes intact");
        Require(copyResult.AppliedItems == 1,
            "the replacement receipt counts only its own copy, not the earlier completed move");
        var before = (remote.MoveCount, remote.UploadCount, remote.DownloadCount);
        await Cli("--cli", "first-sync", "apply", "--pair", pair.Id, "--yes", "--choices", choices, "--json");
        Require(before == (remote.MoveCount, remote.UploadCount, remote.DownloadCount),
            "replaying the completed review performs no second move or copy");
    }
}
