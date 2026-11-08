using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;

// These cases execute the real CLI dispatcher, binding/storage authority and retained stores.
// Only HTTP responses and application-effect callbacks are fixture inputs. No live account,
// Windows credential store, Cloud Files registration, installed app or user state is touched.
internal static class CliSurfaceChecks
{
    private const string Account = "dbid:cli-surface-fixture";
    private const string Namespace = "cli-surface-root";
    private static int _checks;
    private static readonly List<string> Failures = [];

    internal static async Task<int> RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Binding checks need Windows NTFS.");
        if (Directory.Exists(root)) throw new InvalidOperationException("Surface fixture root must be new.");
        Directory.CreateDirectory(root);
        _checks = 0; Failures.Clear();
        try
        {
            await Case("pair policy and replacement", PairChecks);
            await Case("retained staging ownership", ControlBindingChecks);
            await Case("persisted storage policy", StorageChecks);
            await Case("settings transactional effects", SettingsChecks);
            await Case("conflict and recovery pages", CatalogChecks);
            Console.WriteLine(JsonSerializer.Serialize(new { result = Failures.Count == 0 ? "PASS" : "FAIL",
                assertions = _checks, failures = Failures,
                boundary = "Controlled HTTP and fixture NTFS only; native Cloud Files activation and installed-app effects are not exercised." }));
            return Failures.Count == 0 ? 0 : 1;
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }

        async Task Case(string name, Func<Fixture, Task> action)
        {
            try { await action(await Fixture.Create(Path.Combine(root, (Failures.Count + _checks).ToString()))); }
            catch (Exception error) { Failures.Add(name + ": " + error.GetType().Name + ": " + error.Message); Console.WriteLine("FAIL " + Failures[^1]); }
        }
    }

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) { Failures.Add(name); Console.WriteLine("FAIL " + name); }
    }

    private static JsonObject Success(Result result, string name)
    {
        if (result.Exit != 0) throw new InvalidOperationException(name + " exited " + result.Exit + ": " + result.Output);
        var envelope = JsonNode.Parse(result.Output)!.AsObject();
        Check(envelope["ok"]!.GetValue<bool>(), name + " success envelope");
        return envelope["data"]!.AsObject();
    }

    private static async Task PairChecks(Fixture f)
    {
        var pair = await f.Pair();
        var shown = Success(await f.Run("pairs", "show", "--pair", pair.Id), "pair metadata");
        Check(shown["recoveryRoot"]?["rootPath"]?.GetValue<string>() == SyncDropboxArtifactPaths.RecoveryContainerPath &&
            shown["defaultStorage"]?.GetValue<string>() == "AlwaysAvailable" && shown["selectionRules"]!.AsArray().Count == 1 &&
            shown["ignoreRules"]!.AsArray().Count == 1 && shown["directoryStorageRules"]!.AsArray().Count == 1,
            "pair show includes complete retained recovery, selection, ignore and storage metadata");

        var basic = Success(await f.Run("pairs", "edit", "--pair", pair.Id, "--priority", "-99", "--yes"), "negative priority");
        Check(basic["priority"]!.GetValue<int>() == -99, "negative priority is parsed and retained");
        pair = await f.Pair();
        var before = JsonSerializer.Serialize(pair);
        var calls = f.Handler.Calls.Count;
        foreach (var args in new[] {
            new[] { "--account", "unknown", "--generation", pair.PolicyRevision.ToString(), "--yes" },
            new[] { "--account", f.OtherConnection, "--generation", pair.PolicyRevision.ToString(), "--yes" },
            new[] { "--account", f.Connection, "--generation", (pair.PolicyRevision + 1).ToString(), "--yes" },
            new[] { "--account", f.Connection, "--generation", pair.PolicyRevision.ToString() } })
        {
            var refusal = await f.Run(["pairs", "edit", "--pair", pair.Id, "--settings", "{\"direction\":\"UploadOnly\"}", ..args]);
            Check(refusal.Exit != 0, "pair edit exact account/revision/confirmation rejection");
            Check(!refusal.Progress.Contains("committed pair definition read back successfully", StringComparison.Ordinal),
                "refused policy edit never reports a successful commit");
        }
        Check(JsonSerializer.Serialize(await f.Pair()) == before && f.Handler.Calls.Count == calls, "pair refusals change neither retained policy nor provider state");
        var badPriority = await f.Run("pairs", "edit", "--pair", pair.Id, "--priority", "-101", "--yes");
        Check(badPriority.Exit == 2, "out of range negative priority rejected");

        var priorControl = pair.ControlNamespace;
        var providerWaitChecks = 0;
        f.ObserveProgressAtProvider = (route, text) =>
        {
            if (route != "files/get_metadata") return;
            providerWaitChecks++;
            Check(text.Contains("Pair save: asking Dropbox to verify", StringComparison.Ordinal) ||
                text.Contains("Pair save: preparing the selected provider's folder browser", StringComparison.Ordinal),
                "actual awaited provider metadata request has already emitted its binding step");
        };
        var editedResult = await f.MutatePair("edit", pair,
            "--settings", "{\"direction\":\"DownloadOnly\",\"propagateDeletions\":true,\"recoveryRetentionDays\":90,\"defaultStorage\":\"offline\"}");
        f.ObserveProgressAtProvider = null;
        var edited = Success(editedResult, "full policy binding");
        Check(providerWaitChecks >= 2 &&
            editedResult.Progress.Contains("Startup: checking the retained database schema", StringComparison.Ordinal) &&
            editedResult.Progress.Contains("Pair save: registering the verified root bindings", StringComparison.Ordinal) &&
            editedResult.Progress.Contains("committed pair definition read back successfully", StringComparison.Ordinal),
            "policy edit emits initialization, real provider waits, database registration and verified readback");
        Check(edited["direction"]!.GetValue<string>() == "DownloadOnly" && edited["propagateDeletions"]!.GetValue<bool>() &&
            edited["recoveryRetentionDays"]!.GetValue<int>() == 90, "policy fields persisted through genuine binding authority");
        pair = await f.Pair();
        Check(pair.ControlNamespace == priorControl, "same-root policy save preserves exact local control-directory and marker identities");
        Check(pair.SelectionRules!.Single().FolderPath == "excluded" && pair.IgnoreRules!.Single().Value == ".tmp" &&
            pair.DirectoryStorageRules!.Single().FolderPath == "kept", "policy edit preserves unrequested selection/ignore/storage");

        var replacementRoot = Path.Combine(f.Path, "replacement");
        Directory.CreateDirectory(Path.Combine(replacementRoot, "kept"));
        var replaced = Success(await f.MutatePair("edit", pair, "--local", replacementRoot, "--path", "/Replacement"), "exact root replacement");
        var replacementId = replaced["id"]!.GetValue<string>();
        Check(replacementId != pair.Id && f.Handler.Calls.Contains("files/search/continue_v2"), "root replacement derives new pair identity and reaches later exact folder-search page");
        var all = await f.Pairs();
        Check(all.Pairs.Count == 1 && all.Pairs[0].Id == replacementId && all.Pairs[0].Direction == SyncMode.DownloadOnly &&
            all.Pairs[0].SelectionRules!.Count == 1 && all.Pairs[0].IgnoreRules!.Count == 1,
            "replacement retires original binding and preserves copied policy");
        Check(File.Exists(Path.Combine(pair.LocalRoot, "user.txt")), "replacement preserves original local user file");
        pair = all.Pairs[0];
        var duplicateRoot = Path.Combine(f.Path, "duplicate");
        Directory.CreateDirectory(Path.Combine(duplicateRoot, "kept"));
        var duplicate = Success(await f.MutatePair("duplicate", pair, "--name", "Copied policy", "--local", duplicateRoot,
            "--path", "/Duplicate"), "duplicate policy");
        Check(duplicate["id"]!.GetValue<string>() != pair.Id && (await f.Pairs()).Pairs.Count == 2 &&
            duplicate["direction"]!.GetValue<string>() == "DownloadOnly" && duplicate["recoveryRetentionDays"]!.GetValue<int>() == 90,
            "duplicate creates independent binding with copied policy");
        var same = await f.MutatePair("duplicate", pair, "--name", "Not allowed", "--local", pair.LocalRoot, "--path", pair.DropboxRoot);
        Check(same.Exit != 0 && (await f.Pairs()).Pairs.Count == 2, "duplicate same roots refused without retained extra pair");
        var retireId = duplicate["id"]!.GetValue<string>();
        var userFile = Path.Combine(duplicateRoot, "retained-user-file.txt");
        await File.WriteAllTextAsync(userFile, "Retirement must not remove user data.");
        var retired = Success(await f.Run("pairs", "retire", "--pair", retireId, "--yes"), "retire exact pair");
        var replay = Success(await f.Run("pairs", "retire", "--pair", retireId, "--yes"), "repeat exact retirement");
        Check(retired["isRetired"]!.GetValue<bool>() && replay["isRetired"]!.GetValue<bool>() &&
              !(await f.Pairs()).Pairs.Any(item => item.Id == retireId) &&
              await File.ReadAllTextAsync(userFile) == "Retirement must not remove user data.",
            "retirement replay returns the retained terminal identity without removing user files");
        Check((await f.Run("pairs", "retire", "--pair", "not-a-retained-pair", "--yes")).Exit != 0,
            "retirement idempotency does not turn an unknown identity into success");

        // The duplicate command now advertises and parses the policy settings document the shared
        // BindEditedPairAsync already consumes, so a new Parent binding can receive its full exclusion
        // policy in the one atomic shared binding instead of being created and then edited.
        var sourceRevision = pair.PolicyRevision;
        var sourceDirection = pair.Direction;
        var policyRoot = Path.Combine(f.Path, "duplicate-policy");
        Directory.CreateDirectory(Path.Combine(policyRoot, "kept"));
        var policyDuplicate = Success(await f.MutatePair("duplicate", pair, "--name", "Duplicate with policy",
            "--local", policyRoot, "--path", "/DuplicatePolicy", "--settings",
            "{\"direction\":\"UploadOnly\",\"propagateDeletions\":false,\"selectionDefaultIncluded\":false," +
            "\"selectionRules\":[{\"folderPath\":\"excluded\",\"included\":false}]," +
            "\"ignoreRules\":[{\"kind\":\"FileName\",\"value\":\"skip.tmp\"}]}"),
            "duplicate policy settings");
        var policyId = policyDuplicate["id"]!.GetValue<string>();
        Check(policyId != pair.Id && policyDuplicate["direction"]!.GetValue<string>() == "UploadOnly" &&
            policyDuplicate["propagateDeletions"]!.GetValue<bool>() == false,
            "duplicate --settings binds different roots with the requested direction and deletion policy");
        var policyPair = (await f.Pairs()).Pairs.Single(item => item.Id == policyId);
        Check(policyPair.SelectionDefaultIncluded == false &&
            policyPair.SelectionRules is [{ FolderPath: "excluded", Included: false }] &&
            policyPair.IgnoreRules is [{ Value: "skip.tmp" }],
            "duplicate --settings persists the exact requested exclusion selection and ignore policy");
        var sourceAfter = (await f.Pairs()).Pairs.Single(item => item.Id == pair.Id);
        Check(sourceAfter.PolicyRevision == sourceRevision && sourceAfter.Direction == sourceDirection &&
            JsonSerializer.Serialize(sourceAfter) == JsonSerializer.Serialize(pair),
            "duplicate --settings leaves the source pair identity, revision and policy unchanged");
        var unknownMember = await f.MutatePair("duplicate", pair, "--name", "Rejected member",
            "--local", Path.Combine(f.Path, "duplicate-unknown"), "--path", "/DuplicateUnknown",
            "--settings", "{\"unknownPolicy\":1}");
        Check(unknownMember.Exit != 0 && (await f.Pairs()).Pairs.Count == 2 &&
            (await f.Pairs()).Pairs.Single(item => item.Id == pair.Id).PolicyRevision == sourceRevision,
            "duplicate --settings refuses an unknown policy member without adding a pair");
        var malformed = await f.MutatePair("duplicate", pair, "--name", "Rejected json",
            "--local", Path.Combine(f.Path, "duplicate-malformed"), "--path", "/DuplicateMalformed",
            "--settings", "not-json");
        Check(malformed.Exit != 0 && (await f.Pairs()).Pairs.Count == 2,
            "duplicate --settings refuses malformed JSON without adding a pair");
        var badDirection = await f.MutatePair("duplicate", pair, "--name", "Rejected direction",
            "--local", Path.Combine(f.Path, "duplicate-direction"), "--path", "/DuplicateDirection",
            "--settings", "{\"direction\":\"Sideways\"}");
        Check(badDirection.Exit != 0 && (await f.Pairs()).Pairs.Count == 2,
            "duplicate --settings refuses an invalid direction without adding a pair");
        Check(SyncCliRegistry.Rows.Single(row => row.Command == "pairs duplicate").Usage
                .Contains("--settings", StringComparison.Ordinal),
            "the duplicate command advertises its policy settings option in the registry usage");
    }

    private static async Task ControlBindingChecks(Fixture f)
    {
        var pair = await f.Pair();
        var control = pair.ControlNamespace!;
        await using (var runtime = new Runtime(f.Path, f.Handler))
            await runtime.Engine.Authority.RetainPairDefinitionAsync(pair with {
                ControlNamespace = control with { InstallationId = Guid.NewGuid().ToString("N") }
            }, true);
        var wrongOwner = await f.MutatePair("edit", pair, "--settings", "{\"direction\":\"UploadOnly\"}");
        Check(wrongOwner.Exit != 0 && wrongOwner.Output.Contains("owner", StringComparison.OrdinalIgnoreCase),
            "same-root edit rejects a different retained installation owner");
        Check((await f.Pair()).PolicyRevision == pair.PolicyRevision, "wrong owner refusal does not advance retained policy");
        await using (var runtime = new Runtime(f.Path, f.Handler))
        {
            await runtime.Engine.Authority.RetainPairDefinitionAsync(pair, true);
            var refused = false;
            try { await runtime.Engine.Authority.RetainPairDefinitionAsync(pair with {
                ControlNamespace = control with { NamespaceId = "another-namespace" }
            }, true); }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "real retained-pair authority rejects a foreign namespace before it can be saved");
        }
        var marker = System.IO.Path.Combine(control.Local.RootPath, SyncControlNamespace.MarkerName);
        var bytes = await File.ReadAllBytesAsync(marker);
        using (var held = SyncLocalFileAuthority.OpenExistingExclusive(marker)) held.DeleteOnClose();
        var missing = await f.MutatePair("edit", pair, "--settings", "{\"direction\":\"UploadOnly\"}");
        Check(missing.Exit != 0 && !File.Exists(marker) && (await f.Pair()).PolicyRevision == pair.PolicyRevision,
            "missing owner marker rejects edit without recreating or adopting the directory");
        await File.WriteAllBytesAsync(marker, bytes);
        var replaced = await f.MutatePair("edit", pair, "--settings", "{\"direction\":\"UploadOnly\"}");
        Check(replaced.Exit != 0 && (await f.Pair()).PolicyRevision == pair.PolicyRevision,
            "same marker bytes with replaced physical identity cannot authorize same-root edit");
    }

    private static async Task StorageChecks(Fixture f)
    {
        var pair = await f.Pair();
        var overview = Success(await f.Run("storage", "show", "--pair", pair.Id, "--account", f.Connection), "storage overview");
        Check(overview["pair"]?["policyRevision"]?.GetValue<long>() == pair.PolicyRevision &&
            overview["knownSyncedFiles"]?.GetValue<long>() == 0, "storage overview uses real retained size and revision");
        var refusedAvailability = await f.Run("availability", "set", "--pair", pair.Id,
            "--path", "user.txt", "--availability", "offline", "--yes");
        Check(refusedAvailability.Exit != 0 && refusedAvailability.Progress.Contains("Startup:", StringComparison.Ordinal) &&
            (await f.Pair()).PolicyRevision == pair.PolicyRevision,
            "CLI availability reports real startup before its unmanaged-pair refusal without changing the policy");
        await using (var runtime = new Runtime(f.Path, f.Handler))
        {
            var progress = new List<SyncAuthorityBatchProgress>();
            var refusedPending = await runtime.Engine.ApplyPendingDirectoryStorageAsync(pair.Id,
                onProgress: entry => { progress.Add(entry); return Task.CompletedTask; });
            Check(pair.DirectoryStorageApplyPaths!.SequenceEqual(new[] { "kept" }) &&
                refusedPending.FilesChanged == 0 && refusedPending.Failures.Count == 2 &&
                refusedPending.Failures.All(failure => failure.Contains("Enable Windows Files On-Demand", StringComparison.Ordinal)) &&
                progress.Count == 5 && progress.Count(entry => entry.Comparison!.Detail.Contains("Enable Windows Files On-Demand", StringComparison.Ordinal)) == 2 &&
                progress[0].Comparison!.Detail.Contains("1 pending paths", StringComparison.Ordinal) &&
                progress[^1].Comparison!.Detail.Contains("1 paths remain pending", StringComparison.Ordinal),
                "actual existing pending-folder input is refused without native activation and reports retained intent rather than claiming a no-op");
            // Separate legitimate no-work input from the earlier pending-but-disabled input.
            // The real host still performs every progress/persistence decision below.
            await runtime.Engine.Authority.RetainPairDefinitionAsync(pair with { DirectoryStorageApplyPaths = [] }, true);
            progress.Clear();
            var noPending = await runtime.Engine.ApplyPendingDirectoryStorageAsync(pair.Id,
                onProgress: entry => { progress.Add(entry); return Task.CompletedTask; });
            Check(noPending.FilesChanged == 0 && noPending.DirectoriesChanged == 0 && noPending.Failures.Count == 0 &&
                progress.Count == 3 && progress[0].Comparison!.Detail.Contains("0 pending paths", StringComparison.Ordinal) &&
                progress[1].Comparison!.Detail.StartsWith("Saving the exact remaining", StringComparison.Ordinal) &&
                progress[2].Comparison!.Detail.StartsWith("Storage application finished", StringComparison.Ordinal),
                "actual pending-storage host reports its no-op, persistence wait and real completion through the shared retained stream");
        }
        // Exercise native-path inspection without registering or connecting a sync root. An
        // ordinary NTFS file is not an owned placeholder and must never be pinned/dehydrated.
        var userPath = System.IO.Path.Combine(pair.LocalRoot, "user.txt");
        var originalBytes = await File.ReadAllBytesAsync(userPath);
        using (var provider = new WindowsCloudFilesProvider(pair with { FilesOnDemandEnabled = true },
                   new FakeSyncRemoteStore(), f.Path))
        {
            var progress = new List<SyncComparisonProgress>();
            var unchanged = await provider.SetAvailabilityAsync("user.txt", true, recursive: false,
                onProgress: entry => { progress.Add(entry); return Task.CompletedTask; });
            Check(unchanged.FilesChanged == 0 && unchanged.DirectoriesChanged == 0 && unchanged.Failures.Count == 0 &&
                !provider.IsConnected && !provider.RegisteredByThisInstance &&
                (await File.ReadAllBytesAsync(userPath)).SequenceEqual(originalBytes),
                "actual availability inspection preserves unowned NTFS bytes with no Cloud Files activation or effect");
            Check(progress.All(entry => entry.RelativePath == "user.txt") &&
                progress.Single(entry => entry.Detail.StartsWith("Inspecting Windows", StringComparison.Ordinal)).CompletedChecks == 0 &&
                progress.Single(entry => entry.Detail.StartsWith("This is not a placeholder", StringComparison.Ordinal)).CompletedChecks == 1 &&
                progress[^1].TotalChecks == 1 && progress[^1].Detail.StartsWith("Storage availability finished", StringComparison.Ordinal),
                "actual per-path progress identifies inspection before its result and advances only after the ownership check completes");
            var failedSink = await provider.SetAvailabilityAsync("user.txt", false, recursive: false,
                onProgress: _ => throw new InvalidOperationException("Fixture optional presentation failure."));
            Check(failedSink.FilesChanged == 0 && failedSink.Failures.Count == 0 &&
                (await File.ReadAllBytesAsync(userPath)).SequenceEqual(originalBytes),
                "optional diagnostic callback failure cannot replace native-path inspection result or mutate unowned bytes");
        }
        var noEffects = f.Handler.Calls.Count;
        foreach (var command in new[] { "default", "root", "folders", "file", "enable", "reset-file" })
        {
            var extras = command switch {
                "folders" => new[] { "--settings", "[\"kept\"]", "--availability", "offline" },
                "reset-file" => new[] { "--path", "missing.txt", "--key", "987654" },
                "enable" => Array.Empty<string>(),
                "file" => new[] { "--path", "user.txt", "--availability", "offline" },
                _ => new[] { "--availability", "offline" } };
            var stale = await f.Run(["storage", command, "--pair", pair.Id, "--account", f.Connection,
                "--generation", (pair.PolicyRevision + 1).ToString(), ..extras, "--yes"]);
            var wrong = await f.Run(["storage", command, "--pair", pair.Id, "--account", "unknown",
                "--generation", pair.PolicyRevision.ToString(), ..extras, "--yes"]);
            var another = await f.Run(["storage", command, "--pair", pair.Id, "--account", f.OtherConnection,
                "--generation", pair.PolicyRevision.ToString(), ..extras, "--yes"]);
            Check(stale.Exit != 0 && wrong.Exit != 0 && another.Exit != 0 &&
                stale.Progress.Contains("Startup:", StringComparison.Ordinal) &&
                wrong.Progress.Contains("Startup:", StringComparison.Ordinal) &&
                another.Progress.Contains("Startup:", StringComparison.Ordinal),
                command + " storage stale revision/unknown and differently bound identities refused");
        }
        Check((await f.Pair()).PolicyRevision == pair.PolicyRevision && f.Handler.Calls.Count == noEffects,
            "all storage stale and wrong-account paths leave policy/provider untouched");
        var invalid = await f.Storage("folders", pair, "--settings", "[\"kept\",\"KEPT\"]", "--availability", "inherit");
        Check(invalid.Exit != 0 && (await f.Pair()).PolicyRevision == pair.PolicyRevision, "batch duplicate targets rejected without effect");
        var excluded = await f.Storage("folders", pair, "--settings", "[\"excluded\"]", "--availability", "inherit");
        Check(excluded.Exit != 0 && (await f.Pair()).PolicyRevision == pair.PolicyRevision, "storage cannot turn excluded folders into participants");
        var inheritedStorage = await f.Storage("folders", pair, "--settings", "[\"kept\"]", "--availability", "inherit");
        Success(inheritedStorage, "remove directory override");
        Check(inheritedStorage.Progress.Contains("Verifying the selected account", StringComparison.Ordinal) &&
            inheritedStorage.Progress.Contains("Saving the verified storage preference", StringComparison.Ordinal),
            "actual CLI managed-folder success describes account verification and the retained preference write before native activation is considered");
        var inherited = await f.Pair();
        Check(inherited.PolicyRevision == pair.PolicyRevision + 1 && inherited.DirectoryStorageRules!.Count == 0 &&
            !inherited.FilesOnDemandEnabled && inherited.SelectionRules!.SequenceEqual(pair.SelectionRules!),
            "directory inheritance persists one revision without altering sync selection or activating Cloud Files");

        // Input models a vanished file with a previously retained override; reset must not target a replacement.
        await using (var runtime = new Runtime(f.Path, f.Handler))
            await runtime.Engine.Authority.RetainPairDefinitionAsync(inherited with {
                FileStorageRules = [new("missing.txt", SyncDirectoryStorage.AlwaysAvailable, 987654, "id:old-file")]
            }, true);
        var files = Success(await f.Run("storage", "files", "--pair", inherited.Id, "--account", f.Connection,
            "--limit", "1"), "file override list");
        Check(files["items"]!.AsArray().Count == 1 && files["items"]![0]!["localFileId"]!.GetValue<ulong>() == 987654,
            "file override read exposes exact reset identity");
        var wrongFile = await f.Storage("reset-file", inherited, "--path", "missing.txt", "--key", "123");
        Check(wrongFile.Exit != 0 && (await f.Pair()).FileStorageRules!.Count == 1, "wrong file identity does not reset neighboring preference");
        Success(await f.Storage("reset-file", inherited, "--path", "missing.txt", "--key", "987654"), "file override reset");
        var reset = await f.Pair();
        Check(reset.FileStorageRules!.Count == 0 && reset.PolicyRevision == inherited.PolicyRevision + 1 &&
            !File.Exists(Path.Combine(reset.LocalRoot, "missing.txt")), "real reset removes exact vanished-file preference without recreating content");
    }

    private static async Task SettingsChecks(Fixture f)
    {
        var initial = Success(await f.Run("settings", "show"), "initial settings");
        var invalid = await f.Run("settings", "set", "--settings",
            "{\"automation\":{\"intervalMinutes\":17},\"transfer\":{\"maximumConcurrentPairs\":\"bad\"}}", "--yes");
        Check(invalid.Exit != 0 && JsonNode.DeepEquals(initial, Success(await f.Run("settings", "show"), "unchanged invalid settings")),
            "invalid later section never commits earlier automation section");
        var changedStartup = !initial["automation"]!["startWithWindows"]!.GetValue<bool>();
        var startup = await f.Run("settings", "set", "--settings", JsonSerializer.Serialize(new { automation = new { startWithWindows = changedStartup } }), "--yes");
        Check(startup.Exit == 5 && JsonNode.DeepEquals(initial, Success(await f.Run("settings", "show"), "direct startup refusal")),
            "direct changed startup registration fails before persistence");
        var changed = Success(await f.Run("settings", "set", "--settings",
            "{\"interface\":{\"compactRows\":true,\"notificationSound\":true},\"automation\":{\"intervalMinutes\":17}}", "--yes"), "new stored interface preferences");
        Check(changed["interface"]!["compactRows"]!.GetValue<bool>() && changed["interface"]!["notificationSound"]!.GetValue<bool>() &&
            changed["effectsApplied"]!.GetValue<bool>() == false && changed["effectsNote"]!.GetValue<string>().Contains("next application launch"),
            "stored preferences do not claim app effects occurred");
        var saved = Success(await f.Run("settings", "show"), "saved preferences");
        f.AppEffects = true; f.FailEffects = true;
        var failed = await f.Run("settings", "set", "--settings",
            "{\"automation\":{\"intervalMinutes\":31},\"interface\":{\"compactRows\":false}}", "--yes");
        Check(failed.Exit != 0 && f.BeforeCount == 1 && f.CompleteCount == 1 && f.ApplyCount == 2,
            "effect failure runs exact rollback callback and releases lifecycle gate");
        Check(JsonNode.DeepEquals(saved, Success(await f.Run("settings", "show"), "rollback retained settings")),
            "effect failure restores all exact previous retained settings");
        f.FailEffects = false;
        Success(await f.Run("settings", "set", "--settings", "{\"interface\":{\"compactRows\":false}}", "--yes"), "next app settings save");
        Check(f.BeforeCount == 2 && f.CompleteCount == 2, "settings lifecycle is reusable after failed effects");
        f.RejectBefore = true;
        var before = Success(await f.Run("settings", "show"), "precheck snapshot");
        var refused = await f.Run("settings", "set", "--settings", "{\"automation\":{\"intervalMinutes\":47}}", "--yes");
        Check(refused.Exit != 0 && f.CompleteCount == 2 && JsonNode.DeepEquals(before, Success(await f.Run("settings", "show"), "precheck refusal")),
            "failed pre-save precheck does not save or release an unacquired gate");
    }

    private static async Task CatalogChecks(Fixture f)
    {
        var pair = await f.Pair();
        var localText = "local fixture text\n";
        var remoteText = "Dropbox fixture text\n";
        f.Handler.Text = Encoding.UTF8.GetBytes(remoteText);
        await File.WriteAllTextAsync(Path.Combine(pair.LocalRoot, "item0501.txt"), localText);
        var localHash = await DropboxContentHash.ComputeAsync(new MemoryStream(Encoding.UTF8.GetBytes(localText)));
        f.Handler.Hash = await DropboxContentHash.ComputeAsync(new MemoryStream(f.Handler.Text));
        string reviewedLocalIdentity;
        var reviewedLocalPath = Path.Combine(pair.LocalRoot, "item0501.txt");
        using (var stream = File.OpenRead(reviewedLocalPath))
        {
            if (!PilotDirectoryLease.GetFileInformationByHandle(stream.SafeFileHandle, out var identity))
                throw new IOException("The conflict fixture could not bind its real local file identity.");
            reviewedLocalIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.File,
                identity.VolumeSerialNumber, ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow,
                File.GetLastWriteTimeUtc(reviewedLocalPath));
        }
        await using (var runtime = new Runtime(f.Path, f.Handler))
        {
            var db = runtime.Engine.Authority;
            var localRoot = await db.LoadActiveRootBindingIdAsync(pair.Id, "local");
            var remoteRoot = await db.LoadActiveRootBindingIdAsync(pair.Id, "dropbox");
            await db.InitializeAsync((await db.LoadInstallationIdAsync())!, "surface-fixture-profile", "fixture", "{}");
            for (var i = 0; i < 502; i++)
            {
                var id = i.ToString("D4"); var path = "item" + id + ".txt";
                await db.RegisterObjectAsync("object" + id, "file");
                await db.RegisterObjectVersionAsync("local" + id, "object" + id, "local",
                    i == 501 ? reviewedLocalIdentity : "{}", localHash, Encoding.UTF8.GetByteCount(localText));
                await db.RegisterObjectVersionAsync("remote" + id, "object" + id, "dropbox",
                    JsonSerializer.Serialize(new { fileId = "id:file" + id, revision = "revision" + id }), f.Handler.Hash, f.Handler.Text.Length);
                await db.RegisterMembershipAsync("ml" + id, "object" + id, localRoot, path, 1);
                await db.RegisterMembershipAsync("mr" + id, "object" + id, remoteRoot, path, 1);
                await db.RecordConflictAsync("conflict" + id, pair.Id, "object" + id, "fixture input", "local" + id, "ml" + id, "remote" + id, "mr" + id);
            }
            var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id, null, null,
                "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
            await db.CreateOperationAsync(new("recovery-operation", "recovery-idempotency", SyncAuthorityOperationKind.Reconcile,
                pair.Id, "object0000", pair.PolicyRevision, "surface-fixture-profile", "{}", 0,
                [new("recovery-member", pair.Id, "object0000", "affected", localRoot, "ml0000", "local0000", 1, "item0000.txt", "{}")], [], []), admission);
            for (var i = 0; i < 502; i++)
                await db.RegisterRecoveryArtifactAsync("archive" + i.ToString("D4"), "recovery-operation", "object0000", null,
                    "local", JsonSerializer.Serialize(new { kind = "file", path = Path.Combine(pair.LocalRoot, "missing-archive" + i) }), localHash, 1);
        }
        var page = Success(await f.Run("conflicts", "list", "--pair", pair.Id, "--account", f.Connection,
            "--offset", "500", "--limit", "2", "--group", "Unreliable or missing time"), "late conflict page");
        Check(page["totalOpen"]!.GetValue<int>() == 502 && page["returned"]!.GetValue<int>() == 2 && !page["hasMore"]!.GetValue<bool>(),
            "group-filtered conflict pagination reaches beyond first 500");
        var captured = Success(await f.Run("conflicts", "select", "--pair", pair.Id, "--account", f.Connection), "all matching conflict snapshot");
        Check(captured["count"]!.GetValue<int>() == 502, "all matching selection retains every exact conflict identity/generation");
        var otherAccount = await f.Run("conflicts", "select", "--pair", pair.Id, "--account", f.OtherConnection);
        Check(otherAccount.Exit != 0, "conflict snapshot cannot capture a pair under another retained account");
        var show = Success(await f.Run("conflicts", "show", "--pair", pair.Id, "--account", f.Connection,
            "--path", "item0501.txt"), "late exact text comparison");
        Check(show["localText"]?.GetValue<string>() == localText && show["dropboxText"]?.GetValue<string>() == remoteText,
            "exact later conflict comparison reads both verified text versions");
        // The successful read requires the real retained file identity, not a fabricated empty identity.
        await File.WriteAllTextAsync(reviewedLocalPath, "changed after the retained conflict\n");
        var changed = Success(await f.Run("conflicts", "show", "--pair", pair.Id, "--account", f.Connection,
            "--path", "item0501.txt"), "changed conflict version remains unavailable");
        Check(changed["localText"] is null && changed["note"]?.GetValue<string>().Contains("unavailable", StringComparison.OrdinalIgnoreCase) == true,
            "conflict preview never substitutes newer local bytes for the reviewed version");
        var calls = f.Handler.Calls.Count;
        foreach (var choice in new[] { "KeepLocal", "KeepDropbox", "PreserveBoth" })
        {
            var stale = await f.Run("conflicts", "resolve", "--pair", pair.Id, "--account", f.Connection, "--path", "item0501.txt",
                "--choice", choice, "--key", "conflict0501", "--generation", "99", "--yes", "--timeout-seconds", "30");
            Check(stale.Exit != 0 && stale.Output.Contains("conflict changed", StringComparison.OrdinalIgnoreCase),
                choice + " reaches authority and refuses stale exact conflict generation");
        }
        var merge = await f.Run("conflicts", "resolve", "--pair", pair.Id, "--account", f.Connection, "--path", "item0501.txt",
            "--choice", "Merge", "--key", "conflict0501", "--generation", "1", "--yes");
        Check(merge.Exit != 0 && f.Handler.Calls.Skip(calls).All(route => route is "users/get_current_account" or "files/get_metadata"),
            "no invented merge and only identity-verification reads for stale choices");
        calls = f.Handler.Calls.Count;
        var selected = captured["items"]!.AsArray()[0]!.DeepClone();
        selected["generation"] = 999;
        var snapshot = Path.Combine(f.Path, "selection.json");
        await File.WriteAllTextAsync(snapshot, new JsonArray(selected).ToJsonString());
        var staleBatch = await f.Run("conflicts", "resolve-selection", "--account", f.Connection, "--choice", "KeepLocal", "--choices", snapshot, "--yes", "--timeout-seconds", "30");
        Check(staleBatch.Exit != 0 &&
              staleBatch.Output.Contains("A selected conflict changed, disappeared or belongs to another account", StringComparison.Ordinal) &&
              staleBatch.Output.Contains("No selected file was changed", StringComparison.Ordinal) &&
              f.Handler.Calls.Count == calls,
            "raw-array batch snapshots parse and stale captured identity rejects entire selection before any effect");
        var recovery = Success(await f.Run("recovery", "list", "--pair", pair.Id, "--account", f.Connection,
            "--offset", "500", "--limit", "2"), "late recovery page");
        Check(recovery["returned"]!.GetValue<int>() == 2 && recovery["offset"]!.GetValue<int>() == 500 && !recovery["hasMore"]!.GetValue<bool>(),
            "recovery pagination reaches retained rows after 500");
        var laterKey = recovery["items"]!.AsArray()[0]!["key"]!.GetValue<string>();
        var cleanup = await f.Run("recovery", "cleanup", "--pair", pair.Id, "--key", laterKey, "--yes");
        Check(cleanup.Exit != 0 && cleanup.Output.Contains("protected", StringComparison.OrdinalIgnoreCase) &&
            !cleanup.Output.Contains("not retained", StringComparison.OrdinalIgnoreCase), "exact later-page recovery lookup reaches retention authority, not false missing-target refusal");
        var revisions = Success(await f.Run("recovery", "revisions", "--pair", pair.Id, "--account", f.Connection, "--path", "item0501.txt"), "provider revisions");
        Check(revisions["items"]!.AsArray().Count == 1 && revisions["items"]![0]!["key"]!.GetValue<string>() == "revision:revision0501" &&
            f.Handler.Calls.Contains("files/list_revisions"), "revision listing uses real provider host method and exact revision key");
        calls = f.Handler.Calls.Count;
        var wrongAccount = await f.Run("recovery", "revisions", "--pair", pair.Id, "--account", f.OtherConnection, "--path", "item0501.txt");
        Check(wrongAccount.Exit != 0 && f.Handler.Calls.Count == calls, "revisions reject mismatched retained account before HTTP");
        var beforeWrongRevision = await File.ReadAllBytesAsync(reviewedLocalPath);
        var wrongRevision = await f.Run("recovery", "restore", "--pair", pair.Id, "--account", f.Connection, "--path", "item0501.txt", "--key", "revision:wrong", "--yes");
        Check(wrongRevision.Exit != 0, "wrong provider revision is refused with a nonzero exit");
        Check((await File.ReadAllBytesAsync(reviewedLocalPath)).SequenceEqual(beforeWrongRevision),
            "wrong provider revision preserves the current local bytes, including the later edit");
        var jobs = Success(await f.Run("jobs", "status"), "idle jobs status");
        Check(!jobs["active"]!.GetValue<bool>() && jobs["job"] is null, "job status reads real idle snapshot without starting work");
        var watch = await f.Run("jobs", "watch", "--key", "unknown", "--seconds", "1");
        Check(watch.Exit != 0, "watch refuses absent exact active job");
    }

    private sealed record Result(int Exit, string Output, string Progress);
    private sealed class Fixture(string path, string connection)
    {
        internal string Path => path;
        internal string Connection => connection;
        internal string OtherConnection => DropboxConnectionBinding.CreateConnectionId("dbid:cli-surface-other");
        internal Handler Handler { get; } = new();
        internal Action<string, string>? ObserveProgressAtProvider;
        internal bool AppEffects, FailEffects, RejectBefore;
        internal int BeforeCount, ApplyCount, CompleteCount;
        internal static async Task<Fixture> Create(string path)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Fixture binding requires NTFS.");
            Directory.CreateDirectory(path);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, Account, Namespace,
                "cli-surface-home", DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
            var fixture = new Fixture(path, binding.ConnectionId);
            await using var runtime = new Runtime(path, fixture.Handler);
            await new DropboxConnectionStore(runtime.State).SaveNewAsync(binding);
            var other = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "dbid:cli-surface-other",
                "cli-surface-other-root", "cli-surface-other-home", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes).Normalize();
            await new DropboxConnectionStore(runtime.State).SaveAsync(other with {
                CredentialReference = WindowsDropboxRefreshTokenStore.AccountTarget(other.ConnectionId) });
            var local = System.IO.Path.Combine(path, "local");
            Directory.CreateDirectory(System.IO.Path.Combine(local, "kept"));
            await File.WriteAllTextAsync(System.IO.Path.Combine(local, "user.txt"), "preserve original local data");
            await runtime.Engine.BindPairAsync(new(null, "Fixture pair", local, new("Source", "/Source", "id:source"),
                new("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath, "id:recovery"), true, 0, false,
                SelectionRules: [new("excluded", false)], DirectoryStorageRules: [new("kept", SyncDirectoryStorage.AlwaysAvailable)],
                ConnectionId: binding.ConnectionId, DefaultStorage: SyncDirectoryStorage.AlwaysAvailable,
                IgnoreRules: [new(SyncIgnoreKind.FileExtension, ".tmp")]));
            return fixture;
        }
        internal async Task<SyncPairSet> Pairs() { await using var r = new Runtime(path, Handler); return await r.Engine.LoadPairsAsync(); }
        internal async Task<SyncPair> Pair() => (await Pairs()).Pairs.Single();
        internal Task<Result> MutatePair(string action, SyncPair pair, params string[] args) => Run(["pairs", action, "--pair", pair.Id,
            "--account", connection, "--generation", pair.PolicyRevision.ToString(), ..args, "--yes"]);
        internal Task<Result> Storage(string action, SyncPair pair, params string[] args) => Run(["storage", action, "--pair", pair.Id,
            "--account", connection, "--generation", pair.PolicyRevision.ToString(), ..args, "--yes"]);
        internal async Task<Result> Run(params string[] args)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Handler.BeforeProviderCall = route => ObserveProgressAtProvider?.Invoke(route, error.ToString());
            int exit;
            try
            {
                exit = await SyncCli.ExecuteAsync(["--cli", ..args, "--json"], output, error,
                    () => new Runtime(path, Handler, this));
            }
            finally { Handler.BeforeProviderCall = null; }
            Check(!output.ToString().Contains("fixture-access", StringComparison.Ordinal) && !output.ToString().Contains("fixture-refresh", StringComparison.Ordinal),
                "surface output excludes fixture credentials");
            Check(!error.ToString().Contains("fixture-access", StringComparison.Ordinal) &&
                !error.ToString().Contains("fixture-refresh", StringComparison.Ordinal), "progress excludes fixture credentials");
            return new(exit, output.ToString(), error.ToString());
        }
    }

    private sealed class Runtime : SyncCliOperationsBase
    {
        private readonly PilotInstanceLock _instance;
        private readonly HttpClient _http;
        private readonly Fixture? _fixture;
        internal Runtime(string path, Handler handler, Fixture? fixture = null)
        {
            _fixture = fixture;
            _instance = PilotInstanceLock.Acquire(System.IO.Path.Combine(path, "state", "instance.lock"));
            _http = new HttpClient(handler, disposeHandler: false);
            var authority = _instance.StateAuthority;
            Engine = new(authority, _http, new SyncPairStore(authority), new SyncRuntimeStateStore(authority),
                new DropboxConnectionStore(authority), new MemoryDropboxRefreshTokenStore("fixture-refresh"),
                new SyncAuthorityDatabase(System.IO.Path.Combine(path, "authority.db")));
        }
        internal SyncAuthorityRuntimeHost Engine { get; }
        internal PilotStateAuthority State => _instance.StateAuthority;
        protected override SyncAuthorityRuntimeHost Host => Engine;
        protected override PilotStateAuthority StateAuthority => State;
        protected override bool ApplicationOwnsRuntime => _fixture?.AppEffects == true;
        public override string Mode => "controlled-http";
        protected override Task BeforeSaveSettingsAsync(CancellationToken cancellationToken)
        {
            if (_fixture?.AppEffects == true)
            {
                _fixture.BeforeCount++;
                if (_fixture.RejectBefore) throw new InvalidOperationException("Fixture unsaved draft.");
            }
            return Task.CompletedTask;
        }
        protected override Task<bool> ApplySavedSettingsAsync(SyncCliSettingsData previous, SyncCliSettingsData saved, CancellationToken cancellationToken)
        {
            if (_fixture?.AppEffects != true) return Task.FromResult(false);
            _fixture.ApplyCount++;
            if (_fixture.FailEffects && _fixture.ApplyCount == 1) throw new IOException("Fixture OS registration failure.");
            return Task.FromResult(true);
        }
        protected override Task CompleteSettingsSaveAsync() { if (_fixture?.AppEffects == true) _fixture.CompleteCount++; return Task.CompletedTask; }
        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync(); } finally { _http.Dispose(); _instance.Dispose(); }
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal Action<string>? BeforeProviderCall;
        internal List<string> Calls { get; } = [];
        internal byte[] Text = [];
        internal string Hash = new('a', 64);
        private string _query = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host is not ("api.dropboxapi.com" or "content.dropboxapi.com")) throw new InvalidOperationException("Non-fixture HTTP rejected.");
            var route = request.RequestUri.AbsolutePath;
            if (route == "/oauth2/token") return Json(new { access_token = "fixture-access", expires_in = 3600, account_id = Account });
            route = route[3..]; Calls.Add(route);
            BeforeProviderCall?.Invoke(route);
            if (route == "users/get_current_account") return Json(new { account_id = Account, root_info = new { root_namespace_id = Namespace, home_namespace_id = "cli-surface-home" } });
            if (route == "files/download")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Text) };
                response.Headers.Add("Dropbox-API-Result", FileMetadata().ToJsonString()); return response;
            }
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            if (route == "files/get_metadata")
            {
                var path = body["path"]!.GetValue<string>();
                return Json(path.Contains("file", StringComparison.Ordinal) || path.EndsWith("item0501.txt", StringComparison.Ordinal)
                    ? FileMetadata() : Folder(path));
            }
            if (route == "files/search_v2") { _query = body["query"]!.GetValue<string>(); return Json(new { matches = Array.Empty<object>(), has_more = true, cursor = "fixture-search-continuation" }); }
            if (route == "files/search/continue_v2") return Json(new { matches = new[] { new { metadata = new { metadata = Folder("/" + _query) } } }, has_more = false });
            if (route == "files/list_revisions") return Json(new { entries = new[] { FileMetadata() } });
            if (route is "files/list_folder" or "files/list_folder/continue") return Json(new { entries = Array.Empty<object>(), has_more = false, cursor = "fixture-list-cursor" });
            throw new InvalidOperationException("Unexpected provider route: " + route);
        }
        private static JsonObject Folder(string identity)
        {
            // A folder is looked up by path and then re-verified by its id, so both forms must resolve to
            // the same name/id/path; an unmapped id would answer with its own literal text and a wrong path.
            var name = identity switch {
                "id:source" => "Source", "id:replacement" => "Replacement", "id:duplicate" => "Duplicate",
                "id:recovery" => "Recovery", "id:duplicatepolicy" => "DuplicatePolicy",
                _ => identity.TrimStart('/') };
            var path = name == "Recovery" ? SyncDropboxArtifactPaths.RecoveryContainerPath : "/" + name;
            return new() { [".tag"] = "folder", ["id"] = "id:" + name.ToLowerInvariant(), ["name"] = name,
                ["path_display"] = path, ["path_lower"] = path.ToLowerInvariant() };
        }
        private JsonObject FileMetadata() => new() {
            [".tag"] = "file", ["id"] = "id:file0501", ["rev"] = "revision0501", ["name"] = "item0501.txt",
            ["path_display"] = "/Source/item0501.txt", ["path_lower"] = "/source/item0501.txt", ["size"] = Text.Length,
            ["content_hash"] = Hash, ["client_modified"] = "2026-01-01T00:00:00Z", ["server_modified"] = "2026-01-01T00:00:00Z" };
        private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
