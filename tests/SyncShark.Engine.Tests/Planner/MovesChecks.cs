using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

public static class MovesChecks
{
    public static Task RunAsync()
    {
        var pc = new Entry("ITEM", false, 3, default, "local", "", "", null);
        var cloud = pc with { Path = "Item", Id = "remote", ContentHash = "base" };
        var saved = new SyncedEntry("ITEM", "item", "ITEM", "item", false, pc.Id, 3, 0, cloud.Id, "base");
        var left = new RenameCandidate(Side.Local, pc, saved);
        var right = new RenameCandidate(Side.Dropbox, cloud, saved);
        var facts = new Moves.Observations(null, null, false, false, false, false, null, "Item", "ITEM");
        SyncAction Both(Entry remote) => Moves.For([left, right with { Moved = remote }], null, facts, false);
        Check.Require(Both(cloud) is {Kind: ActionKind.Record, InfoAfter: TextKey.InfoCapitalsDiffer, Row.Local.Path: "ITEM", Row.Dropbox.Path: "Item"}, "Different capitals must keep both spellings on one record");
        Check.Require(Both(cloud with {Path = "ITEM"}) is {Kind: ActionKind.Record, InfoAfter: null}, "Equal destinations should adopt without a notice");
        Check.Require(Both(cloud with {Path = "different"}).InfoAfter == TextKey.InfoMovedTwice, "Different destinations must split");
        right = right with {Moved = cloud with {Path="new"}};
        facts = facts with {LocalDestination="new"};
        SyncAction One(Moves.Observations f) => Moves.For([right], null, f, true);
        Check.Require(One(facts).InfoAfter == TextKey.InfoMovedDeleted, "Absent counterpart must not discard the move");
        facts = facts with { LocalAtSynced = pc with {Path="item"} };
        Check.Require(One(facts).Kind == ActionKind.NeedLocalHash && One(facts with { LocalHash="different" }).InfoAfter == TextKey.InfoMovedEdited, "Same-size dirty content needs a hash before splitting");
        Check.Require(One(facts with {LocalHash="base"}) is {Kind:ActionKind.MoveLocal, Target:"new"}, "Unchanged counterpart must follow");
        Check.Require(One(facts with {CarriesOtherPair=true}).Cause == TextKey.FailCarriesOtherPair && One(facts with {OldOrNewHeld=true}).None == NoneReason.Held && One(facts with {NewOutOfScope=true}).None == NoneReason.OutOfScope, "Eligibility must precede all move legs");
        return Task.CompletedTask;
    }
}
