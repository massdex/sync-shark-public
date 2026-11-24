using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

public static class ResolutionsChecks
{
    public static Task RunAsync()
    {
        var original = new Entry("item", false, 1, default, "pc", "", "", null);
        var copy = original with {Path="pc-label", Id="copy-pc"};
        var cloud = original with {Id="cloud"};
        var label = cloud with {Path="cloud-label", Id="copy-cloud"};
        var left = new Row("ITEM", original, copy, SyncedEntries.From(original,copy), [], null, RowContext.Neutral);
        var right = left with { Local=label, Dropbox=cloud, Synced=SyncedEntries.From(label,cloud) };
        var notice = new Notice(1, NoticeKind.Info, TextKey.InfoKeptBoth, "item", new IssueSubject([
            new(Side.Local,original.Path,original.Id),new(Side.Dropbox,cloud.Path,cloud.Id),
            new(Side.Dropbox,copy.Path,copy.Id),new(Side.Local,label.Path,label.Id)], null), default, false);
        var choices = Resolutions.Choices(notice);
        Check.Require(choices.Contains(ResolutionChoice.UseLocal) && !choices.Contains(ResolutionChoice.KeepLocal) &&
            Resolutions.Choices(notice with {Cause=TextKey.InfoMovedTwice}).Contains(ResolutionChoice.KeepLocal) &&
            !Resolutions.Choices(notice with {Cause=TextKey.InfoIgnoredFolderKept}).Contains(ResolutionChoice.UseLocal), "The projection must distinguish the individual Info causes");
        var visibilityErrors = new List<string>();
        foreach (var hidden in new[]{false,true})
        {
            var current = notice with {Ignored=hidden};
            var allowed = Resolutions.Choices(current);
            if (allowed.Contains(ResolutionChoice.Ignore)==hidden || allowed.Contains(ResolutionChoice.Unignore)!=hidden)
                visibilityErrors.Add($"Wrong hide/restore projection for Ignored={hidden}");
            var toggle = hidden ? ResolutionChoice.Unignore : ResolutionChoice.Ignore;
            Check.Require(Resolutions.For(current,toggle).Count==0 && Resolutions.For(current,ResolutionChoice.Dismiss).Count==0,
                "Valid row-free choice must return no actions");
            foreach (var choice in new[]{toggle, ResolutionChoice.Dismiss})
            {
                try { Resolutions.For(current,choice,null,left,right); visibilityErrors.Add($"Two-row overload accepted {choice}"); }
                catch (EngineRequestException ex) when (ex.Key==TextKey.ResolveBadChoice) { }
            }
        }
        Check.Require(visibilityErrors.Count==0,string.Join("; ",visibilityErrors));
        var oneLabelFailures = new List<string>();
        foreach (var side in new[]{Side.Local,Side.Dropbox})
        {
            var one = notice with {Subject=new(notice.Subject.Items.Where((item,i) => i < IssueSubject.KeptBothOriginals || item.Side == side).ToArray(),null)};
            var offered = Resolutions.Choices(one);
            Check.Require(offered.Contains(ResolutionChoice.RenameLocal) == (side == Side.Local) &&
                offered.Contains(ResolutionChoice.RenameDropbox) == (side == Side.Dropbox), "A one-label notice must offer only the rename that can run");
            var action = Resolutions.For(one,side == Side.Local ? ResolutionChoice.RenameLocal : ResolutionChoice.RenameDropbox,"renamed",
                side == Side.Local ? left with {Dropbox=null,Synced=null} : left, side == Side.Dropbox ? right with {Local=null,Synced=null} : right);
            Check.Require(action is [{Kind:ActionKind.MoveUnlinked,Target:"renamed"}] && action[0].Side == side &&
                action[0].Row.EntryOn(side) == (side == Side.Local ? label : copy), "The offered rename must select the actual label");
            foreach (var mixedKinds in new[]{false,true})
            {
                var remote = cloud with {Path="other-parent/item",IsFolder=mixedKinds};
                var pcLabel = label with {IsFolder=remote.IsFolder};
                var localRow = side == Side.Dropbox ? left : left with {Dropbox=null,Synced=null};
                var remoteRow = right with {Local=side==Side.Local ? pcLabel : null, Dropbox=remote,
                    Synced=side==Side.Local ? SyncedEntries.From(pcLabel,remote) : null};
                var scoped = one with {Subject=new(one.Subject.Items.Select(item => item.Id==cloud.Id ? item with {Path=remote.Path} : item).ToArray(),null)};
                foreach (var choice in new[]{ResolutionChoice.UseLocal,ResolutionChoice.UseDropbox})
                {
                    var written = choice == ResolutionChoice.UseLocal ? Side.Dropbox : Side.Local;
                    var plan = Resolutions.For(scoped,choice,null,localRow,remoteRow);
                    var labelRow = side == Side.Local ? remoteRow : localRow;
                    if (!(plan is [var delete,var copyOriginal] && delete.Kind == (side==Side.Local ? ActionKind.DeleteLocal : ActionKind.DeleteDropbox) &&
                        delete.Side==side && delete.Row==labelRow && delete.Row.Synced?.IsFolder==labelRow.EntryOn(side)!.IsFolder && delete.Target==labelRow.EntryOn(side)!.Path &&
                        copyOriginal.Kind==(mixedKinds ? ActionKind.ReplaceKind : written==Side.Local ? ActionKind.Download : ActionKind.Upload) &&
                        copyOriginal.Side==written && copyOriginal.Target==(written==Side.Local ? original.Path : remote.Path) &&
                        copyOriginal.Row.Local==original && copyOriginal.Row.Dropbox==remote && copyOriginal.Row.Synced==null))
                        oneLabelFailures.Add($"{side} label, {choice}, mixed={mixedKinds}: expected sole bound label delete then original transfer");
                }
            }
        }
        Check.Require(oneLabelFailures.Count==0,string.Join("; ",oneLabelFailures));
        // The reviewed Dropbox label in Extra cannot authorize acting on its successor in the binding row.
        var successor = left with {Dropbox=copy with {Id="successor"}, Extra=[copy]};
        foreach (var choice in new[]{ResolutionChoice.UseLocal, ResolutionChoice.UseDropbox, ResolutionChoice.RenameDropbox})
        {
            try
            {
                Resolutions.For(notice,choice,"renamed",successor,right);
                throw new InvalidDataException($"{choice} accepted a replacement label at the reviewed path");
            }
            catch (EngineRequestException ex) when (ex.Key == TextKey.ResolveSubjectStale) { }
        }
        var rename = Resolutions.For(notice, ResolutionChoice.RenameLocal, "renamed", left, right);
        Check.Require(rename is [{Kind: ActionKind.MoveUnlinked, Target:"renamed", Row.Local.Id:"copy-cloud"}], "Rename the selected label, not the original");
        try
        {
            Resolutions.For(notice, ResolutionChoice.UseLocal, null, left, right with {Local=label with {Id="replacement"}});
            throw new InvalidDataException("A changed fourth endpoint must reject the whole choice");
        }
        catch (EngineRequestException ex) when (ex.Key == TextKey.ResolveSubjectStale) { }
        try
        {
            Resolutions.For(notice, ResolutionChoice.UseLocal, null, right, left);
            throw new InvalidDataException("Swapped binding rows must not put either original in the bin");
        }
        catch (EngineRequestException ex) when (ex.Key == TextKey.ResolveSubjectStale) { }
        var actions = Resolutions.For(notice, ResolutionChoice.UseLocal, null, left, right);
        Check.Require(actions is [{Kind:ActionKind.DeleteDropbox, Target:"pc-label"}, {Kind:ActionKind.DeleteLocal, Target:"cloud-label"}, {Kind:ActionKind.Upload, Target:"item", Row.Local.Id:"pc", Row.Dropbox.Id:"cloud", Row.Synced:null}], "Use-PC must remove both labels before copying the original");
        Check.Require(actions[0].Row.Synced == left.Synced && actions[1].Row.Synced == right.Synced, "Use-PC must retire the selected label bindings");
        var mirror = Resolutions.For(notice, ResolutionChoice.UseDropbox, null, left, right);
        Check.Require(mirror is [{Kind:ActionKind.DeleteLocal, Side:Side.Local, Target:"cloud-label"}, {Kind:ActionKind.DeleteDropbox, Side:Side.Dropbox, Target:"pc-label"}, {Kind:ActionKind.Download, Side:Side.Local, Target:"item", Row.Local.Id:"pc", Row.Dropbox.Id:"cloud", Row.Synced:null}] &&
            mirror[0].Row.Synced == right.Synced && mirror[1].Row.Synced == left.Synced, "Use-Dropbox must delete PC label then Dropbox label, preserving the bindings, then download the original");
        Row Place(string path, string id)
        {
            var pc=original with {Path=path,Id=id+"-pc"}; var remote=cloud with {Path=path,Id=id+"-remote"};
            return new(PathNames.Key(path),pc,remote,SyncedEntries.From(pc,remote),[],null,RowContext.Neutral);
        }
        var pcPlace=Place("first-place","first"); var remotePlace=Place("other-place","other");
        var items=new[]{pcPlace,remotePlace}.SelectMany(row => new NoticeItem[]{new(Side.Local,row.Local!.Path,row.Local.Id), new(Side.Dropbox,row.Dropbox!.Path,row.Dropbox.Id)}).ToArray();
        var moved=Resolutions.For(notice with {Cause=TextKey.InfoMovedTwice, Subject=new(items,null)}, ResolutionChoice.KeepDropbox,null,pcPlace,remotePlace);
        Check.Require(moved is [{Kind:ActionKind.DeleteLocal, Target:"first-place"}, {Kind:ActionKind.DeleteDropbox, Target:"first-place"}] && moved.All(a => a.Row == pcPlace), "Keep-Dropbox removes only the PC-place binding");
        var errors = new List<string>();
        var keepPc=Resolutions.For(notice with {Cause=TextKey.InfoMovedTwice,Subject=new(items,null)},ResolutionChoice.KeepLocal,null,pcPlace,remotePlace);
        if (!(keepPc is [{Kind:ActionKind.DeleteDropbox,Side:Side.Dropbox,Target:"other-place"},{Kind:ActionKind.DeleteLocal,Side:Side.Local,Target:"other-place"}] && keepPc.All(a => a.Row == remotePlace)))
            errors.Add("KeepLocal did not retire exactly the Dropbox-place binding");
        var pair=left with {Local=original,Dropbox=cloud,Synced=SyncedEntries.From(original,cloud)};
        var subject=new IssueSubject([new(Side.Local,original.Path,original.Id),new(Side.Dropbox,cloud.Path,cloud.Id)],null);
        foreach (var (cause,choice,kind,side) in new[]{
            (TextKey.TieEqualTimes,ResolutionChoice.UseLocal,ActionKind.Upload,(Side?)null),
            (TextKey.TieUnreliableTime,ResolutionChoice.UseDropbox,ActionKind.Download,null),
            (TextKey.TieEqualTimes,ResolutionChoice.KeepBoth,ActionKind.KeepBoth,null),
            (TextKey.TieCaseBoth,ResolutionChoice.UseLocal,ActionKind.MoveDropbox,null),
            (TextKey.TieCaseBoth,ResolutionChoice.UseDropbox,ActionKind.MoveLocal,null),
            (TextKey.TieKindBoth,ResolutionChoice.UseLocal,ActionKind.ReplaceKind,Side.Dropbox),
            (TextKey.TieKindBoth,ResolutionChoice.UseDropbox,ActionKind.ReplaceKind,Side.Local)})
        {
            var tieRow = cause == TextKey.TieCaseBoth ? pair with {Dropbox=cloud with {Path="ITEM"}} :
                cause == TextKey.TieKindBoth ? pair with {Dropbox=cloud with {IsFolder=true}} : pair;
            var tie = notice with {Kind=NoticeKind.Tie,Cause=cause,Subject=new([subject.Items[0],new(Side.Dropbox,tieRow.Dropbox!.Path,cloud.Id)],null)};
            var plan=Resolutions.For(tie,choice,null,tieRow,tieRow);
            var target=kind == ActionKind.KeepBoth ? null : kind == ActionKind.MoveLocal ? "ITEM" : "item";
            if (!(plan is [var action] && action.Kind == kind && action.Target == target && action.Side == side && action.Row == tieRow))
                errors.Add($"legacy {cause}/{choice} did not select {kind} on the reviewed pair");
        }
        var clash=notice with {Kind=NoticeKind.NameClash,Cause=TextKey.ClashNameTaken,Subject=subject with {Incoming=Side.Local}};
        var renamed=Resolutions.For(clash,ResolutionChoice.RenameIncoming,"free",pair,pair);
        var replaced=Resolutions.For(clash,ResolutionChoice.ReplaceExisting,null,pair,pair);
        Check.Require(renamed is [{Kind:ActionKind.MoveUnlinked,Side:Side.Local,Target:"free"}] && renamed[0].Row.Local==original &&
            replaced is [{Kind:ActionKind.Upload,Target:"item",Row.Synced:null}] && replaced[0].Row.Local==original && replaced[0].Row.Dropbox==cloud,
            "Legacy clash must rename its reviewed incoming item or replace the reviewed occupant");
        // The host-startup notice frame and its no-pair choice line are the planner contract for the refused-store slot.
        Check.Require(Text.Get(TextKey.HostStartupFailed, (TextArg.Reason, "denied")).Contains("denied", StringComparison.Ordinal),
            "Host startup notice must carry the observed reason");
        var hostChoice = Text.Get(TextKey.ResultIssueVisibilityChoiceHost, (TextArg.Name, "retry"), (TextArg.Id, "7"), (TextArg.Value, "tok"), (TextArg.Choice, "retry"));
        Check.Require(hostChoice.Contains("--issue 7", StringComparison.Ordinal) && hostChoice.Contains("--reviewed tok", StringComparison.Ordinal) && !hostChoice.Contains("--pair", StringComparison.Ordinal),
            "Host choice line must omit a pair while naming issue, token and choice");
        Check.Require(errors.Count == 0,string.Join("; ",errors));
        return Task.CompletedTask;
    }
}
