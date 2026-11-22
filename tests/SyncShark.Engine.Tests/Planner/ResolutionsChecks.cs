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
        SyncedEntry Binding(Entry pc, Entry remote) => new(PathNames.Key(pc.Path),pc.Path,PathNames.Key(remote.Path),remote.Path,false,pc.Id,pc.Size,pc.Modified.UtcTicks,remote.Id,remote.ContentHash);
        var left = new Row("ITEM", original, copy, Binding(original,copy), [], null, RowContext.Neutral);
        var right = left with { Local=label, Dropbox=cloud, Synced=Binding(label,cloud) };
        var notice = new Notice(1, NoticeKind.Info, TextKey.InfoKeptBoth, "item", new IssueSubject([
            new(Side.Local,original.Path,original.Id),new(Side.Dropbox,cloud.Path,cloud.Id),
            new(Side.Dropbox,copy.Path,copy.Id),new(Side.Local,label.Path,label.Id)], null), default, false);
        var choices = Resolutions.Choices(notice);
        Check.Require(choices.Contains(ResolutionChoice.UseLocal) && !choices.Contains(ResolutionChoice.KeepLocal) &&
            Resolutions.Choices(notice with {Cause=TextKey.InfoMovedTwice}).Contains(ResolutionChoice.KeepLocal) &&
            !Resolutions.Choices(notice with {Cause=TextKey.InfoIgnoredFolderKept}).Contains(ResolutionChoice.UseLocal), "The projection must distinguish the individual Info causes");
        foreach (var side in new[]{Side.Local,Side.Dropbox})
        {
            var one = notice with {Subject=new(notice.Subject.Items.Where((item,i) => i < 2 || item.Side == side).ToArray(),null)};
            var offered = Resolutions.Choices(one);
            Check.Require(offered.Contains(ResolutionChoice.RenameLocal) == (side == Side.Local) &&
                offered.Contains(ResolutionChoice.RenameDropbox) == (side == Side.Dropbox), "A one-label notice must offer only the rename that can run");
            var action = Resolutions.For(one,side == Side.Local ? ResolutionChoice.RenameLocal : ResolutionChoice.RenameDropbox,"renamed",
                side == Side.Local ? left with {Dropbox=null,Synced=null} : left, side == Side.Dropbox ? right with {Local=null,Synced=null} : right);
            Check.Require(action is [{Kind:ActionKind.MoveUnlinked,Target:"renamed"}] && action[0].Side == side &&
                action[0].Row.EntryOn(side) == (side == Side.Local ? label : copy), "The offered rename must select the actual label");
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
            return new(PathNames.Key(path),pc,remote,Binding(pc,remote),[],null,RowContext.Neutral);
        }
        var pcPlace=Place("first-place","first"); var remotePlace=Place("other-place","other");
        var items=new[]{pcPlace,remotePlace}.SelectMany(row => new NoticeItem[]{new(Side.Local,row.Local!.Path,row.Local.Id), new(Side.Dropbox,row.Dropbox!.Path,row.Dropbox.Id)}).ToArray();
        var moved=Resolutions.For(notice with {Cause=TextKey.InfoMovedTwice, Subject=new(items,null)}, ResolutionChoice.KeepDropbox,null,pcPlace,remotePlace);
        Check.Require(moved is [{Kind:ActionKind.DeleteLocal, Target:"first-place"}, {Kind:ActionKind.DeleteDropbox, Target:"first-place"}] && moved.All(a => a.Row == pcPlace), "Keep-Dropbox removes only the PC-place binding");
        return Task.CompletedTask;
    }
}
