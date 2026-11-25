using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using ProcessMonitor = Massdex.SyncShark.Engine.Run.Monitor;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class MonitorChecks
{
    public static async Task RunAsync()
    {
        using var store=new InMemoryStateStore(); var pair=ItemChecks.Pair();
        var roots=new RootIdentity(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("a","r","h","d"));
        pair=pair with {Roots=roots}; var paused=pair with {PairId="paused",Enabled=false};
        var unrelated=pair with {PairId="other",Roots=roots with {Local=new(2,1,null)}};
        foreach(var member in new[]{pair,paused,unrelated}) await store.SavePairAsync(member,default);
        var local=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        await store.SaveCursorAsync(pair.PairId,"before",default);
        db.Pages.Enqueue(new([],"after",null)); await ProcessMonitor.ObserveAsync(pair,db,store,default);
        Check.Require(await store.CursorAsync(pair.PairId,default)=="before" && (await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Reason==null,"Ordinary page treated as source reset");
        db.Pages.Enqueue(new([],null,null){ContinuityLost=true});
        try { await ProcessMonitor.ObserveAsync(pair,db,store,default); throw new InvalidDataException("Unverified new cursor accepted"); }
        catch(InventoryIncompleteException) { }
        Check.Require(await store.CursorAsync(pair.PairId,default)=="before" && (await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Reason==null,"Unverified reset changed the saved boundary");
        await store.ResetJournalAsync("1",new(2,10),"gap",default);
        var debt=await store.ReadRepairAsync(pair.PairId,Side.Local,default);
        await store.SavePositionAsync("1",new(2,12),default);
        local.Pages.Enqueue(new([],null,new(2,10)){ContinuityLost=true}); await ProcessMonitor.ObserveAsync(pair,local,store,default);
        Check.Require(await store.PositionAsync("1",default)==new JournalPosition(2,12) && await store.ReadRepairAsync(pair.PairId,Side.Local,default)==debt,"Delayed Local reset rewound the shared boundary or recorded duplicate debt");
        foreach(var member in new[]{pair,paused,unrelated}) Check.Require(((await store.ReadRepairAsync(member.PairId,Side.Local,default)).Reason!=null)==(member!=unrelated),"Wrong reset membership");
        db.Pages.Enqueue(new([],"fresh",null){ContinuityLost=true}); await ProcessMonitor.ObserveAsync(pair,db,store,default);
        Check.Require(await store.CursorAsync(pair.PairId,default)=="fresh" && (await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Reason!=null,"Reset omitted cursor or debt");
    }
}
