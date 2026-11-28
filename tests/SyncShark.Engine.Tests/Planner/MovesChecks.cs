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
        SyncAction Both(Entry remote) => Moves.For([left, right with { Moved = remote }], null, facts with {LocalDestination=remote.Path}, false);
        Check.Require(Both(cloud) is {Kind: ActionKind.Record, InfoAfter: TextKey.InfoCapitalsDiffer, Row.Local.Path: "ITEM", Row.Dropbox.Path: "Item"}, "Different capitals must keep both spellings on one record");
        Check.Require(Both(cloud with {Path = "ITEM"}) is {Kind: ActionKind.Record, InfoAfter: null}, "Equal destinations should adopt without a notice");
        Check.Require(Both(cloud with {Path = "different"}) is {Kind:ActionKind.ForgetOnly, InfoAfter:TextKey.InfoMovedTwice}, "Different destinations must split");
        var mappedLeft = left with {Moved=pc with {Path="Left/x",IsFolder=true}, Synced=saved with {IsFolder=true}};
        var mappedRight = right with {Moved=cloud with {Path="Right/x",IsFolder=true}, Synced=mappedLeft.Synced};
        var together = Moves.For([mappedLeft,mappedRight], null, facts with {LocalDestination="Left/x",DropboxDestination="Right/x"},false);
        Check.Require(together is {Kind:ActionKind.Record, InfoAfter:null, Row.Local.Path:"Left/x", Row.Dropbox.Path:"Right/x"} && together.Row.Synced == mappedLeft.Synced,
            "One mapped destination was split");
        var missing = Moves.For([left],null,facts with {DropboxAtSynced=null},false);
        Check.Require(missing is {Kind:ActionKind.ForgetOnly,InfoAfter:TextKey.InfoMovedDeleted} && missing.Row.Synced == saved,"Missing counterpart must split only the saved binding");
        return Task.CompletedTask;
    }

    /// <summary>These one-sided moves use the ordinary content comparison before deciding on the destination.</summary>
    public static Task CounterpartAsync()
    {
        var pc = new Entry("C",false,3,default,"local","","",null);
        var cloud = pc with {Path="item",Id="remote",ContentHash="base"};
        var saved = new SyncedEntry("ITEM","item","ITEM","item",false,pc.Id,3,0,cloud.Id,"base");
        foreach (var folder in new[] { false, true })
        foreach (var movedSide in new[] { Side.Local, Side.Dropbox })
        {
            var binding = saved with {IsFolder=folder};
            var local = pc with {Path="item", IsFolder=folder};
            var remote = cloud with {Path="item", IsFolder=folder};
            var source = (movedSide == Side.Local ? local : remote) with {Path="iTeM"};
            var obs = new Moves.Observations(movedSide == Side.Local ? null : local, movedSide == Side.Dropbox ? null : remote,
                false,false,false,false,null,"iTeM","iTeM");
            var action = Moves.For([new(movedSide,source,binding)],null,obs,false);
            var written = PathNames.Other(movedSide);
            Check.Require(action.Kind == (written == Side.Local ? ActionKind.MoveLocal : ActionKind.MoveDropbox) &&
                action.Side == written && action.Target == "iTeM" && action.Row.EntryOn(written)?.Path == "item" &&
                action.Row.EntryOn(movedSide) == source && action.Row.Synced == binding && action.InfoAfter is null,
                "A one-sided capitals change must move the counterpart to the exact spelling, for files and folders");
        }
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
        Check.Require(One(facts with {LocalHash="base",LeavesScope=true}) is {Kind:ActionKind.DeleteLocal, Row.Dropbox:null} && One(facts with {LocalHash="different",LeavesScope=true}) is {Kind:ActionKind.Upload, InfoAfter:TextKey.InfoEditBeatsDelete},"A move out of scope did not take the ordinary delete or keep the changed peer");
        // A clean (stamp-settled) by-ID move out of scope takes the ordinary delete without a content read.
        Check.Require(Moves.For([right], null, facts with {LeavesScope=true}, false) is {Kind:ActionKind.DeleteLocal, Row.Dropbox:null}, "A clean move out of scope did not take the ordinary delete");
        return Task.CompletedTask;
    }
}
