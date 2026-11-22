using System.Text.Json;
using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark.Engine.Tests.Edge;

/// <summary>Contract checks for Cli.</summary>
public static class CliChecks
{
    /// <summary>Verifies the Cli contract through the module surface.</summary>
    public static Task RunAsync()
    {
        // Help is driven from the catalog: every published usage appears verbatim in one help line.
        var help = Cli.Help();
        Check.Require(help.Count > 0, "empty help");
        using var stream = typeof(Text).Assembly.GetManifestResourceStream(ResourceNames.Cli)
            ?? throw new InvalidDataException(ResourceNames.Cli);
        using var document = JsonDocument.Parse(stream);
        var usages = document.RootElement.GetProperty("commands").EnumerateArray()
            .Select(row => row.GetProperty("usage").GetString()!).ToArray();
        Check.Require(usages.Length > 0 && usages.Distinct().Count() == usages.Length, "empty or duplicate command registry");
        foreach (var usage in usages)
            Check.Require(help.Any(line => line.Contains(usage, StringComparison.Ordinal)), "help missing usage: " + usage);

        // Parsing is catalogue-driven: a full invocation keeps its option values and flags.
        var parsed = Cli.Parse(["pairs", "exclude", "--pair", "p1", "--side", "local", "--rel", "Child", "--keep", "--yes"]);
        Check.Require(parsed is not null, "valid pairs exclude did not parse");
        Check.Require(parsed!.Value("Pair") == "p1" && parsed.Value("Side") == "local" &&
            parsed.Value("Rel") == "Child", "option values lost");
        Check.Require(parsed.Flag("Keep") && parsed.Flag("Yes"), "flags lost");

        Check.Require(Cli.Parse(["bogus"]) is null, "unknown command accepted");
        Check.Require(Cli.Parse(["status", "--pair"]) is null, "missing option value accepted");
        Check.Require(Cli.Parse(["pairs", "remove"]) is null, "missing required option accepted");
        Check.Require(Cli.Parse(["pairs", "exclude", "--pair", "p", "--side", "everywhere", "--rel", "x"]) is null,
            "invalid choice value accepted");
        Check.Require(Cli.Parse(["status", "--nope"]) is null, "unknown option accepted");

        // F070 required acceptance check: a definitely absent host for a non-log read renders HostNotRunning and
        // reports a non-zero exit, never the generic unavailable status and never a switch-on instruction.
        using var output = new StringWriter();
        var code = Cli.RenderHostAbsent(output);
        var rendered = output.ToString();
        Check.Require(code != 0, "absent host must report a non-zero exit");
        Check.Require(rendered.Contains(Text.Get(TextKey.HostNotRunning), StringComparison.Ordinal),
            "absent host did not render HostNotRunning");
        Check.Require(!rendered.Contains(Text.Get(TextKey.StatusUnavailable), StringComparison.Ordinal),
            "absent host rendered the generic unavailable status");
        return Task.CompletedTask;
    }
}
