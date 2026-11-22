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
        await ThrottledWriteAsync();
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
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            await request.Content!.CopyToAsync(Stream.Null, ct);
            var json = JsonSerializer.Serialize(new { name=file.Path, id="id:result", path_display=path,
                path_lower=path.ToLowerInvariant(), rev="new", size=file.Size + 1, server_modified=file.Modified,
                client_modified=file.Modified, content_hash=file.ContentHash });
            return new(HttpStatusCode.OK) { Content=new StringContent(json) };
        }
        var dispatcher = new Dispatch(Send, store, name => new Uri(new Uri("https://example.invalid/"), name), _ => 2);
        using var bytes = new MemoryStream([42]);
        foreach (var stage in new[]{ global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Single(file.Path,bytes,file),
            global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Session(file.Path,"session",file.Size,file,file.ContentHash) })
        {
            var (_, result) = await dispatcher.SubmitAsync(pair,"files/upload",new(null,file.Path,null),null,false,stage,default);
            Check.Require(result is JobReply.Terminal { Members.Count: 1 } t && t.Members[0].Failure == FailureDisposition.UnknownEffect && t.Members[0].Entry is null,
                "Successful write with unusable uploaded metadata was accepted or refused definitively");
        }
    }

    /// <summary>A non-JSON HTTP throttle with a Retry-After survives staging and dispatch as a per-call retry.</summary>
    public static async Task ThrottledWriteAsync()
    {
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

    // Release the scheduled send only once the three callers are ready, without a timer or a scheduler race.
    private sealed class ReadyWrites : SynchronizationContext
    {
        private readonly Queue<Action> ready = [];
        public override void Post(SendOrPostCallback callback, object? state) => ready.Enqueue(() => callback(state));
        internal void SendReady() { while (ready.TryDequeue(out var action)) action(); }
    }
}
