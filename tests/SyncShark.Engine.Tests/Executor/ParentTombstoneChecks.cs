using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

internal static class ParentTombstoneChecks
{
    internal static async Task RunAsync()
    {
        foreach(var mode in new[]{"missing","queued","unreadable","retry","recreated","bound","child-fault","unchanged","edited"})
            await Run.AutoSyncChecks.WithinDeadline(ct=>Verify(mode,ct));
    }

    private static async Task Verify(string mode,CancellationToken ct)
    {
        var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed); using var store=new InMemoryStateStore();
        await store.SavePairAsync(pair,ct); var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        var left=ItemChecks.File(Side.Local,"U/c",1,"L");var right=ItemChecks.File(Side.Dropbox,left.Path,1,"D");
        var child=SyncedEntries.From(left,right);await store.CommitAsync(pair.PairId,null,child,null,null,ct);
        var parent=new Change(Side.Dropbox,ChangeKind.Deleted,"U",null,null,null,false,false);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store); var path=mode=="bound"?"unrelated":"U";
        if(mode is "unreadable" or "unchanged" or "edited" or "child-fault")
        {
            pc.Seed(ItemChecks.File(Side.Local,"U",0,"folder",true),[]);
            byte value=mode=="edited"?(byte)2:(byte)1;pc.Seed(ItemChecks.File(Side.Local,left.Path,value,left.Id),[value]);
        }
        if(mode=="unreadable") db.Seed(ItemChecks.File(Side.Dropbox,"U",0,"skipped",true) with {Skip=nameof(TextKey.SkipUnreadable)},[]);
        if(mode is "bound" or "recreated")
        {
            var a=ItemChecks.File(Side.Local,path,2,"new-local");var b=ItemChecks.File(Side.Dropbox,path,2,"new-cloud");pc.Seed(a,[2]);db.Seed(b,[2]);
            if(mode=="bound") await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,ct);
        }
        var retry=mode is "bound" or "retry" or "recreated" or "child-fault";
        if(retry) await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,path,new([],Side.Dropbox),DateTimeOffset.UnixEpoch,false),ct);
        var original=store.OpenNotices.SingleOrDefault(); bool closedEarly=false;int failures=0;
        var watched=FolderDeleteChecks.Observe.Wrap<IStore>(store,(name,args)=>{
            if(original!=null && name==nameof(IStore.CloseNoticeAsync) && args[1] is Notice n && n.Id==original.Id && mode!="bound")
                closedEarly|=store.Records.Values.Contains(child) || mode=="recreated" && !store.Records.Values.Any(b=>b.DropboxId=="new-cloud");
            return null;});
        var local=FolderDeleteChecks.Observe.On(pc,(op,args)=>{
            if(mode=="missing" && op==nameof(ISide.ReadAllAsync) && Equals(args[0],"U")) throw new InvalidDataException("Listed an absent root");
            if(mode=="child-fault" && op==nameof(ISide.ReadOneAsync) && Equals(args[0],left.Path)) {failures++;throw new WorkQueue.Unsettled();}
            return null;});
        var pop=ItemChecks.Population(pair,paths,local,db,watched);var hop=ItemChecks.Hop(pair,paths,queue,local,db,watched);
        if(retry)
        {
            await queue.Unfinished.CheckAsync(pair,ItemChecks.Settings,_=>{},paths,parent,local,db,watched,queue.Held,pop,hop,ct);
            async Task Retry()=>await new Retries(queue.Unfinished).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,original!,local,db,watched,queue.Held,pop,hop,ct);
            if(mode=="child-fault")
            {
                for(int i=0;i<2;i++)
                {
                    try {await Retry();throw new InvalidDataException("Unsettled child acknowledged its parent");} catch(WorkQueue.Unsettled) { }
                    Check.Require(!closedEarly && (await store.NoticeAsync(pair.PairId,original!.Id,ct))?.Matches(original!)==true,"Late child failure closed the parent notice");
                }
                Check.Require(failures==2,"The second Retry did not revisit the child");return;
            }
            await Retry();Check.Require(!closedEarly && store.OpenNotices.All(n=>n.Id!=original!.Id),"Parent notice did not close after its scope settled");
            if(mode=="bound") {Check.Require(store.Records.Values.Contains(child),"Bound Retry disturbed another scope");return;}
            if(mode=="recreated") Check.Require(store.Records.Values.Single() is {LocalId:"new-local",DropboxId:"new-cloud"},"Recreated files were not linked");
        }
        else if(mode=="unchanged")
        {
            ItemChecks.Script(pc,ItemChecks.Journal([],1));ItemChecks.Script(db,new([parent],"complete",null));
            await ItemChecks.StartQueueAsync(pair,paths,local,db,watched);
        }
        else if(mode=="queued")
        {
            await AutoSync.ApplyPageAsync(pair,ItemChecks.Settings,_=>{},paths,queue,Side.Dropbox,new([parent,parent with {Path=left.Path}],"queued",null),pc,db,store,ct);
            Check.Require(await store.CursorAsync(pair.PairId,ct)=="queued","Parent and child tombstones held each other");
        }
        else
        {
            bool retained=false;
            try {await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,parent,local,db,watched,pop,hop,ct,queue.Held,(_,_)=>throw new WorkQueue.Unsettled());}
            catch(WorkQueue.Unsettled) when(mode=="unreadable") {retained=true;}
            if(mode=="unreadable") {Check.Require(retained && store.Records.Values.Contains(child) && pc.Items.ContainsKey(left.Path) && pc.Mutations.Count==0 && db.Mutations.Count==0,"Unreadable root supplied deletion evidence");return;}
        }
        if(mode=="edited") Check.Require(db.Items.TryGetValue(left.Path,out var copy) && copy.Bytes.SequenceEqual(new byte[]{2}) && pc.Items[left.Path].Bytes.SequenceEqual(copy.Bytes),"Child edit lost to an ancestor tombstone");
        else Check.Require(!store.Records.Values.Contains(child) && !pc.Items.ContainsKey(left.Path),"A stale child survived the parent decision");
        if(mode is "missing" or "retry" or "queued") Check.Require(store.Records.Count==0 && db.Mutations.Count==0 && pc.Mutations.Count==0,"Missing roots caused a mutation");
        if(mode=="unchanged") Check.Require(pc.History.Any(v=>v.Entry.Id==left.Id),"Unchanged surviving child was not recycled");
    }
}
