using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.Engine.Edge.Results;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Edge;

/// <summary>Checks for EngineCommands (`--engine-checks cli`), written by EngineCommands's writer.</summary>
public static class EngineCommandsChecks
{
    private const string SettingName = "LogRetentionDays";
    private const string IssueValue = "45";
    private const string CatalogResource = "SyncShark.EngineCli.json";

    private static readonly string[] ResolveChoices =
    [
        "use-local", "use-dropbox", "keep-both", "dismiss", "rename-incoming", "replace-existing",
        "keep-local", "keep-dropbox", "rename-local", "rename-dropbox", "retry", "first-sync", "sync-normally",
    ];

    private static readonly string?[] FirstRunChoices = [null, "newest-wins", "local-wins", "dropbox-wins"];

    private static readonly JsonSerializerOptions JsonOptions = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Run()
    {
        KeptBothLocations();
        UnknownMoveIgnoreConfirmation();
        ParsesEveryCommand();
        ConfirmationRules();
        RowsAreCoherent();
        CatalogDocuments();
        CatalogLabels();
        RecoveryParsingAndRefusals();
        RecoveryTerminalEvidence();
        WithProductRoot(ResolveOptionValidation);
        WithProductRoot(RecoveryRefusalsBeforeState);
        WithProductRoot(RecoveryReadOnlyDiscovery);
        WithProductRoot(RecoveryDiscardRoundTrip);
        WithProductRoot(DriveChoiceDispatch);
        WithProductRoot(FirstSyncWithValidRoots);
        WithProductRoot(IssueGuidance);
        WithProductRoot(CommandsOnEmptyRoot);
        WithProductRoot(CommandsReportIssueCounts);
        WithProductRoot(CommandsIncludeRequest);
        WithProductRoot(CommandsExcludeRequest);
        WithProductRoot(CommandsThroughTheCli);
        WithProductRoot(LogsViewStreamsAndStructures);
        WithProductRoot(IgnoresCommands);
        PreferenceCommands();
        WithProductRoot(UnboundPreferencesRefuse);
        WithProductRoot(PreferenceResultAndMerge);
        RootBindingResultShape();
        RetryViews();
        WithProductRoot(RetryViewsThroughCommands);
        CapacityAndNoticeRendering();
        HumanAndJson();
        DiagnosticText();
        IssueGrouping();
        JsonSkipsHumanRender();
    }

    // The loader reads exact (case-sensitive) property names and rejects duplicate case-variant keys and numeric
    // enum values, instead of silently resolving one of them.
    private static void CatalogDocuments()
    {
        using var real = CliCatalogResource.Load(typeof(EngineCliCatalog).Assembly, CatalogResource);
        CheckCatalog(real);
        (string Label, Action<JsonObject> Poison)[] poisons =
        [
            ("duplicate key case variant", root => DuplicateCaseVariant(root, "key", "Key")),
            ("duplicate confirm case variant", root => DuplicateCaseVariant(root, "confirm", "Confirm")),
            ("numeric confirm value", root => root["commands"]!.AsArray()[0]!.AsObject()["confirm"] = 1),
        ];
        foreach (var (label, poison) in poisons)
        {
            var root = JsonNode.Parse(real.RootElement.GetRawText())!.AsObject();
            poison(root);
            RejectsCatalog(root, label);
        }
    }

    private static void DuplicateCaseVariant(JsonObject root, string name, string variant)
    {
        var command = root["commands"]!.AsArray()[0]!.AsObject();
        command[variant] = command[name]!.DeepClone();
    }

    private static void RejectsCatalog(JsonNode root, string label)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        try
        {
            CheckCatalog(document);
            Check.Require(false, "catalog fixture should fail: " + label);
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException or JsonException)
        {
        }
    }

    // EngineCliCatalog's document load-and-validate entry point is Core-internal, like OverrideProductRoot below.
    private static void CheckCatalog(JsonDocument document) =>
        typeof(EngineCliCatalog).GetMethod("CheckCatalog", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { document });

    // EngineOperations takes the product lock and the state root; both must stay under a temporary root.
    private static void WithProductRoot(Action<string> body)
    {
        var root = Directory.CreateTempSubdirectory("sync-shark-engine-cli-").FullName;
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

    private static void ParsesEveryCommand()
    {
        var local = Path.Combine(Path.GetTempPath(), "sync-shark-engine-cli-local");
        (EngineCommandKey Key, string Command, string[] Args)[] commands =
        [
            (EngineCommandKey.PairsList, "engine pairs list", ["engine", "pairs", "list"]),
            (EngineCommandKey.PairsAdd, "engine pairs add", ["engine", "pairs", "add", "--name", "n", "--local", local,
                "--path", "/d", "--account", "a", "--first-run", "dropbox-wins", "--yes"]),
            (EngineCommandKey.PairsRemove, "engine pairs remove", ["engine", "pairs", "remove", "--pair", "p", "--yes"]),
            (EngineCommandKey.PairsEnable, "engine pairs enable", ["engine", "pairs", "enable", "--pair", "p", "--yes"]),
            (EngineCommandKey.PairsDisable, "engine pairs disable", ["engine", "pairs", "disable", "--pair", "p", "--yes"]),
            (EngineCommandKey.PairsExclude, "engine pairs exclude", ["engine", "pairs", "exclude", "--pair", "p",
                "--side", "local", "--rel", "a/b", "--keep"]),
            (EngineCommandKey.PairsInclude, "engine pairs include", ["engine", "pairs", "include", "--pair", "p",
                "--side", "dropbox", "--rel", "a/b", "--yes"]),
            (EngineCommandKey.PairsBind, "engine pairs bind", ["engine", "pairs", "bind", "--pair", "p", "--yes"]),
            (EngineCommandKey.Repair, "engine repair", ["engine", "repair", "--pair", "p", "--yes"]),
            (EngineCommandKey.Logs, "engine logs", ["engine", "logs", "--pair", "p", "--run", "r"]),
            (EngineCommandKey.Status, "engine status", ["engine", "status", "--pair", "p"]),
            (EngineCommandKey.Issues, "engine issues", ["engine", "issues", "--pair", "p", "--kind", "name-clash"]),
            (EngineCommandKey.Resolve, "engine resolve", ["engine", "resolve", "--pair", "p", "--issue", "7", "--reviewed", "reviewed",
                "--choice", "rename-incoming", "--name", "x", "--yes"]),
            (EngineCommandKey.Recovery, "engine recovery", ["engine", "recovery", "--pair", "p"]),
            (EngineCommandKey.RecoveryResolve, "engine recovery-resolve", ["engine", "recovery-resolve", "--pair",
                "p", "--operation", "op", "--generation", "2", "--choice", "check", "--yes"]),
            (EngineCommandKey.IgnoresList, "engine ignores list", ["engine", "ignores", "list", "--pair", "p"]),
            (EngineCommandKey.IgnoresSet, "engine ignores set", ["engine", "ignores", "set", "--pair", "p",
                "--rules", "FileName:desktop.ini", "--yes"]),
            (EngineCommandKey.SettingsList, "engine settings list", ["engine", "settings", "list"]),
            (EngineCommandKey.SettingsSet, "engine settings set", ["engine", "settings", "set", "--name",
                SettingName, "--value", IssueValue, "--yes"]),
            (EngineCommandKey.PreferencesShow, "engine preferences show", ["engine", "preferences", "show"]),
            (EngineCommandKey.PreferencesSet, "engine preferences set", ["engine", "preferences", "set",
                "--settings", "{\"theme\":\"Dark\"}", "--yes"]),
            (EngineCommandKey.HostStart, "engine host start", ["engine", "host", "start", "--yes"]),
            (EngineCommandKey.HostStatus, "engine host status", ["engine", "host", "status"]),
            (EngineCommandKey.HostPause, "engine host pause", ["engine", "host", "pause", "--yes"]),
            (EngineCommandKey.HostResume, "engine host resume", ["engine", "host", "resume", "--yes"]),
            (EngineCommandKey.HostStop, "engine host stop", ["engine", "host", "stop", "--yes"]),
        ];
        foreach (var command in commands)
        {
            var invocation = SyncCliRegistry.Parse(["--cli", .. command.Args]);
            Check.Require(invocation.Command == command.Command, "parses: " + command.Command);
            Check.Require(EngineCliCatalog.Owns(command.Command), "owns: " + command.Command);
            Check.Require(EngineCliCatalog.CommandKey(command.Command) == command.Key, "key: " + command.Command);
        }

        var add = SyncCliRegistry.Parse(["--cli", "engine", "pairs", "add", "--name", "n", "--local", local, "--path",
            "/d", "--account", "a", "--first-run", "dropbox-wins", "--yes"]);
        Check.Require(add.Value(EngineCliCatalog.Flag(EngineOptionKey.FirstRun)) == "dropbox-wins", "first-run value");
        Check.Require(add.Value(EngineCliCatalog.Flag(EngineOptionKey.Local)) == local, "local value");

        var exclude = SyncCliRegistry.Parse(["--cli", "engine", "pairs", "exclude", "--pair", "p", "--side", "local",
            "--rel", "a/b", "--keep"]);
        Check.Require(exclude.Flag(EngineCliCatalog.Flag(EngineOptionKey.Keep)), "keep is a flag");
        Check.Require(exclude.Value(EngineCliCatalog.Flag(EngineOptionKey.Keep)) is null, "keep takes no value");
        Check.Require(exclude.Value(EngineCliCatalog.Flag(EngineOptionKey.Rel)) == "a/b", "rel value");

        var resolve = SyncCliRegistry.Parse(["--cli", "engine", "resolve", "--pair", "p", "--issue", "7", "--reviewed", "reviewed",
            "--choice", "keep-folder-always", "--rules", "FileName:desktop.ini", "--yes"]);
        Check.Require(resolve.Value(EngineCliCatalog.Flag(EngineOptionKey.Rules)) == "FileName:desktop.ini",
            "keep-folder-always carries its rules");
        var bind = SyncCliRegistry.Parse(["--cli", "engine", "resolve", "--pair", "p", "--issue", "7", "--reviewed", "reviewed",
            "--choice", "bind-roots", "--yes"]);
        Check.Require(bind.Value(EngineCliCatalog.Flag(EngineOptionKey.Choice)) == "bind-roots",
            "the root action is reachable through the shared resolve catalog");
        var recoveryResolve = SyncCliRegistry.Parse(["--cli", "engine", "recovery-resolve", "--pair", "p",
            "--operation", "op", "--generation", "2", "--choice", "accept-current", "--yes"]);
        Check.Require(recoveryResolve.Generation == 2 &&
            recoveryResolve.Value(EngineCliCatalog.Flag(EngineOptionKey.Operation)) == "op", "recovery options");
        var hostPause = SyncCliRegistry.Parse(["--cli", "engine", "host", "pause", "--pair", "p", "--yes"]);
        Check.Require(hostPause.Value(EngineCliCatalog.Flag(EngineOptionKey.Pair)) == "p", "pair-scoped host pause");
        var ignores = SyncCliRegistry.Parse(["--cli", "engine", "ignores", "set", "--pair", "p", "--folder-keep",
            "--rules", "none", "--yes"]);
        Check.Require(ignores.Flag(EngineCliCatalog.Flag(EngineOptionKey.FolderKeep)), "folder-keep is a flag");

        RejectsUsage("missing --yes", "engine", "repair", "--pair", "p");
        RejectsUsage("missing --pair", "engine", "issues");
        RejectsUsage("missing required option", "engine", "pairs", "exclude", "--pair", "p", "--side", "local");
        RejectsUsage("bad choice value", "engine", "resolve", "--pair", "p", "--issue", "1", "--choice", "use-both",
            "--yes");
        RejectsUsage("bad first-run value", "engine", "pairs", "add", "--name", "n", "--local", local, "--path", "/d",
            "--account", "a", "--first-run", "oldest", "--yes");
        RejectsUsage("unknown engine option", "engine", "pairs", "list", "--kind", "tie");
        RejectsUsage("bind missing --pair", "engine", "pairs", "bind", "--yes");
        RejectsUsage("bind missing --yes", "engine", "pairs", "bind", "--pair", "p");
        RejectsUsage("host start missing --yes", "engine", "host", "start");
        RejectsUsage("ignores set missing --rules", "engine", "ignores", "set", "--pair", "p", "--yes");
        RejectsUsage("preferences set missing --yes", "engine", "preferences", "set", "--settings",
            "{\"theme\":\"Dark\"}");
        RejectsUsage("preferences show takes no pair", "engine", "preferences", "show", "--pair", "p");
    }

    private static void ConfirmationRules()
    {
        (EngineCommandKey Key, bool Mutating, bool RequiresYes, bool RequiresPair)[] rules =
        [
            (EngineCommandKey.PairsList, false, false, false),
            (EngineCommandKey.PairsAdd, true, true, false),
            (EngineCommandKey.PairsRemove, true, true, true),
            (EngineCommandKey.PairsEnable, true, true, true),
            (EngineCommandKey.PairsDisable, true, true, true),
            (EngineCommandKey.PairsExclude, true, false, true),
            (EngineCommandKey.PairsInclude, true, true, true),
            (EngineCommandKey.PairsBind, true, true, true),
            (EngineCommandKey.Repair, true, true, true),
            (EngineCommandKey.Logs, false, false, true),
            (EngineCommandKey.Status, false, false, false),
            (EngineCommandKey.Issues, false, false, true),
            (EngineCommandKey.Resolve, true, true, true),
            (EngineCommandKey.Recovery, false, false, true),
            (EngineCommandKey.RecoveryResolve, true, true, true),
            (EngineCommandKey.IgnoresList, false, false, true),
            (EngineCommandKey.IgnoresSet, true, true, true),
            (EngineCommandKey.SettingsList, false, false, false),
            (EngineCommandKey.SettingsSet, true, true, false),
            (EngineCommandKey.PreferencesShow, false, false, false),
            (EngineCommandKey.PreferencesSet, true, true, false),
            (EngineCommandKey.HostStart, true, true, false),
            (EngineCommandKey.HostStatus, false, false, false),
            (EngineCommandKey.HostPause, true, true, false),
            (EngineCommandKey.HostResume, true, true, false),
            (EngineCommandKey.HostStop, true, true, false),
        ];
        Check.Require(EngineCliCatalog.Rows.Count == Enum.GetValues<EngineCommandKey>().Length,
            "one row per command key");
        foreach (var rule in rules)
        {
            var row = EngineCliCatalog.Rows.Single(candidate => EngineCliCatalog.CommandKey(candidate.Command) == rule.Key);
            Check.Require(row.Mutating == rule.Mutating, "mutating: " + rule.Key);
            Check.Require(row.RequiresYes == rule.RequiresYes, "requires --yes: " + rule.Key);
            Check.Require(row.RequiresPair == rule.RequiresPair, "requires --pair: " + rule.Key);
            Check.Require(row.Approval.Length > 0, "approval word: " + rule.Key);
        }
    }

    // P2-11: the registry row's usage, options and confirmation rule must agree, so a capacity refusal or any
    // other path cannot be described by a row that names different options than it accepts.
    private static void RowsAreCoherent()
    {
        foreach (var row in EngineCliCatalog.Rows)
        {
            var existingHostOnly = EngineCliCatalog.CommandKey(row.Command) is
                EngineCommandKey.PreferencesShow or EngineCommandKey.PreferencesSet;
            Check.Require(row.Mode == (existingHostOnly ? "existing-host-only" : "direct"),
                "the host-only mode applies to the preference rows only: " + row.Command);
            if (existingHostOnly)
                Check.Require(row.Requires.Contains("running Engine host", StringComparison.Ordinal),
                    "a preference row names its existing-host requirement: " + row.Command);
            Check.Require(row.Usage == row.Command || row.Usage.StartsWith(row.Command + " ",
                StringComparison.Ordinal), "usage names its command: " + row.Command);
            foreach (var flag in row.Options.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                      StringSplitOptions.TrimEntries))
                Check.Require(row.Usage.Contains(flag, StringComparison.Ordinal),
                    "usage lists option " + flag + ": " + row.Command);
            Check.Require(row.Approval.Length > 0 && row.Mutating == (row.Approval != "none") &&
                row.RequiresYes == (row.Approval == "yes"),
                "confirmation agrees with the approval word: " + row.Command);
        }
        var repair = EngineCliCatalog.Rows.Single(row =>
            EngineCliCatalog.CommandKey(row.Command) == EngineCommandKey.Repair);
        Check.Require(repair.Mutating && repair.RequiresYes && repair.RequiresPair &&
            repair.Usage.Contains("--pair <exact-id>", StringComparison.Ordinal) &&
            repair.Usage.Contains("--yes", StringComparison.Ordinal),
            "repair keeps its exact target and confirmation");
    }

    // F014.fix3: the catalogue enums and the data file are one coordinated set; the loader already proves the key
    // sets, so these pin the new choice labels that the UI and CLI both render.
    private static void CatalogLabels()
    {
        (EngineMessageKey Key, string Text)[] labels =
        [
            (EngineMessageKey.KeepThisTimeLabel, "Keep this time"),
            (EngineMessageKey.KeepFolderAlwaysLabel, "Always keep folders with these ignored files"),
            (EngineMessageKey.RecoveryCheckLabel, "Check recovery"),
            (EngineMessageKey.RecoveryAcceptCurrentLabel, "Accept current recovery state"),
            (EngineMessageKey.RecoveryAbandonIncomingLabel, "Abandon incoming recovery"),
        ];
        foreach (var (key, text) in labels)
            Check.Require(EngineCliCatalog.Message(key) == text, "catalogue label: " + key);
        foreach (var key in Enum.GetValues<EngineMessageKey>())
            Check.Require(EngineCliCatalog.Message(key).Length > 0, "every message key has text: " + key);
    }

    // F023.fix1: recovery requests are structurally refused by the shared parser before any state access.
    private static void RecoveryDiscardRoundTrip(string root)
    {
        var database = StateStore.DatabasePath(root);
        var pair = new PairConfig("pair-a", "Pair A", Path.Combine(root, "local"), "/Remote", "account-a",
            [], [], FirstRunPolicy.NewestWins, false, null);
        var folder = new Entry("Folder", true, 0, DateTimeOffset.UnixEpoch, "folder-a", "rev-a", "", null);
        var selected = new Entry("Folder/Child", false, 4, DateTimeOffset.UnixEpoch, "child-a", "rev-a", "hash-a", null);
        string operationId;
        long ordinal;
        using (var store = StateStore.Open(database))
        {
            store.SavePair(pair);
            var context = new RowContext(false, false, false, null, false, false, false, false);
            var row = Row.ForOneSide(Side.Dropbox, PathNames.Key(folder.Path), folder, null, [], null, context);
            using var plan = store.OpenFolderPlan(pair.PairId, row, Side.Dropbox);
            plan.CaptureAsync(Empty(), CancellationToken.None).GetAwaiter().GetResult();
            foreach (var child in plan.Children(CancellationToken.None))
                plan.MarkClassified(child.Key, child.NativeKey, new FolderChild(child.Entry, child.Synced, FolderVerdict.Removed),
                    false, CancellationToken.None);
            plan.Complete(true, CancellationToken.None);
            operationId = store.BeginFolderRemoval(pair.PairId, plan, StateSchema.OneRow, CancellationToken.None);
            var operation = store.FolderRemoval(pair.PairId, operationId);
            var item = store.InsertFolderMember(pair.PairId, operationId, operation.Generation,
                FolderRemovalItem.ProvenanceKind.Unexpected, selected.Path, selected, selected);
            ordinal = item.Ordinal;
            var allocated = item with { Generation = item.Generation + 1, Phase = FolderRemovalItem.RecoveryPhase.AllocateArmed,
                StageAllocation = new StageAllocation(operationId, ordinal, "allocation-a", Path.Combine(root, "stage-a")) };
            store.SettleFolderRemovalItem(pair.PairId, operationId, ordinal, item.Generation, allocated, StateChange.Empty);
            var staged = allocated with { Generation = allocated.Generation + 1, Phase = FolderRemovalItem.RecoveryPhase.Staged,
                Stage = new StageProof(allocated.StageAllocation!.Path, "volume-a", "file-a", selected.Size, selected.ContentHash) };
            store.SettleFolderRemovalItem(pair.PairId, operationId, ordinal, allocated.Generation, staged, StateChange.Empty);
            var abandoned = staged.BeginTerminalDiscard() with { Generation = staged.Generation + 1 };
            store.SettleFolderRemovalItem(pair.PairId, operationId, ordinal, staged.Generation, abandoned, StateChange.Empty);
        }
        // Both interruption points retain the same durable intent: before discard, and after discard before settlement.
        foreach (var afterDiscard in new[] { false, true })
        {
            using var store = StateStore.Open(database);
            var item = store.FolderRemovalItems(pair.PairId, operationId, long.MinValue, 100).Single(value => value.Ordinal == ordinal);
            Check.Require(item.IsTerminalDiscard && item.Selected == selected && item.Result is null && item.Stage is not null,
                "real decoder retains terminal abandonment across interruption " + afterDiscard);
            Check.Require(item.SettledRecovery().Phase == FolderRemovalItem.RecoveryPhase.Settled,
                "persisted abandonment has a terminal continuation, never a reselected incoming version");
        }
        using (var store = StateStore.Open(database))
        {
            var operation = store.FolderRemoval(pair.PairId, operationId);
            var item = store.InsertFolderMember(pair.PairId, operationId, operation.Generation,
                FolderRemovalItem.ProvenanceKind.Unexpected, "Folder/Absent", selected with { Path = "Folder/Absent" }, selected with { Path = "Folder/Absent" });
            var allocated = item with { Generation = item.Generation + 1, Phase = FolderRemovalItem.RecoveryPhase.AllocateArmed,
                StageAllocation = new StageAllocation(operationId, item.Ordinal, "allocation-b", Path.Combine(root, "absent-stage")) };
            store.SettleFolderRemovalItem(pair.PairId, operationId, item.Ordinal, item.Generation, allocated, StateChange.Empty);
            store.SettleFolderRemovalItem(pair.PairId, operationId, item.Ordinal, allocated.Generation,
                allocated.SettledRecovery() with { Generation = allocated.Generation + 1 }, StateChange.Empty);
            var decoded = store.FolderRemovalItems(pair.PairId, operationId, long.MinValue, 100).Single(value => value.Ordinal == item.Ordinal);
            Check.Require(decoded.Phase == FolderRemovalItem.RecoveryPhase.Settled && decoded.StageAllocation is null,
                "absent allocation settles directly from AllocateArmed through real Store decoder");
        }
        static async IAsyncEnumerable<Listed> Empty() { await Task.CompletedTask; yield break; }
    }

    private static void RecoveryTerminalEvidence()
    {
        var item = new FolderRemovalItem("operation-a", 0, 7, FolderRemovalItem.ProvenanceKind.Planned,
            "Folder/Child", PathNames.Key("Folder/Child"), null, null, FolderVerdict.Survives,
            false, FolderRemovalItem.FollowUpStatus.None, false, FolderEffectKind.None,
            FolderRemovalItem.EffectStatus.Unknown, 4, null, null, null,
            FolderRemovalItem.RecoveryPhase.Unknown, null, null, null, null, TextKey.FailChanged);
        var view = new RecoveryView(item.OperationId, 3, "Folder", FolderRemovalPhase.Unknown,
            FolderRemovalStatus.Unknown, TextKey.FailChanged, null, [RecoveryChoice.Check]);
        var settled = item.SettledRecovery();
        Check.Require(settled.Phase == FolderRemovalItem.RecoveryPhase.Settled &&
            settled.EffectState == FolderRemovalItem.EffectStatus.Unknown && settled.EffectGeneration == 4 &&
            settled.Generation == 7 && settled.Cause == item.Cause && settled.Result == item.Result,
            "acknowledgement preserves unknown effect evidence and leaves member CAS generation to Store caller");
        var verified = (item with { EffectState = FolderRemovalItem.EffectStatus.Verified }).SettledRecovery();
        Check.Require(verified.EffectState == FolderRemovalItem.EffectStatus.Settled && verified.EffectGeneration == 5,
            "only a verified effect advances to settled evidence");
        Check.Require(FolderRetirement.For([item]) == FolderRetirementDisposition.Acknowledged &&
            !FolderRetirement.CanAcknowledge([item]) && FolderRetirement.CanAcknowledge([settled]),
            "unknown member requires explicit terminal disposition before operation acknowledgement");
        foreach (var state in Enum.GetValues<FolderRemovalItem.EffectStatus>())
        {
            var candidate = item with { EffectState = state };
            var expected = state is FolderRemovalItem.EffectStatus.None or FolderRemovalItem.EffectStatus.Ready or
                FolderRemovalItem.EffectStatus.NoEffect ? FolderRetirementDisposition.NoEffect : FolderRetirementDisposition.Acknowledged;
            Check.Require(FolderRetirement.For([candidate]) == expected, "retirement classification preserves every effect state");
            if (state is FolderRemovalItem.EffectStatus.Armed or FolderRemovalItem.EffectStatus.Accepted)
                Check.Require(IssuePresentation.RecoveryChoices(view, candidate).Count == 0,
                    "unjoined jobs offer no member terminal action");
        }
        var allocation = item with { StageAllocation = new StageAllocation(item.OperationId, item.Ordinal,
            "allocation-a", @"D:\Recovery\allocated.tmp") };
        Check.Require(IssuePresentation.RecoveryChoices(view, allocation).SequenceEqual([RecoveryChoice.AbandonIncoming]) &&
            !FolderRetirement.CanAcknowledge([allocation.SettledRecovery() with { StageAllocation = allocation.StageAllocation }]),
            "a locator permits only explicit abandon inspection and blocks operation release");
        Check.Require(IssuePresentation.RecoveryChoices(view, settled).Count == 0,
            "a settled member does not offer another terminal action");
        var mixed = new List<FolderRemovalItem>
        {
            item with { EffectState = FolderRemovalItem.EffectStatus.Ready },
            item with { EffectState = FolderRemovalItem.EffectStatus.Accepted },
            item with { EffectState = FolderRemovalItem.EffectStatus.Verified },
            settled,
            allocation.SettledRecovery() with { StageAllocation = allocation.StageAllocation }
        };
        var singlePass = new CountingSequence<FolderRemovalItem>(mixed);
        var (disposition, acknowledge) = FolderRetirement.Evaluate(singlePass);
        Check.Require(singlePass.Enumerations == 1 && disposition == FolderRetirement.For(mixed) &&
            acknowledge == FolderRetirement.CanAcknowledge(mixed),
            "retirement's two answers come from one enumeration of the same mixed members");
        // BD-3: both answers are final at a first blocked member, so its tail is never consumed.
        var blockedFirst = new CountingSequence<FolderRemovalItem>(
        [
            item with { EffectState = FolderRemovalItem.EffectStatus.Accepted },
            item with { EffectState = FolderRemovalItem.EffectStatus.Ready },
            settled
        ]);
        Check.Require(FolderRetirement.Evaluate(blockedFirst) ==
                (FolderRetirementDisposition.Acknowledged, false) && blockedFirst.Consumed == 1,
            "a first Accepted member ends the walk without consuming its tail");
        var empty = new CountingSequence<FolderRemovalItem>([]);
        Check.Require(FolderRetirement.Evaluate(empty) == (FolderRetirementDisposition.NoEffect, true) &&
            empty.Consumed == 0, "an empty operation is unsent and acknowledgeable");
        var unsent = new CountingSequence<FolderRemovalItem>(
        [
            item with { EffectState = FolderRemovalItem.EffectStatus.None },
            item with { EffectState = FolderRemovalItem.EffectStatus.Ready },
            item with { EffectState = FolderRemovalItem.EffectStatus.NoEffect }
        ]);
        Check.Require(FolderRetirement.Evaluate(unsent) == (FolderRetirementDisposition.NoEffect, true) &&
            unsent.Consumed == 3, "every proven-unsent member is read when both answers stay open");
        var lateBlocker = new CountingSequence<FolderRemovalItem>(
        [
            item with { EffectState = FolderRemovalItem.EffectStatus.Ready },
            item with { EffectState = FolderRemovalItem.EffectStatus.Accepted }
        ]);
        Check.Require(FolderRetirement.Evaluate(lateBlocker) ==
                (FolderRetirementDisposition.Acknowledged, false) && lateBlocker.Consumed == 2,
            "a late blocker is read and then stops the walk");
        Check.Require(IssuePresentation.RecoveryResult(new RemovalResult(RemovalOutcome.Published,
            item.OperationId, null, null), item.Path).Contains(Text.Get(TextKey.RecoveryPublishedBytesLimits), StringComparison.Ordinal),
            "publication guidance does not promise restored identity or history");
    }

    private static void RecoveryParsingAndRefusals()
    {
        var page = SyncCliRegistry.Parse(["--cli", "engine", "recovery", "--pair", "p", "--operation", "op",
            "--after", "7"]);
        Check.Require(page.Value(EngineCliCatalog.Flag(EngineOptionKey.After)) == "7" &&
            page.Value(EngineCliCatalog.Flag(EngineOptionKey.Operation)) == "op", "recovery page options");
        var afterOperation = SyncCliRegistry.Parse(["--cli", "engine", "recovery", "--pair", "p",
            "--after-operation", "op"]);
        Check.Require(afterOperation.Value(EngineCliCatalog.Flag(EngineOptionKey.AfterOperation)) == "op",
            "recovery list continuation");
        var legacy = SyncCliRegistry.Parse(["--cli", "engine", "recovery-resolve", "--pair", "p", "--operation",
            "op", "--generation", "0", "--choice", "check", "--yes"]);
        Check.Require(legacy.Generation == 0, "legacy generation zero is accepted");
        var member = SyncCliRegistry.Parse(["--cli", "engine", "recovery-resolve", "--pair", "p", "--operation",
            "op", "--generation", "2", "--choice", "check", "--item", "1", "--item-generation", "3", "--yes"]);
        Check.Require(member.Generation == 2, "recovery generation");

        RejectsUsage("recovery wildcard operation", "engine", "recovery", "--pair", "p", "--operation", "*");
        RejectsUsage("recovery after without operation", "engine", "recovery", "--pair", "p", "--after", "1");
        RejectsUsage("recovery operation with after-operation", "engine", "recovery", "--pair", "p", "--operation",
            "op", "--after-operation", "other");
        RejectsUsage("recovery non-numeric after", "engine", "recovery", "--pair", "p", "--operation", "op",
            "--after", "x");
        RejectsUsage("recovery core without generation", "engine", "recovery-resolve", "--pair", "p", "--operation",
            "op", "--choice", "check", "--yes");
        RejectsUsage("recovery core without operation", "engine", "recovery-resolve", "--pair", "p", "--generation",
            "2", "--choice", "check", "--yes");
        RejectsUsage("recovery core without choice", "engine", "recovery-resolve", "--pair", "p", "--operation",
            "op", "--generation", "2", "--yes");
        RejectsUsage("recovery core without yes", "engine", "recovery-resolve", "--pair", "p", "--operation", "op",
            "--generation", "2", "--choice", "check");
        RejectsUsage("recovery item without item-generation", "engine", "recovery-resolve", "--pair", "p",
            "--operation", "op", "--generation", "2", "--choice", "check", "--item", "1", "--yes");
        RejectsUsage("recovery overflowing generation", "engine", "recovery-resolve", "--pair", "p", "--operation",
            "op", "--generation", "9223372036854775808", "--choice", "check", "--yes");
    }

    private static void RecoveryRefusalsBeforeState(string root)
    {
        var commands = new EngineCommands(new EngineOperations(root, _ => { }));
        foreach (var choice in new[] { "use-local", "keep-both", "retry", "first-sync", "keep-folder-always", "bind-roots" })
        {
            var invocation = SyncCliRegistry.Parse(["--cli", "engine", "recovery-resolve", "--pair", "p",
                "--operation", "op", "--generation", "2", "--choice", choice, "--yes"]);
            RejectsResolveOptions("recovery-resolve refuses a resolve choice before state: " + choice,
                () => Await(commands.RunAsync(invocation, CancellationToken.None)));
        }
        foreach (var choice in new[] { "check", "accept-current", "abandon-incoming" })
        {
            var invocation = SyncCliRegistry.Parse(["--cli", "engine", "resolve", "--pair", "p", "--issue", "7", "--reviewed", "reviewed",
                "--choice", choice, "--yes"]);
            RejectsResolveOptions("resolve refuses a recovery choice before state: " + choice,
                () => Await(commands.RunAsync(invocation, CancellationToken.None)));
        }
        Check.Require(Directory.GetFileSystemEntries(root).Length == 0,
            "recovery and resolve choice refusals never create state");
    }

    // CV-RECOVERY read-only discovery: an exact pair with no active operation lists an empty page and writes nothing.
    private static void RecoveryReadOnlyDiscovery(string root)
    {
        var pair = new PairConfig("p", "Pair A", "/local", "/dropbox", "acc", [], [], FirstRunPolicy.NewestWins,
            true, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(root))) store.SavePair(pair);
        var database = StateStore.DatabasePath(root);
        var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        // A read may leave SQLite's two sidecars, but it must change no logical row or schema version; main-file
        // bytes alone can stay identical while a write hides in the WAL.
        var beforeLogical = LogicalDatabase(database);
        Check.Require(beforeLogical.Contains(pair.PairId),
            "the logical oracle reads the seeded pair row");
        var databaseHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database));
        var commands = new EngineCommands(new EngineOperations(root, _ => { }));
        var views = Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "recovery", "--pair",
            pair.PairId]), CancellationToken.None));
        Check.Require(views is IReadOnlyList<RecoveryView> { Count: 0 },
            "recovery lists no active operation");
        var output = new StringWriter();
        var exit = Await(SyncCli.ExecuteAsync(["--cli", "engine", "recovery", "--pair", pair.PairId, "--json"],
            output, new StringWriter(), () => new EngineCliRuntime()));
        Check.Require(exit == (int)SyncCliExitCode.Success &&
            output.ToString().Contains("\"data\":[]", StringComparison.Ordinal),
            "recovery inspection returns the empty typed page");
        var after = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        var afterHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database));
        var afterLogical = LogicalDatabase(database);
        bool EnginePath(string path) => path != database + "-shm" && path != database + "-wal";
        Check.Require(before.Where(EnginePath).SequenceEqual(after.Where(EnginePath)),
            "recovery inspection changes no path except SQLite sidecars; added: " +
            string.Join(", ", after.Except(before).Where(EnginePath)) + "; removed: " +
            string.Join(", ", before.Except(after).Where(EnginePath)));
        Check.Require(beforeLogical == afterLogical,
            "recovery inspection preserves every table and the schema version");
        Check.Require(databaseHash.SequenceEqual(afterHash),
            "recovery inspection preserves database bytes");
    }

    // The read-only contract is logical. The schema version and every table's rows (including generated virtual
    // columns) are read through a plain connection, never immutable.
    private static string LogicalDatabase(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        connection.CreateFunction(Massdex.SyncShark.Engine.Storage.StateSchema.PathKeyFunction,
            (string? path) => path is null ? null : PathNames.Key(path), isDeterministic: true);
        var text = new System.Text.StringBuilder();
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            text.Append("user_version=").Append(version.ExecuteScalar()).Append('\n');
        }
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA schema_version;";
            text.Append("schema_version=").Append(schema.ExecuteScalar()).Append('\n');
        }
        var names = new List<string>();
        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            using var reader = tables.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        foreach (var name in names)
        {
            text.Append('[').Append(name).Append("]\n");
            using var rows = connection.CreateCommand();
            using var columns = connection.CreateCommand();
            columns.CommandText = "SELECT name FROM pragma_table_info($table) ORDER BY cid;";
            columns.Parameters.AddWithValue("$table", name);
            var keys = new List<string>();
            using (var fields = columns.ExecuteReader())
                while (fields.Read()) keys.Add("\"" + fields.GetString(0).Replace("\"", "\"\"") + "\"");
            rows.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\" ORDER BY " + string.Join(',', keys) + ";";
            using var reader = rows.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    text.Append(reader.IsDBNull(i) ? "<null>"
                            : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))
                        .Append('|');
                text.Append('\n');
            }
        }
        return text.ToString();
    }

    private static void RejectsResolveOptions(string label, Action action)
    {
        try
        {
            action();
            Check.Require(false, label);
        }
        catch (SyncCliUsageException error)
        {
            Check.Require(error.Message == EngineCliCatalog.Message(EngineMessageKey.ResolveOptionsInvalid),
                label + " message");
        }
    }

    private static string VisibilityGuidance(Issue issue, string pairId)
    {
        var choice = issue.Ignored ? ResolutionChoice.Unignore : ResolutionChoice.Ignore;
        var label = issue.Ignored ? Text.Get(TextKey.RestoreLabel) : EngineCliCatalog.OptionValue(EngineOptionKey.Choice, choice.ToString());
        return Text.Get(TextKey.ResultIssueVisibilityChoice, (TextArg.Name, label), (TextArg.Pair, pairId),
            (TextArg.Id, issue.Id.ToString(CultureInfo.InvariantCulture)), (TextArg.Value, issue.Reviewed ?? string.Empty),
            (TextArg.Choice, EngineCliCatalog.OptionValue(EngineOptionKey.Choice, choice.ToString()))) +
            Environment.NewLine + IssuePresentation.ResolutionConfirmation(issue, choice);
    }

    private static void ResolveOptionValidation(string root)
    {
        var progress = 0;
        var commands = new EngineCommands(new EngineOperations(root, _ => progress++));
        foreach (var choice in ResolveChoices)
        foreach (var issue in new[] { false, true })
        foreach (var all in new[] { false, true })
        foreach (var firstRun in FirstRunChoices)
        {
            string[] args = ["--cli", "engine", "resolve", "--pair", "p", "--choice", choice, "--yes",
                .. (issue ? new[] { "--issue", "7", "--reviewed", "reviewed" } : []), .. (all ? new[] { "--all" } : []),
                .. (firstRun is null ? [] : new[] { "--first-run", firstRun })];
            var invocation = SyncCliRegistry.Parse(args);
            Check.Require(invocation.Value(EngineCliCatalog.Flag(EngineOptionKey.FirstRun)) == firstRun,
                "first-run option is preserved");
            if (issue != all && (!all || choice == "retry") && (firstRun is null || choice == "first-sync"))
                continue;
            try
            {
                Await(commands.RunAsync(invocation, CancellationToken.None));
                Check.Require(false, "invalid resolve combination: " + string.Join(' ', args));
            }
            catch (SyncCliUsageException error)
            {
                Check.Require(error.Message == EngineCliCatalog.Message(EngineMessageKey.ResolveOptionsInvalid),
                    "invalid combination uses its specific diagnostic");
            }
            var output = new StringWriter();
            var exit = Await(SyncCli.ExecuteAsync([.. args, "--json"], output, new StringWriter(),
                () => new EngineCliRuntime()));
            Check.Require(exit == (int)SyncCliExitCode.Usage && output.ToString().Contains(
                EngineCliCatalog.Message(EngineMessageKey.ResolveOptionsInvalid), StringComparison.Ordinal),
                "invalid combination returns a JSON usage error");
            Check.Require(progress == 0 && Directory.GetFileSystemEntries(root).Length == 0,
                "invalid combination never opens operations, state or providers");
        }
        foreach (var choice in ResolveChoices)
        foreach (var issue in new[] { "x", "9223372036854775808", "-9223372036854775809" })
        {
            var invocation = SyncCliRegistry.Parse(["--cli", "engine", "resolve", "--pair", "p", "--issue", issue, "--reviewed", "reviewed",
                "--choice", choice, "--yes"]);
            RejectsUsageWhenRun("invalid issue number: " + choice + "/" + issue,
                () => Await(commands.RunAsync(invocation, CancellationToken.None)));
            Check.Require(progress == 0 && Directory.GetFileSystemEntries(root).Length == 0,
                "invalid number never opens operations, state or providers");
        }

        // Keep-folder-always is the only choice with rules; each rule of its contract is refused before state.
        (string Label, string[] Args)[] keepRules =
        [
            ("keep-folder-always needs rules", ["--cli", "engine", "resolve", "--pair", "p", "--issue", "7", "--reviewed", "reviewed",
                "--choice", "keep-folder-always", "--yes"]),
            ("keep-folder-always needs a nonempty rules value", ["--cli", "engine", "resolve", "--pair", "p",
                "--issue", "7", "--reviewed", "reviewed", "--choice", "keep-folder-always", "--rules", "", "--yes"]),
            ("rules belong only to keep-folder-always", ["--cli", "engine", "resolve", "--pair", "p", "--issue",
                "7", "--reviewed", "reviewed", "--choice", "use-local", "--rules", "FileName:x", "--yes"]),
            ("keep-folder-always takes no name", ["--cli", "engine", "resolve", "--pair", "p", "--issue", "7", "--reviewed", "reviewed",
                "--choice", "keep-folder-always", "--rules", "FileName:x", "--name", "x", "--yes"]),
            ("keep-folder-always takes no first-run", ["--cli", "engine", "resolve", "--pair", "p", "--issue",
                "7", "--reviewed", "reviewed", "--choice", "keep-folder-always", "--rules", "FileName:x", "--first-run", "newest-wins",
                "--yes"]),
            ("keep-folder-always refuses --all", ["--cli", "engine", "resolve", "--pair", "p", "--all",
                "--choice", "keep-folder-always", "--rules", "FileName:x", "--yes"]),
            ("keep-folder-always needs an exact issue", ["--cli", "engine", "resolve", "--pair", "p",
                "--choice", "keep-folder-always", "--rules", "FileName:x", "--yes"]),
        ];
        foreach (var (label, args) in keepRules)
        {
            RejectsResolveOptions(label, () => Await(commands.RunAsync(
                SyncCliRegistry.Parse(args), CancellationToken.None)));
            var output = new StringWriter();
            var exit = Await(SyncCli.ExecuteAsync([.. args, "--json"], output, new StringWriter(),
                () => new EngineCliRuntime()));
            Check.Require(exit == (int)SyncCliExitCode.Usage && output.ToString().Contains(
                EngineCliCatalog.Message(EngineMessageKey.ResolveOptionsInvalid), StringComparison.Ordinal),
                label + " returns a JSON usage error");
            Check.Require(progress == 0 && Directory.GetFileSystemEntries(root).Length == 0,
                label + " never opens operations, state or providers");
        }

        // A well-formed keep-folder-always request passes the edge and reaches the Run owner; a missing pair is then
        // a request refusal, not a usage error, and nothing is created.
        var valid = SyncCliRegistry.Parse(["--cli", "engine", "resolve", "--pair", "missing", "--issue", "7", "--reviewed", "reviewed",
            "--choice", "keep-folder-always", "--rules", "FileName:x", "--yes"]);
        try
        {
            Await(commands.RunAsync(valid, CancellationToken.None));
            Check.Require(false, "a well-formed keep-folder-always request must reach the Run owner");
        }
        catch (EngineRequestException)
        {
        }
        Check.Require(progress == 0 && Directory.GetFileSystemEntries(root).Length == 0,
            "a keep-folder-always request on a missing pair creates nothing");
        var bind = SyncCliRegistry.Parse(["--cli", "engine", "resolve", "--pair", "missing", "--issue", "7", "--reviewed", "reviewed",
            "--choice", "bind-roots", "--yes"]);
        try
        {
            Await(commands.RunAsync(bind, CancellationToken.None));
            Check.Require(false, "bind-roots must reach the Run owner");
        }
        catch (EngineRequestException) { }
        Check.Require(progress == 0 && Directory.GetFileSystemEntries(root).Length == 0,
            "a bind-roots request on a missing pair creates nothing");
    }

    private static void FirstSyncWithValidRoots(string root)
    {
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var connection = DropboxConnectionBinding.CreateConnectionId("account-a");
        var binding = new RootBinding(local, "/Remote", connection, Massdex.SyncShark.WindowsDirectoryListing.ReadIdentity(local),
            new DropboxRootIdentity("account-a", "namespace-a", "namespace-a", "folder-a"));
        var pair = new PairConfig("pair-a", "Pair A", local, "/Remote", connection, [], [],
            FirstRunPolicy.DropboxWins, false, new DriveEvidence(1, 10, 1), binding);
        using var settingsDoc = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        var settings = SettingsParser.Parse(settingsDoc.RootElement.GetRawText(), null);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(new DropboxConnectionBinding(
            DropboxProviderConfiguration.RegisteredClientId, "account-a", "namespace-a", "namespace-a",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            ConnectionId: pair.ConnectionId)).GetAwaiter().GetResult();
        using var handler = new ConfigurationRootsHandler();
        using var http = new HttpClient(handler);
        var host = EngineHost.Compose(root, _ => { }, instance.StateAuthority, settings, http, TimeProvider.System,
            _ => throw new InvalidOperationException("configuration reset must not open a run log"),
            validator => StateStore.Open(StateStore.DatabasePath(root), validator), EngineHost.Mode.Direct,
            new EngineExternals(new SyntheticCredentials(), _ => new HttpClient(handler, disposeHandler: false)));
        try
        {
            host.Store.SavePair(pair);
            host.Store.Commit(pair.PairId, StateChange.Empty with
            { Raise = [(IssueKind.DriveChange, TextKey.DriveRestored, "", new IssueSubject([], null))] });
            var record = new SyncedEntry(PathNames.Key("old"), "old", PathNames.DropboxKey("old"), "old", false,
                "local-a", 1, 1, "remote-a", "hash-a");
            host.Store.Commit(pair.PairId, StateChange.Empty with { Record = [record] });
            // Only a full completed Repair sets the bit, so the fixture seeds that one fact directly.
            using (var seed = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = StateStore.DatabasePath(root), Pooling = false }.ToString()))
            {
                seed.Open();
                using var update = seed.CreateCommand();
                update.CommandText = "UPDATE pair SET first_repair_done=1;";
                update.ExecuteNonQuery();
            }
            var generation = host.Store.ReadPairExecution(pair.PairId)!.Value.Generation;
            Check.Require(host.Store.FirstRepairDone(pair.PairId), "the first-sync fixture starts with Repair history");
            var issue = host.Operations.Issues(pair.PairId, null).Single();
            var result = (ResolveResult)Await(new EngineCommands(host.Operations).RunAsync(SyncCliRegistry.Parse(
                ["--cli", "engine", "resolve", "--pair", pair.PairId, "--issue", issue.Id.ToString(CultureInfo.InvariantCulture), "--reviewed", issue.Reviewed!,
                 "--choice", "first-sync", "--first-run", "local-wins", "--yes"]), CancellationToken.None));
            var saved = host.Store.Pair(pair.PairId)!;
            Check.Require(host.Store.SyncedByKey(pair.PairId, Side.Local, record.LocalKey) is null &&
                !host.Store.FirstRepairDone(pair.PairId) &&
                host.Store.ReadPairExecution(pair.PairId)!.Value.Generation == generation + 1,
                "first-sync clears saved records and the first-repair bit in one generation step");
            Check.Require(result.Done && saved.Roots == binding && saved.FirstRun == FirstRunPolicy.LocalWins &&
                saved.Drive is null && host.Store.IssueById(pair.PairId, issue.Id) is null &&
                host.Store.LastRepair(pair.PairId) is null && handler.Accounts == 2 && handler.Metadata == 1,
                "first-sync records once after one valid bilateral root acquisition, preserving binding without Repair; " +
                JsonSerializer.Serialize(new { result.Done, saved.Roots, Expected = binding, saved.FirstRun,
                    saved.Drive, Issue = host.Store.IssueById(pair.PairId, issue.Id),
                    Repair = host.Store.LastRepair(pair.PairId), handler.Accounts, handler.Metadata }));
        }
        finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private sealed class ConfigurationRootsHandler : HttpMessageHandler
    {
        internal int Accounts;
        internal int Metadata;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            object body;
            if (request.RequestUri!.AbsolutePath == "/oauth2/token")
                body = new { access_token = "synthetic-access", token_type = "bearer", expires_in = 14400 };
            else if (request.RequestUri.AbsolutePath.EndsWith("get_current_account", StringComparison.Ordinal))
            {
                Accounts++;
                body = new { account_id = "account-a", root_info = new { root_namespace_id = "namespace-a", home_namespace_id = "namespace-a" } };
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("get_metadata", StringComparison.Ordinal))
            {
                Metadata++;
                body = new Dictionary<string, object> { [".tag"] = "folder", ["name"] = "Remote",
                    ["path_lower"] = "/remote", ["path_display"] = "/Remote", ["id"] = "folder-a" };
            }
            else throw new InvalidOperationException("configuration command issued an unexpected provider route");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") });
        }
    }

    public static void ReviewedNoticeChoices() => WithProductRoot(ReviewedNoticeChoices);

    private static void ReviewedNoticeChoices(string root)
    {
        var pair = new PairConfig("reviewed", "Pair A", Path.Combine(root, "local"), "/Remote", "account", [], [],
            FirstRunPolicy.NewestWins, false, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(root)))
        {
            store.SavePair(pair);
            store.Commit(pair.PairId, StateChange.Empty with { Raise = [(IssueKind.Info,
                TextKey.InfoIgnoredFolderKept, "folder", new IssueSubject([new(Side.Dropbox, "folder", "old")], null))] });
        }
        var operations = new EngineOperations(root, _ => { });
        var reviewed = operations.Issues(pair.PairId, null).Single();
        using (var store = StateStore.Open(StateStore.DatabasePath(root)))
            store.Commit(pair.PairId, StateChange.Empty with { Raise = [(IssueKind.Info,
                TextKey.InfoIgnoredFolderKept, "folder", new IssueSubject([new(Side.Dropbox, "folder", "replacement")], null))] });
        var current = operations.Issues(pair.PairId, null).Single();
        Check.Require(current.Id == reviewed.Id && current.Reviewed != reviewed.Reviewed,
            "a replacement retains its id but changes the displayed reviewed token");
        foreach (var stale in new[] { true, false })
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = Await(SyncCli.ExecuteAsync(["--cli", "engine", "resolve", "--pair", pair.PairId,
                "--issue", current.Id.ToString(CultureInfo.InvariantCulture), "--reviewed",
                (stale ? reviewed : current).Reviewed!, "--choice", "ignore", "--yes"], output, error,
                () => new EngineCliRuntime()));
            using var store = StateStore.OpenReadOnly(StateStore.DatabasePath(root))!;
            Check.Require(store.IssueById(pair.PairId, current.Id)!.Ignored == !stale,
                "CLI applies only the current reviewed notice, never a replacement subject");
            Check.Require(stale ? exit != 0 && (output.ToString() + error).Contains(Text.Get(TextKey.ResolveStale,
                (TextArg.Id, current.Id.ToString(CultureInfo.InvariantCulture))), StringComparison.Ordinal) : exit == 0,
                "CLI reports stale refusal or current-token success");
        }
        var commands = new EngineCommands(operations);
        RejectsResolveOptions("an exact notice requires its reviewed token", () => Await(commands.RunAsync(
            SyncCliRegistry.Parse(["--cli", "engine", "resolve", "--pair", pair.PairId, "--issue", "1",
                "--choice", "ignore", "--yes"]), CancellationToken.None)));
    }

    private static void DriveChoiceDispatch(string root)
    {
        var pair = new PairConfig("p", "Pair A", Path.Combine(root, "local"), "/Remote", "unbound-account", [], [],
            FirstRunPolicy.DropboxWins, false, new DriveEvidence(1, ulong.MaxValue, 10));
        var commands = new EngineCommands(new EngineOperations(root, _ =>
            throw new InvalidOperationException("Recording a drive choice must not run Repair.")));
        foreach (var choice in new[] { "first-sync", "sync-normally" })
        foreach (var firstRun in FirstRunChoices)
        {
            if (choice == "sync-normally" && firstRun is not null) continue;
            long id;
            using (var store = StateStore.Open(StateStore.DatabasePath(root)))
            {
                store.SavePair(pair);
                store.Commit(pair.PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.DriveChange, TextKey.DriveRestored, "", new IssueSubject([], null))],
                });
                id = store.Issues(pair.PairId).Single().Id;
            }
            var invocation = SyncCliRegistry.Parse(["--cli", "engine", "resolve", "--pair", pair.PairId,
                "--issue", id.ToString(CultureInfo.InvariantCulture), "--reviewed",
                new EngineOperations(root, _ => { }).Issues(pair.PairId, null).Single().Reviewed!, "--choice", choice, "--name", "unused", "--yes",
                .. (firstRun is null ? [] : new[] { "--first-run", firstRun })]);
            if (choice == "first-sync")
            {
                long beforeGeneration;
                bool beforeFirstRepair;
                using (var before = StateStore.OpenReadOnly(StateStore.DatabasePath(root))!)
                {
                    beforeGeneration = before.ReadPairExecution(pair.PairId)!.Value.Generation;
                    beforeFirstRepair = before.FirstRepairDone(pair.PairId);
                }
                try
                {
                    Await(commands.RunAsync(invocation, CancellationToken.None));
                    Check.Require(false, "first-sync cannot erase history before acquiring valid roots");
                }
                catch (EngineRequestException error)
                {
                    Check.Require(error.Key == TextKey.AccountMissing, "first-sync reports the missing account");
                }
                using var unchanged = StateStore.OpenReadOnly(StateStore.DatabasePath(root))!;
                Check.Require(unchanged.IssueById(pair.PairId, id) is not null && unchanged.Pair(pair.PairId)!.Drive == pair.Drive &&
                    unchanged.ReadPairExecution(pair.PairId)!.Value.Generation == beforeGeneration &&
                    unchanged.FirstRepairDone(pair.PairId) == beforeFirstRepair &&
                    unchanged.LastRepair(pair.PairId) is null && !Directory.Exists(pair.LocalRoot) &&
                    !Directory.Exists(Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.LogsFolder)),
                    "refused first-sync preserves notice, Drive evidence and database history without log or local root; issue=" +
                    (unchanged.IssueById(pair.PairId, id) is not null) + "; drive=" + (unchanged.Pair(pair.PairId)!.Drive == pair.Drive) +
                    "; generation=" + (unchanged.ReadPairExecution(pair.PairId)!.Value.Generation == beforeGeneration) +
                    "; repair=" + (unchanged.LastRepair(pair.PairId) is not null) + "; local=" + Directory.Exists(pair.LocalRoot) +
                    "; log=" + Directory.Exists(Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.LogsFolder)));
                continue;
            }
            var result = Await(commands.RunAsync(invocation, CancellationToken.None));
            Check.Require(result is ResolveResult, "drive choice dispatch returns ResolveResult, not RepairResult");
            var resolved = (ResolveResult)result;
            var expected = choice == "first-sync" ? ResolutionChoice.FirstSync : ResolutionChoice.SyncNormally;
            Check.Require(resolved.Issue == id && resolved.Choice == expected && resolved.Done &&
                resolved.ExitCode == (int)SyncCliExitCode.Success, "drive choice committed");
            Check.Require(resolved.RenderHuman() == Text.Get(TextKey.ResultDriveChoiceRecorded,
                (TextArg.Id, id.ToString(CultureInfo.InvariantCulture)), (TextArg.Choice, expected.ToString())),
                "drive choice says recorded, not synced");
            using var json = JsonDocument.Parse(JsonOf(resolved));
            Check.Require(json.RootElement.GetProperty("done").GetBoolean() &&
                json.RootElement.GetProperty("issue").GetInt64() == id &&
                !json.RootElement.TryGetProperty("repair", out _) && !json.RootElement.TryGetProperty("summary", out _),
                "drive result keeps the existing JSON shape");
            using var read = StateStore.OpenReadOnly(StateStore.DatabasePath(root))!;
            Check.Require(read.LastRepair(pair.PairId) is null && !Directory.Exists(pair.LocalRoot) &&
                !Directory.Exists(Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.LogsFolder)),
                "disabled-pair drive choice neither syncs nor opens a log or provider session");
        }
    }

    private static void IssueGuidance(string root)
    {
        var pair = new PairConfig("p", "Pair A", Path.Combine(root, "local"), "/Remote", "unbound-account", [], [],
            FirstRunPolicy.NewestWins, true, new DriveEvidence(1, ulong.MaxValue, 10));
        using (var store = StateStore.Open(StateStore.DatabasePath(root)))
        {
            store.SavePair(pair);
            store.Commit(pair.PairId, StateChange.Empty with
            {
                Raise =
                [
                    (IssueKind.Failure, TextKey.FailDropboxNoPermission, "a", SubjectAt("a")),
                    (IssueKind.Failure, TextKey.FailDropboxNoPermission, "b", SubjectAt("b")),
                    (IssueKind.Failure, TextKey.FailLocalNoSpace, "c", SubjectAt("c")),
                    (IssueKind.DriveChange, TextKey.DriveRestored, "", new IssueSubject([], null)),
                    (IssueKind.NameClash, TextKey.ResolveSameSideUnsupported, "d",
                        new IssueSubject([new IssueItem(Side.Local, "d", "1"), new IssueItem(Side.Local, "D", "2")], Side.Local)),
                    (IssueKind.Tie, TextKey.ResolveSubjectStale, "e", SubjectAt("e")),
                ],
            });
        }
        var commands = new EngineCommands(new EngineOperations(root, _ => { }));
        var result = (IssuesResult)Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "issues", "--pair",
            pair.PairId]), CancellationToken.None));
        var human = result.RenderHuman();
        var retry = Text.Get(TextKey.ResultFailureRetry, (TextArg.Pair, pair.PairId));
        var drive = result.Issues.Single(issue => issue.Kind == IssueKind.DriveChange);
        Check.Require(human == string.Join(Environment.NewLine,
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Failure.ToString()),
                (TextArg.Cause, TextKey.FailDropboxNoPermission.ToString()), (TextArg.Count, "2")),
            Line(result.Issues[0]), Text.Get(TextKey.FailDropboxNoPermission, (TextArg.Path, "a")), VisibilityGuidance(result.Issues[0], pair.PairId),
            Line(result.Issues[1]), Text.Get(TextKey.FailDropboxNoPermission, (TextArg.Path, "b")), VisibilityGuidance(result.Issues[1], pair.PairId),
            retry,
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Failure.ToString()),
                (TextArg.Cause, TextKey.FailLocalNoSpace.ToString()), (TextArg.Count, "1")),
            Line(result.Issues[2]), Text.Get(TextKey.FailLocalNoSpace, (TextArg.Path, "c")), VisibilityGuidance(result.Issues[2], pair.PairId),
            retry,
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.DriveChange.ToString()),
                (TextArg.Cause, TextKey.DriveRestored.ToString()), (TextArg.Count, "1")),
            Line(drive), Text.Get(TextKey.DriveRestored), VisibilityGuidance(drive, pair.PairId),
            Text.Get(TextKey.ResultDriveChoices, (TextArg.Pair, pair.PairId),
                (TextArg.Id, drive.Id.ToString(CultureInfo.InvariantCulture)), (TextArg.Value, drive.Reviewed!)),
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.NameClash.ToString()),
                (TextArg.Cause, TextKey.ResolveSameSideUnsupported.ToString()), (TextArg.Count, "1")),
            Line(result.Issues[4]), Text.Get(TextKey.ResolveSameSideUnsupported), VisibilityGuidance(result.Issues[4], pair.PairId),
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Tie.ToString()),
                (TextArg.Cause, TextKey.ResolveSubjectStale.ToString()), (TextArg.Count, "1")),
            Line(result.Issues[5]), Text.Get(TextKey.ResolveSubjectStale), VisibilityGuidance(result.Issues[5], pair.PairId)),
            "issue causes follow their items, drive choices follow their notice and retry closes each Failure group");
        Check.Require(!human.Contains("{Id}", StringComparison.Ordinal) && !human.Contains("{Pair}", StringComparison.Ordinal),
            "issue guidance fills identifiers");
        Check.Require(!human.Contains("replace-existing", StringComparison.Ordinal),
            "regression guard: issue guidance never offers replace-existing");

        static string Line(Issue issue) => Text.Get(TextKey.ResultIssueLine,
            (TextArg.Id, issue.Id.ToString(CultureInfo.InvariantCulture)), (TextArg.Path, issue.Path),
            (TextArg.At, issue.At.ToString(CultureInfo.InvariantCulture)));
        foreach (var kind in Enum.GetValues<IssueKind>())
        {
            var vocabulary = EngineCliCatalog.OptionValue(EngineOptionKey.Kind, kind.ToString());
            var filtered = (IssuesResult)Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "issues",
                "--pair", pair.PairId, "--kind", vocabulary]), CancellationToken.None));
            Check.Require(filtered.Issues.Select(issue => issue.Id).SequenceEqual(result.Issues.Where(issue => issue.Kind == kind)
                .Select(issue => issue.Id)), "kind filtering keeps exact issue IDs: " + kind);
        }
        using var json = JsonDocument.Parse(JsonOf(result));
        Check.Require(result.PairId == pair.PairId && json.RootElement.GetProperty("pairId").GetString() == pair.PairId &&
            json.RootElement.TryGetProperty("retryViews", out _),
            "issues JSON includes the approved exact pair identity and retry projection");
        Check.Require(json.RootElement.GetProperty("issues").GetArrayLength() == result.Issues.Count &&
            !json.RootElement.GetProperty("issues")[0].GetProperty("subject").TryGetProperty("localHash", out _),
            "issues JSON preserves all notices and omits absent hash proofs");
        var proof = new LocalHashProof(1, 2, "hash");
        using var proofJson = JsonDocument.Parse(JsonOf(SubjectAt("a") with { LocalHash = proof }));
        Check.Require(proofJson.RootElement.GetProperty("localHash").GetProperty("hash").GetString() == proof.Hash,
            "present local hash proof is serialized");
        var status = (StatusResult)Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "status", "--pair",
            pair.PairId]), CancellationToken.None));
        Check.Require(status.Items.Single().IssueCounts[IssueKind.DriveChange] == 1,
            "status reports DriveChange through the existing issue-kind counts");
        using var statusJson = JsonDocument.Parse(JsonOf(status));
        Check.Require(statusJson.RootElement.GetProperty("items")[0].GetProperty("pair").GetProperty("drive")
            .GetProperty("journalId").GetUInt64() == ulong.MaxValue, "status retains unsigned drive evidence");
    }

    private static void CommandsOnEmptyRoot(string root)
    {
        var commands = new EngineCommands(new EngineOperations(root, _ => { }));
        var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        var list = SyncCliRegistry.Parse(["--cli", "engine", "pairs", "list"]);
        var status = SyncCliRegistry.Parse(["--cli", "engine", "status"]);
        var settings = SyncCliRegistry.Parse(["--cli", "engine", "settings", "list"]);

        var pairs = (PairsResult)Await(commands.RunAsync(list, CancellationToken.None));
        Check.Require(pairs.Pairs.Count == 0, "pairs list is empty on a missing database");
        Check.Require(pairs.RenderHuman() == Text.Get(TextKey.NoPairsYet), "pairs list human");
        Check.Require(pairs.ExitCode == (int)SyncCliExitCode.Success, "pairs list exit code");
        Check.Require(Await(commands.RunAsync(list, CancellationToken.None)) is PairsResult,
            "dispatch pairs list");
        Check.Require(JsonOf(pairs).Contains("\"pairs\":", StringComparison.Ordinal), "pairs list json");

        var info = (StatusResult)Await(commands.RunAsync(status, CancellationToken.None));
        Check.Require(info.Items.Count == 0, "status is empty on a missing database");
        Check.Require(info.RenderHuman() == Text.Get(TextKey.NoPairsYet), "status human");
        Check.Require(Await(commands.RunAsync(status, CancellationToken.None)) is StatusResult,
            "dispatch status");

        var listed = (SettingsResult)Await(commands.RunAsync(settings, CancellationToken.None));
        Check.Require(listed.Settings.Count > 0, "settings list reads the embedded defaults");
        Check.Require(listed.Settings.All(setting => !setting.Overridden), "no setting is overridden yet");
        Check.Require(Await(commands.RunAsync(settings, CancellationToken.None)) is SettingsResult,
            "dispatch settings list");

        var after = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        Check.Require(before.SequenceEqual(after), "read commands create nothing");

        var set = SyncCliRegistry.Parse(["--cli", "engine", "settings", "set", "--name", SettingName, "--value",
            IssueValue, "--yes"]);
        var applied = (SettingsResult)Await(commands.RunAsync(set, CancellationToken.None));
        Check.Require(applied.Settings.Any(setting => setting.Name == SettingName && setting.Value == IssueValue &&
            setting.Overridden), "settings set applies the override");
        var reread = (SettingsResult)Await(commands.RunAsync(settings, CancellationToken.None));
        Check.Require(reread.Settings.Any(setting => setting.Name == SettingName && setting.Value == IssueValue &&
            setting.Overridden), "settings list shows the override");
        Check.Require(reread.Settings.All(setting => setting.Name != "StageSuffix"), "the removed stage suffix is not listed");
        var settingsPath = Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.UserSettingsFile);
        var settingsBytes = File.ReadAllBytes(settingsPath);
        var removed = SyncCliRegistry.Parse(["--cli", "engine", "settings", "set", "--name", "StageSuffix",
            "--value", ".stage", "--yes"]);
        try
        {
            Await(commands.RunAsync(removed, CancellationToken.None));
            Check.Require(false, "the removed stage suffix must be refused");
        }
        catch (InvalidDataException error)
        {
            Check.Require(error.Message == Text.Get(TextKey.SettingsInvalid,
                (TextArg.Name, "StageSuffix"), (TextArg.Value, ".stage")), "unknown setting diagnostic reaches the edge");
        }
        Check.Require(File.ReadAllBytes(settingsPath).SequenceEqual(settingsBytes),
            "refusing the removed stage suffix leaves the settings file unchanged");

        // R3-09: both R3 buffers round-trip through the shared settings route at their smallest accepted value,
        // and the value just below it is refused without rewriting the file.
        foreach (var (name, smallest) in new[] { (nameof(EngineSettings.LogReadBufferBytes), 1),
            (nameof(EngineSettings.JournalPageBufferBytes), NtfsUsnJournal.MinimumPageBufferBytes) })
        {
            var value = smallest.ToString(CultureInfo.InvariantCulture);
            var setBuffer = SyncCliRegistry.Parse(["--cli", "engine", "settings", "set", "--name", name, "--value", value, "--yes"]);
            Check.Require(((SettingsResult)Await(commands.RunAsync(setBuffer, CancellationToken.None))).Settings
                .Any(setting => setting.Name == name && setting.Value == value && setting.Overridden), name + " is set");
            Check.Require(((SettingsResult)Await(commands.RunAsync(settings, CancellationToken.None))).Settings
                .Any(setting => setting.Name == name && setting.Value == value && setting.Overridden), name + " is listed");
            var bufferBytes = File.ReadAllBytes(settingsPath);
            var below = (smallest - 1).ToString(CultureInfo.InvariantCulture);
            var refusedBuffer = false;
            try
            {
                Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "settings", "set", "--name", name,
                    "--value", below, "--yes"]), CancellationToken.None));
            }
            catch (InvalidDataException error)
            {
                refusedBuffer = error.Message == Text.Get(TextKey.SettingsInvalid, (TextArg.Name, name), (TextArg.Value, below));
            }
            Check.Require(refusedBuffer && File.ReadAllBytes(settingsPath).SequenceEqual(bufferBytes),
                name + " below its smallest value is refused and changes nothing");
        }

        // Default ignore rules validate like pair rules: a valid list and an empty list persist, an invalid one
        // is refused without rewriting the settings file.
        var defaultRules = SyncCliRegistry.Parse(["--cli", "engine", "settings", "set", "--name",
            "DefaultIgnoreRules", "--value", "FileName:desktop.ini", "--yes"]);
        var rulesApplied = (SettingsResult)Await(commands.RunAsync(defaultRules, CancellationToken.None));
        Check.Require(rulesApplied.Settings.Any(setting => setting.Name == "DefaultIgnoreRules" &&
            setting.Value == "FileName:desktop.ini" && setting.Overridden),
            "a valid default ignore list persists");
        var rulesBytes = File.ReadAllBytes(settingsPath);
        var invalidRules = SyncCliRegistry.Parse(["--cli", "engine", "settings", "set", "--name",
            "DefaultIgnoreRules", "--value", "NoSuchKind:x", "--yes"]);
        try
        {
            Await(commands.RunAsync(invalidRules, CancellationToken.None));
            Check.Require(false, "an invalid default ignore list must be refused");
        }
        catch (InvalidDataException error)
        {
            Check.Require(error.Message == Text.Get(TextKey.SettingsInvalid,
                (TextArg.Name, "DefaultIgnoreRules"), (TextArg.Value, "NoSuchKind:x")),
                "an invalid default ignore rule diagnostic reaches the edge");
        }
        Check.Require(File.ReadAllBytes(settingsPath).SequenceEqual(rulesBytes),
            "refusing an invalid default ignore list leaves the settings file unchanged");
        var emptyRules = SyncCliRegistry.Parse(["--cli", "engine", "settings", "set", "--name",
            "DefaultIgnoreRules", "--value", "", "--yes"]);
        var emptyApplied = (SettingsResult)Await(commands.RunAsync(emptyRules, CancellationToken.None));
        Check.Require(emptyApplied.Settings.Any(setting => setting.Name == "DefaultIgnoreRules" &&
            setting.Value.Length == 0 && setting.Overridden), "an empty default ignore list persists");

        var badIssue = SyncCliRegistry.Parse(["--cli", "engine", "resolve", "--pair", "p", "--issue", "x", "--reviewed", "reviewed",
            "--choice", "use-local", "--yes"]);
        RejectsUsageWhenRun("non-numeric --issue", () =>
            Await(commands.RunAsync(badIssue, CancellationToken.None)));
    }

    // Interleaved insertion distinguishes grouping order from the store's issue order.
    private static void CommandsReportIssueCounts(string root)
    {
        var pair = new PairConfig("p", "Pair A", "/local", "/dropbox", "acc", [], [], FirstRunPolicy.NewestWins, true,
            null);
        using (var store = StateStore.Open(StateStore.DatabasePath(root)))
        {
            store.SavePair(pair);
            store.Commit(pair.PairId, StateChange.Empty with
            {
                Raise =
                [
                    (IssueKind.Tie, TextKey.TieEqualTimes, "a", SubjectAt("a")),
                    (IssueKind.Failure, TextKey.PairUnknown, "b", SubjectAt("b")),
                    (IssueKind.Tie, TextKey.TieEqualTimes, "c", SubjectAt("c")),
                ],
            });
        }

        var commands = new EngineCommands(new EngineOperations(root, _ => { }));
        var issues = (IssuesResult)Await(commands.RunAsync(
            SyncCliRegistry.Parse(["--cli", "engine", "issues", "--pair", pair.PairId]), CancellationToken.None));
        Check.Require(issues.Issues.Select(issue => issue.Id).SequenceEqual([1L, 2L, 3L]),
            "engine issues keeps issue order");
        Check.Require(issues.ByCause.Count == 2 &&
            issues.ByCause[0] == new IssueCount(TextKey.TieEqualTimes, IssueKind.Tie, 2) &&
            issues.ByCause[1] == new IssueCount(TextKey.PairUnknown, IssueKind.Failure, 1),
            "engine issues counts in first-seen group order");
        var at = issues.Issues[0].At.ToString(CultureInfo.InvariantCulture);
        Check.Require(issues.RenderHuman() == string.Join(Environment.NewLine,
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Tie.ToString()),
                (TextArg.Cause, TextKey.TieEqualTimes.ToString()), (TextArg.Count, "2")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "1"), (TextArg.Path, "a"), (TextArg.At, at)),
            Text.Get(TextKey.TieEqualTimes, (TextArg.Path, "a"), (TextArg.Message, "")), VisibilityGuidance(issues.Issues[0], pair.PairId),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "3"), (TextArg.Path, "c"), (TextArg.At, at)),
            Text.Get(TextKey.TieEqualTimes, (TextArg.Path, "c"), (TextArg.Message, "")), VisibilityGuidance(issues.Issues[2], pair.PairId),
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Failure.ToString()),
                (TextArg.Cause, TextKey.PairUnknown.ToString()), (TextArg.Count, "1")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "2"), (TextArg.Path, "b"), (TextArg.At, at)),
            Text.Get(TextKey.PairUnknown, (TextArg.Path, "b"), (TextArg.Message, ""), (TextArg.Pair, pair.PairId)), VisibilityGuidance(issues.Issues[1], pair.PairId),
            Text.Get(TextKey.ResultFailureRetry, (TextArg.Pair, pair.PairId))),
            "engine issues human header and item lines");
    }

    // The include command passes the request's own side and path (it no longer fabricates a Keep=false
    // exclusion), so only the matching side's stored rule is dropped.
    private static void CommandsIncludeRequest(string root)
    {
        var pair = new PairConfig("p", "Pair A", "/local", "/dropbox", "acc", [],
            [new Exclusion(Side.Dropbox, "a/b", true), new Exclusion(Side.Dropbox, "keep/other", true)],
            FirstRunPolicy.NewestWins, true, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(root)))
            store.SavePair(pair);

        var commands = new EngineCommands(new EngineOperations(root, _ => { }));
        var otherSide = (PairResult)Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "pairs",
            "include", "--pair", pair.PairId, "--side", "local", "--rel", "a/b", "--yes"]), CancellationToken.None));
        Check.Require(otherSide.Pair.Exclusions.SequenceEqual(pair.Exclusions), "include on the other side keeps the rule");

        var included = (PairResult)Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "pairs",
            "include", "--pair", pair.PairId, "--side", "dropbox", "--rel", "a/b", "--yes"]), CancellationToken.None));
        Check.Require(included.Pair.Exclusions.SequenceEqual([new Exclusion(Side.Dropbox, "keep/other", true)]),
            "include drops only the matching side and path");
    }

    // GSE-01/GSE-04: preview and apply share the side's path owner, so an invalid relative path is refused before
    // saving and a confirmed exclusion never previews. The handler counts every provider request.
    private static void CommandsExcludeRequest(string root)
    {
        var local = Path.Combine(root, "exclude-local");
        Directory.CreateDirectory(local);
        var connection = DropboxConnectionBinding.CreateConnectionId("account-exclude");
        var binding = new RootBinding(local, "/Remote", connection,
            Massdex.SyncShark.WindowsDirectoryListing.ReadIdentity(local),
            new DropboxRootIdentity("account-exclude", "namespace-exclude", "namespace-exclude", "folder-exclude"));
        var pair = new PairConfig("exclude-pair", "Pair A", local, "/Remote", connection, [], [],
            FirstRunPolicy.DropboxWins, true, null, binding);
        using var settingsDoc = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        var settings = SettingsParser.Parse(settingsDoc.RootElement.GetRawText(), null);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "exclude-state", "instance.lock"));
        new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(new DropboxConnectionBinding(
            DropboxProviderConfiguration.RegisteredClientId, "account-exclude", "namespace-exclude",
            "namespace-exclude", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes, ConnectionId: pair.ConnectionId,
            CredentialReference: Massdex.SyncShark.WindowsDropboxRefreshTokenStore.RegisteredTarget))
            .GetAwaiter().GetResult();
        using var handler = new NoProviderHandler();
        using var http = new HttpClient(handler);
        var host = EngineHost.Compose(root, _ => { }, instance.StateAuthority, settings, http, TimeProvider.System,
            _ => throw new InvalidOperationException("an exclusion command must not open a run log"),
            validator => StateStore.Open(StateStore.DatabasePath(root), validator), EngineHost.Mode.Direct,
            new EngineExternals(new SyntheticCredentials(), _ => new HttpClient(handler, disposeHandler: false)));
        try
        {
            host.Store.SavePair(pair);
            var commands = new EngineCommands(host.Operations);
            var stored = host.Store.Pair(pair.PairId)!.Exclusions;
            foreach (var args in new[]
            {
                new[] { "--cli", "engine", "pairs", "exclude", "--pair", pair.PairId, "--side", "local",
                    "--rel", "../outside", "--yes" },
                new[] { "--cli", "engine", "pairs", "exclude", "--pair", pair.PairId, "--side", "local",
                    "--rel", "../outside" },
            })
            {
                var refused = false;
                try { Await(commands.RunAsync(SyncCliRegistry.Parse(args), CancellationToken.None)); }
                catch (EngineRequestException) { refused = true; }
                Check.Require(refused && host.Store.Pair(pair.PairId)!.Exclusions.SequenceEqual(stored),
                    "an invalid relative exclusion is refused before saving, previewed or applied");
            }
            Check.Require(handler.Requests == 0, "a refused exclusion never reads the provider");

            var applied = (ExcludeResult)Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "pairs",
                "exclude", "--pair", pair.PairId, "--side", "local", "--rel", "a/b", "--yes"]),
                CancellationToken.None));
            Check.Require(applied.Applied && applied.Preview is null &&
                host.Store.Pair(pair.PairId)!.Exclusions.SequenceEqual(
                    [.. stored, new Exclusion(Side.Local, "a/b", false)]),
                "a confirmed exclusion saves the rule and reports no preview payload");
            Check.Require(JsonOf(applied).Contains("\"applied\":true", StringComparison.Ordinal) &&
                JsonOf(applied).Contains("\"preview\":null", StringComparison.Ordinal),
                "a confirmed exclusion's JSON says applied with a null preview");
            Check.Require(handler.Requests == 0, "a confirmed exclusion never contacts the provider");

            var preview = (ExcludeResult)Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "pairs",
                "exclude", "--pair", pair.PairId, "--side", "local", "--rel", "a/b", "--keep"]),
                CancellationToken.None));
            Check.Require(!preview.Applied && preview.Preview is not null &&
                host.Store.Pair(pair.PairId)!.Exclusions.Count == stored.Count + 1,
                "an unconfirmed exclusion previews without saving");
            Check.Require(JsonOf(preview).Contains("\"applied\":false", StringComparison.Ordinal) &&
                JsonOf(preview).Contains("\"preview\":{", StringComparison.Ordinal),
                "an unconfirmed exclusion's JSON carries the preview payload");
            Check.Require(handler.Requests == 0, "an unconfirmed keep exclusion performs no provider read");
        }
        finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private sealed class NoProviderHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("the exclusion command must not call the provider");
        }
    }

    private static void CommandsThroughTheCli(string root)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = SyncCli.ExecuteAsync(["--cli", "engine", "pairs", "list"], output, error,
            () => new EngineCliRuntime()).GetAwaiter().GetResult();
        Check.Require(exit == (int)SyncCliExitCode.Success, "the CLI dispatch answers pairs list");
        Check.Require(output.ToString().Contains(Text.Get(TextKey.NoPairsYet), StringComparison.Ordinal),
            "the CLI dispatch prints the pairs list");

        var set = SyncCli.ExecuteAsync(["--cli", "engine", "settings", "set", "--name", SettingName, "--value",
            IssueValue, "--yes", "--json"], new StringWriter(), new StringWriter(),
            () => new EngineCliRuntime()).GetAwaiter().GetResult();
        Check.Require(set == (int)SyncCliExitCode.Success, "the CLI dispatch applies a setting");
        Check.Require(File.ReadAllText(Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.UserSettingsFile))
                .Contains("\"" + SettingName + "\"", StringComparison.Ordinal),
            "the CLI dispatch writes only under the temporary product root");
    }

    // R1 review 2/3/§4.2: the stored-log view streams readable blocks through the caller's transport, keeps the
    // stored file untouched, and structured output carries the canonical six-field records.
    private static void LogsViewStreamsAndStructures(string root)
    {
        var logs = Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.LogsFolder);
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "PairA-r1" + ResourceNames.LogExtension);
        var stored =
            "2026-11-05T00:40:39.0000000+00:00\tStage\tStageListing\t\tstarted\t\n" +
            "2026-11-05T00:41:02.0000000+00:00\tEffect\tUploaded\tParent/Child/a.txt\t\tDropbox\n" +
            "2026-11-05T00:41:03.0000000+00:00\tIssue\tFailure\tParent/Child/b.txt\trejected\tFailChanged\n" +
            "malformed\n" +
            "2026-11-05T00:41:04.0000000+00:00\tcomplete\t\t\tUploaded=1;Failure=1\ttrue\n";
        File.WriteAllText(path, stored);

        var standard = new StringWriter();
        var streamed = new StringWriter();
        var exit = Await(SyncCli.ExecuteAsync(["--cli", "engine", "logs", "--pair", "PairA", "--run", "r1"],
            standard, streamed, () => new EngineCliRuntime(80)));
        Check.Require(exit == (int)SyncCliExitCode.Success, "engine logs succeeds");
        Check.Require(streamed.ToString().Length == 0,
            "the streamed human view leaves standard error quiet");
        var text = standard.ToString();
        Check.Require(text.Contains("SYNC SHARK | RUN", StringComparison.Ordinal) &&
            text.Contains("[ATTENTION] FILE NOT SYNCED", StringComparison.Ordinal) &&
            text.Contains("Skipped a malformed record at line 4.", StringComparison.Ordinal) &&
            text.Contains("Malformed records skipped: 1", StringComparison.Ordinal),
            "engine logs streams readable blocks and reports a malformed record");
        Check.Require(File.ReadAllText(path) == stored, "the read-only log view changes no stored byte");

        var json = new StringWriter();
        exit = Await(SyncCli.ExecuteAsync(["--cli", "engine", "logs", "--pair", "PairA", "--run", "r1", "--json"],
            json, new StringWriter(), () => new EngineCliRuntime(80)));
        using var document = JsonDocument.Parse(json.ToString());
        var view = document.RootElement.GetProperty("data").GetProperty("view");
        var records = view.GetProperty("records");
        Check.Require(exit == (int)SyncCliExitCode.Success && records.GetArrayLength() == 4 &&
            view.GetProperty("malformedRecords").GetInt32() == 1,
            "structured logs returns every canonical record and the malformed count");
        var record = records[1];
        Check.Require(record.GetProperty("kind").GetString() == nameof(RunEventKind.Effect) &&
            record.GetProperty("what").GetString() == nameof(EffectKind.Uploaded) &&
            record.GetProperty("path").GetString() == "Parent/Child/a.txt" &&
            record.GetProperty("outcome").GetString() == nameof(Side.Dropbox),
            "a structured record keeps the six canonical fields instead of prose");
    }

    // U-IGNORE/CV-D09: the pair's ignore list and its separate keep-folder preferences are read and rendered here,
    // and an invalid replacement is refused before the pair is ever read.
    private static void IgnoresCommands(string root)
    {
        var pair = new PairConfig("p", "Pair A", "/local", "/dropbox", "acc",
            ["FileName:desktop.ini"], [], FirstRunPolicy.NewestWins, true, null,
            IgnoredFolderKeepRules: ["FolderName:Cache"]);
        using (var store = StateStore.Open(StateStore.DatabasePath(root))) store.SavePair(pair);
        var commands = new EngineCommands(new EngineOperations(root, _ => { }));
        var list = (IgnoresResult)Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "ignores",
            "list", "--pair", pair.PairId]), CancellationToken.None));
        Check.Require(list.PairId == pair.PairId && !list.FolderKeep &&
            list.Rules.SequenceEqual(["FileName:desktop.ini"]), "ignores list reads the saved ignore rules");
        var kept = (IgnoresResult)Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "ignores",
            "list", "--pair", pair.PairId, "--folder-keep"]), CancellationToken.None));
        Check.Require(kept.FolderKeep && kept.Rules.SequenceEqual(["FolderName:Cache"]),
            "ignores list reads the separate keep-folder preferences");
        Check.Require(list.RenderHuman() == string.Join(Environment.NewLine,
            Text.Get(TextKey.ResultIgnoreRulesHeader, (TextArg.Pair, pair.PairId)),
            Text.Get(TextKey.ResultIgnoreRuleLine, (TextArg.Value, "FileName:desktop.ini")),
            Text.Get(TextKey.ResultIgnoreRulesDefaults)), "ignores human is the exact stored list");
        Check.Require(JsonOf(list).Contains("\"rules\":", StringComparison.Ordinal) &&
            !JsonOf(list).Contains("\"folderKeep\"", StringComparison.Ordinal),
            "a false folder-keep flag is omitted from JSON");
        Check.Require(JsonOf(kept).Contains("\"folderKeep\":true", StringComparison.Ordinal),
            "a true folder-keep flag is present in JSON");

        var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        var invalid = SyncCliRegistry.Parse(["--cli", "engine", "ignores", "set", "--pair", "missing",
            "--rules", "NoSuchKind:x", "--yes"]);
        var refused = false;
        try { Await(commands.RunAsync(invalid, CancellationToken.None)); }
        catch (EngineRequestException) { refused = true; }
        Check.Require(refused, "invalid ignore rules are refused before pair access");
        var after = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        Check.Require(before.SequenceEqual(after), "a refused ignore replacement changes nothing");
    }

    // RC05-T7: both preference commands and the closed patch grammar are refused before any runtime or state.
    private static void PreferenceCommands()
    {
        var show = SyncCliRegistry.Parse(["--cli", "engine", "preferences", "show"]);
        var set = SyncCliRegistry.Parse(["--cli", "engine", "preferences", "set", "--settings",
            "{\"theme\":\"Dark\"}", "--yes"]);
        Check.Require(EngineCliCatalog.CommandKey(show.Command) == EngineCommandKey.PreferencesShow &&
            EngineCliCatalog.CommandKey(set.Command) == EngineCommandKey.PreferencesSet, "preference keys");
        Check.Require(set.Value(EngineCliCatalog.Flag(EngineOptionKey.Settings)) == "{\"theme\":\"Dark\"}",
            "the preference patch value is preserved");
        foreach (var patch in new[]
        {
            "{}", "{\"theme\":\"Nope\"}", "{\"Theme\":\"Dark\",\"theme\":\"Light\"}", "{\"mystery\":1}",
            "{\"theme\":null}", "{\"statusIconStyle\":\"Fancy\"}",
            "{\"pageViewModes\":{\"Activity\":\"Detailed\"}}", "{\"pageViewModes\":{\"activity\":\"Fancy\"}}",
            "{\"pauseUntilResumed\":true}", "{\"downloadedUpdateFolder\":\"C:\\\\updates\"}",
        })
        {
            var refused = false;
            try
            {
                SyncCliRegistry.Parse(["--cli", "engine", "preferences", "set", "--settings", patch, "--yes"]);
            }
            catch (SyncCliUsageException) { refused = true; }
            Check.Require(refused, "an invalid preference patch is refused before any runtime: " + patch);
        }
    }

    // RC05-T7: a standalone/unbound operation refuses the preference commands rather than constructing authority.
    private static void UnboundPreferencesRefuse(string root)
    {
        var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        var commands = new EngineCommands(new EngineOperations(root, _ => { }));
        string[][] invocations =
        [
            ["--cli", "engine", "preferences", "show"],
            ["--cli", "engine", "preferences", "set", "--settings", "{\"theme\":\"Dark\"}", "--yes"],
        ];
        foreach (var args in invocations)
        {
            var refused = false;
            try { Await(commands.RunAsync(SyncCliRegistry.Parse(args), CancellationToken.None)); }
            catch (InvalidOperationException) { refused = true; }
            Check.Require(refused, "an unbound operation refuses preferences instead of taking authority");
        }
        var after = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        Check.Require(before.SequenceEqual(after), "unbound preference commands create no state");
    }

    // RC05-T4/T7: the typed result is the committed value, and two store instances merge disjoint fields through the
    // one shared authority gate rather than per-instance state.
    private static void PreferenceResultAndMerge(string root)
    {
        var preferences = new UiPreferences("Dark", KeepRunningWhenClosed: false, CompactRows: true,
            PageViewModes: new Dictionary<string, string> { ["activity"] = "Detailed" });
        var result = new UiPreferencesResult(preferences);
        Check.Require(result.ExitCode == (int)SyncCliExitCode.Success &&
            ReferenceEquals(result.Preferences, preferences), "the preference result keeps the typed value");
        var human = result.RenderHuman();
        Check.Require(human.Contains("theme: Dark", StringComparison.Ordinal) &&
            human.Contains("compactRows: true", StringComparison.Ordinal) &&
            human.Contains("keepRunningWhenClosed: false", StringComparison.Ordinal),
            "the preference human lists the saved values");
        var json = JsonOf(result);
        Check.Require(json.Contains("\"preferences\":", StringComparison.Ordinal) &&
            json.Contains("\"theme\":\"Dark\"", StringComparison.Ordinal) &&
            !json.Contains("theme: Dark", StringComparison.Ordinal),
            "the preference JSON is the typed value, never the human text");

        using var instanceLock = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        Check.Require(SyncSharkRuntimePaths.InstanceLockFile.StartsWith(root, StringComparison.OrdinalIgnoreCase),
            "the preference authority stays under the temporary product root");
        var authority = instanceLock.StateAuthority;
        var one = new UiPreferencesStore(authority);
        var two = new UiPreferencesStore(authority);
        Check.Require(Await(one.LoadAsync()).Theme == UiPreferencesStore.SystemTheme,
            "an absent preference document reads the normalized defaults without writing");
        Check.Require(Await(one.UpdateAsync(UiPreferencesUpdate.Parse("{\"theme\":\"Dark\"}"))).Theme == "Dark",
            "an update returns the committed value");
        Await(two.UpdateAsync(UiPreferencesUpdate.Parse("{\"statusIconStyle\":\"Symbol\"}")));
        Await(one.UpdateAsync(UiPreferencesUpdate.Parse("{\"pageViewModes\":{\"activity\":\"Detailed\"}}")));
        Await(two.UpdateAsync(UiPreferencesUpdate.Parse("{\"pageViewModes\":{\"notifications\":\"Simple\"}}")));
        var merged = Await(two.LoadAsync());
        Check.Require(merged.Theme == "Dark" && merged.StatusIconStyle == "Symbol" &&
            merged.PageViewModes is { } pages && pages["activity"] == "Detailed" &&
            pages["notifications"] == "Simple",
            "two store instances merge disjoint fields through the shared authority gate");
    }

    // ROOTID/CV-RESET: only a committed bind constructs this result; its data, text, exit and JSON are exact.
    private static void RootBindingResultShape()
    {
        var result = new RootBindingResult("p");
        Check.Require(result.PairId == "p" && result.Bound &&
            result.ExitCode == (int)SyncCliExitCode.Success,
            "the root binding result carries the exact pair, binding and exit code");
        Check.Require(result.RenderHuman() == Text.Get(TextKey.ResultRootsBound, (TextArg.Pair, "p")),
            "the root binding human is the data-owned sentence");
        var json = JsonOf(result);
        Check.Require(json.Contains("\"pairId\":\"p\"", StringComparison.Ordinal) &&
            json.Contains("\"bound\":true", StringComparison.Ordinal) &&
            !json.Contains("Root identities saved", StringComparison.Ordinal),
            "the root binding JSON is the typed object, never the human sentence");
        var output = new StringWriter();
        var exit = Await(SyncCli.ExecuteAsync(["--cli", "engine", "pairs", "bind", "--pair", "p", "--yes",
            "--json"], output, new StringWriter(), () => new CountingRuntime(new RootBindingResult("p"))));
        Check.Require(exit == (int)SyncCliExitCode.Success &&
            output.ToString().Contains("\"pairId\":\"p\"", StringComparison.Ordinal) &&
            !output.ToString().Contains("Root identities saved", StringComparison.Ordinal),
            "the CLI JSON envelope keeps the typed root binding result");
    }

    private static void UnknownMoveIgnoreConfirmation()
    {
        var ordinary = IssueAt(1, IssueKind.Failure, TextKey.FailChanged, "Source");
        var unknown = ordinary with
        {
            Subject = ordinary.Subject with
            {
                Move = new IssueSubject.UnknownMove("remote-id", "Source", "Destination", null, false)
            }
        };
        var confirmation = IssuePresentation.ResolutionConfirmation(ResolutionChoice.Ignore);
        Check.Require(IssuePresentation.ResolutionConfirmation(ordinary, ResolutionChoice.Ignore) == confirmation &&
            IssuePresentation.ResolutionConfirmation(unknown, ResolutionChoice.Ignore) == confirmation +
                Environment.NewLine + Text.Get(TextKey.ResolutionIgnoreHeldConsequence),
            "Ignoring an unknown-effect Failure retains the held-footprint consequence even for a non-holding cause.");
    }

    private static void KeptBothLocations()
    {
        IssueItem local = new(Side.Local, "Parent/local.txt", "local");
        IssueItem remote = new(Side.Dropbox, "Other/remote.txt", "remote");
        IssueItem upload = new(Side.Dropbox, "Other/local-copy.txt", "upload");
        IssueItem download = new(Side.Local, "Parent/remote-copy.txt", "download");
        const string generic = "Parent/local.txt: both versions were kept. Linked originals and labelled copies are synced files, not independent backups. Deleting either endpoint of a linked version also deletes its unchanged counterpart on the other side; an edited counterpart is kept and copied back to where it was deleted. To retain a version before cleaning up, copy it outside all synced folders and verify that copy before deleting. Dismiss only hides this notice; it does not delete files or stop syncing.";
        var locations = Environment.NewLine + "Locations recorded when the versions were kept (paths relative to this pair’s roots):" + Environment.NewLine;
        (IssueItem[] Items, string Expected)[] layouts =
        [
            ([], generic), ([local], generic), ([local, remote], generic),
            ([local, remote, upload],
                "Other/remote.txt: both versions were kept on this side. Dismiss only hides this notice." + locations +
                "Version from Dropbox: Dropbox \"Other/remote.txt\". Version from this PC: Dropbox \"Other/local-copy.txt\"."),
            ([local, remote, download],
                "Parent/local.txt: both versions were kept on this side. Dismiss only hides this notice." + locations +
                "Version from this PC: this PC \"Parent/local.txt\". Version from Dropbox: this PC \"Parent/remote-copy.txt\"."),
            ([local, remote, upload, download], generic + locations +
                "Version from this PC: this PC \"Parent/local.txt\"; Dropbox \"Other/local-copy.txt\"." + Environment.NewLine +
                "Version from Dropbox: Dropbox \"Other/remote.txt\"; this PC \"Parent/remote-copy.txt\"."),
            ([remote, local, upload], generic), ([local, remote, download, upload], generic),
            ([local, remote, upload, upload], generic), ([local, remote, upload, download, local], generic)
        ];
        foreach (var (items, expected) in layouts)
        {
            var issue = IssueAt(7, IssueKind.Info, TextKey.InfoKeptBoth, local.Path) with
                { Subject = new IssueSubject(items, null) };
            var rendered = IssuePresentation.Render(issue, new(RetryPresentationState.Unavailable));
            Check.Require(rendered == expected && IssuesResult.From([issue]).RenderHuman().Contains(rendered),
                "CLI and shared renderer match the literal location and safety guidance for each layout.");
            using var json = JsonDocument.Parse(JsonOf(IssuesResult.From([issue])));
            Check.Require(json.RootElement.GetProperty("issues")[0].GetProperty("subject").GetProperty("items")
                .GetArrayLength() == items.Length, "CLI JSON preserves enriched and legacy subjects.");
        }
        WithProductRoot(root =>
        {
            using var store = StateStore.Open(StateStore.DatabasePath(root));
            store.SavePair(new PairConfig("pair", "Pair A", "C:/PairA", "/PairA", "account", [], [],
                FirstRunPolicy.NewestWins, true, null));
            var subject = new IssueSubject([local, remote, upload, download], null);
            var change = StateChange.Empty with { Raise = [(IssueKind.Info, TextKey.InfoKeptBoth, local.Path, subject)] };
            store.Commit("pair", change);
            var saved = store.Issues("pair").Single();
            Check.Require(saved.Subject.Items.SequenceEqual(subject.Items), "SQLite JSON round-trips copy identities in order.");
            store.Commit("pair", change);
            Check.Require(store.Issues("pair").Single().Id == saved.Id && store.Issues("pair").Single().At == saved.At, "An identical repeat retains the notice identity and timestamp.");
            var replacement = subject with { Items = [local, remote, upload with { Id = "new-copy" }, download] };
            store.Commit("pair", StateChange.Empty with
                { Raise = [(IssueKind.Info, TextKey.InfoKeptBoth, local.Path, replacement)] });
            Check.Require(store.Issues("pair").Single().Subject.Items.SequenceEqual(replacement.Items),
                "A new actual copy updates the stored notice.");
            store.DismissIssue("pair", saved.Id);
            Check.Require(store.Issues("pair").Count == 0, "Dismiss still removes the informational notice.");
        });
        var unusual = local with { Path = "Parent/{Name} \"雪\".txt" };
        var escaped = IssueAt(8, IssueKind.Info, TextKey.InfoKeptBoth, unusual.Path) with
            { Subject = new IssueSubject([unusual, remote, upload, download], null) };
        Check.Require(IssuePresentation.Render(escaped, new(RetryPresentationState.Unavailable)).Contains(unusual.Path),
            "Braces, quotes and Unicode remain path data, not template operands.");
    }

    // CV-RETRYVIEW: all eight presentation states render their own guidance for the in-use family only.
    private static void RetryViews()
    {
        (RetryPresentationState State, TextKey Key)[] states =
        [
            (RetryPresentationState.Running, TextKey.RetryRunning),
            (RetryPresentationState.Waiting, TextKey.RetryWaiting),
            (RetryPresentationState.Offline, TextKey.RetryOffline),
            (RetryPresentationState.Paused, TextKey.RetryPaused),
            (RetryPresentationState.Disabled, TextKey.RetryRepairOnly),
            (RetryPresentationState.RepairOnly, TextKey.RetryRepairOnly),
            (RetryPresentationState.RecoveryRequired, TextKey.RetryRecoveryRequired),
            (RetryPresentationState.Unavailable, TextKey.RetryUnavailable),
        ];
        foreach (var (state, key) in states)
        {
            var issue = IssueAt(7, IssueKind.Failure, TextKey.FailLocalInUse, "a");
            var result = IssuesResult.From([issue]) with
            {
                RetryViews = new Dictionary<long, IssueRetryView> { [7] = new IssueRetryView(state) },
            };
            var expected = string.Join(Environment.NewLine,
                Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Info.ToString()),
                    (TextArg.Cause, TextKey.FailLocalInUse.ToString()), (TextArg.Count, "1")),
                Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "7"), (TextArg.Path, "a"),
                    (TextArg.At, DateTimeOffset.UnixEpoch.ToString(CultureInfo.InvariantCulture))),
                Text.Get(TextKey.FailLocalInUse, (TextArg.Path, "a"), (TextArg.Message, "")) +
                Environment.NewLine + Text.Get(key));
            Check.Require(result.RenderHuman() == expected, "retry guidance for state: " + state);
        }

        var plain = IssuesResult.From([IssueAt(8, IssueKind.Failure, TextKey.FailLocalNoSpace, "b")]) with
        {
            RetryViews = new Dictionary<long, IssueRetryView>
                { [8] = new IssueRetryView(RetryPresentationState.Running) },
        };
        Check.Require(!plain.RenderHuman().Contains(Text.Get(TextKey.RetryRunning), StringComparison.Ordinal),
            "retry guidance is added only for the in-use family");
        var missing = IssuesResult.From([IssueAt(9, IssueKind.Failure, TextKey.FailLocalInUse, "c")]) with
        {
            RetryViews = new Dictionary<long, IssueRetryView>(),
        };
        Check.Require(missing.RenderHuman().Contains(Text.Get(TextKey.RetryUnavailable), StringComparison.Ordinal),
            "a missing retry view renders unavailable, never an active retry");
        var withPair = IssuesResult.From([IssueAt(7, IssueKind.Failure, TextKey.FailLocalInUse, "a")]) with
        {
            PairId = "p",
            RetryViews = new Dictionary<long, IssueRetryView>
                { [7] = new IssueRetryView(RetryPresentationState.Waiting) },
        };
        var choices = IssuePresentation.Options(withPair.Issues.Single()).Select(option => option.Choice).ToArray();
        Check.Require(choices.Contains(nameof(ResolutionChoice.Retry)) && choices.Contains(nameof(ResolutionChoice.Ignore)) &&
            !choices.Contains(nameof(ResolutionChoice.Dismiss)) && withPair.RenderHuman().Contains(
                Text.Get(TextKey.ResultFailureRetry, (TextArg.Pair, "p")), StringComparison.Ordinal),
            "waiting Info keeps stored Failure Retry and Ignore authority, never Dismiss");
        Check.Require(Text.Get(TextKey.FailLocalInUse, (TextArg.Path, "Parent/Child")) ==
            "Waiting: Parent/Child is open in another program", "confirmed lock sentence is exact");
        var roundTrip = JsonSerializer.Deserialize<IssuesResult>(JsonOf(withPair), JsonOptions)!;
        Check.Require(roundTrip.Issues.Single().Kind == IssueKind.Failure &&
            roundTrip.Categories![7] == IssueKind.Info && roundTrip.RenderHuman() == withPair.RenderHuman(),
            "actual wire roundtrip retains stored kinds and public category map without a fallback projection");
        using var pairJson = JsonDocument.Parse(JsonOf(withPair));
        var pairRoot = pairJson.RootElement;
        Check.Require(pairRoot.GetProperty("issues").GetArrayLength() == 1 &&
            pairRoot.GetProperty("issues")[0].GetProperty("id").GetInt64() == 7 &&
            pairRoot.GetProperty("issues")[0].GetProperty("kind").GetInt32() == (int)IssueKind.Failure &&
            pairRoot.GetProperty("categories").GetProperty("7").GetInt32() == (int)IssueKind.Info &&
            pairRoot.GetProperty("byCause")[0].GetProperty("kind").GetInt32() == (int)IssueKind.Info &&
            pairRoot.GetProperty("byCause").GetArrayLength() == 1 &&
            pairRoot.GetProperty("byCause")[0].GetProperty("count").GetInt32() == 1 &&
            pairRoot.GetProperty("pairId").GetString() == "p" &&
            pairRoot.GetProperty("retryViews").GetProperty("7").GetProperty("state").GetInt32() ==
            (int)RetryPresentationState.Waiting,
            "the issues JSON keeps its issue and by-cause lists and adds the guidance pair ID and retry views");
    }

    // CV-INUSE F4: the real command projection supplies a retry view for every listed issue; an unbound operation
    // has no live authority, so the truthful presentation is Unavailable.
    private static void RetryViewsThroughCommands(string root)
    {
        var pair = new PairConfig("p", "Pair A", "/local", "/dropbox", "acc", [], [], FirstRunPolicy.NewestWins,
            true, null);
        using (var store = StateStore.Open(StateStore.DatabasePath(root)))
        {
            store.SavePair(pair);
            store.Commit(pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, "busy.txt", SubjectAt("busy.txt"))],
            });
        }
        var commands = new EngineCommands(new EngineOperations(root, _ => { }));
        var result = (IssuesResult)Await(commands.RunAsync(SyncCliRegistry.Parse(["--cli", "engine", "issues",
            "--pair", pair.PairId]), CancellationToken.None));
        var issue = result.Issues.Single();
        Check.Require(result.RetryViews is { } views &&
            views.TryGetValue(issue.Id, out var view) && view.State == RetryPresentationState.Unavailable,
            "an unbound command reports the in-use issue's retry state as unavailable");
        Check.Require(result.RenderHuman().Contains(Text.Get(TextKey.RetryUnavailable), StringComparison.Ordinal),
            "the in-use issue renders its truthful unavailable guidance");
        var operations = new EngineOperations(root, _ => { });
        Check.Require(operations.Issues(pair.PairId, IssueKind.Info).Single().Id == issue.Id &&
            operations.Issues(pair.PairId, IssueKind.Failure).Count == 0,
            "actual command reads filter the waiting notice by public category, never by stored Failure");
        var conflicts = JsonSerializer.SerializeToElement(Await(EngineHostReads.ReadAsync(operations,
            EngineHostReadKind.ConflictsList, SyncCliRegistry.Parse(["--cli", "conflicts", "list", "--pair", pair.PairId]),
            CancellationToken.None)), JsonOptions);
        var conflict = conflicts.GetProperty("items")[0];
        Check.Require(conflict.GetProperty("group").GetString() == nameof(IssueKind.Info) &&
            conflict.GetProperty("options").EnumerateArray().Any(option =>
                option.GetProperty("choice").GetString() == nameof(ResolutionChoice.Retry)) &&
            !conflict.GetProperty("options").EnumerateArray().Any(option =>
                option.GetProperty("choice").GetString() == nameof(ResolutionChoice.Dismiss)),
            "Host conflicts project public Info with stored Retry authority and no Dismiss");
        var attention = JsonSerializer.SerializeToElement(Await(EngineHostReads.ReadAsync(operations,
            EngineHostReadKind.AttentionList, SyncCliRegistry.Parse(["--cli", "attention", "list"]),
            CancellationToken.None)), JsonOptions);
        Check.Require(attention.GetProperty("pendingCount").GetInt32() == 0 &&
            attention.GetProperty("items").GetArrayLength() == 0,
            "Host attention excludes confirmed waiting Info before counting or projecting rows");
    }

    // P2-11/D09: the capacity-refused and kept-ignored-folder notices render their exact data-owned sentences with
    // no unresolved placeholder, and the keep notice is nonblocking.
    private static void CapacityAndNoticeRendering()
    {
        Issue[] issues =
        [
            IssueAt(1, IssueKind.Failure, TextKey.FailLocalNoSpace, "full/item.txt"),
            IssueAt(2, IssueKind.Failure, TextKey.FailLocalSpaceUnknown, "unknown/item.txt"),
            IssueAt(3, IssueKind.Info, TextKey.InfoIgnoredFolderKept, "kept"),
        ];
        var result = IssuesResult.From(issues);
        var human = result.RenderHuman();
        Check.Require(human.Contains(Text.Get(TextKey.FailLocalNoSpace, (TextArg.Path, "full/item.txt"),
            (TextArg.Message, "")), StringComparison.Ordinal), "the capacity refusal names the exact path");
        Check.Require(human.Contains(Text.Get(TextKey.FailLocalSpaceUnknown, (TextArg.Path, "unknown/item.txt"),
            (TextArg.Message, "")), StringComparison.Ordinal), "the unknown-space refusal names the exact path");
        Check.Require(human.Contains(Text.Get(TextKey.InfoIgnoredFolderKept), StringComparison.Ordinal),
            "the kept-ignored-folder notice is the exact data-owned sentence");
        Check.Require(!human.Contains("{Path}", StringComparison.Ordinal) &&
            !human.Contains("{Message}", StringComparison.Ordinal), "the notices have no unresolved placeholder");
        Check.Require(!human.Contains(Text.Get(TextKey.FailLocalInUse, (TextArg.Path, "full/item.txt"),
            (TextArg.Message, "")), StringComparison.Ordinal), "a capacity refusal is not an in-use notice");
    }

    private static void HumanAndJson()
    {
        var pair = new PairConfig("p1", "Pair A", "/local", "/dropbox", "acc",
            [], [new Exclusion(Side.Local, "folder/sub", false)], FirstRunPolicy.NewestWins, true, null);
        var view = new PairView(pair.PairId, pair.Name, pair.LocalRoot, pair.DropboxRoot, pair.ConnectionId,
            pair.FirstRun, pair.Enabled, pair.Exclusions, pair.Roots is not null);

        var pairs = new PairsResult([view]);
        Check.Require(pairs.RenderHuman().Contains(view.Id, StringComparison.Ordinal), "pair line");
        Check.Require(new PairsResult([]).RenderHuman() == Text.Get(TextKey.NoPairsYet), "no pairs human");
        Check.Require(JsonOf(pairs).Contains("\"pairs\":", StringComparison.Ordinal), "pairs json");
        Check.Require(JsonOf(view).Contains("\"localRoot\":", StringComparison.Ordinal), "pair view json");
        Check.Require(JsonOf(view).Contains("\"firstRun\":", StringComparison.Ordinal), "pair view enum json");
        Check.Require(JsonOf(view).Contains("\"rootsBound\":false", StringComparison.Ordinal),
            "a pair without saved roots reports the truthful binding flag");
        Check.Require((view with { RootsBound = true }).Render() == view.Render(),
            "the binding flag is data, not a different pair line");

        var single = new PairResult(view);
        Check.Require(single.RenderHuman() == view.Render(), "pair result human");
        Check.Require(JsonOf(single).Contains("\"pair\":", StringComparison.Ordinal), "pair result json");
        Check.Require(view.Render() != (view with { Enabled = false }).Render(), "switched-off pair line");
        Check.Require(single.ExitCode == (int)SyncCliExitCode.Success, "pair result exit code");

        var done = new DoneResult(Text.Get(TextKey.ResultPairRemoved, (TextArg.Pair, pair.PairId)));
        Check.Require(done.RenderHuman() == Text.Get(TextKey.ResultPairRemoved, (TextArg.Pair, pair.PairId)),
            "done human");
        Check.Require(JsonOf(done).Contains("\"message\":", StringComparison.Ordinal), "done json");

        var preview = new ExcludePreview(Side.Local, "folder/sub", 2, 1024, 3, 2048, TextKey.RecoveryRecycleBin, false);
        var previewText = new ExcludeResult(view, false, preview).RenderHuman();
        Check.Require(previewText.Contains(Text.Get(TextKey.RecoveryRecycleBin), StringComparison.Ordinal),
            "exclude preview shows the recovery place");
        Check.Require(previewText.Contains("2", StringComparison.Ordinal) &&
            previewText.Contains("1024", StringComparison.Ordinal), "exclude preview shows the counts");
        Check.Require(new ExcludeResult(view, false, preview with { BlockedByProtected = true }).RenderHuman() ==
            Text.Get(TextKey.ResultExcludeBlocked, (TextArg.Path, preview.Path)), "exclude blocked human");
        var applied = new ExcludeResult(view, true, null);
        Check.Require(applied.RenderHuman() == Text.Get(TextKey.ResultExcludeApplied, (TextArg.Path, preview.Path)),
            "exclude applied human");
        Check.Require(JsonOf(applied).Contains("\"applied\":true", StringComparison.Ordinal) &&
            JsonOf(applied).Contains("\"preview\":null", StringComparison.Ordinal),
            "an applied exclusion carries no preview payload");
        Check.Require(applied.ExitCode == (int)SyncCliExitCode.Success, "exclude exit code");

        var summary = new RepairSummary("run-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(2),
            true, false, null, false, new Dictionary<string, int> { [nameof(EffectKind.Uploaded)] = 2 },
            new Dictionary<string, int>(), "/log/run-1.txt", false);
        var repaired = new RepairResult(summary);
        Check.Require(repaired.ExitCode == (int)SyncCliExitCode.Success, "completed repair exit code");
        Check.Require(repaired.RenderHuman() == string.Join(Environment.NewLine,
            Text.Get(TextKey.ResultRepairHeader,
                (TextArg.Started, summary.Started.ToString(CultureInfo.InvariantCulture)),
                (TextArg.Finished, summary.Finished.ToString(CultureInfo.InvariantCulture))),
            Text.Get(TextKey.ResultRepairCount, (TextArg.Effect, nameof(EffectKind.Uploaded)),
                (TextArg.Count, "2")),
            Text.Get(TextKey.ResultRepairLog, (TextArg.LogPath, summary.LogPath))), "repair human exact text");
        Check.Require(repaired.RenderHuman().Contains(summary.LogPath, StringComparison.Ordinal), "repair log line");
        Check.Require(repaired.RenderHuman().Contains(nameof(EffectKind.Uploaded), StringComparison.Ordinal),
            "repair effect count");
        Check.Require(JsonOf(repaired).Contains("\"summary\":", StringComparison.Ordinal), "repair json");
        var incomplete = new RepairResult(summary with { Completed = false, IncompleteReason = "listing failed" });
        Check.Require(incomplete.ExitCode == (int)SyncCliExitCode.TransientUnavailable, "incomplete repair exit code");
        Check.Require(incomplete.RenderHuman().Contains("listing failed", StringComparison.Ordinal),
            "incomplete repair reason");
        Check.Require(new RepairResult(summary with { LogFailed = true }).RenderHuman()
            .Contains(Text.Get(TextKey.ResultRepairLogFailed), StringComparison.Ordinal), "repair log failure line");

        var never = new StatusResult([new StatusInfo(pair, null, new Dictionary<IssueKind, int>
            { [IssueKind.Tie] = 2 }, false, false)]);
        Check.Require(never.RenderHuman().Contains(Text.Get(TextKey.ResultStatusExclusion,
            (TextArg.Side, Side.Local.ToString()), (TextArg.Path, pair.Exclusions[0].Path)), StringComparison.Ordinal),
            "status lists the exclusion");
        var kept = new StatusResult([new StatusInfo(
            pair with { Exclusions = [new Exclusion(Side.Dropbox, "kept", true)] }, null,
            new Dictionary<IssueKind, int>(), false, false)]);
        Check.Require(kept.RenderHuman().Contains(Text.Get(TextKey.ResultStatusExclusionKept,
            (TextArg.Side, Side.Dropbox.ToString()), (TextArg.Path, "kept")), StringComparison.Ordinal),
            "status marks a kept copy");
        var repairedStatus = new StatusResult([new StatusInfo(pair, summary, new Dictionary<IssueKind, int>(),
            false, true)]);
        Check.Require(repairedStatus.RenderHuman().Contains(Text.Get(TextKey.ResultStatusLine,
            (TextArg.Pair, pair.PairId), (TextArg.Name, pair.Name),
            (TextArg.Finished, summary.Finished.ToString(CultureInfo.InvariantCulture)), (TextArg.Count, "0")),
            StringComparison.Ordinal), "status last repair line");
        Check.Require(new StatusResult([]).RenderHuman() == Text.Get(TextKey.NoPairsYet), "empty status human");
        Check.Require(JsonOf(repairedStatus).Contains("\"items\":", StringComparison.Ordinal), "status json");

        var issues = IssuesResult.From(
        [
            new Issue(7, IssueKind.Tie, TextKey.TieEqualTimes, "a/b.txt",
                new IssueSubject([new IssueItem(Side.Local, "a/b.txt", "1")], null), DateTimeOffset.UnixEpoch, false),
            new Issue(8, IssueKind.Failure, TextKey.PairUnknown, "c/d.txt",
                new IssueSubject([new IssueItem(Side.Dropbox, "c/d.txt", "2")], Side.Dropbox),
                DateTimeOffset.UnixEpoch, false),
        ]);
        var issuesText = issues.RenderHuman();
        Check.Require(issuesText.Contains(Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Tie.ToString()),
            (TextArg.Cause, TextKey.TieEqualTimes.ToString()), (TextArg.Count, "1")), StringComparison.Ordinal),
            "issue group line");
        Check.Require(issuesText.Contains(Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "7"),
            (TextArg.Path, "a/b.txt"), (TextArg.At, DateTimeOffset.UnixEpoch.ToString(CultureInfo.InvariantCulture))),
            StringComparison.Ordinal), "issue line");
        Check.Require(JsonOf(issues).Contains("\"byCause\":", StringComparison.Ordinal) &&
            JsonOf(issues).Contains("\"issues\":", StringComparison.Ordinal), "issues json");
        Check.Require(issuesText == string.Join(Environment.NewLine,
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Tie.ToString()),
                (TextArg.Cause, TextKey.TieEqualTimes.ToString()), (TextArg.Count, "1")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "7"), (TextArg.Path, "a/b.txt"),
                (TextArg.At, DateTimeOffset.UnixEpoch.ToString(CultureInfo.InvariantCulture))),
            Text.Get(TextKey.TieEqualTimes, (TextArg.Path, "a/b.txt"), (TextArg.Message, "")),
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Failure.ToString()),
                (TextArg.Cause, TextKey.PairUnknown.ToString()), (TextArg.Count, "1")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "8"), (TextArg.Path, "c/d.txt"),
                (TextArg.At, DateTimeOffset.UnixEpoch.ToString(CultureInfo.InvariantCulture))),
            Text.Get(TextKey.PairUnknown, (TextArg.Path, "c/d.txt"), (TextArg.Message, ""), (TextArg.Pair, ""))),
            "issues human header and item lines");
        Check.Require(IssuesResult.From([]).RenderHuman().Length == 0, "no issues renders nothing");

        var oneGroup = IssuesResult.From(
        [
            IssueAt(1, IssueKind.Tie, TextKey.TieEqualTimes, "a"),
            IssueAt(2, IssueKind.Tie, TextKey.TieEqualTimes, "b"),
        ]);
        Check.Require(oneGroup.RenderHuman() == string.Join(Environment.NewLine,
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Tie.ToString()),
                (TextArg.Cause, TextKey.TieEqualTimes.ToString()), (TextArg.Count, "2")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "1"), (TextArg.Path, "a"),
                (TextArg.At, DateTimeOffset.UnixEpoch.ToString(CultureInfo.InvariantCulture))),
            Text.Get(TextKey.TieEqualTimes, (TextArg.Path, "a"), (TextArg.Message, "")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "2"), (TextArg.Path, "b"),
                (TextArg.At, DateTimeOffset.UnixEpoch.ToString(CultureInfo.InvariantCulture))),
            Text.Get(TextKey.TieEqualTimes, (TextArg.Path, "b"), (TextArg.Message, ""))),
            "one issue group keeps group and within-group order");

        var interleaved = IssuesResult.From(
        [
            IssueAt(1, IssueKind.Tie, TextKey.TieEqualTimes, "a"),
            IssueAt(2, IssueKind.Failure, TextKey.PairUnknown, "b"),
            IssueAt(3, IssueKind.Tie, TextKey.TieEqualTimes, "c"),
        ]);
        Check.Require(interleaved.RenderHuman() == string.Join(Environment.NewLine,
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Tie.ToString()),
                (TextArg.Cause, TextKey.TieEqualTimes.ToString()), (TextArg.Count, "2")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "1"), (TextArg.Path, "a"),
                (TextArg.At, DateTimeOffset.UnixEpoch.ToString(CultureInfo.InvariantCulture))),
            Text.Get(TextKey.TieEqualTimes, (TextArg.Path, "a"), (TextArg.Message, "")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "3"), (TextArg.Path, "c"),
                (TextArg.At, DateTimeOffset.UnixEpoch.ToString(CultureInfo.InvariantCulture))),
            Text.Get(TextKey.TieEqualTimes, (TextArg.Path, "c"), (TextArg.Message, "")),
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Failure.ToString()),
                (TextArg.Cause, TextKey.PairUnknown.ToString()), (TextArg.Count, "1")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "2"), (TextArg.Path, "b"),
                (TextArg.At, DateTimeOffset.UnixEpoch.ToString(CultureInfo.InvariantCulture))),
            Text.Get(TextKey.PairUnknown, (TextArg.Path, "b"), (TextArg.Message, ""), (TextArg.Pair, ""))),
            "interleaved issue groups keep first-seen group order");

        var resolved = new ResolveResult(7, ResolutionChoice.UseLocal, true,
            new SyncOutcome(ActionKind.Upload, null, EffectKind.Uploaded, null, false));
        Check.Require(resolved.ExitCode == (int)SyncCliExitCode.Success, "resolved exit code");
        Check.Require(resolved.RenderHuman() == Text.Get(TextKey.ResultResolveDone, (TextArg.Id, "7"),
            (TextArg.Choice, ResolutionChoice.UseLocal.ToString())), "resolved human");
        var failed = new ResolveResult(7, ResolutionChoice.UseLocal, false,
            new SyncOutcome(ActionKind.None, null, null, IssueKind.Failure, false));
        Check.Require(failed.ExitCode == (int)SyncCliExitCode.TransientUnavailable, "failed resolve exit code");
        Check.Require(failed.RenderHuman() == Text.Get(TextKey.ResultResolveFailed, (TextArg.Id, "7"),
            (TextArg.Reason, IssueKind.Failure.ToString())), "failed resolve human");
        var waiting = failed with { Choice = ResolutionChoice.Retry,
            Outcome = failed.Outcome with { Cause = TextKey.FailLocalInUse } };
        using var waitingJson = JsonDocument.Parse(JsonOf(waiting));
        Check.Require(waiting.RenderHuman() == Text.Get(TextKey.ResultResolveFailed, (TextArg.Id, "7"),
            (TextArg.Reason, IssueKind.Info.ToString())) && !waiting.Done &&
            waiting.ExitCode == (int)SyncCliExitCode.TransientUnavailable &&
            waitingJson.RootElement.GetProperty("outcome").GetProperty("raised").GetInt32() == (int)IssueKind.Failure,
            "failed Retry projects Info only in human text, preserving stored JSON, Done and exit semantics");
        Check.Require(new ResolveResult(7, ResolutionChoice.KeepBoth, true,
            new SyncOutcome(ActionKind.KeepBoth, null, EffectKind.KeptBoth, IssueKind.Info, false)).ExitCode ==
            (int)SyncCliExitCode.Success, "a raised non-failure notice still resolves");
        Check.Require(JsonOf(resolved).Contains("\"choice\":", StringComparison.Ordinal) &&
            JsonOf(resolved).Contains("\"outcome\":", StringComparison.Ordinal), "resolve json");
        foreach (var choice in new[] { ResolutionChoice.FirstSync, ResolutionChoice.SyncNormally })
        {
            var failedDrive = failed with { Choice = choice };
            Check.Require(failedDrive.ExitCode == (int)SyncCliExitCode.TransientUnavailable &&
                failedDrive.RenderHuman() == failed.RenderHuman(), "failed drive choice never claims it was recorded");
        }

        var settings = new SettingsResult([new SettingView(SettingName, IssueValue, false),
            new SettingView("ConflictLabelLocalName", "Local copy", true)]);
        Check.Require(settings.RenderHuman().Contains(Text.Get(TextKey.ResultSettingLine,
            (TextArg.Name, SettingName), (TextArg.Value, IssueValue)), StringComparison.Ordinal), "setting line");
        Check.Require(settings.RenderHuman().Contains(Text.Get(TextKey.ResultSettingLineOverridden,
            (TextArg.Name, "ConflictLabelLocalName"), (TextArg.Value, "Local copy")), StringComparison.Ordinal),
            "overridden setting line");
        Check.Require(JsonOf(settings).Contains("\"settings\":", StringComparison.Ordinal), "settings json");
        Check.Require(settings.ExitCode == (int)SyncCliExitCode.Success, "settings exit code");
    }

    private static void DiagnosticText()
    {
        (TextKey Key, string Expected)[] cases =
        [
            (TextKey.ResolveSameSideUnsupported,
                "Replacing an existing item on the same side is not supported. Choose a different name for the incoming item."),
            (TextKey.ResolveSubjectStale, "This notice no longer matches the items on disk; run Repair to refresh it."),
        ];
        foreach (var (key, expected) in cases)
            Check.Require(Text.Get(key, (TextArg.Path, "item"), (TextArg.Message, "detail")) == expected,
                "accurate diagnostic without unresolved Id or settings/import language: " + key);
    }

    // The result owner groups by (cause, kind) in first-appearance order, counting every occurrence.
    private static void IssueGrouping()
    {
        var empty = IssuesResult.From([]);
        Check.Require(empty.Issues.Count == 0 && empty.ByCause.Count == 0 && empty.RenderHuman().Length == 0,
            "no issues groups nothing");

        Issue[] issues =
        [
            IssueAt(1, IssueKind.Tie, TextKey.TieEqualTimes, "a"),
            IssueAt(2, IssueKind.Failure, TextKey.TieEqualTimes, "b"),
            IssueAt(3, IssueKind.Tie, TextKey.TieEqualTimes, "c"),
        ];
        var grouped = IssuesResult.From(issues);
        Check.Require(ReferenceEquals(grouped.Issues, issues), "grouping keeps the supplied issues");
        Check.Require(grouped.ByCause.SequenceEqual(
        [
            new IssueCount(TextKey.TieEqualTimes, IssueKind.Tie, 2),
            new IssueCount(TextKey.TieEqualTimes, IssueKind.Failure, 1),
        ]), "one cause with two kinds is two groups in first-seen order");
        var at = DateTimeOffset.UnixEpoch.ToString(CultureInfo.InvariantCulture);
        Check.Require(grouped.RenderHuman() == string.Join(Environment.NewLine,
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Tie.ToString()),
                (TextArg.Cause, TextKey.TieEqualTimes.ToString()), (TextArg.Count, "2")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "1"), (TextArg.Path, "a"), (TextArg.At, at)),
            Text.Get(TextKey.TieEqualTimes, (TextArg.Path, "a"), (TextArg.Message, "")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "3"), (TextArg.Path, "c"), (TextArg.At, at)),
            Text.Get(TextKey.TieEqualTimes, (TextArg.Path, "c"), (TextArg.Message, "")),
            Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, IssueKind.Failure.ToString()),
                (TextArg.Cause, TextKey.TieEqualTimes.ToString()), (TextArg.Count, "1")),
            Text.Get(TextKey.ResultIssueLine, (TextArg.Id, "2"), (TextArg.Path, "b"), (TextArg.At, at)),
            Text.Get(TextKey.TieEqualTimes, (TextArg.Path, "b"), (TextArg.Message, ""))),
            "grouped issues keep group and within-group order");
        Check.Require(JsonOf(grouped).Contains("\"issues\":", StringComparison.Ordinal) &&
            JsonOf(grouped).Contains("\"byCause\":", StringComparison.Ordinal), "grouped issues json");
    }

    // A JSON command must not render the human text; a human command renders it once.
    private static void JsonSkipsHumanRender()
    {
        var renders = 0;
        var runtime = new CountingRuntime(new CountingData(() => renders++));
        var json = SyncCli.ExecuteAsync(["--cli", "engine", "pairs", "list", "--json"], new StringWriter(),
            new StringWriter(), () => runtime).GetAwaiter().GetResult();
        Check.Require(json == (int)SyncCliExitCode.Success && renders == 0, "json skips the human render");
        var human = SyncCli.ExecuteAsync(["--cli", "engine", "pairs", "list"], new StringWriter(),
            new StringWriter(), () => runtime).GetAwaiter().GetResult();
        Check.Require(human == (int)SyncCliExitCode.Success && renders == 1, "human output renders once");
    }

    private static void RejectsUsage(string label, params string[] args)
    {
        try
        {
            SyncCliRegistry.Parse(["--cli", .. args]);
            Check.Require(false, label);
        }
        catch (SyncCliUsageException)
        {
        }
    }

    private static void RejectsUsageWhenRun(string label, Action action)
    {
        try
        {
            action();
            Check.Require(false, label);
        }
        catch (SyncCliUsageException error)
        {
            Check.Require(error.Message == EngineCliCatalog.Message(EngineMessageKey.BadNumber,
                (EngineMessageArg.Flag, EngineCliCatalog.Flag(EngineOptionKey.Issue))), label + " message");
        }
    }

    private static T Await<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static Issue IssueAt(long id, IssueKind kind, TextKey cause, string path) =>
        new(id, kind, cause, path, SubjectAt(path), DateTimeOffset.UnixEpoch, false);

    private static IssueSubject SubjectAt(string path) =>
        new([new IssueItem(Side.Local, path, "1")], null);

    private sealed class CountingData(Action render) : IEngineCliData
    {
        public string RenderHuman()
        {
            render();
            return "counted";
        }

        public int ExitCode => (int)SyncCliExitCode.Success;
    }

    // Only the engine route reaches the counting result; every old-runtime member refuses.
    private sealed class CountingRuntime(IEngineCliData data) : ISyncCliRuntime, IEngineCliAccess
    {
        public Task<object> RunEngineCommandAsync(SyncCliInvocation invocation, Action<string> progress,
            CancellationToken ct) => Task.FromResult<object>(data);

        public Task<SyncCliStatusData> LoadStatusAsync(CancellationToken cancellationToken) => throw Unsupported();
        public Task<SyncCliAccountListData> LoadAccountsAsync(CancellationToken cancellationToken) => throw Unsupported();
        public Task<SyncCliPairListData> LoadPairsAsync(CancellationToken cancellationToken) => throw Unsupported();
        public Task<SyncCliPairData> LoadPairAsync(string pairId, CancellationToken cancellationToken) =>
            throw Unsupported();
        public Task<SyncCliActivityListData> LoadActivityAsync(string? accountId, int limit, int offset,
            CancellationToken cancellationToken, SyncActivityQuery? query, string? search,
            string? filter, string? pairId, string? itemType, DateTimeOffset? sinceUtc) => throw Unsupported();
        public Task<SyncCliSettingsData> LoadSettingsAsync(CancellationToken cancellationToken) => throw Unsupported();
        public Task<SyncCliPlanData> LoadPlanAsync(string pairId, CancellationToken cancellationToken, int limit,
            int offset) => throw Unsupported();
        public Task<SyncCliRunData> RunPairAsync(string pairId, Action<string> progress,
            CancellationToken cancellationToken) => throw Unsupported();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static SyncCliUnsupportedException Unsupported() => new("The counting runtime runs engine commands only.");
    }

    // Core's test hook redirects the product lock and state folder to a temporary root; the engine test assembly is
    // not on Core's InternalsVisibleTo list.
    private static IDisposable OverrideProductRoot(string root) =>
        (IDisposable)typeof(SyncSharkRuntimePaths)
            .GetMethod("OverrideProductRootForTest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { root })!;

    private static string JsonOf<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private sealed class CountingSequence<T>(IReadOnlyList<T> values) : IEnumerable<T>
    {
        public int Enumerations { get; private set; }

        public int Consumed { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            return Enumerate();
        }

        private IEnumerator<T> Enumerate()
        {
            foreach (var value in values)
            {
                Consumed++;
                yield return value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
