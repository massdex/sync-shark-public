using System.Text.Json;

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Checks every published command’s usage against both its help and its capability row.</summary>
public static class CliParityChecks
{
    public static async Task RunAsync(CheckArguments input)
    {
        var document = await File.ReadAllLinesAsync(input.Inventory);
        using var stream = typeof(Text).Assembly.GetManifestResourceStream(ResourceNames.Cli) ?? throw new InvalidDataException(ResourceNames.Cli);
        using var catalog = JsonDocument.Parse(stream);
        var rows = catalog.RootElement.GetProperty("commands").EnumerateArray().Select(x =>
            (Name: x.GetProperty("command").GetString()!, Usage: x.GetProperty("usage").GetString()!)).ToArray();
        Check.Require(rows.Length > 0 && rows.Select(x => x.Name).Distinct().Count() == rows.Length, "Empty or duplicate command registry");
        foreach (var row in rows)
            Check.Require(document.Count(line => line.StartsWith($"| `{row.Name}` | `{row.Usage.Replace("|", "\\|")}` |", StringComparison.Ordinal)) == 1, row.Name);
        var help = global::Massdex.SyncShark.Engine.Edge.Cli.Help();
        foreach (var row in rows) Check.Require(help.Contains(row.Usage, StringComparer.Ordinal), row.Usage);
    }
}
