using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.V2;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Checks for EngineOperations (`--engine-checks repair`), written by EngineOperations's writer.</summary>
public static class EngineOperationsChecks
{
    // Fixture writes need no retry window.
    private static readonly AtomicReplacePolicy ImmediatePolicy = new([TimeSpan.Zero]);

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncshark-engine-operations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var productRoot = OverrideProductRoot(root);
            SettingsComposition(root);
            SettingsWriteOrder(root);
            SettingsPublicationOnly(root);
            MissingDatabase(root);
            PairLifecycle(root);
            DriveDecisionRepairRefusal(root);
            ResolutionRule(root);
            DriveChoices(root);
            DriveChoiceWriteFailures(root);
            ResolutionAuthority(root);
            CorruptPairDoesNotBlockExactLookup(root);
            PreferencesStandalone(root);
            PreferencesHostRoundTrip(root);
            PreferencesRefuseCorruptState(root);
            PreferencePatchRejection(root);
            TransportConstruction(root);
            PreviewModeAuthority(root);
            BindRootsRefusals(root);
            BindingIdentityGuards(root);
            FolderKeepRules(root);
            RetryViews(root);
            RecoveryReads(root);
            ReviewedNoticeCosts(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void ReviewedNoticeCosts(string root)
    {
        // known-facts R03's observed open-issue cohort and its doubled regression workload, not pair item counts.
        foreach (var size in new[] { 461, 922 })
        {
            var productRoot = Path.Combine(root, "reviewed-cost-" + size);
            using var instance = AcquireIsolated(productRoot);
            using var http = GuardedClient();
            var inner = StateStore.Open(StateStore.DatabasePath(productRoot));
            var store = new TestDoubles.StoreCounters(inner);
            var pair = new PairConfig("cost", "Pair", Path.Combine(productRoot, "local"), "/Pair",
                "connection", [], [], FirstRunPolicy.NewestWins, false, null);
            inner.SavePair(pair);
            inner.Commit(pair.PairId, StateChange.Empty with
            {
                Raise = Enumerable.Range(0, size).Select(index => (IssueKind.Info,
                    TextKey.InfoIgnoredFolderKept, "item-" + index,
                    new IssueSubject([new IssueItem(Side.Local, "item-" + index, "id-" + index)], null))).ToArray()
            });
            var host = EngineHost.Compose(productRoot, _ => { }, instance.StateAuthority, ShippedSettings(),
                http, TimeProvider.System, _ => null, _ => store, EngineHost.Mode.Preview);
            try
            {
                var operations = new EngineOperations(productRoot, _ => { }, host);
                // The notice UI uses the full engine-issues result. Compatibility CLI pages default to 50;
                // its maximum is 200, and the older UI asks for that page plus one has-more sentinel.
                foreach (var take in new[] { SyncCli.ActivityDefaultLimit, SyncCli.ActivityMaximumLimit,
                    SyncCli.ActivityMaximumLimit + 1, int.MaxValue })
                {
                    var rows = Math.Min(size, take);
                    Check.Require(store.OpenIssuePage(pair.PairId, null, null, null, take, 0).Page.Count == rows,
                        "the stored control returns the full requested page");
                    var page = operations.OpenIssuePage(pair.PairId, null, null, null, take, 0);
                    Check.Require(page.Total == size && page.Page.Count == rows &&
                        page.Page.All(row => row.Issue.Reviewed is { Length: 64 }),
                        "one reviewed token is returned for every displayed notice");
                    MeasureNoticeCost(store, size, take, "stored-page", () =>
                        store.OpenIssuePage(pair.PairId, null, null, null, take, 0));
                    MeasureNoticeCost(store, size, take, "reviewed-page", () =>
                        operations.OpenIssuePage(pair.PairId, null, null, null, take, 0));
                }
                MeasureNoticeCost(store, size, size, "engine-issues-control", () =>
                {
                    _ = store.Pair(pair.PairId);
                    _ = store.OpenIssuePage(pair.PairId, null, null, null, int.MaxValue, 0)
                        .Page.Select(row => row.Issue).ToArray();
                });
                Check.Require(operations.Issues(pair.PairId, null).Count == size,
                    "the engine notice UI reads the entire issue cohort");
                MeasureNoticeCost(store, size, size, "engine-issues", () =>
                    operations.Issues(pair.PairId, null));
                var expected = inner.Issues(pair.PairId).First();
                foreach (var reviewed in new Issue?[] { null, expected })
                {
                    var mode = reviewed is null ? "ordinary" : "reviewed";
                    MeasureNoticeCost(store, size, 1, mode + "-prerequisite", () =>
                        store.ReadPrerequisiteRetry(pair.PairId, reviewed));
                    MeasureNoticeCost(store, size, 1, mode + "-item", () =>
                        store.ReadItemRetryState(pair.PairId, [expected.Id], reviewed));
                }
                using (var probe = new TestDoubles.CostProbe(StateStore.DatabasePath(productRoot)))
                {
                    var guard = probe.Execute(StateSchema.SelectIssueById,
                        (StateSchema.ParamPair, pair.PairId), (StateSchema.ParamId, expected.Id));
                    Console.WriteLine($"REVIEWED-COST cohort={size} extra-guard-statements=1 " +
                        $"vm-steps={guard.VmSteps} path-keys={guard.PathKeyCalls} changed-rows={guard.ChangedRows}");
                }
                var many = expected with { Subject = expected.Subject with
                {
                    Items = Enumerable.Range(0, size).Select(index =>
                        new IssueItem(Side.Local, "member-" + index, "id-" + index)).ToArray()
                } };
                var bytes = TestDoubles.AllocProbe.Measure(_ => PairSafety.ForReview(many));
                Console.WriteLine($"REVIEWED-COST subject-members={size} tokens=1 allocated-bytes={bytes}");
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
    }

    private static void MeasureNoticeCost(TestDoubles.StoreCounters store, int cohort, int rows,
        string mode, Action read)
    {
        read();
        store.Reset();
        var bytes = TestDoubles.AllocProbe.Measure(_ => read(), warmup: 0);
        Console.WriteLine($"REVIEWED-COST cohort={cohort} take={rows} mode={mode} " +
            $"store-calls={store.Commands} allocated-bytes={bytes}");
    }

    private static void SettingsComposition(string root)
    {
        var operations = new EngineOperations(root, _ => { });
        var settings = operations.Settings();
        Check.Require(settings.Count == 21, "the settings list holds every EngineSettings value");
        Check.Require(Value(settings, "DefaultFirstRunPolicy") == FirstRunPolicy.NewestWins.ToString(),
            "the embedded defaults are listed");
        Check.Require(!Overridden(settings, "DefaultFirstRunPolicy"), "an untouched value is not overridden");

        SettingsFile.Write(root, "{\"LogRetentionDays\":7}", ImmediatePolicy);
        settings = operations.Settings();
        Check.Require(Value(settings, "LogRetentionDays") == "7", "a user value applies to the composition");
        Check.Require(Overridden(settings, "LogRetentionDays"), "a user value is marked overridden");

        settings = operations.SetSetting("LogRetentionDays", "5");
        Check.Require(Value(settings, "LogRetentionDays") == "5", "a set value is returned");
        Check.Require(Value(operations.Settings(), "LogRetentionDays") == "5", "a set value is saved");

        var refused = false;
        try { operations.SetSetting("LogRetentionDays", "many"); }
        catch (InvalidDataException) { refused = true; }
        Check.Require(refused, "a bad setting value is refused");
        Check.Require(Value(operations.Settings(), "LogRetentionDays") == "5", "a refused setting changes nothing");
    }

    // The merged document is validated before the write, so a refused setting never rewrites the file.
    private static void SettingsWriteOrder(string root)
    {
        var productRoot = Path.Combine(root, "settings-order");
        var operations = new EngineOperations(productRoot, _ => { });
        var settingsPath = Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.UserSettingsFile);

        // The file can be edited by hand; an override merged onto a value the engine refuses must not be saved.
        SettingsFile.Write(productRoot, "{ \"LogRetentionDays\": \"many\" }", ImmediatePolicy);
        var original = File.ReadAllBytes(settingsPath);
        var mergedRefused = false;
        try { operations.SetSetting("ConflictLabelFormat", "{name}"); }
        catch (InvalidDataException error)
        {
            mergedRefused = error.Message == Text.Get(TextKey.SettingsInvalid,
                (TextArg.Name, "LogRetentionDays"), (TextArg.Value, "many"));
        }
        Check.Require(mergedRefused, "an override that makes the merged document invalid is refused");
        Check.Require(File.ReadAllBytes(settingsPath).SequenceEqual(original),
            "a refused merged document leaves the settings file byte-identical");

        // The invalid value itself is still correctable: the publication policy comes from the merged
        // replacement, so the old document is never parsed again (#135).
        var corrected = operations.SetSetting(nameof(EngineSettings.LogRetentionDays), "7");
        Check.Require(Value(corrected, nameof(EngineSettings.LogRetentionDays)) == "7"
            && Value(operations.Settings(), nameof(EngineSettings.LogRetentionDays)) == "7",
            "an invalid saved value is corrected through SetSetting");
        Check.Require(!File.ReadAllBytes(settingsPath).SequenceEqual(original),
            "a corrected setting replaces the invalid document");
    }

    // CV-SPEED / D1-Z: a settings edit is a durable file publication only; it opens no state, generation or provider.
    private static void SettingsPublicationOnly(string root)
    {
        var productRoot = Path.Combine(root, "settings-publication");
        var operations = new EngineOperations(productRoot, _ => { });
        operations.Settings();
        operations.SetSetting("LogRetentionDays", "3");
        Check.Require(File.Exists(Path.Combine(productRoot, ResourceNames.EngineFolder,
                ResourceNames.UserSettingsFile)),
            "the engine setting is published to its own file, never to pair state");
        Check.Require(!File.Exists(StateStore.DatabasePath(productRoot)),
            "a settings edit neither opens nor creates the engine database");
        Check.Require(!Directory.Exists(Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.LogsFolder)),
            "a settings edit opens no run log");
    }

    private static void MissingDatabase(string root)
    {
        var operations = new EngineOperations(root, _ => { });
        Check.Require(operations.ListPairs().Count == 0, "there are no pairs before the database exists");
        Check.Require(operations.Status(null).Count == 0, "status without a database is empty");
        Check.Require(operations.Status("any").Count == 0, "status of a pair without a database is empty");
        Check.Require(operations.Issues("any", null).Count == 0, "issues without a database are empty");
        Check.Require(RefusedPairUnknown(() => operations.Pair("any"), "any"),
            "an exact pair lookup without a database is refused");
        Check.Require(!File.Exists(StateStore.DatabasePath(root)), "a read-only pair lookup creates no database");
        Check.Require(RefusedPairUnknown(() => operations.ExcludePreviewAsync("any",
                new Exclusion(Side.Local, "folder", false), CancellationToken.None).GetAwaiter().GetResult(), "any"),
            "a preview of an unknown pair is refused");
        Check.Require(!File.Exists(StateStore.DatabasePath(root)),
            "a refused preview of an unknown pair creates no database");
    }

    private static void PairLifecycle(string root)
    {
        var connection = EnsureConnection();
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        var pair = new PairConfig("pair-1", "Pair A", localRoot, "/Dropbox", connection, [], [],
            FirstRunPolicy.NewestWins, true, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(root))) store.SavePair(pair);

        var operations = new EngineOperations(root, _ => { });
        using (var held = AcquireIsolated(root))
        {
            Check.Require(Threw<IOException>(() => operations.RepairAsync(pair.PairId, default).GetAwaiter().GetResult()),
                "a held instance lock refuses Repair without waiting or contacting a provider");
            var logs = Directory.GetFiles(Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.LogsFolder),
                "*", SearchOption.AllDirectories);
            Check.Require(logs.Any(path => File.ReadAllText(path).Contains(nameof(RunEventKind.Failed))),
                "held-lock refusal is diagnosed in the startup log");
            foreach (var path in logs)
            {
                using var closed = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        using (var released = AcquireIsolated(root)) { }
        Check.Require(operations.Pair("pair-1").PairId == "pair-1", "an exact pair lookup returns the saved pair");
        Check.Require(RefusedPairUnknown(() => operations.Pair("other"), "other"),
            "an exact pair lookup of an unknown pair is refused");
        var pairs = operations.ListPairs();
        Check.Require(pairs.Count == 1 && pairs[0].PairId == "pair-1", "the saved pair is listed");

        var status = operations.Status("pair-1");
        Check.Require(status.Count == 1 && status[0].Pair.PairId == "pair-1" && status[0].LastRepair is null &&
            !status[0].FirstRepairDone && !status[0].PendingPresent && status[0].IssueCounts.Count == 0,
            "status describes a pair that has never run");
        Check.Require(operations.Status(null).Count == 1, "status lists every pair");
        Check.Require(Refused(() => operations.Status("other")), "status of an unknown pair is refused");
        Check.Require(operations.Issues("pair-1", null).Count == 0 &&
            operations.Issues("pair-1", IssueKind.Failure).Count == 0, "a new pair has no issues");
        Check.Require(Refused(() => operations.Issues("other", null)), "issues of an unknown pair are refused");

        using (var store = StateStore.Open(StateStore.DatabasePath(root)))
        {
            store.Commit("pair-1", StateChange.Empty with
            {
                Raise = [
                    (IssueKind.Tie, TextKey.TieEqualTimes, "tie.txt", new IssueSubject(
                        [new IssueItem(Side.Local, "tie.txt", "local-1"),
                            new IssueItem(Side.Dropbox, "tie.txt", "dropbox-2")],
                        null)),
                    (IssueKind.Failure, TextKey.FailChanged, "failure.txt", new IssueSubject(
                        [new IssueItem(Side.Local, "failure.txt", "local-3")], null))]
            });
            store.DismissIssue("pair-1", store.Issues("pair-1").Single(issue => issue.Kind == IssueKind.Tie).Id);
        }
        var counts = operations.Status("pair-1").Single().IssueCounts;
        Check.Require(counts.Count == 1 && counts[IssueKind.Failure] == 1,
            "status counts open issues by kind and leaves dismissed ones out");

        Check.Require(!operations.SetEnabledAsync("pair-1", false, CancellationToken.None).GetAwaiter().GetResult().Enabled,
            "a pair can be switched off");
        Check.Require(!operations.ListPairs()[0].Enabled, "the switch is saved");

        // M5: the saved rule's Keep flag is not the request; re-inclusion matches side and canonical key whatever the
        // stored spelling.
        var excluded = operations.ExcludeAsync("pair-1", new Exclusion(Side.Dropbox, "folder/sub", true),
            CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(excluded.Exclusions.Count == 1 && excluded.Exclusions[0].Keep,
            "an exclusion is added with its Keep flag");
        SeedRecords(root, "folder/sub", "folder/sub/child.txt", "folder/other.txt");
        var included = operations.IncludeAsync("pair-1", Side.Dropbox, "Folder/Sub", CancellationToken.None)
            .GetAwaiter().GetResult();
        Check.Require(included.Exclusions.Count == 0 && operations.ListPairs()[0].Exclusions.Count == 0,
            "re-inclusion removes the rule whatever its Keep flag and spelling");
        Check.Require(Recorded(root, Side.Local, "folder/sub") is not null &&
            Recorded(root, Side.Dropbox, "folder/sub") is not null &&
            Recorded(root, Side.Dropbox, "folder/sub/child.txt") is not null,
            "re-inclusion retains the excluded item and its descendants on both sides");
        Check.Require(Recorded(root, Side.Dropbox, "folder/other.txt") is not null,
            "re-inclusion keeps a sibling record");

        operations.ExcludeAsync("pair-1", new Exclusion(Side.Dropbox, "folder/sub", false), CancellationToken.None)
            .GetAwaiter().GetResult();
        Check.Require(operations.IncludeAsync("pair-1", Side.Dropbox, "folder/sub", CancellationToken.None)
                .GetAwaiter().GetResult().Exclusions.Count == 0,
            "re-inclusion also removes a rule saved with Keep=false");

        operations.ExcludeAsync("pair-1", new Exclusion(Side.Local, "folder/sub", true), CancellationToken.None)
            .GetAwaiter().GetResult();
        var opposite = operations.IncludeAsync("pair-1", Side.Dropbox, "folder/sub", CancellationToken.None)
            .GetAwaiter().GetResult();
        Check.Require(opposite.Exclusions.Count == 1 && opposite.Exclusions[0].Side == Side.Local,
            "re-inclusion keeps an opposite-side rule with the same canonical key");
        operations.IncludeAsync("pair-1", Side.Local, "folder/sub", CancellationToken.None).GetAwaiter().GetResult();

        foreach (var side in Enum.GetValues<Side>())
        foreach (var keep in new[] { true, false })
        {
            operations.ExcludeAsync("pair-1", new Exclusion(side, "folder/sub", keep), CancellationToken.None)
                .GetAwaiter().GetResult();
            SeedRecords(root, "folder/sub", "folder/sub/child.txt", "folder/other.txt");
            Check.Require(operations.IncludeAsync("pair-1", side, "Folder/Sub", CancellationToken.None)
                    .GetAwaiter().GetResult().Exclusions.Count == 0 &&
                Recorded(root, Side.Local, "folder/sub/child.txt") is not null &&
                Recorded(root, Side.Dropbox, "folder/sub/child.txt") is not null,
                $"Public re-inclusion preserves history for each side and Keep flag ({side}, {keep}).");
        }

        Check.Require(Refused(() => operations.AddPairAsync("Pair B", Path.Combine(root, "absent"), "/Dropbox",
                connection, null, CancellationToken.None).GetAwaiter().GetResult()), "a missing local root is refused");
        Check.Require(RefusedWith(() => operations.AddPairAsync("Pair B", localRoot, "/Dropbox",
                DropboxConnectionBinding.CreateConnectionId("unregistered-account"), null, CancellationToken.None)
                .GetAwaiter().GetResult(), TextKey.AccountMissing),
            "an unknown account is refused");
        Check.Require(RefusedWith(() => operations.AddPairAsync("Pair B", localRoot, @"C:\Dropbox\Pair", connection,
                null, CancellationToken.None).GetAwaiter().GetResult(), TextKey.DropboxPathInvalid),
            "a Windows Dropbox root is refused");
        Check.Require(RefusedWith(() => operations.AddPairAsync("Pair B", localRoot, "Dropbox/Pair", connection,
                null, CancellationToken.None).GetAwaiter().GetResult(), TextKey.DropboxPathInvalid),
            "a relative Dropbox root is refused");
        Check.Require(RefusedWith(() => operations.AddPairAsync("Pair B", localRoot, @"\Dropbox", connection,
                null, CancellationToken.None).GetAwaiter().GetResult(), TextKey.DropboxPathInvalid),
            "a backslash Dropbox root is refused");
        Check.Require(operations.ListPairs().Count == 1, "a refused add saves nothing");

        Check.Require(Refused(() => operations.ImportPairAsync("v2-missing", CancellationToken.None)
            .GetAwaiter().GetResult()), "a missing v2 pair is refused");
        Check.Require(Refused(() => operations.RepairAsync("other", CancellationToken.None).GetAwaiter().GetResult()),
            "repairing an unknown pair is refused");
        Check.Require(Refused(() => operations.ResolveAsync("other", 1, ResolutionChoice.Dismiss, null,
            null, false, null, CancellationToken.None).GetAwaiter().GetResult()),
            "resolving on an unknown pair is refused");

        operations.RemovePairAsync("pair-1", CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(operations.ListPairs().Count == 0, "a removed pair is gone");
        Check.Require(Refused(() => operations.RemovePairAsync("pair-1", CancellationToken.None)
            .GetAwaiter().GetResult()), "removing a missing pair is refused");
    }

    // C-RUN-11/CA2-05: an explicit Repair keeps its drive-decision refusal when a prerequisite obligation is also owed.
    private static void DriveDecisionRepairRefusal(string root)
    {
        var connection = EnsureConnection();
        var localRoot = Path.Combine(root, "drive-repair-local");
        Directory.CreateDirectory(localRoot);
        var pair = new PairConfig("drive-repair", "Pair A", localRoot, "/Dropbox", connection, [], [],
            FirstRunPolicy.NewestWins, true, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(root)))
        {
            store.SavePair(pair);
            var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
            var source = store.ReadSource(pair.PairId, Side.Local) ?? throw new InvalidOperationException();
            store.AdvanceSource(pair.PairId, Side.Local, generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor,
                SourceObligation.NormalRepair);
            store.Commit(pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.DriveChange, TextKey.DriveRestored, "", new IssueSubject([], null))]
            });
        }
        using (var visibility = StateStore.Open(StateStore.DatabasePath(root)))
        {
            var drive = visibility.Issues(pair.PairId).Single(issue => issue.Kind == IssueKind.DriveChange);
            Check.Require(visibility.SetIssueIgnored(pair.PairId, drive, true), "hide the pair blocker");
            Check.Require(visibility.OpenIssuePage(pair.PairId, null, null, null, 10, 0, true).Page.Single().Issue.Id == drive.Id,
                "hidden blocker remains available for Restore");
        }
        var openingObserved = false;
        var stages = new List<string>();
        var operations = new EngineOperations(root, message =>
        {
            stages.Add(message);
            if (!message.Contains(Text.Get(TextKey.StageStore), StringComparison.Ordinal)) return;
            var logs = Directory.GetFiles(Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.LogsFolder),
                pair.PairId + "-*" + ResourceNames.LogExtension);
            Check.Require(logs.Length == 1, "Repair has one log before opening its store");
            using var stream = new FileStream(logs[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            Check.Require(reader.ReadToEnd().Contains(nameof(TextKey.StageStore), StringComparison.Ordinal),
                "store opening is already flushed to the run log when its progress is delivered");
            openingObserved = true;
        });
        Check.Require(RefusedWith(() => operations.RepairAsync(pair.PairId, CancellationToken.None)
                .GetAwaiter().GetResult(), TextKey.PairAwaitingDecision),
            "an explicit Repair with an outstanding obligation and a drive decision still reports PairAwaitingDecision");
        Check.Require(openingObserved && stages.Any(message => message.Contains(Text.Get(TextKey.StageSchema), StringComparison.Ordinal)),
            "Repair exposes store and schema work before admission can refuse it");
        var file = Directory.GetFiles(Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.LogsFolder),
            pair.PairId + "-*" + ResourceNames.LogExtension).Single();
        var failures = File.ReadLines(file).Select(line => line.Split(RunLog.FieldSeparator))
            .Where(fields => fields[1] == nameof(RunEventKind.Failed)).ToArray();
        Check.Require(failures.Length == 1 && failures[0][4].Contains(nameof(TextKey.PairAwaitingDecision), StringComparison.Ordinal),
            "pre-Repair admission refusal is diagnosed exactly once in its existing startup file");
    }

    // M2: a notice dismissed earlier must not read as resolved after a different choice leaves it dismissed.
    private static void ResolutionRule(string root)
    {
        var connection = EnsureConnection();
        var localRoot = Path.Combine(root, "resolve-local");
        Directory.CreateDirectory(localRoot);

        var pair = new PairConfig("resolve-pair", "Resolve Pair", localRoot, "/Dropbox", connection, [], [],
            FirstRunPolicy.NewestWins, true, null, new RootBinding(localRoot, "/Dropbox", connection,
                WindowsDirectoryListing.ReadIdentity(localRoot),
                new DropboxRootIdentity("account-a", "root-a", "home-a", "folder-a")));
        // A local name Windows refuses: observing it raises the item's Failure before any Dropbox call.
        var path = "bad|name";
        long issueId;
        using (var store = StateStore.Open(StateStore.DatabasePath(root)))
        {
            store.SavePair(pair);
            store.Commit(pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Tie, TextKey.TieEqualTimes, path, new IssueSubject(
                    [new IssueItem(Side.Local, path, "local-1"), new IssueItem(Side.Dropbox, path, "dropbox-2")],
                    null))]
            });
            issueId = store.Issues(pair.PairId).Single().Id;
            store.DismissIssue(pair.PairId, issueId);
        }

        var operations = new EngineOperations(root, _ => { });
        // The pair is bound, so Dismiss settles its notice through the ordinary resolution route without Repair.
        var (_, dismissedResolved, dismissedRepair) = operations.ResolveAsync(pair.PairId, issueId,
            ResolutionChoice.Dismiss, null, null, false, null, CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(dismissedResolved && dismissedRepair is null,
            "ordinary Dismiss settles only its notice without running Repair");
        using (var check = StateStore.OpenReadOnly(StateStore.DatabasePath(root)))
        {
            // Dismissal marks the notice dismissed; it is not a physical delete.
            Check.Require(check?.IssueById(pair.PairId, issueId) is not { Dismissed: false },
                "the dismissed Tie is closed by its exact notice choice");
        }
    }

    private static void DriveChoices(string root)
    {
        foreach (var enabled in new[] { true, false })
        {
            var productRoot = Path.Combine(root, "choice-" + Guid.NewGuid().ToString("N"));
            var connection = EnsureConnection();
            var (pair, issueId) = SeedDriveDecision(productRoot, "choice", enabled, TextKey.DriveChanged, connection);
            SeedDriveDecision(productRoot, "other", true, TextKey.DriveChanged, connection);
            var otherBefore = DecisionState(productRoot, "other");
            var operations = new EngineOperations(productRoot, _ => { });
            var (_, resolved, repair) = operations.ResolveAsync(pair.PairId, issueId, ResolutionChoice.SyncNormally,
                null, null, false, null, CancellationToken.None).GetAwaiter().GetResult();
            Check.Require(resolved && repair is null, "SyncNormally records a decision without running Repair");
            using (var store = StateStore.Open(StateStore.DatabasePath(productRoot)))
            {
                var saved = store.Pair(pair.PairId)!;
                Check.Require(saved.Drive is null && saved.FirstRun == pair.FirstRun && saved.Enabled == enabled,
                    "SyncNormally clears evidence and preserves the saved policy and enabled state");
                Check.Require(store.IssueById(pair.PairId, issueId) is null &&
                    store.Issues(pair.PairId).Count == 1, "only the selected drive notice is closed");
                Check.Require(store.SyncedByKey(pair.PairId, Side.Local, PathNames.Key("saved.txt")) is not null &&
                    store.FirstRepairDone(pair.PairId), "SyncNormally preserves records and the first-run bit");
            }
            Check.Require(DecisionState(productRoot, "other") == otherBefore, "another pair's state is unchanged");
            Check.Require(!Directory.Exists(Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.LogsFolder)),
                "a drive choice opens no run log or session");
            Check.Require(RefusedWith(() => operations.ResolveAsync(pair.PairId, issueId,
                    ResolutionChoice.SyncNormally, null, null, false, null, CancellationToken.None)
                    .GetAwaiter().GetResult(), TextKey.ResolveStale),
                "a completed choice cannot be replayed using its stale notice ID");

            // FirstSync reads the configured roots first; an unregistered connection refuses before any reset.
            var firstRoot = Path.Combine(root, "first-" + Guid.NewGuid().ToString("N"));
            var unregistered = DropboxConnectionBinding.CreateConnectionId("first-sync-unregistered");
            var (freshPair, freshIssue) = SeedDriveDecision(firstRoot, "first", false, TextKey.DriveChanged,
                unregistered);
            var freshBefore = DecisionState(firstRoot, freshPair.PairId);
            var freshOperations = new EngineOperations(firstRoot, _ => { });
            Check.Require(RefusedWith(() => freshOperations.ResolveAsync(freshPair.PairId, freshIssue,
                    ResolutionChoice.FirstSync, null, FirstRunPolicy.LocalWins, false, null, CancellationToken.None)
                    .GetAwaiter().GetResult(), TextKey.AccountMissing),
                "FirstSync refuses before any reset when the configured account cannot be observed");
            Check.Require(DecisionState(firstRoot, freshPair.PairId) == freshBefore,
                "a refused FirstSync keeps its drive notice, records, binding and first-run state unchanged");
        }
    }

    private static void DriveChoiceWriteFailures(string root)
    {
        foreach (var boundary in new[] { "UPDATE OF def_json ON pair", "DELETE ON issue" })
        {
            var productRoot = Path.Combine(root, "choice-write-" + Guid.NewGuid().ToString("N"));
            var connection = EnsureConnection();
            var (pair, issueId) = SeedDriveDecision(productRoot, "interrupted", true, TextKey.DriveRestored, connection);
            using var instance = AcquireIsolated(productRoot);
            new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(new DropboxConnectionBinding(
                DropboxProviderConfiguration.RegisteredClientId, "engine-operations-account", "ns-root", "ns-home",
                DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
                ConnectionId: connection)).GetAwaiter().GetResult();
            using var http = GuardedClient();
            var host = EngineHost.Compose(productRoot, _ => { }, instance.StateAuthority, ShippedSettings(), http,
                TimeProvider.System, _ => null, validator =>
                {
                    var opened = StateStore.Open(StateStore.DatabasePath(productRoot), validator);
                    ExecuteFixtureSql(productRoot, "CREATE TRIGGER choice_failure BEFORE " + boundary +
                        " BEGIN SELECT RAISE(ABORT, 'injected choice write failure'); END;");
                    return opened;
                }, EngineHost.Mode.Direct);
            var operations = host.Operations;
            var failed = false;
            try
            {
                operations.ResolveAsync(pair.PairId, issueId, ResolutionChoice.SyncNormally, null,
                    null, false, null, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (SqliteException error) when (error.Message.Contains("injected choice write failure")) { failed = true; }
            finally { ExecuteFixtureSql(productRoot, "DROP TRIGGER choice_failure;"); }
            Check.Require(failed, "each choice-write failure is surfaced rather than reporting success");
            using (var store = StateStore.Open(StateStore.DatabasePath(productRoot)))
            {
                Check.Require(store.IssueById(pair.PairId, issueId) is { Kind: IssueKind.DriveChange, Dismissed: false },
                    "a failed choice leaves its drive notice open, including a failed final close");
                Check.Require(store.Pair(pair.PairId)!.Drive == pair.Drive,
                    "a failed choice rolls back its pair update with the rest of the transaction");
                Check.Require(store.SyncedByKey(pair.PairId, Side.Local, PathNames.Key("saved.txt")) is not null &&
                    store.FirstRepairDone(pair.PairId),
                    "a SyncNormally write failure never resets records or the first-run bit");
                var info = store.Issues(pair.PairId).Single(issue => issue.Kind == IssueKind.Info);
                Check.Require(RefusedWith(() => operations.ResolveAsync(pair.PairId, info.Id, ResolutionChoice.Dismiss,
                        null, null, false, null, CancellationToken.None).GetAwaiter().GetResult(),
                        TextKey.PairAwaitingDecision),
                    "an interrupted choice still blocks ordinary resolution after reopening");
            }
            var (_, resolved, repair) = operations.ResolveAsync(pair.PairId, issueId, ResolutionChoice.SyncNormally,
                null, null, false, null, CancellationToken.None).GetAwaiter().GetResult();
            Check.Require(resolved && repair is null && operations.Issues(pair.PairId, IssueKind.DriveChange).Count == 0,
                "retrying an interrupted choice completes without implicitly syncing");
            using var final = StateStore.Open(StateStore.DatabasePath(productRoot));
            Check.Require(final.Pair(pair.PairId)!.Drive is null &&
                final.SyncedByKey(pair.PairId, Side.Local, PathNames.Key("saved.txt")) is not null &&
                final.FirstRepairDone(pair.PairId),
                "a retried choice reaches the same committed record and first-run state as an uninterrupted choice");
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void ResolutionAuthority(string root)
    {
        var productRoot = Path.Combine(root, "resolution-authority");
        var connection = EnsureConnection();
        var (pair, driveId) = SeedDriveDecision(productRoot, "authority", true, TextKey.DriveChanged, connection);
        var (_, otherDriveId) = SeedDriveDecision(productRoot, "other", true, TextKey.DriveRestored, connection);
        using (var store = StateStore.Open(StateStore.DatabasePath(productRoot)))
        {
            store.Commit(pair.PairId, StateChange.Empty with
            {
                Raise = Enum.GetValues<IssueKind>().Where(kind => kind != IssueKind.DriveChange)
                    .Select(kind => (kind, TextKey.FailChanged, kind.ToString(), new IssueSubject([], null))).ToArray()
            });
        }
        var operations = new EngineOperations(productRoot, _ => { });
        var before = DecisionState(productRoot, pair.PairId);
        foreach (var choice in Enum.GetValues<ResolutionChoice>())
        {
            if (choice is ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally or ResolutionChoice.Ignore or ResolutionChoice.Unignore) continue;
            Check.Require(RefusedWith(() => operations.ResolveAsync(pair.PairId, driveId, choice, null, null, false,
                    null, CancellationToken.None).GetAwaiter().GetResult(), TextKey.ResolveBadChoice,
                    (TextArg.Id, driveId.ToString(CultureInfo.InvariantCulture)), (TextArg.Choice, choice.ToString())),
                "a drive notice grants neither Dismiss, Retry nor conflict-choice authority");
        }
        foreach (var choice in new[] { ResolutionChoice.FirstSync, ResolutionChoice.SyncNormally, ResolutionChoice.Retry })
        {
            Check.Require(RefusedWith(() => operations.ResolveAsync(pair.PairId, otherDriveId, choice, null, null,
                    false, null, CancellationToken.None).GetAwaiter().GetResult(), TextKey.ResolveStale),
                "an existing notice ID belonging to another pair is refused");
            Check.Require(RefusedWith(() => operations.ResolveAsync(pair.PairId, long.MaxValue, choice, null, null,
                    false, null, CancellationToken.None).GetAwaiter().GetResult(), TextKey.ResolveStale),
                "an unknown notice ID is refused before session access");
            Check.Require(RefusedPairUnknown(() => operations.ResolveAsync("absent", driveId, choice, null, null,
                    false, null, CancellationToken.None).GetAwaiter().GetResult(), "absent"),
                "resolution requires the exact existing pair");
            foreach (var issue in operations.Issues(pair.PairId, null))
            {
                if (issue.Kind == IssueKind.DriveChange ||
                    choice == ResolutionChoice.Retry && issue.Kind == IssueKind.Failure) continue;
                Check.Require(RefusedWith(() => operations.ResolveAsync(pair.PairId, issue.Id, choice, null, null,
                        false, null, CancellationToken.None).GetAwaiter().GetResult(), TextKey.ResolveBadChoice,
                        (TextArg.Id, issue.Id.ToString(CultureInfo.InvariantCulture)), (TextArg.Choice, choice.ToString())),
                    "a choice cannot borrow authority from a different issue kind");
            }
        }
        var failure = operations.Issues(pair.PairId, IssueKind.Failure).Single();
        Check.Require(RefusedWith(() => operations.ResolveAsync(pair.PairId, failure.Id, ResolutionChoice.Dismiss,
                null, null, false, null, CancellationToken.None).GetAwaiter().GetResult(), TextKey.ResolveBadChoice,
                (TextArg.Id, failure.Id.ToString(CultureInfo.InvariantCulture)),
                (TextArg.Choice, ResolutionChoice.Dismiss.ToString())),
            "Failure Dismiss is refused by kind before the drive gate or any session");
        foreach (var issue in operations.Issues(pair.PairId, null)
            .Where(issue => issue.Kind is IssueKind.Info or IssueKind.Skipped))
        foreach (var choice in Enum.GetValues<ResolutionChoice>().Where(choice => choice is not
            (ResolutionChoice.Dismiss or ResolutionChoice.Retry or ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally or ResolutionChoice.Ignore or ResolutionChoice.Unignore)))
        {
            Check.Require(RefusedWith(() => operations.ResolveAsync(pair.PairId, issue.Id, choice, null, null,
                    false, null, CancellationToken.None).GetAwaiter().GetResult(), TextKey.ResolveBadChoice,
                    (TextArg.Id, issue.Id.ToString(CultureInfo.InvariantCulture)), (TextArg.Choice, choice.ToString())),
                "Info and Skipped notices grant no conflict-choice authority before log, session or provider access");
        }
        var tie = operations.Issues(pair.PairId, IssueKind.Tie).Single();
        Check.Require(RefusedWith(() => operations.ResolveAsync(pair.PairId, tie.Id, ResolutionChoice.Dismiss,
                null, null, false, null, CancellationToken.None).GetAwaiter().GetResult(),
                TextKey.PairAwaitingDecision),
            "a drive decision blocks ordinary resolution before opening a session");
        Check.Require(DecisionState(productRoot, pair.PairId) == before,
            "every refused choice leaves records, evidence, first-run state and all notices unchanged");
        Check.Require(!Directory.Exists(Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.LogsFolder)),
            "ownership and kind validation precede opening a run log");

        operations.SetEnabledAsync(pair.PairId, false, CancellationToken.None).GetAwaiter().GetResult();
        operations.SetEnabledAsync(pair.PairId, true, CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(operations.Issues(pair.PairId, IssueKind.DriveChange).Single().Id == driveId,
            "switching the pair off and on does not clear its drive decision");
        var localRoot = pair.LocalRoot;
        Directory.CreateDirectory(localRoot);
        var marker = Path.Combine(localRoot, "keep.txt");
        File.WriteAllText(marker, "retained");
        var otherBefore = DecisionState(productRoot, "other");
        operations.RemovePairAsync(pair.PairId, CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(operations.ListPairs().All(saved => saved.PairId != pair.PairId) &&
            File.ReadAllText(marker) == "retained" && DecisionState(productRoot, "other") == otherBefore,
            "the existing remove-pair operation removes only app state and never local content or another pair");
    }

    private static (PairConfig Pair, long IssueId) SeedDriveDecision(string productRoot, string pairId,
        bool enabled, TextKey cause, string connection)
    {
        // A saved connection lets the generation composition complete offline; no provider is contacted.
        var pair = new PairConfig(pairId, "Pair " + pairId, Path.Combine(productRoot, pairId, "absent"), "/Dropbox",
            connection, [], [], FirstRunPolicy.DropboxWins, enabled, new DriveEvidence(1, 2, 3),
            new RootBinding(Path.Combine(productRoot, pairId, "absent"), "/Dropbox", connection,
                new DirectoryIdentity(1, 2, 3), new DropboxRootIdentity("root-account", "ns-root", "ns-home", "id:root")));
        using var store = StateStore.Open(StateStore.DatabasePath(productRoot));
        store.SavePair(pair);
        store.Commit(pairId, StateChange.Empty with
        {
            Record = [new SyncedEntry(PathNames.Key("saved.txt"), "saved.txt", PathNames.DropboxKey("saved.txt"),
                "saved.txt", false, "local-saved", 0, 0, "dropbox-saved", "")],
            Raise = [(IssueKind.DriveChange, cause, "", new IssueSubject([], null)),
                (IssueKind.Info, TextKey.FailChanged, "info.txt", new IssueSubject([], null))]
        });
        store.FinishRepair(pairId, new RepairSummary("prior", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            true, false, null, false, new Dictionary<string, int>(), new Dictionary<string, int>(), "", false));
        // The progress-only overload never sets the durable first-run bit; these seeds represent a completed run.
        ExecuteFixtureSql(productRoot, "UPDATE pair SET first_repair_done = 1 WHERE pair_id = '" + pairId + "';");
        return (pair, store.Issues(pairId).Single(issue => issue.Kind == IssueKind.DriveChange).Id);
    }

    private static string DecisionState(string productRoot, string pairId)
    {
        using var store = StateStore.Open(StateStore.DatabasePath(productRoot));
        return JsonSerializer.Serialize(new
        {
            Pair = store.Pair(pairId), Issues = store.Issues(pairId), FirstRepairDone = store.FirstRepairDone(pairId),
            Record = store.SyncedByKey(pairId, Side.Local, PathNames.Key("saved.txt")),
            LastRepair = store.LastRepair(pairId), Pending = store.Pending(pairId)
        });
    }

    private static void ExecuteFixtureSql(string productRoot, string sql)
    {
        using var connection = StateStore.Connect(StateStore.DatabasePath(productRoot), SqliteOpenMode.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Accepted difference: an exact lookup decodes only the requested pair.
    private static void CorruptPairDoesNotBlockExactLookup(string root)
    {
        var productRoot = Path.Combine(root, "corrupt-lookup");
        var pair = new PairConfig("exact", "Exact Pair", root, "/Dropbox", "account-1", [], [],
            FirstRunPolicy.NewestWins, true, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(productRoot))) store.SavePair(pair);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StateStore.DatabasePath(productRoot),
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO pair(pair_id, def_json) VALUES('corrupt', '{');";
            insert.ExecuteNonQuery();
        }

        var operations = new EngineOperations(productRoot, _ => { });
        Check.Require(operations.Pair("exact").PairId == "exact",
            "an exact pair lookup ignores an unrelated pair whose definition is corrupt");
    }

    // RC05-T7/T8: preference access requires the host-bound authority; a standalone entry refuses, state-free.
    private static void PreferencesStandalone(string root)
    {
        var productRoot = Path.Combine(root, "preferences-standalone");
        var operations = new EngineOperations(productRoot, _ => { });
        Check.Require(Threw<InvalidOperationException>(() => operations.ReadUiPreferencesAsync(
                CancellationToken.None).GetAwaiter().GetResult()),
            "a standalone preference read fails unavailable rather than constructing authority");
        Check.Require(Threw<InvalidOperationException>(() => operations.UpdateUiPreferencesAsync(
                UiPreferencesUpdate.Parse("{\"theme\":\"Dark\"}"), CancellationToken.None).GetAwaiter().GetResult()),
            "a standalone preference update fails unavailable rather than writing locally");
        Check.Require(operations.ListPairs().Count == 0 && !File.Exists(StateStore.DatabasePath(productRoot)),
            "refused preference access opens no product state and touches no pair");
    }

    // RC05-T2/T4/T8: real host-bound read/update, changed-field merge, shared-gate commit and restart preservation.
    private static void PreferencesHostRoundTrip(string root)
    {
        var productRoot = Path.Combine(root, "preferences-host");
        using var instanceLock = AcquireIsolated(productRoot);
        var authority = instanceLock.StateAuthority;
        using var http = GuardedClient();
        var defaults = new UiPreferencesStore(authority).LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(defaults.Theme == UiPreferencesStore.SystemTheme &&
            defaults.StatusIconStyle == UiPreferencesStore.DefaultIconSet,
            "a genuinely absent preference state reads the normalized defaults without creating a file");

        var host = ComposeHost(productRoot, authority, http, EngineHost.Mode.Persistent);
        host.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var operations = new EngineOperations(productRoot, _ => { }, host);
        var read = operations.ReadUiPreferencesAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(read.Theme == UiPreferencesStore.SystemTheme, "the host serves the current saved theme");

        var saved = operations.UpdateUiPreferencesAsync(
            UiPreferencesUpdate.Parse("{\"theme\":\"Dark\",\"pageViewModes\":{\"activity\":\"Detailed\"}}"),
            CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(saved.Theme == UiPreferencesStore.DarkTheme &&
            saved.PageViewModes is { Count: 1 } && saved.PageViewModes!["activity"] == UiPreferencesStore.DetailedView &&
            saved.KeepRunningWhenClosed && saved.StatusIconStyle == UiPreferencesStore.DefaultIconSet,
            "one verified save returns the merged value and leaves every unrelated preference as it was");

        // A second store instance on the same authority proves the shared gate and the changed-field merge.
        var second = new UiPreferencesStore(authority).UpdateAsync(
            UiPreferencesUpdate.Parse("{\"compactRows\":true,\"pageViewModes\":{\"pending\":\"Simple\"}}"),
            CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(second.Theme == UiPreferencesStore.DarkTheme && second.CompactRows &&
            second.PageViewModes is { Count: 2 } && second.PageViewModes!["activity"] == UiPreferencesStore.DetailedView &&
            second.PageViewModes!["pending"] == UiPreferencesStore.SimpleView,
            "a disjoint second patch merges under the shared authority gate and keeps both page keys");
        var merged = operations.ReadUiPreferencesAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(merged.Theme == UiPreferencesStore.DarkTheme && merged.CompactRows &&
            merged.PageViewModes is { Count: 2 },
            "the host-bound read observes the committed merge");

        Check.Require(operations.ListPairs().Count == 0 && operations.Status(null).Count == 0,
            "preference work reads no pair and starts no pair work");
        Check.Require(!Directory.Exists(Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.LogsFolder)),
            "preference work writes no run log and contacts no provider");

        host.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        var restarted = ComposeHost(productRoot, authority, http, EngineHost.Mode.Persistent);
        restarted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var reopened = new EngineOperations(productRoot, _ => { }, restarted);
        var persisted = reopened.ReadUiPreferencesAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(persisted.Theme == UiPreferencesStore.DarkTheme && persisted.CompactRows &&
            persisted.PageViewModes is { Count: 2 },
            "a restarted host reads the same committed preferences");
        Check.Require(ReferenceEquals(restarted.Http, http),
            "the host borrows the supplied process client instead of constructing a second one");
        restarted.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    // RC05-V5-P/T2: a failed preference migration does not stop the host, is surfaced by preference access and
    // leaves both stored slots untouched.
    private static void PreferencesRefuseCorruptState(string root)
    {
        var productRoot = Path.Combine(root, "preferences-corrupt");
        using var instanceLock = AcquireIsolated(productRoot);
        var authority = instanceLock.StateAuthority;
        Directory.CreateDirectory(authority.StateRoot);
        var slotA = Path.Combine(authority.StateRoot, "ui-preferences-a.json");
        var slotB = Path.Combine(authority.StateRoot, "ui-preferences-b.json");
        File.WriteAllText(slotA, "{ not a preference record");
        File.WriteAllText(slotB, "{ also not a preference record");
        var beforeA = File.ReadAllBytes(slotA);
        var beforeB = File.ReadAllBytes(slotB);

        using var http = GuardedClient();
        var host = ComposeHost(productRoot, authority, http, EngineHost.Mode.Persistent);
        var started = false;
        try
        {
            host.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            started = true;
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            started = false;
        }
        Check.Require(started, "a corrupt preference successor does not fail host readiness");
        var operations = new EngineOperations(productRoot, _ => { }, host);
        Check.Require(Threw<InvalidDataException>(() => operations.ReadUiPreferencesAsync(CancellationToken.None)
                .GetAwaiter().GetResult()),
            "preference access reports the preserved corruption instead of returning defaults");
        Check.Require(Threw<InvalidDataException>(() => operations.UpdateUiPreferencesAsync(
                UiPreferencesUpdate.Parse("{\"theme\":\"Light\"}"), CancellationToken.None).GetAwaiter().GetResult()),
            "a preference update refuses rather than overwriting a corrupt successor");
        Check.Require(File.ReadAllBytes(slotA).SequenceEqual(beforeA) &&
            File.ReadAllBytes(slotB).SequenceEqual(beforeB),
            "a refused preference operation resets neither alternating slot");
        host.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    // RC05-T7: the closed patch grammar rejects every malformed request before state is touched.
    private static void PreferencePatchRejection(string root)
    {
        foreach (var json in new[]
                 {
                     "{}", "[]", "{\"unknownField\":true}", "{\"theme\":null}", "{\"theme\":\"Purple\"}",
                     "{\"pageViewModes\":{\"activity\":\"Huge\"}}", "{\"pageViewModes\":{\"Bad Key\":\"Simple\"}}",
                     "{\"statusIconStyle\":\"Rounded\"}", "{\"settings\":{\"theme\":\"Dark\"}}",
                 })
        {
            Check.Require(Threw<SyncCliUsageException>(() => UiPreferencesUpdate.Parse(json)),
                "a malformed preference patch is refused: " + json);
        }
        Check.Require(UiPreferencesUpdate.Parse(
                "{\"keepRunningWhenClosed\":false,\"pageViewModes\":{\"activity\":\"Detailed\"}}")
            .PageViewModes?["activity"] == UiPreferencesStore.DetailedView,
            "an exact supported patch parses");
        var productRoot = Path.Combine(root, "preferences-patch");
        _ = new EngineOperations(productRoot, _ => { });
        Check.Require(!File.Exists(StateStore.DatabasePath(productRoot)) &&
            !Directory.Exists(Path.Combine(productRoot, ResourceNames.EngineFolder)),
            "rejected patch text never opens state, authority or a provider");
    }

    // RC05-V5-H: production composition owns the guarded ordinary and long-poll clients.
    private static void TransportConstruction(string root)
    {
        var productRoot = Path.Combine(root, "transport-construction");
        using var instanceLock = AcquireIsolated(productRoot);
        var authority = instanceLock.StateAuthority;
        var production = EngineHost.Compose(productRoot, _ => { }, authority, EngineHost.Mode.Direct);
        try
        {
            var ordinary = production.Http;
            AssertGuarded(ordinary, "the production host client");
            Check.Require(CapturedReadAllowance(ordinary) == DropboxApiLimits.ReadWriteAllowance,
                "the ordinary host client uses the 120-second read allowance");
            Check.Require(ordinary.DefaultRequestVersion == HttpVersion.Version11 &&
                ordinary.DefaultVersionPolicy == HttpVersionPolicy.RequestVersionExact,
                "the ordinary host client sends exactly HTTP/1.1");
            Check.Require(ordinary.Timeout == Timeout.InfiniteTimeSpan,
                "the ordinary host client relies on the guarded pending read, not a total timeout");
        }
        finally
        {
            production.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "transport-account", "ns-root", "ns-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes,
            DropboxConnectionBinding.CreateConnectionId("transport-account"));
        new DropboxConnectionStore(authority).SaveNewAsync(binding).GetAwaiter().GetResult();
        var localRoot = Path.Combine(productRoot, "local");
        Directory.CreateDirectory(localRoot);
        var pair = new PairConfig("transport", "Transport", localRoot, "/Dropbox", binding.ConnectionId, [], [],
            FirstRunPolicy.NewestWins, true, null);
        using var store = StateStore.Open(StateStore.DatabasePath(productRoot));
        store.SavePair(pair);
        using var http = GuardedClient();
        var host = ComposeHost(productRoot, authority, http, EngineHost.Mode.Direct);
        try
        {
            host.RecomposeCurrentAsync(pair.PairId, CancellationToken.None).GetAwaiter().GetResult();
            var longpoll = host.AcquireAsync(pair.PairId, (registration, current, generation, _) => Task.FromResult((HttpClient)typeof(PairGenerationResources)
                    .GetField("_longpollHttp", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(host.BorrowGeneration(registration, current.PairId, generation))!),
                CancellationToken.None).GetAwaiter().GetResult();
            AssertGuarded(longpoll, "the Engine long-poll client");
            Check.Require(longpoll.Timeout == Timeout.InfiniteTimeSpan,
                "the long-poll client relies on guarded activity, not a total timeout");
            Check.Require(CapturedReadAllowance(longpoll) ==
                TimeSpan.FromSeconds(DropboxApiLimits.LongpollMaxSeconds) + DropboxApiLimits.LongpollTimeoutAllowance,
                "the long-poll read allowance is the provider maximum plus its own allowance");
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        Check.Require(ReferenceEquals(host.Http, http),
            "a host reuses its supplied client instead of constructing another client");

        // NOTE T219-LONGPOLL-210: the first-generation 120-second request/210-second read pair requires a
        // long-poll setting the current EngineSettings does not expose; only the immutable maximum is constructed.
    }

    private static void AssertGuarded(HttpClient client, string what)
    {
        var handler = Handler(client);
        Check.Require(!handler.AllowAutoRedirect, what + " refuses redirects");
        Check.Require(handler.AutomaticDecompression == (DecompressionMethods.GZip | DecompressionMethods.Deflate),
            what + " decompresses GZip and Deflate");
        Check.Require(handler.ConnectTimeout == DropboxApiLimits.ConnectTimeout,
            what + " uses the shared connect allowance");
        Check.Require(handler.PlaintextStreamFilter is not null,
            what + " reaches the shared PlaintextStreamFilter guard");
    }

    private static SocketsHttpHandler Handler(HttpClient client)
    {
        for (var type = client.GetType(); type is not null; type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.FieldType == typeof(HttpMessageHandler))
                    return (SocketsHttpHandler)field.GetValue(client)!;
        throw new InvalidOperationException("the client has no handler field");
    }

    private static TimeSpan CapturedReadAllowance(HttpClient client)
    {
        var filter = Handler(client).PlaintextStreamFilter ?? throw new InvalidOperationException("no guard");
        var target = filter.Target ?? throw new InvalidOperationException("the guard captures no allowance");
        var allowances = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(TimeSpan)).ToArray();
        Check.Require(allowances.Length == 1, "the guard captures exactly one read allowance");
        return (TimeSpan)allowances[0].GetValue(target)!;
    }

    // D1/D2 preview authority: a preview host serves an exact read but refuses configuration mutation before any write.
    private static void PreviewModeAuthority(string root)
    {
        var productRoot = Path.Combine(root, "preview-authority");
        var pair = new PairConfig("preview", "Preview", Path.Combine(productRoot, "local"), "/Dropbox",
            "account-1", [], [], FirstRunPolicy.NewestWins, true, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(productRoot))) store.SavePair(pair);
        var before = ReadGeneration(productRoot, pair.PairId);
        using var instanceLock = AcquireIsolated(productRoot);
        using var http = GuardedClient();
        var host = ComposeHost(productRoot, instanceLock.StateAuthority, http, EngineHost.Mode.Preview);
        try
        {
            var operations = new EngineOperations(productRoot, _ => { }, host);
            Check.Require(operations.Pair(pair.PairId).PairId == pair.PairId,
                "a preview host serves the exact pair read");
            Check.Require(Threw<InvalidOperationException>(() => operations.SetIgnoreRulesAsync(pair.PairId,
                    "FileName:desktop.ini", false, CancellationToken.None).GetAwaiter().GetResult()),
                "a preview host refuses configuration mutation");
            Check.Require(ReadGeneration(productRoot, pair.PairId) == before,
                "a refused preview mutation advances no generation");
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void BindingIdentityGuards(string root)
    {
        using var store = StateStore.Open(StateStore.DatabasePath(Path.Combine(root, "binding-guards")));
        var pair = TestDoubles.SourceFixture.Bound(new PairConfig("binding", "Pair A",
            Path.Combine(root, "binding-local"), "/Pair", "connection", [], [],
            FirstRunPolicy.NewestWins, true, null));
        store.SavePair(pair);
        var saved = new SyncedEntry(PathNames.Key("Saved"), "Saved", PathNames.DropboxKey("Saved"),
            "Saved", false, "1", 1, DateTimeOffset.UnixEpoch.UtcTicks, "remote-saved", "hash");
        store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
        var losing = new Entry("Saved", false, 1, DateTimeOffset.UnixEpoch, "1", "", "hash", null);
        store.ArmPending(pair.PairId, new PendingEffect(1, "pending", 0, PendingKind.LocalDelete,
            PendingPhase.Armed, "Saved", new RemovalProof(losing, "1", "hash"), saved,
            null, null, null, null, false, null));
        var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
        var binding = pair.Roots!;
        var beforeSource = store.ReadSource(pair.PairId, Side.Local);
        var beforeRemote = store.ReadSource(pair.PairId, Side.Dropbox);
        var beforePending = store.Pending(pair.PairId);
        var beforeFirst = store.FirstRepairDone(pair.PairId);
        var beforeCount = store.SyncedCount(pair.PairId);
        foreach (var changed in new[]
        {
            binding with { Local = binding.Local with { VolumeSerialNumber = 2 } },
            binding with { Local = binding.Local with { FileId = 2 } },
            binding with { Dropbox = binding.Dropbox with { FolderId = "other-folder" } },
            binding with { Dropbox = binding.Dropbox with { AccountId = "other-account" } },
            binding with { Dropbox = binding.Dropbox with { RootNamespaceId = "other-root" } },
            binding with { Dropbox = binding.Dropbox with { HomeNamespaceId = "other-home" } },
            binding with { LocalRoot = Path.Combine(root, "other") },
            binding with { DropboxRoot = "/Other" },
            binding with { ConnectionId = "other-connection" }
        })
        {
            Check.Require(Threw<EngineRequestException>(() => PairSafety.ValidateConfigurationRoots(
                store, pair, generation, changed, true)), "explicit binding refuses every changed identity or scope");
            Check.Require(store.ReadPairExecution(pair.PairId)!.Value.Generation == generation &&
                store.ReadPairExecution(pair.PairId)!.Value.Pair.Roots == binding &&
                store.ReadSource(pair.PairId, Side.Local) == beforeSource &&
                store.ReadSource(pair.PairId, Side.Dropbox) == beforeRemote &&
                store.Pending(pair.PairId) == beforePending && store.FirstRepairDone(pair.PairId) == beforeFirst &&
                store.SyncedCount(pair.PairId) == beforeCount &&
                store.SyncedByKey(pair.PairId, Side.Local, saved.LocalKey) == saved,
                "a refused binding preserves roots, generation, history, first-run state and pending custody");
        }
        var notice = store.Issues(pair.PairId, [IssueKind.RootIdentity]).Single();
        Check.Require(EngineOperations.ResolutionChoices(notice).SequenceEqual([ResolutionChoice.BindRoots, ResolutionChoice.Ignore]) &&
            Massdex.SyncShark.Engine.Edge.IssuePresentation.Options(notice).Single(option => option.Choice == ResolutionChoice.BindRoots.ToString()).Label == Text.Get(TextKey.ResolutionBindRootsLabel) &&
            Massdex.SyncShark.Engine.Edge.IssuePresentation.ResolutionConfirmation(notice, ResolutionChoice.BindRoots) ==
                Text.Get(TextKey.ResolutionBindRootsConsequence), "root notices share their one label and confirmation");
        PairSafety.ValidateConfigurationRoots(store, pair, generation, binding, true);
        var close = StateChange.Empty with { Close = [(string.Empty, new[] { IssueKind.RootIdentity })] };
        Check.Require(Threw<LiveAdmissionStaleException>(() => store.ReconcileRootIdentity(pair.PairId,
            generation + 1, binding, notice, close)), "stale generation cannot close a root notice");
        store.ReconcileRootIdentity(pair.PairId, generation, binding, notice,
            StateChange.Empty with { Raise = [(IssueKind.RootIdentity, TextKey.RootUnavailable, string.Empty,
                new IssueSubject([], null))] });
        var successor = store.Issues(pair.PairId, [IssueKind.RootIdentity]).Single();
        Check.Require(Threw<LiveAdmissionStaleException>(() => store.ReconcileRootIdentity(pair.PairId,
            generation, binding, notice, close)) && store.IssueById(pair.PairId, successor.Id)?.Cause == successor.Cause,
            "a stale selected notice cannot close its successor");
        store.ReconcileRootIdentity(pair.PairId, generation, binding, successor, close);
        Check.Require(store.Issues(pair.PairId, [IssueKind.RootIdentity]).Count == 0,
            "restored matching identities close the exact current root notice");
        var misconfigured = pair with { PairId = "saved-scope", Roots = binding with { DropboxRoot = "/Other" } };
        store.SavePair(misconfigured);
        Check.Require(Threw<EngineRequestException>(() => PairSafety.ValidateConfigurationRoots(store,
            misconfigured, store.ReadPairExecution(misconfigured.PairId)!.Value.Generation, binding, true)),
            "binding refuses a saved scope that differs from the configured roots");
        var unbound = pair with { PairId = "unbound", Roots = null };
        store.SavePair(unbound);
        var unboundGeneration = store.ReadPairExecution(unbound.PairId)!.Value.Generation;
        PairSafety.ValidateConfigurationRoots(store, unbound, unboundGeneration, binding, true);
        store.SavePairDecision(unbound, unboundGeneration, unbound with { Roots = binding }, null, false, null);
        Check.Require(store.ReadPairExecution(unbound.PairId)!.Value.Pair.Roots == binding,
            "an unbound pair can save its initial observed identities");
    }

    // V13-C48 / ROOTID T02: bind refuses an unknown pair or an unobservable configured account before any write, and
    // cancellation before the transaction leaves the pair as it was.
    private static void BindRootsRefusals(string root)
    {
        var productRoot = Path.Combine(root, "bind-refusals");
        var connection = DropboxConnectionBinding.CreateConnectionId("bind-unregistered");
        // A disabled target keeps the composition-failure bookkeeping that follows a refused bind from writing.
        var pair = new PairConfig("bind", "Bind", Path.Combine(productRoot, "local"), "/Dropbox", connection, [], [],
            FirstRunPolicy.NewestWins, false, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(productRoot))) store.SavePair(pair);
        var operations = new EngineOperations(productRoot, _ => { });
        var before = ReadPairJson(productRoot, pair.PairId);
        var generation = ReadGeneration(productRoot, pair.PairId);

        Check.Require(RefusedPairUnknown(() => operations.BindRootsAsync("absent", CancellationToken.None)
            .GetAwaiter().GetResult(), "absent"), "binding an unknown pair is refused before any provider");
        Check.Require(RefusedWith(() => operations.BindRootsAsync(pair.PairId, CancellationToken.None)
                .GetAwaiter().GetResult(), TextKey.AccountMissing),
            "binding refuses when the configured account cannot be observed");
        Check.Require(ReadPairJson(productRoot, pair.PairId) == before &&
            ReadGeneration(productRoot, pair.PairId) == generation,
            "a refused bind writes no binding and advances no generation");
        Check.Require(Threw<OperationCanceledException>(() => operations.BindRootsAsync(pair.PairId,
                new CancellationToken(canceled: true)).GetAwaiter().GetResult()),
            "a pre-cancelled bind is refused");
        Check.Require(ReadPairJson(productRoot, pair.PairId) == before &&
            ReadGeneration(productRoot, pair.PairId) == generation,
            "cancellation before the transaction writes nothing");
        Check.Require(!Directory.Exists(Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.LogsFolder)),
            "a refused bind opens no run log");
    }

    // CV-D09: pair ignore rules and folder-keep rules are separate lists; the setter is value-idempotent, clears
    // only the requested list and refuses malformed or unknown-target input without a write.
    private static void FolderKeepRules(string root)
    {
        var productRoot = Path.Combine(root, "folder-keep");
        var connection = EnsureConnection();
        var pair = new PairConfig("keep", "Keep", Path.Combine(productRoot, "local"), "/Dropbox", connection,
            ["FileExtension:tmp"], [], FirstRunPolicy.NewestWins, true, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(productRoot))) store.SavePair(pair);
        var operations = new EngineOperations(productRoot, _ => { });
        Check.Require(EngineOperations.IgnoreRules(operations.Pair("keep"), true).Count == 0 &&
            EngineOperations.IgnoreRules(operations.Pair("keep"), false).SequenceEqual(["FileExtension:tmp"]),
            "a missing folder-keep list reads empty and stays separate from the ignore rules");

        var saved = operations.SetIgnoreRulesAsync("keep", "FileNamePrefix:~$", true, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check.Require(saved.IgnoredFolderKeepRules is ["FileNamePrefix:~$"] &&
            saved.IgnoreRules.SequenceEqual(["FileExtension:tmp"]),
            "a folder-keep edit changes only its own list");
        var afterWrite = ReadGeneration(productRoot, "keep");
        var repeated = operations.SetIgnoreRulesAsync("keep", "FileNamePrefix:~$", true, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check.Require(repeated.IgnoredFolderKeepRules is ["FileNamePrefix:~$"] &&
            ReadGeneration(productRoot, "keep") == afterWrite,
            "an identical set is value-idempotent and commits no pair write");

        var cleared = operations.SetIgnoreRulesAsync("keep", "none", true, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check.Require(cleared.IgnoredFolderKeepRules is null &&
            cleared.IgnoreRules.SequenceEqual(["FileExtension:tmp"]),
            "clearing folder-keep leaves the pair ignore rules untouched");
        var ignores = operations.SetIgnoreRulesAsync("keep", "FileNamePrefix:~$;FileExtension:tmp", false,
            CancellationToken.None).GetAwaiter().GetResult();
        Check.Require(ignores.IgnoredFolderKeepRules is null && ignores.IgnoreRules.Count == 2 &&
            ignores.IgnoreRules.Contains("FileNamePrefix:~$") && ignores.IgnoreRules.Contains("FileExtension:.tmp"),
            "a pair ignore edit keeps every folder-keep rule absent and stores canonical entries");
        var afterIgnores = ReadGeneration(productRoot, "keep");
        Check.Require(RefusedWith(() => operations.SetIgnoreRulesAsync("keep", "not-a-rule", false,
                CancellationToken.None).GetAwaiter().GetResult(), TextKey.IgnoreRulesInvalid),
            "a malformed rule list is refused with its own diagnostic");
        Check.Require(ReadGeneration(productRoot, "keep") == afterIgnores,
            "a refused rule list changes no pair definition");
        Check.Require(RefusedPairUnknown(() => operations.SetIgnoreRulesAsync("absent", "FileNamePrefix:~$", false,
            CancellationToken.None).GetAwaiter().GetResult(), "absent"),
            "setting rules on an unknown pair is refused");
        Check.Require(RefusedWith(() => operations.ResolveAsync("keep", 1, ResolutionChoice.Dismiss, null,
                null, false, "FileNamePrefix:~$", CancellationToken.None).GetAwaiter().GetResult(),
                TextKey.ResolveBadChoice),
            "the rules operand is accepted only by KeepFolderAlways");
    }

    // CV-RETRYVIEW: the one reduced projection reports a current presentation state and writes nothing.
    private static void RetryViews(string root)
    {
        var productRoot = Path.Combine(root, "retry-views");
        var pair = new PairConfig("views", "Views", Path.Combine(productRoot, "local"), "/Dropbox", "account-1", [], [],
            FirstRunPolicy.NewestWins, true, null);
        long failureId;
        using (var store = StateStore.Open(StateStore.DatabasePath(productRoot)))
        {
            store.SavePair(pair);
            store.Commit(pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailChanged, "failed.txt", new IssueSubject(
                    [new IssueItem(Side.Local, "failed.txt", "local-1")], null))]
            });
            failureId = store.Issues(pair.PairId).Single().Id;
        }
        var standalone = new EngineOperations(productRoot, _ => { });
        var unavailable = standalone.ReadIssueRetryViews(pair.PairId, [failureId]);
        Check.Require(unavailable.Count == 1 && unavailable[failureId].State == RetryPresentationState.Unavailable,
            "without a host the projection reports the authority as unavailable");
        Check.Require(!Directory.Exists(Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.LogsFolder)),
            "the projection opens no run log or provider work");

        var directRoot = Path.Combine(root, "retry-views-direct");
        var directPair = new PairConfig("views", "Views", Path.Combine(directRoot, "local"), "/Dropbox", "account-1",
            [], [], FirstRunPolicy.NewestWins, true, null);
        long directFailure;
        using (var store = StateStore.Open(StateStore.DatabasePath(directRoot)))
        {
            store.SavePair(directPair);
            store.Commit(directPair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailChanged, "failed.txt", new IssueSubject(
                    [new IssueItem(Side.Local, "failed.txt", "local-1")], null))]
            });
            directFailure = store.Issues(directPair.PairId).Single().Id;
            store.SavePair(directPair with { Enabled = false });
        }
        using (var instanceLock = AcquireIsolated(directRoot))
        using (var http = GuardedClient())
        {
            var host = ComposeHost(directRoot, instanceLock.StateAuthority, http, EngineHost.Mode.Direct);
            try
            {
                var hosted = new EngineOperations(directRoot, _ => { }, host);
                var disabled = hosted.ReadIssueRetryViews(directPair.PairId, [directFailure]);
                Check.Require(disabled[directFailure].State == RetryPresentationState.Disabled,
                    "a disabled pair reports Disabled without starting a host");
            }
            finally
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    // CV-RECOVERY/CV-TYPES: the read-only recovery views and the resolve entry's state-free refusals.
    private static void RecoveryReads(string root)
    {
        var productRoot = Path.Combine(root, "recovery-reads");
        var pair = new PairConfig("recovery", "Recovery", Path.Combine(productRoot, "local"), "/Dropbox",
            "account-1", [], [], FirstRunPolicy.NewestWins, true, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(productRoot))) store.SavePair(pair);
        var operations = new EngineOperations(productRoot, _ => { });
        Check.Require(operations.RecoveryList(pair.PairId, null).Count == 0,
            "a pair with no active folder operation lists no recovery rows");
        Check.Require(operations.RecoveryList(pair.PairId, "zzzz").Count == 0,
            "the after-operation cursor filters the empty page");
        Check.Require(RefusedPairUnknown(() => operations.RecoveryList("absent", null), "absent"),
            "recovery listing requires the exact pair");
        Check.Require(Threw<InvalidOperationException>(() => operations.Recovery(pair.PairId, "missing")),
            "an unknown recovery operation is surfaced rather than reported as a completed operation");
        Check.Require(Threw<InvalidOperationException>(() => operations.RecoveryItems(pair.PairId, "missing", 0, 10)),
            "recovery item paging requires an existing operation");
        Check.Require(Threw<ArgumentException>(() => operations.ResolveRecoveryAsync(pair.PairId, "op", 1,
            RecoveryChoice.Check, false, null, null, CancellationToken.None).GetAwaiter().GetResult()),
            "recovery mutation requires the documented confirmation");
        Check.Require(Threw<ArgumentException>(() => operations.ResolveRecoveryAsync(pair.PairId, "op", 1,
            RecoveryChoice.Check, true, 3, null, CancellationToken.None).GetAwaiter().GetResult()),
            "an item and its generation are supplied together");
        Check.Require(RefusedPairUnknown(() => operations.ResolveRecoveryAsync("absent", "op", 1,
            RecoveryChoice.Check, true, null, null, CancellationToken.None).GetAwaiter().GetResult(), "absent"),
            "recovery mutation requires the exact existing pair");
    }

    private static void SeedRecords(string root, params string[] paths)
    {
        using var store = StateStore.Open(StateStore.DatabasePath(root));
        store.Commit("pair-1", StateChange.Empty with
        {
            Record = paths.Select(path => new SyncedEntry(PathNames.Key(path), path, PathNames.DropboxKey(path), path,
                false, "local-" + path, 0, 0, "dropbox-" + path, "")).ToArray()
        });
    }

    private static SyncedEntry? Recorded(string root, Side side, string path)
    {
        using var store = StateStore.OpenReadOnly(StateStore.DatabasePath(root));
        return store?.SyncedByKey("pair-1", side, PathNames.KeyFor(side, path));
    }

    private static long ReadGeneration(string productRoot, string pairId)
    {
        using var store = StateStore.Open(StateStore.DatabasePath(productRoot));
        return store.ReadPairExecution(pairId)?.Generation ?? -1;
    }

    private static string ReadPairJson(string productRoot, string pairId)
    {
        using var store = StateStore.Open(StateStore.DatabasePath(productRoot));
        return JsonSerializer.Serialize(store.Pair(pairId));
    }

    // One saved connection binding serves every offline generation composition in this check run.
    private static string EnsureConnection()
    {
        using var instanceLock = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        var store = new DropboxConnectionStore(instanceLock.StateAuthority);
        if (store.Load() is { } existing) return existing.ConnectionId;
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "engine-operations-account", "ns-root", "ns-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes,
            DropboxConnectionBinding.CreateConnectionId("engine-operations-account"));
        store.SaveNewAsync(binding).GetAwaiter().GetResult();
        return binding.ConnectionId;
    }

    private static PilotInstanceLock AcquireIsolated(string productRoot) =>
        PilotInstanceLock.Acquire(Path.Combine(productRoot, "state", SyncSharkRuntimePaths.InstanceLockFileName));

    private static HttpClient GuardedClient() => EngineComposition.CreateDropboxHttpClient(
        DropboxApiLimits.ReadWriteAllowance, DecompressionMethods.GZip | DecompressionMethods.Deflate,
        TimeProvider.System);

    private static EngineHost ComposeHost(string productRoot, PilotStateAuthority authority, HttpClient http,
        EngineHost.Mode mode) =>
        EngineHost.Compose(productRoot, _ => { }, authority, ShippedSettings(), http, TimeProvider.System,
            _ => null, validator => StateStore.Open(StateStore.DatabasePath(productRoot), validator), mode);

    private static EngineSettings ShippedSettings()
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        return SettingsParser.Parse(document.RootElement.GetRawText(), null);
    }

    private static string Value(IReadOnlyList<(string Name, string Value, bool Overridden)> settings, string name) =>
        settings.Single(row => row.Name == name).Value;

    private static bool Overridden(IReadOnlyList<(string Name, string Value, bool Overridden)> settings, string name) =>
        settings.Single(row => row.Name == name).Overridden;

    private static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (EngineRequestException) { return true; }
    }

    private static bool Threw<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static bool RefusedWith(Action action, TextKey key, params (TextArg Name, string Value)[] values)
    {
        try { action(); return false; }
        catch (EngineRequestException error)
        {
            return error.Key == key && (values.Length == 0 || error.Message == Text.Get(key, values));
        }
    }

    // An exact pair lookup owns the one PairUnknown request error, with the requested id in its message.
    private static bool RefusedPairUnknown(Action action, string pairId)
    {
        try { action(); return false; }
        catch (EngineRequestException error)
        {
            return error.Key == TextKey.PairUnknown &&
                error.Message == Text.Get(TextKey.PairUnknown, (TextArg.Pair, pairId));
        }
    }

    // Core's test hook redirects the product lock and state folder to a temporary root; the engine test assembly is
    // not on Core's InternalsVisibleTo list.
    private static IDisposable OverrideProductRoot(string root) =>
        (IDisposable)typeof(SyncSharkRuntimePaths)
            .GetMethod("OverrideProductRootForTest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { root })!;

}
