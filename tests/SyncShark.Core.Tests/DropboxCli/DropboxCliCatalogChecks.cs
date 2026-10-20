namespace Massdex.SyncShark.DropboxCli;

/// <summary>Group 5: the loaded Dropbox command data stays in step with the code's keys.</summary>
internal static class DropboxCliCatalogChecks
{
    internal static Task RunAsync(string root)
    {
        var options = Enum.GetValues<DropboxCliOption>();
        foreach (var option in options)
            Require(DropboxCliCatalog.Flag(option).StartsWith("--", StringComparison.Ordinal),
                "option " + option + " has a flag");
        var flags = options.Select(DropboxCliCatalog.Flag).ToArray();
        Require(flags.Distinct(StringComparer.Ordinal).Count() == flags.Length, "option flags are unique");
        Require(DropboxCliCatalog.AllFlags.Count == flags.Length, "every flag belongs to a known option key");

        foreach (var verb in Enum.GetValues<DropboxCliVerb>())
        {
            var command = "dropbox " + verb.ToString().ToLowerInvariant();
            var definition = DropboxCliCatalog.Verb(command);
            Require(definition is not null && definition.Key == verb, "verb " + verb + " has data");
        }
        Require(DropboxCliCatalog.Rows.Count == Enum.GetValues<DropboxCliVerb>().Length,
            "every row belongs to a known verb");

        foreach (var message in Enum.GetValues<DropboxCliMessage>())
            Require(DropboxCliCatalog.Message(message, ("x", "y")).Length > 0, "message " + message + " has text");
        foreach (var view in Enum.GetValues<DropboxCliView>())
            Require(DropboxCliCatalog.View(view).Length > 0, "view " + view + " has text");

        foreach (var row in DropboxCliCatalog.Rows)
            Require(SyncCliRegistry.Rows.Count(candidate => candidate.Command == row.Command) == 1,
                "command " + row.Command + " is unique in the combined registry");

        foreach (var (command, expected) in new[]
        {
            ("dropbox rm", true), ("dropbox mv", true), ("dropbox cp", true), ("dropbox put", true),
            ("dropbox mkdir", true), ("dropbox restore", true), ("dropbox ls", false), ("dropbox get", false),
            ("dropbox whoami", false), ("dropbox changes", false), ("dropbox routes", false), ("dropbox api", false)
        })
            Require(DropboxCliCatalog.Rows.Single(row => row.Command == command).RequiresYes == expected,
                command + " RequiresYes=" + expected);

        foreach (var row in DropboxCliCatalog.Rows)
        {
            var allowed = row.Options.Length == 0 ? [] : row.Options.Split(',');
            foreach (var option in DropboxCliCatalog.Verb(row.Command)!.Options)
                Require(allowed.Contains(DropboxCliCatalog.Flag(option)), row.Command + " lists " + option);
        }

        var help = SyncCli.HelpText();
        var capabilities = SyncCliRegistry.RenderCapabilities(SyncCliRegistry.Capabilities());
        foreach (var verb in Enum.GetValues<DropboxCliVerb>())
        {
            var command = "dropbox " + verb.ToString().ToLowerInvariant();
            Require(help.Contains(command, StringComparison.Ordinal), "--cli help lists " + command);
            Require(capabilities.Contains(command, StringComparison.Ordinal), "--cli capabilities lists " + command);
        }

        _ = root;
        Console.WriteLine("--dropbox-cli-checks catalog passed.");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
