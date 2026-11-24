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
        var latest = left with {Moved=pc with {Path="C"}};
        var receipt = cloud with {Path="B", Revision="r1"};
        var selected = new JobMember(cloud.Id, saved.DropboxPath, "B");
        var oldRemote = cloud with {Path=saved.DropboxPath};
        facts = facts with {LocalDestination="C", DropboxDestination="C", DropboxAtSynced=oldRemote};
        var occupied = new Row("C", latest.Moved, cloud with {Path="C",Id="incumbent",Size=43}, null, [], null, RowContext.Neutral);
        SyncAction Reply(JobReply reply, Entry? seen = null) => Moves.For(latest, occupied, selected, reply,
            seen is null ? null : (new IdentityLocation("pair", seen.Path, seen), true), facts, false);
        var confirmed = Reply(new JobReply.Terminal([(receipt, null, null)]));
        Check.Require(confirmed is {Kind:ActionKind.MoveLocal, Side:Side.Local, Target:"B", Row.Local.Path:"C", Row.Dropbox.Path:"B", InfoAfter:null} && confirmed.Row.Synced == saved,
            "An attributed B hop lands at B without splitting or losing the old binding");
        var settled = Moves.For(latest with {Moved=pc with {Path="B"}}, occupied, selected, new JobReply.Terminal([(receipt,null,null)]),null,facts,false);
        Check.Require(settled is {Kind:ActionKind.Record,Row.Local.Path:"B",Row.Dropbox.Path:"B"} && settled.Row.Synced == saved, "If the PC is already at the selected hop there is no second move");
        var mappedPc = latest with {Moved=pc with {Path="local-parent/C"}};
        var mappedCloud = receipt with {Path="remote-parent/B"};
        var member = selected with {Destination=mappedCloud.Path};
        var mappedFacts = facts with {LocalDestination=mappedPc.Moved.Path};
        var parents = saved with {LocalPath="local-parent", DropboxPath="remote-parent", IsFolder=true};
        SyncedEntry? SavedParent(string key) => key == PathNames.KeyFor(Side.Dropbox, parents.DropboxPath) ? parents : null;
        SyncAction Mapped(RenameCandidate item, Func<string,SyncedEntry?>? lookup) => Moves.For(item, occupied, member,
            new JobReply.Terminal([(mappedCloud,null,null)]), null, mappedFacts, false, lookup);
        var mappedMove = Mapped(mappedPc, SavedParent);
        Check.Require(mappedMove is {Kind:ActionKind.MoveLocal, Target:"local-parent/B", Row.Local.Path:"local-parent/C", Row.Dropbox.Path:"remote-parent/B"} && mappedMove.Row.Synced == saved,
            "Follow the selected target through its saved parent, not the current goal");
        var recasedParent = mappedCloud with {Path="REMOTE-parent/B", Revision="r2", ContentHash="edited"};
        SyncAction Later(Entry seen) => Moves.For(mappedPc, occupied, member, new JobReply.Terminal([(mappedCloud,null,null)]),
            (new IdentityLocation("pair",seen.Path,seen),true), mappedFacts with {DropboxDestination="remote-parent/C"}, false, SavedParent);
        var stillAtLanding = Later(recasedParent);
        Check.Require(stillAtLanding is {Kind:ActionKind.MoveLocal,Side:Side.Local,Target:"local-parent/B",InfoAfter:null} &&
            stillAtLanding.Row.Local == mappedPc.Moved && stillAtLanding.Row.Dropbox == recasedParent && stillAtLanding.Row.Synced == saved,
            "A recased Dropbox parent does not invalidate a confirmed landing; follow its exact leaf using the newer entry");
        var recasedLeaf = recasedParent with {Path="REMOTE-parent/b"};
        var movedAgain = Later(recasedLeaf);
        Check.Require(movedAgain is {Kind:ActionKind.ForgetOnly,InfoAfter:TextKey.InfoMovedTwice} && movedAgain.Row.Dropbox == recasedLeaf,
            "A later change of the leaf spelling is not the confirmed landing");
        var mappedRecord = Mapped(mappedPc with {Moved=pc with {Path="local-parent/B"}},SavedParent);
        Check.Require(mappedRecord is {Kind:ActionKind.Record, Row.Local.Path:"local-parent/B", Row.Dropbox.Path:"remote-parent/B"} && mappedRecord.Row.Synced == saved,
            "Record only when both exact mapped endpoints realize the confirmed hop");
        Check.Require(Mapped(mappedPc,null).Kind == ActionKind.Notice,"A missing parent observation must not guess the local hop");
        var ambiguous = Reply(new JobReply.Invalid(), receipt);
        Check.Require(ambiguous is {Kind:ActionKind.ForgetOnly, InfoAfter:TextKey.InfoMovedTwice} && ambiguous.Row.Synced == saved, "A location alone cannot attribute a move to the queued job");
        Check.Require(Reply(new JobReply.Terminal([(receipt with {Id="successor"}, null, null)])) is {Kind:ActionKind.Notice, Cause:TextKey.FailDropboxOutcomeUnknown}, "A receipt for another ID cannot adopt the move");
        var newer = receipt with {Path="D"};
        var afterHop = Reply(new JobReply.Terminal([(receipt,null,null)]), newer);
        var failures = new List<string>();
        var current = receipt with {Revision="r2", ContentHash="edited", Size=17};
        foreach (var pcPath in new[]{"C","B"})
        {
            var at = latest with {Moved=pc with {Path=pcPath}};
            var fresh = Moves.For(at, occupied, selected, new JobReply.Terminal([(receipt,null,null)]),
                (new IdentityLocation("pair",current.Path,current),true), facts,false);
            if (fresh.Kind != (pcPath == "B" ? ActionKind.Record : ActionKind.MoveLocal) || fresh.Row.Dropbox != current ||
                fresh.Row.Local != at.Moved || fresh.Row.Synced != saved || fresh.Target != (pcPath == "B" ? null : "B"))
                failures.Add($"same-path edit lost on {pcPath}: {fresh.Kind}/{fresh.Row.Dropbox?.Revision}");
        }
        var running = Reply(new JobReply.Running());
        if (!(running is {Kind:ActionKind.None,None:NoneReason.Unfinished,Target:null} && running.Row == occupied))
            failures.Add("Running must retain the selected member without failure or replay");
        Check.Require(Reply(new JobReply.Terminal([(null,TextKey.MoveFailed,FailureDisposition.TemporaryNoEffect)])) is
            {Kind:ActionKind.None,None:NoneReason.Unfinished,Target:null}, "Temporary no-effect is retained");
        Check.Require(Reply(new JobReply.Terminal([(null,TextKey.DropboxRefused,FailureDisposition.Permanent)])) is
            {Kind:ActionKind.Notice,NoticeKind:NoticeKind.Failure,Cause:TextKey.DropboxRefused}, "Permanent refusal preserves its typed cause");
        var unknown = Reply(new JobReply.Terminal([(null,TextKey.FailDropboxOutcomeUnknown,FailureDisposition.UnknownEffect)]),newer);
        Check.Require(unknown is {Kind:ActionKind.ForgetOnly,InfoAfter:TextKey.InfoMovedTwice} && unknown.Row.Dropbox == newer && unknown.Row.Synced == saved,
            "Unknown member uses the by-ID observation, not the intended target");
        if (!(together is {Kind:ActionKind.Record, InfoAfter:null, Row.Local.Path:"Left/x", Row.Dropbox.Path:"Right/x"} && together.Row.Synced == mappedLeft.Synced))
            failures.Add("one mapped destination was split");
        if (!(afterHop is {Kind:ActionKind.ForgetOnly, InfoAfter:TextKey.InfoMovedTwice, Target:null, Row.Local.Path:"C", Row.Dropbox.Path:"D"} && afterHop.Row.Synced == saved))
            failures.Add("a receipt at B overrode the newer by-ID location at D");
        Check.Require(failures.Count == 0, string.Join("; ",failures));
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
        var savedSource = cloud with {Path="remote-parent/A"};
        var seenSource = savedSource with {Path="REMOTE-parent/A",Revision="r2"};
        var bindingAtSource = SyncedEntries.From(pc with {Path="local-parent/A"},savedSource);
        var moved = new RenameCandidate(Side.Local,pc with {Path="local-parent/C"},bindingAtSource);
        var target = new Row("REMOTE-PARENT/C",moved.Moved,null,null,[],null,RowContext.Neutral);
        var selected = new JobMember(cloud.Id,savedSource.Path,"remote-parent/C");
        var observations = new Moves.Observations(null,null,false,false,false,false,null,null,selected.Destination);
        foreach (var reply in new JobReply[]{new JobReply.Invalid(),new JobReply.Terminal([(null,TextKey.FailDropboxOutcomeUnknown,FailureDisposition.UnknownEffect)])})
        {
            SyncAction Recover(Entry seen) => Moves.For(moved,target,selected,reply,(new IdentityLocation("pair",seen.Path,seen),true),observations,false);
            var leaf = seenSource with {Path="REMOTE-parent/a"};
            var independent = Recover(leaf);
            Check.Require(independent is {Kind:ActionKind.ForgetOnly,InfoAfter:TextKey.InfoMovedTwice} && independent.Row.Dropbox==leaf && independent.Row.Synced==bindingAtSource,
                "Unknown move must retain a real change in source leaf spelling");
            var follow = Recover(seenSource);
            Check.Require(follow is {Kind:ActionKind.MoveDropbox,Side:Side.Dropbox,Target:"remote-parent/C",InfoAfter:null} &&
                follow.Row.Local==moved.Moved && follow.Row.Dropbox==seenSource && follow.Row.Synced==bindingAtSource,
                "Unknown move treated a recased source parent as an independent Dropbox move");
        }
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
        return Task.CompletedTask;
    }
}
