using System.Text.Json;
using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark.Engine.Tests.Edge;

/// <summary>Contract checks for Cli.</summary>
public static class CliChecks
{
    /// <summary>Verifies the Cli contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        // Help is driven from the catalog: every published usage is an exact help line (matches Layout/CliParityChecks).
        var help = Cli.Help();
        Check.Require(help.Take(2).SequenceEqual(new[]{"pairs list", "  List the configured pairs."}), "help changed the first usage or its indented effect");
        using var stream = typeof(Text).Assembly.GetManifestResourceStream(ResourceNames.Cli)
            ?? throw new InvalidDataException(ResourceNames.Cli);
        using var document = JsonDocument.Parse(stream);
        var usages = document.RootElement.GetProperty("commands").EnumerateArray()
            .Select(row => row.GetProperty("usage").GetString()!).ToArray();
        Check.Require(usages.Length > 0 && usages.Distinct().Count() == usages.Length, "empty or duplicate command registry");
        foreach (var usage in usages)
            Check.Require(help.Contains(usage, StringComparer.Ordinal), "help missing usage line: " + usage);

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

        // F070 + F052: the CLI's own send decision (the branch RunAsync uses) reaches HostNotRunning for a non-log read
        // when the result is definite pre-send absence (null ExitCode); it must not start the host or re-send.
        static Task<(int? ExitCode, bool ReceivedExit)> NeverSend(CancellationToken c) =>
            throw new InvalidDataException("absent read must not send again");
        static Task<int> NeverStart(CancellationToken c) =>
            throw new InvalidDataException("absent read must not start the host");
        using var absent = new StringWriter();
        var absentCode = await Cli.ResolveSend(Cli.Parse(["status"])!, (null, false), absent, NeverSend, NeverStart, default);
        Check.Require(absentCode != 0, "absent read must report a non-zero exit");
        Check.Require(absent.ToString().Contains(Text.Get(TextKey.HostNotRunning), StringComparison.Ordinal),
            "absent read did not reach HostNotRunning through the CLI decision");
        Check.Require(!absent.ToString().Contains(Text.Get(TextKey.StatusUnavailable), StringComparison.Ordinal),
            "absent read rendered the generic unavailable status");

        var preview = Cli.Parse(["pairs", "exclude", "--pair", "p", "--side", "local", "--rel", "Child"])!;
        Check.Require(await Cli.ResolveSend(preview, (null,false), absent, NeverSend, NeverStart, default) != 0, "absent exclusion preview was treated as a mutation");
        Check.Require(await Cli.ResolveSend(Cli.Parse(["host","stop"])!, (null,false), absent, NeverSend, NeverStart, default) == 0, "absent host stop must already be complete");

        // F052: a received command result, even a failure, is terminal; a lost reply (non-null exit, not received) is
        // not replayed.
        Check.Require(await Cli.ResolveSend(Cli.Parse(["repair", "--pair", "pair-a"])!, (5, true), new StringWriter(), NeverSend, NeverStart, default) == 5,
            "received command failure was not preserved as terminal");
        Check.Require(await Cli.ResolveSend(Cli.Parse(["repair", "--pair", "pair-a"])!, (2, false), new StringWriter(), NeverSend, NeverStart, default) == 2,
            "a lost reply was replayed");

        // F052: on definite absence an eligible command may start the host and send exactly once.
        var started = 0;
        var resent = 0;
        Task<int> Start(CancellationToken c) { started++; return Task.FromResult(0); }
        Task<(int? ExitCode, bool ReceivedExit)> SendAgain(CancellationToken c) { resent++; return Task.FromResult<(int? ExitCode, bool ReceivedExit)>((7, true)); }
        var mutationCode = await Cli.ResolveSend(Cli.Parse(["repair", "--pair", "pair-a"])!, (null, false), new StringWriter(), SendAgain, Start, default);
        Check.Require(started == 1 && resent == 1 && mutationCode == 7, "absent mutation did not start and send exactly once");
    }
}
