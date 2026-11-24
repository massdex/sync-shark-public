using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks cold-cache and pre-install path-only wakes.</summary>
public static class UnfinishedChecks
{
    public static async Task RunAsync()
    {
        var failures=new List<string>();var missing=new List<string>();
        async Task Run(string name,Func<Task> test,bool retries=false)
        {
            try {await test();}
            catch(NotImplementedException) when(retries){missing.Add(name);}
            catch(Exception ex){failures.Add(name+": "+ex.Message);}
        }
        foreach(var mode in new[]{"latest","unverified","missing","remap","close","newer-slot","moved","earlier","different-id","other-side","restart","legacy"})
            await Run(mode,()=>FolderDeleteChecks.SelectedCloudWindowAsync([mode]),mode is not ("latest" or "unverified" or "missing"));
        foreach(var mode in new[]{"lease","legacy-mapped","backdated","stale-echo","moved-away","legacy-failure","stage-race","folder-grant"}) await Run(mode,()=>FolderDeleteChecks.MappedRetryAsync(mode),true);
        await Run("older-snapshot",()=>FolderDeleteChecks.RecoveryAsync(true),true);
        await Run(nameof(FolderDeleteChecks.RecoveryAsync),FolderDeleteChecks.RecoveryAsync,true);
        foreach(var test in new Func<Task>[]{ArmedHopDeleteRetiresProjectedBindingAsync,TwoChildDeletesDuringMoveAsync,MovedPeerAsync,ReviewedJobsAsync,InterruptedRestoreAsync,PublishedCommitAsync,UnboundMoveAsync,DeletedStageAsync,TerminalAsync,MixedAsync,PendingParentAsync,UnknownAsync,ThirdPathAsync,AbsentMoveAsync,MissingOriginAsync,PolicyAsync,PlainRecycleAsync,UnboundStageAsync,ComparedFolderAsync}) await Run(test.Method.Name,test);
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var owner=new Unfinished();var change=new Change(Side.Dropbox,ChangeKind.Deleted,"else",null,null,null,false,false){AffectedPath="new/child"};
        Task Visit(Change? c)=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),c,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        await Visit(null); // Boot can finish before either token exists.
        var job=new AcceptedJob(pair.PairId,"move","token",[new("id","old","new")]);var other=new AcceptedJob(pair.PairId,"move","other",[new("other-id","unrelated","elsewhere")]);
        foreach(var accepted in new[]{other,job}){await store.SaveJobAsync(accepted,default);db.JobReplies[accepted.JobId]=new([new JobReply.Running()]);}
        int wakes=0;Task Wake(Change c){Check.Require(c==change,"Changed held report");wakes++;return Visit(c);}
        await owner.InstalledAsync(other,_=>[change],Wake);
        await owner.InstalledAsync(job,_=>[change,change],Wake);
        await owner.InstalledAsync(job,_=>[change with {AffectedPath="newer/child"}],Wake);
        int pending=0;await foreach(var _ in store.JobsAsync(pair.PairId,default))pending++;
        Check.Require(wakes==1 && pending==2 && db.Requests[nameof(ISide.ReadJobAsync)]==1 && db.JobReplies[other.JobId].Count==1 && db.JobReplies[job.JobId].Count==0 && db.Mutations.Count==0,"Installed path-only wake did not check just its running token once");
        Check.Require(failures.Count==0,string.Join("; ",failures));
        if(missing.Count!=0) throw new NotImplementedException(string.Join("; ",missing));
    }

    public static async Task DeferredEnsureAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var job=new AcceptedJob(pair.PairId,"files/create_folder_batch/check","create",[new(null,"wait",null),new(null,"good",null)]);await store.SaveJobAsync(job,default);var good=ItemChecks.File(Side.Dropbox,"good",0,"good-id",true);var occupant=good with {Path="wait",Id="held-id"};db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,TextKey.ClashNameTaken,FailureDisposition.Permanent),(null,TextKey.ClashNameTaken,FailureDisposition.Permanent)])]);
        int lookups=0,goodLookups=0;var clock=new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch);var start=clock.GetTimestamp();var delay=TimeSpan.FromSeconds(7);
        var wait=new ItemFailedException("wait",TextKey.FailChanged,null){Disposition=FailureDisposition.TemporaryNoEffect,ProviderWait=delay,ProviderWaitStarted=start};
        var held=new TaskCompletionSource<JobReply.Terminal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var side=FolderDeleteChecks.Observe.On(db,(op,args)=>{
            if(op!=nameof(ISide.ResolveCreateClashAsync))return null;
            var selected=(JobMember)args[0]!;
            if(selected==job.Launch[1]){goodLookups++;return Task.FromResult(new JobReply.Terminal([(good,null,null)]));}
            Check.Require(selected==job.Launch[0],"Deferred lookup changed ordinal");
            return ++lookups==1?held.Task:Task.FromResult(new JobReply.Terminal([(occupant,null,null)]));});
        var owner=new Unfinished();Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,side,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        var first=Visit();bool bootReturned=first.IsCompletedSuccessfully,peerStarted=goodLookups==1;
        if(!bootReturned){held.SetException(wait);try{await first;}catch(ItemFailedException error) when(ReferenceEquals(error,wait)){} }
        Check.Require(peerStarted,"Pending ensure blocked its independent neighbour");
        Check.Require(bootReturned,"Finite boot check waited for an outstanding ensure lookup");
        await Visit();Check.Require(!held.Task.IsCompleted && clock.GetTimestamp()==start && goodLookups==1 && lookups==1 && owner.CoversOpenJob(new("wait","wait")),"Another wake resent an in-flight ensure lookup or lost job custody");
        held.SetException(wait);bool original=false;try{await Visit();}catch(ItemFailedException error){original=ReferenceEquals(error,wait);}
        Check.Require(original && lookups==1,"Deferred ensure did not preserve the selected lookup wait");Check.Require(goodLookups==1,"Deferred ensure blocked a good neighbour before its wait expired");clock.Advance(delay);await Visit();
        Check.Require(goodLookups==1 && lookups==2 && db.Requests[nameof(ISide.ReadJobAsync)]==1 && db.Mutations.Count==0 && db.Requests[nameof(ISide.EnsureFolderAsync)]==0 && db.Requests[nameof(ISide.CreateFolderExclusiveAsync)]==0,"A settled ensure neighbour, mutation or job poll was replayed");await foreach(var _ in store.JobsAsync(pair.PairId,default))throw new InvalidDataException("Resolved ensure still owns its job");
    }
    private static async Task ArmedHopDeleteRetiresProjectedBindingAsync()
    {
        foreach(bool present in new[]{false,true})
        {
            var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
            var f=ItemChecks.File(Side.Local,"New/c",1,"c");var r=ItemChecks.File(Side.Dropbox,f.Path,1,"dc");
            var old=SyncedEntries.From(f with {Path="Old/c"},r with {Path="Old/c"});var projected=SyncedEntries.From(f,r);
            await store.CommitAsync(pair.PairId,null,projected,null,null,default);pc.Seed(f,[1]);await pc.DeleteAsync(f,null,default);
            if(present)db.Seed(r,[1]);
            await store.ArmAsync(pair.PairId,new(new(f,"volume",f.ContentHash),old),null,default);
            await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
            if(present) Check.Require(store.Records.Values.Single()==projected && db.Items[r.Path].Entry==r,"Recovery retired a live projected member");
            else Check.Require(store.Records.Count==0 && await store.ArmedAsync(pair.PairId,default)==null && pc.Items.Count==0 && db.Items.Count==0,"A hop-projected binding of an armed, recycled file was not retired");
        }
    }

    private static Task TwoChildDeletesDuringMoveAsync()=>HopDeletesAsync(false);
    public static Task HopChildrenSettleAtReceiptAsync()=>HopDeletesAsync(true);
    public static Task HopRetryKeepsLivePeerAsync()=>HopDeletesAsync(true,true);
    private static async Task HopDeletesAsync(bool settle,bool livePeer=false)
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);using var state=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        using var sql=await Storage.DeletedNamesChecks.OpenAsync();var table=new global::Massdex.SyncShark.Engine.Storage.PairTable();int arms=0;
        var store=FolderDeleteChecks.Observe.Wrap<IStore>(state,(op,args)=>op switch {
            nameof(IStore.ArmAsync)=>table.ArmAsync(sql,"p",(RemovalSelection)args[1]!, (StageProof?)args[2],(CancellationToken)args[3]!),
            nameof(IStore.ArmedAsync)=>table.ArmedAsync(sql,"p",(CancellationToken)args[1]!),
            nameof(IStore.DisarmAsync)=>table.DisarmAsync(sql,"p",(RemovalSelection)args[1]!, (StageProof?)args[2],(CancellationToken)args[3]!),_=>null});
        async Task<RemovalResult> Recycle(Entry child,RemovalSelection victim)
        {
            arms++;if(!await store.ArmAsync(pair.PairId,victim,null,default)) return new(RemovalOutcome.Pending,null,null);
            var result=await pc.DeleteAsync(child,victim,default);
            if(settle && result.Outcome==RemovalOutcome.Removed) await store.DisarmAsync(pair.PairId,victim,null,default);
            return result;
        }
        var local=FolderDeleteChecks.Observe.On(pc,(op,args)=>op==nameof(ISide.DeleteAsync)?Recycle((Entry)args[0]!,(RemovalSelection)args[1]!):
            op==nameof(ISide.ReadByIdAsync) && pc.Items.Values.FirstOrDefault(v=>v.Entry.Id==(string?)args[0]).Entry is {} entry ? Task.FromResult<(IdentityLocation,bool)?>((new("volume",entry.Path,entry),true)):null);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,local,db,store);
        bool covered=false;Func<Func<(JournalPosition? Local,string? Dropbox),bool>> proof=()=>_=>covered;
        queue.GetType().GetProperty("ProofNow",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(queue,proof);
        var root=ItemChecks.File(Side.Local,"Old",0,"folder",true);var peer=root with {Id="peer"};var link=SyncedEntries.From(root,peer);
        await store.CommitAsync(pair.PairId,null,link,null,null,default);pc.Seed(root,[]);db.Seed(peer,[]);
        foreach(var name in new[]{"a","b"})
        {
            var child=ItemChecks.File(Side.Local,"Old/"+name,1,name);pc.Seed(child,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(child,ItemChecks.File(Side.Dropbox,child.Path,1,"peer-"+name)),null,null,default);
        }
        await pc.MoveAsync(root,"New",default);await db.MoveAsync(peer,"New",default);
        var hop=ItemChecks.Hop(pair,paths,queue,local,db,store);await hop(link,new("Old","Old"),new("New","New"),default);
        if(settle) Check.Require(pc.History.Select(v=>v.Entry.Id).Order().SequenceEqual(new[]{"a","b"}) && await store.ArmedAsync(pair.PairId,default)==null && state.OpenNotices.Where(n=>!n.Dismissed && n.Cause==TextKey.WaitItemRetry).Select(n=>n.Path).Order().SequenceEqual(new[]{"New/a","New/b"}),"Both recycles must settle before hop Retry");
        else Check.Require(pc.History.Count==1 && pc.History[0].Entry.Id=="a" && pc.Items["New/b"].Bytes.SequenceEqual(new byte[]{1}) && (await store.ArmedAsync(pair.PairId,default))?.Victim.Proof.Expected.Id=="a" && state.Records.Values.Any(r=>r.LocalId=="b"),"First hop lost custody of the waiting child or the original note");
        await store.ProjectSubtreeAsync(pair.PairId,new("Old","Old"),new("New","New"),default);
        var parent=SyncedEntries.From(pc.Items["New"].Entry,db.Items["New"].Entry);await store.CommitAsync(pair.PairId,link,parent,null,null,default);
        covered=true;pc.IdleBoundary=new([],null,new JournalPosition(1,1));db.IdleBoundary=new([],"end",null);
        await queue.Unfinished.CheckAsync(pair,ItemChecks.Settings,_=>{},paths,null,local,db,store,_=>[],ItemChecks.Population(pair,paths,local,db,store),hop,default);
        if(settle)
        {
            if(livePeer) db.Seed(ItemChecks.File(Side.Dropbox,"New/b",1,"peer-b"),[1]);
            var dbDeletes=db.Requests[nameof(ISide.DeleteAsync)];
            foreach(var n in state.OpenNotices.Where(n=>!n.Dismissed && (!livePeer || n.Path=="New/b")).ToArray()) await new Retries(queue.Unfinished).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,n,local,db,store,queue.Held,ItemChecks.Population(pair,paths,local,db,store),hop,default,_=>covered);
            if(livePeer)
            {
                Check.Require(db.Items.TryGetValue("New/b",out var peerB) && peerB.Entry.Id=="peer-b" && peerB.Bytes.SequenceEqual(new byte[]{1})
                    && db.Requests[nameof(ISide.DeleteAsync)]==dbDeletes
                    && state.OpenNotices.Where(n=>!n.Dismissed && n.Path=="New/b").ToArray() is [{Cause:TextKey.FailChanged}]
                    && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("New/b"),default) is {LocalId:"b",DropboxId:"peer-b"},"Hop Retry deleted or decided a live bound peer instead of keeping it with one stale notice");return;
            }
            Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("New/a"),default)==null && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key("New/b"),default)==null && !state.OpenNotices.Any(n=>!n.Dismissed) && await store.ArmedAsync(pair.PairId,default)==null
                && pc.History.Select(v=>v.Entry.Id).Order().SequenceEqual(new[]{"a","b"}) && pc.History.All(v=>v.Bytes.SequenceEqual(new byte[]{1}))
                && !db.Items.ContainsKey("New/a") && !db.Items.ContainsKey("New/b") && db.Requests[nameof(ISide.DeleteAsync)]==dbDeletes,"Retry of binned hop children did not forget the dead bindings by identity, or touched a version");return;
        }
        var retry=state.OpenNotices.SingleOrDefault(n=>n.Kind==NoticeKind.Failure && !n.Dismissed && n.Path=="New/b");
        Check.Require(retry!=null,"The second child has no Failure notice at New/b");
        await new Retries(queue.Unfinished).RetryAsync(pair,ItemChecks.Settings,_=>{},paths,retry!,local,db,store,queue.Held,ItemChecks.Population(pair,paths,local,db,store),hop,default,_=>covered);
        Check.Require(!state.OpenNotices.Any(n=>n.Id==retry!.Id && !n.Dismissed),"Retry left the second child's notice open");
        Check.Require(arms>=3 && pc.History.Select(v=>v.Entry.Id).SequenceEqual(new[]{"a","b"}) && pc.History.All(v=>v.Bytes.SequenceEqual(new byte[]{1})) && state.Records.Values.Single()==parent && await store.ArmedAsync(pair.PairId,default)==null,"Waiting child did not settle with both original versions recoverable and only the moved parent bound");
    }

    public static async Task NoteAsync(int row)
    {
        var pair=FolderDeleteChecks.BoundPair();var paths=new PathScope(pair,[pair],DriveType.Fixed);using var store=new TestDoubles.InMemoryStateStore();
        var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var path=row==12?"New/a":row==13?"f":"a";var original=row==12?"Old/a":path;
        var a=ItemChecks.File(Side.Local,path,1,"L1",row==13);var d=ItemChecks.File(Side.Dropbox,original,1,"D1",row==13);
        var saved=SyncedEntries.From(a with {Path=original},d);var binding=row==12?SyncedEntries.From(a,d with {Path=path}):saved;
        var fresh=ItemChecks.File(Side.Dropbox,path,2,"D2");var put=ItemChecks.File(Side.Local,path,1,row switch {12=>"L5",15=>"L9",_=>"L3"});
        if(row==9) binding=SyncedEntries.From(put with {Id="L2"},fresh);
        await store.CommitAsync(pair.PairId,null,binding,null,null,default);
        if(row==6)pc.Seed(a,[1]);else if(row!=7)pc.History.Add((a,[1]));
        if(row is 10 or 12 or 13 or 15) pc.Seed(put,[1]);
        if(row is 9 or 11) db.Seed(fresh,[2]);
        if(row==14) db.Seed(d with {Path="b"},[1]);
        if(row==13){var x=ItemChecks.File(Side.Local,"f/x",1,"child");await store.CommitAsync(pair.PairId,null,SyncedEntries.From(x,x with {Id="peer-child"}),null,null,default);}
        var victim=new RemovalSelection(new(a,"volume",a.IsFolder?null:a.ContentHash),saved);
        await using var stage=row>=15?await pc.StageAsync(path,new MemoryStream([2]),fresh,a,default):null;
        await store.ArmAsync(pair.PairId,victim,stage?.Proof,default);
        if(row>=16) await pc.PublishAsync(stage!,null,null,default);
        Notice? reviewed=null;
        if(row==18){await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.WaitItemRetry,path,new([new(Side.Local,path,a.Id)],Side.Local),DateTimeOffset.UtcNow,false),default);reviewed=store.OpenNotices.Single();}
        var queue=new global::Massdex.SyncShark.Engine.Run.WorkQueue();var owner=new Unfinished{CoversThrough=_=>true};int compares=0;
        async Task Compare(SyncedEntry b,Scope from,Scope? to,CancellationToken ct)
        {
            Check.Require(b==saved && from==new Scope(path,path) && from==to,"Unexpected comparison");compares++;
            await ItemChecks.Hop(pair,paths,queue,pc,db,store)(b,from,to,ct);
        }
        var populate=ItemChecks.Population(pair,paths,pc,db,store);
        if(row is 8 or 10 or 11 or 16) queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
        else if(reviewed!=null) await owner.CheckAsync(pair,ItemChecks.Settings,_=>{},paths,null,pc,db,store,_=>[],populate,Compare,default,reviewed);
        else await owner.CheckLiveStageAsync(pair,ItemChecks.Settings,_=>{},paths,pc,db,store,_=>[],populate,Compare,default);
        var armed=await store.ArmedAsync(pair.PairId,default);var found=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(path),default);
        var notices=store.OpenNotices.Where(n=>!n.Dismissed && n.Kind==NoticeKind.Failure && n.Cause==TextKey.RecycleUnchecked).ToArray();
        if(row==17) Check.Require(armed==(victim,stage!.Proof) && notices.Length==0,"Live page ended unfinishable note");
        else Check.Require(armed==null,"Note remained armed");
        bool ok=row switch {
            6=>notices.Length==0 && found==saved && pc.Items.ContainsKey(path),
            7=>notices.Length==1 && notices[0].Subject.Items.SequenceEqual(new[]{new NoticeItem(Side.Local,path,a.Id)}),
            8=>found==null && notices.Length==0,9=>found==binding,10=>found==null,
            13=>!store.Records.Values.Any(b=>b.LocalPath.StartsWith("f/",StringComparison.OrdinalIgnoreCase)) && notices.Length==1 && notices[0].Path==path && compares==1,
            14=>found==saved && db.Items.ContainsKey("b") && db.Requests[nameof(ISide.DeleteAsync)]==0,
            15=>pc.Items[path].Entry.Id==put.Id && pc.Requests[nameof(ISide.PublishAsync)]==0,
            16=>notices.Length==1 && notices[0].Path==path,18=>store.OpenNotices.Where(n=>!n.Dismissed && n.Path==path).SequenceEqual(new[]{reviewed}),_=>true};
        Check.Require(ok,"Unexpected recovery result for row "+row);
        if(row==12){await foreach(var _ in store.ByIdAsync(pair.PairId,Side.Local,a.Id,default)) throw new InvalidDataException("Dead projected binding retained");Check.Require(pc.Items[path].Entry.Id==put.Id,"Replacement lost");}
        if(row is 10 or 11)
        {
            var e=row==10?put:fresh;await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new(row==10?Side.Local:Side.Dropbox,ChangeKind.Changed,path,null,e.Id,e,true,false),pc,db,store,populate,ItemChecks.Hop(pair,paths,queue,pc,db,store),default,coversThrough:_=>true);
            found=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(path),default);
            Check.Require(row==10?db.Items[path].Bytes.SequenceEqual(new byte[]{1}) && found?.LocalId==put.Id && pc.History.All(h=>h.Entry.Id!=put.Id):found?.DropboxId==fresh.Id && pc.Items[path].Bytes.SequenceEqual(new byte[]{2}),"Replacement was not synchronized by replay");
        }
    }
    private static bool RecycleUnchecked(TestDoubles.InMemoryStateStore store,string path,string id)=>
        store.OpenNotices.Where(n=>!n.Dismissed && n.Cause==TextKey.RecycleUnchecked).ToArray() is [{Kind:NoticeKind.Failure} notice]
        && notice.Path==path && notice.Subject.Items.SequenceEqual(new[]{new NoticeItem(Side.Local,path,id)});
    private static async Task MovedPeerAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var local=ItemChecks.File(Side.Local,"arrival",1,"pc");var peer=ItemChecks.File(Side.Dropbox,"elsewhere",2,"peer");var saved=SyncedEntries.From(local with {Path="before"},peer with {Path="before"});
        await store.CommitAsync(pair.PairId,null,saved,null,null,default);pc.History.Add((local,[1]));db.Seed(peer,[2]);var victim=new RemovalSelection(new(local,"volume",local.ContentHash),saved);
        await store.ArmAsync(pair.PairId,victim,null,default);
        Task Restart()=>new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        bool VersionsIntact()=>store.Records.Values.Single()==saved && pc.Items.Count==0 && pc.History.Any(h=>h.Entry.Id==local.Id) && db.Items["elsewhere"].Bytes.SequenceEqual(new byte[]{2});
        await Restart();Check.Require(await store.ArmedAsync(pair.PairId,default)==null && VersionsIntact() && !store.OpenNotices.Any(n=>!n.Dismissed),"Restart did not end a binned victim whose peer is live, or touched a version");
        await using var stage=await pc.StageAsync(local.Path,new MemoryStream([1]),local,local,default);await store.ArmAsync(pair.PairId,victim,stage.Proof,default);
        await Restart();Check.Require(await store.ArmedAsync(pair.PairId,default)==null && RecycleUnchecked(store,"arrival",local.Id) && VersionsIntact(),"Restart did not end the unfinishable stage note with one notice, or published, split or touched a version");
    }
    private static async Task ReviewedJobsAsync()
    {
        var pair=FolderDeleteChecks.BoundPair(); using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var local=ItemChecks.File(Side.Local,"mapped",1,"local");await store.CommitAsync(pair.PairId,null,SyncedEntries.From(local,local with {Path="remote",Id="peer"}),null,null,default);
        var paths=new[]{"folder","remote","source","target","intermediate","unrelated"};
        foreach(var path in paths){await store.SaveJobAsync(new(pair.PairId,"move",path,[new(null,"old/"+path,path)]),default);db.JobReplies[path]=new([new JobReply.Running(),new JobReply.Running()]);}
        var notice=new Notice(1,NoticeKind.Failure,TextKey.FailChanged,"folder/a",new([new(Side.Dropbox,"folder/a","a"),new(Side.Dropbox,"folder/b","b"),new(Side.Local,"mapped","local")],null){Move=new("id","source","target","intermediate",false)},DateTimeOffset.UnixEpoch,false);
        var owner=new Unfinished();await owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,notice);
        Check.Require(paths.All(path=>db.JobReplies[path].Count==(path=="unrelated"?2:1)),"Reviewed footprint missed a job, polled it twice, or polled a neighbour");
    }
    private static async Task InterruptedRestoreAsync()
    {
        foreach(bool same in new[]{true,false})
        {
            var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
            var original=ItemChecks.File(Side.Local,"p",1,"old");var actual=ItemChecks.File(Side.Local,"p",2,same?original.Id:"substitute");var saved=SyncedEntries.From(original,original with {Id="peer"});
            var note=new RemovalSelection(new(original,"volume",original.ContentHash),saved);await store.CommitAsync(pair.PairId,null,saved,null,null,default);await using var stage=await pc.StageAsync("p",new MemoryStream([2]),actual,original,default);await store.ArmAsync(pair.PairId,note,stage.Proof,default);
            var receipt=new IdentityLocation("volume","bin",actual);var side=FolderDeleteChecks.Observe.On(pc,(op,args)=>{
                if(op==nameof(ISide.RestoreRecycledAsync)){pc.Seed(actual,[2]);throw new OperationCanceledException();}
                if(op==nameof(ISide.ReadByIdAsync) && Equals(args[0],actual.Id) && pc.Items.ContainsKey(actual.Path))return Task.FromResult<(IdentityLocation,bool)?>((new("volume",actual.Path,actual),true));return null;});
            try{await HeldRecovery.PutBackSubstituteAsync(pair,new("p","p"),receipt,side,db,store,(_,_,_,_)=>Task.CompletedTask,default);}catch(OperationCanceledException){}
            await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
            Check.Require(await store.ArmedAsync(pair.PairId,default) ==null && stage.Proof!=null && store.OpenNotices.Any(n=>!n.Dismissed && n.Kind==NoticeKind.PutBack) && pc.Items["p"].Entry.Id==actual.Id && pc.Items["p"].Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Single()==saved && db.Items.Count==0
                && (same ? !store.OpenNotices.Any(n=>!n.Dismissed && n.Cause==TextKey.RecycleUnchecked) : RecycleUnchecked(store,"p",original.Id)),"Interrupted restore: the note was not ended (U/G), or the substitute, the put-back notice or the binding changed");
        }
    }
    private static async Task PublishedCommitAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var old=ItemChecks.File(Side.Local,"p",1,"victim");var remote=ItemChecks.File(Side.Dropbox,"p",2,"peer");var saved=SyncedEntries.From(old,remote);await store.CommitAsync(pair.PairId,null,saved,null,null,default);pc.History.Add((old,[1]));db.Seed(remote,[2]);
        await using var stage=await pc.StageAsync("p",new MemoryStream([2]),remote,old,default);var note=new RemovalSelection(new(old,"volume",old.ContentHash),saved);await store.ArmAsync(pair.PairId,note,stage.Proof,default);var published=(await pc.PublishAsync(stage,null,null,default)).Final!;var final=SyncedEntries.From(published,remote);await store.CommitAsync(pair.PairId,saved,final,null,null,default);
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(await store.ArmedAsync(pair.PairId,default)==null && store.Records.Values.Single()==final && pc.Requests[nameof(ISide.PublishAsync)]==1,"Committed stage replay did not release its exact note without republishing");
    }
    private static async Task UnboundMoveAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var arrive=ItemChecks.File(Side.Dropbox,"new",3,"id");db.Seed(arrive,[3]);var job=new AcceptedJob(pair.PairId,"files/move_batch","unbound",[new(arrive.Id,"old","new")]);await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(arrive,null,null)])]);
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        bool pending=false;await foreach(var _ in store.JobsAsync(pair.PairId,default))pending=true;Check.Require(pending || store.Records.Values.Any(x=>x.DropboxId==arrive.Id),"Unbound arrival retired without settlement");
    }
    private static async Task TerminalAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var db=new TestDoubles.FakeSide(Side.Dropbox);var pc=new TestDoubles.FakeSide(Side.Local);
        var root=ItemChecks.File(Side.Dropbox,"p",0,"selected",true);var job=new AcceptedJob(pair.PairId,"files/delete_batch/check","token",[new JobMember(root.Id,root.Path,null){Origin=ActionKind.DeleteDropbox}]);
        await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(root,null,null)])]);
        var cloud=FolderDeleteChecks.Observe.On(db,(op,_)=>op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>(("final",null)):null);
        var mark=new JournalPosition(1,42);pc.Pages.Enqueue(new([],null,mark));bool acquired=false,first=false,last=false;var owner=new Unfinished {AcquiredThrough=f=>acquired && f.Local==mark && f.Dropbox=="final"};
        var v=ItemChecks.File(Side.Dropbox,"p/late",2,"child");db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([2]));
        var edit=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false){ConsumeHeld=()=>first=true};var tomb=new Change(Side.Dropbox,ChangeKind.Deleted,v.Path,null,null,null,false,false){ConsumeHeld=()=>last=true};
        Task Visit(Change c,Change[] held)=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),c,pc,cloud,store,_=>held,(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        await Visit(edit,[edit]);Check.Require(!first && pc.Items.Count==0 && db.Items.Count==0,"Terminal published earlier edit before reading pending tombstone");
        acquired=true;await Visit(tomb,[edit,tomb]);await foreach(var _ in store.JobsAsync(pair.PairId,default))throw new InvalidOperationException("Completed terminal job remained persisted");Check.Require(first && last && pc.Items.Count==0 && db.Items.Count==0 && db.Requests[nameof(ISide.ReadJobAsync)]==1,"Last acquired tombstone failed to settle without publishing old bytes");
    }

    private static async Task MixedAsync()
    {
        var pair=FolderDeleteChecks.BoundPair(); using var store=new TestDoubles.InMemoryStateStore();var db=new TestDoubles.FakeSide(Side.Dropbox);var pc=new TestDoubles.FakeSide(Side.Local);
        var folder=ItemChecks.File(Side.Dropbox,"split",0,"selected",true);var child=ItemChecks.File(Side.Dropbox,"split/c",1,"child");db.Seed(folder,[]);db.Seed(child,[1]);
        var job=new AcceptedJob(pair.PairId,"files/delete_batch/check","mixed",[new JobMember("gone","absent",null){Origin=ActionKind.DeleteDropbox},new JobMember(folder.Id,folder.Path,null){Origin=ActionKind.DeleteDropbox},new JobMember("unknown","untouched",null){Origin=ActionKind.DeleteDropbox}]);
        await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,TextKey.FailChanged,FailureDisposition.Permanent),(null,TextKey.FailDropboxTooManyFiles,FailureDisposition.Permanent),(null,null,FailureDisposition.UnknownEffect)]){AbsentOrdinals=new HashSet<int>{0}}]);
        db.Seed(ItemChecks.File(Side.Dropbox,"untouched",8,"unknown"),[8]);var seen=new HashSet<string>();var mark=new JournalPosition(1,42);pc.Pages.Enqueue(new([],null,mark));var owner=new Unfinished{AcquiredThrough=f=>f.Local==mark && f.Dropbox!=null && seen.Contains(f.Dropbox)};
        var side=FolderDeleteChecks.Observe.On(FolderDeleteChecks.DeleteSide(db),(op,_)=>op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>((db.Mutations.Count==0?"initial":"split",null)):null);
        Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,side,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        await Visit();Check.Require(db.Mutations.Count==0,"Terminal size refusal split before acquisition");seen.Add("initial");await Visit();seen.Add("split");await Visit();await Visit();
        Check.Require(db.Mutations.SequenceEqual(new[]{child.Id,folder.Id}) && db.Items["untouched"].Bytes.SequenceEqual(new byte[]{8}) && db.Requests[nameof(ISide.ReadJobAsync)]==1,"Typed refusal not split exactly once or wrong ordinal mutated");
        int pending=0;await foreach(var _ in store.JobsAsync(pair.PairId,default))pending++;Check.Require(pending==1,"Uncertain live neighbour was retired");db.Items.Remove("untouched");
        db.JobReplies[job.JobId].Enqueue(new JobReply.Terminal([(null,TextKey.FailChanged,FailureDisposition.Permanent),(null,TextKey.FailDropboxTooManyFiles,FailureDisposition.Permanent),(null,null,FailureDisposition.UnknownEffect)]){AbsentOrdinals=new HashSet<int>{0}});
        owner=new Unfinished{AcquiredThrough=f=>f.Local==mark && seen.Contains(f.Dropbox!)};await Visit();await Visit();
        Check.Require(db.Mutations.SequenceEqual(new[]{child.Id,folder.Id}) && db.Requests[nameof(ISide.ReadJobAsync)]==2,"Restart resent a settled split member");await foreach(var _ in store.JobsAsync(pair.PairId,default))throw new InvalidDataException("Settled neighbour ordinals remained in launch");
    }

    private static async Task PendingParentAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var root=ItemChecks.File(Side.Dropbox,"p",0,"root",true);db.Seed(root,[]);var saved=SyncedEntries.From(root with {Id="local"},root);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var member=new JobMember(root.Id,root.Path,null){Origin=ActionKind.DeleteDropbox};var first=new AcceptedJob(pair.PairId,"files/delete_batch/check","first",[member]);var last=first with {JobId="last"};
        await store.SaveJobAsync(first,default);db.JobReplies[first.JobId]=new([new JobReply.Terminal([(null,TextKey.FailDropboxTooManyFiles,FailureDisposition.Permanent)])]);
        db.JobReplies[last.JobId]=new([new JobReply.Running(),new JobReply.Terminal([(root,null,null)])]);int sends=0,compares=0;bool acquired=false,ack=false,effect=false;
        var mark=new JournalPosition(1,42);pc.Pages.Enqueue(new([],null,mark));var owner=new Unfinished{AcquiredThrough=f=>f.Local==mark && (f.Dropbox=="before" || acquired && f.Dropbox=="after")};
        async Task<RemovalResult> Send(){sends++;await store.SaveJobAsync(last,default);await owner.InstalledAsync(last,_=>[],_=>Task.CompletedTask);return new(RemovalOutcome.Pending,null,null){Accepted=last};}
        var cloud=FolderDeleteChecks.Observe.On(db,(op,_)=>op==nameof(ISide.DeleteAsync)?Send():op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>((effect?"after":"before",null)):null);
        var tomb=new Change(Side.Dropbox,ChangeKind.Deleted,"p/child",null,null,null,false,false){ConsumeHeld=()=>ack=true};
        Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,cloud,store,_=>[tomb],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>{compares++;return Task.CompletedTask;},default);
        await Visit();await Visit();effect=true;db.Items.Remove(root.Path);await Visit();
        Check.Require(sends==1 && !ack && compares==0,"Pending split settled against its pre-effect capture");acquired=true;await Visit();
        Check.Require(ack && compares==1 && sends==1,"Final accepted parent did not own settlement exactly once");
    }

    public static async Task LiveThirdPathAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var a=ItemChecks.File(Side.Local,"old",3,"local");var b=ItemChecks.File(Side.Dropbox,"old",3,"remote");pc.Seed(a,[3]);db.Seed(b with {Path="third"},[3]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
        var job=new AcceptedJob(pair.PairId,"files/move_batch_v2","token",[new(b.Id,b.Path,"obsolete")]);await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,null,FailureDisposition.UnknownEffect)])]);
        await new Unfinished{CoversThrough=FolderDeleteChecks.Admitted(pc,db)}.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(pc.Items.TryGetValue("third",out var arrived) && arrived.Entry.Id==a.Id && arrived.Bytes.SequenceEqual(new byte[]{3}) && !pc.Items.ContainsKey("old") && !db.Items.ContainsKey("obsolete") && store.Records.Values.Single().LocalPath=="third" && store.Records.Values.Single().DropboxPath=="third" && db.Mutations.Count==0 && db.Requests[nameof(ISide.ReadJobAsync)]==1,"Unknown third-path move did not settle through the current live counterpart");
    }
    private static Task ThirdPathAsync()=>MoveObservation("third");
    private static Task AbsentMoveAsync()=>MoveObservation(null);
    private static Task UnknownAsync()=>MoveObservation("new");
    private static async Task MoveObservation(string? destination)
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var selected=ItemChecks.File(Side.Dropbox,"old",1,"id");var saved=SyncedEntries.From(selected with {Id="pc"},selected);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var job=new AcceptedJob(pair.PairId,"files/move_batch","token",[new(selected.Id,"old","new")]);await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,null,FailureDisposition.UnknownEffect)])]);db.Seed(selected, [1]);var owner=new Unfinished();int compares=0,oldCompares=0;
        Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,after,ct)=>{if(after!.DropboxPath==destination)compares++;else oldCompares++;return Task.CompletedTask;},default);
        await Visit();db.Items.Clear();if(destination!=null)db.Seed(selected with {Path=destination},[1]);await Visit();int pending=0;await foreach(var j in store.JobsAsync(pair.PairId,default)) pending++;
        var exact=store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure && n.Subject.Move is {ExpectedId:"id",Source:"old",Destination:"new"});
        if(destination==null){Check.Require(oldCompares==0 && pending==0 && exact && db.Mutations.Count==0,"Unknown absence retired as an arrival");return;}
        if(destination=="third"){Check.Require(pending==0 && (compares!=0 || exact) && db.Mutations.Count==0,"Third-path move ignored by ordinary decision");return;}
        Check.Require(compares==1 && pending==0 && db.Requests[nameof(ISide.ReadJobAsync)]==1 && db.Requests[nameof(ISide.ReadByIdAsync)]>=2 && db.Mutations.Count==0,"Unknown move was not reobserved to its exact destination");
    }
    private static Task DeletedStageAsync()=>StaleStage(true);
    public static async Task StaleStageAsync(){await StaleStage(true,true);await StaleStage(false); }
    private static async Task StaleStage(bool deleted,bool published=false)
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var old=ItemChecks.File(Side.Local,"f",1,"old");var remote=ItemChecks.File(Side.Dropbox,"f",2,"remote");var saved=SyncedEntries.From(old,remote with {ContentHash=old.ContentHash});await store.CommitAsync(pair.PairId,null,saved,null,null,default);pc.History.Add((old,[1]));if(!deleted) db.Seed(remote with {ContentHash=old.ContentHash},[1]);
        await using var staged=await pc.StageAsync(old.Path,new MemoryStream([2]),remote,old,default);await store.ArmAsync(pair.PairId,new(new(old,"volume",old.ContentHash),saved),staged.Proof,default);
        if(published)
        {
            db.DeletedHistory[remote.Path]=new DeletionEvidence.DeletedFile(remote.Path,pair.Roots!.Dropbox.RootNamespaceId,remote.Id,remote.Revision,DateTimeOffset.UnixEpoch,null,remote.ContentHash,null);
            await pc.PublishAsync(staged,null,null,default);
        }
        var owner=new Unfinished{AcquiredThrough=FolderDeleteChecks.Admitted(pc,db)};
        var endpoint=FolderDeleteChecks.Observe.On(pc,(op,args)=>op==nameof(ISide.ReadByIdAsync) && pc.Items.TryGetValue(old.Path,out var live) && Equals(args[0],live.Entry.Id)?Task.FromResult<(IdentityLocation,bool)?>((new(pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),live.Entry.Path,live.Entry),true)):null);
        await owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,endpoint,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        if(published){Check.Require(!pc.Items.ContainsKey(old.Path) && pc.History.Any(v=>v.Bytes.SequenceEqual(new byte[]{2})) && db.Mutations.Count==0 && store.Records.Count==0 && await store.ArmedAsync(pair.PairId,default)==null,"Published stage deletion did not settle through recycle without upload");return;}
        Check.Require((deleted?!pc.Items.ContainsKey("f"):pc.Items.TryGetValue("f",out var final) && final.Bytes.SequenceEqual(new byte[]{1}) && store.Records.Values.SingleOrDefault()?.LocalId==final.Entry.Id && store.Records.Values.Single().DropboxContentHash==old.ContentHash) && await store.ArmedAsync(pair.PairId,default)==null,deleted?"Deleted source resurrected from obsolete stage or note left armed":"Changed cloud source did not retire obsolete stage for redecision");
        bool discarded=false;try{await using var residual=await pc.ReopenStageAsync(new(new(old,"volume",old.ContentHash),saved),staged.Proof!,default);}catch(ChangedUnderneathException){discarded=true;}
        Check.Require(discarded,"Obsolete stage remained after note settlement");
    }

    private static async Task ComparedFolderAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var local=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var folder=ItemChecks.File(Side.Local,"p",0,"old",true);var saved=SyncedEntries.From(folder,folder with {Id="peer"});var selection=new RemovalSelection(new(folder,"volume",null),saved);
        local.History.Add((folder,[]));await store.CommitAsync(pair.PairId,null,saved,null,null,default);await store.ArmAsync(pair.PairId,selection,null,default);await store.CommitAsync(pair.PairId,saved,null,null,null,default);
        var full=FolderDeleteChecks.Admitted(local,db);bool acquired=false;var owner=new Unfinished{CoversThrough=f=>acquired && full(f)};Task Visit()=>owner.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,local,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>throw new InvalidDataException("Recompared an already settled folder"),default);
        await Visit();Check.Require(await store.ArmedAsync(pair.PairId,default)==null && RecycleUnchecked(store,"p",folder.Id) && local.History.Count==1 && local.History[0].Entry.Id==folder.Id && store.Records.Count==0,"Start-up did not end the waiting folder note with one notice");
        acquired=true;await Visit();Check.Require(await store.ArmedAsync(pair.PairId,default)==null && RecycleUnchecked(store,"p",folder.Id) && local.History.Count==1 && store.Records.Count==0,"A second start-up raised a second notice or changed the bin");
    }
    private static Task PlainRecycleAsync()=>ReplayNote(false);
    private static Task UnboundStageAsync()=>ReplayNote(true);
    private static async Task ReplayNote(bool staged)
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var pc=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);var old=ItemChecks.File(Side.Local,"f",1,"old");var remote=ItemChecks.File(Side.Dropbox,"f",2,"remote");var saved=staged?null:SyncedEntries.From(old,remote);if(saved!=null) await store.CommitAsync(pair.PairId,null,saved,null,null,default);pc.History.Add((old,[1]));
        var victim=new RemovalSelection(new(old,"volume",old.ContentHash),saved);await using var stage=staged?await pc.StageAsync(old.Path,new MemoryStream([2]),remote,old,default):null;
        if(staged)db.Seed(remote,[2]);await store.ArmAsync(pair.PairId,victim,stage?.Proof,default);
        await new Unfinished().CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(await store.ArmedAsync(pair.PairId,default)==null && (staged?store.Records.Values.Single().LocalId==pc.Items[old.Path].Entry.Id:store.Records.Count==0 && pc.Items.Count==0),"Plain recycle or unbound publication remained armed without its exact outcome");
    }

    private static async Task MissingOriginAsync()
    {
        var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var local=new TestDoubles.FakeSide(Side.Local);var db=new TestDoubles.FakeSide(Side.Dropbox);
        var old=ItemChecks.File(Side.Dropbox,"p",0,"selected",true);var child=ItemChecks.File(Side.Dropbox,"p/new",3,"child");db.Seed(old,[]);db.Seed(child,[3]);local.Seed(ItemChecks.File(Side.Local,"p",4,"replacement"),[4]);
        var job=new AcceptedJob(pair.PairId,"files/delete_batch","before-restart",[new(old.Id,old.Path,null)]);await store.SaveJobAsync(job,default);db.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,TextKey.FailDropboxTooManyFiles,FailureDisposition.Permanent)])]);
        local.Pages.Enqueue(new([],null,new(1,42)));db.Pages.Enqueue(new([],"end",null));await new Unfinished{AcquiredThrough=f=>f.Local!=null && f.Dropbox=="end"}.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,local,FolderDeleteChecks.DeleteSide(db),store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
        int remaining=0;await foreach(var _ in store.JobsAsync(pair.PairId,default))remaining++;
        Check.Require(remaining==1 && db.Mutations.Count==0 && db.Items[child.Path].Bytes.SequenceEqual(new byte[]{3}) && local.Items[old.Path].Bytes.SequenceEqual(new byte[]{4}),"Delete wire route authorized splitting a replacement after restart");
    }

    private static async Task PolicyAsync()
    {
        foreach(var origin in new[]{ActionKind.ReplaceKind,ActionKind.MoveDropbox})
        {
            var pair=FolderDeleteChecks.BoundPair();using var store=new TestDoubles.InMemoryStateStore();var local=new TestDoubles.FakeSide(Side.Local);var cloud=new TestDoubles.FakeSide(Side.Dropbox);
            var old=ItemChecks.File(Side.Dropbox,"parent",0,"original",true);cloud.Seed(old,[]);var child=ItemChecks.File(Side.Dropbox,"parent/child",3,"child");cloud.Seed(child,[3]);
            var job=new AcceptedJob(pair.PairId,"files/delete_batch","accepted",[new JobMember(old.Id,old.Path,null){Origin=origin}]);await store.SaveJobAsync(job,default);
            cloud.JobReplies[job.JobId]=new([new JobReply.Terminal([(null,TextKey.FailDropboxTooManyFiles,FailureDisposition.Permanent)])]);
            local.Pages.Enqueue(new([],null,new(1,42)));cloud.Pages.Enqueue(new([],"end",null));await new Unfinished{AcquiredThrough=f=>f.Local!=null && f.Dropbox=="end"}.CheckAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),null,local,FolderDeleteChecks.DeleteSide(cloud),store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default);
            Check.Require(cloud.Mutations.Count==0 && cloud.Items[child.Path].Bytes.SequenceEqual(new byte[]{3}),"Restored policy was replaced with wire-delete splitting");
        }
    }
}
