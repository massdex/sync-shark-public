using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;

// Parser checks for the `engine` group (engine design 1.3): every command parses, its catalogue key/flag/mode and
// confirmation rule hold at the shared parse boundary before any runtime exists, typed patches are refused there,
// and the existing groups still parse.
internal static partial class CliChecks
{
    private static void EngineParserChecks()
    {
        string[][] valid =
        [
            ["engine", "pairs", "list"],
            ["engine", "pairs", "add", "--name", "n", "--local", @"C:\fixture", "--path", "/fixture", "--account", "a",
                "--first-run", "local-wins", "--yes"],
            ["engine", "pairs", "import", "--pair", "p", "--yes"],
            ["engine", "pairs", "remove", "--pair", "p", "--yes"],
            ["engine", "pairs", "enable", "--pair", "p", "--yes"],
            ["engine", "pairs", "disable", "--pair", "p", "--yes"],
            ["engine", "pairs", "exclude", "--pair", "p", "--side", "local", "--rel", "a/b", "--keep"],
            ["engine", "pairs", "include", "--pair", "p", "--side", "dropbox", "--rel", "a/b", "--yes"],
            ["engine", "repair", "--pair", "p", "--yes"],
            ["engine", "status"],
            ["engine", "issues", "--pair", "p", "--kind", "name-clash"],
            ["engine", "resolve", "--pair", "p", "--issue", "1", "--reviewed", "reviewed", "--choice", "rename-incoming", "--name", "x", "--yes"],
            ["engine", "settings", "list"],
            ["engine", "settings", "set", "--name", "LogRetentionDays", "--value", "30", "--yes"],
            ["engine", "settings", "set", "--name", "DefaultIgnoreRules", "--value", "FileName:desktop.ini",
                "--yes"],

            // The twelve commands added after the first engine slice (RC05-D, P3-IGNORES 5.1).
            ["engine", "pairs", "bind", "--pair", "p", "--yes"],
            ["engine", "host", "start", "--yes"],
            ["engine", "host", "status"],
            ["engine", "host", "pause", "--yes"],
            ["engine", "host", "pause", "--pair", "p", "--yes"],
            ["engine", "host", "resume", "--yes"],
            ["engine", "host", "resume", "--pair", "p", "--yes"],
            ["engine", "host", "stop", "--yes"],
            ["engine", "recovery", "--pair", "p"],
            ["engine", "recovery", "--pair", "p", "--after-operation", "op"],
            ["engine", "recovery", "--pair", "p", "--operation", "op"],
            ["engine", "recovery", "--pair", "p", "--operation", "op", "--after", "2"],
            ["engine", "recovery-resolve", "--pair", "p", "--operation", "op", "--generation", "0", "--choice",
                "check", "--yes"],
            ["engine", "recovery-resolve", "--pair", "p", "--operation", "op", "--generation", "1", "--choice",
                "accept-current", "--yes"],
            ["engine", "recovery-resolve", "--pair", "p", "--operation", "op", "--generation", "1", "--choice",
                "abandon-incoming", "--yes"],
            ["engine", "ignores", "list", "--pair", "p"],
            ["engine", "ignores", "list", "--pair", "p", "--folder-keep"],
            ["engine", "ignores", "list", "--pair", "p", "--json"],
            ["engine", "ignores", "set", "--pair", "p", "--rules", "FileNamePrefix:~$", "--yes"],
            ["engine", "ignores", "set", "--pair", "p", "--folder-keep", "--rules",
                "FileNamePrefix:~$;FileExtension:tmp;FileName:desktop.ini", "--yes"],
            ["engine", "ignores", "set", "--pair", "p", "--rules", "none", "--yes"],
            ["engine", "ignores", "set", "--pair", "p", "--rules", "", "--yes"],
            ["engine", "ignores", "set", "--pair", "p", "--rules", "none", "--yes", "--json"],
            ["engine", "preferences", "show"],
            ["engine", "preferences", "show", "--json"],
            ["engine", "preferences", "set", "--settings",
                """{"theme":"Dark","pageViewModes":{"activity":"Detailed"}}""", "--yes"],
            ["engine", "preferences", "set", "--settings", """{"updateDeliveryFolder":null}""", "--yes"],

            // The resolution choices added with the recovery, keep-folder and first-sync surfaces.
            ["engine", "resolve", "--pair", "p", "--issue", "1", "--reviewed", "reviewed", "--choice", "keep-local", "--yes"],
            ["engine", "resolve", "--pair", "p", "--issue", "1", "--reviewed", "reviewed", "--choice", "keep-dropbox", "--yes"],
            ["engine", "resolve", "--pair", "p", "--issue", "1", "--reviewed", "reviewed", "--choice", "rename-local", "--name", "x", "--yes"],
            ["engine", "resolve", "--pair", "p", "--issue", "1", "--reviewed", "reviewed", "--choice", "rename-dropbox", "--name", "x", "--yes"],
            ["engine", "resolve", "--pair", "p", "--issue", "1", "--reviewed", "reviewed", "--choice", "retry", "--yes"],
            ["engine", "resolve", "--pair", "p", "--all", "--choice", "retry", "--yes"],
            ["engine", "resolve", "--pair", "p", "--issue", "1", "--reviewed", "reviewed", "--choice", "first-sync", "--yes"],
            ["engine", "resolve", "--pair", "p", "--issue", "1", "--reviewed", "reviewed", "--choice", "sync-normally", "--yes"],
            ["engine", "resolve", "--pair", "p", "--issue", "1", "--reviewed", "reviewed", "--choice", "keep-folder-always", "--rules",
                "FileName:x", "--yes"]
        ];
        foreach (var args in valid)
        {
            var invocation = SyncCliRegistry.Parse(["--cli", ..args]);
            Require(EngineCliCatalog.Owns(invocation.Command), "engine command parses: " + string.Join(' ', args));
        }
        Require(SyncCliRegistry.Parse(["--cli", "engine", "pairs", "exclude", "--pair", "p", "--side", "local",
                "--rel", "a", "--keep", "--yes"]).Flag("--keep"),
            "engine --keep is a flag and does not take the next option as its value");
        Require(SyncCliRegistry.Parse(["--cli", "dropbox", "ls", "--account", "a", "--path", "/"]).Command ==
                "dropbox ls", "the Dropbox group still parses after the parser change");
        Require(SyncCliRegistry.Parse(["--cli", "engine", "ignores", "list", "--pair", "p", "--json"]).Json &&
                SyncCliRegistry.Parse(["--cli", "engine", "ignores", "set", "--pair", "p", "--rules", "none",
                    "--yes", "--json"]).Json,
            "the global --json flag is accepted beside the engine ignores commands");

        // The catalogue key, mode, confirmation and exact-target rule of every added command, before a runtime exists
        // (RC05-T7). The rows list is the one registry help, capabilities and the dispatcher read.
        (string Command, EngineCommandKey Key, string Mode, string Approval, bool Mutating, bool RequiresYes,
            bool RequiresPair)[] engineRows =
        [
            ("engine pairs import", EngineCommandKey.PairsImport, "direct", "yes", true, true, true),
            ("engine pairs bind", EngineCommandKey.PairsBind, "direct", "yes", true, true, true),
            ("engine host start", EngineCommandKey.HostStart, "direct", "yes", true, true, false),
            ("engine host status", EngineCommandKey.HostStatus, "direct", "none", false, false, false),
            ("engine host pause", EngineCommandKey.HostPause, "direct", "yes", true, true, false),
            ("engine host resume", EngineCommandKey.HostResume, "direct", "yes", true, true, false),
            ("engine host stop", EngineCommandKey.HostStop, "direct", "yes", true, true, false),
            ("engine recovery", EngineCommandKey.Recovery, "direct", "none", false, false, true),
            ("engine recovery-resolve", EngineCommandKey.RecoveryResolve, "direct", "yes", true, true, true),
            ("engine ignores list", EngineCommandKey.IgnoresList, "direct", "none", false, false, true),
            ("engine ignores set", EngineCommandKey.IgnoresSet, "direct", "yes", true, true, true),
            ("engine preferences show", EngineCommandKey.PreferencesShow, "existing-host-only", "none", false, false,
                false),
            ("engine preferences set", EngineCommandKey.PreferencesSet, "existing-host-only", "yes", true, true, false)
        ];
        foreach (var expected in engineRows)
        {
            var row = SyncCliRegistry.Rows.Single(candidate => candidate.Command == expected.Command);
            Require(EngineCliCatalog.Owns(expected.Command) &&
                    EngineCliCatalog.CommandKey(expected.Command) == expected.Key,
                "engine command key: " + expected.Command);
            Require(row.Mode == expected.Mode && row.Approval == expected.Approval &&
                    row.Mutating == expected.Mutating && row.RequiresYes == expected.RequiresYes &&
                    row.RequiresPair == expected.RequiresPair,
                "engine command mode, confirmation and target rule: " + expected.Command);
        }
        Require(EngineCliCatalog.Rows.Select(row => EngineCliCatalog.CommandKey(row.Command)).Distinct().Count() ==
                EngineCliCatalog.Rows.Count,
            "every engine command owns one distinct command key");

        static bool HasOption(SyncCliCommandRow row, string flag) =>
            row.Options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(flag, StringComparer.Ordinal);

        var ignoresList = SyncCliRegistry.Rows.Single(row => row.Command == "engine ignores list");
        var ignoresSet = SyncCliRegistry.Rows.Single(row => row.Command == "engine ignores set");
        const string ignoresListUsage = "engine ignores list --pair <exact-id> [--folder-keep]";
        const string ignoresSetUsage =
            "engine ignores set --pair <exact-id> [--folder-keep] --rules <kind:value;...|none> --yes";
        Require(ignoresList.Usage == ignoresListUsage && ignoresSet.Usage == ignoresSetUsage,
            "the one registry publishes the exact engine ignores usages");
        Require(HasOption(ignoresList, "--pair") && HasOption(ignoresList, "--folder-keep") &&
                HasOption(ignoresSet, "--rules") && HasOption(ignoresSet, "--folder-keep") &&
                HasOption(ignoresSet, "--yes"),
            "the engine ignores rows publish their exact options");

        var preferencesShow = SyncCliRegistry.Rows.Single(row => row.Command == "engine preferences show");
        var preferencesSet = SyncCliRegistry.Rows.Single(row => row.Command == "engine preferences set");
        Require(preferencesShow.Usage == "engine preferences show" &&
                preferencesSet.Usage == "engine preferences set --settings <changed-field-json> --yes",
            "the preference rows publish the exact existing-host-only usages");

        Require(EngineCliCatalog.Flag(EngineOptionKey.Rules) == "--rules" &&
                EngineCliCatalog.Flag(EngineOptionKey.FolderKeep) == "--folder-keep" &&
                EngineCliCatalog.Flag(EngineOptionKey.Settings) == "--settings" &&
                EngineCliCatalog.Flag(EngineOptionKey.Operation) == "--operation" &&
                EngineCliCatalog.Flag(EngineOptionKey.Generation) == "--generation" &&
                EngineCliCatalog.Flag(EngineOptionKey.Item) == "--item" &&
                EngineCliCatalog.Flag(EngineOptionKey.ItemGeneration) == "--item-generation" &&
                EngineCliCatalog.TakesValue("--rules") && !EngineCliCatalog.TakesValue("--folder-keep"),
            "the engine option keys, flags and value rules are the frozen catalogue values");

        var capabilities = SyncCliRegistry.Capabilities();
        var ignoresListCapability = capabilities.Commands.Single(row => row.Command == "engine ignores list");
        var ignoresSetCapability = capabilities.Commands.Single(row => row.Command == "engine ignores set");
        Require(ignoresListCapability.Usage == ignoresListUsage && ignoresListCapability.Kind == "read" &&
                ignoresListCapability.Mode == "direct" && ignoresSetCapability.Usage == ignoresSetUsage &&
                ignoresSetCapability.Kind == "mutating" && ignoresSetCapability.Mode == "direct",
            "capabilities publish both engine ignores rows with their exact usage, kind and direct mode");

        var listHelp = SyncCliRegistry.Parse(["--cli", "help", "engine", "ignores", "list", "--json"]);
        var setHelp = SyncCliRegistry.Parse(["--cli", "help", "engine", "ignores", "set"]);
        Require(listHelp.HelpCommand == "engine ignores list" && listHelp.Json &&
                setHelp.HelpCommand == "engine ignores set" && !setHelp.Json &&
                SyncCliRegistry.Parse(["--cli", "help"]).Command == "help" &&
                SyncCliRegistry.Parse(["--cli", "capabilities"]).Command == "capabilities" &&
                SyncCliRegistry.Parse(["--cli", "version"]).Command == "version",
            "global help, per-command help, capabilities and version resolve in the same state-free registry");

        var recoveryZero = SyncCliRegistry.Parse(["--cli", "engine", "recovery-resolve", "--pair", "p",
            "--operation", "op", "--generation", "0", "--choice", "check", "--yes"]);
        Require(recoveryZero.Generation == 0,
            "recovery-resolve accepts generation 0 for a current or legacy pending operation");

        void RejectsAtParse(string label, params string[] args)
        {
            try
            {
                SyncCliRegistry.Parse(["--cli", ..args]);
                Require(false, label);
            }
            catch (SyncCliUsageException)
            {
            }
        }

        RejectsAtParse("a word after an engine command is a usage error", "engine", "pairs", "list", "extra");
        RejectsAtParse("engine repair without --yes is a usage error", "engine", "repair", "--pair", "p");
        RejectsAtParse("a --choice value outside the catalog is a usage error", "engine", "resolve", "--pair", "p",
            "--issue", "1", "--reviewed", "reviewed", "--choice", "use-both", "--yes");
        RejectsAtParse("a missing required engine option is a usage error", "engine", "pairs", "exclude", "--pair", "p",
            "--side", "local");
        RejectsAtParse("an engine command without its pair is a usage error", "engine", "repair", "--yes");
        RejectsAtParse("the removed resolve review argument is a usage error", "engine", "resolve", "--pair", "p",
            "--issue", "1", "--reviewed", "reviewed", "--choice", "use-local", "--review", "x", "--yes");

        // Each added mutation refuses without its documented --yes at the shared parse boundary.
        RejectsAtParse("engine pairs bind without --yes is a usage error", "engine", "pairs", "bind", "--pair", "p");
        RejectsAtParse("engine host start without --yes is a usage error", "engine", "host", "start");
        RejectsAtParse("engine host pause without --yes is a usage error", "engine", "host", "pause", "--pair", "p");
        RejectsAtParse("global engine host pause without --yes is a usage error", "engine", "host", "pause");
        RejectsAtParse("engine host resume without --yes is a usage error", "engine", "host", "resume", "--pair", "p");
        RejectsAtParse("engine host stop without --yes is a usage error", "engine", "host", "stop");
        RejectsAtParse("engine recovery-resolve without --yes is a usage error", "engine", "recovery-resolve",
            "--pair", "p", "--operation", "op", "--generation", "0", "--choice", "check");
        RejectsAtParse("engine preferences set without --yes is a usage error", "engine", "preferences", "set",
            "--settings", """{"theme":"Dark"}""");

        RejectsAtParse("engine ignores list without --pair is a usage error", "engine", "ignores", "list");
        RejectsAtParse("a non-exact pair selector is a usage error", "engine", "ignores", "list", "--pair",
            "two pairs");
        RejectsAtParse("engine ignores set without --pair is a usage error", "engine", "ignores", "set",
            "--rules", "none", "--yes");
        RejectsAtParse("engine ignores set without --rules is a usage error", "engine", "ignores", "set",
            "--pair", "p", "--yes");
        RejectsAtParse("engine ignores set with --rules and no value is a usage error", "engine", "ignores", "set",
            "--pair", "p", "--rules");
        RejectsAtParse("engine ignores set without --yes is a usage error", "engine", "ignores", "set",
            "--pair", "p", "--rules", "none");
        RejectsAtParse("a repeated --pair is a usage error", "engine", "ignores", "list", "--pair", "p",
            "--pair", "q");
        RejectsAtParse("a repeated --rules is a usage error", "engine", "ignores", "set", "--pair", "p",
            "--rules", "none", "--rules", "none", "--yes");
        RejectsAtParse("--all is not an engine ignores selector", "engine", "ignores", "set", "--pair", "p",
            "--all", "--rules", "none", "--yes");
        RejectsAtParse("unexpected text after an engine ignores command is a usage error", "engine", "ignores", "list",
            "--pair", "p", "extra");
        RejectsAtParse("an unsupported engine ignores option is a usage error", "engine", "ignores", "set", "--pair",
            "p", "--rules", "none", "--keep", "--yes");
        RejectsAtParse("--folder-keep takes no value", "engine", "ignores", "list", "--pair", "p",
            "--folder-keep", "true");

        RejectsAtParse("engine recovery-resolve requires its operation", "engine", "recovery-resolve", "--pair", "p",
            "--generation", "0", "--choice", "check", "--yes");
        RejectsAtParse("engine recovery-resolve requires its generation", "engine", "recovery-resolve", "--pair", "p",
            "--operation", "op", "--choice", "check", "--yes");
        RejectsAtParse("engine recovery-resolve requires its choice", "engine", "recovery-resolve", "--pair", "p",
            "--operation", "op", "--generation", "0", "--yes");
        RejectsAtParse("a negative recovery generation is a usage error", "engine", "recovery-resolve", "--pair", "p",
            "--operation", "op", "--generation", "-1", "--choice", "check", "--yes");
        RejectsAtParse("a malformed recovery generation is a usage error", "engine", "recovery-resolve", "--pair", "p",
            "--operation", "op", "--generation", "n", "--choice", "check", "--yes");
        RejectsAtParse("an overflowing recovery generation is a usage error", "engine", "recovery-resolve", "--pair",
            "p", "--operation", "op", "--generation", "99999999999999999999", "--choice", "check", "--yes");
        RejectsAtParse("a recovery choice outside the catalog is a usage error", "engine", "recovery-resolve",
            "--pair", "p", "--operation", "op", "--generation", "0", "--choice", "use-both", "--yes");
        RejectsAtParse("paging is not a recovery-resolve option", "engine", "recovery-resolve", "--pair", "p",
            "--operation", "op", "--generation", "0", "--choice", "check", "--after", "1", "--yes");
        RejectsAtParse("an item selector is not a recovery listing option", "engine", "recovery", "--pair", "p",
            "--item", "0");
        RejectsAtParse("recovery listing takes no generation", "engine", "recovery", "--pair", "p",
            "--generation", "0");

        RejectsAtParse("engine preferences set takes no --pair", "engine", "preferences", "set", "--pair", "p",
            "--settings", """{"theme":"Dark"}""", "--yes");
        RejectsAtParse("engine preferences set takes no --rules", "engine", "preferences", "set", "--rules", "none",
            "--settings", """{"theme":"Dark"}""", "--yes");
        RejectsAtParse("engine preferences set requires a value after --settings", "engine", "preferences", "set",
            "--settings");
        RejectsAtParse("an empty preference patch is refused before runtime", "engine", "preferences", "set",
            "--settings", "{}", "--yes");
        RejectsAtParse("a duplicate preference field is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"theme":"Dark","theme":"Light"}""", "--yes");
        RejectsAtParse("a case-colliding preference field is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"theme":"Dark","Theme":"Light"}""", "--yes");
        RejectsAtParse("an unknown preference field is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"unknown":true}""", "--yes");
        RejectsAtParse("a null nonnullable preference is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"theme":null}""", "--yes");
        RejectsAtParse("an unsupported theme is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"theme":"Blue"}""", "--yes");
        RejectsAtParse("an unsupported icon style is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"statusIconStyle":"Blue"}""", "--yes");
        RejectsAtParse("a non-boolean preference is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"compactRows":"yes"}""", "--yes");
        RejectsAtParse("an invalid page key is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"pageViewModes":{"BAD":"Detailed"}}""", "--yes");
        RejectsAtParse("an invalid page view is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"pageViewModes":{"activity":"Fancy"}}""", "--yes");
        RejectsAtParse("an empty page patch is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"pageViewModes":{}}""", "--yes");
        RejectsAtParse("a protected preference field is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"pauseUntilResumed":true}""", "--yes");
        RejectsAtParse("a relative delivery folder is refused before runtime", "engine", "preferences", "set",
            "--settings", """{"updateDeliveryFolder":"relative"}""", "--yes");
    }

    // F014: the one embedded catalog and EngineMessageKey name the same twelve keys exactly once, the four
    // keep/recovery label keys carry their exact data-owned text, the four revised effects project from the same
    // catalog rows, and poisoned message keys are still refused before any command runs (RC05-T7).
    private static void EngineCatalogChecks()
    {
        const string resource = "SyncShark.EngineCli.json";
        const string keepFolderAlwaysLabel = "Always keep folders with these ignored files";
        const string recoveryCheckLabel = "Check recovery";
        const string recoveryAcceptCurrentLabel = "Accept current recovery state";
        const string recoveryAbandonIncomingLabel = "Abandon incoming recovery";

        using var catalog = CliCatalogResource.Load(typeof(EngineCliCatalog).Assembly, resource);
        EngineCliCatalog.CheckCatalog(catalog);

        var declared = Enum.GetNames<EngineMessageKey>();
        var supplied = catalog.RootElement.GetProperty("messages").EnumerateObject()
            .Select(item => item.Name).ToArray();
        Require(declared.Length == 12 && supplied.Length == 12 &&
                declared.ToHashSet(StringComparer.Ordinal).SetEquals(supplied),
            "the catalog and EngineMessageKey name the same twelve message keys exactly once");
        foreach (var key in Enum.GetValues<EngineMessageKey>())
            Require(!string.IsNullOrWhiteSpace(EngineCliCatalog.Message(key)),
                "the catalog supplies text for every message key: " + key);

        Require(EngineCliCatalog.Message(EngineMessageKey.KeepFolderAlwaysLabel) == keepFolderAlwaysLabel &&
                EngineCliCatalog.Message(EngineMessageKey.RecoveryCheckLabel) == recoveryCheckLabel &&
                EngineCliCatalog.Message(EngineMessageKey.RecoveryAcceptCurrentLabel) == recoveryAcceptCurrentLabel &&
                EngineCliCatalog.Message(EngineMessageKey.RecoveryAbandonIncomingLabel) == recoveryAbandonIncomingLabel,
            "the four keep/recovery label keys carry their exact data-owned text");

        var effects = EngineCliCatalog.Rows.ToDictionary(row => row.Command, row => row.Effect,
            StringComparer.Ordinal);
        Require(effects["engine repair"].Contains("SkipReservedName", StringComparison.Ordinal) &&
                effects["engine repair"].Contains("without name substitution", StringComparison.Ordinal) &&
                effects["engine repair"].Contains("saved bindings and local copies are preserved",
                    StringComparison.Ordinal) &&
                effects["engine repair"].Contains("not deletion authority", StringComparison.Ordinal),
            "the engine repair effect states passive SkipReservedName without substitution or deletion authority");
        Require(effects["engine issues"].Contains("ambiguous in-use-or-access-denied", StringComparison.Ordinal) &&
                effects["engine issues"].Contains("real change/CLOSE", StringComparison.Ordinal) &&
                effects["engine issues"].Contains("no timed-item retry or countdown", StringComparison.Ordinal) &&
                effects["engine issues"].Contains("no promise that an unchanged close emits a journal record",
                    StringComparison.Ordinal) &&
                effects["engine issues"].Contains("Skipped/SkipReservedName", StringComparison.Ordinal),
            "the engine issues effect states truthful in-use diagnosis and untimed retry opportunities");
        Require(effects["engine settings list"].Contains("LogBufferCharacters, the log buffer size in characters",
                    StringComparison.Ordinal) &&
                effects["engine settings set"].Contains("LogBufferCharacters as a positive Int32 character count",
                    StringComparison.Ordinal) &&
                effects["engine settings set"].Contains("no separate logging command", StringComparison.Ordinal),
            "the settings effects expose LogBufferCharacters through the existing settings commands only");

        void RejectsPoisonedMessages(string label, Action<JsonObject> poison)
        {
            using var real = CliCatalogResource.Load(typeof(EngineCliCatalog).Assembly, resource);
            var root = JsonNode.Parse(real.RootElement.GetRawText())!.AsObject();
            poison(root);
            using var poisoned = JsonDocument.Parse(root.ToJsonString());
            var refused = false;
            try
            {
                EngineCliCatalog.CheckCatalog(poisoned);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                refused = true;
            }
            Require(refused, "the catalog refuses poisoned message keys: " + label);
        }
        RejectsPoisonedMessages("a missing key", root => root["messages"]!.AsObject().Remove("RecoveryCheckLabel"));
        RejectsPoisonedMessages("an unknown extra key", root => root["messages"]!.AsObject()["UnknownLabel"] = "unknown");
        RejectsPoisonedMessages("a case-variant duplicate", root =>
        {
            var messages = root["messages"]!.AsObject();
            messages["keepthisTimelabel"] = messages["KeepThisTimeLabel"]!.DeepClone();
        });
    }
}
