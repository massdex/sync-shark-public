using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks for RenameDecision's written side.</summary>
public static class RenameDecisionChecks
{
    public static void Run()
    {
        Check.Require(new RenameDecision(RenameKind.FinishCaseRename, MovedSide: Side.Local).WrittenSide()
            == Side.Dropbox, "FinishCaseRename always writes Dropbox.");

        foreach (var side in Enum.GetValues<Side>())
            Check.Require(new RenameDecision(RenameKind.Follow, MovedSide: side).WrittenSide()
                == PathNames.Other(side), $"Follow writes the counterpart of {side}.");

        Check.Require(new RenameDecision(RenameKind.Follow).WrittenSide() is null,
            "A Follow without a moved side keeps its nonthrowing null classification.");

        foreach (var kind in new[] { RenameKind.None, RenameKind.Adopt, RenameKind.Split,
            RenameKind.Leave, RenameKind.Hold, RenameKind.Notice, RenameKind.Vacate })
        foreach (var side in new Side?[] { null, Side.Local, Side.Dropbox })
            Check.Require(new RenameDecision(kind, side).WrittenSide() is null,
                $"{kind} with moved side {side?.ToString() ?? "null"} has no written side.");

        // Methods, not record properties: formatting is the positional record's text and no method member appears.
        var hold = new RenameDecision(RenameKind.Hold);
        Check.Require(hold.ToString() ==
            $"RenameDecision {{ Kind = {hold.Kind}, MovedSide = {hold.MovedSide}, Target = {hold.Target}, " +
            $"NoticeKind = {hold.NoticeKind}, Cause = {hold.Cause}, Destination = {hold.Destination}, " +
            $"Unfinished = {hold.Unfinished}, RetryWait = {hold.RetryWait} }}",
            "RenameDecision formatting remains the positional record's members only.");
        foreach (var kind in Enum.GetValues<RenameKind>())
        {
            var text = new RenameDecision(kind, MovedSide: Side.Local).ToString();
            Check.Require(text.StartsWith("RenameDecision {") && !text.Contains("WrittenSide"),
                $"{kind} formats without the written-side method.");
        }
    }
}
