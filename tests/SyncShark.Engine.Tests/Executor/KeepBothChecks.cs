using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Exercise label selection and the atomic settlement with two different file versions.</summary>
public static class KeepBothChecks
{
    public static async Task RunAsync()
    {
        var pair = ItemChecks.Pair();
        var settings = ItemChecks.Settings with { ComputerName = "Test-PC" };
        var local = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
        var a = ItemChecks.File(Side.Local, "report.txt", 1, "a");
        var b = ItemChecks.File(Side.Dropbox, a.Path, 2, "b");
        local.Seed(a, [1]); cloud.Seed(b, [2]);
        using var store = new InMemoryStateStore();
        var previous=SyncedEntries.From(a,b);
        await store.CommitAsync(pair.PairId,null,previous,null,null,default);
        var row = new Row(a.Path, a, b, previous, [], a.ContentHash, RowContext.Neutral);
        var outcome = await new KeepBoth().ApplyAsync(pair, settings, _ => {}, new(pair, [pair], DriveType.Fixed), row,
            local, cloud, store, (_,_,_,_) => throw new InvalidOperationException(), default);
        const string pcName="report [Dropbox version 1 Jan 00.00].txt", dbName="report [Local version Test-PC 1 Jan 00.00].txt";
        Check.Require(local.Items.Count==2 && cloud.Items.Count==2 && local.Items[a.Path].Entry==a && cloud.Items[b.Path].Entry==b && local.Items[a.Path].Bytes.SequenceEqual(new byte[]{1}) && cloud.Items[b.Path].Bytes.SequenceEqual(new byte[]{2}),"Originals changed during keep-both");
        Check.Require(local.Items.ContainsKey(pcName) && cloud.Items.ContainsKey(dbName),"Conflict label must contain the exact source name and timestamp");
        var pcLabel=local.Items[pcName].Entry; var cloudLabel=cloud.Items[dbName].Entry;
        Check.Require(pcLabel.Path==pcName && cloudLabel.Path==dbName && local.Items[pcName].Bytes.SequenceEqual(new byte[]{2}) && cloud.Items[dbName].Bytes.SequenceEqual(new byte[]{1}) && !store.Records.Values.Contains(previous),"Wrong label spelling, bytes, or stale original link");
        var links = new[]{SyncedEntries.From(a,cloudLabel),SyncedEntries.From(pcLabel,b)};
        var notice = store.OpenNotices.Single();
        var members = new HashSet<NoticeItem>{new(Side.Local,a.Path,a.Id),new(Side.Dropbox,b.Path,b.Id),new(Side.Local,pcLabel.Path,pcLabel.Id),new(Side.Dropbox,cloudLabel.Path,cloudLabel.Id)};
        Check.Require(outcome==EffectOutcome.Applied && links.ToHashSet().SetEquals(store.Records.Values) && store.Records.Count==2,"Kept endpoints linked to the wrong versions");
        Check.Require(notice.Kind==NoticeKind.Info && notice.Cause==TextKey.InfoKeptBoth && notice.Path==a.Path && notice.Subject.Items.Count==4 && members.SetEquals(notice.Subject.Items),"Kept-both notice did not name the four actual endpoints");
        foreach (var receiving in new[]{Side.Local,Side.Dropbox})
        {
            var onlyPc=new FakeSide(Side.Local); var onlyDb=new FakeSide(Side.Dropbox); using var onlyStore=new InMemoryStateStore();
            onlyPc.Seed(a,[1]);onlyDb.Seed(b,[2]); await onlyStore.CommitAsync(pair.PairId,null,previous,null,null,default);
            var legEvents=new List<RunEvent>();
            var one=await new KeepBoth().ApplyAsync(pair,settings,legEvents.Add,new(pair,[pair],DriveType.Fixed),row,onlyPc,onlyDb,onlyStore,(_,_,_,_)=>throw new InvalidOperationException(),default,receiving);
            var receivingEndpoint=receiving==Side.Local?onlyPc:onlyDb;
            var label=receivingEndpoint.Items.Values.Single(x=>x.Entry.Path!=a.Path).Entry;
            var excluded=receiving==Side.Local?onlyDb:onlyPc;
            var name=receiving==Side.Local?pcName:dbName;
            Check.Require(label.Path==name && receivingEndpoint.Items.Count==2 && excluded.Items.Count==1 && receivingEndpoint.Items[name].Bytes.SequenceEqual(new byte[]{receiving==Side.Local?(byte)2:(byte)1}),"Wrong one-sided label, extra label or bytes");
            Check.Require(onlyPc.Items[a.Path].Entry==a && onlyDb.Items[b.Path].Entry==b && onlyPc.Items[a.Path].Bytes.SequenceEqual(new byte[]{1}) && onlyDb.Items[b.Path].Bytes.SequenceEqual(new byte[]{2}) && !onlyStore.Records.Values.Contains(previous),"One-sided conflict changed an original or retained old link");
            Check.Require(excluded.Requests[nameof(ISide.DeleteAsync)]==0 && excluded.Requests[nameof(ISide.MoveAsync)]==0 && excluded.Requests[nameof(ISide.EnsureFolderAsync)]==0 && excluded.Requests[nameof(ISide.StageAsync)]==0 && excluded.Requests[nameof(ISide.PublishAsync)]==0 && excluded.Requests[nameof(ISide.CreateFolderExclusiveAsync)]==0 && excluded.Requests[nameof(ISide.ReadOneAsync)]==1 && receivingEndpoint.Requests[nameof(ISide.OpenReadAsync)]==0,"An omitted conflict leg still queried or transferred a label");
            Check.Require(legEvents.Count==1 && legEvents[0].Kind==RunEventKind.Effect && legEvents[0].Side==receiving && legEvents[0].Path==name && legEvents[0].Detail==a.Path,"Conflict emitted an extra event or attributed the omitted leg");
            var expected=receiving==Side.Local?SyncedEntries.From(label,b):SyncedEntries.From(a,label);
            var three=new HashSet<NoticeItem>{new(Side.Local,a.Path,a.Id),new(Side.Dropbox,b.Path,b.Id),new(receiving,label.Path,label.Id)};
            var oneNotice=onlyStore.OpenNotices.Single();
            Check.Require(one==EffectOutcome.Applied && onlyStore.Records.Values.Single()==expected && oneNotice.Path==a.Path && oneNotice.Kind==NoticeKind.Info && oneNotice.Cause==TextKey.InfoKeptBoth && oneNotice.Subject.Items.Count==3 && three.SetEquals(oneNotice.Subject.Items),"Single receiving side persisted the wrong link or notice members");
        }
        // Three occupied names and a fourth that fills after the probe: the fifth must be tried next, not a rescan.
        string Label(int n)=>n==1?pcName:pcName.Replace(".txt",$" ({n}).txt",StringComparison.Ordinal);
        var raced=Label(4); FakeSide target=null!;
        target=new FakeSide(Side.Local,_=>
        {
            if(target.PublishFailures.Remove(raced)) target.Seed(a with {Path=raced,Id="raced"},[1]);
            return Task.FromResult(false);
        });
        target.Seed(a,[1]); for(int i=2;i<4;i++) target.Seed(a with {Path=Label(i),Id=$"occupied-{i}"},[1]);
        var alternate=b with {Path=Label(1).ToUpperInvariant(),Id="alternate"}; target.Seed(alternate,[2]);
        var hashes=cloud.Requests[nameof(ISide.ContentHashAsync)];
        target.PublishFailures.Add(raced,new PathOccupiedException(raced));
        using var raceStore=new InMemoryStateStore(); await raceStore.CommitAsync(pair.PairId,null,previous,null,null,default);
        var racedOutcome=await new KeepBoth().ApplyAsync(pair,settings,_=>{},new(pair,[pair],DriveType.Fixed),row,target,cloud,raceStore,(_,_,_,_)=>throw new InvalidOperationException(),default,Side.Local);
        Check.Require(target.Items[alternate.Path].Entry==alternate && cloud.Requests[nameof(ISide.ContentHashAsync)]==hashes+1,"An occupied label was adopted with different casing or the source was hashed repeatedly");
        Check.Require(racedOutcome==EffectOutcome.Applied && target.Items[Label(5)].Bytes.SequenceEqual(new byte[]{2}) && target.Requests[nameof(ISide.ReadOneAsync)]==7,"A lost publication race restarted the conflict-label probe before the occupied ordinal");
    }
    public static async Task RewrittenSourceAsync()
    {
        var pair=ItemChecks.Pair(); var settings=ItemChecks.Settings;
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var source=ItemChecks.File(Side.Local,"report.txt",1,"source") with {ContentHash=string.Empty};
        var peer=ItemChecks.File(Side.Dropbox,source.Path,2,"peer");
        pc.Seed(source,[1]); db.Seed(peer,[2]);
        var row=new Row(source.Path,source,peer,null,[],null,RowContext.Neutral);
        string Label(int ordinal)=>global::Massdex.SyncShark.Engine.Planner.ConflictName.For(peer.Path,Side.Local,source.Modified,settings.ComputerName,settings,ordinal);
        var first=Label(1); var occupied=ItemChecks.File(Side.Dropbox,first,1,"occupied"); db.Seed(occupied,[1]);
        using var store=new InMemoryStateStore();
        Task<EffectOutcome> copy;
        using (await new ItemLocks().AcquireAsync(pair,new(ActionKind.KeepBoth,row,Target:first,Side:Side.Dropbox),[],default))
        {
            copy=new KeepBoth().ApplyAsync(pair,settings,_=>{},new(pair,[pair],DriveType.Fixed),row,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),default,Side.Dropbox);
            Check.Require(!copy.IsCompleted && pc.Requests[nameof(ISide.ContentHashAsync)]!=0,"The source digest was not probed before the label grant");
            pc.Items[source.Path].Bytes[0]=3;
        }
        Check.Require(await copy==EffectOutcome.Applied && db.Items[first].Entry==occupied && db.Items[first].Bytes.SequenceEqual(new byte[]{1}) &&
            db.Items.TryGetValue(Label(2),out var fresh) && fresh.Bytes.SequenceEqual(new byte[]{3}) && store.Records.Values.Single()==SyncedEntries.From(source,fresh.Entry),
            "The edited source must link to its new Dropbox copy, leaving the occupied label unchanged");
    }

    public static Task CollisionRetryAsync() => CollisionRetryAsync(false);
    public static Task BoundCollisionRetryAsync() => CollisionRetryAsync(true);

    private static async Task CollisionRetryAsync(bool bound)
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); using var store=new InMemoryStateStore();
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var selected=ItemChecks.File(Side.Local,"old",1,"selected"); var occupant=ItemChecks.File(Side.Local,"new",2,"occupant"); var peer=ItemChecks.File(Side.Dropbox,occupant.Path,1,"peer");
        pc.Seed(selected,[1]); pc.Seed(occupant,[2]); db.Seed(peer,[1]); var saved=SyncedEntries.From(selected,peer with {Path=selected.Path}); await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        pc.Pages.Enqueue(new([],null,new(1,2))); db.Pages.Enqueue(new([],"after",null));
        Task<EffectOutcome> Attempt()=>Collisions.CollisionAsync(pair,ItemChecks.Settings,_=>{},paths,Side.Local,selected,occupant,peer,saved,pc,db,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,f=>f.Local==new JournalPosition(1,2) && f.Dropbox=="after");
        var interrupted=new ChangedUnderneathException(selected.Path); pc.RecycleRefusals[selected.Path]=interrupted;
        try { await Attempt(); } catch(ChangedUnderneathException e) when(ReferenceEquals(e,interrupted)) { }
        var firstPc=pc.Items.Values.Single(i=>i.Entry!=selected && i.Entry!=occupant); var firstDb=db.Items.Values.Single(i=>i.Entry!=peer);
        Check.Require(pc.Items.ContainsKey(selected.Path) && store.Records.Values.Single()==saved && store.OpenNotices.Count==0,"First attempt did not stop between publication and settlement");
        if(bound)
        {
            var pcPartner=peer with {Path=firstPc.Entry.Path,Id="pc-partner"}; db.Seed(pcPartner,firstPc.Bytes);
            var dbPartner=occupant with {Path=firstDb.Entry.Path,Id="db-partner"}; pc.Seed(dbPartner,firstDb.Bytes);
            var pcOwner=SyncedEntries.From(firstPc.Entry,pcPartner); var dbOwner=SyncedEntries.From(dbPartner,firstDb.Entry);
            await store.CommitAsync(pair.PairId,null,pcOwner,null,null,default); await store.CommitAsync(pair.PairId,null,dbOwner,null,null,default);
            pc.RecycleRefusals.Clear(); var retry=await Attempt();
            var nextPc=pc.Items.Values.Single(i=>i.Entry!=occupant && i.Entry!=firstPc.Entry && i.Entry!=dbPartner); var nextDb=db.Items.Values.Single(i=>i.Entry!=peer && i.Entry!=firstDb.Entry && i.Entry!=pcPartner);
            Check.Require(retry==EffectOutcome.Applied && pc.Items[firstPc.Entry.Path]==firstPc && db.Items[firstDb.Entry.Path]==firstDb && pc.Items[dbPartner.Path].Entry==dbPartner && db.Items[pcPartner.Path].Entry==pcPartner &&
                store.Records.Values.ToHashSet().SetEquals([pcOwner,dbOwner,SyncedEntries.From(nextPc.Entry,peer),SyncedEntries.From(occupant,nextDb.Entry)]) && store.OpenNotices.Single().Cause==TextKey.InfoKeptBoth,"Retry adopted or changed independently bound copies");
            return;
        }
        pc.RecycleRefusals.Clear(); var result=await Attempt();
        Check.Require(result==EffectOutcome.Applied && pc.Items.Count==2 && db.Items.Count==2 && pc.Items[firstPc.Entry.Path].Entry==firstPc.Entry && db.Items[firstDb.Entry.Path].Entry==firstDb.Entry &&
            store.Records.Values.ToHashSet().SetEquals([SyncedEntries.From(firstPc.Entry,peer),SyncedEntries.From(occupant,firstDb.Entry)]) && store.OpenNotices.Single().Cause==TextKey.InfoKeptBoth,"Retry did not link the two earlier copies exactly once");
    }

    /// <summary>A peer holds the actual selected physical label; publication and link settlement must wait for release.</summary>
    public static async Task LabelGrantAsync()
    {
        var pair=ItemChecks.Pair(); var settings=ItemChecks.Settings with {ComputerName="Test-PC"};
        var a=ItemChecks.File(Side.Local,"report.txt",1,"original-pc"); var b=ItemChecks.File(Side.Dropbox,a.Path,2,"original-cloud");
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);pc.Seed(a,[1]);db.Seed(b,[2]);
        var row=new Row(a.Path,a,b,null,[],a.ContentHash,RowContext.Neutral); using var store=new InMemoryStateStore();
        var selected=global::Massdex.SyncShark.Engine.Planner.ConflictName.For(a.Path,Side.Dropbox,b.Modified,settings.ComputerName,settings,1);
        var peerEntry=a with {Path=selected};var otherRow=new Row(selected,peerEntry,null,null,[],null,RowContext.Neutral);
        var entered=new TaskCompletionSource(); var release=new TaskCompletionSource();
        var hold=new ItemLocks().RunAsync(pair,new(ActionKind.Record,otherRow),[],async ()=>{entered.SetResult();await release.Task;return true;},default);
        await Task.WhenAny(hold,entered.Task); if (hold.IsCompleted) await hold;
        await entered.Task;
        var copy=new KeepBoth().ApplyAsync(pair,settings,_=>{},new(pair,[pair],DriveType.Fixed),row,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),default,Side.Local);
        try { Check.Require(!copy.IsCompleted && pc.Requests[nameof(ISide.StageAsync)]==0 && !pc.Items.ContainsKey(selected) && store.Records.Count==0,"Conflict label published or settled while another physical grant held its selected path"); }
        finally { release.SetResult();await hold; }
        Check.Require(await copy==EffectOutcome.Applied && pc.Items[selected].Bytes.SequenceEqual(new byte[]{2}) && store.Records.Count==1,"Released conflict-label grant did not finish exactly one receiving leg");
    }

}
