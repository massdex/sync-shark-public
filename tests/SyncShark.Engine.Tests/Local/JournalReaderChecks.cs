using Massdex.SyncShark.Engine.Local;
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
    private sealed class Source : UsnJournal
    {
        public JournalPosition Tail=new(1,100);
        public Func<JournalPosition,JournalPosition?,CancellationToken,Task<NtfsUsnPage>> Page=null!;
        public override JournalPosition Current(string root)=>Tail;
        public override Task<NtfsUsnPage> ReadAsync(string root,JournalPosition from,JournalPosition? end,int bytes,CancellationToken ct)=>Page(from,end,ct);
    }
    public class ResetPause : System.Reflection.DispatchProxy
    {
        public IStore Store=null!; public Func<Task> Pause=null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method!.Name!=nameof(IStore.ResetJournalAsync)) return method.Invoke(Store,args);
            return Reset(); async Task Reset(){await Pause(); await (Task)method.Invoke(Store,args)!;}
        }
    }
    private static async Task ReviewCases()
    {
        var failures=new List<string>();
        foreach(var test in new[]{"rename","rename-file","unreadable","settled-hops","reset-wrap","reset-id","root","skip","remove","skew","live-skew","early-write","child-hop","redacted-hop","locate-fails","entry-fails","join-reset","leave-reset","mismatch-fence","backlog16","backlog32"})
        {
            try { await RunCase(test); }
            catch(Exception error) { failures.Add(test+": "+error.GetType().Name+" "+error.Message); }
        }
        Check.Require(failures.Count==0,string.Join("; ",failures));
    }
    private static async Task RunCase(string test)
    {
        using var store=new InMemoryStateStore(); var src=new Source(); int visits=0; var reader=new JournalReader(src,n=>visits+=n);
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
        if(test is "join-reset" or "leave-reset")
        {
            actual=System.Reflection.DispatchProxy.Create<IStore,ResetPause>();
            var proxy=(ResetPause)(object)actual; proxy.Store=store; proxy.Pause=async ()=>{duringReset.TrySetResult(); await completeReset.Task;};
        }
        bool failRead=false;
        void Attach(PairConfig pair)=>reader.Attach(pair,actual,()=>scope,(id,force,ct)=>
            test=="unreadable" || test=="locate-fails" && failRead && id==12 ? throw new IOException() : id switch {
                12=>(Path.Join(root,"END"),null),13 when test=="child-hop"=>(Path.Join(root,"END","child"),null),14=>(Path.Join(root,"independent"),null),_=>(null,null)},
            p=>test=="entry-fails" && failRead && p=="END" ? throw new IOException() : new(p,false,1,DateTimeOffset.UnixEpoch,p=="independent"?"14":p.EndsWith("child",StringComparison.Ordinal)?"13":"12","","",test=="skip"?nameof(TextKey.SkipOnlineOnly):null));
        foreach(var pair in new[]{a,b}) {await store.SavePairAsync(pair,default); Attach(pair);}
        Task<ChangePage> Fetch(PairConfig pair,ulong n)=>reader.ReadPageAsync(pair,start,new(1,n),settings,default);
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
                Check.Require(last.Position==src.Tail && (await store.ReadRepairAsync("a",Side.Local,default)).Revision==1 && await store.PositionAsync("1",default)==src.Tail,"capture with no proved name change never handed off to Repair");return;
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
            if(test=="redacted-hop") {Check.Require(!hop.Changes.Any(x=>x.Path=="END") && (await store.ReadRepairAsync("a",Side.Local,default)).Revision!=0,"redacted hop substituted latest alias");return;}
            if(test=="child-hop") {pages[30]=[R(31,1,"child",13,12)];var child=await Fetch(a,40); Check.Require(child.Changes.Any(x=>x.Path=="middle/child")&&!child.Changes.Any(x=>x.Path=="END/child"),"child escaped physical parent across pages"); return;}
            Check.Require(hop.Changes.Any(x=>x.Kind==ChangeKind.Renamed && x.OldPath=="start" && x.Path=="END" && x.Id=="12") && hop.Changes.Any(x=>x.Path=="independent"),"failed metadata lost exact move or stranded unrelated entry");
            Check.Require((await store.PositionAsync("1",default))?.NextUsn==11,"unacknowledged NEW lost OLD prefix"); return;
        }
        if(test is "join-reset" or "leave-reset")
        {
            src.Page=(_,_,_)=>Task.FromResult(new NtfsUsnPage(NtfsUsnPageStatus.HistoryLost,10,null));
            await reader.Membership(b,true); var detecting=Fetch(a,80); await duringReset.Task;
            if(test=="leave-reset")
            {
                await store.RemovePairAsync(b.PairId,default); var retiring=reader.Membership(b,false,true); completeReset.SetResult(); await retiring;
                Check.Require((await detecting).ContinuityLost && (await store.ReadRepairAsync("a",Side.Local,default)).Revision==1 && (await store.ReadRepairAsync("b",Side.Local,default)).Revision==0 && await store.PositionAsync("1",default)==src.Tail,"departed member prevented atomic reset");return;
            }
            var joined=a with {PairId="joining"}; await store.SavePairAsync(joined,default); Attach(joined);
            completeReset.SetResult(); var fresh=await detecting; var joinSignal=await Fetch(joined,80);
            await reader.Membership(b,false); var resumed=await Fetch(b,80);
            foreach(var id in new[]{"a","b","joining"})Check.Require((await store.ReadRepairAsync(id,Side.Local,default)).Revision==1,"reset omitted repair for "+id);
            Check.Require(fresh.ContinuityLost && joinSignal.ContinuityLost && resumed.ContinuityLost && await store.PositionAsync("1",default)==src.Tail,"reset membership and position diverged"); return;
        }
        if(test.StartsWith("backlog",StringComparison.Ordinal))
        {
            int count=test=="backlog16"?16:32;
            await reader.Membership(b,false,true);
            for(int i=0;i<count;i++){ulong n=(ulong)(10+i*10);pages[n]=[R(n+1,0x1000,"a"+i,(ulong)(100+i)),R(n+2,0x2000,"b"+i,(ulong)(100+i))];await Fetch(a,n+10);}
            for(int i=0;i<count;i++)await reader.SettleAsync(a,new(1,(ulong)(20+i*10)),store,default);
            Console.WriteLine($"rename backlog={count} settlement visits={visits}");
            Check.Require(visits<=count*3,"settlement rescanned retained rename history");return;
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
        if(test=="unreadable") {pages[10]=[R(11,0x100,null)]; var page=await Fetch(a,20);Check.Require(page.Changes.Count==0 && (await store.ReadRepairAsync("a",Side.Local,default)).Revision==1,"unreadable redacted child passed without Repair");return;}
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
