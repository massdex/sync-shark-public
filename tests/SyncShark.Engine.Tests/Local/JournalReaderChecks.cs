using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>A paused member retains its own prefix; settling another member must not acknowledge it.</summary>
public static class JournalReaderChecks
{
    public static async Task RunAsync()
    {
        await ReviewCases();
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            var journal=new UsnJournal(); var start=journal.Current(dir.FullName);
            var identity=new RootIdentity(dir.FullName,"/a","connection",new(1,1,null),new("a","b","c","d"));
            var a=new PairConfig("a","Pair A",dir.FullName,"/a","connection",[],[],FirstRunPolicy.NewestWins,true,null,identity);
            var b=a with { PairId="b",Name="Pair B" };
            using var store=new InMemoryStateStore();
            await store.SavePositionAsync("1",start,default);
            var reader=new JournalReader(); var scope=new PathScope(a,[a],DriveType.Fixed); var settings=SettingsParser.Load();
            var file=Path.Combine(dir.FullName,"change"); File.WriteAllBytes(file,[1]);
            using var held=File.OpenHandle(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var (fileId,_)=UsnJournalChecks.FileRecord(held);
            var id=unchecked((long)fileId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var entry=new Entry("change",false,1,DateTimeOffset.UnixEpoch,id,"","",null);
            foreach(var pair in new[]{a,b}) reader.Attach(pair,store,()=>scope,(key,_,_)=>key==fileId ? (file,null) : (null,null),_=>entry);
            await reader.Membership(b,true);
            var end=journal.Current(dir.FullName);
            Check.Require(end.NextUsn>start.NextUsn,"Fixture did not create journal input");
            ulong? firstRelevant=null;
            async Task ReadToEnd(PairConfig pair)
            {
                ChangePage page; bool projected=false;
                do
                {
                    page=await reader.ReadPageAsync(pair,start,end,settings,default);
                    foreach(var c in page.Changes.Where(c=>c.Id==id)) firstRelevant??=c.Usn;
                    projected |= page.Changes.Any(c=>c.Path==entry.Path && c.Id==id && c.LocalContentDirty && c.JournalId==start.JournalId);
                } while(!page.CaughtUp);
                Check.Require(projected,"Active or resumed member missed its ordered content-write record");
                Check.Require(page.Position==end && !page.ContinuityLost,"Finite read did not reach its own boundary");
                await reader.SettleAsync(pair,end,store,default);
            }
            await ReadToEnd(a);
            reader.Attach(b);
            Check.Require(await store.PositionAsync("1",default)==new JournalPosition(start.JournalId,firstRelevant!.Value),"Paused or reattached member lost its unsettled prefix");
            await reader.Membership(b,false); await ReadToEnd(b);
            Check.Require(await store.PositionAsync("1",default)==end,"Common settled prefix did not advance without another read");
        }
        finally { dir.Delete(true); }
    }
    /// <summary>Integration: the real queue retries the original projected deletion and releases the waiting successor.</summary>
    public static async Task RetryReleasesReplacementAsync()
    {
        var pair=ItemChecks.Pair(); var root=pair.LocalRoot;
        pair=pair with {Roots=new(root,pair.DropboxRoot,pair.ConnectionId,new(1,11,null),new("a","b","c","d"))};
        var paths=new PathScope(pair,[pair],DriveType.Fixed); using var store=new InMemoryStateStore();
        var local=new FakeSide(Side.Local); var cloud=new FakeSide(Side.Dropbox);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,local,cloud,store);
        var old=ItemChecks.File(Side.Local,"item",1,"12"); var peer=ItemChecks.File(Side.Dropbox,"item",1,"peer");
        var successor=ItemChecks.File(Side.Local,"item",2,"13"); local.Seed(successor,[2]); local.Seed(old with {Path="elsewhere"},[1]); cloud.Seed(peer,[1]);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(old,peer),null,null,default);
        var src=new Source {Page=(_,_,_)=>Task.FromResult(new NtfsUsnPage(NtfsUsnPageStatus.Complete,20,null,
            [new(12,11,11,0x200,FileAttributes.Normal,"item"),new(13,11,12,0x100,FileAttributes.Normal,"item")]))};
        var reader=new JournalReader(src); reader.Attach(pair,store,()=>paths,(id,_,_)=>id==12?throw new IOException():(Path.Join(root,"item"),null),_=>successor);
        var page=await reader.ReadPageAsync(pair,new(1,10),new(1,20),ItemChecks.Settings,default);
        using var stop=new CancellationTokenSource(); var parked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished=new List<ulong>(); int refusals=0;
        var admission=queue.AdmitAsync(pair,page,async (change,ct)=>
        {
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,change,local,cloud,store,ItemChecks.Population(pair,paths,local,cloud,store),ItemChecks.Hop(pair,paths,queue,local,cloud,store),ct,queue.Held,(_,reproducible)=>
            { if(!reproducible) { if(change.Usn==11 && Interlocked.Increment(ref refusals)==2) parked.TrySetResult(); throw new WorkQueue.Unsettled(); } });
            lock(finished) finished.Add(change.Usn!.Value);
        },(position,ct)=>reader.SettleAsync(pair,position,store,ct),stop.Token);
        try
        {
            if(await Task.WhenAny(parked.Task,admission)==admission) await admission;
            Check.Require(parked.Task.IsCompletedSuccessfully && !admission.IsCompleted && cloud.Items["item"].Entry==peer,"A present original was not held ahead of the replacement");
            local.Items.Remove("elsewhere");
            var retried=await queue.RetryHeldAsync(new Scope("item","item"),stop.Token);
            const string release="Retry did not release the original deletion before applying the replacement";
            lock(finished) Check.Require(retried && finished.Contains(11UL),release);
            await admission;
            lock(finished) Check.Require(finished.SequenceEqual(new ulong[]{11,12}),release);
            Check.Require(local.Items["item"].Entry.Id==successor.Id && cloud.Items["item"].Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.Single().LocalId==successor.Id,release);
        }
        finally {stop.Cancel(); try {await admission;} catch(OperationCanceledException) when(stop.IsCancellationRequested) {}}
    }

    private sealed class Source : UsnJournal
    {
        public JournalPosition Tail=new(1,100);
        public Func<JournalPosition,JournalPosition?,CancellationToken,Task<NtfsUsnPage>> Page=null!;
        public override (ulong First,JournalPosition Tail) Retained(string root)=>(0,Tail);
        public override Task<NtfsUsnPage> ReadAsync(string root,JournalPosition from,JournalPosition? end,int bytes,CancellationToken ct)=>Page(from,end,ct);
    }
    public class ResetPause : System.Reflection.DispatchProxy
    {
        public IStore Store=null!; public Func<Task> Pause=null!; public bool CrashAfterCommit;
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method!.Name!=nameof(IStore.ResetJournalAsync)) return method.Invoke(Store,args);
            return Reset(); async Task Reset(){await Pause(); await (Task)method.Invoke(Store,args)!; if(CrashAfterCommit) throw new IOException("interrupted after commit");}
        }
    }
    private static async Task ReviewCases()
    {
        var failures=new List<string>();
        foreach(var test in new[]{"rename","rename-file","unreadable","settled-hops","reset-wrap","reset-id","root","skip","remove","skew","live-skew","early-write","child-hop","redacted-hop","locate-fails","entry-fails","join-reset","leave-reset","mismatch-fence","backlog16","backlog32","name-race","after-observation","alias-old","alias-new","alias-unbound","armed-child","link-race","wake-mixed","cold-high","stale-valid","member-bound","armed-missing","armed-substitute","reset-crash","settlement-surface","armed-delete","lookup-hold","entry-hold","failed-order","child-lookup-failed","child-exact-absent","child-exact-substitute","stage-lookup-failure","stage-entry-failure","armed-stage-entry-failure","retired-stage-create","retired-stage-delete"})
        {
            try { await RunCase(test); }
            catch(Exception error) { failures.Add(test+": "+error.GetType().Name+" "+error.Message); }
        }
        Check.Require(failures.Count==0,string.Join("; ",failures));
    }
    private static async Task RunCase(string test)
    {
        using var store=new InMemoryStateStore(); var src=new Source(); var reader=new JournalReader(src);
        var root=Path.Join(Path.GetTempPath(),"Pair A"); var identity=new RootIdentity(root,"/a","test",new(1,11,null),new("a","b","c","d"));
        var a=new PairConfig("a","Pair A",root,"/a","test",[],[],FirstRunPolicy.NewestWins,true,null,identity); var b=a with {PairId="b"};
        var start=new JournalPosition(1,10); var scope=new PathScope(a,[a],DriveType.Fixed); var settings=SettingsParser.Load();
        UsnRecord R(ulong u,uint flags,string? name,ulong id=12,ulong parent=11,bool folder=false)=>new(id,parent,u,flags,folder?FileAttributes.Directory:FileAttributes.Normal,name);
        var pages=new Dictionary<ulong,UsnRecord[]>(); int reads=0;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        src.Page=async (p,end,ct)=>{ ++reads; if(test.StartsWith("reset",StringComparison.Ordinal)) {entered.TrySetResult(); await release.Task.WaitAsync(ct); return new(test=="reset-id"?NtfsUsnPageStatus.JournalChanged:NtfsUsnPageStatus.HistoryLost,p.NextUsn,null);}
            return new(NtfsUsnPageStatus.Complete,p.NextUsn+10,null,pages.GetValueOrDefault(p.NextUsn)??[]);};
        await store.SavePositionAsync("1",start,default);
        IStore actual=store;
        var duringReset=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeReset=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(test is "join-reset" or "leave-reset" or "reset-crash")
        {
            actual=System.Reflection.DispatchProxy.Create<IStore,ResetPause>();
            var proxy=(ResetPause)(object)actual; proxy.Store=store; proxy.CrashAfterCommit=test=="reset-crash"; proxy.Pause=async ()=>{duringReset.TrySetResult(); await completeReset.Task;};
        }
        bool failRead=false;
        void Attach(PairConfig pair)=>reader.Attach(pair,actual,()=>scope,(id,force,ct)=>
            test=="unreadable" || test=="locate-fails" && failRead && id==12 ? throw new IOException() : id switch {
                12=>(Path.Join(root,"END"),null),13 when test=="child-hop"=>(Path.Join(root,"END","child"),null),14=>(Path.Join(root,"independent"),null),_=>(null,null)},
            p=>test=="entry-fails" && failRead && p=="END" ? throw new IOException() : new(p,false,1,DateTimeOffset.UnixEpoch,p=="independent"?"14":p.EndsWith("child",StringComparison.Ordinal)?"13":"12","","",test=="skip"?nameof(TextKey.SkipOnlineOnly):null));
        foreach(var pair in new[]{a,b}) {await store.SavePairAsync(pair,default); Attach(pair);}
        Task<ChangePage> Fetch(PairConfig pair,ulong n)=>reader.ReadPageAsync(pair,start,new(1,n),settings,default);
        if(test is "cold-high" or "stale-valid")
        {
            await store.SavePositionAsync("1",new(1,80),default); src.Tail=new(1,test=="cold-high"?60UL:100UL);
            var result=await Fetch(a,60); var debt=await store.ReadRepairAsync("a",Side.Local,default);
            Check.Require(test=="cold-high" ? result.ContinuityLost && !result.CaughtUp && debt.Revision==1 && await store.PositionAsync("1",default)==src.Tail : result.CaughtUp && result.Position==new JournalPosition(1,80) && debt.Revision==0,"cold retained range or valid obsolete capture misclassified");return;
        }
        if(test=="member-bound")
        {
            var page=await Fetch(a,20); await reader.SettleAsync(a,page.Position!,store,default);
            Check.Require(await reader.SettledJournalAsync(a,default)==page.Position && await reader.SettledJournalAsync(b,default)==start && await store.PositionAsync("1",default)==start,"member settlement is not volume minimum or offered position");return;
        }
        if(test is "wake-mixed" or "unreadable")
        {
            await reader.Membership(b,false,true);
            bool resolved=false;
            reader.Attach(a,actual,()=>scope,(id,_,_)=>id==91 && !resolved ? test=="unreadable" ? throw new IOException() : (null,null) : (Path.Join(root,id==91?"recovered":"independent"),null),
                path=>new(path,false,1,DateTimeOffset.UnixEpoch,path=="recovered"?"91":"14","","",null));
            pages[10]=[R(11,0x100,null,91),R(12,1,"independent",14)];
            var ordinary=await Fetch(a,20); Check.Require(ordinary.Changes is [{Path:"independent",Usn:12}] && !ordinary.ContinuityLost,"mixed page dropped independent work");
            await reader.SettleAsync(a,ordinary.Position!,store,default);
            Check.Require(await reader.SettledJournalAsync(a,default)==new JournalPosition(1,11) && await store.PositionAsync("1",default)==new JournalPosition(1,11),"acknowledging independent work skipped the unclassified USN");
            int beforeWake=reads; var wake=await reader.ReadPageAsync(a,null,null,settings,default);
            Check.Require(wake.ContinuityLost && !ordinary.CaughtUp && reads==beforeWake && (await store.ReadRepairAsync("a",Side.Local,default)).Revision==1,"quiet classification debt entered a native read instead of losing coverage");
            await reader.Membership(a,true); await reader.Membership(a,false); resolved=true; pages[11]=pages[10];
            var replay=await Fetch(a,21);Check.Require(replay.Changes.Any(c=>c is {Path:"recovered",Id:"91",Usn:11}),"rewind did not deliver the original redacted input after classification");
            await reader.SettleAsync(a,replay.Position!,store,default);
            Check.Require(await store.PositionAsync("1",default)==replay.Position,"decisive replay failed to release the held prefix");return;
        }
        if(test is "stage-lookup-failure" or "stage-entry-failure" or "armed-stage-entry-failure" or "retired-stage-create" or "retired-stage-delete")
        {
            var stage=Path.GetFileName(StageNames.For(Path.Join(root,"target")));
            var retired=test.StartsWith("retired-stage-",StringComparison.Ordinal);
            var armed=retired || test=="armed-stage-entry-failure";
            var current=armed ? Path.Join("folder",Path.GetFileName(StageNames.For(Path.Join(root,"target")))) : stage;
            if(armed)
            {
                var folder=new Entry("folder",true,0,DateTimeOffset.UnixEpoch,"98","","",null);
                await store.CommitAsync(a.PairId,null,new("folder","folder","folder","folder",true,"98",0,0,"parent",""),null,null,default);
                await store.ArmAsync(a.PairId,new(new RemovalProof(folder,"1",null),null),null,default);
            }
            reader.Attach(a,actual,()=>scope,(id,_,_)=>id==12 && test=="stage-lookup-failure" ? throw new IOException() : id==12 && retired ? (null,null) : (Path.Join(root,id==12 ? current : "ordinary"),null),
                path=>StageNames.IsStage(PathNames.Name(path)) ? throw new IOException() : new(path,false,1,DateTimeOffset.UnixEpoch,"14","","",null));
            pages[10]=[R(11,test=="retired-stage-delete"?0x200U:0x100U,stage,12,armed?98UL:11UL),R(12,0x100,"ordinary",14)];
            var page=await Fetch(a,20); await reader.SettleAsync(a,page.Position!,store,default);
            Check.Require(page.Changes is [{Path:"ordinary",Id:"14",Usn:12}] && store.OpenNotices.Count==0 &&
                (await store.ReadRepairAsync(a.PairId,Side.Local,default)).Revision==0 && await reader.SettledJournalAsync(a,default)==new JournalPosition(1,20),
                "failed internal stage entered ordinary work, created a notice or held settlement");return;
        }
        if(test.StartsWith("child-",StringComparison.Ordinal) && test!="child-hop")
        {
            var folder=new Entry("folder",true,0,DateTimeOffset.UnixEpoch,"98","","",null);
            await store.ArmAsync(a.PairId,new(new RemovalProof(folder,"1",null),null),null,default);
            reader.Attach(a,actual,()=>scope,(id,_,_)=>id==99 ? test=="child-lookup-failed" ? throw new IOException() : (Path.Join(root,"folder","child"),null) : (null,null),
                path=>test=="child-exact-absent"?null:new(path,false,1,DateTimeOffset.UnixEpoch,"replacement","","",null));
            pages[10]=[R(11,0x200,null,99,98)];var page=await Fetch(a,20);
            Check.Require(page.Changes is [{Kind:ChangeKind.Deleted,Id:"99",Usn:11,Path:null,AffectedPath:"folder",HistoricalParentId:"98",IdentityDeleted:false}],"armed tombstone lost or full identity absence inferred from inconclusive lookup");
            await reader.SettleAsync(a,page.Position!,store,default);
            Check.Require((await reader.SettledJournalAsync(a,default))?.NextUsn==11,"uncertain affected identity was acknowledged");return;
        }
        if(test=="failed-order")
        {
            await store.CommitAsync("a",null,new(PathNames.Key("folder"),"folder","folder","folder",true,"12",0,0,"d",""),null,null,default);
            reader.Attach(a,actual,()=>scope,(id,_,_)=>id==12?throw new IOException():(Path.Join(root,id==13?"folder":"other"),null),
                path=>new(path,path=="folder",0,DateTimeOffset.UnixEpoch,path=="folder"?"13":"14","","",null));
            pages[10]=[R(11,0x200,"folder",folder:true),R(12,0x100,"folder",13,folder:true),R(13,1,"other",14)];
            var changes=(await Fetch(a,20)).Changes;
            Check.Require(changes is [{Path:"folder",Id:"12",Usn:11,Kind:ChangeKind.Deleted,IdentityDeleted:false,Observed:null,HistoricalParentId:"11",HistoricalName:"folder"},{Path:"folder",Id:"13",Usn:12,Kind:ChangeKind.Changed},{Path:"other",Id:"14",Usn:13}],"missing earlier failed footprint ahead of same-name reuse");
            await reader.SettleAsync(a,new(1,20),store,default);
            Check.Require((await reader.SettledJournalAsync(a,default))?.NextUsn==11 && store.OpenNotices.Count(n=>n.Path=="folder")==1,"ordered failure lost the held prefix or duplicated its notice");return;
        }
        if(test=="armed-delete")
        {
            var folder=new Entry("folder",true,0,DateTimeOffset.UnixEpoch,"98","","",null);
            await store.ArmAsync(a.PairId,new(new RemovalProof(folder,"1",null),null),null,default);
            pages[10]=[R(11,1,null,99,98),R(12,0x200,null,99,98)];var p=await Fetch(a,20);
            Check.Require(p.Changes is [{Kind:ChangeKind.Changed,Path:null,AffectedPath:"folder",IdentityDeleted:false},{Kind:ChangeKind.Deleted,Path:null,AffectedPath:"folder",HistoricalParentId:"98",Id:"99",Usn:12,IdentityDeleted:true}],"redacted child tombstone lost positive deletion evidence");return;
        }
        if(test is "lookup-hold" or "entry-hold")
        {
            await reader.Membership(b,false,true); bool unwritable=true;
            await store.CommitAsync("a",null,new(PathNames.Key("folder"),"folder","folder","folder",true,"12",0,0,"d",""),null,null,default);
            reader.Attach(a,actual,()=>scope,(id,_,_)=>id==12 ? test=="lookup-hold" ? unwritable ? throw new IOException() : (null,null) : (Path.Join(root,"folder"),null) : (Path.Join(root,"other"),null),
                path=>path=="folder"&&unwritable ? throw new IOException() : new(path,path=="folder",0,DateTimeOffset.UnixEpoch,path=="folder"?"12":"14","","",null));
            uint reason=test=="lookup-hold"?0x200U:1U;
            pages[10]=[R(11,reason,"folder",folder:true),R(12,1,"other",14)];var first=await Fetch(a,20);
            Check.Require(first.Changes.Any(c=>c.Id=="14") && store.OpenNotices.Count(n=>n.Kind==NoticeKind.Failure && n.Path=="folder")==1,"failure lost its typed notice or blocked independent work");
            await reader.SettleAsync(a,first.Position!,store,default);
            Check.Require((await store.PositionAsync("1",default))?.NextUsn==11,"acknowledged the metadata failure without its ordered evidence");
            var wake=await Fetch(a,20); Check.Require(wake.ContinuityLost,"held metadata input left no ordinary Repair wake");
            await reader.Membership(a,true); await reader.Membership(a,false); unwritable=false; pages[11]=pages[10]; var replay=await Fetch(a,21);
            Check.Require(replay.Changes.Any(c=>c.Id=="12" && c.Usn==11 && c.Kind==(reason==0x200?ChangeKind.Deleted:ChangeKind.Changed)),"original input did not replay after recovery");
            await reader.SettleAsync(a,replay.Position!,store,default);Check.Require(await store.PositionAsync("1",default)==replay.Position,"replayed metadata record pinned the position forever");return;
        }
        if(test=="settlement-surface")
        {
            Check.Require(typeof(JournalReader).GetConstructors().All(c=>c.GetParameters().All(p=>p.ParameterType!=typeof(Action<int>))),"shipping settlement exposes a visit callback");return;
        }
        if(test is "armed-missing" or "armed-substitute")
        {
            var folder=new Entry("folder",true,0,DateTimeOffset.UnixEpoch,"98","","",null); var oldPath="folder/A";
            await store.ArmAsync(a.PairId,new(new RemovalProof(folder,"1",null),null),null,default);
            await store.CommitAsync(a.PairId,null,new(PathNames.Key("folder"),"folder","folder","folder",true,"98",0,0,"parent",""),null,null,default);
            await store.CommitAsync(a.PairId,null,new(PathNames.Key(oldPath),oldPath,oldPath,oldPath,false,"12",0,0,"child",""),null,null,default);
            reader.Attach(a,actual,()=>scope,(_,_,_)=>(Path.Join(root,"folder","A"),null),p=>test=="armed-missing"?null:new(p,false,1,DateTimeOffset.UnixEpoch,"other","","",null));
            pages[10]=[R(11,1,"A",12,98)]; var page=await Fetch(a,20);
            Check.Require(page.Changes is [{Path:null,AffectedPath:"folder",Id:"12",HistoricalParentId:"98",HistoricalName:"A",IdentityDeleted:false,Kind:ChangeKind.Changed}],"armed footprint re-emitted absent name as ordinary work");
            await reader.SettleAsync(a,page.Position!,store,default);Check.Require((await reader.SettledJournalAsync(a,default))?.NextUsn==11,"armed footprint released withheld binding");return;
        }
        if(test=="armed-child")
        {
            var folder=new Entry("folder",true,0,DateTimeOffset.UnixEpoch,"98","","",null);
            await store.ArmAsync(a.PairId,new(new RemovalProof(folder,"1",null),null),null,default);
            pages[10]=[R(11,1,null,99,98),R(12,1,null,100,97)]; var result=await Fetch(a,20);
            Check.Require(result.Changes.Single() is {Path:null,Id:"99",AffectedPath:"folder",HistoricalParentId:"98"},"pathless report did not carry the exact armed parent footprint");return;
        }
        if(test is "alias-old" or "alias-new" or "alias-unbound" or "link-race" or "name-race" or "after-observation")
        {
            bool stale=false; var dirtyOld=test=="alias-old"; var alias=test is "alias-old" or "alias-new" or "alias-unbound";
            foreach(var path in test=="alias-unbound" ? new[]{"A"} : new[]{"A","X"}) await store.CommitAsync("a",null,new(PathNames.Key(path),path,path,path,false,"12",0,0,"d",""),null,null,default);
            await reader.Membership(b,false,true);
            reader.Attach(a,actual,()=>scope,(_,_,_)=>(test is "name-race" or "after-observation")&&!stale?(Path.Join(root,"A"),new[]{Path.Join(root,"A"),Path.Join(root,"X")}):(Path.Join(root,"B"),new[]{Path.Join(root,"B"),Path.Join(root,"X")}),
                path=>test=="name-race"&&path=="A" ? null : new(path,false,1,DateTimeOffset.UnixEpoch,"12","","",null));
            if(!alias)
            {
                pages[10]=[R(11,test=="link-race"?0x10000U:1U,test=="link-race"?"X":"A")];var first=await Fetch(a,20);
                Check.Require(!first.Changes.Any(x=>x.Path=="A" && (x.Kind==ChangeKind.Deleted || x.Observed==null)),"ordinary input authorized work at now-missing A or removed an unrelated alias");
                if(test=="after-observation") Check.Require(first.Changes.Any(x=>x is {Path:"A",Kind:ChangeKind.Changed,Id:"12",IdentityDeleted:false,JournalId:1} && x.Observed?.Id==x.Id && x.HistoricalName=="A"),"source dropped identity at decisive observation handoff");
                stale=true;
            }
            ulong n=alias?10UL:20UL; pages[n]=[R(n+1,0x1000U|(dirtyOld?1U:0U),"A")];pages[n+10]=[R(n+11,0x2000U|(test is "alias-new" or "alias-unbound"?1U:0U),"B")];
            var old=await Fetch(a,n+10);await reader.SettleAsync(a,old.Position!,store,default);var moved=await Fetch(a,n+20);
            Check.Require(moved.Changes.Any(x=>x.Kind==ChangeKind.Renamed && x.OldPath=="A" && x.Path=="B" && (!alias || x.LocalContentDirty)),"move lost dirty lineage or substituted name");
            if(alias) Check.Require(moved.Changes.Count(x=>x.Path=="X" && x.Kind==ChangeKind.Changed && x.Id=="12" && x.LocalContentDirty)==1 && moved.Changes.Count==2,"dirty rename missed or duplicated independent link");
            Check.Require((await store.PositionAsync("1",default))!.NextUsn<=n+1,"unsettled OLD released"); await reader.SettleAsync(a,moved.Position!,store,default);Check.Require(await store.PositionAsync("1",default)==moved.Position,"settled NEW did not release OLD");return;
        }
        if(test=="settled-hops")
        {
            await reader.Membership(b,false,true); pages[10]=[R(11,0x1000,"start")]; pages[20]=[R(21,0x2000,"END")];
            await Fetch(a,20); await reader.SettleAsync(a,new(1,20),store,default);
            var move=await Fetch(a,30); Check.Require(move.Changes.Single().OldPath=="start" && (await store.PositionAsync("1",default))?.NextUsn==11,"reading NEW advanced OLD");
            await reader.Membership(a,true); await reader.Membership(a,false);
            pages[11]=pages[10]; pages[21]=pages[20]; var replay=await Fetch(a,21); await reader.SettleAsync(a,replay.Position!,store,default);
            var resumed=await Fetch(a,31); Check.Require(resumed.Changes.Single().Kind==ChangeKind.Renamed && (await store.PositionAsync("1",default))?.NextUsn==11,"pause/resume released OLD before ack");
            await reader.SettleAsync(a,resumed.Position!,store,default); Check.Require(await store.PositionAsync("1",default)==resumed.Position,"NEW ack did not release OLD");return;
        }
        if(test is "mismatch-fence" or "early-write" or "child-hop" or "redacted-hop" or "locate-fails" or "entry-fails")
        {
            await store.CommitAsync("a",null,new(PathNames.Key("start"),"start","start","start",true,"12",0,0,"d",""),null,null,default);
            await reader.Membership(b,false,true);
            if(test=="mismatch-fence")
            {
                src.Tail=new(1,30); pages[10]=[R(11,0x80000000,"start")];var first=await Fetch(a,20);
                await reader.SettleAsync(a,first.Position!,store,default);Check.Require((await store.PositionAsync("1",default))?.NextUsn==11,"unresolved close escaped before capture");
                var last=await reader.ReadPageAsync(a,null,null,settings,default);await reader.SettleAsync(a,last.Position!,store,default);
                Check.Require(last.Position==src.Tail && (await store.ReadRepairAsync("a",Side.Local,default)).Revision==1,"capture with no proved name change never handed off to Repair");
                Check.Require(await reader.SettledJournalAsync(a,default)==new JournalPosition(1,11) && await store.PositionAsync("1",default)==new JournalPosition(1,11),"requesting Repair released unresolved mismatch 11");
                var wake=await Fetch(a,30);Check.Require(wake.ContinuityLost,"mismatch did not wake Repair");
                await reader.Membership(a,true);await reader.Membership(a,false);
                reader.Attach(a,actual,()=>scope,(id,_,_)=>id==12?(Path.Join(root,"start"),null):(null,null),p=>new(p,true,0,DateTimeOffset.UnixEpoch,"12","","",null));
                pages[11]=pages[10];var replay=await Fetch(a,21);Check.Require(replay.Changes is [{Path:"start",Id:"12",Usn:11}],"mismatch rewind omitted original input");
                await reader.SettleAsync(a,replay.Position!,store,default);Check.Require(await reader.SettledJournalAsync(a,default)==replay.Position && await store.PositionAsync("1",default)==replay.Position,"decisive mismatch replay failed to release its prefix");return;
            }
            if(test=="early-write")
            {
                pages[10]=[R(11,0x80000001,"start")]; var write=await Fetch(a,20);
                Check.Require(write.Changes.All(x=>x.Path!="start" || x.Kind==ChangeKind.Renamed) && !write.Changes.Any(x=>x.Path=="END"),"write before OLD fabricated deletion or premature new-name creation");
                pages[20]=[R(21,0x1000,"start")]; pages[30]=[R(31,0x2000,"START")]; await Fetch(a,30); var renamed=await Fetch(a,40);
                Check.Require(renamed.Changes.Single() is {Kind:ChangeKind.Renamed,OldPath:"start",Path:"START",LocalContentDirty:true},"earlier write not carried into physical rename"); return;
            }
            pages[10]=[R(11,0x1000,"start",folder:true)]; await Fetch(a,20); await reader.SettleAsync(a,new(1,20),store,default);
            var newName=test=="redacted-hop" ? null : test=="child-hop" ? "middle" : "END";
            pages[20]=[R(21,0x2000,newName,folder:true),R(22,1,"independent",14)]; failRead=true; var hop=await Fetch(a,30);
            if(test=="redacted-hop")
            {
                Check.Require(hop.Changes is [{Path:"independent",Usn:22}] && (await store.ReadRepairAsync("a",Side.Local,default)).Revision!=0,"redacted hop lost independent work or substituted latest alias");
                await reader.SettleAsync(a,hop.Position!,store,default);
                Check.Require(await reader.SettledJournalAsync(a,default)==new JournalPosition(1,11) && await store.PositionAsync("1",default)==new JournalPosition(1,11),"independent acknowledgement released OLD before redacted NEW was classified");
                Check.Require((await Fetch(a,30)).ContinuityLost,"unclassified rename did not wake Repair");
                await reader.Membership(a,true);await reader.Membership(a,false);
                pages[11]=pages[10];pages[21]=[R(21,0x2000,"END",folder:true),R(22,1,"independent",14)];
                var oldPage=await Fetch(a,21);await reader.SettleAsync(a,oldPage.Position!,store,default);
                var replay=await Fetch(a,31);Check.Require(replay.Changes.Any(x=>x is {Kind:ChangeKind.Renamed,OldPath:"start",Path:"END",Usn:21}),"rewind did not pair original OLD with classified NEW");
                await reader.SettleAsync(a,replay.Position!,store,default);Check.Require(await reader.SettledJournalAsync(a,default)==replay.Position && await store.PositionAsync("1",default)==replay.Position,"classified rename replay failed to release original OLD");return;
            }
            if(test=="child-hop") {pages[30]=[R(31,1,"child",13,12)];var child=await Fetch(a,40); Check.Require(child.Changes.Any(x=>x.Path=="middle/child")&&!child.Changes.Any(x=>x.Path=="END/child"),"child escaped physical parent across pages"); return;}
            Check.Require(hop.Changes.Any(x=>x.Kind==ChangeKind.Renamed && x.OldPath=="start" && x.Path=="END" && x.Id=="12") && hop.Changes.Any(x=>x.Path=="independent"),"failed metadata lost exact move or stranded unrelated entry");
            Check.Require((await store.PositionAsync("1",default))?.NextUsn==11,"unacknowledged NEW lost OLD prefix"); return;
        }
        if(test is "join-reset" or "leave-reset" or "reset-crash")
        {
            src.Page=(_,_,_)=>Task.FromResult(new NtfsUsnPage(NtfsUsnPageStatus.HistoryLost,10,null));
            await reader.Membership(b,true); var detecting=Fetch(a,80); await duringReset.Task;
            if(test=="leave-reset")
            {
                await store.RemovePairAsync(b.PairId,default); var retiring=reader.Membership(b,false,true); completeReset.SetResult(); await retiring;
                Check.Require((await detecting).ContinuityLost && (await store.ReadRepairAsync("a",Side.Local,default)).Revision==1 && (await store.ReadRepairAsync("b",Side.Local,default)).Revision==0 && await store.PositionAsync("1",default)==src.Tail,"departed member prevented atomic reset");return;
            }
            var joined=a with {PairId="joining"}; await store.SavePairAsync(joined,default); Attach(joined);
            completeReset.SetResult();
            if(test=="reset-crash")
            {
                try {await detecting;throw new InvalidDataException("reset interruption was not exercised");}catch(IOException){}
                // A new reader after a process loss sees only the durable state, not the old reader's pending join list.
                var cursor=await store.PositionAsync("1",default); var debt=await store.ReadRepairAsync(joined.PairId,Side.Local,default);
                Check.Require(cursor==src.Tail && debt.Revision==1,"fresh cursor committed without the joining pair's Repair");return;
            }
            var fresh=await detecting; var joinSignal=await Fetch(joined,80);
            await reader.Membership(b,false); var resumed=await Fetch(b,80);
            foreach(var id in new[]{"a","b","joining"})Check.Require((await store.ReadRepairAsync(id,Side.Local,default)).Revision==1,"reset omitted repair for "+id);
            Check.Require(fresh.ContinuityLost && joinSignal.ContinuityLost && resumed.ContinuityLost && await store.PositionAsync("1",default)==src.Tail,"reset membership and position diverged"); return;
        }
        if(test.StartsWith("backlog",StringComparison.Ordinal))
        {
            int count=test=="backlog16"?16:32;
            await reader.Membership(b,false,true);
            for(int i=0;i<count;i++){ulong n=(ulong)(10+i*10);pages[n]=[R(n+1,0x1000,"a"+i,(ulong)(100+i)),R(n+2,0x2000,"b"+i,(ulong)(100+i))];await Fetch(a,n+10);}
            for(int i=0;i<count;i++)
            {
                var through=new JournalPosition(1,(ulong)(20+i*10));
                await reader.SettleAsync(a,through,store,default);
                Check.Require(await reader.SettledJournalAsync(a,default)==through && await store.PositionAsync("1",default)==through,"backlog did not settle each contiguous prefix");
            }
            return;
        }
        if(test.StartsWith("rename",StringComparison.Ordinal))
        {
            await store.CommitAsync("a",null,new(PathNames.Key("start"),"start","start","start",test=="rename","12",0,0,"d",""),null,null,default);
            pages[10]=[R(11,0x1000,"start",folder:test=="rename")]; pages[20]=[R(21,0x2000,"START",folder:test=="rename"),R(22,0x100,"child",13,12)];
            pages[30]=[R(31,0x1000,"START",folder:test=="rename"),R(32,0x2000,"END",folder:test=="rename")];
            var first=await Fetch(a,20); await reader.SettleAsync(a,first.Position!,store,default); await reader.Membership(b,false,true);
            var bound=await store.PositionAsync("1",default); var second=await Fetch(a,30); var third=await Fetch(a,40);
            Check.Require(first.Changes.Count==0 && bound!.NextUsn<=11 && second.Changes.Any(x=>x.Kind==ChangeKind.Renamed&&x.OldPath=="start"&&x.Path=="START") && second.Changes.Any(x=>x.Id=="13" && x.HistoricalParentId=="12" && x.HistoricalName=="child" && (test!="rename" || x.Path=="START/child")) && third.Changes.Any(x=>x.Kind==ChangeKind.Renamed&&x.OldPath=="START"&&x.Path=="END"),"OLD escaped or NEW used a later hop"); return;
        }
        if(test.StartsWith("reset",StringComparison.Ordinal))
        {
            var one=Fetch(a,80); await entered.Task; var two=Fetch(b,80); src.Tail=new(test=="reset-id"?2UL:1UL,100); release.SetResult();
            var answers=await Task.WhenAll(one,two); var stale=await Fetch(b,80);
            Check.Require(answers.All(x=>x.ContinuityLost && x.Position==src.Tail) && (test!="reset-id" || stale.ContinuityLost) && (await store.ReadRepairAsync("b",Side.Local,default)).Revision==1,"shared reset lost a member or repeated the write");return;
        }
        if(test=="root") {pages[10]=[R(11,0x200,null,11)];try {await Fetch(a,20);}catch(JournalRootChangedException){Check.Require(await store.PositionAsync("1",default)==start,"root event advanced position");return;}throw new InvalidDataException("root signal missing");}
        if(test=="skip") {pages[10]=[R(11,0x100,"END")];var page=await Fetch(a,20);Check.Require(page.Changes.SingleOrDefault()?.Observed?.Skip==nameof(TextKey.SkipOnlineOnly),"eligible skip discarded");return;}
        if(test=="remove") {var page=await Fetch(a,20);await reader.SettleAsync(a,page.Position!,store,default);await reader.Membership(b,false,true);Check.Require(await store.PositionAsync("1",default)==page.Position,"removal left obsolete hold");return;}
        if(test=="live-skew")
        {
            src.Tail=new(1,40); var finite=src.Page; int waits=0;
            src.Page=async (p,end,ct)=>{if(end is null && ++waits==1){entered.SetResult(); await release.Task.WaitAsync(ct);} return await finite(p,end,ct);};
            for(ulong i=20;i<=40;i+=10) await Fetch(a,i);
            var waiting=reader.ReadPageAsync(a,null,null,settings,default); await entered.Task;
            try { var lag=await reader.ReadPageAsync(b,null,null,settings,default); Check.Require(waits==1 && lag.Position?.NextUsn==20,"lag started a second native wait on the volume"); }
            finally {release.SetResult(); await waiting;}
            return;
        }
        for(ulong i=20;i<=70;i+=10) await Fetch(a,i);
        int before=reads; await Fetch(b,20); Check.Require(reads>before,"lag replayed from accumulated raw backlog, not NTFS");
    }
}
