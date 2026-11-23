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
        await FinishRefusalsAsync(pair,file);
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

    private static async Task FinishRefusalsAsync(PairConfig pair, Entry source)
    {
        using var store = new InMemoryStateStore();
        const string conflict = """{".tag":"path","path":{".tag":"conflict","conflict":{".tag":"file"}}}""";
        const string closed = """{".tag":"lookup_failed","lookup_failed":{".tag":"closed"}}""";
        string[] refusals = [conflict, closed, conflict];
        TextKey[] causes = [TextKey.ClashNameTaken, TextKey.DropboxRefused, TextKey.FailChanged];
        int calls = 0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            var index = calls++;
            bool batch = index == refusals.Length;
            Check.Require(request.RequestUri!.AbsolutePath.EndsWith(batch ? "/finish_batch_v2" : "/finish", StringComparison.Ordinal), "Unexpected finish route");
            using var args = JsonDocument.Parse(batch ? await request.Content!.ReadAsStringAsync(ct) : request.Headers.GetValues("Dropbox-API-Arg").Single());
            var entries = batch ? args.RootElement.GetProperty("entries").EnumerateArray().ToArray() : new[] { args.RootElement };
            for (int i = 0; i < entries.Length; i++)
            {
                int ordinal = batch ? i : index;
                var mode = entries[i].GetProperty("commit").GetProperty("mode");
                Check.Require(entries[i].GetProperty("cursor").GetProperty("session_id").GetString() == $"session-{ordinal}" &&
                    (ordinal == 2 ? mode.GetProperty("update").GetString() == "selected-rev" : mode.GetString() == "add"), "Finish request lost selected mode or ordinal");
            }
            var json = batch ? "{\"entries\":[" + string.Join(",", refusals.Select(value => "{\".tag\":\"failure\",\"failure\":" + value + "}")) + "]}" : "{\"error\":" + refusals[index] + "}";
            return new(batch ? HttpStatusCode.OK : HttpStatusCode.Conflict) { Content = new StringContent(json) };
        }
        var d = new Dispatch(Send, store, name => new Uri(new Uri("https://example.invalid/"), name), _ => refusals.Length);
        Task<(AcceptedJob? Accepted, JobReply MemberReply)> Submit(int i)
        {
            var f = source with { Path = $"file-{i}" };
            return d.SubmitAsync(pair, "files/upload", new(i == 2 ? "id:selected" : null, f.Path, null), i == 2 ? "selected-rev" : null, false,
                global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Session(f.Path, $"session-{i}", f.Size, f, f.ContentHash), default);
        }
        void Verify(JobReply result, int i) => Check.Require(result is JobReply.Terminal { Members.Count: 1 } t &&
            t.Members[0] is (null, var cause, FailureDisposition.Permanent) && cause == causes[i], $"Finish refusal lost its selected cause at ordinal {i}");
        for (int i = 0; i < causes.Length; i++) Verify((await Submit(i)).MemberReply, i);
        var ready = new ReadyWrites(); var previous = SynchronizationContext.Current;
        Task<(AcceptedJob? Accepted, JobReply MemberReply)>[] pending;
        try { SynchronizationContext.SetSynchronizationContext(ready); pending = Enumerable.Range(0, causes.Length).Select(Submit).ToArray(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        ready.SendReady(); var replies = await Task.WhenAll(pending);
        for (int i = 0; i < replies.Length; i++) Verify(replies[i].MemberReply, i);
        Check.Require(calls == refusals.Length + 1, "Finish refusals replayed a mutation or split the ready batch");
    }

    // Observe a real finish independently of whether a replacement-order mutant ever reaches publication.
    public static async Task FinishCursorAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var f = Scenarios.ItemChecks.File(Side.Local,"item",42,"pc");
        using var store = new InMemoryStateStore();
        int calls = 0; string? session = null; long offset = -1;
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            using var arg = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            if (request.RequestUri!.AbsolutePath.EndsWith("/upload_session/finish",StringComparison.Ordinal))
            {
                calls++; var cursor = arg.RootElement.GetProperty("cursor");
                session = cursor.GetProperty("session_id").GetString(); offset = cursor.GetProperty("offset").GetInt64();
            }
            var path = pair.DropboxRoot.TrimEnd('/') + "/" + f.Path;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent(JsonSerializer.Serialize(new { name=f.Path, path_display=path, path_lower=path.ToLowerInvariant(), id="id:published", rev="new", size=f.Size, client_modified=f.Modified, server_modified=f.Modified, content_hash=f.ContentHash }))});
        }
        var d = new Dispatch(Send,store,n=>new Uri(new Uri("https://example.invalid/"),n),_=>2);
        var stage = global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Session(f.Path,"closed",f.Size,f,f.ContentHash);
        var (_,reply) = await d.SubmitAsync(pair,"files/upload",new(null,f.Path,null),null,false,stage,default);
        var wrong = new List<string>();
        if (session != "closed") wrong.Add("finish named a different session");
        if (offset != f.Size) wrong.Add("finish used a different byte offset");
        Check.Require(wrong.Count == 0, string.Join("; ", wrong));
        Check.Require(calls == 1 && reply is JobReply.Terminal t && t.Members.Single().Entry?.Id == "id:published", "Closed session was not published once");
    }

    public static async Task SelectedDigestAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var selected = Scenarios.ItemChecks.File(Side.Local,"item",42,"pc");
        using var store = new InMemoryStateStore(); using var bytes = new MemoryStream([42]);
        var stage=global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Single(selected.Path,bytes,selected);
        bytes.WriteByte(43); bytes.Position=0; int sends=0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request,CancellationToken ct)
        {
            sends++; var sent=await request.Content!.ReadAsByteArrayAsync(ct);
            using var hash=new ContentHash(); hash.Append(sent);
            var path=pair.DropboxRoot.TrimEnd('/')+"/"+selected.Path;
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new {name=selected.Path,path_display=path,path_lower=path.ToLowerInvariant(),id="id:result",rev="new",size=sent.Length,content_hash=hash.Finish(),client_modified=selected.Modified,server_modified=selected.Modified}))};
        }
        var d=new Dispatch(Send,store,n=>new Uri(new Uri("https://example.invalid/"),n),_=>2);
        try { await d.SubmitAsync(pair,"files/upload",new(null,selected.Path,null),null,false,stage,default); }
        catch(ChangedUnderneathException e) when(e.Path==selected.Path && sends==1){return;}
        throw new InvalidDataException("Correct receipt for equal-length edited bytes was accepted as the selected version");
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
        await CheckThrottleAsync(false);
    }

    public static Task BodyWaitAsync() => CheckThrottleAsync(true);

    private static async Task CheckThrottleAsync(bool bodyOnly)
    {
        var delay = TimeSpan.FromSeconds(7);
        var routes = new List<string>();
        Task<HttpResponseMessage> Busy(HttpRequestMessage request, CancellationToken ct)
        {
            routes.Add(request.RequestUri!.AbsolutePath);
            var reply = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(bodyOnly ? """{"error":{"reason":{".tag":"too_many_write_operations"},"retry_after":7}}""" : "busy") };
            if (!bodyOnly) reply.Headers.RetryAfter = new(delay);
            return Task.FromResult(reply);
        }
        static Uri Endpoint(string name) => new(new Uri("https://example.invalid/"), name);
        using var store = new InMemoryStateStore();
        var dispatch = new Dispatch(Busy,store,Endpoint,_=>2);
        var f = Scenarios.ItemChecks.File(Side.Local,"item",42,"source");
        using var bytes = new MemoryStream([42]);
        var upload = new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        foreach (var name in new[]{"stage", "submit"})
        {
            int before = routes.Count;
            var expectedRoute = name == "stage" ? "/files/upload_session/start" : "/files/delete_v2";
            try
            {
                if (name == "stage") await upload.StageAsync(Busy,Endpoint,2,f.Path,bytes,f,f with { IsFolder=true },default);
                else await dispatch.SubmitAsync("pair","files/delete_v2",new("id:selected",f.Path,null),"rev",false,null,default);
            }
            catch (ItemFailedException e) when (routes.Count == before + 1 && routes[before] == expectedRoute &&
                e.Path == f.Path && e.Cause == (bodyOnly ? TextKey.WaitTooManyWrites : TextKey.DropboxRefused) && e.Disposition == FailureDisposition.TemporaryNoEffect && e.ProviderWait == delay && e.ProviderWaitStarted.HasValue) { continue; }
            catch (NotImplementedException) { throw; }
            catch (Exception e) { throw new InvalidDataException((bodyOnly ? "HTTP throttle lost body-only retry_after: " : "HTTP throttle lost Retry-After: ") + name,e); }
            throw new InvalidDataException("Throttle did not refuse the selected request: " + name);
        }
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
        catch(ItemFailedException e) when(e.Path==b.Path && e.Cause==TextKey.WaitTooManyWrites && e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==wait && e.ProviderWaitStarted.HasValue && calls==1){return;}
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
        await VerifyWait(results[1], 1);
        await VerifyWait(d.ResolveCreateClashAsync(pair, new(null,"lookup",null),
            new JobReply.Terminal([(null,TextKey.ClashNameTaken,FailureDisposition.Permanent)]), default), 2);
        async Task VerifyWait(Task attempt, int expectedReads)
        {
            try { await attempt; }
            catch(ItemFailedException e) when(e.Path=="lookup" && e.Cause==TextKey.DropboxRefused && e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==wait && e.ProviderWaitStarted.HasValue && wire.Submits==1 && wire.Reads==expectedReads){return;}
            throw new InvalidDataException("A throttled exact occupant read became an unknown create outcome or was retried");
        }
    }
    public static async Task DeferredSettlementAsync()
    {
        var pair=Scenarios.ItemChecks.Pair(); var delay=TimeSpan.FromSeconds(7);
        var clock=new FakeTimeProvider(DateTimeOffset.UnixEpoch); var original=clock.GetTimestamp();
        using var store=new InMemoryStateStore(); using var wire=new EnsureWire(pair.DropboxRoot,delay,true); using var http=new HttpClient(wire);
        var api=new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http,clock,Scenarios.ItemChecks.Settings,_=>{},_=>ValueTask.FromResult<string?>("synthetic"),"test-namespace");
        var d=new Dispatch(api,store); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        var db=new global::Massdex.SyncShark.Engine.Dropbox.DropboxSide(pair,()=>paths,api,d); var pc=new FakeSide(Side.Local);
        var ready=new ReadyWrites(); var previous=SynchronizationContext.Current;
        Task<(AcceptedJob? Accepted,JobReply MemberReply)>[] pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(ready);
            pending=new[]{"lookup","created"}.Select(path=>d.SubmitAsync(pair,"files/create_folder_v2",new JobMember(null,path,null){Origin=ActionKind.EnsureDropboxFolder},null,false,null,default)).ToArray();
        }
        finally {SynchronizationContext.SetSynchronizationContext(previous);}
        ready.SendReady(); var accepted=await Task.WhenAll(pending);
        Check.Require(accepted.All(x=>x.Accepted?.JobId=="accepted" && x.MemberReply is JobReply.Running),"Deferred ensure was not accepted");
        var owner=new global::Massdex.SyncShark.Engine.Executor.Unfinished();
        Task Attempt()=>owner.CheckAsync(pair,Scenarios.ItemChecks.Settings,_=>{},paths,null,pc,db,store,_=>[],Scenarios.ItemChecks.Population(pair,paths,pc,db,store),Scenarios.ItemChecks.Hop(pair,paths,new global::Massdex.SyncShark.Engine.Run.WorkQueue(),pc,db,store),default);
        bool sawWait=false;
        try {await Attempt();}
        catch(ItemFailedException e) when(e.Path=="lookup" && e.Cause==TextKey.DropboxRefused && e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==delay && e.ProviderWaitStarted==original){sawWait=true;}
        Check.Require(wire.GoodReads==1 && wire.Reads==1 && clock.GetTimestamp()==original,"Good deferred ensure was blocked behind the held lookup");
        Check.Require(sawWait && wire.Submits==1 && wire.Polls==1,"Deferred ensure lost its original wait or replayed the launch/token");
        clock.Advance(delay); await Attempt();
        var jobs=new List<AcceptedJob>(); await foreach(var job in store.JobsAsync(pair.PairId,default)) jobs.Add(job);
        Check.Require(jobs.Count==0 && wire.GoodReads==1 && wire.Reads==2 && wire.Submits==1 && wire.Polls==1,"Deferred retry repeated a settled ordinal, mutation or job poll, or left the job unsettled");
    }

    private sealed class EnsureWire(string root,TimeSpan delay,bool deferred=false):HttpMessageHandler
    {
        internal int Submits,Reads,Polls,GoodReads;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var route=request.RequestUri!.AbsolutePath;
            static HttpResponseMessage Ok(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json)};
            string Folder(string name){var path=root.TrimEnd('/')+"/"+name;return JsonSerializer.Serialize(new Dictionary<string,string>{[".tag"]="folder",["id"]="id:"+name,["name"]=name,["path_display"]=path,["path_lower"]=path.ToLowerInvariant()});}
            const string clash="""{".tag":"failure","failure":{".tag":"path","path":{".tag":"conflict","conflict":{".tag":"folder"}}}}""";
            if(route.EndsWith("/create_folder_batch",StringComparison.Ordinal))
            {
                Submits++;
                return deferred ? Ok("""{".tag":"async_job_id","async_job_id":"accepted"}""") : Ok("{\".tag\":\"complete\",\"entries\":[{\".tag\":\"success\",\"metadata\":"+Folder("created")+"},"+clash+"]}");
            }
            if(deferred && route.EndsWith("/create_folder_batch/check",StringComparison.Ordinal)){Polls++;return Ok("{\".tag\":\"complete\",\"entries\":["+clash+","+clash+"]}");}
            if(deferred && route.EndsWith("/get_latest_cursor",StringComparison.Ordinal)) return Ok("""{"cursor":"head"}""");
            using var read=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Check.Require(route.EndsWith("/get_metadata",StringComparison.Ordinal),"Unexpected lookup route");
            var selected=read.RootElement.GetProperty("path").GetString();
            if(deferred && selected==root.TrimEnd('/')+"/created"){GoodReads++;return Ok(Folder("created"));}
            Check.Require(selected==root.TrimEnd('/')+"/lookup","Unexpected lookup"); Reads++;
            if(deferred && Reads>1)return Ok(Folder("lookup"));
            var reply=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent("")};reply.Headers.RetryAfter=new(delay);return reply;
        }
    }
    private static async Task UnreadableThrottleAsync()
    {
        using var store=new InMemoryStateStore();
        var delay=TimeSpan.FromSeconds(7);
        int calls=0;
        Task<HttpResponseMessage> Send(HttpRequestMessage request,CancellationToken ct)
        {
            calls++; Check.Require(request.RequestUri!.AbsolutePath=="/files/delete_v2","Unexpected unreadable throttle route");
            var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new UnreadableContent()};
            r.Headers.RetryAfter=new(delay);return Task.FromResult(r);
        }
        var d=new Dispatch(Send,store,name=>new Uri(new Uri("https://example.invalid/"),name),_=>2);
        try { await d.SubmitAsync("pair","files/delete_v2",new("id:selected","item",null),"rev",false,null,default); }
        catch(ItemFailedException e) when(calls==1 && e.Path=="item" && e.Cause==TextKey.DropboxRefused && e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==delay && e.ProviderWaitStarted.HasValue){return;}
        catch(NotImplementedException){throw;}
        catch(Exception e){throw new InvalidDataException("Unreadable HTTP 429 lost the observed status or wait",e);}
        throw new InvalidDataException("Unreadable HTTP 429 did not raise a retry for the selected member");
    }
    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>Task.FromException(new IOException());
        protected override bool TryComputeLength(out long length){length=0;return false;}
    }
    /// <summary>Check the actual submit/save handoff. Resolution of a deferred member belongs to Unfinished.</summary>
    public static async Task DeferredCreateAsync()
    {
        var pair=Scenarios.ItemChecks.Pair(); using var store=new InMemoryStateStore(); int calls=0;
        string[] paths=["ensure","exclusive"];
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            calls++;
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
            Check.Require(request.RequestUri!.AbsolutePath.EndsWith("/create_folder_batch",StringComparison.Ordinal) &&
                body.RootElement.GetProperty("paths").EnumerateArray().Select(v=>v.GetString()).SequenceEqual(paths.Select(p=>pair.DropboxRoot.TrimEnd('/')+"/"+p)),
                "Create launch did not carry the selected paths in wire order");
            return new(HttpStatusCode.OK){Content=new StringContent("""{".tag":"async_job_id","async_job_id":"accepted"}""")};
        }
        var dispatch=new Dispatch(Send,store,name=>new Uri(new Uri("https://example.invalid/"),name),_=>2);
        ActionKind[] origins=[ActionKind.EnsureDropboxFolder,ActionKind.KeepBoth];
        Task<(AcceptedJob? Accepted,JobReply MemberReply)>[] pending;
        var ready=new ReadyWrites();var previous=SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(ready);
            pending=paths.Select((path,i)=>dispatch.SubmitAsync(pair,"files/create_folder_v2",new JobMember(null,path,null){Origin=origins[i]},null,i==1,null,default)).ToArray();
        }
        finally {SynchronizationContext.SetSynchronizationContext(previous);}
        ready.SendReady();var replies=await Task.WhenAll(pending);
        var jobs=new List<AcceptedJob>();await foreach(var job in store.JobsAsync(pair.PairId,default)) jobs.Add(job);
        var saved=jobs.Single();var restored=JsonSerializer.Deserialize<AcceptedJob>(JsonSerializer.Serialize(saved))!;
        Check.Require(calls==1 && restored.JobId=="accepted" && replies.All(v=>v.Accepted==saved && v.MemberReply is JobReply.Running) &&
            restored.Launch.Count==paths.Length && restored.Launch.Select((m,i)=>m.Id is null && m.Destination is null && m.Path==paths[i] && m.Origin==origins[i] && m.ExclusiveCreate==(i==1)).All(v=>v),
            "Saved create batch lost ordinal, original action or exclusive intent");
    }

    // Release the scheduled send only once the three callers are ready, without a timer or a scheduler race.
    private sealed class ReadyWrites : SynchronizationContext
    {
        private readonly Queue<Action> ready = [];
        public override void Post(SendOrPostCallback callback, object? state) => ready.Enqueue(() => callback(state));
        internal void SendReady() { while (ready.TryDequeue(out var action)) action(); }
    }
}
