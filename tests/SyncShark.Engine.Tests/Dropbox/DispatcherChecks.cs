using System.Net;
using System.Text.Json;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Dispatch = Massdex.SyncShark.Engine.Dropbox.Dispatcher;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks the selected ID, revision, accepted launch order and provider batch limit on the wire.</summary>
public static class DispatcherChecks
{
    public static async Task RunAsync()
    {
        await GrowingDirectAsync(Scenarios.ItemChecks.Pair(), Scenarios.ItemChecks.File(Side.Local,"item",42,"pc"), beforeOnly:true);
        var failures = new List<string>();
        var selected = new JobMember("id:selected", "item", null);
        if (Dispatch.SelectedIdAbsent(true, true, selected, true) || !Dispatch.SelectedIdAbsent(true, false, selected, true))
            failures.Add("A whole-batch error marked selected IDs absent, or a single by-ID miss lost its evidence");
        try
        {
            var rejected = Dispatch.UnusableReceipt(new ItemFailedException("item", TextKey.DropboxRefused, null));
            if (rejected.Members.Single().Failure != FailureDisposition.UnknownEffect) failures.Add("Unusable upload receipt became a definite refusal");
        }
        catch (ItemFailedException) { failures.Add("Unusable upload receipt escaped as a definite refusal"); }
        var mixed = new JobReply.Terminal([(null,TextKey.FailChanged,FailureDisposition.Permanent),(null,TextKey.FailChanged,FailureDisposition.UnknownEffect)]) { AbsentOrdinals = new HashSet<int>{1} };
        if (Dispatch.MemberReply(mixed,0).AbsentOrdinals.Count != 0 || !Dispatch.MemberReply(mixed,1).AbsentOrdinals.SetEquals([0])) failures.Add("Selected-ID absence was lost or given to another member");
        var ensure = Dispatch.CreateMember("item", false);
        var exclusive = Dispatch.CreateMember("item", true);
        var launch = new AcceptedJob("pair", "files/create_folder_batch", "accepted", [ensure, exclusive]);
        var restored = JsonSerializer.Deserialize<AcceptedJob>(JsonSerializer.Serialize(launch))!;
        if (restored.Launch[0] == restored.Launch[1] || !restored.Launch.SequenceEqual(launch.Launch) ||
            restored.Launch.Any(m => m.Id is not null || m.Destination is not null || m.Path != "item"))
            failures.Add("Saved async create erased exclusive intent or attributed an occupant by name");
        Check.Require(failures.Count == 0, string.Join("; ", failures));
        using var store = new InMemoryStateStore();
        var wire = new System.Collections.Concurrent.ConcurrentDictionary<string, JsonElement>();
        var installs = new List<AcceptedJob>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            var route = request.RequestUri!.AbsolutePath;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false));
            Check.Require(wire.TryAdd(route, body.RootElement.Clone()), "Duplicate wire request");
            var json = route.EndsWith("/delete_batch", StringComparison.Ordinal)
                ? "{\".tag\":\"async_job_id\",\"async_job_id\":\"batch\"}" : "{\"metadata\":{}}";
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Installed(AcceptedJob job)
        {
            var persisted = new List<AcceptedJob>();
            await foreach (var savedJob in store.JobsAsync(job.PairId, default)) persisted.Add(savedJob);
            installs.AddRange(persisted.Where(savedJob => savedJob == job));
            notified.SetResult(); await release.Task.ConfigureAwait(false);
        }
        var dispatcher = new Dispatch(Send, store, name => new Uri(new Uri("https://example.invalid/"), name), _ => 2, Installed);
        var members = Enumerable.Range(0, 3).Select(i => new JobMember($"id:item-{i}", $"item-{i}", null)).ToArray();
        var clashes = new JobReply.Terminal([(null,TextKey.ClashNameTaken,FailureDisposition.Permanent)]);
        var exclusiveReply = await dispatcher.ResolveCreateClashAsync(Scenarios.ItemChecks.Pair(), restored.Launch[1], clashes, default);
        Check.Require(ReferenceEquals(clashes, exclusiveReply) && wire.IsEmpty, "Deferred exclusive create inspected or adopted an occupant");
        var context = new ReadyWrites();
        var previous = SynchronizationContext.Current;
        Task<(AcceptedJob? Accepted, JobReply MemberReply)>[] queued;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            queued = members.Select((m, i) => dispatcher.SubmitAsync("pair", "files/delete_v2", m, $"rev-{i}", false, null, default)).ToArray();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        context.SendReady();
        await notified.Task;
        bool pending = !queued[0].IsCompleted && !queued[1].IsCompleted;
        release.SetResult();
        var replies = await Task.WhenAll(queued);
        Check.Require(pending, "Accepted callers were released before their installed callback finished");
        Check.Require(wire.Count == 2 && wire.ContainsKey("/files/delete_batch") && wire.ContainsKey("/files/delete_v2"),
            "Ready deletes were not split at the provider limit");
        var entries = wire["/files/delete_batch"].GetProperty("entries").EnumerateArray().Append(wire["/files/delete_v2"]).ToArray();
        Check.Require(entries.Length == members.Length && entries.Select((e,i) => e.GetProperty("path").GetString() == members[i].Id &&
            e.GetProperty("parent_rev").GetString() == $"rev-{i}").All(v => v), "Selected delete ID or revision changed");
        var saved = new List<AcceptedJob>();
        await foreach (var job in store.JobsAsync("pair", default)) saved.Add(job);
        Check.Require(saved.Count == 1 && installs.SequenceEqual(saved) && saved[0].Launch.SequenceEqual(members.Take(2)) &&
            replies.Take(2).All(x => x.Accepted == saved[0] && x.MemberReply is JobReply.Running), "Accepted token lost its send order or reported completion");
    }

    /// <summary>A malformed success is still a dispatched write, for direct uploads and closed sessions alike.</summary>
    public static async Task UploadReceiptAsync()
    {
        using var store = new InMemoryStateStore();
        var pair = Scenarios.ItemChecks.Pair();
        var file = Scenarios.ItemChecks.File(Side.Local, "item", 42, "pc");
        var path = pair.DropboxRoot.TrimEnd('/') + "/item";
        await GrowingDirectAsync(pair,file);
        bool correct = false;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            await request.Content!.CopyToAsync(Stream.Null, ct);
            var json = JsonSerializer.Serialize(new { name=file.Path, id="id:result", path_display=path,
                path_lower=path.ToLowerInvariant(), rev="new", size=correct ? file.Size : file.Size + 1, server_modified=file.Modified,
                client_modified=file.Modified, content_hash=file.ContentHash });
            return new(HttpStatusCode.OK) { Content=new StringContent(json) };
        }
        var dispatcher = new Dispatch(Send, store, name => new Uri(new Uri("https://example.invalid/"), name), _ => 2);
        using var bytes = new MemoryStream([42]);
        foreach (var valid in new[]{ false, true })
        foreach (var stage in new[]{ global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Single(file.Path,bytes,file),
            global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Session(file.Path,"session",file.Size,file,file.ContentHash) })
        {
            bytes.Position=0; correct=valid;
            var (_, result) = await dispatcher.SubmitAsync(pair,"files/upload",new(null,file.Path,null),null,false,stage,default);
            Check.Require(result is JobReply.Terminal { Members.Count: 1 } t && (valid
                ? t.Members[0] is ({ Id:"id:result" }, null, null)
                : t.Members[0].Failure == FailureDisposition.UnknownEffect && t.Members[0].Entry is null),
                "Successful write with unusable uploaded metadata was accepted or refused definitively");
        }
    }

    private static async Task GrowingDirectAsync(PairConfig pair, Entry file, bool beforeOnly=false)
    {
        foreach (bool before in beforeOnly ? new[]{true} : new[]{true,false})
        {
            using var source=new MemoryStream();source.WriteByte(42);source.Position=0;
            var stage=global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Single(file.Path, source, file);
            if (before) source.SetLength(2);
            using var store=new InMemoryStateStore(); int calls=0;
            async Task<HttpResponseMessage> Send(HttpRequestMessage request,CancellationToken ct)
            {
                calls++;
                await request.Content!.CopyToAsync(Stream.Null,ct);
                source.SetLength(2);
                var path=pair.DropboxRoot.TrimEnd('/')+"/"+file.Path;
                return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new {name=file.Path, path_display=path, path_lower=path.ToLowerInvariant(), id="id:result", rev="rev", size=file.Size, content_hash=file.ContentHash, client_modified=file.Modified, server_modified=file.Modified}))};
            }
            var d=new Dispatch(Send,store,n=>new Uri(new Uri("https://example.invalid/"),n),_=>2);
            try
            {
                var (_,reply)=await d.SubmitAsync(pair,"files/upload",new(null,file.Path,null),null,false,stage,default);
                if (!before && calls==1 && reply is JobReply.Terminal {Members.Count:1} t && t.Members[0].Failure==FailureDisposition.UnknownEffect) continue;
            }
            catch(ChangedUnderneathException) when(before && calls==0){continue;}
            catch(ItemFailedException error) when(!before && calls==1 && error.Disposition==FailureDisposition.UnknownEffect){continue;}
            throw new InvalidDataException("Growth before direct publication sent a stale prefix, or growth during publication was recorded as synced");
        }
    }

    /// <summary>A non-JSON HTTP throttle with a Retry-After survives staging and dispatch as a per-call retry.</summary>
    public static async Task ThrottledWriteAsync()
    {
        await UnreadableThrottleAsync();
        await MixedBatchThrottleAsync();
        await EnsureLookupThrottleAsync();
        var delay = TimeSpan.FromSeconds(7);
        Task<HttpResponseMessage> Busy(HttpRequestMessage _, CancellationToken ct)
        {
            var reply = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("busy") };
            reply.Headers.RetryAfter = new(delay); return Task.FromResult(reply);
        }
        static Uri Endpoint(string name) => new(new Uri("https://example.invalid/"), name);
        using var store = new InMemoryStateStore();
        var dispatch = new Dispatch(Busy,store,Endpoint,_=>2);
        var f = Scenarios.ItemChecks.File(Side.Local,"item",42,"source");
        using var bytes = new MemoryStream([42]);
        var upload = new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        var errors = new List<string>();
        foreach (var name in new[]{"stage", "submit"})
        {
            try
            {
                if (name == "stage") await upload.StageAsync(Busy,Endpoint,2,f.Path,bytes,f,f with { IsFolder=true },default);
                else await dispatch.SubmitAsync("pair","files/delete_v2",new("id:selected",f.Path,null),"rev",false,null,default);
                errors.Add(name);
            }
            catch (ItemFailedException e) when (e.Disposition == FailureDisposition.TemporaryNoEffect && e.ProviderWait == delay && e.ProviderWaitStarted.HasValue) { }
            catch (NotImplementedException) { throw; }
            catch (Exception) { errors.Add(name); }
        }
        Check.Require(errors.Count == 0, "HTTP throttle lost Retry-After: " + string.Join(", ",errors));
    }

    private static async Task MixedBatchThrottleAsync()
    {
        using var store=new InMemoryStateStore();
        var pair=Scenarios.ItemChecks.Pair();
        var a=Scenarios.ItemChecks.File(Side.Local,"first",42,"source");
        var b=a with {Path="second"};
        var wait=TimeSpan.FromSeconds(7);
        int calls=0;
        Task<HttpResponseMessage> Send(HttpRequestMessage request,CancellationToken ct)
        {
            calls++;
            Check.Require(request.RequestUri!.AbsolutePath.EndsWith("/finish_batch_v2",StringComparison.Ordinal),"Expected a single finish batch");
            var path=pair.DropboxRoot.TrimEnd('/')+"/first";
            var ok=JsonSerializer.Serialize(new Dictionary<string,object>{[".tag"]="success",["name"]=a.Path,["path_display"]=path,["path_lower"]=path.ToLowerInvariant(),["id"]="id:published",["rev"]="rev",["size"]=a.Size,["content_hash"]=a.ContentHash,["client_modified"]=a.Modified,["server_modified"]=a.Modified});
            const string busy="""{".tag":"failure","failure":{".tag":"too_many_write_operations"}}""";
            var reply=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"entries\":["+ok+","+busy+"]}")};
            reply.Headers.RetryAfter=new(wait);return Task.FromResult(reply);
        }
        var d=new Dispatch(Send,store,name=>new Uri(new Uri("https://example.invalid/"),name),_=>2);
        var context=new ReadyWrites();var before=SynchronizationContext.Current;
        Task<(AcceptedJob? Accepted,JobReply MemberReply)>[] members;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            members=new[]{a,b}.Select(f=>d.SubmitAsync(pair,"files/upload",new(null,f.Path,null),null,false,
                global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Session(f.Path,"session-"+f.Path,f.Size,f,f.ContentHash),default)).ToArray();
        }
        finally {SynchronizationContext.SetSynchronizationContext(before);}
        context.SendReady();
        var good=await members[0];
        Check.Require(good.MemberReply is JobReply.Terminal {Members.Count:1} t && t.Members[0] is ({Id:"id:published"},null,null),"Throttled neighbour lost a valid publication");
        try { await members[1]; }
        catch(ItemFailedException e) when(e.Path==b.Path && e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==wait && e.ProviderWaitStarted.HasValue && calls==1){return;}
        throw new InvalidDataException("HTTP 200 member throttle did not carry its original wait or the batch was resent");
    }

    private static async Task EnsureLookupThrottleAsync()
    {
        var pair=Scenarios.ItemChecks.Pair();var wait=TimeSpan.FromSeconds(7);
        using var store=new InMemoryStateStore();using var wire=new EnsureWire(pair.DropboxRoot,wait);using var http=new HttpClient(wire);
        var api=new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http,TimeProvider.System,Scenarios.ItemChecks.Settings,_=>{},_=>ValueTask.FromResult<string?>("synthetic"),"test-namespace");
        var d=new Dispatch(api,store); var ready=new ReadyWrites();var previous=SynchronizationContext.Current;
        Task<(AcceptedJob? Accepted,JobReply MemberReply)>[] results;
        try
        {
            SynchronizationContext.SetSynchronizationContext(ready);
            results=new[]{"created","lookup"}.Select(path=>d.SubmitAsync(pair,"files/create_folder_v2",new(null,path,null),null,false,null,default)).ToArray();
        }
        finally {SynchronizationContext.SetSynchronizationContext(previous);}
        ready.SendReady(); var good=await results[0];
        Check.Require(good.MemberReply is JobReply.Terminal {Members.Count:1} t && t.Members[0] is ({Id:"id:created"},null,null),"Ensure lookup cancelled its successful neighbour");
        try {await results[1];}
        catch(ItemFailedException e) when(e.Path=="lookup" && e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==wait && e.ProviderWaitStarted.HasValue && wire.Submits==1 && wire.Reads==1){return;}
        throw new InvalidDataException("A throttled exact occupant read became an unknown create outcome or was retried");
    }
    private sealed class EnsureWire(string root,TimeSpan delay):HttpMessageHandler
    {
        internal int Submits,Reads;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.RequestUri!.AbsolutePath.EndsWith("/create_folder_batch",StringComparison.Ordinal))
            {
                Submits++; var path=root.TrimEnd('/')+"/created";
                var meta=JsonSerializer.Serialize(new Dictionary<string,string>{[".tag"]="folder",["id"]="id:created",["name"]="created",["path_display"]=path,["path_lower"]=path.ToLowerInvariant()});
                const string clash="""{".tag":"failure","failure":{".tag":"path","path":{".tag":"conflict","conflict":{".tag":"folder"}}}}""";
                return new(HttpStatusCode.OK){Content=new StringContent("{\".tag\":\"complete\",\"entries\":[{\".tag\":\"success\",\"metadata\":"+meta+"},"+clash+"]}")};
            }
            using var read=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));Reads++;
            Check.Require(request.RequestUri.AbsolutePath.EndsWith("/get_metadata",StringComparison.Ordinal) && read.RootElement.GetProperty("path").GetString()==root.TrimEnd('/')+"/lookup","Unexpected lookup");
            var reply=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent("")};reply.Headers.RetryAfter=new(delay);return reply;
        }
    }
    private static async Task UnreadableThrottleAsync()
    {
        using var store=new InMemoryStateStore();
        var delay=TimeSpan.FromSeconds(7);
        Task<HttpResponseMessage> Send(HttpRequestMessage _,CancellationToken ct)
        {
            var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new UnreadableContent()};
            r.Headers.RetryAfter=new(delay);return Task.FromResult(r);
        }
        var d=new Dispatch(Send,store,name=>new Uri(new Uri("https://example.invalid/"),name),_=>2);
        try { await d.SubmitAsync("pair","files/delete_v2",new("id:selected","item",null),"rev",false,null,default); }
        catch(ItemFailedException e) when(e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==delay && e.ProviderWaitStarted.HasValue){return;}
        catch(NotImplementedException){throw;}
        catch(Exception e){throw new InvalidDataException("Unreadable HTTP 429 lost the observed status or wait",e);}
        throw new InvalidDataException("Unreadable HTTP 429 did not raise a retry for the selected member");
    }
    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>Task.FromException(new IOException());
        protected override bool TryComputeLength(out long length){length=0;return false;}
    }
    /// <summary>A real terminal create conflict adopts exactly the ensured folder, never the exclusive label.</summary>
    public static async Task DeferredCreateAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var paths = new PathScope(pair,[pair],DriveType.Fixed);
        using var store = new InMemoryStateStore();
        var reads = new System.Collections.Concurrent.ConcurrentBag<string>();
        var clock=new FakeTimeProvider(DateTimeOffset.UnixEpoch); var waits=new List<RunEvent>();
        using var wire=new CreatePollWire(reads,pair.DropboxRoot);
        using var client = new HttpClient(wire);
        var api = new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(client,clock,Scenarios.ItemChecks.Settings,waits.Add,_=>ValueTask.FromResult<string?>("synthetic"),"test-namespace");
        var side = new global::Massdex.SyncShark.Engine.Dropbox.DropboxSide(pair,()=>paths,api,new Dispatch(api,store));
        var job = new AcceptedJob(pair.PairId,"files/create_folder_batch","token",[Dispatch.CreateMember("ensure",false),Dispatch.CreateMember("exclusive",true),Dispatch.CreateMember("outside",false),Dispatch.CreateMember("busy",false)]);
        var pending = side.ReadJobAsync(job, default);
        if (pending.IsCompleted) await pending; // Let R29 stubs surface as not implemented.
        Check.Require(!pending.IsCompleted && clock.ScheduledDelay==TimeSpan.FromSeconds(17),"Deferred exact lookup lost its provider wait");
        clock.ElapseScheduledDelay();var reply=await pending;
        var root=pair.DropboxRoot.TrimEnd('/');
        Check.Require(reply is JobReply.Terminal { Members.Count:4 } t && t.Members[0] is ({Id:"id:ensure",IsFolder:true},null,null) &&
            t.Members[1] is (null,TextKey.ClashNameTaken,FailureDisposition.Permanent) && t.Members[2].Failure == FailureDisposition.UnknownEffect &&
            t.Members[3] is ({Id:"id:busy",IsFolder:true},null,null) && wire.Polls==1 && waits.Count(e=>e.Kind==RunEventKind.Wait)==1 &&
            reads.Count==4 && reads.Contains(root+"/ensure") && reads.Contains(root+"/outside") && reads.Count(path=>path==root+"/busy")==2,
            "Deferred ensure retried another operation, adopted an exclusive/out-of-root occupant, or lost a neighbour");
    }
    private sealed class CreatePollWire(System.Collections.Concurrent.ConcurrentBag<string> reads,string root) : HttpMessageHandler
    {
        internal int Polls;
        private int busyReads;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string json;
            if(request.RequestUri!.AbsolutePath.EndsWith("/create_folder_batch/check",StringComparison.Ordinal))
            {
                const string clash = """{".tag":"failure","failure":{".tag":"path","path":{".tag":"conflict","conflict":{".tag":"folder"}}}}""";
                Polls++;
                json="{\".tag\":\"complete\",\"entries\":["+string.Join(",",Enumerable.Repeat(clash,4))+"]}";
            }
            else
            {
                using var arg=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var asked=arg.RootElement.GetProperty("path").GetString()!;
                reads.Add(asked);
                var name = asked.Split('/')[^1];
                if (name=="busy" && Interlocked.Increment(ref busyReads)==1)
                {
                    var throttle=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent("")};
                    throttle.Headers.RetryAfter=new(TimeSpan.FromSeconds(17)); return throttle;
                }
                var path = name=="outside" ? "/other/outside" : root.TrimEnd('/')+"/"+name;
                json=JsonSerializer.Serialize(new Dictionary<string,string>{[".tag"]="folder",["name"]=name,["id"]="id:"+name,["path_display"]=path,["path_lower"]=path.ToLowerInvariant()});
            }
            return new(HttpStatusCode.OK){ Content=new StringContent(json) };
        }
    }

    // Release the scheduled send only once the three callers are ready, without a timer or a scheduler race.
    private sealed class ReadyWrites : SynchronizationContext
    {
        private readonly Queue<Action> ready = [];
        public override void Post(SendOrPostCallback callback, object? state) => ready.Enqueue(() => callback(state));
        internal void SendReady() { while (ready.TryDequeue(out var action)) action(); }
    }
}
