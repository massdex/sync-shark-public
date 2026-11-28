namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for UploadContent.</summary>
public static class UploadContentChecks
{
    // Stream.CopyToAsync(Stream, int, CancellationToken): Microsoft Learn documents the default buffer as 81,920 bytes.
    public const int CopyBufferBytes = 81_920;

    /// <summary>Verifies the UploadContent contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        await DirectAndTruncatedAsync();
        await ResendAsync();
        await WrappedShrinkAsync();
    }

    // A transport re-send of a single-use slice body: every re-serialization throws one identity, raw or wrapped.
    private static async Task ResendAsync()
    {
        var writer = new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        byte[] bytes = [1, 2, 3];
        using var hash = new ContentHash(); hash.Append(bytes);
        var file = Scenarios.ItemChecks.File(Side.Local, "item", 3, "source") with { Size = 3, ContentHash = hash.Finish() };
        var folder = file with { IsFolder = true };
        static Uri Endpoint(string name) => new(new Uri("https://example.invalid/"), name);
        static HttpResponseMessage Session() => new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"session_id\":\"receipt\"}") };
        foreach (bool raw in new[] { true, false })
        {
            async Task<HttpResponseMessage> Resent(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri!.Segments[^1] == "start") { await request.Content!.CopyToAsync(Stream.Null, ct); return Session(); }
                var first = request.Content!.CopyToAsync(Stream.Null, ct);
                Exception? duplicate = null;
                try { await request.Content.CopyToAsync(Stream.Null, ct); } catch (InvalidOperationException e) { duplicate = e; }
                try { await request.Content.CopyToAsync(Stream.Null, ct); } catch (InvalidOperationException) { }
                await first;
                throw raw ? duplicate! : new HttpRequestException("resent", duplicate);
            }
            try { await using var stage = await writer.StageAsync(Resent, Endpoint, 2, file.Path, new Sequential(bytes), file, folder, default); }
            catch (ItemFailedException e) when (e.Path == file.Path && e.Cause == TextKey.RequestIncomplete && e.Disposition == FailureDisposition.TemporaryNoEffect) { continue; }
            throw new InvalidDataException("A re-sent slice body was not a timed retry (" + (raw ? "raw" : "wrapped") + ")");
        }
        // The next attempt starts a fresh session and stages the selected bytes.
        var routes = new List<string>(); var received = new List<byte>();
        async Task<HttpResponseMessage> Fresh(HttpRequestMessage request, CancellationToken ct)
        {
            routes.Add(request.RequestUri!.Segments[^1]);
            received.AddRange(await request.Content!.ReadAsByteArrayAsync(ct));
            return Session();
        }
        await using (var restaged = await writer.StageAsync(Fresh, Endpoint, 2, file.Path, new Sequential(bytes), file, folder, default))
            Check.Require(routes.FirstOrDefault() == "start" && restaged.ContentHash == file.ContentHash && received.SequenceEqual(bytes),
                "A retry after a re-send did not restage from a fresh session");
        // Not a re-send: a swallowed duplicate followed by a reply keeps the reply's own classification.
        foreach (var (status, json, expected) in new[] {
            (System.Net.HttpStatusCode.OK, "{", FailureDisposition.Permanent),
            (System.Net.HttpStatusCode.BadRequest, "{}", FailureDisposition.Permanent),
            (System.Net.HttpStatusCode.TooManyRequests, "{}", FailureDisposition.TemporaryNoEffect) })
        {
            async Task<HttpResponseMessage> Swallowed(HttpRequestMessage request, CancellationToken ct)
            {
                await request.Content!.CopyToAsync(Stream.Null, ct);
                try { await request.Content.CopyToAsync(Stream.Null, ct); } catch (InvalidOperationException) { }
                var reply = new HttpResponseMessage(status) { Content = new StringContent(json) };
                if (status == System.Net.HttpStatusCode.TooManyRequests) reply.Headers.RetryAfter = new(TimeSpan.FromSeconds(7));
                return reply;
            }
            try { await using var stage = await writer.StageAsync(Swallowed, Endpoint, 2, file.Path, new Sequential(bytes), file, folder, default); }
            catch (ItemFailedException e) when (e.Path == file.Path && e.Disposition == expected && e.Cause != TextKey.RequestIncomplete &&
                (expected == FailureDisposition.Permanent || e.ProviderWait == TimeSpan.FromSeconds(7))) { continue; }
            throw new InvalidDataException("A swallowed duplicate changed the reply's classification: " + status);
        }
        using var cancel = new CancellationTokenSource();
        async Task<HttpResponseMessage> Cancelled(HttpRequestMessage request, CancellationToken ct)
        {
            await request.Content!.CopyToAsync(Stream.Null, ct);
            try { await request.Content.CopyToAsync(Stream.Null, ct); } catch (InvalidOperationException) { }
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        }
        try { await writer.StageAsync(Cancelled, Endpoint, 2, file.Path, new Sequential(bytes), file, folder, cancel.Token); throw new InvalidDataException("Cancelled stage returned"); }
        catch (OperationCanceledException) { }
        // Serialized late, after completion closed the unsent body: refused, but never as this body's re-send.
        using var emptyHash = new ContentHash();
        var empty = file with { Size = 0, ContentHash = emptyHash.Finish() };
        HttpContent? closed = null;
        Exception? late = null, later = null;
        Task<HttpResponseMessage> Late(HttpRequestMessage request, CancellationToken _)
        {
            closed = request.Content!;
            // The reply is read after completion has claimed the body; serialize it there.
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new AfterRead(
                "{\"session_id\":\"receipt\"}"u8.ToArray(), () =>
                {
                    try { closed.CopyToAsync(Stream.Null).GetAwaiter().GetResult(); } catch (Exception e) { late = e; }
                    try { closed.CopyToAsync(Stream.Null).GetAwaiter().GetResult(); } catch (Exception e) { later = e; }
                })) });
        }
        await using (var stage = await writer.StageAsync(Late, Endpoint, 2, empty.Path, new MemoryStream(), empty, folder, default))
            Check.Require(stage.SessionId == "receipt", "A closed empty slice did not stage");
        // A re-send refusal is one shared instance; two late refusals of a closed body must be distinct.
        Check.Require(late is InvalidOperationException && later is InvalidOperationException && !ReferenceEquals(late, later),
            "A serialization after completion closed the body was recorded as a transport re-send");
    }

    // Runs once before the first read of the reply body.
    private sealed class AfterRead(byte[] bytes, Action first) : MemoryStream(bytes, writable: false)
    {
        private Action? pending = first;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Interlocked.Exchange(ref pending, null)?.Invoke();
            return base.ReadAsync(buffer, ct);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Exchange(ref pending, null)?.Invoke();
            return base.Read(buffer, offset, count);
        }
    }

    // A truncated seekable source surfaces through the transport's wrapper as a changed source, not a refusal.
    private static async Task WrappedShrinkAsync()
    {
        var writer = new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        var file = Scenarios.ItemChecks.File(Side.Local, "item", 3, "source") with { Size = 3 };
        var folder = file with { IsFolder = true };
        static Uri Endpoint(string name) => new(new Uri("https://example.invalid/"), name);
        foreach (var (param, cancelled) in new[] { ("offset", false), ("length", false), ("count", false), ("offset", true) })
        {
            using var source = new MemoryStream(); source.Write([1, 2, 3]); source.Position = 0;
            using var cancel = new CancellationTokenSource();
            Task<HttpResponseMessage> Wrapped(HttpRequestMessage _, CancellationToken ct)
            {
                source.SetLength(1);
                if (cancelled) cancel.Cancel();
                throw new HttpRequestException("wrapped", new ArgumentOutOfRangeException(param));
            }
            try { await writer.StageAsync(Wrapped, Endpoint, 2, file.Path, source, file, folder, cancel.Token); }
            catch (OperationCanceledException) when (cancelled) { continue; }
            catch (ChangedUnderneathException e) when (!cancelled && param != "count" && e.Path == file.Path) { continue; }
            catch (ItemFailedException e) when (!cancelled && param == "count" && e.Path == file.Path && e.Disposition == FailureDisposition.Permanent) { continue; }
            throw new InvalidDataException("A wrapped truncation was misclassified: " + param + (cancelled ? " cancelled" : string.Empty));
        }
    }

    public static async Task AppendAsync()
    {
        var writer = new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        var file = Scenarios.ItemChecks.File(Side.Local, "item", 42, "source");
        foreach (var (status, expected) in new[] {
            (System.Net.HttpStatusCode.InternalServerError, FailureDisposition.TemporaryNoEffect),
            (System.Net.HttpStatusCode.BadRequest, FailureDisposition.Permanent) })
        {
            using var content = new Sequential([42]);
            try
            {
                await using var stage = await writer.StageAsync((_, _) => Task.FromResult(new HttpResponseMessage(status)),
                    name => new Uri(new Uri("https://example.invalid/"), name), file.Size, file.Path, content, file, null, default);
                throw new InvalidOperationException("Refused session was staged");
            }
            catch (ItemFailedException error) { Check.Require(error.Disposition == expected, "Wrong session refusal disposition: " + status); }
        }
        await StageWindowsAsync(string.Empty);
        await StageWindowsAsync(null); // No content length; EOF is the only indication that the void reply is empty.
        await StageWindowsAsync("{}");
        await MalformedStageAsync();
    }
    private static async Task StageWindowsAsync(string? appendReply)
    {
        var writer = new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        var file = Scenarios.ItemChecks.File(Side.Local, "item", 42, "source");
        var folder = Scenarios.ItemChecks.File(Side.Dropbox, "item", 0, "victim", true);
        var received = new List<byte>();
        bool closed = false; string? appendSession = null;
        var windows = new List<(string Route, long Offset, int Count, bool Closed)>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            using var args = System.Text.Json.JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            var chunk = await request.Content!.ReadAsByteArrayAsync(ct);
            received.AddRange(chunk);
            closed = args.RootElement.GetProperty("close").GetBoolean();
            var append = args.RootElement.TryGetProperty("cursor", out var cursor);
            if (append) appendSession = cursor.GetProperty("session_id").GetString();
            windows.Add((request.RequestUri!.Segments[^1], append ? cursor.GetProperty("offset").GetInt64() : 0, chunk.Length, closed));
            return new(System.Net.HttpStatusCode.OK) { Content = append && appendReply is null ? new StreamContent(new Sequential([])) : new StringContent(append ? appendReply! : "{\"session_id\":\"receipt\"}") };
        }
        using var bytes = new MemoryStream([42]);
        static Uri Endpoint(string name) => new(new Uri("https://example.invalid/"), name);
        await using var staged = await writer.StageAsync(Send, Endpoint, 2, file.Path, bytes, file, folder, default);
        Check.Require(closed && received.SequenceEqual(new byte[]{42}) && staged.SessionId == "receipt" &&
            !staged.ConsumesSourceOnPublish && staged.ContentHash == file.ContentHash,
            "Folder replacement kept a live source instead of closing a session containing its bytes");
        received.Clear(); windows.Clear();
        using var recovery = new Sequential([1,2,3]);
        using var hash = new ContentHash(); hash.Append([1,2,3]);
        var recovered = file with { Size = 3, ContentHash = hash.Finish() };
        await using var restored = await writer.StageAsync(Send, Endpoint, 2, file.Path, recovery, recovered, null, default);
        Check.Require(appendSession == "receipt", "Append named a different session from start");
        Check.Require(windows.SequenceEqual(new[]{("start",0L,2,false),("append_v2",2L,1,true)}) &&
            restored.ContentHash == recovered.ContentHash && received.SequenceEqual(new byte[]{1,2,3}),
            "Nonseekable recovery did not stage all bytes in bounded windows");
    }
    private static async Task DirectAndTruncatedAsync()
    {
        var writer=new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        var file=Scenarios.ItemChecks.File(Side.Local,"item",42,"source");
        var folder=file with {IsFolder=true};
        int sends=0;
        static Uri Endpoint(string name)=>new(new Uri("https://example.invalid/"),name);
        async Task<HttpResponseMessage> Send(HttpRequestMessage request,CancellationToken ct)
        {
            sends++;
            await request.Content!.CopyToAsync(Stream.Null,ct);
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"session_id\":\"receipt\"}")};
        }
        var payload = new byte[CopyBufferBytes + 1];
        using (var input = new Sequential(payload))
        using (var hash = new ContentHash())
        {
            hash.Append(payload);
            var large = file with { Size = payload.Length, ContentHash = hash.Finish() };
            await using var stage = await writer.StageAsync(Send, Endpoint, large.Size, large.Path, input, large, folder, default);
            Check.Require(input.LargestRead == CopyBufferBytes && stage.ContentHash == large.ContentHash && sends == 1,
                "Upload did not copy the selected bytes in stream-sized reads");
        }
        sends = 0;
        using var directBytes = new MemoryStream([42]);
        await using var direct = await writer.StageAsync(Send, Endpoint, 2, file.Path, directBytes, file, null, default);
        Check.Require(ReferenceEquals(direct.Content, directBytes) && direct.ConsumesSourceOnPublish && sends == 0,
            "A sole small ordinary upload was sent before publication");
        var failures = new List<string>();
        foreach (bool disconnect in new[]{false,true})
        {
            using var shortRead = new Sequential([1]);
            try
            {
                async Task<HttpResponseMessage> Attempt(HttpRequestMessage request, CancellationToken ct)
                {
                    if (disconnect) throw new HttpRequestException(HttpRequestError.ConnectionError,"Disconnected");
                    return await Send(request,ct);
                }
                await using var stage = await writer.StageAsync(Attempt,Endpoint,2,file.Path,shortRead,file with { Size=3 },folder,default);
            }
            catch (ItemFailedException error) when (error.Path == file.Path && (disconnect ? error.Cause == TextKey.RequestIncomplete && error.Disposition == FailureDisposition.TemporaryNoEffect : error.Cause == TextKey.DropboxRefused && sends==1 && error.Disposition == FailureDisposition.Permanent && (error.InnerException is EndOfStreamException or HttpRequestException { InnerException:EndOfStreamException }))) { continue; }
            catch (NotImplementedException) { throw; }
            catch (Exception) { }
            failures.Add(disconnect ? "broken connection" : "short source");
        }
        using var shrinking = new MemoryStream();shrinking.Write([1,2,3]);shrinking.Position=0;
        var selected=file with { Size=3 }; int attempts=0;
        async Task<HttpResponseMessage> Shrink(HttpRequestMessage request,CancellationToken ct)
        {
            attempts++;shrinking.SetLength(1);
            await request.Content!.CopyToAsync(Stream.Null,ct);
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"session_id\":\"receipt\"}")};
        }
        bool changed=false;
        try { await writer.StageAsync(Shrink, Endpoint, 2, file.Path, shrinking, selected, folder, default); }
        catch(ChangedUnderneathException e) when(e.Path==file.Path){changed=true;}
        catch(NotImplementedException){throw;}
        catch(Exception){ }
        if (!changed || attempts!=1) failures.Add("seekable window truncation escaped ChangedUnderneath");
        using var midRead = new ShrinkOnRead([1,2,3]);
        int before = sends; bool caught = false;
        try { await writer.StageAsync(Send, Endpoint, 2, file.Path, midRead, selected, folder, default); }
        catch (ChangedUnderneathException e) when (e.Path == file.Path && sends == before + 1) { caught = true; }
        catch (NotImplementedException) { throw; }
        catch (Exception) { }
        if (!caught) failures.Add("seekable EOF after window construction escaped ChangedUnderneath");
        var clock=new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch); var delay=TimeSpan.FromSeconds(7);
        var receipt=clock.GetTimestamp(); int refusedSends=0;
        Task<HttpResponseMessage> Refused(HttpRequestMessage _,CancellationToken ct)
        {
            refusedSends++; clock.Advance(TimeSpan.FromTicks(1));
            throw new ItemFailedException(file.Path,TextKey.WaitTooManyWrites,null)
                {Disposition=FailureDisposition.TemporaryNoEffect,ProviderWait=delay,ProviderWaitStarted=receipt};
        }
        try { await writer.StageAsync(Refused,Endpoint,2,file.Path,directBytes,file,folder,default,clock); throw new InvalidDataException("Staging accepted a refused request"); }
        catch(ItemFailedException e) when (e.Path==file.Path && e.Cause==TextKey.WaitTooManyWrites && e.Disposition==FailureDisposition.TemporaryNoEffect && e.ProviderWait==delay && refusedSends==1)
        {
            Check.Require(e.ProviderWaitStarted==receipt && receipt!=clock.GetTimestamp(),"Staging restarted an already classified provider wait");
        }
        Check.Require(refusedSends==1,"Stage did not attempt the refused request exactly once");
        Check.Require(failures.Count==0, "Stage did not return the typed item failure: "+string.Join(", ",failures));
    }
    private static async Task MalformedStageAsync()
    {
        foreach (bool badStart in new[]{true,false})
        {
            var file=Scenarios.ItemChecks.File(Side.Local,"item",42,"pc") with { Size=2 };
            using var source=new Sequential([42,42]);
            int sends=0;
            async Task<HttpResponseMessage> Send(HttpRequestMessage request,CancellationToken ct)
            {
                await request.Content!.CopyToAsync(Stream.Null,ct);
                var json=++sends==1 ? badStart ? "{}" : "{\"session_id\":\"receipt\"}" : "{";
                return new(System.Net.HttpStatusCode.OK){Content=new StringContent(json)};
            }
            try { await new global::Massdex.SyncShark.Engine.Dropbox.UploadContent().StageAsync(Send, name=>new Uri(new Uri("https://example.invalid/"),name),1,file.Path,source,file,null,default); }
            catch (ItemFailedException e) when(e.Path==file.Path && e.Cause==TextKey.DropboxRefused && sends==(badStart?1:2)) { continue; }
            catch (NotImplementedException) { throw; }
            catch (Exception e) { throw new InvalidDataException("Malformed session acknowledgement escaped the item's failure path",e); }
            throw new InvalidDataException("Malformed session acknowledgement returned a usable stage");
        }
    }
    /// <summary>Drive the real replace action, not StageAsync in isolation; the wire records any premature delete even if the executor catches its error.</summary>
    public static Task ReplacementOrderAsync() => CheckReplacementAsync(false, false);
    public static Task ReplacementRejectedAsync() => CheckReplacementAsync(true, false);
    public static Task ReplacementUnreadableAsync() => CheckReplacementAsync(false, true);
    private static async Task CheckReplacementAsync(bool reject, bool unreadable)
    {
        var pair = Scenarios.ItemChecks.Pair();
        pair = pair with { Roots = new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,1,null),new("account","test-namespace","home","id:root")) };
        var file = Scenarios.ItemChecks.File(Side.Local, "item", 42, "pc");
        var folder = Scenarios.ItemChecks.File(Side.Dropbox, "item", 0, "id:victim", true);
        var wire = new ReplacementWire(pair.DropboxRoot.TrimEnd('/') + "/item", file, folder, reject);
        var paths = new PathScope(pair, [pair], DriveType.Fixed);
        using var http = new HttpClient(wire);
        using var store = new TestDoubles.InMemoryStateStore();
        var api = new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http, TimeProvider.System, Scenarios.ItemChecks.Settings, _ => {},
            _ => ValueTask.FromResult<string?>("synthetic"), "test-namespace");
        ISide target = new global::Massdex.SyncShark.Engine.Dropbox.DropboxSide(pair, () => paths, api,
            new global::Massdex.SyncShark.Engine.Dropbox.Dispatcher(api, store));
        var local = new TestDoubles.FakeSide(Side.Local); local.Seed(file, [42]);
        if (unreadable) local.ContentFailures[file.Path]=new ItemFailedException(file.Path,TextKey.ReadLocalFailed,null);
        var row = new Row(PathNames.DropboxKey(folder.Path), file, folder, null, [], file.ContentHash, RowContext.Neutral with { ProtectedContents = false });
        JournalPosition? settled=null;
        var queue = new global::Massdex.SyncShark.Engine.Run.WorkQueue(_=>Task.FromResult(settled));
        var projected = new JournalPosition(1,2);
        var page = new ChangePage([],null,projected){ProjectedThrough=projected};
        // Keep the captured journal boundary observable after the finite read, so the covered physical hop can elide its rescan.
        local.Pages.Enqueue(page); local.Pages.Enqueue(page); local.Pages.Enqueue(page);
        var before = await target.BoundaryAsync(default);
        // Admit the starting prefixes before removal; the post-delete check still needs the actual deletion echo.
        var initialLocal = await local.ReadChangesAsync(null,null,(before.Cursor,projected),default);
        var initialCloud = await target.ReadChangesAsync(before.Cursor,null,(before.Cursor,projected),default);
        await queue.AdmitAsync(pair,initialLocal,(_,_)=>throw new InvalidDataException("Unexpected journal change"),async (at,ct)=>{await local.SettleJournalAsync(at,ct);settled=at;},default);
        await queue.AdmitAsync(pair,initialCloud,(_,_)=>throw new InvalidDataException("Unexpected feed change"),null,(cursor,ct)=>store.SaveCursorAsync(pair.PairId,cursor,ct),null,default);
        bool covered=false;
        bool Covered((JournalPosition? Local,string? Dropbox) fence)
        {
            if (wire.Deletes==0) return queue.CoversHop(fence);
            if (wire.Deletes!=1 || fence.Local!=projected || string.IsNullOrEmpty(fence.Dropbox) || fence.Dropbox==before.Cursor) return false;
            var until=(fence.Dropbox,fence.Local);
            var pc=local.ReadChangesAsync(null,null,until,default).GetAwaiter().GetResult();
            var db=target.ReadChangesAsync(before.Cursor,null,until,default).GetAwaiter().GetResult();
            if (!pc.CaughtUp || pc.ContinuityLost || pc.ProjectedThrough!=fence.Local || pc.Position!=pc.ProjectedThrough || pc.Changes.Count!=0 ||
                !db.CaughtUp || db.ContinuityLost || db.Cursor!=fence.Dropbox || db.Changes.Count!=1 ||
                db.Changes[0] is not {Side:Side.Dropbox,Kind:ChangeKind.Deleted} echo || echo.Path!=folder.Path) return false;
            // Settle the empty projected journal interval and exactly the echo of this fixture's confirmed removal.
            queue.AdmitAsync(pair,pc,(_,_)=>throw new InvalidDataException("Unexpected journal change"),async (at,ct)=>{await local.SettleJournalAsync(at,ct);settled=at;},default).GetAwaiter().GetResult();
            queue.AdmitAsync(pair,db,(change,_)=>change==echo ? Task.CompletedTask : throw new InvalidDataException("Unexpected feed change"),
                null,(cursor,ct)=>store.SaveCursorAsync(pair.PairId,cursor,ct),null,default).GetAwaiter().GetResult();
            return covered=queue.CoversHop(fence);
        }
        var action = new global::Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.ReplaceKind, row, Side: Side.Dropbox);
        if (reject || unreadable)
        {
            // Start with the actual observed folder: ItemSync also verifies the selected row before an effect.
            var observed = await target.ReadOneAsync(folder.Path,default) ?? throw new InvalidDataException("Missing selected folder");
            var baseline = SyncedEntries.From(file with {IsFolder=true,Size=0,ContentHash=""},observed);
            await store.CommitAsync(pair.PairId,null,baseline,null,null,default);
            row = row with {Dropbox=observed,Synced=baseline};
            var events = new List<RunEvent>(); var retained = new List<string>();
            var cause = unreadable ? TextKey.ReadLocalFailed : TextKey.DropboxRefused;
            try
            {
                await global::Massdex.SyncShark.Engine.Executor.RowExecution.ApplyAsync(pair,Scenarios.ItemChecks.Settings,events.Add,paths,row,[],local,target,store,
                    Scenarios.ItemChecks.Population(pair,paths,local,target,store),Scenarios.ItemChecks.Hop(pair,paths,queue,local,target,store),default,heldInput:queue.Held,coversThrough:Covered,
                    failed:(key,reproducible)=>{if (!reproducible) retained.Add(key);});
            }
            catch (ItemFailedException error) when (error.Path == file.Path && error.Cause == cause) { }
            if (unreadable) Check.Require(local.Requests[nameof(ISide.OpenReadAsync)]==1 && !wire.StageAttempted && wire.Deletes==0 && wire.Finishes==0,"Source read failed but replacement removed the folder or published");
            else Check.Require(wire.StageAttempted && wire.Deletes==0 && wire.Finishes==0,"Rejected stage removed the selected folder or published");
            var evidence = store.OpenNotices.Where(n=>n.Kind==NoticeKind.Failure && n.Path==file.Path && n.Cause==cause &&
                n.Subject.Items.Any(m=>m.Side==Side.Local && m.Path==file.Path && m.Id==file.Id)).ToArray();
            Check.Require(retained.SequenceEqual(new[]{row.Key}) && evidence.Length==1 && store.Records.Values.Single()==baseline &&
                events.Any(e=>e.Kind==RunEventKind.Failed && e.Path==file.Path && e.Cause==cause) &&
                !events.Any(e=>e.Kind==RunEventKind.Effect && e.Outcome=="Applied"), "Replacement failure was applied, lost its selected item, or omitted its ordinary Failure notice");
            return;
        }
        bool applied;
        try
        {
            var outcome=await new global::Massdex.SyncShark.Engine.Executor.Effects().ApplyAsync(pair,Scenarios.ItemChecks.Settings,_=>{},paths,action,local,target,store,
                Scenarios.ItemChecks.Population(pair,paths,local,target,store),Scenarios.ItemChecks.Hop(pair,paths,queue,local,target,store),default,heldInput:queue.Held,coversThrough:Covered);
            applied=outcome==global::Massdex.SyncShark.Engine.Executor.EffectOutcome.Applied;
        }
        catch(Exception error) when(wire.Deletes>0 && !wire.ClosedBeforeDelete && error is not (NotImplementedException or OperationCanceledException))
        { throw new InvalidDataException("Replacement deleted before verified session closure or did not delete the selected folder once",error); }
        var wrong = new List<string>();
        if (!wire.StageAttempted || wire.Deletes != 1 || !wire.ClosedBeforeDelete) wrong.Add("Replacement deleted before verified session closure or did not delete the selected folder once");
        if (!wire.FinishId) wrong.Add("finish named a different session");
        if (!wire.FinishOffset) wrong.Add("finish used a different byte offset");
        if (!covered || !applied || wire.Finishes != 1 || !wire.CreateOnlyAfterDelete) wrong.Add("Correct replacement was not published create-only after deletion");
        Check.Require(wrong.Count == 0, string.Join("; ",wrong));
    }

    private sealed class ReplacementWire(string path, Entry file, Entry folder, bool reject) : HttpMessageHandler
    {
        internal bool StageAttempted, CreateOnlyAfterDelete;
        internal bool ClosedBeforeDelete=true;
        internal int Deletes, Finishes;
        private bool closed;
        internal bool FinishId, FinishOffset;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check.Require(request.Headers.Authorization?.Parameter == "synthetic" && request.Headers.TryGetValues("Dropbox-API-Path-Root", out var roots) &&
                roots.Single().Contains("test-namespace", StringComparison.Ordinal), "Unbound replacement transport");
            var name = request.RequestUri!.AbsolutePath;
            var folderJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string,object>{[".tag"]="folder",["id"]=folder.Id,["name"]=folder.Path,["path_display"]=path,["path_lower"]=path.ToLowerInvariant()});
            var status = System.Net.HttpStatusCode.OK;
            string json;
            if (name.EndsWith("/upload_session/start", StringComparison.Ordinal))
            {
                StageAttempted = true;
                using var arg = System.Text.Json.JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
                closed = !reject && arg.RootElement.GetProperty("close").GetBoolean() &&
                    (await request.Content!.ReadAsByteArrayAsync(ct)).SequenceEqual(new byte[]{42});
                if (reject) status = System.Net.HttpStatusCode.BadRequest;
                json = reject ? "{}" : "{\"session_id\":\"closed\"}";
            }
            else if (name.EndsWith("/delete_v2", StringComparison.Ordinal))
            {
                Deletes++;
                using var arg = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                ClosedBeforeDelete &= closed && arg.RootElement.GetProperty("path").GetString() == folder.Id;
                json = "{\"metadata\":" + folderJson + "}";
            }
            else if (name.EndsWith("/get_metadata", StringComparison.Ordinal))
            {
                json = folderJson;
                if (Deletes != 0) { status = System.Net.HttpStatusCode.Conflict; json = "{\"error_summary\":\"path/not_found/\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}"; }
            }
            else if (name.EndsWith("/get_latest_cursor",StringComparison.Ordinal)) json=Deletes==0 ? """{"cursor":"before"}""" : """{"cursor":"after"}""";
            else if (name.EndsWith("/list_folder/continue",StringComparison.Ordinal))
            {
                using var arg=System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check.Require(Deletes==1 && arg.RootElement.GetProperty("cursor").GetString()=="before","Removal coverage did not read through the selected delete");
                var deleted=new Dictionary<string,string>{[".tag"]="deleted",["path_display"]=path,["path_lower"]=path.ToLowerInvariant(),["name"]=folder.Path};
                json=System.Text.Json.JsonSerializer.Serialize(new {entries=new[]{deleted},cursor="after",has_more=false});
            }
            else if (name.EndsWith("/list_folder", StringComparison.Ordinal)) json = "{\"entries\":[],\"cursor\":\"cursor\",\"has_more\":false}";
            else if (name.EndsWith("/upload_session/finish", StringComparison.Ordinal))
            {
                Finishes++;
                using var arg = System.Text.Json.JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
                var cursor = arg.RootElement.GetProperty("cursor"); var commit = arg.RootElement.GetProperty("commit");
                FinishId = cursor.GetProperty("session_id").GetString() == "closed";
                FinishOffset = cursor.GetProperty("offset").GetInt64() == file.Size;
                CreateOnlyAfterDelete = Deletes == 1 && commit.GetProperty("path").GetString() == path && commit.GetProperty("mode").GetString() == "add" &&
                    commit.GetProperty("strict_conflict").GetBoolean() && !commit.GetProperty("autorename").GetBoolean();
                json = System.Text.Json.JsonSerializer.Serialize(new { name=file.Path, path_lower=path.ToLowerInvariant(), path_display=path, id="id:result", rev="rev", size=file.Size, server_modified=file.Modified, client_modified=file.Modified, content_hash=file.ContentHash });
            }
            else throw new InvalidDataException(name);
            return new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }
    private sealed class ShrinkOnRead(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            SetLength(0);
            return base.ReadAsync(buffer, ct);
        }
    }
    internal sealed class Sequential(byte[] bytes) : MemoryStream(bytes)
    {
        internal int LargestRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            return base.ReadAsync(buffer, ct);
        }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }
    /// <summary>Read and seek only within the selected upload window.</summary>
    public static Task WindowAsync()
    {
        using var bytes=new MemoryStream(new byte[]{0,1,2,3});using var window=new global::Massdex.SyncShark.Engine.Dropbox.UploadSlice(bytes,1,2);
        Check.Require(window.ReadByte()==1 && window.ReadByte()==2 && window.ReadByte()==-1 && window.Seek(0,SeekOrigin.Begin)==0 && window.ReadByte()==1,"Upload window escaped its source range");
        return Task.CompletedTask;
    }
}
