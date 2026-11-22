using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class RepairChecks
{
    public static async Task RunAsync()
    {
        await CoveredHopAsync();
        var pair=ItemChecks.Pair(); var local=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,default);
        await store.OweRepairAsync(pair.PairId,Side.Local,"requested",default);
        var paths=new PathScope(pair,[pair],DriveType.Fixed); var queue=new WorkQueue();
        db.ReadFailures[""]=new IOException("listing interrupted");
        try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,db,store,null,null,default); throw new InvalidDataException("Unread scan accepted"); }
        catch(IOException e) when (e.Message=="listing interrupted") { }
        Check.Require(!(await store.RepairStateAsync(pair.PairId,default)).FirstRepairDone && (await store.ReadRepairAsync(pair.PairId,Side.Local,default)).Reason=="requested","Incomplete scan consumed the obligation");
        db.ReadFailures.Clear(); await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,db,store,null,null,default);
        Check.Require((await store.RepairStateAsync(pair.PairId,default)).FirstRepairDone && (await store.ReadRepairAsync(pair.PairId,Side.Local,default)).Reason==null,"Complete scan did not publish");
    }
    private static async Task CoveredHopAsync()
    {
        var pair=ItemChecks.Pair(); pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
        using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,default);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        pc.Pages.Enqueue(new([],null,new(7,31))); db.Pages.Enqueue(new([],"opaque",null));
        await store.SavePositionAsync("1",new(7,32),default); await store.SaveCursorAsync(pair.PairId,"opaque",default);
        var before=new Scope("old","old"); var after=new Scope("new","new");
        var binding=SyncedEntries.From(ItemChecks.File(Side.Local,"old",0,"pc",true),ItemChecks.File(Side.Dropbox,"old",0,"db",true));
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),new WorkQueue(),pc,db,store,before,(binding,before,after),default);
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]==0 && db.Requests[nameof(ISide.ReadAllAsync)]==0,"Already settled physical hop listed again");
        Check.Require(!(await store.RepairStateAsync(pair.PairId,default)).FirstRepairDone,"Physical hop published a full Repair");
    }
}
