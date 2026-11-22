using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class RepairChecks
{
    public static async Task RunAsync()
    {
        await InventoryPruningAsync();
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
    public static async Task InventoryPruningAsync()
    {
        foreach(bool history in new[]{false,true})
        {
            bool read=false; string? terminal=null;
            var side=new FakeSide(history?Side.Dropbox:Side.Local);
            side.Seed(ItemChecks.File(side.Side,"visible",1,"included"),[1]);
            IAsyncEnumerable<Listed> Read(string root,ListOptions options,CancellationToken ct)
            {
                Check.Require(!options.IncludeOutOfScope,"Ordinary Repair requested traversal into excluded folders");
                Check.Require(options.Recursive && options.IncludeDeleted==history,"Wrong Repair listing mode");
                read=true; return side.ReadAllAsync(root,options,ct);
            }
            var seen=new List<string>();
            await foreach(var item in Repair.ReadInventory(Read,"",_=>{},history?value=>terminal=value:null,default)) seen.Add(item.Path);
            Check.Require(read && seen.SequenceEqual(new[]{"visible"}) && terminal==(history?"terminal":null),"Repair skipped its included sibling or lost its history cursor");
        }
    }

    private static async Task CoveredHopAsync()
    {
        var pair=ItemChecks.Pair(); pair=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"))};
        using var store=new InMemoryStateStore(); await store.SavePairAsync(pair,default);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        pc.Pages.Enqueue(new([],null,new(7,31))); db.Pages.Enqueue(new([],"opaque",null));
        var queue=new WorkQueue();
        await queue.AdmitAsync(pair,new([],null,new(7,32)),(_,_)=>Task.CompletedTask,(position,ct)=>store.SavePositionAsync("1",position,ct),null,null,default);
        await queue.AdmitAsync(pair,new([],"opaque",null),(_,_)=>Task.CompletedTask,null,(cursor,ct)=>store.SaveCursorAsync(pair.PairId,cursor,ct),null,default);
        var before=new Scope("old","old"); var after=new Scope("new","new");
        var binding=SyncedEntries.From(ItemChecks.File(Side.Local,"old",0,"pc",true),ItemChecks.File(Side.Dropbox,"old",0,"db",true));
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),queue,pc,db,store,before,(binding,before,after),default);
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]==0 && db.Requests[nameof(ISide.ReadAllAsync)]==0,"Already settled physical hop listed again");
        Check.Require(!(await store.RepairStateAsync(pair.PairId,default)).FirstRepairDone,"Physical hop published a full Repair");
        db.Pages.Enqueue(new([],"later",null));
        var occupant=ItemChecks.File(Side.Local,"old/item",9,"new-occupant"); pc.Seed(occupant,[9]);
        var child=ItemChecks.File(Side.Local,"old/item",1,"carried"); var prior=ItemChecks.File(Side.Dropbox,"old/item",1,"remote-child");
        var priorBinding=SyncedEntries.From(child,prior); await store.CommitAsync(pair.PairId,null,priorBinding,null,null,default);
        pc.Seed(child with {Path="new/item"},[1]);
        try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},new PathScope(pair,[pair],DriveType.Fixed),queue,pc,db,store,before,(binding,before,after),default); throw new InvalidDataException("Changed mapped row was not held for retry"); }
        catch(WorkQueue.Unsettled) { }
        Check.Require(pc.Items[occupant.Path].Entry==occupant && pc.Items[occupant.Path].Bytes.SequenceEqual(new byte[]{9}),"Physical difference was applied to a reused old path");
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]!=0 && db.Requests[nameof(ISide.ReadAllAsync)]!=0,"Unread opaque cursor treated as covered");
    }
}
