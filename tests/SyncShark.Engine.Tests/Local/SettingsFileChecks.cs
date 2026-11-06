using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for SettingsFile (`--engine-checks local`), written by SettingsFile's writer.</summary>
public static class SettingsFileChecks
{
    public static void Run()
    {
        var temporary = Directory.CreateTempSubdirectory(nameof(SettingsFileChecks));
        var productRoot = Path.Combine(temporary.FullName, nameof(SettingsFile));
        var engineFolder = Path.Combine(productRoot, ResourceNames.EngineFolder);
        var path = Path.Combine(engineFolder, ResourceNames.UserSettingsFile);
        var replacement = Path.Combine(engineFolder, ResourceNames.UserSettingsReplacementFile);
        try
        {
            Check.Require(SettingsFile.Read(productRoot) is null, nameof(SettingsFile.Read));
            Check.Require(!Directory.Exists(productRoot), nameof(productRoot));

            Directory.CreateDirectory(productRoot);
            Check.Require(SettingsFile.Read(productRoot) is null, nameof(SettingsFile.Read));
            Check.Require(!Directory.Exists(engineFolder), nameof(engineFolder));

            var overrides = new Dictionary<string, string>
            {
                [nameof(EngineSettings.ConflictLabelLocalName)] = nameof(SettingsFileChecks),
                [nameof(EngineSettings.ConflictLabelDropboxName)] = nameof(SettingsFile)
            };
            // P3 8.6: a prefix-bearing override and the old-supported value restored over it must each
            // publish as one complete document, so an interruption never leaves a mixed settings file.
            const string oldSupportedRules = "FileName:.ds_store;FileName:desktop.ini";
            var oldSupported = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [nameof(EngineSettings.DefaultIgnoreRules)] = oldSupportedRules
            });
            var emptyOverride = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [nameof(EngineSettings.DefaultIgnoreRules)] = ""
            });
            var prefixBearing = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [nameof(EngineSettings.DefaultIgnoreRules)] =
                    oldSupportedRules + ";FileNamePrefix:~$;FileNamePrefix:.~"
            });
            var jsons = new[]
            {
                JsonSerializer.Serialize(overrides),
                JsonSerializer.Serialize(new Dictionary<string, string>()),
                "",
                nameof(SettingsFileChecks),
                "{\"" + nameof(SettingsFileChecks) + "\":\"" + new string('\u2603', 100_000) + "\"}",
                emptyOverride,
                prefixBearing
            };
            // An unrelated file in the engine folder proves a publication touches only its own two names.
            var unrelated = Path.Combine(engineFolder, "unrelated.txt");
            var unrelatedBytes = Encoding.UTF8.GetBytes(nameof(SettingsFileChecks));
            var seeded = false;
            foreach (var json in jsons)
            {
                SettingsFile.Write(productRoot, json);
                // The first write creates the engine folder; a publication adds nothing but its own result.
                Check.Require(Directory.Exists(engineFolder), nameof(engineFolder));
                Check.Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(json)), nameof(SettingsFile.Write));
                Check.Require(SettingsFile.Read(productRoot) == json, nameof(SettingsFile.Read));
                Check.Require(!File.Exists(replacement), nameof(ResourceNames.UserSettingsReplacementFile));
                if (!seeded)
                {
                    File.WriteAllBytes(unrelated, unrelatedBytes);
                    seeded = true;
                }
                Check.Require(File.ReadAllBytes(unrelated).SequenceEqual(unrelatedBytes), nameof(SettingsFile.Write));
                Check.Require(OnlyEntries(engineFolder, path, unrelated), nameof(SettingsFile.Write));
            }

            Check.Require(!File.Exists(Path.Combine(engineFolder, ResourceNames.DatabaseFile)),
                nameof(ResourceNames.DatabaseFile));
            Check.Require(OnlyEntries(productRoot, engineFolder), nameof(productRoot));

            var previous = jsons[^1];
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                RequireThrows<IOException>(() => SettingsFile.Read(productRoot));
                // Write reports the OS's own failure, so either managed I/O type may surface here.
                RequireManagedIoFailure(() => SettingsFile.Write(productRoot, oldSupported));
                // A failed publication keeps the old complete document and leaves the complete new one
                // in its owned replacement; the file is never a mixture of the two.
                locked.Position = 0;
                var preserved = new byte[locked.Length];
                locked.ReadExactly(preserved);
                Check.Require(Encoding.UTF8.GetString(preserved) == previous, nameof(SettingsFile.Write));
                Check.Require(File.Exists(replacement), nameof(ResourceNames.UserSettingsReplacementFile));
                Check.Require(File.ReadAllText(replacement) == oldSupported,
                    nameof(ResourceNames.UserSettingsReplacementFile));
                Check.Require(File.ReadAllBytes(unrelated).SequenceEqual(unrelatedBytes), nameof(SettingsFile.Write));
                Check.Require(OnlyEntries(engineFolder, path, replacement, unrelated), nameof(SettingsFile.Write));
            }
            // The public read still observes the old complete document; the replacement is not consulted.
            Check.Require(SettingsFile.Read(productRoot) == previous, nameof(SettingsFile.Read));

            File.Delete(path);
            Check.Require(SettingsFile.Read(productRoot) is null, nameof(SettingsFile.Read));
            Check.Require(OnlyEntries(engineFolder, replacement, unrelated), nameof(engineFolder));

            // An abandoned replacement is truncated and reused by the next successful publication,
            // which restores the old-supported value over the prefix-bearing one.
            File.WriteAllText(replacement, new string('x', 200_000));
            SettingsFile.Write(productRoot, oldSupported);
            Check.Require(File.ReadAllText(path) == oldSupported, nameof(SettingsFile.Write));
            Check.Require(SettingsFile.Read(productRoot) == oldSupported, nameof(SettingsFile.Read));
            Check.Require(!File.Exists(replacement), nameof(ResourceNames.UserSettingsReplacementFile));
            Check.Require(File.ReadAllBytes(unrelated).SequenceEqual(unrelatedBytes), nameof(SettingsFile.Write));
            Check.Require(OnlyEntries(engineFolder, path, unrelated), nameof(SettingsFile.Write));

            File.Delete(path);
            Directory.CreateDirectory(path);
            RequireThrows<UnauthorizedAccessException>(() => SettingsFile.Read(productRoot));
            RequireThrows<UnauthorizedAccessException>(() => SettingsFile.Write(productRoot, ""));
            Check.Require(File.ReadAllBytes(unrelated).SequenceEqual(unrelatedBytes), nameof(SettingsFile.Write));
            Check.Require(
                Directory.EnumerateFileSystemEntries(engineFolder)
                    .All(entry => entry == path || entry == replacement || entry == unrelated),
                nameof(SettingsFile.Write));
            Check.Require(!File.Exists(Path.Combine(engineFolder, ResourceNames.DatabaseFile)),
                nameof(ResourceNames.DatabaseFile));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static bool OnlyEntries(string folder, params string[] expected) =>
        Directory.EnumerateFileSystemEntries(folder)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(entry => entry, StringComparer.Ordinal));

    private static void RequireManagedIoFailure(Action action)
    {
        var refused = false;
        try
        {
            action();
        }
        catch (Exception error) when (LocalErrors.IsManagedIoFailure(error))
        {
            refused = true;
        }
        Check.Require(refused, nameof(LocalErrors.IsManagedIoFailure));
    }

    private static void RequireThrows<TException>(Action action) where TException : Exception
    {
        var threw = false;
        try
        {
            action();
        }
        catch (TException)
        {
            threw = true;
        }
        Check.Require(threw, typeof(TException).Name);
    }
}
