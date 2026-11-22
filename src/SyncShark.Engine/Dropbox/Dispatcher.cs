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
    private readonly Func<AcceptedJob, Task>? installed;
    private readonly TimeProvider clock = TimeProvider.System;
    private readonly DropboxApi? api;
    private readonly Dictionary<(PairConfig? Pair, string PairId, string Route, CancellationToken Token), List<Pending>> pending = [];

    /// <summary>Supplies the configured transport and provider catalog; acceptance notification follows the durable save.</summary>
    public Dispatcher(DropboxApi api, IStore store, Func<AcceptedJob, Task>? installed = null)
        : this(api.SendAsync, store, Routes.Endpoint, Routes.MaxEntries, installed) { this.api = api; clock = api.Clock; }

    /// <summary>Accepts the configured transport and catalog operations separately for isolated wire checks.</summary>
    public Dispatcher(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, IStore store,
        Func<string, Uri> endpoint, Func<string, int> maxEntries, Func<AcceptedJob, Task>? installed = null)
    {
        this.send = send; this.store = store; this.endpoint = endpoint; this.maxEntries = maxEntries; this.installed = installed;
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
        if (revision is not null) ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        if (route is not (Create or Move or Delete or Upload) || (exclusive && route != Create) ||
            (route is Delete or Move && (member.Id is null || !member.Id.StartsWith("id:", StringComparison.Ordinal) || member.Id.Length == 3)) ||
            (route == Move && string.IsNullOrEmpty(member.Destination)) || (route == Upload) != (upload is not null) ||
            (upload is not null && upload.Path != member.Path)) throw new ArgumentException(nameof(member));
        var item = new Pending(route == Create ? CreateMember(member.Path, exclusive) : member, revision, upload);
        var key = (pair, pairId, route, ct);
        lock (pending)
        {
            if (pending.TryGetValue(key, out var group)) group.Add(item);
            else { pending.Add(key, [item]); _ = DrainAsync(key); }
        }
        return item.Done.Task;
    }

    /// <summary>Save exactly the selected create name and its ensure/exclusive intent, never another item's identity.</summary>
    public static JobMember CreateMember(string path, bool exclusiveCreate) => new(null, path, null) { ExclusiveCreate = exclusiveCreate };

    private async Task DrainAsync((PairConfig? Pair, string PairId, string Route, CancellationToken Token) key)
    {
        await Task.Yield(); // Collect already-ready writes without a timer or fill wait.
        List<Pending> group;
        lock (pending) { group = pending[key]; pending.Remove(key); }
        try
        {
            var batch = key.Route switch { Create => "files/create_folder_batch", Delete => "files/delete_batch", Move => "files/move_batch_v2", _ => FinishBatch };
            var limit = group.Count == 1 ? 1 : maxEntries(batch);
            await Task.WhenAll(group.Chunk(limit).Select(async members =>
            {
                try { await SubmitGroupAsync(key.Pair, key.PairId, key.Route, batch, members, key.Token).ConfigureAwait(false); }
                catch (Exception error) { foreach (var item in members) item.Done.TrySetException(error); }
            })).ConfigureAwait(false);
        }
        catch (Exception error) { foreach (var item in group) item.Done.TrySetException(error); }
    }

    private async Task SubmitGroupAsync(PairConfig? pair, string pairId, string single, string batch, Pending[] group, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool many = group.Length > 1;
        if (many && single == Upload)
        {
            await Task.WhenAll(group.Where(x => x.Upload?.Content is not null).Select(async item =>
            {
                var stage = item.Upload!;
                try { item.Upload = await UploadContent.SessionAsync(send, endpoint, Routes.ContentRequestBytes, stage.Path, stage.Content!, stage.Source, ct, clock).ConfigureAwait(false); }
                catch (Exception error) { item.Done.TrySetException(error); }
            })).ConfigureAwait(false);
            group = group.Where(item => !item.Done.Task.IsCompleted).ToArray();
            if (group.Length == 0) return;
            many = group.Length > 1;
        }
        var first = group[0];
        var route = many ? batch : first.Upload?.SessionId is not null ? Finish : single;
        object[] args = group.Select(x => Argument(pair, single, x)).ToArray();
        object argument = many ? single == Create ? new { paths = args, autorename = false } : new { entries = args } :
            single == Create ? new { path = args[0], autorename = false } : args[0];
        var bytes = !many ? first.Upload?.Content : null;
        using var hash = bytes is null ? null : new ContentHash();
        using var body = bytes is null ? null : new UploadContent.Body(bytes, 0, first.Upload!.Total, hash!, true);
        using var request = route is Upload or Finish ? UploadContent.Request(endpoint(route), argument, (HttpContent?)body ?? new ByteArrayContent([])) :
            new HttpRequestMessage(HttpMethod.Post, endpoint(route)) { Content = JsonContent.Create(argument) };
        try
        {
            using var response = await send(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (body is not null) await body.StopAsync().ConfigureAwait(false);
                var failure = await WriteFailure.ReadAsync(response, clock, ct).ConfigureAwait(false);
                foreach (var item in group)
                {
                    var classified = failure.Classify(route, single == Create || (single == Upload && item.Revision is null));
                    if (classified.Disposition == FailureDisposition.TemporaryNoEffect)
                        item.Done.TrySetException(failure.Error(item.Member.Path, classified));
                    else
                        await SettleAsync(pair, item, single, (null, classified.Cause, classified.Disposition), ct,
                            SelectedIdAbsent(single == Delete, many, item.Member, classified.Absent)).ConfigureAwait(false);
                }
                return;
            }
            if (body is not null) { await body.CompleteAsync(ct).ConfigureAwait(false); first.Digest = hash!.Finish(); }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            if (!many)
            {
                var entry = pair is null ? null : await DropboxReply.ReadAsync(stream, DropboxReplyShapes.Metadata(pair,
                    single == Upload ? "file" : single == Create ? "folder" : null, nested: single != Upload), ct).ConfigureAwait(false);
                await SettleAsync(pair, first, single, (entry, null, null), ct).ConfigureAwait(false);
                return;
            }
            var launch = group.Select(x => x.Member).ToArray();
            AcceptedJob? job = null;
            JobReply reply;
            if (pair is not null) (job, reply) = await DropboxReply.ReadAsync(stream, DropboxReplyShapes.Submit(pair, route, launch), ct).ConfigureAwait(false);
            else
            {
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var root = json.RootElement;
                if (root.TryGetProperty(".tag", out var tag) && tag.GetString() == "async_job_id")
                {
                    var token = root.GetProperty("async_job_id").GetString();
                    if (string.IsNullOrEmpty(token)) throw new InvalidDataException();
                    job = new AcceptedJob(pairId, route, token, launch);
                    reply = new JobReply.Running();
                }
                else reply = DropboxReplyShapes.Job(root, new AcceptedJob(pairId, route, string.Empty, launch));
            }
            if (job is not null)
            {
                await store.SaveJobAsync(job, CancellationToken.None).ConfigureAwait(false);
                if (installed is not null) await installed(job).ConfigureAwait(false);
                foreach (var item in group) item.Done.TrySetResult((job, reply));
                return;
            }
            if (reply is not JobReply.Terminal terminal || terminal.Members.Count != group.Length) throw new InvalidDataException();
            for (int i = 0; i < group.Length; i++)
            {
                var projected = MemberReply(terminal, i);
                var receipt = projected.Members[0];
                if (single == Upload && group[i].Revision is not null && receipt.Cause == TextKey.ClashNameTaken)
                    receipt.Cause = TextKey.FailChanged;
                await SettleAsync(pair, group[i], single, receipt, ct, projected.AbsentOrdinals.Contains(0)).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not NotImplementedException)
        {
            if (body is not null) await body.StopAsync().ConfigureAwait(false);
            foreach (var item in group) item.Done.TrySetException(new ItemFailedException(item.Member.Path, TextKey.FailDropboxOutcomeUnknown, error)
                { Disposition = FailureDisposition.UnknownEffect });
        }
        finally { if (body is not null) await body.StopAsync().ConfigureAwait(false); }
    }

    /// <summary>Only an attributable by-ID delete miss proves absence of that selected identity.</summary>
    public static bool SelectedIdAbsent(bool delete, bool batch, JobMember member, bool absent) => !batch && delete && member.Id is not null && absent;

    /// <summary>Project one selected ordinal for a waiting caller.</summary>
    public static JobReply.Terminal MemberReply(JobReply.Terminal batch, int ordinal) => new([batch.Members[ordinal]])
        { AbsentOrdinals = batch.AbsentOrdinals.Contains(ordinal) ? SingletonAbsent : System.Collections.Frozen.FrozenSet<int>.Empty };
    private static readonly IReadOnlySet<int> SingletonAbsent = System.Collections.Frozen.FrozenSet.ToFrozenSet(new[] { 0 });

    /// <summary>Classify an exception raised while validating an already successful publication reply.</summary>
    public static JobReply.Terminal UnusableReceipt(Exception error) => error is NotImplementedException ? throw error :
        new([(null, TextKey.FailDropboxOutcomeUnknown, FailureDisposition.UnknownEffect)]);

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

    /// <summary>Resolve an attributable create conflict once, identically after an immediate or deferred result.</summary>
    public async Task<JobReply.Terminal> ResolveCreateClashAsync(PairConfig pair, JobMember member, JobReply.Terminal reply, CancellationToken ct)
    {
        if (member.ExclusiveCreate || reply.Members.Count != 1 || reply.Members[0] is not (null, TextKey.ClashNameTaken, FailureDisposition.Permanent)) return reply;
        var existing = await new DropboxListing().ReadOneAsync(api ?? throw new InvalidOperationException(), pair, member.Path, ct).ConfigureAwait(false);
        return existing is {IsFolder:true} && !string.IsNullOrWhiteSpace(existing.Id) && PathNames.Same(existing.Path, member.Path)
            ? new JobReply.Terminal([(existing, null, null)]) : reply;
    }

    private async Task SettleAsync(PairConfig? pair, Pending item, string route,
        (Entry? Entry, TextKey? Cause, FailureDisposition? Failure) receipt, CancellationToken ct, bool absent = false)
    {
        try
        {
            if (route == Create && receipt.Cause == TextKey.ClashNameTaken)
                receipt = (await ResolveCreateClashAsync(pair!, item.Member, new JobReply.Terminal([receipt]), ct).ConfigureAwait(false)).Members[0];
            if (receipt.Cause is null && receipt.Failure is null)
            {
                var entry = receipt.Entry;
                if (route == Upload && entry is not null)
                    DropboxReply.ValidateUploadResult(entry, item.Member.Path, item.Upload!.Total, item.Digest ?? item.Upload.ContentHash!);
                bool proven = entry is not null && !string.IsNullOrEmpty(entry.Id) && (route switch
                {
                    Delete => entry.Id == item.Member.Id,
                    Move => entry.Id == item.Member.Id && PathNames.Same(entry.Path, item.Member.Destination!),
                    Create => entry.IsFolder && PathNames.Same(entry.Path, item.Member.Path),
                    _ => item.Member.Id is null || item.Member.Id == entry.Id
                });
                if (!proven) receipt = (null, TextKey.FailDropboxOutcomeUnknown, FailureDisposition.UnknownEffect);
            }
            item.Done.TrySetResult((null, absent ? new JobReply.Terminal([receipt]) { AbsentOrdinals = SingletonAbsent } : new JobReply.Terminal([receipt])));
        }
        catch (NotImplementedException error) { item.Done.TrySetException(error); }
        catch (Exception error) { item.Done.TrySetResult((null, UnusableReceipt(error))); }
    }

    /// <summary>Read an HTTP write refusal once, preserving the status, Retry-After and monotonic receipt time even without JSON.</summary>
    internal sealed record WriteFailure(HttpStatusCode Status, JsonElement Envelope, TimeSpan? Wait, long Started)
    {
        internal static async Task<WriteFailure> ReadAsync(HttpResponseMessage response, TimeProvider clock, CancellationToken ct)
        {
            var started = clock.GetTimestamp();
            var retry = response.Headers.RetryAfter;
            var header = retry?.Delta ?? retry?.Date - clock.GetUtcNow();
            JsonElement envelope = default;
            try
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                envelope = doc.RootElement.Clone();
            }
            catch (JsonException) { /* Status and header remain attributable even if the body is not JSON. */ }
            var detail = envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("error", out var nested) ? nested : envelope;
            var wait = DropboxProtocol.ProviderWait(header < TimeSpan.Zero ? TimeSpan.Zero : header, detail);
            return new(response.StatusCode, envelope, wait, started);
        }
        internal DropboxErrors.Classified Classify(string route, bool create) => new DropboxErrors().Classify(Status, Envelope, route, create,
            new(false, true, Status == HttpStatusCode.TooManyRequests, Status == HttpStatusCode.TooManyRequests));
        internal ItemFailedException Error(string path, DropboxErrors.Classified failure) => new(path, failure.Cause, null)
        {
            Disposition = failure.Disposition, ProviderWait = Wait, ProviderWaitStarted = Wait is null ? null : Started
        };
    }

    private sealed class Pending(JobMember member, string? revision, DropboxStaged? upload)
    {
        internal readonly JobMember Member = member;
        internal readonly string? Revision = revision;
        internal DropboxStaged? Upload = upload;
        internal string? Digest;
        internal readonly TaskCompletionSource<(AcceptedJob?, JobReply)> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
