using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

internal static class MergeReplayChecks
{
    internal static async Task RunAsync()
    {
        foreach(var cut in new[]{"compare","local-first","cloud-first","retry","replaced","offline-rename"})
            await Run.RepairChecks.WithRepairStore((store,pair,ct)=>Replay(cut,store,pair,ct));
    }

    private static async Task Replay(string cut,IStore store,PairConfig pair,CancellationToken ct)
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var original=ItemChecks.File(Side.Local,"A",0,"P",true); var old=ItemChecks.File(Side.Dropbox,"A",0,"D",true);
        var dest=original with {Path="B",Id="I"}; var moved=old with {Path="B"};
        pc.Seed(original,[]); pc.Seed(dest,[]); db.Seed(moved,[]);
        var child=ItemChecks.File(Side.Local,"B/x",1,"x"); if(cut!="offline-rename") pc.Seed(child,[1]);
        var saved=SyncedEntries.From(original,old); await store.CommitAsync(pair.PairId,null,saved,null,null,ct);
        var left=await store.ReadRepairAsync(pair.PairId,Side.Local,ct); var right=await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct);
        await store.FinishRepairAsync(pair.PairId,(left.Revision,right.Revision),"{}",ct);
        var feed=new Change(Side.Dropbox,ChangeKind.Changed,"B",null,"D",moved,false,false);
        var removed=new Change(Side.Local,ChangeKind.Deleted,"A",null,"P",null,false,true);
        var acquired=await ItemChecks.ReadPrefixAsync(pc,ItemChecks.Journal([],1),ct);
        var cloud=await ItemChecks.ReadPrefixAsync(db,new([feed],"boundary",null),ct);
        var after=ItemChecks.Journal([removed],2); pc.AfterDelete=_=>ItemChecks.Script(pc,after);
        bool Covered((JournalPosition? Local,string? Dropbox) fence)
        {
            if(fence.Local==after.Position && acquired.Position!=after.Position)
                acquired=pc.ReadChangesAsync(null,acquired.Position,(fence.Dropbox,fence.Local),ct).GetAwaiter().GetResult();
            return fence==(acquired.ProjectedThrough,cloud.Cursor);
        }
        var queue=new WorkQueue {Unfinished=new()}; bool interrupted=false; int visits=0;
        var matching=new Scope("B","B");
        Task Compare(SyncedEntry owner,Scope before,Scope? next,CancellationToken token)
        {
            if(next==null && before==matching) visits++;
            if(!interrupted && (cut=="compare" ? next==null && before==matching : before==next && before==new Scope("A","A")))
            { interrupted=true; throw new OperationCanceledException(); }
            return ItemChecks.Hop(pair,paths,queue,pc,db,store)(owner,before,next,token);
        }
        Task Apply(Change input,CancellationToken token)=>ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,input,pc,db,store,
            ItemChecks.Population(pair,paths,pc,db,store),Compare,token,queue.Held,(_,_)=>throw new WorkQueue.Unsettled(),Covered);
        try {await queue.AdmitAsync(pair,cloud,Apply,null,ct);} catch(OperationCanceledException) when(interrupted && !ct.IsCancellationRequested) { }
        Check.Require(interrupted,"Merge did not reach the selected cut");
        queue=new WorkQueue {Unfinished=new()};
        Task Remote()=>queue.AdmitAsync(pair,cloud,Apply,null,(cursor,t)=>store.SaveCursorAsync(pair.PairId,cursor,t),null,ct);
        int localAttempts=0;
        Task Local()=>queue.AdmitAsync(pair,after,(input,t)=>{
            if(cut=="local-first" && localAttempts++==0) throw new WorkQueue.Unsettled();
            return Apply(input,t);
        },(position,t)=>store.SavePositionAsync("volume",position,t),ct);
        if(cut=="compare")
        {
            await Remote();
            Check.Require(visits==2 && db.Items.TryGetValue(child.Path,out var copy) && copy.Bytes.SequenceEqual(new byte[]{1}) &&
                await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(child.Path),ct) is {LocalId:"x"},"Replay skipped or duplicated the destination child comparison");
        }
        else
        {
            Check.Require(!pc.Items.ContainsKey("A") && await store.FindAsync(pair.PairId,Side.Local,saved.LocalKey,ct)==saved,"Cut missed the uncommitted source removal");
            if(cut is "retry" or "replaced")
            {
                var failure=new Notice(0,NoticeKind.Failure,TextKey.FailChanged,"B",new([new(Side.Dropbox,"B","D")],Side.Dropbox),DateTimeOffset.UnixEpoch,false);
                await store.SaveNoticeAsync(pair.PairId,failure,ct);
                var notice=(await Repair.CurrentRetryAsync(store,pair.PairId,failure,ct))!; int closes=0;
                var watched=FolderDeleteChecks.Observe.Wrap<IStore>(store,(name,args)=>{
                    if(name==nameof(IStore.CloseNoticeAsync) && args[1] is Notice closing && closing.Matches(notice))
                    {closes++; Check.Require(store.FindAsync(pair.PairId,Side.Local,PathNames.Key("B"),ct).GetAwaiter().GetResult() is {LocalId:"I",DropboxId:"D"},"Retry closed before binding");}
                    if(cut=="replaced" && name==nameof(IStore.SaveNoticeAsync) && args[1] is Notice {Cause:TextKey.InfoMovedDeleted}) db.Items["B"]=(moved with {Id="replacement"},[]);
                    return null;});
                await new Retries(queue.Unfinished).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,notice,pc,db,watched,queue.Held,ItemChecks.Population(pair,paths,pc,db,watched),ItemChecks.Hop(pair,paths,queue,pc,db,watched),ct,Covered);
                Check.Require(closes==(cut=="retry"?1:0) && (await store.NoticeAsync(pair.PairId,notice.Id,ct)!=null)==(cut=="replaced"),"Retry lost its reviewed member or closed the notice twice");
                if(cut=="replaced") return;
            }
            else
            {
                if(cut=="offline-rename") {await pc.MoveAsync(dest,"C",ct);pc.Seed(child with {Path="C/x"},[1]);}
                if(cut=="local-first") {await Local();await Remote();} else {await Remote();await Local();}
                Check.Require(await store.CursorAsync(pair.PairId,ct)==cloud.Cursor && await store.PositionAsync("volume",ct)==after.Position && (cut!="local-first" || localAttempts==2) && !queue.Held(new("","")).Any(),"An idle pair did not publish both prefixes");
                if(cut=="offline-rename")
                {
                    await Apply(new(Side.Local,ChangeKind.Renamed,"C","B","I",pc.Items["C"].Entry,false,false),ct);
                    Check.Require(pc.Items.ContainsKey("B") && db.Items["B"].Entry.Id==old.Id && pc.Items["C"].Entry.Id==dest.Id && pc.Items["C/x"].Bytes.SequenceEqual(new byte[]{1}) && db.Items["C/x"].Bytes.SequenceEqual(new byte[]{1}) && !db.History.Any() && pc.History.All(v=>v.Entry.Id==original.Id),"Offline rename lost the original versions or unexpectedly mirrored");return;
                }
            }
        }
        Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("B"),ct) is {LocalId:"I",DropboxId:"D",DropboxPath:"B"} && !db.History.Any() && pc.History.All(v=>v.Entry.Id=="P"),"Meeting folders failed to rebind without deleting live data");
        if(cut=="local-first")
        {
            await pc.MoveAsync(dest,"C",ct);
            await AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Local,ItemChecks.Journal([new(Side.Local,ChangeKind.Renamed,"C","B","I",pc.Items["C"].Entry,false,false)],3),pc,db,store,ct);
            Check.Require(db.Items["C"].Entry.Id=="D" && !db.Items.ContainsKey("B"),"A later rename did not move the rebound peer");
        }
    }
}
