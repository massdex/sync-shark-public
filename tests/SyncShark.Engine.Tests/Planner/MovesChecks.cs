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
        Check.Require(Both(cloud with {Path = "different"}) is {Kind:ActionKind.ForgetOnly, InfoAfter:TextKey.InfoMovedTwice}, "Different destinations must split");
        var latest = left with {Moved=pc with {Path="C"}};
        var receipt = cloud with {Path="B"};
        var selected = new JobMember(cloud.Id, saved.DropboxPath, "B");
        var oldRemote = cloud with {Path=saved.DropboxPath};
        facts = facts with {LocalDestination="B", DropboxDestination="C", DropboxAtSynced=oldRemote};
        var occupied = new Row("C", latest.Moved, cloud with {Path="C",Id="incumbent",Size=43}, null, [], null, RowContext.Neutral);
        SyncAction Reply(JobReply reply, Entry? seen = null) => Moves.For(latest, occupied, selected, reply,
            seen is null ? null : (new IdentityLocation("pair", seen.Path, seen), true), facts, false);
        var confirmed = Reply(new JobReply.Terminal([(receipt, null, null)]));
        Check.Require(confirmed is {Kind:ActionKind.MoveLocal, Side:Side.Local, Target:"B", Row.Local.Path:"C", Row.Dropbox.Path:"B", InfoAfter:null} && confirmed.Row.Synced == saved,
            "An attributed B hop lands at B without splitting or losing the old binding");
        var settled = Moves.For(latest with {Moved=pc with {Path="B"}}, occupied, selected, new JobReply.Terminal([(receipt,null,null)]),null,facts,false);
        Check.Require(settled.Kind == ActionKind.Record && settled.Row.Synced == saved, "If the PC is already at the selected hop there is no second move");
        var ambiguous = Reply(new JobReply.Invalid(), receipt);
        Check.Require(ambiguous is {Kind:ActionKind.ForgetOnly, InfoAfter:TextKey.InfoMovedTwice} && ambiguous.Row.Synced == saved, "A location alone cannot attribute a move to the queued job");
        Check.Require(Reply(new JobReply.Terminal([(receipt with {Id="successor"}, null, null)])) is {Kind:ActionKind.Notice, Cause:TextKey.FailDropboxOutcomeUnknown}, "A receipt for another ID cannot adopt the move");
        var missing = Moves.For([left],null,facts with {DropboxAtSynced=null},false);
        Check.Require(missing is {Kind:ActionKind.ForgetOnly,InfoAfter:TextKey.InfoMovedDeleted} && missing.Row.Synced == saved,"Missing counterpart must split only the saved binding");
        return CounterpartAsync();
    }

    /// <summary>These one-sided moves use the ordinary content comparison before deciding on the destination.</summary>
    public static Task CounterpartAsync()
    {
        var pc = new Entry("C",false,3,default,"local","","",null);
        var cloud = pc with {Path="item",Id="remote",ContentHash="base"};
        var saved = new SyncedEntry("ITEM","item","ITEM","item",false,pc.Id,3,0,cloud.Id,"base");
        var latest = new RenameCandidate(Side.Local,pc,saved);
        var right = new RenameCandidate(Side.Dropbox,cloud with {Path="new"},saved);
        var oldRemote = cloud;
        var facts = new Moves.Observations(null,oldRemote,false,false,false,false,null,"new","C");
        var occupied = new Row("C",pc,cloud with {Path="C",Id="incumbent",Size=43},null,[],null,RowContext.Neutral);
        var incumbent = saved with {DropboxId="incumbent", DropboxPath="C", DropboxKey="C"};
        SyncAction At(Moves.DestinationIdentity disposition) => Moves.For([latest], occupied,
            facts with {Destination=new(incumbent,Side.Dropbox,disposition)},false);
        var here = At(Moves.DestinationIdentity.Here);
        Check.Require(here is {Kind:ActionKind.MoveDropbox, Target:"C", Side:Side.Dropbox} && here.Row.Dropbox == oldRemote && here.Row.Synced == saved,
            "An occupied target is not the selected source; Effects makes the collision decision");
        var gone = At(Moves.DestinationIdentity.Gone);
        Check.Require(gone is {Kind:ActionKind.ForgetOnly,Target:"C",Side:Side.Dropbox} && gone.Row.Synced == incumbent &&
            At(Moves.DestinationIdentity.Moving) is {Kind:ActionKind.None,None:NoneReason.Held}, "Vacate only the proved-gone destination binding; wait for a moving incumbent");
        SyncAction One(Moves.Observations f) => Moves.For([right], null, f, true);
        Check.Require(One(facts) is {Kind:ActionKind.ForgetOnly, InfoAfter:TextKey.InfoMovedDeleted}, "Absent counterpart must not discard the move");
        facts = facts with { LocalAtSynced = pc with {Path="item"} };
        Check.Require(One(facts).Kind == ActionKind.NeedLocalHash && One(facts with { LocalHash="different" }) is {Kind:ActionKind.ForgetOnly, InfoAfter:TextKey.InfoMovedEdited}, "Same-size dirty content needs a hash before splitting");
        Check.Require(One(facts with {LocalHash="base"}) is {Kind:ActionKind.MoveLocal, Target:"new"}, "Unchanged counterpart must follow");
        Check.Require(One(facts with {CarriesOtherPair=true}).Cause == TextKey.FailCarriesOtherPair && One(facts with {OldOrNewHeld=true}).None == NoneReason.Held && One(facts with {NewOutOfScope=true}).None == NoneReason.OutOfScope, "Eligibility must precede all move legs");
        return Task.CompletedTask;
    }
}
