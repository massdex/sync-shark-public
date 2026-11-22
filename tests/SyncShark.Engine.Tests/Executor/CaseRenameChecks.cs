using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Check both folder hops against actual physical endpoints and the unchanged logical binding.</summary>
public static class CaseRenameChecks
{
    public static async Task RunAsync()
    {
        var side = new FakeSide(Side.Dropbox);
        var folder = ItemChecks.File(Side.Dropbox,"old",0,"folder",true);
        side.Seed(folder,[]);
        var binding = SyncedEntries.From(folder with {Id="pc"},folder);
        var start = new Scope("OLD",folder.Path); var goal = new Scope("OLD","OLD");
        var temporary = goal with {DropboxPath=PathNames.CaseRenamePath(goal.DropboxPath,ItemChecks.Settings.CaseRenameSuffix)};
        var hops = new List<(Scope Before, Scope After)>(); var events = new List<RunEvent>();
        var result = await new CaseRename().MoveAsync(side,folder,goal.DropboxPath,ItemChecks.Settings.CaseRenameSuffix,binding,start,(saved,before,after,ct) =>
        {
            Check.Require(saved == binding && side.Items[after.DropboxPath].Entry.Path == after.DropboxPath,"Handoff lost binding or ran before physical movement");
            hops.Add((before,after)); return Task.CompletedTask;
        },default,events.Add);
        Check.Require(result.Final?.Id == folder.Id && hops.SequenceEqual(new[]{(start,temporary),(temporary,goal)}),"Both physical folder ranges must be compared in order");
        Check.Require(events.Select(e=>e.Path).SequenceEqual(new[]{temporary.DropboxPath,goal.DropboxPath}),"Physical legs not logged individually");
    }
}
