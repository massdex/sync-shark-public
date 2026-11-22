using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark.Engine.Tests.Edge;

/// <summary>Contract checks for Commands.</summary>
public static class CommandsChecks
{
    /// <summary>Verifies the Commands contract through the module surface.</summary>
    public static Task RunAsync()
    {
        // F069 required acceptance check: the destructive-exclusion warning keeps the recovery period with the
        // destination. A mutant that keeps the path and place but drops the period fails on the period assertion.
        var preview = Commands.RenderExclusionPreview("Child", Side.Local, 2, 10, 1, 5, "D:\\Recovery\\keep");
        Check.Require(preview.Contains("Child", StringComparison.Ordinal), "preview lost the path");
        Check.Require(preview.Contains("D:\\Recovery\\keep", StringComparison.Ordinal), "preview lost the recovery place");
        Check.Require(preview.Contains("30 days", StringComparison.Ordinal), "preview omitted the recovery period");

        // Pair lines render the enabled and switched-off forms from the pair facts.
        var pair = new PairConfig("pair-1", "Docs", "C:\\Local", "/Cloud", "account-1", Array.Empty<string>(),
            Array.Empty<Exclusion>(), FirstRunPolicy.NewestWins, true, null);
        var line = Commands.RenderPair(pair);
        Check.Require(line.Contains("Docs", StringComparison.Ordinal) && line.Contains("NewestWins", StringComparison.Ordinal),
            "enabled pair line missing facts");
        var off = Commands.RenderPair(pair with { Enabled = false });
        Check.Require(off.Contains("switched off", StringComparison.Ordinal), "switched-off pair line missing marker");
        Check.Require(Commands.RenderPairs([]) == Text.Get(TextKey.NoPairsYet), "empty pair list did not render NoPairsYet");

        // A refused pair removal renders the returned reason; a removed pair renders the removed sentence.
        var refused = Commands.RenderPairNotRemoved("pair-1", TextKey.PairPutBackOutstanding);
        Check.Require(refused.Contains("pair-1", StringComparison.Ordinal), "refused removal lost the pair id");
        Check.Require(refused.Contains(Text.Get(TextKey.PairPutBackOutstanding), StringComparison.Ordinal),
            "refused removal lost the returned reason");
        Check.Require(Commands.RenderPairRemoved("pair-1").Contains("pair-1", StringComparison.Ordinal),
            "removed sentence lost the pair id");

        // Applied and blocked exclusion sentences name the path; the ignore listing prints a line per rule.
        Check.Require(Commands.RenderExclusionApplied("Child").Contains("Child", StringComparison.Ordinal),
            "applied exclusion lost the path");
        Check.Require(Commands.RenderExclusionBlocked("Child").Contains("Child", StringComparison.Ordinal),
            "blocked exclusion lost the path");
        var ignores = Commands.RenderIgnores("pair-1", ["FileExtension:.tmp"]);
        Check.Require(ignores.Contains("pair-1", StringComparison.Ordinal) &&
            ignores.Contains("FileExtension:.tmp", StringComparison.Ordinal), "ignore listing lost its facts");

        // R19 item (2): the record kind is the SECOND TAB column (timestamp<TAB>kind<TAB>...). A first-token reader
        // would treat the timestamp as the kind and leak every real Effect record; per-file Effect only with --show-files.
        const string effectRecord = "2026-01-01T00:00:00.0000000Z\tEffect\tupload\tChild\t\tok";
        const string stageRecord = "2026-01-01T00:00:00.0000000Z\tStage\tscan\t\t\t";
        Check.Require(!Commands.ShowLogRecord(effectRecord, false), "timestamped Effect record leaked without --show-files");
        Check.Require(Commands.ShowLogRecord(effectRecord, true), "Effect record must show with --show-files");
        Check.Require(Commands.ShowLogRecord(stageRecord, false), "ordinary record must show without --show-files");

        // R19 item (1): the detached logs read is owned by Edge.Commands and renders not-found without a host.
        var logsInvocation = Cli.Parse(["logs", "--pair", "p1", "--run", "r1"]);
        Check.Require(logsInvocation is not null, "logs did not parse");
        using var logOutput = new StringWriter();
        Commands.ReadLogs(logsInvocation!, new AbsentLogFiles(), logOutput, default);
        Check.Require(logOutput.ToString().Contains(
            Text.Get(TextKey.HumanLogNotFound, (TextArg.Pair, "p1"), (TextArg.Run, "r1")), StringComparison.Ordinal),
            "detached log read did not render HumanLogNotFound");

        return Task.CompletedTask;
    }

    private sealed class AbsentLogFiles : ILogFiles
    {
        public TextWriter Open(string path, int bufferCharacters) => throw new NotSupportedException();
        public string? Resolve(string folder, string pairId, string runId) => null;
        public ILogFiles.ReadStatus Read(string path, Action<string, bool> record, int bufferBytes, CancellationToken ct,
            out bool unfinishedTrailingRecord) => throw new NotSupportedException();
        public void Prune(string folder, TimeSpan olderThan, DateTimeOffset now) => throw new NotSupportedException();
    }
}
