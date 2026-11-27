using System.Net;
using System.Text;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxListing.</summary>
public static class DropboxListingChecks
{
    /// <summary>Verifies the DropboxListing contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        Check.Require(DropboxListing.Respelled(FileEntry("/Parent/Old.txt"), "Folder/New.txt").Path == "Folder/Old.txt",
            "Respelled took a stale parent instead of the requested parent.");
        Check.Require(DropboxListing.Respelled(FileEntry("/x/y/C.txt"), "A/B/C.txt").Path == "A/B/C.txt",
            "Respelled did not rebuild the full requested path.");

        // Exact-name: the decoded key must equal the requested key; the actual leaf spelling is kept.
        var same = new Listed("A/Note.txt", FileEntry("/x/A/Note.txt"));
        Check.Require(DropboxListing.ExactOutcome(same, "A/Note.txt").Path == "A/Note.txt",
            "ExactOutcome rejected a matching key.");
        var different =
            new Listed("B/Note.txt", FileEntry("/x/B/Note.txt"));
        try
        {
            _ = DropboxListing.ExactOutcome(different, "A/Note.txt");
            Check.Require(false, "ExactOutcome accepted a different in-pair key.");
        }
        catch (ItemFailedException) { }
        try
        {
            _ = DropboxListing.ExactOutcome(new Listed("A/Note.txt", null), "A/Note.txt");
            Check.Require(false, "ExactOutcome accepted a tombstone reply.");
        }
        catch (ItemFailedException) { }

        // By-ID: preserve the actual location, including outside the pair; refuse a mismatched identity.
        var roots = new RootIdentity("T:\\Pair A", "/Pair A", "connection-a",
            new DirectoryIdentity(1, 2, null), new DropboxRootIdentity("account", "ns-root", "ns-home", "folder"));
        var pair = new PairConfig("pair-a", "Pair A", "T:\\Pair A", "/Pair A", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null, roots);
        var at = DropboxListing.VerifiedById(
            new Entry("Pair A/Child/Note.txt", false, 1, DateTimeOffset.UnixEpoch, "id:inside", "rev-1", "", null),
            "id:inside", pair);
        Check.Require(at.Domain == "ns-root" && at.NativePath == "/Pair A/Child/Note.txt" &&
            at.Entry.Path == "Child/Note.txt", "VerifiedById mapped an in-pair identity wrongly.");
        var far = DropboxListing.VerifiedById(
            new Entry("Other/x.txt", false, 1, DateTimeOffset.UnixEpoch, "id:outside", "rev-2", "", null),
            "id:outside", pair);
        Check.Require(far.NativePath == "/Other/x.txt" && far.Entry.Path == "/Other/x.txt",
            "VerifiedById discarded a location outside the pair.");
        try
        {
            _ = DropboxListing.VerifiedById(
                new Entry("Pair A/Child/Note.txt", false, 1, DateTimeOffset.UnixEpoch, "id:inside", "rev-1", "", null),
                "different", pair);
            Check.Require(false, "VerifiedById accepted a mismatched identity.");
        }
        catch (ItemFailedException) { }

        using var wire = new DropboxStubHandler();
        var api = wire.Client();
        var listing = new DropboxListing();
        var ct = CancellationToken.None;
        await CheckInterruptedContinuationAsync(listing, api, pair, wire);
        await CheckInterruptedCaptureAsync(listing, api, pair, wire);
        await CheckInventoryRetryAsync(listing, pair);
        await CheckCaptureRetryAsync(listing, pair);
        await CheckPageWaitAsync(listing, pair);
        await CheckUnreadableIsolationAsync(listing, pair);
        const string path = "/Pair A/A/Note.txt", id = "id:inside", metadata = "files/get_metadata";
        var folder = DropboxStubHandler.Folder(path, id);
        wire.Add(metadata, path, folder);
        Check.Require((await listing.ReadOneAsync(api, pair, "A/Note.txt", ct))?.Id == id, "Exact read lost its identity.");
        wire.Add(metadata, id, DropboxStubHandler.Folder("/Other/Folder", id));
        Check.Require((await listing.ReadByIdAsync(api, pair, id, ct))?.NativePath == "/Other/Folder", "Lost outside location.");
        foreach (var reply in new[] { DropboxStubHandler.Folder("/Pair A/B", id), DropboxStubHandler.Folder("/Other", id),
            """{"path_display":"/Pair A/A/Note.txt"}""", """{".tag":"file","path_display":"/Pair A/A/Note.txt","size":false}""" })
        {
            wire.Add(metadata, path, reply);
            try { await listing.ReadOneAsync(api, pair, "A/Note.txt", ct); Check.Require(false, "Invalid exact reply accepted."); }
            catch (ItemFailedException e) when (e.Path == "A/Note.txt") { }
        }
        foreach (var reply in new[] { DropboxStubHandler.Folder(path, "id:different"), "{}" })
        {
            wire.Add(metadata, id, reply);
            try { await listing.ReadByIdAsync(api, pair, id, ct); Check.Require(false, "Invalid by-id reply accepted."); }
            catch (ItemFailedException e) when (e.Path == id) { }
        }
        wire.Add(metadata, path, DropboxStubHandler.Missing, HttpStatusCode.Conflict);
        Check.Require(await listing.ReadOneAsync(api, pair, "A/Note.txt", ct) is null, "Missing item was not absent.");
        wire.Add(metadata, path, "", HttpStatusCode.TooManyRequests, 7);
        try { await listing.ReadOneAsync(api, pair, "A/Note.txt", ct); Check.Require(false, "Throttle mistaken for absence."); }
        catch (ItemFailedException e) when (e.Disposition == FailureDisposition.TemporaryNoEffect &&
            e.ProviderWait == TimeSpan.FromSeconds(7) && e.ProviderWaitStarted.HasValue) { }
        wire.Add(metadata, path, """{"error":{"retry_after":9}}""", HttpStatusCode.TooManyRequests);
        wire.Add(metadata, path, null, HttpStatusCode.TooManyRequests, 9, new InterruptedContent());
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { await listing.ReadOneAsync(api, pair, "A/Note.txt", ct); Check.Require(false, "Throttle produced metadata."); }
            catch (ItemFailedException e) when (e.Path == "A/Note.txt" && e.Disposition == FailureDisposition.TemporaryNoEffect &&
                e.ProviderWait == TimeSpan.FromSeconds(9) && e.ProviderWaitStarted.HasValue) { }
        }
        string? acquired = null;
        var options = new ListOptions(true, false) { CursorAcquired = value => acquired = value };
        wire.Add("files/list_folder", "id:folder", DropboxStubHandler.Page("next", false, folder));
        var count = 0;
        await foreach (var row in listing.ReadFolderAsync(api, pair, "id:folder", options, ct))
        {
            Check.Require(row.Entry?.Id == id, "Folder read returned a different child.");
            count++;
        }
        Check.Require(count == 1 && acquired == "next", "Folder listing lacked its terminal boundary.");
        wire.Add("files/list_folder", "id:folder", DropboxStubHandler.Page(" ", false, folder));
        try
        {
            await foreach (var row in listing.ReadFolderAsync(api, pair, "id:folder", options, ct))
                Check.Require(false, "Published a row from an unusable page.");
            Check.Require(false, "Completed inventory without a boundary.");
        }
        catch (InventoryIncompleteException) { }
        Check.Require(acquired == "next", "Overwrote the held boundary.");
        wire.Add("files/list_folder", "id:folder", DropboxStubHandler.Page("a", true));
        wire.Add("files/list_folder/continue", "a", DropboxStubHandler.Page("b", true));
        wire.Add("files/list_folder/continue", "b", DropboxStubHandler.Page("a", false, folder));
        try
        {
            await foreach (var row in listing.ReadFolderAsync(api, pair, "id:folder", options, ct))
                Check.Require(false, "Fresh inventory exposed a row from the terminal cycle.");
            Check.Require(false, "Fresh inventory accepted the repeated terminal boundary.");
        }
        catch (InventoryIncompleteException) { }
        Check.Require(acquired == "next", "Fresh inventory replaced the held boundary.");
        wire.Add("files/list_folder/continue", "a", DropboxStubHandler.Page("b", true));
        wire.Add("files/list_folder/continue", "b", DropboxStubHandler.Page("a", false, folder));
        try
        {
            await foreach (var row in listing.ReadFolderAsync(api, pair, "id:folder", options with { Cursor = "a" }, ct))
                Check.Require(false, "Published a row from a cyclic terminal inventory page.");
            Check.Require(false, "Accepted a cyclic terminal inventory boundary.");
        }
        catch (InventoryIncompleteException) { }
        Check.Require(acquired == "next", "A cyclic terminal page changed the acquired boundary.");
        wire.Add("files/list_folder/continue", "a", DropboxStubHandler.Page("a", false, folder));
        try
        {
            await foreach (var row in listing.ReadFolderAsync(api, pair, "id:folder", options with { Cursor = "a" }, ct))
                Check.Require(false, "Inventory exposed a repeated row at a quiet boundary.");
            Check.Require(false, "Accepted a nonempty same-cursor inventory page.");
        }
        catch (InventoryIncompleteException) { }
        wire.Add("files/list_folder/continue", "a", DropboxStubHandler.Page("a", false));
        await foreach (var row in listing.ReadFolderAsync(api, pair, "id:folder", options with { Cursor = "a" }, ct))
            Check.Require(false, "A quiet terminal inventory reply had rows.");
        Check.Require(acquired == "a", "Quiet terminal inventory did not acquire its boundary.");
        wire.Done();
    }

    // Cursor capture stays single-attempt: a proof or settlement caller (which may hold the host command gate) gets the refusal back at once.
    private static async Task CheckCaptureRetryAsync(DropboxListing listing, PairConfig pair)
    {
        const string route = "files/list_folder/get_latest_cursor";
        using var wire = new DropboxStubHandler();
        var api = wire.Client(new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch), _ => { });
        wire.Add(route, pair.DropboxRoot, "{}", HttpStatusCode.TooManyRequests, 300);
        var capture = listing.CaptureCursorAsync(api, pair, new ListOptions(true, false, true), CancellationToken.None);
        try { await capture.WaitAsync(TimeSpan.FromSeconds(10)); Check.Require(false, "A refused cursor capture returned a cursor."); }
        catch (TimeoutException e) { throw new InvalidDataException("Cursor capture waited after one refusal instead of returning it", e); }
        catch (ItemFailedException error) when (error.Disposition == FailureDisposition.TemporaryNoEffect && error.ProviderWait == TimeSpan.FromSeconds(300)) { }
        wire.Done();
    }

    private static async Task CheckInventoryRetryAsync(DropboxListing listing, PairConfig pair)
    {
        const string route = "files/list_folder", folderId = "id:folder", childId = "id:child";
        const int retryAfter = 7;
        using var wire = new DropboxStubHandler();
        var clock = new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var waits = 0;
        var api = wire.Client(clock, evt => { if (evt.Kind == RunEventKind.Wait) waits++; });
        string? acquired = null;
        var options = new ListOptions(true, false) { CursorAcquired = value => acquired = value };
        wire.Add(route, folderId, "{}", HttpStatusCode.TooManyRequests, retryAfter);
        wire.Add(route, folderId, DropboxStubHandler.Page("end", false, DropboxStubHandler.Folder("/Pair A/Child", childId)));
        await using var rows = listing.ReadFolderAsync(api, pair, folderId, options, default).GetAsyncEnumerator();
        var first = rows.MoveNextAsync();
        Check.Require(!first.IsCompleted && waits == 1 && clock.ScheduledDelay == TimeSpan.FromSeconds(retryAfter),
            "Inventory did not wait for the provider's Retry-After before retrying.");
        clock.ElapseScheduledDelay();
        Check.Require(await first && rows.Current.Entry?.Id == childId && acquired is null,
            "The retried page lost its row or published its cursor early.");
        Check.Require(!await rows.MoveNextAsync() && acquired == "end" && waits == 1,
            "The retried page did not complete after exactly one wait.");

        // A transport failure on a later page retries that page with its cursor after the backoff, keeping earlier rows.
        acquired = null;
        wire.Add(route, folderId, DropboxStubHandler.Page("mid", true, DropboxStubHandler.Folder("/Pair A/Child", childId)));
        wire.Add("files/list_folder/continue", "mid", null);
        wire.Add("files/list_folder/continue", "mid", DropboxStubHandler.Page("last", false));
        await using var again = listing.ReadFolderAsync(api, pair, folderId, options, default).GetAsyncEnumerator();
        Check.Require(await again.MoveNextAsync() && again.Current.Entry?.Id == childId, "The first page lost its row.");
        var retried = again.MoveNextAsync();
        Check.Require(!retried.IsCompleted && waits == 2 && acquired is null, "A transport failure did not wait to retry its page.");
        clock.ElapseScheduledDelay();
        Check.Require(!await retried && acquired == "last", "The failed page was not retried with its own cursor.");
        wire.Done();
    }

    // The awaiting Repair's page-wait callback: true at the first same-page retry, false once that page is accepted or the listing ends.
    private static async Task CheckPageWaitAsync(DropboxListing listing, PairConfig pair)
    {
        const string route = "files/list_folder", folderId = "id:folder", childId = "id:child";
        var child = DropboxStubHandler.Folder("/Pair A/Child", childId);
        var scope = new PathScope(pair, [pair], DriveType.Fixed);
        foreach (var full in new[] { true, false })
        {
            // Two transient classes (transport, then a provider wait), then the page: one enter, one leave at acceptance.
            using var wire = new DropboxStubHandler();
            var clock = new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch);
            var api = wire.Client(clock);
            var calls = new List<bool>();
            var options = new ListOptions(true, true) { PageWait = waiting => { calls.Add(waiting); return Task.CompletedTask; } };
            var key = full ? pair.DropboxRoot : folderId;
            wire.Add(route, key, null);
            wire.Add(route, key, "{}", HttpStatusCode.TooManyRequests, 7);
            wire.Add(route, key, DropboxStubHandler.Page("end", false, child));
            using var stop = new CancellationTokenSource();
            await using var rows = (full ? listing.ReadAsync(api, pair, () => scope, string.Empty, options, stop.Token)
                : listing.ReadFolderAsync(api, pair, folderId, options, stop.Token)).GetAsyncEnumerator();
            var move = rows.MoveNextAsync().AsTask();
            try
            {
                Check.Require(!move.IsCompleted && calls.SequenceEqual(new[] { true }), "A same-page retry did not raise the page wait first.");
                clock.ElapseScheduledDelay();
                await clock.Registered.WaitAsync(PageBound); // the provider wait of the second refusal
                Check.Require(!move.IsCompleted && calls.Count == 1, "A second retry of the same page raised the wait again.");
                clock.ElapseScheduledDelay();
                Check.Require(await Bounded(move, "The accepted page") && rows.Current.Entry?.Id == childId && calls.SequenceEqual(new[] { true, false }),
                    "The accepted page did not end the wait before its rows.");
                move = rows.MoveNextAsync().AsTask();
                Check.Require(!await Bounded(move, "The listing end") && calls.Count == 2, "The finished listing changed the wait again.");
            }
            finally { await JoinAsync(stop, move); }
            wire.Done();
        }
        // Each other end of a waiting read leaves the wait: cancellation, a permanent refusal, an unusable page.
        foreach (var end in new[] { "cancel", "refusal", "cursor" })
        {
            using var wire = new DropboxStubHandler();
            var clock = new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch);
            var api = wire.Client(clock);
            var calls = new List<bool>();
            var options = new ListOptions(true, false) { PageWait = waiting => { calls.Add(waiting); return Task.CompletedTask; } };
            using var stop = new CancellationTokenSource();
            wire.Add(route, folderId, null);
            if (end == "refusal") wire.Add(route, folderId, """{"error":{".tag":"other"}}""", HttpStatusCode.Conflict);
            if (end == "cursor") wire.Add(route, folderId, DropboxStubHandler.Page(" ", false, child));
            var failed = false;
            await using (var rows = listing.ReadFolderAsync(api, pair, folderId, options, stop.Token).GetAsyncEnumerator())
            {
                var next = rows.MoveNextAsync().AsTask();
                try
                {
                    Check.Require(!next.IsCompleted && calls.SequenceEqual(new[] { true }), "A same-page retry did not raise the page wait.");
                    if (end == "cancel") stop.Cancel(); else clock.ElapseScheduledDelay();
                    try { await Bounded(next, "A waiting read that ended (" + end + ")"); }
                    catch (OperationCanceledException) when (end == "cancel") { failed = true; }
                    catch (Exception error) when (end != "cancel" && error is ItemFailedException or InventoryIncompleteException) { failed = true; }
                }
                finally { await JoinAsync(stop, next); }
            }
            Check.Require(failed && calls.SequenceEqual(new[] { true, false }), "A waiting read that ended (" + end + ") left its page wait open.");
            wire.Done();
        }
    }

    private static readonly TimeSpan PageBound = TimeSpan.FromSeconds(10);
    private static async Task<bool> Bounded(Task<bool> move, string what)
    {
        Check.Require(await Task.WhenAny(move, Task.Delay(PageBound)) == move, what + " did not finish within its bound.");
        return await move;
    }
    // A failed assertion can leave a page move outstanding: cancel and join it (bounded) so disposal does not mask that failure.
    private static async Task JoinAsync(CancellationTokenSource stop, Task move)
    {
        stop.Cancel();
        try { await move.WaitAsync(PageBound); } catch (Exception) { } // its outcome was already asserted, or the assertion that failed is reported
    }

    // Only an isolating listing yields a decodable-path, file-tag, id-bearing entry whose other fields fail as SkipUnreadable.
    private static async Task CheckUnreadableIsolationAsync(DropboxListing listing, PairConfig pair)
    {
        const string route = "files/list_folder";
        var scope = new PathScope(pair, [pair], DriveType.Fixed);
        var good = DropboxStubHandler.Folder("/Pair A/Good", "id:good");
        static string NoRev(string path, string? id = "id:bad") => System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object?> { [".tag"] = "file", ["path_display"] = path, ["id"] = id, ["size"] = 1,
                ["client_modified"] = "2020-01-01T00:00:00Z" }.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value));
        async Task<List<Listed>> ListAsync(bool isolate, string page)
        {
            using var wire = new DropboxStubHandler();
            wire.Add(route, pair.DropboxRoot, page);
            var rows = new List<Listed>();
            await foreach (var row in listing.ReadAsync(wire.Client(), pair, () => scope, string.Empty,
                new ListOptions(true, true) { IsolateUnreadableFiles = isolate }, default)) rows.Add(row);
            wire.Done();
            return rows;
        }
        async Task FailsAsync(bool isolate, string page, string reason)
        {
            try { await ListAsync(isolate, page); }
            catch (InventoryIncompleteException) { return; }
            throw new InvalidDataException(reason);
        }
        var mixed = DropboxStubHandler.Page("end", false, good, NoRev("/Pair A/Bad.txt"));
        var isolated = await ListAsync(true, mixed);
        Check.Require(isolated.Count == 2 && isolated[0].Entry?.Id == "id:good" &&
            isolated[1] is { Path: "Bad.txt", Entry: { IsFolder: false, Id: "id:bad", Skip: nameof(TextKey.SkipUnreadable) } },
            "An isolating listing did not keep good rows and mark the undecodable file.");
        await FailsAsync(false, mixed, "A strict listing accepted an undecodable file.");
        foreach (var entry in new[]
        {
            """{".tag":"folder","path_display":"/Pair A/F"}""",
            """{".tag":"unknown","path_display":"/Pair A/U","id":"id:u"}""",
            """{".tag":"file","id":"id:bad","size":1}""",
            NoRev("/Pair A/Bad.txt", null), NoRev("/Pair A/Bad.txt", ""), NoRev("/Pair A/Bad.txt", " "), NoRev("/Pair A/Bad.txt", "abc"),
            NoRev("/Other/Bad.txt"), NoRev("/Pair A/CON.txt"), NoRev("/Pair A/a:b/Bad.txt")
        })
            await FailsAsync(true, DropboxStubHandler.Page("end", false, good, entry), "An isolating listing kept an entry it cannot name: " + entry);
        var wellFormed = """{".tag":"file","path_display":"/Pair A/a:b/ok.txt","id":"id:ok","rev":"r","size":1,"client_modified":"2020-01-01T00:00:00Z"}""";
        Check.Require((await ListAsync(true, DropboxStubHandler.Page("end", false, wellFormed))).Count == 0,
            "A well-formed child of an invalid-name folder was not pruned.");
        await FailsAsync(true, """{"entries":{},"cursor":"end","has_more":false}""", "An isolating listing accepted entries that are not an array.");
        await FailsAsync(true, DropboxStubHandler.Page(" ", false, good), "An isolating listing accepted a bad cursor.");
        // Strict consumers of the same page stay strict, whatever the options ask.
        using var strict = new DropboxStubHandler();
        var api = strict.Client();
        strict.Add(route, "id:folder", mixed);
        try
        {
            await foreach (var row in listing.ReadFolderAsync(api, pair, "id:folder", new ListOptions(true, true) { IsolateUnreadableFiles = true }, default)) { }
            throw new InvalidDataException("A folder read isolated an undecodable file.");
        }
        catch (InventoryIncompleteException) { }
        strict.Add("files/list_folder/continue", "held", mixed);
        // The unread-page projection of the decoder's own failure: the missing rev, wrapped once by the reply decoder.
        try { await new DropboxListing().ReadChangePageAsync(api, pair, "held", default); throw new InvalidDataException("A change page isolated an undecodable file."); }
        catch (IOException error) when (error.GetType() == typeof(IOException) &&
            error.InnerException is System.Text.Json.JsonException { InnerException: KeyNotFoundException }) { }
        strict.Add("files/get_metadata", "/Pair A/Bad.txt", NoRev("/Pair A/Bad.txt"));
        try { await listing.ReadOneAsync(api, pair, "Bad.txt", default); throw new InvalidDataException("A single read isolated an undecodable file."); }
        catch (ItemFailedException) { }
        strict.Done();
    }

    private static async Task CheckInterruptedContinuationAsync(DropboxListing listing, DropboxApi api, PairConfig pair,
        DropboxStubHandler wire)
    {
        const string route = "files/list_folder/continue", cursor = "held";
        var timeout = new TimeoutException();
        wire.Add(route, cursor, null, content: new StreamContent(new InterruptedRead(_ => timeout)));
        try
        {
            await listing.ReadChangePageAsync(api, pair, cursor, CancellationToken.None);
            Check.Require(false, "Timed-out continuation returned a page.");
        }
        catch (IOException error) when (ReferenceEquals(error.InnerException, timeout)) { }
        catch (TimeoutException error) { throw new InvalidDataException("Continuation body timeout escaped the unread-page projection.", error); }

        using var caller = new CancellationTokenSource();
        wire.Add(route, cursor, null, content: new StreamContent(new InterruptedRead(ct =>
        {
            caller.Cancel();
            return new OperationCanceledException(ct);
        })));
        try { await listing.ReadChangePageAsync(api, pair, cursor, caller.Token); }
        catch (OperationCanceledException error) when (caller.IsCancellationRequested && error.CancellationToken == caller.Token) { return; }
        throw new InvalidDataException("Caller cancellation returned a continuation page.");
    }

    private static async Task CheckInterruptedCaptureAsync(DropboxListing listing, DropboxApi api, PairConfig pair,
        DropboxStubHandler wire)
    {
        const string route = "files/list_folder/get_latest_cursor";
        var options = new ListOptions(true, false, true);
        var timeout = new TimeoutException();
        wire.Add(route, pair.DropboxRoot, null, content: new StreamContent(new InterruptedRead(_ => timeout)));
        try { await listing.CaptureCursorAsync(api, pair, options, CancellationToken.None); }
        catch (ItemFailedException error) when (error.Path == pair.DropboxRoot &&
            error.Disposition == FailureDisposition.TemporaryNoEffect && ReferenceEquals(error.InnerException, timeout)) { return; }
        catch (TimeoutException error) { throw new InvalidDataException("Cursor capture body timeout escaped the transient read projection.", error); }
        throw new InvalidDataException("Timed-out capture returned a cursor.");
    }

    private sealed class InterruptedRead(Func<CancellationToken, Exception> failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure(cancellationToken));
    }

    private sealed class InterruptedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.FromException(new IOException());
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private static Entry FileEntry(string path) => new(path, false, 1, DateTimeOffset.UnixEpoch, "id", "rev", "", null);
}

internal sealed class DropboxStubHandler : HttpMessageHandler
{
    private string? unexpectedRequest;
    internal const string Missing = """{"error":{".tag":"path","path":{".tag":"not_found"}}}""";
    private readonly Queue<(string Route, string Key, string? Body, HttpStatusCode Status, int? Wait, HttpContent? Content)> replies = new();
    internal static string Folder(string path, string id) => System.Text.Json.JsonSerializer.Serialize(
        new Dictionary<string,string> { [".tag"] = "folder", ["path_display"] = path, ["id"] = id });
    internal static string Page(string cursor, bool more, params string[] entries) =>
        $$"""{"entries":[{{string.Join(",", entries)}}],"cursor":"{{cursor}}","has_more":{{(more ? "true" : "false")}}}""";
    internal void Add(string route, string key, string? body, HttpStatusCode status = HttpStatusCode.OK, int? wait = null, HttpContent? content = null) =>
        replies.Enqueue((route, key, body, status, wait, content));
    internal void Done() => Check.Require(replies.Count == 0, "A scripted request was not sent.");
    internal DropboxApi Client(TimeProvider? clock = null, Action<RunEvent>? events = null) =>
        new(new HttpClient(this), clock ?? TimeProvider.System, SettingsParser.Load(), events ?? (_ => { }),
            static _ => ValueTask.FromResult<string?>("test-token"), "ns-root");
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // Assert outside the reader: a negative fixture must not consume its own request mismatch as a product error.
        if (disposing && unexpectedRequest is not null) Check.Require(false, unexpectedRequest);
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var arg = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var root = arg.RootElement;
        var key = (root.TryGetProperty("path", out var value) || root.TryGetProperty("cursor", out value)) &&
            value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
        if (!replies.TryPeek(out var step) || request.RequestUri != Routes.Endpoint(step.Route) || key != step.Key)
        {
            unexpectedRequest ??= $"Unexpected endpoint or request identity for {step.Route} {step.Key}.";
            throw new InvalidOperationException(unexpectedRequest);
        }
        if (step.Route == "files/list_folder/get_latest_cursor" &&
            (!root.TryGetProperty("recursive", out var recursive) || recursive.ValueKind != System.Text.Json.JsonValueKind.True ||
             !root.TryGetProperty("include_deleted", out var deleted) || deleted.ValueKind != System.Text.Json.JsonValueKind.True))
        {
            unexpectedRequest ??= "Cursor capture did not request recursive changes with deletions.";
            throw new InvalidOperationException(unexpectedRequest);
        }
        if (step.Route == "files/list_revisions" &&
            (!root.TryGetProperty("mode", out var mode) || mode.ValueKind != System.Text.Json.JsonValueKind.String || mode.GetString() != "path" ||
             !root.TryGetProperty("limit", out var limit) || limit.ValueKind != System.Text.Json.JsonValueKind.Number || !limit.TryGetInt32(out var count) || count != 1))
        {
            unexpectedRequest ??= "Revision lookup did not request a single head in path mode.";
            throw new InvalidOperationException(unexpectedRequest);
        }
        replies.Dequeue();
        if (step.Body is null && step.Content is null) throw new HttpRequestException();
        var response = new HttpResponseMessage(step.Status) { Content = step.Content ?? new StringContent(step.Body!, Encoding.UTF8, "application/json") };
        if (step.Wait is { } seconds) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
        return response;
    }
}
