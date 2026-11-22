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
        var left = new Row("ITEM", original, copy, null, [], null, RowContext.Neutral);
        var right = left with { Local=label, Dropbox=cloud };
        var notice = new Notice(1, NoticeKind.Info, TextKey.InfoKeptBoth, "item", new IssueSubject([
            new(Side.Local,original.Path,original.Id),new(Side.Dropbox,cloud.Path,cloud.Id),
            new(Side.Dropbox,copy.Path,copy.Id),new(Side.Local,label.Path,label.Id)], null), default, false);
        var choices = Resolutions.Choices(notice);
        Check.Require(choices.Contains(ResolutionChoice.UseLocal) && !choices.Contains(ResolutionChoice.KeepLocal) &&
            Resolutions.Choices(notice with {Cause=TextKey.InfoMovedTwice}).Contains(ResolutionChoice.KeepLocal) &&
            !Resolutions.Choices(notice with {Cause=TextKey.InfoIgnoredFolderKept}).Contains(ResolutionChoice.UseLocal), "The projection must distinguish the individual Info causes");
        var rename = Resolutions.For(notice, ResolutionChoice.RenameLocal, "renamed", left, right);
        Check.Require(rename is [{Kind: ActionKind.MoveUnlinked, Target:"renamed", Row.Local.Id:"copy-cloud"}], "Rename the selected label, not the original");
        try
        {
            Resolutions.For(notice, ResolutionChoice.UseLocal, null, left, right with {Local=label with {Id="replacement"}});
            throw new InvalidDataException("A changed fourth endpoint must reject the whole choice");
        }
        catch (EngineRequestException ex) when (ex.Key == TextKey.ResolveSubjectStale) { }
        var actions = Resolutions.For(notice, ResolutionChoice.UseLocal, null, left, right);
        Check.Require(actions is [{Kind:ActionKind.DeleteDropbox, Target:"pc-label"}, {Kind:ActionKind.DeleteLocal, Target:"cloud-label"}, {Kind:ActionKind.Upload, Target:"item", Row.Local.Id:"pc", Row.Dropbox.Id:"cloud", Row.Synced:null}], "Use-PC must remove both labels before copying the original");
        var moved = Resolutions.For(notice with {Cause=TextKey.InfoMovedTwice}, ResolutionChoice.KeepDropbox, null, left, right);
        Check.Require(moved is [{Kind:ActionKind.DeleteLocal, Row.Local.Id:"pc"}, {Kind:ActionKind.DeleteDropbox, Row.Dropbox.Id:"copy-pc"}], "Keep-Dropbox removes only the PC-place binding");
        return Task.CompletedTask;
    }
}
