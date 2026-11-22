using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Recovery and notice checks against populated pairs; fixture setup does not perform the operation under test.</summary>
public static class RecoveryChecks
{
    private sealed class PairState : IDisposable
    {
        internal PairConfig Pair { get; } = ItemChecks.Pair();
        internal FakeSide Pc { get; } = new(Side.Local);
        internal FakeSide Cloud { get; } = new(Side.Dropbox);
        internal InMemoryStateStore Store { get; } = new();
        internal async Task<SyncedEntry> LinkAsync(string path, byte value)
        {
            var pc = ItemChecks.File(Side.Local,path,value,"pc-"+path); var cloud = ItemChecks.File(Side.Dropbox,path,value,"db-"+path);
            Pc.Seed(pc,[value]); Cloud.Seed(cloud,[value]); var row=SyncedEntries.From(pc,cloud);
            await Store.CommitAsync(Pair.PairId,null,row,null,default); return row;
        }
        internal async Task InitializeAsync() { await Store.SavePairAsync(Pair,default); Store.MarkFirstRepairDone(Pair.PairId); }
        internal Task CompareAsync() => Repair.RunAsync(Pair,Pc,Cloud,Store,null,default);
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
        x.Pc.Seed(ItemChecks.File(Side.Local,"same",1,"pc"),[1]);
        x.Cloud.Seed(ItemChecks.File(Side.Dropbox,"same",2,"cloud",folder),folder?[]:[2]);
        if(folder) x.Cloud.Seed(ItemChecks.File(Side.Dropbox,"same/quiet-child",3,"child"),[3]);
        await x.CompareAsync();
        x.VerifyLinks();
        foreach(var side in new[]{x.Pc,x.Cloud})
        {
            Check.Require(side.Items.Count==(folder?3:2), "Unexpected labels or orphan content");
            Check.Require(side.Items.Values.Where(a=>!a.Entry.IsFolder).Select(a=>a.Bytes.Single()).Order().SequenceEqual(folder?new byte[]{1,3}:new byte[]{1,2}),"A version was lost");
            if(folder) Check.Require(side.Items.Values.Single(a=>a.Bytes.SequenceEqual(new byte[]{3})).Entry.Path.EndsWith("/quiet-child",StringComparison.Ordinal),"Missing quiet descendant");
        }
        Check.Require(x.Store.Records.Count==(folder?3:2) && x.Store.OpenNotices.Single().Cause==TextKey.InfoKeptBoth,"Keep-both notice or binding missing");
    }
    public static async Task ChurnAsync()
    {
        using var x=new PairState(); await x.InitializeAsync(); await x.LinkAsync("item",1);
        foreach(byte value in new byte[]{2,3,4})
        {
            var edit=ItemChecks.File(Side.Local,"item",value,"pc-item");x.Pc.Items[edit.Path]=(edit,[value]);
            await ItemSync.ApplyAsync(x.Pair,new(Side.Local,ChangeKind.Changed,edit.Path,null,edit.Id,edit,true,false),x.Pc,x.Cloud,x.Store,default);
            Check.Require(x.Cloud.Items[edit.Path].Bytes.SequenceEqual(new[]{value}),"Edit did not reach the counterpart"); x.VerifyLinks();
        }
        Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Store.OpenNotices.Count==0,"Churn created a spurious conflict");
    }
    public static async Task ConcurrentRepairAsync()
    {
        using var x=new PairState(); await x.InitializeAsync();await x.LinkAsync("item",1);
        var edit=ItemChecks.File(Side.Local,"item",2,"pc-item"); x.Pc.Items[edit.Path]=(edit,[2]);
        await Task.WhenAll(x.CompareAsync(),ItemSync.ApplyAsync(x.Pair,new(Side.Local,ChangeKind.Changed,edit.Path,null,edit.Id,edit,true,false),x.Pc,x.Cloud,x.Store,default));
        Check.Require(x.Cloud.Requests[nameof(ISide.PublishAsync)]==1 && x.Cloud.Items[edit.Path].Bytes.SequenceEqual(new byte[]{2}),"Duplicate or missing write");
        x.VerifyLinks(); Check.Require(x.Store.OpenNotices.Count==0 && x.Store.Records.Count==1,"Incorrect settlement");
    }
    public static async Task RepeatRepairAsync()
    {
        using var x=new PairState(); await x.InitializeAsync();await x.LinkAsync("item",1);
        await x.CompareAsync(); long pc=x.Pc.Requests[nameof(ISide.OpenReadAsync)], db=x.Cloud.Requests[nameof(ISide.OpenReadAsync)], commits=x.Store.Requests[nameof(IStore.CommitAsync)];
        await x.CompareAsync(); x.VerifyLinks();
        Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Store.Records.Count==1 && x.Store.OpenNotices.Count==0,"Unchanged pair not preserved");
        Check.Require(pc==x.Pc.Requests[nameof(ISide.OpenReadAsync)] && db==x.Cloud.Requests[nameof(ISide.OpenReadAsync)] && commits==x.Store.Requests[nameof(IStore.CommitAsync)],"Unchanged second comparison repeated work");
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
        await new Unfinished().CheckAsync(x.Pair,x.Pc,x.Cloud,x.Store,default);
        var pending=new List<AcceptedJob>();await foreach(var p in x.Store.JobsAsync(x.Pair.PairId,default)) pending.Add(p);
        Check.Require(pending.Single()==job && x.Cloud.Requests[nameof(ISide.ReadJobAsync)]==1 && x.Store.OpenNotices.Count==0,"Running token lost or repeatedly polled");
        x.VerifyLinks(); Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Store.Records.Count==1,"Quiet job modified unrelated data");
    }
    public static async Task AlteredStageAsync()
    {
        using var x=new PairState(); await x.InitializeAsync();var saved=await x.LinkAsync("item",1);
        var previous=x.Pc.Items["item"].Entry; x.Pc.Items.Clear();
        var incoming=ItemChecks.File(Side.Dropbox,"item",2,"db-item");x.Cloud.Items["item"]=(incoming,[2]);
        var altered=ItemChecks.File(Side.Local,"item.stage",3,"stage-id");x.Pc.Seed(altered,[3]);
        await x.Store.ArmAsync(x.Pair.PairId,new(new(previous,"volume",previous.ContentHash),saved),new("item.stage","volume",altered.Id,1,incoming.ContentHash),default);
        await new Unfinished().CheckAsync(x.Pair,x.Pc,x.Cloud,x.Store,default);
        Check.Require(!x.Pc.Items.Values.Any(v=>v.Entry.Path=="item" && v.Bytes.SequenceEqual(new byte[]{3})) && x.Cloud.Items["item"].Bytes.SequenceEqual(new byte[]{2}),"Altered stage published or incoming bytes destroyed");
        Check.Require(!x.Store.Records.Values.Any(v=>v.LocalId==altered.Id && v.DropboxContentHash==incoming.ContentHash),"Unverified stage linked");
        Check.Require(x.Store.OpenNotices.All(n=>n.Kind==NoticeKind.Failure),"Unrelated notice produced during recovery");
    }
    public static async Task FirstDeletedAsync(bool folder, bool measured)
    {
        using var x=new PairState();await x.Store.SavePairAsync(x.Pair,default);
        const string path="formerly-deleted";var entry=ItemChecks.File(Side.Local,path,1,"pc",folder);x.Pc.Seed(entry,folder?[]:[1]);
        x.Cloud.ClockOffset=measured?(TimeSpan.Zero,TimeSpan.Zero):null;
        x.Cloud.DeletedHistory[path]=folder?new DeletionEvidence.DeletedPath(path,path):
            new DeletionEvidence.DeletedFile(path,"cloud","previous","r1",entry.Modified.AddDays(1),null,entry.ContentHash,null);
        await x.CompareAsync();
        if(folder || measured)
        {
            Check.Require(x.Pc.Items.Count==0 && x.Cloud.Items.Count==0 && x.Pc.History.Single().Entry==entry,"First-sync recycle did not preserve selected version");
            Check.Require(x.Store.OpenNotices.Count==0 && x.Store.Records.Count==0,"False notice or retained binding");
        }
        else
        {
            Check.Require(x.Pc.Items[path].Bytes.SequenceEqual(new byte[]{1}) && x.Cloud.Items[path].Bytes.SequenceEqual(new byte[]{1}),"Absent clock proof lost a copy");
            x.VerifyLinks(); Check.Require(x.Store.Records.Count==1 && x.Store.OpenNotices.Single().Cause==TextKey.InfoEditBeatsDelete,"First-sync preservation notice missing");
        }
    }
    public static async Task RefusedRemovalAsync(bool recycle)
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("item",1);
        var saved=x.Store.Records.Values.Single();var entry=x.Pc.Items["item"].Entry;
        x.Cloud.Items.Clear();var error=new ItemFailedException("item",recycle?TextKey.RecycleUnavailable:TextKey.ReadLocalFailed,null);
        if(recycle)x.Pc.RecycleRefusals["item"]=error; else x.Pc.ReadFailures["item"]=error;
        await ItemSync.ApplyAsync(x.Pair,new(Side.Dropbox,ChangeKind.Deleted,"item",null,saved.DropboxId,null,false,true),x.Pc,x.Cloud,x.Store,default);
        Check.Require(x.Pc.Items.Count==1 && x.Pc.Items["item"].Entry==entry && x.Pc.Items["item"].Bytes.SequenceEqual(new byte[]{1}) && x.Cloud.Items.Count==0,"Refusal discarded data");
        Check.Require(x.Pc.History.Count==0 && x.Store.Records.Values.Single()==saved && x.Store.OpenNotices.Single().Cause==error.Cause,"Failed effect was settled or hidden");
    }
    public static async Task SafeSaveAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("item",1);
        var next=ItemChecks.File(Side.Local,"item",2,"temporary-file-id");x.Pc.Items["item"]=(next,[2]);
        await new WorkQueue().AdmitAsync(x.Pair,new ChangePage([
            new(Side.Local,ChangeKind.Deleted,"item",null,"pc-item",null,false,true,1),
            new(Side.Local,ChangeKind.Renamed,"item","x.tmp",next.Id,next,true,false,2)],null,new(1,3)),
            (change,ct)=>ItemSync.ApplyAsync(x.Pair,change,x.Pc,x.Cloud,x.Store,ct),default);
        x.VerifyLinks();Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Cloud.Items["item"].Bytes.SequenceEqual(new byte[]{2}),"Safe-save bytes or endpoint lost");
        Check.Require(x.Cloud.Requests[nameof(ISide.DeleteAsync)]==0 && x.Store.OpenNotices.Count==0 && x.Store.Records.Count==1,"Safe save executed as delete/create");
    }
    public static async Task OccupiedDestinationAsync(bool folder)
    {
        using var x=new PairState();await x.InitializeAsync();
        Entry Src(Side side,string path,string id,byte value,bool isFolder=false)=>ItemChecks.File(side,path,value,id,isFolder);
        var a=Src(Side.Local,"old","moving-a",1,folder);var b=Src(Side.Dropbox,"old","moving-b",1,folder);
        await x.Store.CommitAsync(x.Pair.PairId,null,SyncedEntries.From(a,b),null,default);
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
        await AutoSync.ApplyPageAsync(x.Pair,Side.Dropbox,new([],"after",null),x.Pc,x.Cloud,x.Store,default);
        Check.Require(await x.Store.CursorAsync(x.Pair.PairId,default)=="after", "Irrelevant page blocked the cursor");
        x.VerifyLinks(); Check.Require(x.Store.Records.Count==1 && x.Store.OpenNotices.Count==0 && x.Cloud.Requests[nameof(ISide.ReadOneAsync)]==0,"Empty page touched data");
    }
    public static async Task RequestWaitAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var clock=new FakeTimeProvider(DateTimeOffset.UnixEpoch);var delay=TimeSpan.FromSeconds(7);
        var waiting=new global::Massdex.SyncShark.Engine.Dropbox.RateLimit().WaitAsync(delay,clock,default);
        Check.Require(!waiting.IsCompleted && clock.ScheduledDelay==delay,"Provider wait ignored or replaced");
        clock.ElapseScheduledDelay();await waiting;x.VerifyLinks();
        Check.Require(x.Store.OpenNotices.Count==0 && x.Store.Records.Count==1,"A provider wait created an item failure");
    }
    public static async Task RootReplacementAsync()
    {
        using var x=new PairState();await x.InitializeAsync();await x.LinkAsync("steady",1);
        var roots=new RootIdentity(x.Pair.LocalRoot,x.Pair.DropboxRoot,x.Pair.ConnectionId,new(1,1,null),new("account","namespace","home","saved"));
        var pair=x.Pair with { Roots=roots };await x.Store.SavePairAsync(pair,default);
        var observed=roots with { Dropbox=roots.Dropbox with { FolderId="other" } };
        bool ready=await new PairRunner().CheckRootsAsync(pair,observed,x.Store,default);
        Check.Require(!ready && (await x.Store.PairAsync(pair.PairId,default))?.Roots==roots && x.Store.OpenNotices.Single().Kind==NoticeKind.RootIdentity,"Different folder silently rebound");
        x.VerifyLinks();Check.Require(x.Pc.Items.Count==1 && x.Cloud.Items.Count==1 && x.Store.Records.Count==1,"Root refusal changed data");
    }
    public static async Task PauseAsync()
    {
        var host=new Host();await host.PauseAsync("pair-a",default);
        Check.Require(host.IsPaused("pair-a") && !host.IsPaused("pair-b"),"Pair pause leaked to another pair");
        await host.PauseAsync(null,default);await host.ResumeAsync("pair-a",default);
        Check.Require(host.IsPaused("pair-a") && host.IsPaused("pair-b"),"Pair resume overrode global pause");
        await host.ResumeAsync(null,default);Check.Require(!host.IsPaused("pair-a") && !host.IsPaused("pair-b"),"Global resume left an unexpected hold");
    }
    public static async Task BatchSelectionAsync(int count)
    {
        var wire=new DeleteWire();using var http=new HttpClient(wire);
        var api=new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http,TimeProvider.System);
        var dispatcher=new global::Massdex.SyncShark.Engine.Dropbox.Dispatcher(api);
        var members=Enumerable.Range(0,count).Select(i=>new JobMember($"id:selected-{i}",$"member-{i}",null)).ToArray();
        await dispatcher.SubmitAsync("files/delete_v2",members,default);
        Check.Require(wire.Paths.Count==1 && wire.Paths[0].EndsWith(count==1?"/files/delete_v2":"/files/delete_batch",StringComparison.Ordinal),"Wrong number or shape of deletion requests");
        foreach(var member in members)Check.Require(wire.Bodies.Single().Contains(member.Id!,StringComparison.Ordinal),"Delete not bound to selected member");
    }
    private sealed class DeleteWire : HttpMessageHandler
    {
        internal List<string> Paths { get; }=[];
        internal List<string> Bodies { get; }=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var json=Paths[^1].EndsWith("delete_batch",StringComparison.Ordinal)?"""{".tag":"async_job_id","async_job_id":"token"}""":"""{"metadata":{".tag":"folder","name":"member-0","id":"id:selected-0","path_display":"/member-0","path_lower":"/member-0"}}""";
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")};
        }
    }
}
