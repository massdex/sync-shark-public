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
    private static async Task ReviewCases()
    {
        var failures=new List<string>();
        foreach(var test in new[]{"rename","rename-file","unreadable","reset-wrap","reset-id","root","skip","remove","skew"})
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
        foreach(var pair in new[]{a,b}) {await store.SavePairAsync(pair,default); reader.Attach(pair,store,()=>scope,(id,force,ct)=>test=="unreadable"?throw new IOException():id==12?(Path.Join(root,"END"),null):(null,null),p=>new(p,false,1,DateTimeOffset.UnixEpoch,"12","","",test=="skip"?nameof(TextKey.SkipOnlineOnly):null));}
        Task<ChangePage> Fetch(PairConfig pair,ulong n)=>reader.ReadPageAsync(pair,start,new(1,n),settings,default);
        if(test.StartsWith("rename",StringComparison.Ordinal))
        {
            await store.CommitAsync("a",null,new(PathNames.Key("start"),"start","start","start",test=="rename","12",0,0,"d",""),null,null,default);
            pages[10]=[R(11,0x1000,"start",folder:test=="rename")]; pages[20]=[R(21,0x2000,"START",folder:test=="rename"),R(22,0x100,"child",13,12)];
            pages[30]=[R(31,0x1000,"START",folder:test=="rename"),R(32,0x2000,"END",folder:test=="rename")];
            var first=await Fetch(a,20); await reader.SettleAsync(a,first.Position!,store,default); await reader.Membership(b,false,true);
            var bound=await store.PositionAsync("1",default); var second=await Fetch(a,30); var third=await Fetch(a,40);
            Check.Require(first.Changes.Count==0 && bound!.NextUsn<=11 && second.Changes.Any(x=>x.Kind==ChangeKind.Renamed&&x.OldPath=="start"&&x.Path=="START") && second.Changes.Any(x=>x.Id=="13" && x.HistoricalParentId=="12" && x.HistoricalName=="child") && third.Changes.Any(x=>x.Kind==ChangeKind.Renamed&&x.OldPath=="START"&&x.Path=="END"),"OLD escaped or NEW used a later hop"); return;
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
        for(ulong i=20;i<=70;i+=10) await Fetch(a,i);
        int before=reads; await Fetch(b,20); Check.Require(reads>before,"lag replayed from accumulated raw backlog, not NTFS");
    }
}
