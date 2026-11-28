using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Obs=Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.Observation;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Repair-line cases from the frozen 0.3.105 designs that the writers left open (test completion pass).</summary>
public static class RepairCompletionChecks
{
    // ---------------------------------------------------------------------------------------------------------------------------
    // L6 R7 M1.2: "these two population paths never take an effect whose source or destination endpoint is at or under a folder this
    // Repair could not list." Cases (b) PopulateCovered, (c) run.Populate in place with a succeeding fresh target listing, (d) both in
    // the other direction, (f) sibling-name controls; each asserts no effect/binding under F, F's own notice, its owed scope, and that a
    // later Repair that reads F completes the population.
    public static async Task UnreadSubtreePopulationsAsync()
    {
        foreach(var source in new[]{Side.Local,Side.Dropbox}) foreach(var inPlace in new[]{false,true}) await UnreadPopulationAsync(source,inPlace);
    }

    private static Task UnreadPopulationAsync(Side source,bool inPlace) => RepairChecks.WithRepairStore(async (store,pair,ct)=>
    {
        await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct); // after first sync: P is an ordinary covered folder, not a first-run deletion candidate
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var from=source==Side.Local?pc:db; var to=source==Side.Local?db:pc; var label=$"source={source} inPlace={inPlace}"; var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct);
        from.Seed(ItemChecks.File(source,"P",0,"p",true),[]); from.Seed(ItemChecks.File(source,"P/F",0,"f",true),[]);
        var under=ItemChecks.File(source,"P/F/c",1,"c"); from.Seed(under,[1]);
        string[] siblings=["P/F2","P/F x","P/F.txt"]; foreach(var s in siblings) from.Seed(ItemChecks.File(source,s,2,"s-"+s),[2]);
        from.ReadFailures["P/F"]=new ItemFailedException("P/F",source==Side.Local?TextKey.ReadLocalFailed:TextKey.ReadDropboxFailed,null);
        // F's listing fails partway: the child read before the failure is listed, then F itself is reported unreadable.
        async IAsyncEnumerable<Listed> Partway(IAsyncEnumerable<Listed> items)
        {
            await foreach(var item in items) { yield return item; if(PathNames.Same(item.Path,"P/F") && from.ReadFailures.ContainsKey("P/F")) yield return new(under.Path,under); }
        }
        int ensures=0, targetListings=0, renames=0; var events=new List<RunEvent>();
        var sourceSide=Obs.Wrap<ISide>((m,args)=>m.Name==nameof(ISide.ReadAllAsync) ? Partway((IAsyncEnumerable<Listed>)Obs.Forward(from,m,args)!) : Obs.Forward(from,m,args));
        var targetSide=Obs.Wrap<ISide>((m,args)=>
        {
            if(m.Name==nameof(ISide.ReadAllAsync) && (string?)args[0]=="P") Interlocked.Increment(ref targetListings);
            if(m.Name is nameof(ISide.EnsureFolderAsync) or nameof(ISide.CreateFolderExclusiveAsync) && (string?)args[0]=="P" && Interlocked.Increment(ref ensures)==1 && inPlace)
                throw new ItemFailedException("P",TextKey.FailLocalInUse,null); // the first turn fails, so the in-place turn populates through run.Populate
            return Obs.Forward(to,m,args);
        });
        var counted=Obs.Wrap<IStore>((m,args)=>{ if(m.Name==nameof(IStore.RenamesAsync)) Interlocked.Increment(ref renames); return Obs.Forward(store,m,args); });
        ISide L()=>source==Side.Local?sourceSide:targetSide; ISide D()=>source==Side.Local?targetSide:sourceSide;
        await Repair.RunAsync(pair,ItemChecks.Settings,e=>{lock(events) events.Add(e);},paths,queue,L(),D(),counted,null,null,ct,caughtUp:true);
        bool AtOrUnderF(string? path)=>path!=null && PathNames.IsSelfOrUnder(path,"P/F");
        Check.Require(to.Items.ContainsKey("P") && !to.Items.Keys.Any(AtOrUnderF),$"{label}: a population wrote at or under the unread folder: [{string.Join(",",to.Items.Keys)}]");
        foreach(var s in siblings) Check.Require(to.Items.TryGetValue(s,out var copied) && copied.Bytes.SequenceEqual(new byte[]{2}),$"{label}: the sibling {s} beside the unread folder did not populate");
        lock(events) Check.Require(!events.Any(e=>e.Kind==RunEventKind.Effect && AtOrUnderF(e.Path)),$"{label}: an effect was logged at or under the unread folder");
        Check.Require(await store.FindAsync(pair.PairId,source,PathNames.KeyFor(source,"P/F/c"),ct)==null && await store.FindAsync(pair.PairId,source,PathNames.KeyFor(source,"P/F"),ct)==null,$"{label}: a binding was recorded at or under the unread folder");
        var open=new List<Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(AtOrUnderF(n.Path)) open.Add(n);
        Check.Require(open.Count==1 && open[0].Path=="P/F",$"{label}: the unread folder did not keep exactly its own notice: {string.Join(",",open.Select(n=>n.Cause+"@"+n.Path))}");
        Check.Require((await store.PairAsync(pair.PairId,ct))?.OwedScopes?.Contains(new Scope("P/F","P/F"))==true,$"{label}: the unread folder is not owed to a later Repair");
        Check.Require(renames==1,$"{label}: the run read its rename groups {renames} times");
        if(inPlace) Check.Require(ensures>=2 && targetListings>0,$"{label}: the in-place population did not run with a fresh target listing (creates {ensures}, listings {targetListings})");
        // The later Repair that reads F completes the population.
        from.ReadFailures.Remove("P/F");
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct,caughtUp:true);
        Check.Require(to.Items.TryGetValue("P/F/c",out var late) && late.Bytes.SequenceEqual(new byte[]{1}),$"{label}: the Repair that read the folder did not complete its population");
    });

    // ---------------------------------------------------------------------------------------------------------------------------
    // L2 MEMBERS PendingAtCloseSeams, retirement arm (CloseSelected): "(a) open job over the footprint registered before
    // the close -> open; (c), (c2') -> open; (e) -> closed. Mutant: guard evaluated before the gate -> red."
    public static async Task PendingAtCloseSeamsAsync()
    {
        foreach(var arm in new[]{"none","job before","job at close","held before","held at close"}) await RetirementSeamAsync(arm);
    }

    private static Task RetirementSeamAsync(string arm) => RepairChecks.WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed); await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct); // CloseSelected on a listed, bilaterally absent row
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,"gone",new([new(Side.Local,"gone","previous")],Side.Local),DateTimeOffset.UnixEpoch,false),ct);
        var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct); var label=$"arm={arm}"; int closes=0;
        var job=new AcceptedJob(pair.PairId,"files/delete_batch/check","open",[new("accepted","gone",null)]);
        using var pageStop=CancellationTokenSource.CreateLinkedTokenSource(ct); Task? page=null;
        db.JobReplies[job.JobId]=new(Enumerable.Range(0,8).Select(_=>(JobReply)new JobReply.Running())); // the job stays open throughout
        async Task PendingAsync()
        {
            if(arm.StartsWith("job",StringComparison.Ordinal)) await Task.Run(()=>queue.Unfinished!.InstalledAsync(job,queue.Held,_=>Task.CompletedTask),ct);
            else
            {
                // The page stays unsettled while its input is held; wait (bounded by ct) until the queue holds it, not for the page.
                page=queue.AdmitAsync(pair,new ChangePage([new Change(Side.Local,ChangeKind.Changed,"gone",null,"later",null,false,false)],null,null),(_,_)=>throw new WorkQueue.Unsettled(),null,pageStop.Token);
                while(!queue.Held(new("gone","gone")).Any()) { if(page.IsCompleted) await page; ct.ThrowIfCancellationRequested(); await Task.Yield(); }
            }
        }
        if(arm.EndsWith("before",StringComparison.Ordinal)) await PendingAsync();
        // At close: the pending owner appears after the pre-check and before the close transaction's own guard.
        var observed=Obs.Wrap<IStore>((m,args)=>m.Name==nameof(IStore.CloseNoticeAsync) ? CloseAsync(m,args) : Obs.Forward(store,m,args));
        async Task<bool> CloseAsync(System.Reflection.MethodInfo m,object?[] args)
        {
            Interlocked.Increment(ref closes);
            if(arm.EndsWith("at close",StringComparison.Ordinal)) await PendingAsync();
            return await (Task<bool>)Obs.Forward(store,m,args)!;
        }
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,observed,null,null,ct,caughtUp:true);
        var open=false; await foreach(var n in store.NoticesAsync(pair.PairId,ct)) open|=n.Path=="gone";
        Check.Require(pc.Requests[nameof(ISide.ReadAllAsync)]==1,$"{label}: the Repair did not take the listed path");
        Check.Require(open==(arm!="none"),arm=="none" ? $"{label}: a bilaterally absent notice with no pending owner was not retired" : $"{label}: the retirement closed a notice whose footprint has a pending owner");
        if(arm.EndsWith("at close",StringComparison.Ordinal)) Check.Require(closes>0,$"{label}: the close transaction was never reached, so the in-transaction guard is unproved");
        if(page!=null)
        {
            Check.Require(!page.IsCompleted && queue.Held(new("gone","gone")).Any(),$"{label}: the Repair consumed or dropped the other owner's held input");
            pageStop.Cancel(); try { await page; } catch(OperationCanceledException) when(pageStop.IsCancellationRequested) { }
        }
    });

    // ---------------------------------------------------------------------------------------------------------------------------
    // L2 R11 H WholeTreeDescendantGap (MEMBERS KEEP): "whole-tree acknowledged delete of "f" with an open exact Failure on "f/c" -> open
    // after the run (binding retired); pins today's behaviour until section L item 7 is fixed. No close expectation for any descendant."
    // Deletion safety: the removed descendant's version is in Dropbox's deleted files, never nowhere.
    public static Task WholeTreeDescendantGapAsync() => RepairChecks.WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        foreach(var (path,folder) in new[]{("f",true),("f/c",false)})
        {
            var remote=ItemChecks.File(Side.Dropbox,path,folder?(byte)0:(byte)1,"db-"+path,folder); db.Seed(remote,folder?[]:[1]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(ItemChecks.File(Side.Local,path,folder?(byte)0:(byte)1,"pc-"+path,folder),remote),null,null,ct);
        }
        // The PC folder and its child are gone; the child's own exact Failure is still open.
        var exact=(await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,"f/c",new([new(Side.Dropbox,"f/c","db-f/c")],Side.Dropbox),DateTimeOffset.UnixEpoch,false),ct))!;
        var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct); FolderChecks.AdmitFullRepair(queue,pc,db);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(!db.Items.ContainsKey("f") && !db.Items.ContainsKey("f/c"),"The whole-tree removal of the PC-deleted folder did not reach Dropbox");
        Check.Require(db.History.Any(h=>h.Entry.Id=="db-f/c" && h.Bytes.SequenceEqual(new byte[]{1})) && db.History.Any(h=>h.Entry.Id=="db-f"),"The removed descendant's version is not in Dropbox's deleted files");
        Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("f"),ct)==null,"The removed folder's binding was not retired");
        Notice? still=null; await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(n.Id==exact.Id) still=n;
        // Amended pin (coordinator 2026-11-27, as at bdb8c6f85): the removal's cleanup hop retires f/c as absent on both sides and closes its notice.
        Check.Require(still is not {Dismissed:false},"The removed descendant's exact Failure stayed open after the cleanup hop retired it");
    });

    // ---------------------------------------------------------------------------------------------------------------------------
    // L2 MEMBERS RepairClosesSettledTable (AMEND): "an earlier run's Failure on each of these rows [Record equal bytes, Upload Applied,
    // Download, overwrite upload] stays open after the commit, as at bdb8c6f85", and the KEEP stays-open rows (InSync, dismissed/ignored,
    // member of another row, memberless). Each row's effect still lands; no commit closes another run's notice.
    public static Task RepairClosesSettledTableAsync() => RepairChecks.WithRepairStore(async (store,pair,ct)=>
    {
        await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        async Task BindAsync(string path,byte value) { var l=ItemChecks.File(Side.Local,path,value,"pc-"+path); var d=ItemChecks.File(Side.Dropbox,path,value,"db-"+path); pc.Seed(l,[value]); db.Seed(d,[value]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(l,d),null,null,ct); }
        pc.Seed(ItemChecks.File(Side.Local,"up",1,"pc-up"),[1]);                                   // Upload
        db.Seed(ItemChecks.File(Side.Dropbox,"down",1,"db-down"),[1]);                             // Download
        await BindAsync("over",1); pc.Items["over"]=(ItemChecks.File(Side.Local,"over",2,"pc-over"),[2]); // overwrite upload
        pc.Seed(ItemChecks.File(Side.Local,"rec",3,"pc-rec"),[3]); db.Seed(ItemChecks.File(Side.Dropbox,"rec",3,"db-rec"),[3]); // Record equal bytes
        await BindAsync("insync",4); await BindAsync("ignored",5); await BindAsync("member",6); await BindAsync("memberless",7);
        var earlier=new List<Notice>();
        async Task RaiseAsync(string path,NoticeItem[] members,bool ignore=false)
        {
            var saved=(await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,path,new(members,members.Length==0?Side.Local:members[0].Side),DateTimeOffset.UnixEpoch,false),ct))!;
            if(ignore) { Check.Require(await store.UpdateNoticeVisibilityAsync(pair.PairId,saved,true,ct),"Fixture could not ignore its notice"); saved=(await store.NoticeAsync(pair.PairId,saved.Id,ct))!; }
            earlier.Add(saved);
        }
        await RaiseAsync("up",[new(Side.Local,"up","pc-up")]); await RaiseAsync("down",[new(Side.Dropbox,"down","db-down")]);
        await RaiseAsync("over",[new(Side.Local,"over","pc-over")]); await RaiseAsync("rec",[new(Side.Local,"rec","pc-rec")]);
        await RaiseAsync("insync",[new(Side.Local,"insync","pc-insync")]); await RaiseAsync("ignored",[new(Side.Local,"ignored","pc-ignored")],ignore:true);
        await RaiseAsync("elsewhere",[new(Side.Local,"member","pc-member")]); await RaiseAsync("memberless",[]);
        var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct,caughtUp:true);
        Check.Require(db.Items.TryGetValue("up",out var up) && up.Bytes.SequenceEqual(new byte[]{1}) && pc.Items.TryGetValue("down",out var down) && down.Bytes.SequenceEqual(new byte[]{1}) &&
            db.Items["over"].Bytes.SequenceEqual(new byte[]{2}) && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("rec"),ct) is {DropboxId:"db-rec"},"A table row's own effect did not land");
        var now=new Dictionary<long,Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) now[n.Id]=n;
        foreach(var n in earlier) Check.Require(now.TryGetValue(n.Id,out var kept) && !kept.Dismissed && kept.At==n.At && kept.Ignored==n.Ignored,$"An earlier run's Failure at {n.Path} was closed or rewritten by this Repair's commit");
    });

    // ---------------------------------------------------------------------------------------------------------------------------
    // L14b R7 Part 1 (g): "full Repair with forced scan, standalone both-absent bound folder: matching -> retired, one RootAsync; replaced ->
    // no CommitAsync, binding kept; a both-absent FILE binding: no RootAsync." (The RootDropboxFolderChanged notice is the host's, HostChecks.)
    public static async Task BothAbsentFolderProvesRootAsync()
    {
        var baseline=await BothAbsentAsync(null,false); var file=await BothAbsentAsync(false,false); var folder=await BothAbsentAsync(true,false);
        Check.Require(file==baseline && folder==baseline+1,$"Root proofs: none {baseline}, both-absent file {file}, both-absent folder {folder} (expected the folder alone to add one)");
        await BothAbsentAsync(true,true);
    }

    private static async Task<int> BothAbsentAsync(bool? folder,bool replace)
    {
        int proofs=0;
        await RepairChecks.WithRepairStore(async (store,pair,ct)=>
        {
            await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct); await store.OweRepairAsync(pair.PairId,Side.Dropbox,"forced",ct);
            var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
            var binding=folder is {} isFolder ? SyncedEntries.From(ItemChecks.File(Side.Local,"gone",1,"pc-gone",isFolder),ItemChecks.File(Side.Dropbox,"gone",1,"db-gone",isFolder)) : null;
            if(binding!=null) await store.CommitAsync(pair.PairId,null,binding,null,null,ct);
            var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct); int entryProofs=0, commits=0;
            // Entry and post-listing root checks see the saved root; with `replace`, every later proof sees a replaced Dropbox folder.
            var remote=Obs.Wrap<ISide>((m,args)=>
            {
                if(m.Name!=nameof(ISide.RootAsync)) return Obs.Forward(db,m,args);
                Interlocked.Increment(ref proofs);
                return replace && Interlocked.Increment(ref entryProofs)>2 ? Task.FromResult(((DirectoryIdentity?)null,(DropboxRootIdentity?)new("a","r","h","replaced"),(DriveEvidence?)null,(DriveType?)null)) : Obs.Forward(db,m,args);
            });
            var counted=Obs.Wrap<IStore>((m,args)=>{ if(m.Name==nameof(IStore.CommitAsync)) Interlocked.Increment(ref commits); return Obs.Forward(store,m,args); });
            try { await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,remote,counted,null,null,ct,caughtUp:true); Check.Require(!replace,"A both-absent folder retired under a replaced Dropbox root"); }
            catch(ChangedUnderneathException e) when(replace && PathNames.Same(e.Path,pair.DropboxRoot)) { }
            var kept=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("gone"),ct);
            if(replace) Check.Require(kept==binding && commits==0,"A replaced root still committed the both-absent folder's retirement");
            else if(binding!=null) Check.Require(kept==null,"A both-absent binding under the saved root was not retired");
        });
        return proofs;
    }

    // L14b R7 Part 1 (i): "cleanup counts: RetireAsync and SettleDeleteAsync cleanups of a K-folder tree: exactly 1 RootAsync each, for
    // K = 1 and K = 3" (here the cleanup hop itself, uncovered); mutant "drop confirmedCleanup -> (i) counts K+1".
    // (j2) matching root, covered branch: "covered exit after exactly 1 RootAsync per hop (no scan, no BeginScan)".
    public static async Task CleanupHopRootCountAsync()
    {
        foreach(var k in new[]{1,3}) await RepairChecks.WithRepairStore(async (store,pair,ct)=>
        {
            var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed); int proofs=0;
            var tree=new List<string>{"t"}; while(tree.Count<k) tree.Add(tree[^1]+"/"+tree.Count); SyncedEntry? root=null;
            foreach(var path in tree) { var b=SyncedEntries.From(ItemChecks.File(Side.Local,path,0,"pc-"+path,true),ItemChecks.File(Side.Dropbox,path,0,"db-"+path,true)); root??=b; await store.CommitAsync(pair.PairId,null,b,null,null,ct); }
            var remote=Obs.Wrap<ISide>((m,args)=>{ if(m.Name==nameof(ISide.RootAsync)) Interlocked.Increment(ref proofs); return Obs.Forward(db,m,args); });
            var scope=new Scope("t","t");
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,new WorkQueue(),pc,remote,store,scope,(root,scope,scope),ct);
            Check.Require(proofs==1,$"A {k}-folder confirmed-removal cleanup proved the Dropbox root {proofs} times");
            foreach(var p in tree.Skip(1)) Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(p),ct)==null,$"The cleanup did not retire the descendant {p}");
        });
        await RepairChecks.WithRepairStore(async (store,pair,ct)=>
        {
            var (bound,pc,db,queue)=await RepairChecks.CoveredQueueAsync(store,pair,ct); var scope=new Scope("old","old"); int proofs=0, scans=0;
            var binding=SyncedEntries.From(ItemChecks.File(Side.Local,"old",0,"pc",true),ItemChecks.File(Side.Dropbox,"old",0,"db",true));
            var remote=Obs.Wrap<ISide>((m,args)=>{ if(m.Name==nameof(ISide.RootAsync)) Interlocked.Increment(ref proofs); return Obs.Forward(db,m,args); });
            var counted=Obs.Wrap<IStore>((m,args)=>{ if(m.Name==nameof(IStore.BeginScan)) Interlocked.Increment(ref scans); return Obs.Forward(store,m,args); });
            await Repair.RunAsync(bound,ItemChecks.Settings,_=>{},new PathScope(bound,[bound],DriveType.Fixed),queue,pc,remote,counted,scope,(binding,scope,scope),ct);
            Check.Require(proofs==1 && scans==0 && pc.Requests[nameof(ISide.ReadAllAsync)]+db.Requests[nameof(ISide.ReadAllAsync)]==0,$"A covered cleanup hop under the saved root proved {proofs} times, began {scans} scans or listed");
        });
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // L14b R7 Part 4 (b): "ordinary Repair, list succeeds but strict single read fails: unrelated new files copied; the malformed file not
    // copied; one ReadDropboxFailed notice; in-place and end turns ran (2 strict ReadOneAsync); a changed PC peer elsewhere syncs."
    // (c): "a second Repair re-raises the same exact token and the row is not skipped as touched."
    public static Task IsolatedFileRowAsync() => RepairChecks.WithRepairStore(async (store,pair,ct)=>
    {
        await store.FinishRepairAsync(pair.PairId,(0,0),"{}",ct);
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        db.Seed(ItemChecks.File(Side.Dropbox,"bad",1,"db-bad"),[1]); db.ReadFailures["bad"]=new ItemFailedException("bad",TextKey.ReadDropboxFailed,null);
        foreach(var n in new[]{"new1","new2"}) db.Seed(ItemChecks.File(Side.Dropbox,n,2,"db-"+n),[2]);
        var l=ItemChecks.File(Side.Local,"edited",3,"pc-edited"); var d=ItemChecks.File(Side.Dropbox,"edited",3,"db-edited");
        pc.Seed(l with {Modified=l.Modified.AddDays(1)},[4]); db.Seed(d,[3]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(l,d),null,null,ct);
        int strict=0;
        var remote=Obs.Wrap<ISide>((m,args)=>{ if(m.Name==nameof(ISide.ReadOneAsync) && (string?)args[0]=="bad") Interlocked.Increment(ref strict); return Obs.Forward(db,m,args); });
        var queue=await BoundQueueAsync(pair,paths,pc,remote,store,ct);
        async Task<List<Notice>> AtBad() { var all=new List<Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(n.Path=="bad") all.Add(n); return all; }
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,remote,store,null,null,ct,caughtUp:true);
        Check.Require(pc.Items.ContainsKey("new1") && pc.Items.ContainsKey("new2") && !pc.Items.ContainsKey("bad") && db.Items["edited"].Bytes.SequenceEqual(new byte[]{4}),"An isolated Dropbox file stopped unrelated work or was copied");
        var first=await AtBad();
        Check.Require(first.Count==1 && first[0].Cause==TextKey.ReadDropboxFailed && strict==2,$"The isolated file did not get one ReadDropboxFailed notice after its in-place and end turns ({string.Join(",",first.Select(n=>n.Cause))}; strict reads {strict})");
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,remote,store,null,null,ct,caughtUp:true);
        var second=await AtBad();
        Check.Require(second.Count==1 && second[0].Id==first[0].Id && second[0].Cause==TextKey.ReadDropboxFailed && strict==4,$"A second Repair did not re-raise the same notice after its own turns (strict reads {strict}; notices {second.Count})");
    });

    // L14b R7 Part 4 (e5): "PC-side: a Dropbox-departed bound folder elsewhere in the range: PC folder not recycled, RetryRecoveryRequired.
    // Controls: e1-e5 with no isolated file delete/recycle as today." Deletion safety: the retained folder and its file stay on the PC; the
    // control's recycled folder and file are in the Recycle Bin history, never nowhere.
    public static async Task IsolatedFileKeepsPcFolderAsync() { await IsolatedPcAsync(true); await IsolatedPcAsync(false); }
    private static Task IsolatedPcAsync(bool isolated) => RepairChecks.WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        foreach(var (path,folder) in new[]{("H",true),("H/x",false)})
        {
            var local=ItemChecks.File(Side.Local,path,folder?(byte)0:(byte)1,"pc-"+path,folder); pc.Seed(local,folder?[]:[1]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(local,ItemChecks.File(Side.Dropbox,path,folder?(byte)0:(byte)1,"db-"+path,folder)),null,null,ct);
        }
        db.Seed(ItemChecks.File(Side.Dropbox,"F",0,"db-F",true),[]); db.Seed(ItemChecks.File(Side.Dropbox,"F/m",2,"db-F/m"),[2]); // Dropbox H is gone
        if(isolated) db.ReadFailures["F/m"]=new ItemFailedException("F/m",TextKey.ReadDropboxFailed,null);
        var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct); FolderChecks.AdmitFullRepair(queue,pc,db);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(pc.Items.ContainsKey("F"),"The Dropbox-only folder holding the isolated file was not brought to the PC");
        if(isolated) Check.Require(pc.Items.ContainsKey("H") && pc.Items.TryGetValue("H/x",out var kept) && kept.Bytes.SequenceEqual(new byte[]{1}) && pc.History.Count==0,"A Repair whose listing isolated a Dropbox file recycled a PC folder");
        else Check.Require(!pc.Items.ContainsKey("H") && pc.History.Any(h=>h.Entry.Id=="pc-H/x" && h.Bytes.SequenceEqual(new byte[]{1})),"The control did not recycle the Dropbox-departed PC folder with its file");
    });

    // ---------------------------------------------------------------------------------------------------------------------------
    // A moved folder old -> new on both sides; its children are seeded per case and the mapped hop (Before old, After new) is run directly,
    // as Effects' compareOldBinding runs it. The queue's proof never covers, so a one-sided bound child cannot be decided in the pass.
    private sealed record MovedFolder(PairConfig Pair,PathScope Paths,FakeSide Pc,FakeSide Db,SyncedEntry Parent,WorkQueue Queue);
    private static async Task<MovedFolder> MovedFolderAsync(IStore store,PairConfig pair,Func<FakeSide,FakeSide,Task> children,CancellationToken ct)
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var parent=SyncedEntries.From(ItemChecks.File(Side.Local,"old",0,"pc",true),ItemChecks.File(Side.Dropbox,"old",0,"db",true));
        await store.CommitAsync(pair.PairId,null,parent,null,null,ct);
        pc.Seed(ItemChecks.File(Side.Local,"old",0,"pc",true),[]); db.Seed(ItemChecks.File(Side.Dropbox,"old",0,"db",true),[]);
        await children(pc,db);
        await pc.MoveAsync(pc.Items["old"].Entry,"new",ct); await db.MoveAsync(db.Items["old"].Entry,"new",ct);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
        Func<Func<(JournalPosition? Local,string? Dropbox),bool>?> never=()=>_=>false;
        typeof(WorkQueue).GetProperty("ProofNow",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(queue,never);
        return new(pair,paths,pc,db,parent,queue);
    }
    private static async Task BindChildAsync(IStore store,PairConfig pair,FakeSide pc,FakeSide db,string name,byte value,CancellationToken ct)
    {
        var l=ItemChecks.File(Side.Local,"old/"+name,value,"pc-"+name); var d=ItemChecks.File(Side.Dropbox,"old/"+name,value,"db-"+name);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(l,d),null,null,ct); pc.Seed(l,[value]); db.Seed(d,[value]);
    }
    private static Task HopAsync(MovedFolder m,ISide local,ISide dropbox,IStore store,CancellationToken ct) =>
        Repair.RunAsync(m.Pair,ItemChecks.Settings,_=>{},m.Paths,m.Queue,local,dropbox,store,new("old","old"),(m.Parent,new("old","old"),new("new","new")),ct);
    private static IStore CountOwes(IStore store,Action owed,Func<bool>? fault=null) => Obs.Wrap<IStore>((m,args)=>
    {
        if(m.Name==nameof(IStore.OweScopeAsync)) { if(fault?.Invoke()==true) return Task.FromException(new IOException("owe interrupted")); owed(); }
        return Obs.Forward(store,m,args);
    });

    // L9 R4/R6 A7 (frozen): "Mixed rows in one canonical pass (an unreadable leaf, an Info child, a Touched child, an injected race, an
    // Unsettled one-sided child): one OweScopeAsync, one revision advance." and "No owed scope (exact count 0) and no effect for: an excluded
    // child, ... a Tie-held child". Touched, KEEP, independent-move, intermediate-hop and unmappable arms are not built here (see REPORT).
    public static async Task HopOweMatrixAsync()
    {
        await RepairChecks.WithRepairStore(async (store,pair,ct)=>
        {
            var m=await MovedFolderAsync(store,pair,async (pc,db)=>
            {
                foreach(var (name,v) in new[]{("bad",(byte)1),("info",(byte)2),("race",(byte)3)}) await BindChildAsync(store,pair,pc,db,name,v,ct);
                pc.Seed(ItemChecks.File(Side.Local,"old/add",4,"pc-add"),[4]); // unbound add whose upload fails: Unsettled
            },ct);
            var (bad,bytes)=m.Pc.Items["new/bad"]; m.Pc.Items["new/bad"]=(bad with {Skip=nameof(TextKey.SkipUnreadable)},bytes);
            m.Db.Items.Remove("new/info"); // one-sided bound child with an open Info notice: undecided, owed
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Info,TextKey.InfoLinks,"old/info",new([new(Side.Local,"old/info","pc-info")],Side.Local),DateTimeOffset.UnixEpoch,false),ct);
            m.Pc.Items["new/race"]=(ItemChecks.File(Side.Local,"new/race",5,"pc-race"),[5]);
            var remote=Obs.Wrap<ISide>((mi,args)=>mi.Name==nameof(ISide.PublishAsync) && args[0] is IStaged {Path:var p} ?
                (p=="new/race" ? Task.FromException<RemovalResult>(new ChangedUnderneathException("new/race")) : p=="new/add" ? Task.FromException<RemovalResult>(new ItemFailedException("new/add",TextKey.FailLocalInUse,null)) : Obs.Forward(m.Db,mi,args))
                : Obs.Forward(m.Db,mi,args));
            int owes=0; var before=(await store.PairAsync(pair.PairId,ct))!.OwedScopesRevision;
            await HopAsync(m,m.Pc,remote,CountOwes(store,()=>Interlocked.Increment(ref owes)),ct);
            var after=(await store.PairAsync(pair.PairId,ct))!;
            Check.Require(owes==1 && after.OwedScopesRevision==before+1 && after.OwedScopes?.Contains(new Scope("new","new"))==true,$"A mixed canonical pass owed {owes} times (revision +{after.OwedScopesRevision-before})");
            Check.Require(m.Pc.Items.ContainsKey("new/info") && m.Pc.History.Count==0 && !m.Db.Items.ContainsKey("new/add"),"A child left to the owed Repair took an effect in the mapped pass");
        });
        foreach(var arm in new[]{"tie","excluded"}) await RepairChecks.WithRepairStore(async (store,pair,ct)=>
        {
            if(arm=="excluded") { pair=pair with {Exclusions=[new(Side.Local,"new/ex",true)]}; await store.SavePairAsync(pair,ct); }
            var m=await MovedFolderAsync(store,pair,async (pc,db)=>
            {
                if(arm=="tie") await BindChildAsync(store,pair,pc,db,"held",1,ct);
                else pc.Seed(ItemChecks.File(Side.Local,"old/ex",1,"pc-ex"),[1]);
            },ct);
            if(arm=="tie")
            {
                m.Db.Items.Remove("new/held");
                await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Tie,TextKey.TieEqualTimes,"old/held",new([new(Side.Local,"old/held","pc-held")],null),DateTimeOffset.UnixEpoch,false),ct);
            }
            int owes=0; long writes=m.Pc.Mutations.Count+m.Db.Mutations.Count;
            await HopAsync(m,m.Pc,m.Db,CountOwes(store,()=>Interlocked.Increment(ref owes)),ct);
            Check.Require(owes==0 && (await store.PairAsync(pair.PairId,ct))?.OwedScopes is null or {Count:0},$"arm={arm}: a child with its own owner owed the hop scope ({owes})");
            Check.Require(m.Pc.Mutations.Count+m.Db.Mutations.Count==writes && !m.Db.Items.ContainsKey("new/ex") && (arm!="tie" || m.Pc.Items.ContainsKey("new/held")),$"arm={arm}: the mapped pass took an effect on a child with its own owner");
        });
    }

    // L14a R6 A4 (frozen) HopUnsettledOwedOnce: "unbound add + both-present unsettled + half-present row: one owe, one revision advance, Wait
    // only for the half-present row; ... failure/recovery: OweScopeAsync faults once -> the hop fails through its caller's route, the next
    // hop execution owes once and the add reaches the other side." The held-callback-on-a-different-footprint arm needs the host's owed
    // dispatcher and is not built here (see REPORT).
    public static Task HopUnsettledOwedOnceAsync() => RepairChecks.WithRepairStore(async (store,pair,ct)=>
    {
        var m=await MovedFolderAsync(store,pair,async (pc,db)=>
        {
            await BindChildAsync(store,pair,pc,db,"both",1,ct); await BindChildAsync(store,pair,pc,db,"half",2,ct);
            pc.Seed(ItemChecks.File(Side.Local,"old/add",3,"pc-add"),[3]);
        },ct);
        m.Pc.Items["new/both"]=(ItemChecks.File(Side.Local,"new/both",4,"pc-both"),[4]); // both present, PC edited: its upload fails
        m.Db.Items.Remove("new/half"); // half present: its removal cannot settle without a proof the queue does not give
        bool refuse=true;
        var remote=Obs.Wrap<ISide>((mi,args)=>refuse && mi.Name==nameof(ISide.PublishAsync) && args[0] is IStaged {Path:"new/both" or "new/add"} staged ?
            Task.FromException<RemovalResult>(new ItemFailedException(staged.Path,TextKey.FailLocalInUse,null)) : Obs.Forward(m.Db,mi,args));
        async Task<List<Notice>> Waits() { var all=new List<Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(n.Cause==TextKey.WaitItemRetry) all.Add(n); return all; }
        // The first execution's owe faults: the hop fails to its caller, nothing is owed.
        int owes=0, faults=0; var revision=(await store.PairAsync(pair.PairId,ct))!.OwedScopesRevision;
        try { await HopAsync(m,m.Pc,remote,CountOwes(store,()=>Interlocked.Increment(ref owes),()=>Interlocked.Increment(ref faults)==1),ct); throw new InvalidDataException("A faulted owe did not fail the hop"); }
        catch(IOException e) when(e.Message=="owe interrupted") { }
        Check.Require((await store.PairAsync(pair.PairId,ct))!.OwedScopesRevision==revision && m.Pc.Items.ContainsKey("new/half"),"A faulted owe still advanced the owed scopes or the half-present child took an effect");
        // The next execution owes once for all three rows; only the half-present bound row shows a Wait.
        owes=0; await HopAsync(m,m.Pc,remote,CountOwes(store,()=>Interlocked.Increment(ref owes)),ct);
        var owed=(await store.PairAsync(pair.PairId,ct))!; var waits=await Waits();
        Check.Require(owes==1 && owed.OwedScopesRevision==revision+1 && owed.OwedScopes?.Contains(new Scope("new","new"))==true,$"The next hop execution owed {owes} times (revision +{owed.OwedScopesRevision-revision})");
        Check.Require(waits.Count==1 && waits[0].Path=="new/half",$"Wait notices were not exactly the half-present row's: {string.Join(",",waits.Select(n=>n.Path))}");
        // Deletion safety: an unproved removal is Pending under its armed recovery; the version is on the PC or in its Recycle Bin, never nowhere.
        Check.Require(m.Pc.Items.ContainsKey("new/half") || m.Pc.History.Any(h=>h.Entry.Id=="pc-half" && h.Bytes.SequenceEqual(new byte[]{2})),"The half-present child's version ended up nowhere");
        // The owed scoped Repair: uploads now succeed and the add reaches Dropbox.
        refuse=false;
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},m.Paths,m.Queue,m.Pc,remote,store,new("new","new"),null,ct);
        Check.Require(m.Db.Items.TryGetValue("new/add",out var add) && add.Bytes.SequenceEqual(new byte[]{3}) && m.Db.Items.TryGetValue("new/both",out var both) && both.Bytes.SequenceEqual(new byte[]{4}),"The owed Repair did not bring the unsettled add and edit to Dropbox");
    });

    // ---------------------------------------------------------------------------------------------------------------------------
    // L2 MEMBERS FolderSettlement (KEEP): "RemoveBoundAsync folder root removal (both sides) -> root's exact tokens closed in the :302
    // commit" and "ReadLocalFailed on a surviving folder -> open". A full Repair reaches a departed bound folder through RemoveBoundAsync.
    // Deletion safety: the removed root is in the other side's deleted files or Recycle Bin; the surviving folder's unreadable file stays.
    public static async Task FolderSettlementAsync() { await RootRemovalClosesAsync(Side.Local); await RootRemovalClosesAsync(Side.Dropbox); await SurvivingFolderAsync(); }

    private static Task RootRemovalClosesAsync(Side departed) => RepairChecks.WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var kept=departed==Side.Local?db:pc; var other=PathNames.Other(departed);
        var local=ItemChecks.File(Side.Local,"H",0,"pc-H",true); var remote=ItemChecks.File(Side.Dropbox,"H",0,"db-H",true);
        kept.Seed(departed==Side.Local?remote:local,[]); await store.CommitAsync(pair.PairId,null,SyncedEntries.From(local,remote),null,null,ct); // the folder departed on one side
        var exact=(await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,"H",new([new(other,"H",other==Side.Local?"pc-H":"db-H")],other),DateTimeOffset.UnixEpoch,false),ct))!;
        var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct); FolderChecks.AdmitFullRepair(queue,pc,db);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(!kept.Items.ContainsKey("H") && kept.History.Any(h=>h.Entry.IsFolder && h.Entry.Path=="H") && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("H"),ct)==null,$"departed={departed}: the bound folder was not removed into the other side's deleted files or Recycle Bin");
        Notice? still=null; await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(n.Id==exact.Id) still=n;
        Check.Require(still==null,$"departed={departed}: the root's exact earlier Failure did not close in its removal commit");
    });

    private static Task SurvivingFolderAsync() => RepairChecks.WithRepairStore(async (store,pair,ct)=>
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        foreach(var (path,folder) in new[]{("H",true),("H/x",false)})
        {
            var local=ItemChecks.File(Side.Local,path,folder?(byte)0:(byte)1,"pc-"+path,folder); pc.Seed(local,folder?[]:[1]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(local,ItemChecks.File(Side.Dropbox,path,folder?(byte)0:(byte)1,"db-"+path,folder)),null,null,ct);
        }
        pc.ReadFailures["H/x"]=new ItemFailedException("H/x",TextKey.ReadLocalFailed,null); // Dropbox H is gone; the PC file under H still cannot be read
        var read=(await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.ReadLocalFailed,"H/x",new([new(Side.Local,"H/x","pc-H/x")],Side.Local),DateTimeOffset.UnixEpoch,false),ct))!;
        var queue=await BoundQueueAsync(pair,paths,pc,db,store,ct); FolderChecks.AdmitFullRepair(queue,pc,db);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,db,store,null,null,ct);
        Check.Require(pc.Items.ContainsKey("H") && pc.Items.TryGetValue("H/x",out var kept) && kept.Bytes.SequenceEqual(new byte[]{1}) && pc.History.Count==0,"A folder holding an unreadable PC file was recycled");
        Notice? still=null; await foreach(var n in store.NoticesAsync(pair.PairId,ct)) if(n.Id==read.Id) still=n;
        Check.Require(still is {Dismissed:false},"The surviving folder's ReadLocalFailed notice was closed");
    });

    // A Repair queue bound to its job owner the way the runner binds it (retirement reads its open jobs).
    internal static async Task<WorkQueue> BoundQueueAsync(PairConfig pair,PathScope paths,ISide local,ISide dropbox,IStore store,CancellationToken ct)
    {
        var queue=new WorkQueue();
        await new PairRunner().CatchUpAsync(pair,(null,null),ItemChecks.Settings,_=>{},paths,queue,local,dropbox,store,static()=>{},ct);
        return queue;
    }
}
