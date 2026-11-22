using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Only the original pair of roots can clear a binding refusal.</summary>
public static class PairRunnerChecks
{
    public static async Task RunAsync()
    {
        using var store = new InMemoryStateStore();
        var pair = ItemChecks.Pair();
        var roots = new RootIdentity(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,2,null),new("account","root","home","folder"));
        pair = pair with {Roots=roots,Drive=new(1,2,3)};
        await store.SavePairAsync(pair,default);
        var runner = new PairRunner();
        var changed = roots with {Dropbox=roots.Dropbox with {FolderId="other"}};
        Check.Require(!await runner.CheckRootsAsync(pair,changed,pair.Drive,store,default),"Different folder admitted at the saved path");
        Check.Require(store.OpenNotices.Single().Cause == TextKey.RootDropboxFolderChanged && (await store.PairAsync(pair.PairId,default))!.Roots==roots,"Mismatch replaced the binding or lost its refusal");
        Check.Require(await runner.CheckRootsAsync(pair,roots,pair.Drive,store,default) && !store.OpenNotices.Any(),"Matching identities did not clear the root refusal");
        Check.Require(!await runner.CheckRootsAsync(pair,changed,new(2,2,3),store,default) && store.OpenNotices.Single().Kind==NoticeKind.DriveChange,"Drive change did not outrank folder mismatch");
    }
}
