using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

public static class FolderDeletesChecks
{
    public static Task RunAsync()
    {
        var folder = new Entry("a", true, 0, default, "folder", "", "", null);
        var context = RowContext.Neutral with { FirstRun = true, CompleteInventory = true, DeletionEligible = true, Destination = "a" };
        var row = new Row("A", folder, null, null, [], null, context, new DeletionEvidence.DeletedPath("a", "a"));
        Check.Require(DecideFolder(row, false).Kind == ActionKind.DeleteLocal, "An unmarked deleted folder follows the delete");
        Check.Require(DecideFolder(row, true) is { Kind: ActionKind.EnsureDropboxFolder, InfoAfter: null }, "Readable protected content must sync quietly");
        Check.Require(DecideFolder(row, true, TextKey.ReadLocalFailed) is { Kind: ActionKind.Notice, NoticeKind: NoticeKind.Failure, Cause: TextKey.ReadLocalFailed }, "An unreadable child must retain the folder and the actual read failure");
        Check.Require(DecideFolder(row with { Context = context with { CompleteInventory = false } }, false).Kind == ActionKind.None, "A partial comparison cannot remove a folder");
        Check.Require(DecideFolder(row with {Deletion=new DeletionEvidence.HistoryUnavailable("a", TextKey.ReadDropboxFailed)}, false) is {Kind:ActionKind.EnsureDropboxFolder, Target:"a", InfoAfter:null}, "Unconfirmed history must not remove a folder");
        for (var mask = 0; mask < 32; mask++)
        {
            var verdict = FolderDeletes.Classify((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0, (mask & 16) != 0);
            Check.Require(verdict == (mask == 0 ? FolderDeletes.FolderVerdict.Removed : FolderDeletes.FolderVerdict.Survives), "Classify must report the actual protection");
            Check.Require(FolderDeletes.SelectBranch(verdict, false, false) == (mask == 0) &&
                !FolderDeletes.SelectBranch(verdict, true, false) && !FolderDeletes.SelectBranch(verdict, false, true), "Only a maximal free branch may be removed");
        }
        var other = new IdentityLocation("pair","/b",folder with {Path="b",Id="other"});
        Check.Require(DecideFolder(row with {Deletion=new DeletionEvidence.LiveElsewhere("a",other)},false) is {Kind:ActionKind.Notice, Cause:TextKey.FailChanged}, "A live ID elsewhere is not the counterpart at this name");
        return LiveTargetAsync();
    }

    private static SyncAction DecideFolder(Row row,bool content,TextKey? unreadable=null) =>
        FolderDeletes.For(row,content,unreadable,FirstRunPolicy.NewestWins,default,false);

    public static Task LiveTargetAsync()
    {
        var pc = new Entry("a", true,0,default,"folder","","",null);
        var cloud = pc with {IsFolder=false, Id="file", Revision="current"};
        var context = RowContext.Neutral with {FirstRun=true,CompleteInventory=true,DeletionEligible=true,Destination="a"};
        var live = new DeletionEvidence.LiveAtTarget("a",new("pair","/a",cloud));
        var row = new Row("A",pc,null,null,[],null,context,live);
        foreach (var policy in new[]{FirstRunPolicy.NewestWins,FirstRunPolicy.LocalWins,FirstRunPolicy.DropboxWins})
        {
            var result = FolderDeletes.For(row,false,null,policy,default,false);
            Check.Require(result.Row.Local == pc && result.Row.Dropbox == cloud && result.Row.Deletion == null &&
                (policy == FirstRunPolicy.NewestWins ? result.Kind == ActionKind.KeepBoth :
                    result.Kind == ActionKind.ReplaceKind && result.Side == (policy == FirstRunPolicy.LocalWins ? Side.Dropbox : Side.Local)),
                "A live file discovered at the PC folder target must enter ordinary two-sided first sync with its actual revision and policy");
        }
        return Task.CompletedTask;
    }
}
