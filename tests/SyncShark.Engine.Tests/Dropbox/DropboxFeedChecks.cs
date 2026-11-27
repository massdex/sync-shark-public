using System.Net;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxFeed.</summary>
public static class DropboxFeedChecks
{
    /// <summary>Verifies the DropboxFeed contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var live = new Entry("dir/file", false, 3, DateTimeOffset.UnixEpoch, "id:1", "rev-1", "", null);
        var changes = DropboxFeed.ToChanges([new Listed("dir/file", live), new Listed("dir/gone", null)]);
        Check.Require(changes.Count == 2, "Every decoded entry must become one change.");
        Check.Require(changes[0].Kind == ChangeKind.Changed && changes[0].Id == "id:1" &&
            ReferenceEquals(changes[0].Observed, live), "A live entry must keep its id and observed entry.");
        Check.Require(changes[1].Kind == ChangeKind.Deleted && changes[1].Id is null && changes[1].Observed is null,
            "A tombstone must be a deletion with no identity or observed entry.");

        Check.Require(changes[0] is { Side: Side.Dropbox, Path: "dir/file" } && changes[1] is { Side: Side.Dropbox, Path: "dir/gone" },
            "Change projection lost the Dropbox side or exact ordered path.");

        var cycle = new ContinuationCycle();
        Check.Require(!cycle.Repeats("a", "b") && cycle.Repeats("b", "a"),
            "A continuation returned to the starting boundary.");
        cycle = new ContinuationCycle();
        Check.Require(new ContinuationCycle().Repeats("same", "same"), "A repeated cursor was not detected.");
        Check.Require(!cycle.Repeats("a", "b") && !cycle.Repeats("b", "c"),
            "A fresh continuation step was reported as a repeat.");
        Check.Require(cycle.Repeats("x", "c"), "A retained checkpoint repeat was not detected.");

        cycle = new ContinuationCycle();
        Check.Require(!cycle.Repeats("a", "b") && cycle.Repeats("b", "a", false), "Terminal page returned to an earlier boundary.");
        Check.Require(!cycle.Repeats("b", "b", false), "Quiet terminal page was rejected.");

        cycle = new ContinuationCycle();
        Check.Require(!cycle.Repeats(string.Empty, "a") && !cycle.Repeats("a", "b") && cycle.Repeats("b", "a", false),
            "Fresh inventory accepted a terminal return to its first real cursor.");

        var roots = new RootIdentity(@"T:\Pair A", "/Pair A", "connection-a", new DirectoryIdentity(1, 2, null),
            new DropboxRootIdentity("account", "ns-root", "ns-home", "folder"));
        var pair = new PairConfig("pair-a", "Pair A", @"T:\Pair A", "/Pair A", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null, roots);
        using var wire = new DropboxStubHandler();
        var clock = new TestDoubles.FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var stopAtWait = new CancellationTokenSource();
        string? waitDetail = null;
        var api = wire.Client(clock, evt =>
        {
            if (evt.Kind != RunEventKind.Wait) return;
            waitDetail = evt.Detail;
            stopAtWait.Cancel();
        });
        var feed = new DropboxFeed();
        var ct = CancellationToken.None;
        const string read = "files/list_folder/continue", poll = "files/list_folder/longpoll";
        const string gone = """{".tag":"deleted","path_display":"/Pair A/Gone"}""";
        var listing = new DropboxListing();
        foreach (var boundary in new[] { "provider-boundary", "next-provider-boundary" })
        {
            wire.Add("files/list_folder/get_latest_cursor", "/Pair A", System.Text.Json.JsonSerializer.Serialize(new { cursor = boundary }));
            var captured = await listing.CaptureCursorAsync(api, pair, new ListOptions(true, false, true), ct);
            Check.Require(captured == boundary, "Cursor capture did not return the provider boundary.");
        }
        wire.Add(read, "a", DropboxStubHandler.Page("b", false));
        var empty = await feed.ReadAsync(api, pair, "a", ct);
        Check.Require(empty.Cursor == "b" && empty.CaughtUp && empty.Changes.Count == 0, "Lost empty advanced boundary.");
        wire.Add(read, "b", DropboxStubHandler.Page("b", false));
        wire.Add(poll, "b", """{"changes":"invalid"}""");
        try { await feed.ReadAsync(api, pair, "b", ct); Check.Require(false, "Malformed longpoll produced a page."); }
        catch (IOException e) when (e.InnerException is System.Text.Json.JsonException) { }
        wire.Add(read, "b", DropboxStubHandler.Page("b", false));
        wire.Add(poll, "b", null);
        try { await feed.ReadAsync(api, pair, "b", ct); Check.Require(false, "Disconnected wait returned a page."); }
        catch (IOException e) when (e.InnerException is HttpRequestException { StatusCode: null }) { }
        wire.Add(read, "b", DropboxStubHandler.Page("b", false));
        wire.Add(poll, "b", """{"changes":true,"backoff":5}""");
        wire.Add(read, "b", DropboxStubHandler.Page("c", true, gone));
        const string prematureWait = "Backoff delayed the signalled read.";
        ChangePage signal;
        try { signal = await feed.ReadAsync(api, pair, "b", stopAtWait.Token); }
        catch (OperationCanceledException error) when (waitDetail is not null && stopAtWait.IsCancellationRequested &&
            error.CancellationToken == stopAtWait.Token)
        {
            throw new InvalidDataException(prematureWait, error);
        }
        Check.Require(waitDetail is null, prematureWait);
        Check.Require(signal.Cursor == "c" && signal.Changes.Single() is
            { Side: Side.Dropbox, Kind: ChangeKind.Deleted, Path: "Gone", Id: null, Observed: null },
            "The signalled read lost its deletion.");
        wire.Add(read, "c", DropboxStubHandler.Page("c", false));
        clock.Advance(TimeSpan.FromSeconds(2));
        try { await feed.ReadAsync(api, pair, "c", stopAtWait.Token); Check.Require(false, "Skipped the next poll floor."); }
        catch (OperationCanceledException) when (stopAtWait.IsCancellationRequested) { }
        Check.Require(waitDetail == Text.Get(TextKey.WaitTooManyRequests, (TextArg.Seconds, "3")),
            "Did not wait the remaining provider interval on the next poll.");
        var finite = new DropboxListing();
        wire.Add(read, "a", DropboxStubHandler.Page("b", true, gone));
        Check.Require(!(await finite.ReadChangePageAsync(api, pair, "a", ct)).CaughtUp, "Advancing page ended catch-up.");
        wire.Add(read, "b", DropboxStubHandler.Page("a", true, gone));
        try { await finite.ReadChangePageAsync(api, pair, "b", ct); Check.Require(false, "Published a cyclic page."); }
        catch (IOException) { }
        wire.Add(read, "b", DropboxStubHandler.Page("a", false, gone));
        try { await finite.ReadChangePageAsync(api, pair, "b", ct); Check.Require(false, "Returned cyclic terminal page."); }
        catch (IOException) { }
        wire.Add(read, "b", DropboxStubHandler.Page("b", false, gone));
        try { await finite.ReadChangePageAsync(api, pair, "b", ct); Check.Require(false, "Replayed nonempty same-cursor terminal page."); }
        catch (IOException) { }
        wire.Add(read, "b", DropboxStubHandler.Page("b", false));
        Check.Require((await finite.ReadChangePageAsync(api, pair, "b", ct)).CaughtUp, "Finite terminal page tried to wait.");
        wire.Add(read, "b", DropboxStubHandler.Page(" ", false, gone));
        try { await finite.ReadChangePageAsync(api, pair, "b", ct); Check.Require(false, "Published a blank boundary."); }
        catch (IOException) { }
        wire.Add(read, "b", """{"entries":[],"cursor":"next"}""");
        try { await finite.ReadChangePageAsync(api, pair, "b", ct); Check.Require(false, "Malformed page claimed completion."); }
        catch (IOException) { }
        wire.Add(read, "b", """{"error":{".tag":"reset"}}""", HttpStatusCode.Conflict);
        try { await finite.ReadChangePageAsync(api, pair, "b", ct); Check.Require(false, "Ignored cursor reset."); }
        catch (ListingCursorResetException) { }
        wire.Add(read, "b", "", HttpStatusCode.TooManyRequests, 7);
        try { await finite.ReadChangePageAsync(api, pair, "b", ct); Check.Require(false, "Throttle produced a change page."); }
        catch (ItemFailedException e) when (e.Disposition == FailureDisposition.TemporaryNoEffect &&
            e.ProviderWait == TimeSpan.FromSeconds(7) && e.ProviderWaitStarted.HasValue) { }
        clock.Advance(TimeSpan.FromSeconds(7)); // the route's pause ends before the retry
        wire.Add(read, "b", DropboxStubHandler.Page("d", false));
        Check.Require((await finite.ReadChangePageAsync(api, pair, "b", ct)).Cursor == "d", "Retry lost the held cursor.");
        wire.Done();
    }
}
