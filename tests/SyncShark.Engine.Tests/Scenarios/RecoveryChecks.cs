using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Recovery and notice checks against populated pairs; fixture setup does not perform the operation under test.</summary>
public static class RecoveryChecks
{
    private sealed class PairState : IDisposable
    {
        internal PairConfig Pair { get; }
        internal FakeSide Pc { get; }
        internal PathScope Paths { get; }
        internal PairState(PairConfig? pair=null) { Pair=pair??ItemChecks.Pair();Paths=new(Pair,[Pair],DriveType.Fixed);Pc=new FakeSide(Side.Local,async proof=>(await Store.ArmedAsync(Pair.PairId,default))?.Stage==proof); }
        internal PairRunner Runner { get; } = new();
        internal WorkQueue Queue { get; private set; } = null!;
        internal async Task StartAsync() => Queue=await ItemChecks.StartQueueAsync(Pair,Paths,Pc,Cloud,Store,Runner);
        internal FakeSide Cloud { get; } = new(Side.Dropbox);
        internal InMemoryStateStore Store { get; } = new();
        internal async Task<SyncedEntry> LinkAsync(string path, byte value)
        {
            var pc = ItemChecks.File(Side.Local,path,value,"pc-"+path); var cloud = ItemChecks.File(Side.Dropbox,path,value,"db-"+path);
            Pc.Seed(pc,[value]); Cloud.Seed(cloud,[value]); var row=SyncedEntries.From(pc,cloud);
            await Store.CommitAsync(Pair.PairId,null,row,null,null,default); return row;
        }
        internal async Task InitializeAsync() { await Store.SavePairAsync(Pair,default); await StartAsync(); await Store.FinishRepairAsync(Pair.PairId,(0,await Store.OweRepairAsync(Pair.PairId,Side.Dropbox,"requested",default)),"{}",default); }
        internal Task CompareAsync() => Repair.RunAsync(Pair,ItemChecks.Settings,_=>{},Paths,Queue,Pc,Cloud,Store,null,null,default);
        internal void VerifyLinks()
        {
            foreach(var row in Store.Records.Values)
                Check.Require(Pc.Items.TryGetValue(row.LocalPath,out var a) && Cloud.Items.TryGetValue(row.DropboxPath,out var b) &&
                    row == SyncedEntries.From(a.Entry,b.Entry) && a.Bytes.SequenceEqual(b.Bytes),"Mismatched linked versions");
        }
        public void Dispose() => Store.Dispose();
    }

    public static async Task KeptVersionsAsync(bool folder)
    {
        using var x=new PairState(); await x.InitializeAsync();
        var pc=ItemChecks.File(Side.Local,"same",1,"pc");
        var cloud=ItemChecks.File(Side.Dropbox,"same",2,"cloud",folder);
        if(!folder)
        {
            pc=pc with { Modified=new DateTimeOffset(2020,5,17,8,37,0,TimeSpan.Zero) };
            cloud=cloud with { Modified=pc.Modified };
        }
        x.Pc.Seed(pc,[1]);
        x.Cloud.Seed(cloud,folder?[]:[2]);
        if(folder) x.Cloud.Seed(ItemChecks.File(Side.Dropbox,"same/quiet-child",3,"child"),[3]);
        await x.CompareAsync();
        x.VerifyLinks();
        foreach(var side in new[]{x.Pc,x.Cloud})
        {
            Check.Require(side.Items.Count==(folder?3:2), "Unexpected labels or orphan content");
            Check.Require(side.Items.Values.Where(a=>!a.Entry.IsFolder).Select(a=>a.Bytes.Single()).Order().SequenceEqual(folder?new byte[]{1,3}:new byte[]{1,2}),"A version was lost");
            if(folder) Check.Require(side.Items.Values.Single(a=>a.Bytes.SequenceEqual(new byte[]{3})).Entry.Path.EndsWith("/quiet-child",StringComparison.Ordinal),"Missing quiet descendant");
        }
        if(!folder)
        {
            var expected=$"same [Local version {Environment.MachineName} 17 May 08.37]";
            Check.Require(x.Cloud.Items.Any(item=>string.Equals(item.Key,expected,StringComparison.Ordinal) && item.Value.Bytes.SequenceEqual(new byte[]{1})),
                "Original PC bytes were not published at the exact PC-labelled Dropbox path");
        }
        Check.Require(x.Store.Records.Count==(folder?3:2) && x.Store.OpenNotices.Single().Cause==TextKey.InfoKeptBoth,"Keep-both notice or binding missing");
    }
    public static async Task ChurnAsync()
    {
        using var x=new PairState(); await x.InitializeAsync(); await x.LinkAsync("item",1);
        foreach(byte value in new byte[]{2,3,4})
        {
            var edit=ItemChecks.File(Side.Local,"item",value,"pc-item");x.Pc.Items[edit.Path]=(edit,[value]);
            await ItemSync.ApplyAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,new(Side.Local,ChangeKind.Changed,edit.Path,null,edit.Id,edit,true,false),x.Pc,x.Cloud,x.Store,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
            Check.Require(x.Cloud.Items[edit.Path].Bytes.SequenceEqual(new[]{value}),"Edit did not reach the counterpart"); x.VerifyLinks();
        }
        Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Store.OpenNotices.Count==0,"Churn created a spurious conflict");
    }
    public static async Task ConcurrentRepairAsync()
    {
        using var x=new PairState(); await x.InitializeAsync();await x.LinkAsync("item",1);
        var edit=ItemChecks.File(Side.Local,"item",2,"pc-item"); x.Pc.Items[edit.Path]=(edit,[2]);
        await Task.WhenAll(x.CompareAsync(),AutoSync.ApplyPageAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,x.Queue,Side.Local,new([new(Side.Local,ChangeKind.Changed,edit.Path,null,edit.Id,edit,true,false)],null,new(1,2)),x.Pc,x.Cloud,x.Store,default));
        Check.Require(x.Cloud.Requests[nameof(ISide.PublishAsync)]==1 && x.Cloud.Items[edit.Path].Bytes.SequenceEqual(new byte[]{2}),"Duplicate or missing write");
        x.VerifyLinks(); Check.Require(x.Store.OpenNotices.Count==0 && x.Store.Records.Count==1,"Incorrect settlement");
    }
    public static async Task RepeatRepairAsync()
    {
        using var x=new PairState();await x.Store.SavePairAsync(x.Pair,default);await x.StartAsync();
        x.Pc.Seed(ItemChecks.File(Side.Local,"steady",1,"steady-id"),[1]);x.Pc.Seed(ItemChecks.File(Side.Local,"edit",1,"edit-id"),[1]);
        await x.CompareAsync();x.VerifyLinks();Check.Require(x.Store.Records.Count==2 && (await x.Store.RepairStateAsync(x.Pair.PairId,default)).FirstRepairDone,"First Repair did not link both files");
        var before=x.Cloud.Items["steady"];long writes=x.Cloud.Requests[nameof(ISide.PublishAsync)];long commits=x.Store.Requests[nameof(IStore.CommitAsync)];
        x.Pc.Items["edit"]=(ItemChecks.File(Side.Local,"edit",2,"edit-id"),[2]);await x.CompareAsync();x.VerifyLinks();
        Check.Require(x.Cloud.Items["edit"].Bytes.SequenceEqual(new byte[]{2}) && x.Cloud.Items["steady"]==before && x.Cloud.Requests[nameof(ISide.PublishAsync)]==writes+1 && x.Store.Requests[nameof(IStore.CommitAsync)]==commits+1,"Repeat Repair missed the edit or rewrote unchanged work");
    }
    public static async Task ClosedNoticeAsync(bool legacy)
    {
        using var x=new PairState(); await x.InitializeAsync();await x.LinkAsync("item",1);
        var subject=new IssueSubject([new(Side.Local,"item","pc-item"),new(Side.Dropbox,"item","db-item")],null);
        var notice=new Notice(1,legacy?NoticeKind.Tie:NoticeKind.Failure,legacy?TextKey.TieEqualTimes:TextKey.FailChanged,"item",subject,DateTimeOffset.UnixEpoch,false);
        await x.Store.SaveNoticeAsync(x.Pair.PairId,notice,default);
        if(legacy) { await x.CompareAsync(); Check.Require(x.Store.OpenNotices.Single().Matches(notice),"Matching legacy tie was silently released"); }
        x.Pc.Items.Clear(); x.Cloud.Items.Clear(); await x.CompareAsync();
        Check.Require(x.Pc.Items.Count==0 && x.Cloud.Items.Count==0 && x.Store.Records.Count==0 && x.Store.OpenNotices.Count==0,"Fully absent subjects were resurrected or retained");
    }
    public static async Task QuietJobAsync()
    {
        using var x=new PairState(); await x.InitializeAsync(); await x.LinkAsync("unrelated",1);
        var job=new AcceptedJob(x.Pair.PairId,"files/delete_batch/check","token",[new("selected","gone",null)]);
        await x.Store.SaveJobAsync(job,default); x.Cloud.JobReplies[job.JobId]=new Queue<JobReply>([new JobReply.Running()]);
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,null,x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        var pending=new List<AcceptedJob>();await foreach(var p in x.Store.JobsAsync(x.Pair.PairId,default)) pending.Add(p);
        Check.Require(pending.Single()==job && x.Cloud.Requests[nameof(ISide.ReadJobAsync)]==1 && x.Store.OpenNotices.Count==0,"Running token lost or repeatedly polled");
        x.VerifyLinks(); Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Store.Records.Count==1,"Quiet job modified unrelated data");
    }
    public static async Task AlteredStageAsync()
    {
        using var x=new PairState(); await x.InitializeAsync();var saved=await x.LinkAsync("item",1);
        var previous=x.Pc.Items["item"].Entry; x.Pc.Items.Clear();
        var incoming=ItemChecks.File(Side.Dropbox,"item",2,"db-item");x.Cloud.Items["item"]=(incoming,[2]);
        var altered=ItemChecks.File(Side.Local,"item.stage",3,"stage-id");
        await using var staged=await x.Pc.StageAsync("item",new MemoryStream(new byte[]{3}),altered,previous,default);
        x.Pc.History.Add((previous,new byte[]{1}));
        var actual=staged.Proof!;
        await x.Store.ArmAsync(x.Pair.PairId,new(new(previous,"volume",previous.ContentHash),saved),new(actual.Path,actual.VolumeId,actual.FileId,actual.Size,incoming.ContentHash),default);
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,null,x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        Check.Require(x.Pc.Requests[nameof(ISide.ReopenStageAsync)]>0 && (await x.Store.ArmedAsync(x.Pair.PairId,default)!=null || x.Store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure)),"Altered stage was neither verified nor retained for recovery");
        Check.Require(!x.Pc.Items.Values.Any(v=>v.Entry.Path=="item" && v.Bytes.SequenceEqual(new byte[]{3})) && x.Cloud.Items["item"].Bytes.SequenceEqual(new byte[]{2}),"Altered stage published or incoming bytes destroyed");
        Check.Require(!x.Store.Records.Values.Any(v=>v.LocalId==actual.FileId && v.DropboxContentHash==incoming.ContentHash),"Unverified stage linked");
        Check.Require(x.Store.OpenNotices.All(n=>n.Kind==NoticeKind.Failure),"Unrelated notice produced during recovery");
    }
    public static async Task FirstDeletedAsync(bool folder, bool measured)
    {
        using var x=new PairState();await x.Store.SavePairAsync(x.Pair,default);await x.StartAsync();
        const string path="formerly-deleted";var entry=ItemChecks.File(Side.Local,path,1,"pc",folder);x.Pc.Seed(entry,folder?[]:[1]);
        x.Cloud.Seed(ItemChecks.File(Side.Dropbox,"unrelated",1,"other"),[1]);
        x.Pc.Seed(ItemChecks.File(Side.Local,"unrelated",1,"pc-other"),[1]);
        x.Cloud.ClockOffset=measured?(TimeSpan.Zero,TimeSpan.Zero):null;
        x.Cloud.DeletedHistory[path]=folder?new DeletionEvidence.DeletedPath(path,path):
            new DeletionEvidence.DeletedFile(path,"cloud","previous","r1",entry.Modified.AddDays(1),null,entry.ContentHash,null);
        await x.CompareAsync();
        Check.Require(x.Cloud.Requests[nameof(ISide.DeletedAsync)]==1,"Ineligible live name received a history lookup or eligible name was missed");
        if(folder || measured)
        {
            Check.Require(!x.Pc.Items.ContainsKey(path) && !x.Cloud.Items.ContainsKey(path) && x.Pc.History.Single().Entry==entry,"First-sync recycle did not preserve selected version");
            Check.Require(x.Store.OpenNotices.Count==0 && x.Store.Records.Count==1,"False notice or retained binding");
        }
        else
        {
            Check.Require(x.Pc.Items[path].Bytes.SequenceEqual(new byte[]{1}) && x.Cloud.Items[path].Bytes.SequenceEqual(new byte[]{1}),"Absent clock proof lost a copy");
            x.VerifyLinks(); Check.Require(x.Store.Records.Count==2 && x.Store.OpenNotices.Single().Cause==TextKey.InfoEditBeatsDelete,"First-sync preservation notice missing");
        }
    }
    public static async Task RefusedRemovalAsync(bool recycle)
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("item",1);
        var saved=x.Store.Records.Values.Single();var entry=x.Pc.Items["item"].Entry;
        x.Cloud.Items.Clear();var error=new ItemFailedException("item",recycle?TextKey.RecycleUnavailable:TextKey.ReadLocalFailed,null);
        if(recycle)x.Pc.RecycleRefusals["item"]=error; else x.Pc.ReadFailures["item"]=error;
        var change=new Change(Side.Dropbox,ChangeKind.Deleted,"item",null,saved.DropboxId,null,false,true);
        if(recycle)
        {
            await ItemChecks.CloudChangeAsync(x.Pair,x.Paths,x.Queue,change,x.Pc,x.Cloud,x.Store,expectEffect:false);
            Check.Require(x.Pc.Requests[nameof(ISide.DeleteAsync)]==1,"Recycle veto was not invoked");
        }
        else
            await ItemSync.ApplyAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,change,x.Pc,x.Cloud,x.Store,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        Check.Require(x.Pc.Items.Count==1 && x.Pc.Items["item"].Entry==entry && x.Pc.Items["item"].Bytes.SequenceEqual(new byte[]{1}) && x.Cloud.Items.Count==0,"Refusal discarded data");
        Check.Require(x.Pc.History.Count==0 && x.Store.Records.Values.Single()==saved && x.Store.OpenNotices.Single().Cause==error.Cause,"Failed effect was settled or hidden");
    }
    public static async Task SafeSaveAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("item",1);
        var next=ItemChecks.File(Side.Local,"item",2,"temporary-file-id");x.Pc.Items["item"]=(next,[2]);
        await x.Queue.AdmitAsync(x.Pair,new ChangePage([
            new(Side.Local,ChangeKind.Deleted,"item",null,"pc-item",null,false,true,1,JournalId:1),
            new(Side.Local,ChangeKind.Renamed,"item","x.tmp",next.Id,next,true,false,2,JournalId:1)],null,new(1,3)),
            (change,ct)=>ItemSync.ApplyAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,change,x.Pc,x.Cloud,x.Store,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),ct,x.Queue.Held),x.Pc.SettleJournalAsync,default);
        x.VerifyLinks();Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Cloud.Items["item"].Bytes.SequenceEqual(new byte[]{2}),"Safe-save bytes or endpoint lost");
        Check.Require(x.Cloud.Requests[nameof(ISide.DeleteAsync)]==0 && x.Store.OpenNotices.Count==0 && x.Store.Records.Count==1,"Safe save executed as delete/create");
    }
    public static async Task OccupiedDestinationAsync(bool folder)
    {
        using var x=new PairState();await x.InitializeAsync();
        Entry Src(Side side,string path,string id,byte value,bool isFolder=false)=>ItemChecks.File(side,path,value,id,isFolder);
        var a=Src(Side.Local,"old","moving-a",1,folder);var b=Src(Side.Dropbox,"old","moving-b",1,folder);
        await x.Store.CommitAsync(x.Pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
        var moved=a with { Path="dest" };x.Pc.Seed(moved,folder?[]:[1]);x.Cloud.Seed(b,folder?[]:[1]);
        var occupant=Src(Side.Dropbox,"dest","existing",1,folder);x.Cloud.Seed(occupant,folder?[]:[1]);
        if(folder) { x.Pc.Seed(Src(Side.Local,"dest/new","child-a",2),[2]);x.Cloud.Seed(Src(Side.Dropbox,"dest/other","child-b",3),[3]); }
        await x.CompareAsync();x.VerifyLinks();
        foreach(var side in new[]{x.Pc,x.Cloud})
        {
            Check.Require(!side.Items.ContainsKey("old") && side.Items["dest"].Entry.IsFolder==folder,"Obsolete source or missing destination");
            if(folder)Check.Require(side.Items["dest/new"].Bytes.SequenceEqual(new byte[]{2}) && side.Items["dest/other"].Bytes.SequenceEqual(new byte[]{3}),"Merge dropped a child");
            else Check.Require(side.Items["dest"].Bytes.SequenceEqual(new byte[]{1}),"Identical occupant changed");
        }
        Check.Require(x.Store.Records.Count==(folder?3:1) && x.Store.OpenNotices.Count==0,"Occupied destination settlement wrong");
    }

    public static async Task EmptyPageAsync()
    {
        using var x=new PairState(); await x.InitializeAsync();await x.LinkAsync("steady",1);
        await x.Store.SaveCursorAsync(x.Pair.PairId,"before",default);
        await AutoSync.ApplyPageAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,x.Queue,Side.Dropbox,new([],"after",null),x.Pc,x.Cloud,x.Store,default);
        Check.Require(await x.Store.CursorAsync(x.Pair.PairId,default)=="after", "Irrelevant page blocked the cursor");
        x.VerifyLinks(); Check.Require(x.Store.Records.Count==1 && x.Store.OpenNotices.Count==0 && x.Cloud.Requests[nameof(ISide.ReadOneAsync)]==0,"Empty page touched data");
    }
    public static async Task RequestWaitAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var clock=new FakeTimeProvider(DateTimeOffset.UnixEpoch);var delay=TimeSpan.FromSeconds(7);
        var events=new List<RunEvent>();
        var waiting=new global::Massdex.SyncShark.Engine.Dropbox.RateLimit(ItemChecks.Settings.InitialLimiterLevel,ItemChecks.Settings.Backoff,events.Add).WaitAsync(delay,clock,default);
        Check.Require(!waiting.IsCompleted && clock.ScheduledDelay==delay,"Provider wait ignored or replaced");
        clock.ElapseScheduledDelay();await waiting;x.VerifyLinks();
        Check.Require(events.Any(e=>e.Kind==RunEventKind.Wait),"Provider wait missing from the run log");
        Check.Require(x.Store.OpenNotices.Count==0 && x.Store.Records.Count==1,"A provider wait created an item failure");
    }
    public static async Task RootReplacementAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var roots=new RootIdentity(x.Pair.LocalRoot,x.Pair.DropboxRoot,x.Pair.ConnectionId,new(1,1,null),new("account","namespace","home","saved"));
        var pair=x.Pair with { Roots=roots };await x.Store.SavePairAsync(pair,default);
        var observed=roots with { Dropbox=roots.Dropbox with { FolderId="other" } };
        bool ready=await x.Runner.CheckRootsAsync(pair,observed,pair.Drive,x.Store,default);
        Check.Require(!ready && (await x.Store.PairAsync(pair.PairId,default))?.Roots==roots && x.Store.OpenNotices.Single().Kind==NoticeKind.RootIdentity,"Different folder silently rebound");
        x.VerifyLinks();Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Store.Records.Count==1,"Root refusal changed data");
    }
    public static async Task PauseAsync()
    {
        using var store = new InMemoryStateStore();
        var settings = SettingsParser.Load();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(store), (_,opened,_,_) => ReferenceEquals(opened,store) ? (new FakeSide(Side.Local),new FakeSide(Side.Dropbox)) : throw new InvalidOperationException("Wrong adapter store"), settings, null!, TimeProvider.System, _=>throw new InvalidOperationException(), (_,_)=>throw new InvalidOperationException());await host.PauseAsync("pair-a",default);
        Check.Require(host.IsPaused("pair-a") && !host.IsPaused("pair-b"),"Pair pause leaked to another pair");
        await host.PauseAsync(null,default);await host.ResumeAsync("pair-a",default);
        Check.Require(host.IsPaused("pair-a") && host.IsPaused("pair-b"),"Pair resume overrode global pause");
        await host.ResumeAsync(null,default);Check.Require(!host.IsPaused("pair-a") && !host.IsPaused("pair-b"),"Global resume left an unexpected hold");
    }
    public static async Task BatchSelectionAsync(int count)
    {
        var wire=new DeleteWire();using var http=new HttpClient(wire);
        var api=new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http,TimeProvider.System,ItemChecks.Settings,_=>{},_=>ValueTask.FromResult<string?>("synthetic"),"test-namespace");
        using var store=new InMemoryStateStore();
        var dispatcher=new global::Massdex.SyncShark.Engine.Dropbox.Dispatcher(api, store);
        var members=Enumerable.Range(0,count).Select(i=>(Member: new JobMember($"id:selected-{i}",$"member-{i}",null), ExpectedRevision: (string?)$"rev-{i}", Upload: (global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged?)null)).ToArray();
        // Drain yields onto the same exclusive scheduler, behind this entire submission loop.
        var ready=new ConcurrentExclusiveSchedulerPair();
        var replies=await Task.Factory.StartNew(()=>Task.WhenAll(members.Select(m=>dispatcher.SubmitAsync("pair","files/delete_v2",m.Member,m.ExpectedRevision,false,m.Upload,default))),
            default,TaskCreationOptions.None,ready.ExclusiveScheduler).Unwrap();
        ready.Complete();
        if(count>1)
        {
            var saved=new List<AcceptedJob>(); await foreach(var job in store.JobsAsync("pair",default)) saved.Add(job);
            Check.Require(saved.Count==1 && saved[0].Launch.SequenceEqual(members.Select(m=>m.Member)) && replies.All(x=>x.Accepted==saved[0] && x.MemberReply is JobReply.Running),"Lost batch custody or treated Running as success");
        }
        Check.Require(wire.Paths.Count==1 && wire.Paths[0].EndsWith(count==1?"/files/delete_v2":"/files/delete_batch",StringComparison.Ordinal),"Wrong number or shape of deletion requests");
        using var body = System.Text.Json.JsonDocument.Parse(wire.Bodies.Single());
        var entries = count == 1 ? new[]{body.RootElement} : body.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Check.Require(entries.Length == members.Length, "Wrong number of delete members");
        for(int i=0;i<entries.Length;i++)
            Check.Require(entries[i].GetProperty("path").GetString()==members[i].Member.Id && entries[i].GetProperty("parent_rev").GetString()==members[i].ExpectedRevision,"Lost selected identity or revision");
    }
    /// <summary>A sole small file replacing a folder must close a session containing its bytes before the selected folder can be removed.</summary>
    public static async Task StageBeforeFolderAsync()
    {
        var wire=new DeleteWire();using var http=new HttpClient(wire);var api=new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http,TimeProvider.System,ItemChecks.Settings,_=>{});
        using var bytes=new MemoryStream(new byte[]{42});var file=ItemChecks.File(Side.Local,"item",42,"pc");var folder=ItemChecks.File(Side.Dropbox,"item",0,"db",true);
        await using var staged=await new global::Massdex.SyncShark.Engine.Dropbox.UploadContent().StageAsync(api,file.Path,bytes,file,folder,default);
        Check.Require(staged.SessionId=="session" && !staged.ConsumesSourceOnPublish && staged.ContentHash==file.ContentHash && wire.ClosedUpload && wire.Bodies.Any(b=>b=="*"),"Selected folder replacement returned before staging the closed upload");
    }
    private sealed class DeleteWire : HttpMessageHandler
    {
        internal List<string> Paths { get; }=[];
        internal List<string> Bodies { get; }=[];
        internal bool ClosedUpload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            if(Paths[^1].Contains("/upload_session/",StringComparison.Ordinal))
            {
                if(request.Headers.TryGetValues("Dropbox-API-Arg",out var args)) { using var doc=System.Text.Json.JsonDocument.Parse(args.Single());ClosedUpload|=doc.RootElement.TryGetProperty("close",out var close) && close.GetBoolean(); }
                return new(System.Net.HttpStatusCode.OK){Content=new StringContent(Paths[^1].EndsWith("/start",StringComparison.Ordinal)?"{\"session_id\":\"session\"}":"null")};
            }
            var json=Paths[^1].EndsWith("delete_batch",StringComparison.Ordinal)?"""{".tag":"async_job_id","async_job_id":"token"}""":"""{"metadata":{".tag":"folder","name":"member-0","id":"id:selected-0","path_display":"/member-0","path_lower":"/member-0"}}""";
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")};
        }
    }

    public static async Task A13R1ThenR2KeepLastVersionAsync()
    {
        var p=ItemChecks.Pair();
        using var x=new PairState(p with { Roots=new(p.LocalRoot,p.DropboxRoot,p.ConnectionId,new(1,1,null),new("account","namespace","home","root")) });
        await x.InitializeAsync();const string parent="parent",child="parent/item";
        var pcRoot=ItemChecks.File(Side.Local,parent,0,"pc-root",true);var dbRoot=ItemChecks.File(Side.Dropbox,parent,0,"db-root",true);
        var binding=SyncedEntries.From(pcRoot,dbRoot);await x.Store.CommitAsync(x.Pair.PairId,null,binding,null,null,default);x.Cloud.Seed(dbRoot,[]);
        var r1=ItemChecks.File(Side.Dropbox,child,1,"child-id");var r2=ItemChecks.File(Side.Dropbox,child,2,r1.Id);
        foreach(var v in new[]{r1,r2}) x.Cloud.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream(new[]{(byte)(v==r1?1:2)}));
        x.Cloud.Seed(r2,[2]);x.Pc.PublishFailures[child]=new ItemFailedException(child,TextKey.FailLocalInUse,null);
        var first=new Change(Side.Dropbox,ChangeKind.Changed,child,null,r1.Id,r1,false,false);var second=first with {Observed=r2};
        var deletion=new Change(Side.Local,ChangeKind.Deleted,parent,null,pcRoot.Id,null,false,true,1,JournalId:1);
        var populate=ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store);var hop=ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store);
        Task? children=null;int independent=0;bool retried=false;
        var local=await ItemChecks.ReadPrefixAsync(x.Pc,ItemChecks.Journal([deletion],2),default);
        await x.Queue.AdmitAsync(x.Pair,local,async (_,ct)=>{
            var remote=await ItemChecks.ReadPrefixAsync(x.Cloud,new([first,second],"after",null),ct);
            children=x.Queue.AdmitAsync(x.Pair,remote,(_,_)=>{Interlocked.Increment(ref independent);return Task.CompletedTask;},null,ct);
            var coverage=FolderChecks.RemovalCoverage(x.Pc,x.Cloud,Side.Dropbox,dbRoot,local,remote,ct,out var verified);
            var result=await new FolderDelete().ApplyAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,binding,Side.Local,x.Pc,x.Cloud,x.Store,x.Queue.Held,populate,hop,ct,coversThrough:coverage);
            Check.Require(verified(),"Missing bounded Dropbox removal echo");
            Check.Require(result.Outcome==RemovalOutcome.Retained && x.Cloud.History.Any(h=>h.Entry==dbRoot),"Failed put-back did not retain settlement after the selected physical removal");
            var pending=x.Store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack && n.Path==child);
            Check.Require(x.Queue.Held(new(parent,parent)).Any(c=>c.Observed==r2 && c.ConsumeHeld!=null),"Failed put-back released its outstanding version");
            x.Pc.PublishFailures.Remove(child);
            var retained=x.Cloud.History.Single(h=>h.Entry==r2);
            x.Cloud.Recoverable[(r2.Id,r2.Revision,r2.ContentHash)]=new Recovery.File(r2,new MemoryStream(retained.Bytes,false));
            await new Retries().RetryAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,pending,x.Pc,x.Cloud,x.Store,x.Queue.Held,populate,hop,ct);
            retried=true;
        },x.Pc.SettleJournalAsync,default);
        Check.Require(children!=null,"Parent removal never started");await children!;
        Check.Require(retried && independent==0 && !x.Queue.Held(new(parent,parent)).Any() && x.Store.OpenNotices.All(n=>n.Kind!=NoticeKind.PutBack),"Consumed revision ran independently or Retry did not settle it");
        Check.Require(x.Pc.Items.TryGetValue(child,out var pc) && pc.Bytes.SequenceEqual(new byte[]{2}) && x.Cloud.Items.TryGetValue(child,out var cloud) && cloud.Bytes.SequenceEqual(new byte[]{2}),"Retry failed to preserve the last attributable revision on both sides");
    }
    public static Task A13LateFileCreateAsync() => throw new NotImplementedException();
    public static Task A13LatePopulatedFolderWithoutChildReportsAsync() => throw new NotImplementedException();
    public static Task A13ChildEditedThenDeletedAsync() => throw new NotImplementedException();
    public static Task A13ChildTombstoneAsync() => throw new NotImplementedException();
    public static Task A13SilentChildNotTombstoneAsync() => throw new NotImplementedException();
    public static Task A13FullFeedPageBeforeEchoAsync() => throw new NotImplementedException();
    public static Task A13JobRunningAtFirstCheckAsync() => throw new NotImplementedException();
    public static Task A13TerminalBeforeReaderSeesEchoAsync() => throw new NotImplementedException();
    public static Task A13OverlappingRepairAsync() => throw new NotImplementedException();
    public static Task A13ReusedChildNameAsync() => throw new NotImplementedException();
    public static Task A13EditBeforeDeleteAsync() => throw new NotImplementedException();
    public static async Task PutBackSurvivesRepairAsync(bool replaced)
    {
        using var x=new PairState(); await x.InitializeAsync();
        var notice=new Notice(1,NoticeKind.PutBack,TextKey.FailLocalInUse,"edit",new([new(Side.Local,"edit","edit-id")],Side.Local),DateTimeOffset.UnixEpoch,false,true);
        await x.Store.SaveNoticeAsync(x.Pair.PairId,notice,default);
        var ordinary=new Notice(2,NoticeKind.Failure,TextKey.PublishFailed,"gone",new([new(Side.Local,"gone","gone-id")],Side.Local),DateTimeOffset.UnixEpoch,false);
        await x.Store.SaveNoticeAsync(x.Pair.PairId,ordinary,default);
        if(replaced) x.Pc.Seed(ItemChecks.File(Side.Local,"edit",2,"new-id"),[2]);
        await x.Store.OweRepairAsync(x.Pair.PairId,Side.Local,"requested",default);
        await x.CompareAsync();
        Check.Require(x.Pc.Requests[nameof(ISide.ReadAllAsync)]>0 && x.Cloud.Requests[nameof(ISide.ReadAllAsync)]>0 && (await x.Store.ReadRepairAsync(x.Pair.PairId,Side.Local,default)).Reason==null,"Full Repair did not acquire and settle its debt");
        Check.Require(x.Store.OpenNotices.All(n=>!n.Matches(ordinary)) && x.Store.OpenNotices.Any(n=>n.Ignored && n.Matches(notice)) && await x.Store.RemovePairAsync(x.Pair.PairId,default)==TextKey.PairPutBackOutstanding,"Comparison failed to distinguish the put-back from an obsolete ordinary failure");
    }
    public static Task A13FailedPutBackInUseAsync() => throw new NotImplementedException();
    public static Task A13RetryPutBackAfterRestartAsync() => throw new NotImplementedException();
    public static Task A13CrashBetweenPutBacksAsync() => throw new NotImplementedException();
    public static Task A13PreparedButUnsentAsync() => throw new NotImplementedException();
    public static Task A13RecycledSubstituteAsync() => throw new NotImplementedException();
    public static Task A13UnreadableAffectedEvidenceAsync() => throw new NotImplementedException();
    public static Task A13SupersededEvidenceAsync() => throw new NotImplementedException();
    public static Task A38JournalGapAsync() => throw new NotImplementedException();
    public static Task A38JournalResetAsync() => throw new NotImplementedException();
    public static async Task A38CloudCursorResetAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        await AutoSync.ApplyPageAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,x.Queue,Side.Dropbox,new ChangePage([],"fresh-cursor",null){ContinuityLost=true},x.Pc,x.Cloud,x.Store,default);
        Check.Require(await x.Store.CursorAsync(x.Pair.PairId,default)=="fresh-cursor","A Dropbox cursor reset was not installed");
        Check.Require((await x.Store.ReadRepairAsync(x.Pair.PairId,Side.Dropbox,default)).Reason==nameof(ChangePage.ContinuityLost),"A cursor reset did not owe a Repair");
        x.VerifyLinks();Check.Require(x.Cloud.Items.Count==1 && x.Store.Records.Count==1 && x.Store.OpenNotices.Count==0,"A cursor reset deleted data or raised a notice");
    }
    public static Task A38CaptureFirstCursorBeforeListingAsync() => throw new NotImplementedException();
    public static async Task A38JournalPageOtherPairsAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        await AutoSync.ApplyPageAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,x.Queue,Side.Local,ItemChecks.Journal([],5),x.Pc,x.Cloud,x.Store,default);
        Check.Require((await x.Store.ReadRepairAsync(x.Pair.PairId,Side.Local,default)).Reason==null,"An irrelevant local page owed a Repair");
        Check.Require(x.Pc.Requests[nameof(ISide.SettleJournalAsync)]>=1,"An irrelevant local page did not advance its settled position");
        x.VerifyLinks();Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Store.Records.Count==1 && x.Store.OpenNotices.Count==0,"An irrelevant local page touched data");
    }
    public static Task A38JournalPageIrrelevantReasonsAsync() => throw new NotImplementedException();
    public static Task A38RestartAfterEmptyPageAsync() => throw new NotImplementedException();
    public static Task A38OneFailedReadThenSuccessNoRepairAsync() => throw new NotImplementedException();
    public static async Task A39InUseHolderNameAsync()
    {
        using var x=new PairState();await x.InitializeAsync();var saved=await x.LinkAsync("item",1);
        var edit=ItemChecks.File(Side.Dropbox,"item",2,saved.DropboxId);x.Cloud.Items["item"]=(edit,[2]);
        x.Pc.PublishFailures["item"]=new ItemFailedException("item",TextKey.FailLocalInUse,null){Holder="Word"};
        await ItemSync.ApplyAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,new Change(Side.Dropbox,ChangeKind.Changed,"item",null,saved.DropboxId,edit,false,false),x.Pc,x.Cloud,x.Store,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        var notice=x.Store.OpenNotices.Single();
        Check.Require(notice.Kind==NoticeKind.Failure && notice.Cause==TextKey.FailLocalInUse && notice.Subject.Holder=="Word","In-use failure did not name the holding program");
        Check.Require(x.Pc.Items["item"].Bytes.SequenceEqual(new byte[]{1}) && x.Cloud.Items["item"].Bytes.SequenceEqual(new byte[]{2}) && x.Store.Records.Count==1,"In-use failure overwrote user data or lost the binding");
    }
    public static Task A39MissingDriveAsync() => throw new NotImplementedException();
    public static Task A39ListingFailureAsync() => throw new NotImplementedException();
    public static Task A39InsufficientSpaceAsync() => throw new NotImplementedException();
    public static async Task A39CloudRefusalAsync()
    {
        using var x=new PairState();await x.InitializeAsync();var saved=await x.LinkAsync("item",1);
        var edit=ItemChecks.File(Side.Local,"item",2,saved.LocalId);x.Pc.Items["item"]=(edit,[2]);
        x.Cloud.PublishFailures["item"]=new ItemFailedException("item",TextKey.PublishFailed,null);
        await ItemSync.ApplyAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,new Change(Side.Local,ChangeKind.Changed,"item",null,saved.LocalId,edit,true,false),x.Pc,x.Cloud,x.Store,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        var notice=x.Store.OpenNotices.Single();
        Check.Require(notice.Kind==NoticeKind.Failure && notice.Cause==TextKey.PublishFailed,"A Dropbox refusal was not reported as a failure");
        Check.Require(x.Pc.Items["item"].Bytes.SequenceEqual(new byte[]{2}) && x.Cloud.Items["item"].Bytes.SequenceEqual(new byte[]{1}) && x.Store.Records.Count==1,"A Dropbox refusal lost the user edit or the binding");
    }
    public static Task A39RepairRetriesOwnFailedItemAsync() => throw new NotImplementedException();
    public static Task A39AutosyncOnlyFailureNotRetriedByRepairAsync() => throw new NotImplementedException();
    public static Task A39OlderFullScanDoesNotCloseNewFailureAsync() => throw new NotImplementedException();
    public static Task A39OlderScopedScanDoesNotCloseRefreshedFailureAsync() => throw new NotImplementedException();
    public static Task A39ChangeAfterUnlockAsync() => throw new NotImplementedException();
    public static Task A39ManualRetryAfterUnlockAsync() => throw new NotImplementedException();
    public static Task A40OfflineAsync() => throw new NotImplementedException();
    public static Task A40RateLimitedAsync() => throw new NotImplementedException();
    public static Task A40TooManyWritesAsync() => throw new NotImplementedException();
    public static Task A40LongpollBackoffAsync() => throw new NotImplementedException();
    public static Task A40OnlyOneBackoffWithoutProviderDelayAsync() => throw new NotImplementedException();
    public static Task A40UnrelatedWorkContinuesAsync() => throw new NotImplementedException();
    public static Task A40SignedOutAsync() => throw new NotImplementedException();
    public static async Task A40DisconnectedAsync()
    {
        using var x=new PairState();await x.InitializeAsync();
        Check.Require(x.Pair.ConnectionId.Length>0,"Disconnected pair has no seeded connection id");
        await x.Store.SaveNoticeAsync(x.Pair.PairId,new Notice(1,NoticeKind.Failure,TextKey.AccountMissing,string.Empty,new([],Side.Local),DateTimeOffset.UnixEpoch,false),default);
        Check.Require(x.Store.OpenNotices.SingleOrDefault(n=>n.Cause==TextKey.AccountMissing) is not null,"Disconnected pair has no stored AccountMissing notice");
        var adapters=0;var tokens=0;
        var host=new Host((_,_)=>Task.FromResult<IStore?>(x.Store),(_,_,_,_)=>{adapters++;return(x.Pc,x.Cloud);},
            ItemChecks.Settings,null!,TimeProvider.System,_=>{tokens++;return Task.FromResult(x.Pair.ConnectionId);},(_,_)=>{tokens++;return Task.CompletedTask;});
        using var output=new StringWriter();using var errors=new StringWriter();
        var code=await new global::Massdex.SyncShark.Engine.Edge.Commands().ExecuteAsync(["notices","--pair",x.Pair.PairId],host,output,errors,default);
        Check.Require(code==0,"Disconnected notices command failed");
        Check.Require(output.ToString().Contains(Text.Get(TextKey.AccountMissing,(TextArg.Account,x.Pair.ConnectionId)),StringComparison.Ordinal),"Disconnected CLI notice did not name the pair's account");
        Check.Require(x.Store.OpenNotices.SingleOrDefault(n=>n.Cause==TextKey.AccountMissing) is not null,"Disconnected CLI notice lost its stored cause");
        Check.Require(adapters==0,"Disconnected notices read opened an adapter");
        Check.Require(tokens==0,"Disconnected notices read acquired a token");
    }
    private static RootIdentity BoundRoots(PairConfig pair) => new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("account","namespace","home","saved"));
    public static async Task A41MissingRootAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var pair=x.Pair with { Roots=BoundRoots(x.Pair) };await x.Store.SavePairAsync(pair,default);
        bool ready=await x.Runner.CheckRootsAsync(pair,null,pair.Drive,x.Store,default);
        Check.Require(!ready && x.Store.OpenNotices.Single() is {Kind:NoticeKind.RootIdentity,Cause:TextKey.RootUnavailable},"A missing top folder was admitted");
        x.VerifyLinks();Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Store.Records.Count==1,"A root refusal changed data");
    }
    public static async Task A41WrongAccountAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var roots=BoundRoots(x.Pair);var pair=x.Pair with { Roots=roots };await x.Store.SavePairAsync(pair,default);
        var observed=roots with { Dropbox=roots.Dropbox with { AccountId="other" } };
        bool ready=await x.Runner.CheckRootsAsync(pair,observed,pair.Drive,x.Store,default);
        Check.Require(!ready && x.Store.OpenNotices.Single() is {Kind:NoticeKind.RootIdentity,Cause:TextKey.RootDropboxScopeChanged},"A wrong account was silently rebound");
        x.VerifyLinks();Check.Require(x.Store.Records.Count==1,"A wrong account changed data");
    }
    public static Task A41PcRootRemovedWhileQueuedAsync() => throw new NotImplementedException();
    public static Task A41PcRootRenamedWhileQueuedAsync() => throw new NotImplementedException();
    public static async Task A41ChangedDriveAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var roots=BoundRoots(x.Pair);var pair=x.Pair with { Roots=roots, Drive=new DriveEvidence(4242,9,100) };await x.Store.SavePairAsync(pair,default);
        bool ready=await x.Runner.CheckRootsAsync(pair,roots,new DriveEvidence(9999,9,100),x.Store,default);
        Check.Require(!ready && x.Store.OpenNotices.Single() is {Kind:NoticeKind.DriveChange,Cause:TextKey.DriveChanged},"A changed drive was silently adopted");
        x.VerifyLinks();Check.Require(x.Store.Records.Count==1,"A changed drive changed data");
    }
    public static async Task A41RestoredDriveAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var roots=BoundRoots(x.Pair);var pair=x.Pair with { Roots=roots, Drive=new DriveEvidence(4242,9,100) };await x.Store.SavePairAsync(pair,default);
        bool ready=await x.Runner.CheckRootsAsync(pair,roots,new DriveEvidence(4242,9,50),x.Store,default);
        Check.Require(!ready && x.Store.OpenNotices.Single() is {Kind:NoticeKind.DriveChange,Cause:TextKey.DriveRestored},"A restored drive was silently adopted");
        x.VerifyLinks();Check.Require(x.Store.Records.Count==1,"A restored drive changed data");
    }
    public static Task A41SettledPositionHeldAsync() => throw new NotImplementedException();
    public static async Task A41IgnoringNoticeDoesNotResumeAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var pair=x.Pair with { Roots=BoundRoots(x.Pair) };await x.Store.SavePairAsync(pair,default);
        bool first=await x.Runner.CheckRootsAsync(pair,null,pair.Drive,x.Store,default);
        await x.Store.SaveNoticeAsync(pair.PairId,x.Store.OpenNotices.Single() with {Ignored=true},default);
        bool again=await x.Runner.CheckRootsAsync(pair,null,pair.Drive,x.Store,default);
        Check.Require(!first && !again && x.Store.OpenNotices.Any(n=>n.Kind==NoticeKind.RootIdentity),"Ignoring the notice resumed a stopped pair");
        x.VerifyLinks();Check.Require(x.Store.Records.Count==1,"Ignoring the notice changed data");
    }
    public static async Task A41MatchingRootRestoredAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var roots=BoundRoots(x.Pair);var pair=x.Pair with { Roots=roots };await x.Store.SavePairAsync(pair,default);
        await x.Store.SaveNoticeAsync(pair.PairId,new Notice(1,NoticeKind.RootIdentity,TextKey.RootDropboxFolderChanged,string.Empty,new([],null),DateTimeOffset.UnixEpoch,false),default);
        bool ready=await x.Runner.CheckRootsAsync(pair,roots,pair.Drive,x.Store,default);
        Check.Require(ready && x.Store.OpenNotices.All(n=>n.Kind!=NoticeKind.RootIdentity),"Restoring the original root did not resume the pair");
        x.VerifyLinks();Check.Require(x.Store.Records.Count==1,"Restoring the root changed data");
    }
    public static Task A41ExplicitBindMatchingAsync() => throw new NotImplementedException();
    public static async Task A41BindDifferentRootRefusedAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var roots=BoundRoots(x.Pair);var pair=x.Pair with { Roots=roots };await x.Store.SavePairAsync(pair,default);
        var observed=roots with { Dropbox=roots.Dropbox with { FolderId="other-folder" } };
        bool ready=await x.Runner.CheckRootsAsync(pair,observed,pair.Drive,x.Store,default);
        Check.Require(!ready && x.Store.OpenNotices.Single() is {Kind:NoticeKind.RootIdentity,Cause:TextKey.RootDropboxFolderChanged},"A different folder at the same path was rebound");
        x.VerifyLinks();Check.Require(x.Store.Records.Count==1,"A different folder changed data");
    }
    public static async Task A42ReplaceMissingTargetValidStageAsync()
    {
        using var x=new PairState();await x.InitializeAsync();var saved=await x.LinkAsync("item",1);
        var old=x.Pc.Items["item"].Entry;var source=ItemChecks.File(Side.Dropbox,saved.DropboxPath,2,saved.DropboxId);
        x.Cloud.Items[source.Path]=(source,[2]);
        await using var staged=await x.Pc.StageAsync("item",new MemoryStream(new byte[]{2}),source,old,default);
        var victim=new RemovalSelection(new(old,"volume",old.ContentHash),saved);
        var abandoned=await x.Pc.StageAsync("item",new MemoryStream(new byte[]{9}),source,old,default);var unused=abandoned.Proof!;
        Check.Require(await x.Pc.ReadOneAsync(unused.Path,default)==null,"Unarmed stage visible as a user file");
        await abandoned.DisposeAsync();
        try { await x.Pc.ReopenStageAsync(victim,unused,default);throw new InvalidDataException("Unarmed stage survived disposal"); } catch(ChangedUnderneathException) { }
        await x.Store.ArmAsync(x.Pair.PairId,victim,staged.Proof,default);await staged.DisposeAsync();
        await foreach(var row in x.Pc.ReadAllAsync("",new ListOptions(true,true),default)) Check.Require(row.Path!=staged.Proof!.Path,"Armed stage leaked into user listing");
        Check.Require(await x.Pc.ReadOneAsync(staged.Proof!.Path,default)==null,"Armed stage visible as a user file");
        await x.Pc.DeleteAsync(old,victim,default);await x.Cloud.DeleteAsync(source,null,default);
        var note=(await x.Store.ArmedAsync(x.Pair.PairId,default))!.Value;
        await using var reopened=await x.Pc.ReopenStageAsync(note.Victim,note.Stage!,default);
        var occupant=ItemChecks.File(Side.Local,"item",3,"replacement");x.Pc.Seed(occupant,[3]);
        try { await x.Pc.PublishAsync(reopened,null,null,default); throw new InvalidDataException("Overwrote a reused name"); } catch(PathOccupiedException) { }
        Check.Require(x.Pc.Items["item"].Bytes.SequenceEqual(new byte[]{3}),"Occupant changed");x.Pc.Items.Remove("item");
        var pcPublishes=x.Pc.Requests[nameof(ISide.PublishAsync)];var cloudPublishes=x.Cloud.Requests[nameof(ISide.PublishAsync)];
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,null,x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        Check.Require(x.Pc.Requests[nameof(ISide.PublishAsync)]==pcPublishes && x.Cloud.Requests[nameof(ISide.PublishAsync)]==cloudPublishes && x.Pc.Items.Count==0 && x.Cloud.Items.Count==0,"Replay resurrected the deleted source");
        Check.Require(await x.Store.ArmedAsync(x.Pair.PairId,default)==null && !x.Store.Records.Values.Any(v=>v.LocalId==staged.Proof!.FileId),"Replay retained the armed note or linked the discarded stage");
        Check.Require(x.Pc.History.Single().Entry==old && x.Cloud.History.Single().Entry==source && x.Cloud.History.Single().Bytes.SequenceEqual(new byte[]{2}),"Replay lost a retained version");
        try { await x.Pc.ReopenStageAsync(note.Victim,note.Stage!,default);throw new InvalidDataException("Discarded stage remained available after replay"); } catch(ChangedUnderneathException) { }
        Check.Require(!x.Cloud.Items.ContainsKey(staged.Proof!.Path) && !x.Store.Records.Values.Any(v=>v.LocalPath==staged.Proof.Path || v.DropboxPath==staged.Proof.Path),"Stage synced as a user file");
    }
    public static Task A42SameIdSizeAlteredStageAsync() => throw new NotImplementedException();
    public static Task A42AlteredFinalNotLinkedAsync() => throw new NotImplementedException();
    public static Task A42ShellRecycledSubstituteAsync() => throw new NotImplementedException();
    public static Task A42RestoredFileEditedThroughAliasAsync() => throw new NotImplementedException();
    public static Task A42RestartBeforePutBackAsync() => throw new NotImplementedException();
    /// <summary>Restart with a saved token after its reply was lost. A replacement at the old name is never the selected identity.</summary>
    private static async Task LostReplyAsync(bool move)
    {
        using var x=new PairState();await x.InitializeAsync();var binding=await x.LinkAsync("old",1);var original=x.Cloud.Items["old"];
        var member=new JobMember(binding.DropboxId,"old",move?"new":null);var job=new AcceptedJob(x.Pair.PairId,move?"files/move_batch/check":"files/delete_batch/check","token",[member]);
        await x.Store.SaveJobAsync(job,default);x.Cloud.Items.Remove("old");if(move) x.Cloud.Seed(original.Entry with {Path="new"},original.Bytes);else x.Cloud.History.Add(original);
        var successor=ItemChecks.File(Side.Dropbox,"old",2,"successor");x.Cloud.Seed(successor,[2]);x.Cloud.JobReplies[job.JobId]=new([new JobReply.Invalid()]);
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,null,x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        Check.Require(x.Cloud.Requests[nameof(ISide.ReadJobAsync)]==1 && x.Cloud.Requests[nameof(ISide.ReadByIdAsync)]>0 && x.Cloud.Items["old"].Entry==successor && x.Cloud.Items["old"].Bytes.SequenceEqual(new byte[]{2}) && x.Cloud.Mutations.All(id=>id!=successor.Id),"Restart failed to check the token and selected identity, or mutated a successor");
    }
    public static Task A42LostMoveReplyAsync() => LostReplyAsync(true);
    public static Task A42CaseRenameFirstHopAsync() => throw new NotImplementedException();
    public static Task A42CaseRenameSecondHopAsync() => throw new NotImplementedException();
    public static Task A42MidMergeAsync() => throw new NotImplementedException();
    public static Task A42MidFolderDeleteAsync() => throw new NotImplementedException();
    public static Task A42MidBatchMixedResultsAsync() => throw new NotImplementedException();
    public static Task A42MixedSuccessUnknownSizeRefusalAsync() => throw new NotImplementedException();
    public static Task A42WrongMemberCountAsync() => throw new NotImplementedException();
    public static Task A42MalformedEnvelopeAsync() => throw new NotImplementedException();
    public static Task A42MalformedSingleReceiptAsync() => throw new NotImplementedException();
    public static Task A42MidKeepBothAsync() => throw new NotImplementedException();
    public static Task A42MidRepairAsync() => throw new NotImplementedException();
    public static Task A42MidRequestedRepairAsync() => throw new NotImplementedException();
    public static Task A42PcMovedAgainAsync() => throw new NotImplementedException();
    public static Task A42UnconfirmedMoveAbsentIdAsync() => throw new NotImplementedException();
    public static Task A42UnconfirmedMoveOutsidePairAsync() => throw new NotImplementedException();
    public static Task A42UnconfirmedMoveUnreadableAsync() => throw new NotImplementedException();
    public static Task A42LostDeleteReplyOldNameReusedAsync() => LostReplyAsync(false);
    public static Task A42RestartWithOpenFailureAsync() => throw new NotImplementedException();
    public static Task A42RestartBeforeFolderListingAsync() => throw new NotImplementedException();
    public static Task A42CreateOnlyPutBackOccupiedAsync() => throw new NotImplementedException();
    public static Task A42NoPathResendAfterRestartAsync() => throw new NotImplementedException();
    public static Task A43SingleUploadFinishAsync() => throw new NotImplementedException();
    public static Task A43BatchUploadFinishAsync() => throw new NotImplementedException();
    public static Task A43SingleFolderCreateAsync() => throw new NotImplementedException();
    public static Task A43BatchFolderCreateAsync() => throw new NotImplementedException();
    public static Task A43SingleMoveAsync() => throw new NotImplementedException();
    public static Task A43BatchMoveAsync() => throw new NotImplementedException();
    public static Task A43DownloadsStaySingleAsync() => throw new NotImplementedException();
    public static Task A43RecoveredCreateOnlyUploadAsync() => throw new NotImplementedException();
    public static Task A43SingleEnsureExistingFolderAsync() => throw new NotImplementedException();
    public static Task A43BatchEnsureExistingFolderAsync() => throw new NotImplementedException();
    public static Task A43SingleLabelTakenAsync() => throw new NotImplementedException();
    public static Task A43BatchLabelTakenAsync() => throw new NotImplementedException();
    public static Task A43DeleteParentRevisionSingleAsync() => throw new NotImplementedException();
    public static Task A43DeleteParentRevisionBatchAsync() => throw new NotImplementedException();
    public static async Task A45PathOnlyWakeUnboundMemberAsync()
    {
        using var x=new PairState();await x.InitializeAsync();
        foreach(var path in new[]{"selected","unrelated"})
        {
            var job=new AcceptedJob(x.Pair.PairId,"files/delete_batch/check",path,[new(path,path,null)]);
            await x.Store.SaveJobAsync(job,default);x.Cloud.JobReplies[path]=new([new JobReply.Running()]);
            await x.Queue.Unfinished.InstalledAsync(job,x.Queue.Held,_=>throw new InvalidOperationException("Empty held input woke a job during installation"));
        }
        var received=new Change(Side.Dropbox,ChangeKind.Deleted,"selected/child",null,null,null,false,false);
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,received,x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        var pending=new List<AcceptedJob>();await foreach(var token in x.Store.JobsAsync(x.Pair.PairId,default)) pending.Add(token);
        Check.Require(pending.Count==2 && pending.Any(j=>j.JobId=="selected" && j.Launch.Single().Path=="selected") && x.Cloud.Requests[nameof(ISide.ReadJobAsync)]==1 && x.Cloud.Requests[nameof(ISide.DeleteAsync)]==0,"Running job was settled, resent or polled more than once");
        Check.Require(x.Cloud.JobReplies["selected"].Count==0 && x.Cloud.JobReplies["unrelated"].Count==1,"Path-only news must check exactly the overlapping saved job");
    }
    public static async Task A45NoLookupBeforeWakeAsync()
    {
        using var x=new PairState();await x.InitializeAsync();
        var job=new AcceptedJob(x.Pair.PairId,"files/delete_batch/check","tok",[new("tok","gone",null)]);
        await x.Store.SaveJobAsync(job,default);x.Cloud.JobReplies["tok"]=new([new JobReply.Running()]);
        long lookups=x.Cloud.Requests[nameof(ISide.ReadByIdAsync)];
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,new Change(Side.Dropbox,ChangeKind.Deleted,"gone",null,null,null,false,false),x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        Check.Require(x.Cloud.Requests[nameof(ISide.ReadJobAsync)]==1 && x.Cloud.Requests[nameof(ISide.ReadByIdAsync)]==lookups,"A path wake performed a current id lookup");
        var pending=new List<AcceptedJob>();await foreach(var p in x.Store.JobsAsync(x.Pair.PairId,default)) pending.Add(p);
        Check.Require(pending.Single()==job && x.Store.OpenNotices.Count==0,"A running job was settled or a notice raised");
    }
    public static async Task A45UnrelatedNewsNoCheckAsync()
    {
        using var x=new PairState();await x.InitializeAsync();
        var job=new AcceptedJob(x.Pair.PairId,"files/delete_batch/check","tok",[new("tok","gone",null)]);
        await x.Store.SaveJobAsync(job,default);x.Cloud.JobReplies["tok"]=new([new JobReply.Running()]);
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,new Change(Side.Dropbox,ChangeKind.Changed,"elsewhere",null,null,null,false,false),x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        Check.Require(x.Cloud.Requests[nameof(ISide.ReadJobAsync)]==0,"News at an unrelated path checked a saved job");
    }
    public static Task A45NewsBehindUnsettledWorkAsync() => throw new NotImplementedException();
    public static Task A45NewsDuringCheckOneFollowupAsync() => throw new NotImplementedException();
    public static Task A45NewsBeforeLaunchReplyCoalescedAsync() => throw new NotImplementedException();
    public static async Task A45InvalidTokenNoResendAsync()
    {
        using var x=new PairState();await x.InitializeAsync();
        var job=new AcceptedJob(x.Pair.PairId,"files/delete_batch/check","tok",[new("id:tok","gone",null)]);
        await x.Store.SaveJobAsync(job,default);x.Cloud.JobReplies["tok"]=new([new JobReply.Invalid()]);
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,new Change(Side.Dropbox,ChangeKind.Deleted,"gone",null,null,null,false,false),x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        Check.Require(x.Cloud.Requests[nameof(ISide.DeleteAsync)]==0 && x.Store.OpenNotices.Any(n=>n.Cause==TextKey.FailDropboxDeleteJobInvalid),"An invalid token was resent or went unreported");
    }
    public static Task A45InvalidVersusInconclusiveAsync() => throw new NotImplementedException();
    public static Task A45TwoInvalidTokensOnePathAsync() => throw new NotImplementedException();
    public static Task A45OneTokenClearsOtherRemainsAsync() => throw new NotImplementedException();
    public static async Task A45HigherFailureMasksInvalidAsync()
    {
        using var x=new PairState();await x.InitializeAsync();
        var job=new AcceptedJob(x.Pair.PairId,"files/delete_batch/check","tok",[new("id:tok","gone",null)]);
        await x.Store.SaveJobAsync(job,default);x.Cloud.JobReplies["tok"]=new([new JobReply.Invalid()]);
        await x.Store.SaveNoticeAsync(x.Pair.PairId,new Notice(1,NoticeKind.Failure,TextKey.FailChanged,"gone",new([new(Side.Dropbox,"gone","id:tok")],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,new Change(Side.Dropbox,ChangeKind.Deleted,"gone",null,null,null,false,false),x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        Check.Require(x.Store.OpenNotices.Any(n=>n.Cause==TextKey.FailChanged) && x.Store.OpenNotices.All(n=>n.Cause!=TextKey.FailDropboxDeleteJobInvalid),"An invalid contribution was not masked by a higher failure");
    }
    public static Task A45InvalidReappearsAfterHigherClosesAsync() => throw new NotImplementedException();
    public static Task A45RestartInconclusiveAfterMaskedInvalidAsync() => throw new NotImplementedException();
    public static async Task A45QuietJobNextRepairOpportunityAsync()
    {
        using var x=new PairState();await x.InitializeAsync();
        var job=new AcceptedJob(x.Pair.PairId,"files/delete_batch/check","tok",[new("tok","gone",null)]);
        await x.Store.SaveJobAsync(job,default);x.Cloud.JobReplies["tok"]=new([new JobReply.Running(),new JobReply.Running()]);
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,new Change(Side.Dropbox,ChangeKind.Deleted,"gone",null,null,null,false,false),x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        await x.Queue.Unfinished.CheckAsync(x.Pair,ItemChecks.Settings,_=>{},x.Paths,null,x.Pc,x.Cloud,x.Store,x.Queue.Held,ItemChecks.Population(x.Pair,x.Paths,x.Pc,x.Cloud,x.Store),ItemChecks.Hop(x.Pair,x.Paths,x.Queue,x.Pc,x.Cloud,x.Store),default);
        var pending=new List<AcceptedJob>();await foreach(var p in x.Store.JobsAsync(x.Pair.PairId,default)) pending.Add(p);
        Check.Require(x.Cloud.Requests[nameof(ISide.ReadJobAsync)]==2 && pending.Single()==job && x.Store.OpenNotices.Count==0,"A quiet running job did not get its next opportunity");
    }
    public static Task A47SecondStartAttachesAsync() => throw new NotImplementedException();
    public static Task A47LogsWhileHostDownAsync() => throw new NotImplementedException();
    public static async Task A47OtherReadWhileHostDownAsync()
    {
        // The default pipe and data root are per user. Never run this against a developer's installed host.
        if(Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT")!="github-hosted" ||
            Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),ResourceNames.ProductFolder)))
            throw new NotImplementedException("Requires a clean disposable Windows runner");
        const string reason="Detached status did not distinguish absence from an unavailable status";
        var client=new global::Massdex.SyncShark.Engine.Edge.HostClient();
        async Task<bool> Absent() => (await client.SendAsync(["status"],TextWriter.Null,TextWriter.Null,default)) is (null,false);
        Check.Require(await Absent(),reason);
        using var output=new StringWriter();using var errors=new StringWriter();
        var code=await global::Massdex.SyncShark.Engine.Edge.Cli.RunAsync(["status"],null!,output,errors,default);
        Check.Require(code==global::Massdex.SyncShark.Engine.Edge.Cli.HostAbsentExitCode &&
            output.ToString().TrimEnd()==Text.Get(TextKey.HostNotRunning) && errors.ToString().Length==0 && await Absent(),reason);

        // At F1, stage the matched signed candidate peers beside the test runner; never resolve them through PATH.
        var hostFile=Path.Combine(AppContext.BaseDirectory,ResourceNames.HostExecutable);
        var cliFile=Path.Combine(AppContext.BaseDirectory,ResourceNames.CliExecutable);
        if(!File.Exists(hostFile) || !File.Exists(cliFile)) throw new NotImplementedException("Attached status requires both candidate peers");
        using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var owner=new System.Diagnostics.Process {StartInfo=new(hostFile){UseShellExecute=false,RedirectStandardOutput=true}};
        owner.StartInfo.ArgumentList.Add("--notify-ready");owner.Start();
        try
        {
            Check.Require(await owner.StandardOutput.ReadLineAsync(limit.Token)=="1" && !owner.HasExited,reason);
            using var cli=new System.Diagnostics.Process {StartInfo=new(cliFile){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true}};
            cli.StartInfo.ArgumentList.Add("status");cli.Start();
            try
            {
                var stdout=cli.StandardOutput.ReadToEndAsync(limit.Token);var stderr=cli.StandardError.ReadToEndAsync(limit.Token);
                await cli.WaitForExitAsync(limit.Token);
                Check.Require(cli.ExitCode==0 && (await stdout).TrimEnd()==Text.Get(TextKey.NoPairsYet) &&
                    (await stderr).Length==0 && !owner.HasExited,reason);
            }
            finally { if(!cli.HasExited) {cli.Kill();await cli.WaitForExitAsync();} }
        }
        finally { if(!owner.HasExited) {owner.Kill();await owner.WaitForExitAsync();} }
    }
    public static Task A47MutationWhileHostDownAsync() => throw new NotImplementedException();
    public static Task A47PerPairPauseSharedVolumeAsync() => throw new NotImplementedException();
    public static Task A47ResumeFromHeldPositionAsync() => throw new NotImplementedException();
    public static Task A47ResumeAfterHistoryGapAsync() => throw new NotImplementedException();
    public static async Task A47RestartClearsPauseAsync()
    {
        using var store=new InMemoryStateStore();
        var settings=SettingsParser.Load();
        Host NewHost()=>new((_,_)=>Task.FromResult<IStore?>(store),(_,opened,_,_)=>ReferenceEquals(opened,store)?(new FakeSide(Side.Local),new FakeSide(Side.Dropbox)):throw new InvalidOperationException("Wrong adapter store"),settings,null!,TimeProvider.System,_=>throw new InvalidOperationException(),(_,_)=>throw new InvalidOperationException());
        var first=NewHost();await first.PauseAsync("pair-a",default);
        Check.Require(first.IsPaused("pair-a"),"A pair pause was not applied");
        var restarted=NewHost();
        Check.Require(!restarted.IsPaused("pair-a"),"A pause survived the host restart");
        await first.StopAsync(default);await restarted.StopAsync(default);
    }
    public static Task A47FailedPairDoesNotStopOtherPairsAsync() => throw new NotImplementedException();
    public static Task A47QuietJobHostReadyAsync() => throw new NotImplementedException();
    public static Task A47FailedRecoveryHostReadyAsync() => throw new NotImplementedException();
    public static Task A47SecondStartDuringStopDrainAsync() => throw new NotImplementedException();
    public static Task A51LegacyTieNewChangeStillHeldAsync() => throw new NotImplementedException();
    public static Task A51LegacyTieIdenticalEditStillHeldAsync() => throw new NotImplementedException();
    public static Task A51AllMembersAbsentAsync() => throw new NotImplementedException();
    public static Task A52UnreadFolderNoticeRetainedAsync() => throw new NotImplementedException();
    public static Task A52MultiMemberPartiallyCoveredAsync() => throw new NotImplementedException();
    public static Task A52InvalidJobPathsAbsentAsync() => throw new NotImplementedException();
    public static Task A52NewAutosyncFailureAfterScanAsync() => throw new NotImplementedException();
    public static Task A52RefreshedAutosyncFailureAfterScanAsync() => throw new NotImplementedException();
    public static Task A52FullScanNewerMembershipAsync() => throw new NotImplementedException();
    public static Task A52ScopedScanNewerMembershipAsync() => throw new NotImplementedException();
    public static Task A52ClockRollbackAsync() => throw new NotImplementedException();
}
