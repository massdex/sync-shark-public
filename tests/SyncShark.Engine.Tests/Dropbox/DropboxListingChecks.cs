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
        replies.Dequeue();
        if (step.Body is null && step.Content is null) throw new HttpRequestException();
        var response = new HttpResponseMessage(step.Status) { Content = step.Content ?? new StringContent(step.Body!, Encoding.UTF8, "application/json") };
        if (step.Wait is { } seconds) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
        return response;
    }
}
