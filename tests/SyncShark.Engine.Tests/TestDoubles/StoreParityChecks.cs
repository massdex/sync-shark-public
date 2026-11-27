namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

internal static class StoreParityChecks
{
    internal static Task RunAsync() => Run.RepairChecks.WithRepairStore(async (sqlite,pair,ct) =>
    {
        using var memory=new InMemoryStateStore();await memory.SavePairAsync(pair,ct);
        var expected=await Observe(sqlite,pair.PairId,ct);
        var actual=await Observe(memory,pair.PairId,ct);
        Check.Require(expected.SequenceEqual(actual),$"Store parity: SQLite {string.Join(",",expected)}; memory {string.Join(",",actual)}");
        // Also self-check this lane's harness seams on real code; no separate registered entry exists for them.
        await Massdex.SyncShark.Engine.Tests.Harness.HarnessSelfCheck.RunAsync(ct);
    });

    private static async Task<List<bool>> Observe(IStore store,string pair, CancellationToken ct)
    {
        var scan=Guid.NewGuid(); var other=Guid.NewGuid(); var values=new List<bool>();
        bool Touched(string path,bool tree=false)=>store.ScanTouched(pair,scan,Side.Local,path,tree);
        values.Add(Touched("placed")); store.BeginScan(pair,scan); values.Add(Touched("placed"));
        values.Add(await store.DeletedHistoryAsync(pair,scan,ct)==(null,true));
        static async IAsyncEnumerable<Listed> Rows(){await Task.CompletedTask;yield return new("gone",null);}
        await store.AddScanAsync(pair,scan,Side.Dropbox,Rows(),()=>"checkpoint",ct);
        var child=PathNames.DropboxKey("gone/child");
        values.Add(await store.DeletedNameAsync(pair,scan,child,true,ct));
        values.Add(await store.DeletedNameAsync(pair,scan,child,false,ct));
        values.Add(await store.DeletedNameAsync(pair,other,child,true,ct));
        await store.PublishDeletedHistoryAsync(pair,scan,ct);
        values.Add(await store.DeletedHistoryAsync(pair,other,ct)==("checkpoint",false));
        values.Add(await store.DeletedNameAsync(pair,other,child,true,ct));
        await store.CommitAsync(pair,null,null,null,child,ct);
        values.Add(await store.DeletedNameAsync(pair,other,child,true,ct));
        var link=SyncedEntries.From(Scenarios.ItemChecks.File(Side.Local,"placed/item",1,"11"),Scenarios.ItemChecks.File(Side.Dropbox,"placed/item",1,"peer"));
        var notice=new Notice(0,NoticeKind.Info,TextKey.FailChanged,"review",new([],null),DateTimeOffset.UnixEpoch,false);
        await store.CommitKeepBothAsync(pair,null,link,notice,ct);
        values.Add(await store.FindAsync(pair,Side.Local,link.LocalKey,ct)==link);
        var count=0;await foreach(var n in store.NoticesAsync(pair,ct)) if(n.Path==notice.Path && n.Cause==notice.Cause) count++;
        values.Add(count==1);store.BeginScan(pair,scan);values.Add(Touched("placed/item"));values.Add(Touched("placed"));values.Add(Touched("placed",true));values.Add(Touched("review"));
        await store.ClearScanAsync(pair,scan,ct); values.Add(Touched("elsewhere"));
        // A fresh folder link retires a timeless spend but keeps its published-history membership; a rejected insert retires nothing.
        var scan3=Guid.NewGuid(); store.BeginScan(pair,scan3);
        static async IAsyncEnumerable<Listed> More(){await Task.CompletedTask;yield return new("spentfolder",null);yield return new("occupied",null);}
        await store.AddScanAsync(pair,scan3,Side.Dropbox,More(),()=>"cp3",ct);
        await store.PublishDeletedHistoryAsync(pair,scan3,ct);
        var xk=PathNames.DropboxKey("spentfolder"); var dk=PathNames.DropboxKey("occupied");
        await store.CommitAsync(pair,null,null,null,xk,ct);
        values.Add(await store.DeletedNameAsync(pair,null,xk,true,ct));
        var xfan=SyncedEntries.From(Scenarios.ItemChecks.File(Side.Local,"spentfolder",0,"pxf",true),Scenarios.ItemChecks.File(Side.Dropbox,"spentfolder",0,"dxf",true));
        await store.CommitAsync(pair,null,xfan,null,null,ct);
        values.Add(await store.DeletedNameAsync(pair,null,xk,true,ct));
        var occ=SyncedEntries.From(Scenarios.ItemChecks.File(Side.Local,"occupier",0,"poc",true),Scenarios.ItemChecks.File(Side.Dropbox,"occupied",0,"doc",true));
        await store.CommitAsync(pair,null,occ,null,null,ct);
        await store.CommitAsync(pair,null,null,null,dk,ct);
        try { await store.CommitAsync(pair,null,SyncedEntries.From(Scenarios.ItemChecks.File(Side.Local,"collide",0,"pcl",true),Scenarios.ItemChecks.File(Side.Dropbox,"occupied",0,"dcl",true)),null,null,ct); } catch(Exception) { }
        values.Add(await store.DeletedNameAsync(pair,null,dk,true,ct));
        return values;
    }
}
