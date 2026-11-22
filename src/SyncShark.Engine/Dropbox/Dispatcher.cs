using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Collects ready writes for the same pair and route and retains accepted batches in wire order.</summary>
public sealed class Dispatcher
{
    private const string Delete = "files/delete_v2", Move = "files/move_v2", Create = "files/create_folder_v2", Upload = "files/upload";
    private const string Finish = "files/upload_session/finish", FinishBatch = "files/upload_session/finish_batch_v2";
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
    private readonly IStore store;
    private readonly Func<string, Uri> endpoint;
    private readonly Func<string, int> maxEntries;
    private readonly Action<AcceptedJob>? installed;
    private readonly Func<JsonElement, PairConfig, string, IReadOnlyList<JobMember>, (AcceptedJob? Accepted, JobReply Reply)>? results;
    private readonly Func<HttpStatusCode, JsonElement, string, bool, (TextKey Cause, FailureDisposition Failure)> classify;
    private readonly Dictionary<(PairConfig? Pair, string PairId, string Route, CancellationToken Token), List<Pending>> pending = [];

    /// <summary>Supplies the configured transport and provider catalog; acceptance notification follows the durable save.</summary>
    public Dispatcher(DropboxApi api, IStore store, Action<AcceptedJob>? installed = null,
        Func<JsonElement, PairConfig, string, IReadOnlyList<JobMember>, (AcceptedJob? Accepted, JobReply Reply)>? results = null,
        Func<HttpStatusCode, JsonElement, string, bool, (TextKey Cause, FailureDisposition Failure)>? classify = null)
        : this(api.SendAsync, store, Routes.Endpoint, Routes.MaxEntries, installed, results, classify) { }

    /// <summary>Accepts the transport and catalog operations separately for isolated wire checks.</summary>
    public Dispatcher(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, IStore store,
        Func<string, Uri> endpoint, Func<string, int> maxEntries, Action<AcceptedJob>? installed = null,
        Func<JsonElement, PairConfig, string, IReadOnlyList<JobMember>, (AcceptedJob? Accepted, JobReply Reply)>? results = null,
        Func<HttpStatusCode, JsonElement, string, bool, (TextKey Cause, FailureDisposition Failure)>? classify = null)
    {
        this.send = send; this.store = store; this.endpoint = endpoint; this.maxEntries = maxEntries; this.installed = installed;
        this.results = results;
        this.classify = classify ?? ((status, json, _, _) => (TextKey.DropboxRefused, new DropboxErrors().Classify(status, json)));
    }

    /// <summary>Submits an identity-only delete; a destination name requires the configured pair.</summary>
    public Task<(AcceptedJob? Accepted, JobReply MemberReply)> SubmitAsync(string pairId, string route, JobMember member,
        string? expectedRevision, bool exclusiveCreate, DropboxStaged? upload, CancellationToken ct) => route == Delete
        ? Enqueue(null, pairId, route, member, expectedRevision, exclusiveCreate, upload, ct) : throw new ArgumentException(nameof(route));

    /// <summary>Uses the loaded root for path-addressed destinations; deletes and move sources use only the selected ID.</summary>
    public Task<(AcceptedJob? Accepted, JobReply MemberReply)> SubmitAsync(PairConfig pair, string route, JobMember member,
        string? expectedRevision, bool exclusiveCreate, DropboxStaged? upload, CancellationToken ct) =>
        Enqueue(pair, pair.PairId, route, member, expectedRevision, exclusiveCreate, upload, ct);

    private Task<(AcceptedJob?, JobReply)> Enqueue(PairConfig? pair, string pairId, string route, JobMember member,
        string? revision, bool exclusive, DropboxStaged? upload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (route == Finish) route = Upload;
        if (route is not (Create or Move or Delete or Upload) || (exclusive && route != Create) ||
            (route is Delete or Move && (member.Id is null || !member.Id.StartsWith("id:", StringComparison.Ordinal) || member.Id.Length == 3)) ||
            (route == Move && string.IsNullOrEmpty(member.Destination)) || (route == Upload) != (upload is not null) ||
            (upload is not null && upload.Path != member.Path)) throw new ArgumentException(nameof(member));
        var item = new Pending(route == Create ? member with { Id = null, Destination = null } : member, revision, exclusive, upload);
        var key = (pair, pairId, route, ct);
        lock (pending)
        {
            if (pending.TryGetValue(key, out var group)) group.Add(item);
            else { pending.Add(key, [item]); _ = DrainAsync(key); }
        }
        return item.Done.Task;
    }

    private async Task DrainAsync((PairConfig? Pair, string PairId, string Route, CancellationToken Token) key)
    {
        await Task.Yield(); // Collect already-ready writes without a timer or fill wait.
        List<Pending> group;
        lock (pending) { group = pending[key]; pending.Remove(key); }
        try
        {
            var batch = key.Route switch { Create => "files/create_folder_batch", Delete => "files/delete_batch", Move => "files/move_batch_v2", _ => FinishBatch };
            var limit = group.Count == 1 ? 1 : maxEntries(batch);
            foreach (var members in group.Chunk(limit))
            {
                try { await SubmitGroupAsync(key.Pair, key.PairId, key.Route, batch, members, key.Token).ConfigureAwait(false); }
                catch (Exception error) { foreach (var item in members) item.Done.TrySetException(error); }
            }
        }
        catch (Exception error) { foreach (var item in group) item.Done.TrySetException(error); }
    }

    private async Task SubmitGroupAsync(PairConfig? pair, string pairId, string single, string batch, Pending[] group, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool many = group.Length > 1;
        if (many && single == Upload)
            await Task.WhenAll(group.Where(x => x.Upload?.Content is not null).Select(async x =>
            {
                var stage = x.Upload!;
                x.Upload = await UploadContent.SessionAsync(send, endpoint, Routes.ContentRequestBytes, stage.Path, stage.Content!, stage.Source, ct).ConfigureAwait(false);
            })).ConfigureAwait(false);
        var first = group[0];
        var route = many ? batch : first.Upload?.SessionId is not null ? Finish : single;
        object[] args = group.Select(x => Argument(pair, single, x)).ToArray();
        object argument = many ? single == Create ? new { paths = args, autorename = false } : new { entries = args } :
            single == Create ? new { path = args[0], autorename = false } : args[0];
        using var hash = new ContentHash();
        using var body = !many && first.Upload?.Content is Stream bytes ? new UploadContent.Body(bytes, 0, first.Upload.Total, hash, true) : null;
        using var request = route is Upload or Finish ? UploadContent.Request(endpoint(route), argument, (HttpContent?)body ?? new ByteArrayContent([])) :
            new HttpRequestMessage(HttpMethod.Post, endpoint(route)) { Content = JsonContent.Create(argument) };
        try
        {
            using var response = await send(request, ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var root = json.RootElement;
            if (!response.IsSuccessStatusCode)
            {
                foreach (var item in group)
                {
                    var failure = classify(response.StatusCode, root, route, item.Revision is null);
                    await SettleAsync(pair, item, single, (null, failure.Cause, failure.Failure), ct).ConfigureAwait(false);
                }
                return;
            }
            if (body is not null) { await body.CompleteAsync(ct).ConfigureAwait(false); first.Digest = hash.Finish(); }
            if (!many)
            {
                var metadata = single == Upload ? root : root.GetProperty("metadata");
                var entry = pair is null ? null : DropboxReply.Metadata(metadata, pair);
                await SettleAsync(pair, first, single, (entry, null, null), ct).ConfigureAwait(false);
                return;
            }
            var launch = group.Select(x => x.Member).ToArray();
            AcceptedJob? job = null;
            JobReply reply;
            if (results is not null && pair is not null) (job, reply) = results(root, pair, route, launch);
            else if (route != FinishBatch && root.TryGetProperty(".tag", out var tag) && tag.GetString() == "async_job_id")
            {
                var token = root.GetProperty("async_job_id").GetString();
                if (string.IsNullOrEmpty(token)) throw new InvalidDataException();
                job = new AcceptedJob(pairId, route, token, launch);
                reply = new JobReply.Running();
            }
            else reply = DropboxReplyShapes.Job(root, new AcceptedJob(pairId, route, string.Empty, launch));
            if (job is not null)
            {
                await store.SaveJobAsync(job, CancellationToken.None).ConfigureAwait(false);
                installed?.Invoke(job);
                foreach (var item in group) item.Done.TrySetResult((job, reply));
                return;
            }
            if (reply is not JobReply.Terminal terminal || terminal.Members.Count != group.Length) throw new InvalidDataException();
            for (int i = 0; i < group.Length; i++)
                await SettleAsync(pair, group[i], single, terminal.Members[i], ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not NotImplementedException)
        {
            foreach (var item in group) item.Done.TrySetException(new ItemFailedException(item.Member.Path, TextKey.FailDropboxOutcomeUnknown, error)
                { Disposition = FailureDisposition.UnknownEffect });
        }
        finally { if (body is not null) await body.StopAsync().ConfigureAwait(false); }
    }

    private static object Argument(PairConfig? pair, string route, Pending item)
    {
        var m = item.Member;
        if (route == Delete) return string.IsNullOrEmpty(item.Revision) ? new { path = m.Id } : new { path = m.Id, parent_rev = item.Revision };
        if (route == Move) return new { from_path = m.Id, to_path = DropboxPaths.Remote(pair!, m.Destination!) };
        var path = DropboxPaths.Remote(pair!, m.Path);
        if (route == Create) return path;
        var stage = item.Upload!;
        object mode = item.Revision is null ? "add" : new Dictionary<string, string> { [".tag"] = "update", ["update"] = item.Revision };
        var commit = new { path, mode, autorename = false, strict_conflict = true,
            client_modified = stage.Source.Modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
        return stage.SessionId is null ? commit : new { cursor = new { session_id = stage.SessionId, offset = stage.Total }, commit };
    }

    private async Task SettleAsync(PairConfig? pair, Pending item, string route,
        (Entry? Entry, TextKey? Cause, FailureDisposition? Failure) receipt, CancellationToken ct)
    {
        try
        {
            if (route == Create && !item.Exclusive && receipt.Cause == TextKey.ClashNameTaken && receipt.Failure == FailureDisposition.Permanent)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint("files/get_metadata"))
                    { Content = JsonContent.Create(new { path = DropboxPaths.Remote(pair!, item.Member.Path) }) };
                using var reply = await send(request, ct).ConfigureAwait(false);
                reply.EnsureSuccessStatusCode();
                using var json = await JsonDocument.ParseAsync(await reply.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                var existing = DropboxReply.Metadata(json.RootElement, pair!);
                if (existing.IsFolder) receipt = (existing, null, null);
            }
            if (receipt.Cause is null && receipt.Failure is null)
            {
                var entry = receipt.Entry;
                bool proven = entry is not null && (route switch
                {
                    Delete => entry.Id == item.Member.Id,
                    Move => entry.Id == item.Member.Id && PathNames.Same(entry.Path, item.Member.Destination!),
                    Create => entry.IsFolder && PathNames.Same(entry.Path, item.Member.Path),
                    _ => !entry.IsFolder && PathNames.Same(entry.Path, item.Member.Path) && entry.Size == item.Upload!.Total &&
                        StringComparer.OrdinalIgnoreCase.Equals(entry.ContentHash, item.Digest ?? item.Upload.ContentHash)
                });
                if (!proven) receipt = (null, TextKey.FailDropboxOutcomeUnknown, FailureDisposition.UnknownEffect);
            }
            item.Done.TrySetResult((null, new JobReply.Terminal([receipt])));
        }
        catch (Exception error) { item.Done.TrySetException(error); }
    }

    private sealed class Pending(JobMember member, string? revision, bool exclusive, DropboxStaged? upload)
    {
        internal readonly JobMember Member = member;
        internal readonly string? Revision = revision;
        internal readonly bool Exclusive = exclusive;
        internal DropboxStaged? Upload = upload;
        internal string? Digest;
        internal readonly TaskCompletionSource<(AcceptedJob?, JobReply)> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
