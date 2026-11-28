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
        foreach (var refusal in new[]
        {
            new ItemFailedException(string.Empty, TextKey.ReadDropboxFailed, null)
                { Disposition = FailureDisposition.TemporaryNoEffect, ProviderWait = TimeSpan.FromSeconds(1), ProviderWaitStarted = 0 },
            new ItemFailedException(string.Empty, TextKey.SignInReturnRetry, null) { Disposition = FailureDisposition.Permanent }
        })
        {
            int sends = 0;
            var unsent = new Dispatch((_, _) => { sends++; throw refusal; }, store,
                name => new Uri(new Uri("https://example.invalid/"), name), _ => 1);
            try { await unsent.SubmitAsync("pair", "files/delete_v2", selected, null, false, null, default); throw new InvalidOperationException("Pre-send failure lost"); }
            catch (ItemFailedException error)
            {
                Check.Require(sends == 1 && error.Path == selected.Path && error.Cause == refusal.Cause &&
                    error.Disposition == refusal.Disposition && error.ProviderWait == refusal.ProviderWait && error.ProviderWaitStarted == refusal.ProviderWaitStarted,
                    "A pre-send failure lost the failure evidence for its member");
            }
        }
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
        bool accepted = queued.Take(2).All(task=>task.IsCompletedSuccessfully);
        release.SetResult();
        var replies = await Task.WhenAll(queued);
        Check.Require(accepted, "Reconciliation retained a durably accepted member");
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

    /// <summary>Single and checked deletes must prove the ID and report its actual location, not the launch name.</summary>
    public static async Task RelocatedDeleteAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        pair = pair with { Roots = new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("account","namespace","home","id:root")) };
        var selected = new JobMember("id:selected","original",null);
        var other = new JobMember("id:other","neighbour",null);
        var job = new AcceptedJob(pair.PairId,"files/delete_batch","token",[selected,other]);
        using var store = new InMemoryStateStore();
        foreach (var (location, actual) in new[]{("/Elsewhere/Moved","/Elsewhere/Moved"),(pair.DropboxRoot+"/Moved","Moved")})
        {
            object Meta(string id,string path) => new Dictionary<string,string> { [".tag"]="folder", ["id"]=id, ["name"]=path.Split('/')[^1], ["path_display"]=path, ["path_lower"]=path.ToLowerInvariant() };
            foreach (bool correct in new[]{false,true})
            {
                // No location is required to reject someone else's ID.
                var metadata = correct ? Meta(selected.Id!,location) : new { id="id:wrong" }; int sends=0;
                async Task<HttpResponseMessage> Send(HttpRequestMessage request,CancellationToken ct)
                {
                    sends++; using var args=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    Check.Require(request.RequestUri!.AbsolutePath=="/files/delete_v2" && args.RootElement.GetProperty("path").GetString()==selected.Id,"Delete did not address the selected ID");
                    return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new {metadata}))};
                }
                var dispatcher=new Dispatch(Send,store,n=>new Uri(new Uri("https://example.invalid/"),n),_=>2);
                JobReply single;
                try { (_,single)=await dispatcher.SubmitAsync(pair,"files/delete_v2",selected,null,false,null,default); }
                catch (ItemFailedException error) when (!correct && sends==1 && error.Disposition==FailureDisposition.UnknownEffect)
                { throw new InvalidDataException("Single delete tried to map the wrong ID before rejecting it",error); }
                bool Proven(JobReply.Terminal reply,int i,string? id,string path) => reply.Members[i] is ({ } e,null,null) && e.Id==id && e.Path==path;
                bool Unknown(JobReply.Terminal t,int i) => t.Members[i].Entry is null && t.Members[i].Failure==FailureDisposition.UnknownEffect && !t.AbsentOrdinals.Contains(i);
                Check.Require(sends==1 && single is JobReply.Terminal {Members.Count:1} t && (correct ? Proven(t,0,selected.Id,actual) : Unknown(t,0)),"Single delete lost the returned location or accepted a different ID");
                object Member(object m) => new Dictionary<string,object>{{".tag","success"},{"metadata",m}};
                var payload = new Dictionary<string,object>{{".tag","complete"},{"entries",new[]{Member(metadata),Member(Meta(other.Id!,pair.DropboxRoot+"/neighbour"))}}};
                using var body=new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload));
                var result=await global::Massdex.SyncShark.Engine.Dropbox.DropboxReply.ReadAsync(body,global::Massdex.SyncShark.Engine.Dropbox.DropboxReplyShapes.Job(pair,job),default);
                Check.Require(result is JobReply.Terminal {Members.Count:2} batch && (correct ? Proven(batch,0,selected.Id,actual) : Unknown(batch,0)) && Proven(batch,1,other.Id,"neighbour") && job.Launch[0].Path=="original" && job.Launch[1].Path=="neighbour","Checked delete lost an actual location, credited a different ID, or changed the launch footprint");
            }
        }
    }

    /// <summary>A malformed success is still a dispatched write, for direct uploads and closed sessions alike.</summary>
    public static async Task UploadReceiptAsync()
    {
        using var store = new InMemoryStateStore();
        var pair = Scenarios.ItemChecks.Pair();
        var file = Scenarios.ItemChecks.File(Side.Local, "item", 42, "pc");
        var path = pair.DropboxRoot.TrimEnd('/') + "/item";
        await GrowingDirectAsync(pair,file);
        await LostUploadReplyAsync(pair,file);
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

    /// <summary>A selected update whose reply is lost, unreadable, parseable but unproving, or an unreadable gateway error after the
    /// whole body settles by one path read holding the selected ID and digest. A create (no selected ID), another ID or digest, a body that failed mid-way
    /// or a definite refusal stays unsettled; no write is replayed.</summary>
    private static async Task LostUploadReplyAsync(PairConfig pair, Entry file)
    {
        var other=new string('0',64);
        const string conflict="{\"error\":{\".tag\":\"path\",\"reason\":{\".tag\":\"conflict\",\"conflict\":{\".tag\":\"file\"}},\"upload_session_id\":\"session\"}}"; // UploadWriteFailed
        foreach (var (mode,id,held,expect,expectedReads) in new (string,string?,string,string,int)[]{
            ("unreadable","id:result",file.ContentHash!,"proven",1),("unreadable","id:result",other,"unknown",1),
            ("dropped","id:result",file.ContentHash!,"proven",1),("midbody","id:result",file.ContentHash!,"unknown",0),
            ("session","id:result",file.ContentHash!,"proven",1),("session","id:result",other,"unknown",1),
            ("unreadable","id:selected",file.ContentHash!,"unknown",1), // the path holds equal bytes under another ID
            ("unreadable",null,file.ContentHash!,"unknown",0),("dropped",null,file.ContentHash!,"unknown",0), // a create cannot attribute equal bytes
            ("gateway","id:result",file.ContentHash!,"proven",1),("gateway","id:result",other,"unknown",1),("gateway",null,file.ContentHash!,"unknown",0),
            ("invalid","id:result",file.ContentHash!,"proven",1),("invalid","id:result",other,"unknown",1),("invalid",null,file.ContentHash!,"unknown",0),
            ("conflict","id:result",file.ContentHash!,"refused",0),
            ("changed","id:result",file.ContentHash!,"changed",0)})
        {
            // Mid-body: an unhashed source one byte past one copy buffer, and a wire that accepts the first chunk only.
            var bytes=mode=="midbody" ? new byte[UploadContentChecks.CopyBufferBytes+1] : new byte[]{42};
            using var source=new MemoryStream(bytes);
            using var wire=new MemoryStream(new byte[bytes.Length-1]);
            // Changed: the whole body went out but is not the selected version, and its reply is lost.
            var sent=mode=="midbody" ? file with {Size=bytes.Length,ContentHash=""} : mode=="changed" ? file with {ContentHash=other} : file;
            var stage=mode=="session" ? global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Session(file.Path,"session",file.Size,file,file.ContentHash!)
                : global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Single(file.Path,source,sent);
            using var store=new InMemoryStateStore(); int uploads=0, reads=0; string? wrong=null; // the dispatcher absorbs wire exceptions
            var path=pair.DropboxRoot.TrimEnd('/')+"/"+file.Path;
            async Task<HttpResponseMessage> Send(HttpRequestMessage request,CancellationToken ct)
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/get_metadata",StringComparison.Ordinal))
                {
                    uploads++;
                    if (!request.RequestUri.AbsolutePath.EndsWith(mode=="session" ? "/upload_session/finish" : "/files/upload",StringComparison.Ordinal)) wrong="route";
                    using var arg=JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
                    var commit=mode=="session" ? arg.RootElement.GetProperty("commit") : arg.RootElement;
                    var sentMode=commit.GetProperty("mode");
                    if (id is null ? sentMode.ValueKind!=JsonValueKind.String || sentMode.GetString()!="add" : sentMode.ValueKind!=JsonValueKind.Object || sentMode.GetProperty("update").GetString()!="rev") wrong="mode";
                    if (mode=="midbody")
                    {
                        await request.Content!.CopyToAsync(wire,ct); // throws once the second chunk overflows the wire
                        throw new InvalidDataException("The mid-body fixture sent the whole body");
                    }
                    await request.Content!.CopyToAsync(Stream.Null,ct);
                    if (mode is "unreadable" or "session") return new(HttpStatusCode.OK){Content=new StringContent("{\"name\":")};
                    if (mode=="gateway") return new(HttpStatusCode.BadGateway){Content=new StringContent("<html>bad gateway</html>")};
                    if (mode=="conflict") return new(HttpStatusCode.Conflict){Content=new StringContent(conflict)};
                    if (mode=="invalid") return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new Dictionary<string,object>{["name"]=file.Path,["path_display"]=path,["path_lower"]=path.ToLowerInvariant(),["id"]="id:result",["rev"]="rev",["size"]=file.Size,["content_hash"]=other,["client_modified"]=file.Modified,["server_modified"]=file.Modified}))}; // a receipt naming other bytes
                    throw new HttpRequestException("connection closed before headers");
                }
                reads++;
                using (var lookup=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                    if (!string.Equals(lookup.RootElement.GetProperty("path").GetString(),path,StringComparison.OrdinalIgnoreCase)) wrong="lookup path";
                return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new Dictionary<string,object>{[".tag"]="file",["name"]=file.Path,["path_display"]=path,["path_lower"]=path.ToLowerInvariant(),["id"]="id:result",["rev"]="rev",["size"]=file.Size,["content_hash"]=held,["client_modified"]=file.Modified,["server_modified"]=file.Modified}))};
            }
            var clock=new ReceiptClock(); using var http=new HttpClient(new ReceiptWire(Send,clock));
            var api=new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http,clock,Scenarios.ItemChecks.Settings,_=>{},_=>ValueTask.FromResult<string?>("synthetic"),"test-namespace");
            var d=new Dispatch(api,store);
            (Entry? Entry, FailureDisposition? Failure) outcome=(null,null); bool changed=false;
            try
            {
                var (_,reply)=await d.SubmitAsync(pair,"files/upload",new(id,file.Path,null),id is null ? null : "rev",false,stage,default);
                var member=((JobReply.Terminal)reply).Members.Single();
                outcome=(member.Entry,member.Failure);
            }
            catch(ItemFailedException error){outcome=(null,error.Disposition);}
            catch(ChangedUnderneathException error) when(error.Path==file.Path){changed=true;}
            bool met=changed==(expect=="changed") && expect switch
            {
                "changed" => true,
                "proven" => outcome is ({Id:"id:result"},null),
                "unknown" => outcome is (null,FailureDisposition.UnknownEffect),
                _ => outcome is (null,FailureDisposition.Permanent)
            };
            Check.Require(wrong is null && met && uploads==1 && reads==expectedReads && (mode!="midbody" || wire.Position>0),
                $"A lost upload reply ({mode}, {id ?? "create"}, {wrong}, {outcome.Failure}, {uploads}, {reads}) was credited without the selected ID and digest, missed a proven one, read after an unsent body or a definite refusal, or replayed the write");
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
                // Dropbox echoed the sent digest, so the sent snapshot is our own write; the growth reads as a later local edit.
                if (!before && calls==1 && reply is JobReply.Terminal {Members.Count:1} t && t.Members[0] is ({Id:"id:result"},null,null)) continue;
            }
            catch(ChangedUnderneathException) when(before && calls==0){continue;}
            throw new InvalidDataException("Growth before direct publication sent a stale prefix, or a proven publication with growth after send was left unconfirmed");
        }
    }

    /// <summary>A non-JSON HTTP throttle with a Retry-After survives staging and dispatch as a per-call retry.</summary>
    public static async Task ThrottledWriteAsync()
    {
        await UnreadableThrottleAsync();
        await MixedBatchThrottleAsync();
        await EnsureLookupThrottleAsync();
        await CheckThrottleAsync(false);
        // An unreadable error_summary or reason tag (a lone surrogate escape) is no pause evidence and never drops the wait,
        // with or without a structured error.
        await CheckThrottleAsync(false, """{"error_summary":"\uD800","error":{}}""");
        await CheckThrottleAsync(false, """{"error_summary":"\uD800"}""");
        await CheckThrottleAsync(false, """{"error_summary":"\uD800","error":{"reason":{".tag":"\uD800"}}}""");
    }

    public static Task BodyWaitAsync() => CheckThrottleAsync(true);

    // The wire records the instant headers are returned. W09 samples monotonic time, then wall time;
    // advance only on that wall read so resampling at dispatch or settlement gives a different answer.
    private sealed class ReceiptClock : TimeProvider
    {
        private readonly FakeTimeProvider clock=new(DateTimeOffset.UnixEpoch);
        private bool returned;
        internal long? AtHeaders { get; private set; }
        internal void Headers(){AtHeaders=clock.GetTimestamp();returned=true;}
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override long GetTimestamp() => clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow(){var wall=clock.GetUtcNow();if(returned){returned=false;clock.Advance(TimeSpan.FromTicks(1));}return wall;}
    }
    private sealed class ReceiptWire(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send,ReceiptClock clock):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {var response=await send(request,ct);clock.Headers();return response;}
    }
    private static async Task CheckThrottleAsync(bool bodyOnly, string? body = null)
    {
        var delay = TimeSpan.FromSeconds(7);
        var routes = new List<string>();
        Task<HttpResponseMessage> Busy(HttpRequestMessage request, CancellationToken ct)
        {
            routes.Add(request.RequestUri!.AbsolutePath);
            var reply = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(bodyOnly ? """{"error":{"reason":{".tag":"too_many_write_operations"},"retry_after":7}}""" : body ?? "busy") };
            if (!bodyOnly) reply.Headers.RetryAfter = new(delay);
            return Task.FromResult(reply);
        }
        using var store = new InMemoryStateStore();
        var f = Scenarios.ItemChecks.File(Side.Local,"item",42,"source");
        using var bytes = new MemoryStream([42]);
        var upload = new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        foreach (var name in new[]{"read", "stage", "submit"})
        {
            var clock=new ReceiptClock(); using var http=new HttpClient(new ReceiptWire(Busy,clock));
            var api=new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http,clock,Scenarios.ItemChecks.Settings,_=>{},_=>ValueTask.FromResult<string?>("synthetic"),"test-namespace");
            var dispatch=new Dispatch(api,store);
            int before = routes.Count;
            var expectedRoute = name switch { "read" => "/files/get_metadata", "stage" => "/files/upload_session/start", _ => "/files/delete_v2" };
            try
            {
                if (name == "read") await new global::Massdex.SyncShark.Engine.Dropbox.DropboxListing().ReadOneAsync(api,Scenarios.ItemChecks.Pair(),f.Path,default);
                else if (name == "stage") await upload.StageAsync(api,f.Path,bytes,f,f with { IsFolder=true },default);
                else await dispatch.SubmitAsync("pair","files/delete_v2",new("id:selected",f.Path,null),"rev",false,null,default);
            }
            catch (ItemFailedException e) when (routes.Count == before + 1 && routes[before].EndsWith(expectedRoute,StringComparison.Ordinal) &&
                e.Path == f.Path && e.Cause == (bodyOnly ? TextKey.WaitTooManyWrites : TextKey.DropboxRefused) && e.Disposition == FailureDisposition.TemporaryNoEffect && e.ProviderWait == delay)
            {
                Check.Require(clock.AtHeaders is {} stamp && stamp!=clock.GetTimestamp() && e.ProviderWaitStarted==stamp,
                    "HTTP throttle restarted the provider wait after receipt: " + name);
                continue;
            }
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
        var clock=new ReceiptClock(); using var http=new HttpClient(new ReceiptWire(Send,clock));
        var api=new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http,clock,Scenarios.ItemChecks.Settings,_=>{},_=>ValueTask.FromResult<string?>("synthetic"),"test-namespace");
        var d=new Dispatch(api,store);
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
        catch(ItemFailedException e) when(e.Path==b.Path && e.Cause==TextKey.WaitTooManyWrites && e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==wait && calls==1){Check.Require(clock.AtHeaders is {} stamp && stamp!=clock.GetTimestamp() && e.ProviderWaitStarted==stamp,"Mixed batch restarted the provider wait after receipt");return;}
        throw new InvalidDataException("HTTP 200 member throttle did not carry its original wait or the batch was resent");
    }

    private static async Task EnsureLookupThrottleAsync()
    {
        var pair=Scenarios.ItemChecks.Pair();var wait=TimeSpan.FromSeconds(7);
        using var store=new InMemoryStateStore();using var wire=new EnsureWire(pair.DropboxRoot,wait);using var inner=new HttpMessageInvoker(wire,false); var clock=new ReceiptClock(); using var http=new HttpClient(new ReceiptWire(inner.SendAsync,clock));
        var api=new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http,clock,Scenarios.ItemChecks.Settings,_=>{},_=>ValueTask.FromResult<string?>("synthetic"),"test-namespace");
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
            catch(ItemFailedException e) when(e.Path=="lookup" && e.Cause==TextKey.DropboxRefused && e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==wait && clock.AtHeaders is {} stamp && stamp!=clock.GetTimestamp() && e.ProviderWaitStarted==stamp && wire.Submits==1 && wire.Reads==expectedReads){return;}
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
        // The lookup's 429 names no reason, so its wait binds only the lookup: the good neighbour's read still reaches Dropbox.
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
            calls++; Check.Require(request.RequestUri!.AbsolutePath.EndsWith("/files/delete_v2",StringComparison.Ordinal),"Unexpected unreadable throttle route");
            var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new UnreadableContent()};
            r.Headers.RetryAfter=new(delay);return Task.FromResult(r);
        }
        var clock=new ReceiptClock(); using var http=new HttpClient(new ReceiptWire(Send,clock));
        var api=new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http,clock,Scenarios.ItemChecks.Settings,_=>{},_=>ValueTask.FromResult<string?>("synthetic"),"test-namespace");
        var d=new Dispatch(api,store);
        try { await d.SubmitAsync("pair","files/delete_v2",new("id:selected","item",null),"rev",false,null,default); }
        catch(ItemFailedException e) when(calls==1 && e.Path=="item" && e.Cause==TextKey.DropboxRefused && e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==delay && clock.AtHeaders is {} stamp && stamp!=clock.GetTimestamp() && e.ProviderWaitStarted==stamp){return;}
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
