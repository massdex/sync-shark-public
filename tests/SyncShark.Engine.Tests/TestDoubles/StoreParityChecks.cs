namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

internal static class StoreParityChecks
{
    internal static Task RunAsync() => Run.RepairChecks.WithRepairStore(async (sqlite,pair,ct) =>
    {
        using var memory=new InMemoryStateStore();await memory.SavePairAsync(pair,ct);
        var expected=await Observe(sqlite,pair.PairId,ct);
        var actual=await Observe(memory,pair.PairId,ct);
        Check.Require(expected.SequenceEqual(actual),$"Store parity: SQLite {string.Join(",",expected)}; memory {string.Join(",",actual)}");
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
        await store.ClearScanAsync(pair,scan,ct); values.Add(Touched("elsewhere"));return values;
    }
}
