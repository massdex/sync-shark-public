using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

public static class FolderDeletesChecks
{
    public static Task RunAsync()
    {
        var folder = new Entry("a", true, 0, default, "folder", "", "", null);
        var context = RowContext.Neutral with { FirstRun = true, CompleteInventory = true, DeletionEligible = true, Destination = "a" };
        var row = new Row("A", folder, null, null, [], null, context, new DeletionEvidence.DeletedPath("a", "a"));
        Check.Require(FolderDeletes.For(row, false).Kind == ActionKind.DeleteLocal, "An unmarked deleted folder follows the delete");
        Check.Require(FolderDeletes.For(row, true) is { Kind: ActionKind.EnsureDropboxFolder, InfoAfter: null }, "Readable protected content must sync quietly");
        Check.Require(FolderDeletes.For(row, true, TextKey.ReadLocalFailed) is { Kind: ActionKind.Notice, NoticeKind: NoticeKind.Failure, Cause: TextKey.ReadLocalFailed }, "An unreadable child must retain the folder and the actual read failure");
        Check.Require(FolderDeletes.For(row with { Context = context with { CompleteInventory = false } }, false).Kind == ActionKind.None, "A partial comparison cannot remove a folder");
        for (var mask = 0; mask < 32; mask++)
        {
            var verdict = FolderDeletes.Classify((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0, (mask & 16) != 0);
            Check.Require(FolderDeletes.SelectBranch(verdict, false, false) == (mask == 0) &&
                !FolderDeletes.SelectBranch(verdict, true, false) && !FolderDeletes.SelectBranch(verdict, false, true), "Only a maximal free branch may be removed");
        }
        return Task.CompletedTask;
    }
}
