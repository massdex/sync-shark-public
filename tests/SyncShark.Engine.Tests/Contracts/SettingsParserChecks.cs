using System.Text.Json;
using Massdex.SyncShark.V2;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Checks for SettingsParser (`--engine-checks settings`), written by SettingsParser's writer.</summary>
public static class SettingsParserChecks
{
    private const string DefaultsJson = """
        { "ConflictLabelFormat": "default-format", "ConflictLabelTimeFormat": "default-time",
          "ConflictLabelLocalName": "default-local", "ConflictLabelDropboxName": "default-dropbox",
          "CaseRenameSuffix": "default-case",
          "WhenRecycleBinUnavailable": "Fail", "LogRetentionDays": 3,
          "DefaultFirstRunPolicy": "NewestWins", "InitialLimiterLevel": 1,
          "DefaultIgnoreRules": "FileName:desktop.ini",
          "LogBufferCharacters": 262144, "InitialBackoffSeconds": 1, "BackoffMultiplier": 1.6,
          "BackoffJitter": 0.2, "MaximumBackoffSeconds": 120,
          "BatchPollIntervalSeconds": 0.1, "FolderDeleteMinimumPollSeconds": 1, "StoragePageSize": 100,
          "StoreBatchRows": 20000 }
        """;

    public static void Run()
    {
        // The coordinator-adopted storage page bound; the shipped value itself lives only in engine-settings.json.
        const int storagePageSize = 100;
        var expected = new EngineSettings("default-format", "default-time", "default-local", "default-dropbox",
            "default-case", RecycleFallback.Fail, 3, FirstRunPolicy.NewestWins, 1, "FileName:desktop.ini", 262144, 20000)
        {
            Backoff = new FailureRetryOwner.BackoffSettings(1, 1.6, 0.2, 120),
            BatchPollIntervalSeconds = 0.1,
            FolderDeleteMinimumPollSeconds = 1,
            StoragePageSize = storagePageSize,
        };
        Check.Require(SettingsParser.Parse(DefaultsJson, null) == expected, "the defaults alone");

        var overridden = SettingsParser.Parse(DefaultsJson,
            """{ "LogRetentionDays": 30, "DefaultFirstRunPolicy": "DropboxWins" }""");
        var expectedOverride = expected with
        {
            LogRetentionDays = 30,
            DefaultFirstRunPolicy = FirstRunPolicy.DropboxWins,
        };
        Check.Require(overridden == expectedOverride, "a partial override keeps the other defaults");

        RequiresInvalid(DefaultsJson, """{ "NoSuchSetting": 1 }""", "NoSuchSetting", "1", "an unknown name");
        // StageSuffix is gone; an old override is an unknown name, never silently consumed.
        RequiresInvalid(DefaultsJson, """{ "StageSuffix": "default-stage" }""", "StageSuffix", "default-stage",
            "a removed setting is an unknown name");
        // A malformed file keeps the exact JSON error text; a non-object root names its JSON kind.
        RequiresFileInvalid(() => SettingsParser.Parse(DefaultsJson, "{ not json"),
            FileInvalid(JsonError("{ not json")), "a syntax error keeps the JSON message");
        RequiresFileInvalid(() => SettingsParser.Parse(DefaultsJson, "[1, 2]"),
            FileInvalid(JsonValueKind.Array.ToString()), "a non-object root names its kind");

        // Absent input is no override; a literal JSON null is a non-object root and is refused like any other.
        var nullMessage = FileInvalid(JsonValueKind.Null.ToString());
        RequiresFileInvalid(() => SettingsParser.Parse(DefaultsJson, "null"), nullMessage,
            "parse refuses a JSON null file");
        RequiresFileInvalid(() => SettingsParser.List(DefaultsJson, "null"), nullMessage,
            "list refuses a JSON null file");
        RequiresFileInvalid(() => SettingsParser.WithOverride("null", nameof(EngineSettings.LogRetentionDays), "7"),
            nullMessage, "an override refuses a JSON null file");

        // A repeated user property takes its last value, for both the parsed settings and the listed row.
        var repeatedUser = """{ "LogRetentionDays": 7, "LogRetentionDays": 30 }""";
        Check.Require(SettingsParser.Parse(DefaultsJson, repeatedUser).LogRetentionDays == 30,
            "a repeated user property takes its last value");
        RequireRow(SettingsParser.List(DefaultsJson, repeatedUser), nameof(EngineSettings.LogRetentionDays), "30", true,
            "list shows the last of repeated user properties");

        // A repeated default keeps every row in order; an override applies to each of its occurrences.
        var duplicateDefaults = DefaultsJson.Replace("\"CaseRenameSuffix\": \"default-case\",",
            "\"CaseRenameSuffix\": \"first-case\", \"CaseRenameSuffix\": \"second-case\",", StringComparison.Ordinal);
        Check.Require(SettingsParser.Parse(duplicateDefaults, null).CaseRenameSuffix == "second-case",
            "a repeated default takes its last value");
        var duplicateRows = SettingsParser.List(duplicateDefaults, null);
        Check.Require(duplicateRows.Count == 20 && duplicateRows[4].Value == "first-case"
            && duplicateRows[5].Value == "second-case"
            && !duplicateRows[4].Overridden && !duplicateRows[5].Overridden,
            "list keeps every duplicate default in order");
        var duplicateOverrideRows = SettingsParser.List(duplicateDefaults, """{ "CaseRenameSuffix": "mine" }""");
        Check.Require(duplicateOverrideRows.Count == 20 && duplicateOverrideRows[4].Value == "mine"
            && duplicateOverrideRows[5].Value == "mine"
            && duplicateOverrideRows[4].Overridden && duplicateOverrideRows[5].Overridden,
            "a user override applies to every duplicate default row");
        RequiresInvalid(DefaultsJson, """{ "LogRetentionDays": "many" }""", "LogRetentionDays", "many",
            "a bad number");
        RequiresInvalid(DefaultsJson, """{ "InitialLimiterLevel": 1.5 }""", "InitialLimiterLevel", "1.5",
            "a non-integer number");
        RequiresInvalid(DefaultsJson, """{ "WhenRecycleBinUnavailable": "Explode" }""",
            "WhenRecycleBinUnavailable", "Explode", "a bad enum");
        // Data-file enums accept declared names only, in the defaults, the user file and an override.
        RequiresInvalid(DefaultsJson, """{ "WhenRecycleBinUnavailable": "fail" }""",
            "WhenRecycleBinUnavailable", "fail", "a case-variant enum");
        RequiresInvalid(DefaultsJson, """{ "DefaultFirstRunPolicy": "1" }""",
            "DefaultFirstRunPolicy", "1", "a numeric enum spelling");
        RequiresInvalid(DefaultsJson, """{ "DefaultFirstRunPolicy": " NewestWins" }""",
            "DefaultFirstRunPolicy", " NewestWins", "a whitespace variant of an enum name");
        RequiresInvalid(DefaultsJson, """{ "WhenRecycleBinUnavailable": 0 }""",
            "WhenRecycleBinUnavailable", "0", "a non-string enum value");
        RequiresInvalid(DefaultsJson, """{ "DefaultFirstRunPolicy": "newestwins" }""",
            "DefaultFirstRunPolicy", "newestwins", "a case-variant enum in the user file");
        RequiresInvalidOverride(null, nameof(EngineSettings.DefaultFirstRunPolicy), "newestwins",
            "a case-variant enum override");
        RequiresInvalidOverride(null, nameof(EngineSettings.WhenRecycleBinUnavailable), "4",
            "a numeric enum override");
        RequiresInvalid(DefaultsJson, """{ "CaseRenameSuffix": 5 }""", "CaseRenameSuffix", "5",
            "a value of the wrong JSON type");
        // The default ignore rules use the pair rule syntax and are validated wherever settings are.
        RequiresInvalid(DefaultsJson, """{ "DefaultIgnoreRules": "NoSuchKind:x" }""",
            "DefaultIgnoreRules", "NoSuchKind:x", "an unknown ignore rule kind");
        RequiresInvalid(DefaultsJson, """{ "DefaultIgnoreRules": "FileName:bad/name" }""",
            "DefaultIgnoreRules", "FileName:bad/name", "an invalid ignore rule value");
        RequiresInvalid(DefaultsJson, """{ "DefaultIgnoreRules": 5 }""",
            "DefaultIgnoreRules", "5", "a non-string ignore rule list");
        RequiresInvalidOverride(null, nameof(EngineSettings.DefaultIgnoreRules), "NoSuchKind:x",
            "an invalid ignore rule list override");

        // One integer rule, so the defaults, the user file and WithOverride refuse a limiter level below one.
        foreach (var level in new[] { "0", "-1" })
        {
            var userJson = "{ \"InitialLimiterLevel\": " + level + " }";
            RequiresInvalid(DefaultsJson, userJson, nameof(EngineSettings.InitialLimiterLevel), level,
                "a limiter level below one in the user file");
            RequiresInvalidOverride(null, nameof(EngineSettings.InitialLimiterLevel), level,
                "a limiter level below one saved as an override");
            var listed = false;
            try { SettingsParser.List(DefaultsJson, userJson); }
            catch (InvalidDataException error)
            {
                listed = error.Message == Text.Get(TextKey.SettingsInvalid,
                    (TextArg.Name, nameof(EngineSettings.InitialLimiterLevel)), (TextArg.Value, level));
            }
            Check.Require(listed, "list refuses a limiter level below one: " + level);
        }
        var badEmbedded = DefaultsJson.Replace("\"InitialLimiterLevel\": 1", "\"InitialLimiterLevel\": 0",
            StringComparison.Ordinal);
        try
        {
            SettingsParser.Parse(badEmbedded, null);
            throw new InvalidOperationException("an embedded limiter level below one must throw");
        }
        catch (InvalidDataException error)
        {
            Check.Require(error.Message == Text.Get(TextKey.SettingsDefaultsInvalid,
                (TextArg.Name, nameof(EngineSettings.InitialLimiterLevel))),
                "an embedded default below one names the setting");
        }
        var embeddedListed = false;
        try { SettingsParser.List(badEmbedded, null); }
        catch (InvalidDataException) { embeddedListed = true; }
        Check.Require(embeddedListed, "list refuses an embedded limiter level below one");

        // The log buffer must be a positive Int32; old partial settings inherit the embedded default.
        Check.Require(SettingsParser.Parse(DefaultsJson,
            """{ "LogBufferCharacters": 4096 }""").LogBufferCharacters == 4096,
            "a log buffer override is accepted");
        RequiresInvalid(DefaultsJson, """{ "LogBufferCharacters": 0 }""",
            nameof(EngineSettings.LogBufferCharacters), "0", "a zero log buffer");
        RequiresInvalid(DefaultsJson, """{ "LogBufferCharacters": -1 }""",
            nameof(EngineSettings.LogBufferCharacters), "-1", "a negative log buffer");
        RequiresInvalid(DefaultsJson, """{ "LogBufferCharacters": 1.5 }""",
            nameof(EngineSettings.LogBufferCharacters), "1.5", "a non-integral log buffer");
        RequiresInvalid(DefaultsJson, """{ "LogBufferCharacters": 2147483648 }""",
            nameof(EngineSettings.LogBufferCharacters), "2147483648", "an overflowing log buffer");
        RequiresInvalid(DefaultsJson, """{ "LogBufferCharacters": null }""",
            nameof(EngineSettings.LogBufferCharacters), "null", "a null log buffer");
        RequiresInvalid(DefaultsJson, """{ "LogBufferCharacters": "many" }""",
            nameof(EngineSettings.LogBufferCharacters), "many", "a log buffer of the wrong type");
        RequiresInvalidOverride(null, nameof(EngineSettings.LogBufferCharacters), "0",
            "a zero log buffer override");
        RequiresDefaultsInvalid(DefaultsJson.Replace("\"LogBufferCharacters\": 262144",
                "\"LogBufferCharacters\": 0", StringComparison.Ordinal),
            nameof(EngineSettings.LogBufferCharacters), "an embedded zero log buffer");

        // StoragePageSize is required data: the shared positive-integer rule rejects an absent key, a malformed
        // value, zero and a negative value wherever settings are validated, including the override path.
        RequiresDefaultsInvalid(DefaultsJson.Replace(", \"StoragePageSize\": 100", "", StringComparison.Ordinal),
            nameof(EngineSettings.StoragePageSize), "a defaults file without the page size");
        foreach (var pageSize in new[] { "0", "-1" })
        {
            var userJson = "{ \"StoragePageSize\": " + pageSize + " }";
            RequiresInvalid(DefaultsJson, userJson, nameof(EngineSettings.StoragePageSize), pageSize,
                "a page size below one in the user file");
            RequiresInvalidOverride(null, nameof(EngineSettings.StoragePageSize), pageSize,
                "a page size below one saved as an override");
        }
        RequiresInvalid(DefaultsJson, """{ "StoragePageSize": 1.5 }""",
            nameof(EngineSettings.StoragePageSize), "1.5", "a non-integral page size");
        RequiresInvalid(DefaultsJson, """{ "StoragePageSize": "many" }""",
            nameof(EngineSettings.StoragePageSize), "many", "a page size of the wrong type");
        RequiresInvalid(DefaultsJson, """{ "StoragePageSize": null }""",
            nameof(EngineSettings.StoragePageSize), "null", "a null page size");
        RequiresInvalid(DefaultsJson, """{ "StoragePageSize": 2147483648 }""",
            nameof(EngineSettings.StoragePageSize), "2147483648", "an overflowing page size");
        RequiresDefaultsInvalid(DefaultsJson.Replace("\"StoragePageSize\": 100", "\"StoragePageSize\": 0",
                StringComparison.Ordinal),
            nameof(EngineSettings.StoragePageSize), "an embedded zero page size");
        Check.Require(SettingsParser.Parse(DefaultsJson,
            """{ "StoragePageSize": 7 }""").StoragePageSize == 7, "a page size override is accepted");

        // The scan save batch uses the same positive-integer rule: zero or a negative value is refused everywhere.
        foreach (var storeBatchRows in new[] { "0", "-1" })
        {
            var userJson = "{ \"StoreBatchRows\": " + storeBatchRows + " }";
            RequiresInvalid(DefaultsJson, userJson, nameof(EngineSettings.StoreBatchRows), storeBatchRows,
                "a scan save batch below one in the user file");
            RequiresInvalidOverride(null, nameof(EngineSettings.StoreBatchRows), storeBatchRows,
                "a scan save batch below one saved as an override");
        }
        Check.Require(SettingsParser.Parse(DefaultsJson,
            """{ "StoreBatchRows": 7 }""").StoreBatchRows == 7, "a scan save batch override is accepted");
        RequiresDefaultsInvalid(DefaultsJson.Replace("\"StoreBatchRows\"", "\"StoreBatchRowsX\"", StringComparison.Ordinal),
            nameof(EngineSettings.StoreBatchRows), "a defaults file without the scan save batch");

        // Retry backoff: finite non-negative waits, jitter at most the whole, and the largest wait
        // max(initial, maximum) * (1 + jitter) inside TimeSpan's tick range.
        var zeroBackoff = SettingsParser.Parse(DefaultsJson,
            """{ "InitialBackoffSeconds": 0, "BackoffMultiplier": 0, "BackoffJitter": 0, "MaximumBackoffSeconds": 0 }""");
        Check.Require(zeroBackoff.Backoff == new FailureRetryOwner.BackoffSettings(0, 0, 0, 0),
            "a zero retry backoff is accepted");
        foreach (var backoffName in new[]
                 {
                     nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds),
                     nameof(FailureRetryOwner.BackoffSettings.BackoffMultiplier),
                     nameof(FailureRetryOwner.BackoffSettings.BackoffJitter),
                     nameof(FailureRetryOwner.BackoffSettings.MaximumBackoffSeconds),
                 })
            RequiresInvalid(DefaultsJson, "{ \"" + backoffName + "\": -1 }", backoffName, "-1",
                "a negative retry backoff");
        RequiresInvalid(DefaultsJson, """{ "BackoffJitter": 1.5 }""",
            nameof(FailureRetryOwner.BackoffSettings.BackoffJitter), "1.5", "a jitter above the whole delay");
        RequiresInvalidOverride(null, nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds), "NaN",
            "a non-finite retry backoff");
        RequiresInvalid(DefaultsJson, """{ "InitialBackoffSeconds": 1000000000000 }""",
            nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds), "1000000000000",
            "an initial backoff above the TimeSpan range");
        RequiresInvalid(DefaultsJson, """{ "MaximumBackoffSeconds": 1000000000000 }""",
            nameof(FailureRetryOwner.BackoffSettings.MaximumBackoffSeconds), "1000000000000",
            "a maximum backoff above the TimeSpan range");
        RequiresInvalid(DefaultsJson, """{ "InitialBackoffSeconds": 1e308 }""",
            nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds), "1e308",
            "a finite backoff whose largest wait overflows");
        RequiresInvalid(DefaultsJson, """{ "InitialBackoffSeconds": 800000000000 }""",
            nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds), "800000000000",
            "an initial backoff made unrepresentable by jitter");
        RequiresInvalid(DefaultsJson, """{ "MaximumBackoffSeconds": 800000000000 }""",
            nameof(FailureRetryOwner.BackoffSettings.MaximumBackoffSeconds), "800000000000",
            "a maximum backoff made unrepresentable by jitter");
        RequiresDefaultsInvalid(DefaultsJson.Replace("\"InitialBackoffSeconds\": 1",
                "\"InitialBackoffSeconds\": 1000000000000", StringComparison.Ordinal),
            nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds),
            "an embedded backoff above the TimeSpan range");
        RequiresInvalid(DefaultsJson.Replace("\"InitialBackoffSeconds\": 1",
                "\"InitialBackoffSeconds\": 800000000000", StringComparison.Ordinal),
            """{ "BackoffJitter": 0.5 }""", nameof(FailureRetryOwner.BackoffSettings.BackoffJitter), "0.5",
            "a jitter override that pushes an embedded backoff past the range");
        Check.Require(SettingsParser.Parse(DefaultsJson,
                """{ "InitialBackoffSeconds": 768614336404 }""").Backoff.InitialBackoffSeconds == 768614336404,
            "a largest wait at the TimeSpan boundary is accepted");
        RequiresInvalid(DefaultsJson, """{ "InitialBackoffSeconds": 768614336405 }""",
            nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds), "768614336405",
            "a largest wait just past the TimeSpan boundary");
        // EngineOperations.SetSetting checks the merged document through List before it writes, so the
        // same out-of-range waits must be refused by List as well as by Parse.
        RequiresListInvalid(DefaultsJson, """{ "InitialBackoffSeconds": 1000000000000 }""",
            nameof(FailureRetryOwner.BackoffSettings.InitialBackoffSeconds), "1000000000000",
            "list refuses an initial backoff above the TimeSpan range");
        RequiresListInvalid(DefaultsJson, """{ "MaximumBackoffSeconds": 1000000000000 }""",
            nameof(FailureRetryOwner.BackoffSettings.MaximumBackoffSeconds), "1000000000000",
            "list refuses a maximum backoff above the TimeSpan range");
        RequiresListInvalid(DefaultsJson, """{ "MaximumBackoffSeconds": 800000000000 }""",
            nameof(FailureRetryOwner.BackoffSettings.MaximumBackoffSeconds), "800000000000",
            "list refuses a maximum backoff made unrepresentable by jitter");
        RequiresListInvalid(DefaultsJson.Replace("\"InitialBackoffSeconds\": 1",
                "\"InitialBackoffSeconds\": 800000000000", StringComparison.Ordinal),
            """{ "BackoffJitter": 0.5 }""", nameof(FailureRetryOwner.BackoffSettings.BackoffJitter), "0.5",
            "list names the jitter override that pushes an embedded backoff past the range");

        var incomplete = DefaultsJson.Replace("\"LogRetentionDays\": 3,", "", StringComparison.Ordinal);
        try
        {
            SettingsParser.Parse(incomplete, null);
            throw new InvalidOperationException("an incomplete defaults file must throw");
        }
        catch (InvalidDataException)
        {
        }

        // The shipped defaults are complete and round-trip through the parser.
        using var embedded = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        var embeddedJson = embedded.RootElement.GetRawText();
        SettingsParser.Parse(embeddedJson, null);
        Check.Require(SettingsParser.List(embeddedJson, null).Count == 19, "the embedded defaults name every setting");
        Check.Require(SettingsParser.Parse(embeddedJson, null).StoragePageSize == storagePageSize,
            "the shipped page-size default loads");
        Check.Require(SettingsParser.Parse(embeddedJson, null).StoreBatchRows == 20_000,
            "the shipped scan save batch loads");
        RequireRow(SettingsParser.List(embeddedJson, null), nameof(EngineSettings.StoragePageSize),
            storagePageSize.ToString(), false, "the shipped page-size default is listed");
        TemporaryPrefixOwnership();
        var embeddedRules = embedded.RootElement.GetProperty(nameof(EngineSettings.DefaultIgnoreRules)).GetString()!
            .Split(';');
        Check.Require(embeddedRules.Count(rule => rule == "FileNamePrefix:~$") == 1
            && embeddedRules.Count(rule => rule == "FileNamePrefix:.~") == 1,
            "the embedded defaults carry each temporary prefix once");
        Check.Require(SettingsParser.Parse(DefaultsJson,
            """{ "DefaultIgnoreRules": "FileNamePrefix:temp" }""").DefaultIgnoreRules == "FileNamePrefix:temp",
            "a prefix rule parses in an override");
        RequiresInvalid(DefaultsJson, """{ "DefaultIgnoreRules": "FileNamePrefix:~$*" }""",
            nameof(EngineSettings.DefaultIgnoreRules), "FileNamePrefix:~$*", "a malformed prefix rule");

        // Transition fixtures: an empty override changes nothing, and old-supported rules still parse.
        Check.Require(SettingsParser.Parse(DefaultsJson, "{}") == expected, "an empty override keeps the defaults");
        Check.Require(SettingsParser.Parse(DefaultsJson,
            """{ "DefaultIgnoreRules": "" }""").DefaultIgnoreRules == "", "an explicitly empty ignore list is kept");
        Check.Require(SettingsParser.Parse(DefaultsJson,
            """{ "DefaultIgnoreRules": "FileName:.ds_store;FileName:desktop.ini" }""").DefaultIgnoreRules
            == "FileName:.ds_store;FileName:desktop.ini", "old-supported ignore rules still parse");

        var numberJson = SettingsParser.WithOverride(null, nameof(EngineSettings.LogRetentionDays), "7");
        var expectedNumber = expected with { LogRetentionDays = 7 };
        Check.Require(SettingsParser.Parse(DefaultsJson, numberJson) == expectedNumber, "an override sets a number");

        var textJson = SettingsParser.WithOverride(null, nameof(EngineSettings.ConflictLabelFormat), "{name}");
        var expectedText = expected with { ConflictLabelFormat = "{name}" };
        Check.Require(SettingsParser.Parse(DefaultsJson, textJson) == expectedText, "an override sets a string");

        var kept = SettingsParser.WithOverride("""{ "CaseRenameSuffix": "mine" }""",
            nameof(EngineSettings.WhenRecycleBinUnavailable), "DeletePermanently");
        var keptSettings = SettingsParser.Parse(DefaultsJson, kept);
        Check.Require(keptSettings.CaseRenameSuffix == "mine"
            && keptSettings.WhenRecycleBinUnavailable == RecycleFallback.DeletePermanently,
            "an override keeps the earlier ones");

        RequiresInvalidOverride(null, "NoSuchSetting", "1", "an unknown override name");
        RequiresInvalidOverride(null, nameof(EngineSettings.LogRetentionDays), "many", "a bad override number");
        RequiresInvalidOverride(null, nameof(EngineSettings.DefaultFirstRunPolicy), "Sideways",
            "a bad override enum");

        var rows = SettingsParser.List(DefaultsJson, numberJson);
        Check.Require(rows.Count == 19, "list names every setting once");
        RequireRow(rows, nameof(EngineSettings.LogRetentionDays), "7", true, "an overridden value");
        RequireRow(rows, nameof(EngineSettings.CaseRenameSuffix), "default-case", false, "a default value");
        RequireRow(rows, nameof(EngineSettings.WhenRecycleBinUnavailable), "Fail", false, "an enum default");
        Check.Require(SettingsParser.List(DefaultsJson, null).Count == 19, "list works with no user settings");

        try
        {
            SettingsParser.List(DefaultsJson, """{ "LogRetentionDays": "many" }""");
            throw new InvalidOperationException("list must refuse an unsupported effective setting");
        }
        catch (InvalidDataException error)
        {
            Check.Require(error.Message == Text.Get(TextKey.SettingsInvalid,
                (TextArg.Name, nameof(EngineSettings.LogRetentionDays)), (TextArg.Value, "many")),
                "list validates the effective settings");
        }
    }

    private static void TemporaryPrefixOwnership()
    {
        foreach (var prefix in new[] { "~$", ".~" })
        {
            Check.Require(TemporaryPrefixLiterals("class C { string P = \"" + prefix + "\"; }").Any(),
                "a hardwired temporary prefix is refused");
            Check.Require(TemporaryPrefixLiterals("class C { string P = \"FileNamePrefix:" + prefix + "\"; }").Any(),
                "a copied temporary-prefix rule is refused");
        }
        Check.Require(TemporaryPrefixLiterals("class C { string P = \"" + (char)92 + "u007e" + (char)92 + "u0024\"; }").Any(),
            "escaped temporary-prefix literals are refused");
        Check.Require(!TemporaryPrefixLiterals("""class C { string P = "FileNamePrefix:temp"; } // ~$ .~""").Any(),
            "generic prefixes and comments are not temporary-prefix rules");

        const string providerFile = "src/SyncShark.Core/V2/BlockerNames.cs";
        const string providerRule = """class BlockerNames { bool DropboxRefuses(string name) => name.StartsWith("~$"); }""";
        Check.Require(!TemporaryPrefixLiterals(providerRule, providerFile).Any(),
            "the existing provider-refusal operation is not an ignore rule");
        Check.Require(TemporaryPrefixLiterals(providerRule, "src/Other.cs").Any()
            && TemporaryPrefixLiterals(providerRule.Replace("DropboxRefuses", "IsIgnored", StringComparison.Ordinal),
                providerFile).Any()
            && TemporaryPrefixLiterals(providerRule.Replace("~$", ".~", StringComparison.Ordinal), providerFile).Any(),
            "the provider-refusal exception cannot admit another file, member or prefix");

        var root = Layout.LiteralsChecks.FindRepoRoot();
        var offences = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "obj" or "bin")) continue;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            offences.AddRange(TemporaryPrefixLiterals(File.ReadAllText(file), relative)
                .Select(line => $"{relative}:{line}"));
        }
        Check.Require(offences.Count == 0, "temporary prefixes belong only in default data: " + string.Join("; ", offences));
    }

    private static IEnumerable<int> TemporaryPrefixLiterals(string source, string? relative = null) =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)).GetRoot()
            .DescendantTokens()
            .Where(token => token.Value is string value &&
                (value is "~$" or ".~" || value.Contains("FileNamePrefix:~$", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("FileNamePrefix:.~", StringComparison.OrdinalIgnoreCase)))
            // Provider refusal is not a configured ignore rule; keep its existing operation distinct.
            .Where(token => !(relative == "src/SyncShark.Core/V2/BlockerNames.cs" && token.ValueText == "~$"
                && token.Parent?.Parent is ArgumentSyntax { Parent: ArgumentListSyntax
                    { Parent: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } } }
                && member.Name.Identifier.ValueText == "StartsWith"
                && token.Parent.Ancestors().OfType<MethodDeclarationSyntax>()
                    .Any(method => method.Identifier.ValueText == "DropboxRefuses")))
            .Select(token => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

    private static void RequiresInvalid(string defaultsJson, string? userJson, string name, string value, string what)
    {
        var expected = Text.Get(TextKey.SettingsInvalid, (TextArg.Name, name), (TextArg.Value, value));
        try
        {
            SettingsParser.Parse(defaultsJson, userJson);
        }
        catch (InvalidDataException exception)
        {
            Check.Require(exception.Message == expected, what);
            return;
        }
        throw new InvalidOperationException(what);
    }

    private static void RequiresListInvalid(string defaultsJson, string userJson, string name, string value,
        string what)
    {
        var expected = Text.Get(TextKey.SettingsInvalid, (TextArg.Name, name), (TextArg.Value, value));
        try
        {
            SettingsParser.List(defaultsJson, userJson);
        }
        catch (InvalidDataException exception)
        {
            Check.Require(exception.Message == expected, what);
            return;
        }
        throw new InvalidOperationException(what);
    }

    private static void RequiresDefaultsInvalid(string defaultsJson, string name, string what)
    {
        var expected = Text.Get(TextKey.SettingsDefaultsInvalid, (TextArg.Name, name));
        try
        {
            SettingsParser.Parse(defaultsJson, null);
        }
        catch (InvalidDataException exception)
        {
            Check.Require(exception.Message == expected, what);
            return;
        }
        throw new InvalidOperationException(what);
    }

    private static void RequiresFileInvalid(Action call, string message, string what)
    {
        try
        {
            call();
        }
        catch (InvalidDataException exception)
        {
            Check.Require(exception.Message == message, what);
            return;
        }
        throw new InvalidOperationException(what);
    }

    private static string FileInvalid(string message) =>
        Text.Get(TextKey.SettingsFileInvalid, (TextArg.Message, message));

    private static string JsonError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            return error.Message;
        }
        throw new InvalidOperationException("the sample must be invalid JSON: " + json);
    }

    private static void RequiresInvalidOverride(string? userJson, string name, string value, string what)
    {
        var expected = Text.Get(TextKey.SettingsInvalid, (TextArg.Name, name), (TextArg.Value, value));
        try
        {
            SettingsParser.WithOverride(userJson, name, value);
        }
        catch (InvalidDataException exception)
        {
            Check.Require(exception.Message == expected, what);
            return;
        }
        throw new InvalidOperationException(what);
    }

    private static void RequireRow(IReadOnlyList<(string Name, string Value, bool Overridden)> rows,
        string name, string value, bool overridden, string what)
    {
        var row = rows.Single(candidate => candidate.Name == name);
        Check.Require(row.Value == value && row.Overridden == overridden, what);
    }
}
