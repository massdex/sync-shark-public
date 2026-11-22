using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark.Engine.Tests.Edge;

/// <summary>Contract checks for Commands.</summary>
public static class CommandsChecks
{
    /// <summary>Verifies the Commands contract through the module surface.</summary>
    public static Task RunAsync()
    {
        // F069 required acceptance check: the destructive-exclusion warning names the recovery destination and the
        // recovery period together. A location-only warning omits the period and fails here.
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

        // Open notices render one group line per kind and cause, followed by the issue line.
        var notice = new Notice(7, NoticeKind.Failure, TextKey.ReadLocalFailed, "Child",
            new IssueSubject(Array.Empty<NoticeItem>(), null), DateTimeOffset.UnixEpoch, Dismissed: false);
        var notices = Commands.RenderNotices([notice]);
        Check.Require(notices.Contains("Failure", StringComparison.Ordinal) &&
            notices.Contains("ReadLocalFailed", StringComparison.Ordinal), "notice group lost its kind or cause");
        Check.Require(notices.Contains("7", StringComparison.Ordinal) && notices.Contains("Child", StringComparison.Ordinal),
            "notice line lost its id or path");
        return Task.CompletedTask;
    }
}
