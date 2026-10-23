using System.Diagnostics;
using System.Net;
using Massdex.SyncShark.DropboxCli;

namespace Massdex.SyncShark.V2;

/// <summary>The Dropbox change-watch and account-access checks (continuous-sync.md sections 1 and 5).</summary>
internal static class DropboxWatchChecks
{
    private const string RootId = "rb";
    private const string Cursor = "cursor-1";
    private const string Longpoll = "files/list_folder/longpoll";
    private const string NotifyHost = "notify.dropboxapi.com";
    private const string LongpollPath = "/2/files/list_folder/longpoll";

    private static readonly TimeSpan Fires = TimeSpan.FromSeconds(5);

    // A "did not poll" window: the parked wait arms no timer at all, so a fraction of a second is ample.
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(30);

    public static async Task RunAsync()
    {
        await ChangesFalseThenTrueFiresAsync().ConfigureAwait(false);
        await SameSignalledCursorDoesNotSpinAsync().ConfigureAwait(false);
        await NoCursorMakesNoRequestAsync().ConfigureAwait(false);
        await ConflictSignalsAndDoesNotSpinAsync().ConfigureAwait(false);
        await BackoffDelaysAndSurvivesRebuildAsync().ConfigureAwait(false);
        await UnauthorizedLongpollFaultsAsync().ConfigureAwait(false);
        await TokenRefusalAsync().ConfigureAwait(false);
        await TransientRetryPacingAsync().ConfigureAwait(false);
        await CursorReplacedAfterConflictAsync().ConfigureAwait(false);
        await BackoffAfterChangesAsync().ConfigureAwait(false);
        await ConcurrentMultiRootAsync().ConfigureAwait(false);
        await DeleteAccessAsync().ConfigureAwait(false);
        await InventoryAccessAsync().ConfigureAwait(false);
        await ChangeFeedAccessAsync().ConfigureAwait(false);
        await HashAccessAsync().ConfigureAwait(false);
        await DeletionEvidenceAccessAsync().ConfigureAwait(false);
        await ObservationAccessAsync().ConfigureAwait(false);
        await PutAccessAsync().ConfigureAwait(false);
        await MoveAccessAsync().ConfigureAwait(false);
        await MkDirAccessAsync().ConfigureAwait(false);
        await OpenReadAccessAsync().ConfigureAwait(false);
        await UncertainWriteStaysUncertainAsync().ConfigureAwait(false);
        Console.WriteLine("--v2-dropboxwatch-checks passed.");
    }

    private static async Task ChangesFalseThenTrueFiresAsync()
    {
        using var rig = new WatchRig(new ManualClock());
        rig.Wire.Respond(Longpoll, HttpStatusCode.OK, "{\"changes\":false}");
        rig.Wire.Respond(Longpoll, HttpStatusCode.OK, "{\"changes\":true}");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(rig, stop.Token).ConfigureAwait(false);
        Require(await CompletesAsync(watch.ChangedAsync(stop.Token)).ConfigureAwait(false),
            "changes:false then changes:true fires");
        Require(rig.Wire.Requests.Count == 2, "each wait polls once per scripted answer");
        Require(rig.Wire.Requests.All(request => request.Host == NotifyHost), "the longpoll uses the notify host");
        Require(rig.Wire.Requests.All(request => request.Route == LongpollPath), "the longpoll route is used");
        Require(rig.Wire.Requests[0].BodyText.Contains(Cursor, StringComparison.Ordinal),
            "the wait polls the cursor the store holds");
        watch.Dispose();
    }

    private static async Task SameSignalledCursorDoesNotSpinAsync()
    {
        using var rig = new WatchRig(new ManualClock());
        rig.Wire.Respond(Longpoll, HttpStatusCode.OK, "{\"changes\":true}");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(rig, stop.Token).ConfigureAwait(false);
        Require(await CompletesAsync(watch.ChangedAsync(stop.Token)).ConfigureAwait(false), "changes:true fires");
        Require(rig.Wire.Requests.Count == 1, "the store cursor is polled once");
        var again = watch.ChangedAsync(stop.Token);
        Require(!await CompletesAsync(again, Quiet).ConfigureAwait(false),
            "the cursor that already signalled is not polled again");
        Require(rig.Wire.Requests.Count == 1, "no extra request for the signalled cursor");
        stop.Cancel();
        await FaultAsync(again).ConfigureAwait(false);
        watch.Dispose();
    }

    private static async Task NoCursorMakesNoRequestAsync()
    {
        using var rig = new WatchRig(new ManualClock(), cursor: null);
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(rig, stop.Token).ConfigureAwait(false);
        var wait = watch.ChangedAsync(stop.Token);
        Require(!await CompletesAsync(wait, Quiet).ConfigureAwait(false),
            "a root with no stored cursor never signals on its own");
        Require(rig.Wire.Requests.Count == 0, "no stored cursor makes no request");
        stop.Cancel();
        await FaultAsync(wait).ConfigureAwait(false);
        watch.Dispose();
    }

    private static async Task ConflictSignalsAndDoesNotSpinAsync()
    {
        using var rig = new WatchRig(new ManualClock());
        rig.Wire.Respond(Longpoll, HttpStatusCode.Conflict, "{\"error_summary\":\"reset/..\"}");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(rig, stop.Token).ConfigureAwait(false);
        Require(await CompletesAsync(watch.ChangedAsync(stop.Token)).ConfigureAwait(false), "a 409 fires");
        Require(rig.Wire.Requests.Count == 1, "a 409 is polled once");
        var again = watch.ChangedAsync(stop.Token);
        Require(!await CompletesAsync(again, Quiet).ConfigureAwait(false),
            "the invalid cursor is not polled repeatedly after a 409");
        Require(rig.Wire.Requests.Count == 1, "a re-wait on the invalid cursor sends no request");
        stop.Cancel();
        await FaultAsync(again).ConfigureAwait(false);
        watch.Dispose();
    }

    private static async Task BackoffDelaysAndSurvivesRebuildAsync()
    {
        var clock = new ManualClock();
        using var rig = new WatchRig(clock);
        rig.Wire.Respond(Longpoll, HttpStatusCode.OK, "{\"changes\":false,\"backoff\":5}");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(rig, stop.Token).ConfigureAwait(false);
        var wait = watch.ChangedAsync(stop.Token);
        await UntilAsync(() => rig.Wire.Requests.Count == 1, stop.Token).ConfigureAwait(false);
        Require(!await CompletesAsync(wait, Quiet).ConfigureAwait(false), "a backoff delays the next poll");
        stop.Cancel();
        await FaultAsync(wait).ConfigureAwait(false);
        watch.Dispose();

        using var rebuiltStop = new CancellationTokenSource();
        var rebuilt = await ArmedAsync(rig, rebuiltStop.Token).ConfigureAwait(false);
        rig.Wire.Respond(Longpoll, HttpStatusCode.OK, "{\"changes\":true}");
        var resumed = rebuilt.ChangedAsync(rebuiltStop.Token);
        Require(!await CompletesAsync(resumed, Quiet).ConfigureAwait(false),
            "the surviving backoff delays the rebuilt watch's first poll");
        Require(rig.Wire.Requests.Count == 1, "the rebuilt watch has not polled before the backoff passes");
        clock.Advance(TimeSpan.FromSeconds(5));
        Require(await CompletesAsync(resumed).ConfigureAwait(false), "polling resumes once the backoff has passed");
        Require(rig.Wire.Requests.Count == 2, "the rebuilt watch polls after the backoff");
        rebuilt.Dispose();
    }

    private static async Task TokenRefusalAsync()
    {
        await RequireTokenOutcomeAsync(
            new DropboxApiException(HttpStatusCode.BadRequest, "Dropbox token exchange returned HTTP 400.")
                { OAuthError = DropboxOAuthError.InvalidGrant },
            true, "an invalid_grant token refresh becomes EndpointAccessException");
        await RequireTokenOutcomeAsync(
            new DropboxApiException(HttpStatusCode.BadRequest, "Dropbox token exchange returned HTTP 400.")
                { OAuthError = DropboxOAuthError.Other },
            false, "another OAuth 400 is not an access refusal");
        await RequireTokenOutcomeAsync(new DropboxCredentialUnavailableException("acc"), true,
            "a missing credential becomes EndpointAccessException");
        await RequireRefusalAsync("{\"error\":\"invalid_grant\",\"error_description\":\"refresh token is malformed\"}",
            DropboxOAuthError.InvalidGrant, "invalid_grant");
        await RequireRefusalAsync("{\"error\":\"invalid_request\"}", DropboxOAuthError.Other, "invalid_request");
        await RequireRefusalAsync("not json", DropboxOAuthError.Other, "an unparseable body");
    }

    // The refusal goes through the real RefreshAsync; the caller's token is cancelled the moment the response has
    // arrived, so classifying the body must neither replace the established HTTP exception nor lose the typed code.
    private static async Task RequireRefusalAsync(string body, DropboxOAuthError expected, string label)
    {
        foreach (var cancelAtBoundary in new[] { false, true })
        {
            using var stop = new CancellationTokenSource();
            using var http = new HttpClient(new RefusingTokenEndpoint(body, cancelAtBoundary ? stop : null));
            var error = await FaultAsync(DropboxOAuth.RefreshAsync(http, "client", "refresh", stop.Token))
                .ConfigureAwait(false);
            var when = cancelAtBoundary ? " with cancellation at the response boundary" : string.Empty;
            Require(error is DropboxApiException { StatusCode: HttpStatusCode.BadRequest } api &&
                    api.Message == "Dropbox token exchange returned HTTP 400." && api.OAuthError == expected,
                $"a 400 {label} refusal{when} stays DropboxApiException with OAuthError {expected}");
        }
    }

    private static async Task RequireTokenOutcomeAsync(Exception thrown, bool access, string label)
    {
        using var rig = new WireRig(new TokenRefused(thrown));
        var error = await FaultAsync(rig.Inventory().FullAsync(1, _ => true, null!, CancellationToken.None))
            .ConfigureAwait(false);
        Require(access ? error is EndpointAccessException : error is not EndpointAccessException, label);
    }

    private static async Task TransientRetryPacingAsync()
    {
        var clock = new ManualClock();
        using var rig = new WatchRig(clock);
        rig.Wire.RespondFault(Longpoll, new HttpRequestException("no network"));
        rig.Wire.Respond(Longpoll, HttpStatusCode.OK, "{\"changes\":true}");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(rig, stop.Token).ConfigureAwait(false);
        var wait = watch.ChangedAsync(stop.Token);
        await UntilAsync(() => rig.Wire.Requests.Count == 1, stop.Token).ConfigureAwait(false);
        Require(!await CompletesAsync(wait, Quiet).ConfigureAwait(false),
            "a transient failure waits AutoRetryDelay before retrying");
        Require(rig.Wire.Requests.Count == 1, "no retry before the delay elapses");
        clock.Advance(V2Settings.Default.AutoRetryDelay);
        Require(await CompletesAsync(wait).ConfigureAwait(false), "the retry polls again after the delay and fires");
        Require(rig.Wire.Requests.Count == 2, "the retry is exactly one further poll");
        watch.Dispose();
    }

    private static async Task CursorReplacedAfterConflictAsync()
    {
        using var rig = new WatchRig(new ManualClock());
        rig.Wire.Respond(Longpoll, HttpStatusCode.Conflict, "{\"error_summary\":\"reset/..\"}");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(rig, stop.Token).ConfigureAwait(false);
        Require(await CompletesAsync(watch.ChangedAsync(stop.Token)).ConfigureAwait(false), "a 409 fires");
        rig.SetCursor("cursor-2"); // the run's full scan stored a fresh cursor
        rig.Wire.Respond(Longpoll, HttpStatusCode.OK, "{\"changes\":true}");
        Require(await CompletesAsync(watch.ChangedAsync(stop.Token)).ConfigureAwait(false),
            "after a 409 the next wait polls the replaced cursor");
        Require(rig.Wire.Requests.Count == 2, "the replaced cursor is polled once");
        Require(rig.Wire.Requests[1].BodyText.Contains("cursor-2", StringComparison.Ordinal),
            "the replacement cursor is the one polled");
        watch.Dispose();
    }

    private static async Task BackoffAfterChangesAsync()
    {
        var clock = new ManualClock();
        using var rig = new WatchRig(clock);
        rig.Wire.Respond(Longpoll, HttpStatusCode.OK, "{\"changes\":true,\"backoff\":5}");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(rig, stop.Token).ConfigureAwait(false);
        Require(await CompletesAsync(watch.ChangedAsync(stop.Token)).ConfigureAwait(false),
            "changes:true carrying a backoff still fires");
        rig.SetCursor("cursor-2");
        rig.Wire.Respond(Longpoll, HttpStatusCode.OK, "{\"changes\":true}");
        var next = watch.ChangedAsync(stop.Token);
        Require(!await CompletesAsync(next, Quiet).ConfigureAwait(false),
            "a backoff returned with changes:true is honoured before the next poll");
        Require(rig.Wire.Requests.Count == 1, "no poll before that backoff passes");
        clock.Advance(TimeSpan.FromSeconds(5));
        Require(await CompletesAsync(next).ConfigureAwait(false), "the next poll runs after the backoff");
        Require(rig.Wire.Requests.Count == 2, "the next poll happens once");
        watch.Dispose();
    }

    // Many roots on one source poll at once: the wire holds every longpoll until all of them are in flight, then answers
    // each root differently (changes with a per-root backoff, or a 409). Every root's suppression and backoff must
    // land on that root alone.
    private static async Task ConcurrentMultiRootAsync()
    {
        const int count = 16;
        var clock = new ManualClock();
        var wire = new ConcurrentLongpollWire(count);
        using var http = new HttpClient(wire);
        using var store = new InMemoryStateStore();
        var client = new DropboxPilotClient(http, new SecretDropboxTokenSource(), "/Pilot", Binding());
        var source = new DropboxChangeWatchSource(store, client, V2Settings.Default, () => SafetyPoll, clock);
        var roots = Enumerable.Range(0, count).Select(index => Root() with { RootId = "rb" + index }).ToArray();
        foreach (var root in roots)
            store.CompleteGeneration(store.BeginGeneration(root.RootId, "snapshot"), "cursor-" + root.RootId, true);
        using var stop = new CancellationTokenSource();
        var watches = await Task.WhenAll(roots.Select(async root =>
        {
            var watch = await source.WatchAsync(root, stop.Token).ConfigureAwait(false);
            await watch.ChangedAsync(stop.Token).ConfigureAwait(false); // the catch-up signal
            return watch;
        })).ConfigureAwait(false);
        var started = clock.GetUtcNow();
        var waits = watches.Select(watch => Task.Run(() => watch.ChangedAsync(stop.Token))).ToArray();
        Require(await CompletesAsync(Task.WhenAll(waits)).ConfigureAwait(false),
            "sixteen roots polled in parallel all signal without a fault");
        Require(wire.PeakInFlight == count, "every root's longpoll was in flight at the same time");
        Require(wire.Polled.Count == count && roots.All(root => wire.Polled.Count(c => c == "cursor-" + root.RootId) == 1),
            "each root polled its own cursor exactly once");
        for (var index = 0; index < count; index++)
        {
            var rootId = roots[index].RootId;
            Require(source.SignalledCursor(rootId) == "cursor-" + rootId, $"{rootId} suppresses its own signalled cursor");
            var expected = index % 2 == 0 ? started + TimeSpan.FromSeconds(index + 1) : DateTimeOffset.MinValue;
            Require(source.NotBefore(rootId) == expected, $"{rootId} keeps its own backoff state");
        }
        var again = watches.Select(watch => watch.ChangedAsync(stop.Token)).ToArray();
        Require(!await CompletesAsync(Task.WhenAny(again), Quiet).ConfigureAwait(false),
            "no root re-polls its signalled cursor");
        Require(wire.Polled.Count == count, "the re-waits sent no request");
        stop.Cancel();
        foreach (var wait in again) await FaultAsync(wait).ConfigureAwait(false);
        foreach (var watch in watches) watch.Dispose();
    }

    private static async Task DeleteAccessAsync()
    {
        using var rig = new WireRig();
        rig.Wire.Script("/2/files/get_metadata", HttpStatusCode.Unauthorized, 1);
        var target = new ItemState("s.txt", ItemKind.File, "id:s", "r1", "hash", 1, DateTimeOffset.UnixEpoch,
            ItemFlags.None);
        var action = new SyncAction("a-del", 3, ActionKind.Delete, Side.B, "s.txt", null, null, target, false, "k");
        var error = await FaultAsync(rig.Writer().DeleteAsync(action, CancellationToken.None)).ConfigureAwait(false);
        Require(error is EndpointAccessException, "a delete refused with 401 becomes EndpointAccessException");
    }

    private static async Task UnauthorizedLongpollFaultsAsync()
    {
        using var rig = new WatchRig(new ManualClock());
        rig.Wire.Respond(Longpoll, HttpStatusCode.Unauthorized, "{\"error_summary\":\"invalid_access_token/value\"}");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(rig, stop.Token).ConfigureAwait(false);
        var error = await FaultAsync(watch.ChangedAsync(stop.Token)).ConfigureAwait(false);
        Require(error is EndpointAccessException, "a 401 longpoll faults the wait with EndpointAccessException");
        Require(error.InnerException is not null, "the access error keeps the provider error as its inner exception");
        watch.Dispose();
    }

    private static async Task InventoryAccessAsync()
    {
        using var rig = new WireRig();
        rig.Wire.Script("/2/files/get_metadata", HttpStatusCode.Unauthorized, 1);
        var error = await FaultAsync(rig.Inventory().FullAsync(1, _ => true, null!, CancellationToken.None))
            .ConfigureAwait(false);
        Require(error is EndpointAccessException, "an inventory read refused with 401 becomes EndpointAccessException");
        Require(error.InnerException is not null, "the inventory access error keeps its inner exception");
    }

    private static async Task ChangeFeedAccessAsync()
    {
        using var rig = new WireRig();
        rig.Wire.Script("/2/files/list_folder/continue", HttpStatusCode.Unauthorized, 1);
        var error = await FaultAsync(rig.Inventory().ChangesSinceAsync(Cursor, 1, _ => true, CancellationToken.None))
            .ConfigureAwait(false);
        Require(error is EndpointAccessException, "a change-feed read refused with 401 becomes EndpointAccessException");
    }

    private static async Task HashAccessAsync()
    {
        using var rig = new WireRig();
        rig.SeedStored(new ItemState("a.txt", ItemKind.File, "id:a", "r1", null, 1, DateTimeOffset.UnixEpoch,
            ItemFlags.None));
        rig.Wire.Script("/2/files/get_metadata", HttpStatusCode.Unauthorized, 1);
        var error = await FaultAsync(rig.Inventory().HashAsync(new[] { "a.txt" }, CancellationToken.None))
            .ConfigureAwait(false);
        Require(error is EndpointAccessException, "a hash read refused with 401 becomes EndpointAccessException");
    }

    private static async Task ObservationAccessAsync()
    {
        using var rig = new WireRig();
        rig.Wire.Script("/2/files/get_metadata", HttpStatusCode.Unauthorized, 1);
        var error = await FaultAsync(rig.Writer().ObserveAsync("a.txt", CancellationToken.None)).ConfigureAwait(false);
        Require(error is EndpointAccessException, "an observation refused with 401 becomes EndpointAccessException");
    }

    private static async Task PutAccessAsync()
    {
        // A create upload transmits its bytes first, and the client calls any refusal after transmission an uncertain
        // effect; a conditional update reads the target first, so its 401 reaches the writer's access mapping.
        using var rig = new WireRig();
        rig.Wire.Script("/2/files/get_metadata", HttpStatusCode.Unauthorized, 1);
        var source = new ItemState("a.txt", ItemKind.File, "id:a", "r1", "hash", 3, DateTimeOffset.UnixEpoch,
            ItemFlags.None);
        var action = new SyncAction("a-put", 2, ActionKind.Update, Side.B, "a.txt", null, source, source, false, "k");
        var error = await FaultAsync(
            rig.Writer().PutAsync(action, new MemoryStream(new byte[] { 1, 2, 3 }), CancellationToken.None))
            .ConfigureAwait(false);
        Require(error is EndpointAccessException, "a conditional put refused with 401 becomes EndpointAccessException");
    }

    private static async Task MoveAccessAsync()
    {
        using var rig = new WireRig();
        rig.Wire.Script("/2/files/get_metadata", HttpStatusCode.Unauthorized, 1);
        var target = new ItemState("src.txt", ItemKind.File, "id:s", "r1", "hash", 1, DateTimeOffset.UnixEpoch,
            ItemFlags.None);
        var action = new SyncAction("a-move", 2, ActionKind.Move, Side.B, "dest.txt", "src.txt", null, target, false, "k");
        var error = await FaultAsync(rig.Writer().MoveAsync(action, CancellationToken.None)).ConfigureAwait(false);
        Require(error is EndpointAccessException, "a move refused with 401 becomes EndpointAccessException");
    }

    private static async Task MkDirAccessAsync()
    {
        using var rig = new WireRig();
        rig.Wire.Script("/2/files/create_folder_batch", HttpStatusCode.Unauthorized, 1);
        var error = await FaultAsync(rig.Writer().MkDirAsync("newdir", CancellationToken.None)).ConfigureAwait(false);
        Require(error is EndpointAccessException, "a folder create refused with 401 becomes EndpointAccessException");
    }

    private static async Task OpenReadAccessAsync()
    {
        using var rig = new WireRig();
        rig.Wire.Script("/2/files/get_metadata", HttpStatusCode.Unauthorized, 1);
        var source = new ItemState("a.txt", ItemKind.File, "id:a", "r1", "hash", 1, DateTimeOffset.UnixEpoch,
            ItemFlags.None);
        var error = await FaultAsync(rig.Writer().OpenReadAsync(source, CancellationToken.None)).ConfigureAwait(false);
        Require(error is EndpointAccessException, "an exact-revision read refused with 401 becomes EndpointAccessException");
    }

    private static async Task DeletionEvidenceAccessAsync()
    {
        using var rig = new WireRig();
        rig.Wire.Script("/2/files/get_metadata", HttpStatusCode.Unauthorized, 1);
        var error = await FaultAsync(rig.Inventory().DeletionTimesAsync(new[] { "gone.txt" }, CancellationToken.None))
            .ConfigureAwait(false);
        Require(error is EndpointAccessException,
            "a deletion-evidence read refused with 401 becomes EndpointAccessException, never 'no evidence'");
    }

    private static async Task UncertainWriteStaysUncertainAsync()
    {
        using var rig = new WireRig();
        rig.Wire.SeedFile("s.txt", "content");
        var writer = rig.Writer();
        var current = (await writer.ObserveAsync("s.txt", CancellationToken.None).ConfigureAwait(false))!;
        rig.Wire.Script("/2/files/delete_batch", HttpStatusCode.InternalServerError, 1);
        var error = await FaultAsync(writer.DeleteAsync(
            new SyncAction("a-del", 3, ActionKind.Delete, Side.B, "s.txt", null, null, current, false, "k"),
            CancellationToken.None)).ConfigureAwait(false);
        Require(error is UncertainEffectException, "an uncertain write stays UncertainEffectException");
    }

    private static async Task<DropboxChangeWatch> ArmedAsync(WatchRig rig, CancellationToken ct)
    {
        var watch = (DropboxChangeWatch)await rig.Source.WatchAsync(Root(), ct).ConfigureAwait(false);
        await watch.ChangedAsync(ct).ConfigureAwait(false); // the new watch's one catch-up signal
        return watch;
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed > Fires)
                throw new InvalidOperationException("dropbox watch check timed out waiting for a poll.");
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    // Awaiting the winner rethrows its fault, so a dead watch can never pass as "fired".
    private static async Task<bool> CompletesAsync(Task task, TimeSpan? bound = null)
    {
        if (await Task.WhenAny(task, Task.Delay(bound ?? Fires)).ConfigureAwait(false) != task) return false;
        await task.ConfigureAwait(false);
        return true;
    }

    private static async Task<Exception> FaultAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return error;
        }
        throw new InvalidOperationException("dropbox watch check failed: the wait was expected to fault.");
    }

    private static RootDef Root() => new(RootId, SyncProviders.Dropbox, "account-id", "/Pilot", "id:pilot-folder");

    private static DropboxConnectionBinding Binding() => new(
        DropboxProviderConfiguration.RegisteredClientId, "account-id", "root-namespace", "home-namespace",
        DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes);

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("dropbox watch check failed: " + label);
        Console.WriteLine("PASS " + label);
    }

    // The route-caller harness: a scripted Dropbox wire, a client and the change-watch source over one root.
    private sealed class WatchRig : IDisposable
    {
        private readonly HttpClient _http;

        internal WatchRig(TimeProvider clock, string? cursor = Cursor)
        {
            Wire = new DropboxRouteWire();
            _http = new HttpClient(Wire);
            Store = new InMemoryStateStore();
            Client = new DropboxPilotClient(_http, new SecretDropboxTokenSource(), "/Pilot", Binding());
            if (cursor is not null)
            {
                var gen = Store.BeginGeneration(RootId, "snapshot");
                Store.CompleteGeneration(gen, cursor, true);
            }
            Source = new DropboxChangeWatchSource(Store, Client, V2Settings.Default, () => SafetyPoll, clock);
        }

        internal DropboxRouteWire Wire { get; }
        internal InMemoryStateStore Store { get; }
        internal DropboxPilotClient Client { get; }
        internal DropboxChangeWatchSource Source { get; }

        internal void SetCursor(string cursor)
        {
            var gen = Store.BeginGeneration(RootId, "snapshot");
            Store.CompleteGeneration(gen, cursor, true);
        }

        public void Dispose()
        {
            _http.Dispose();
            Wire.Dispose();
            Store.Dispose();
        }
    }

    // A token source whose credential boundary fails, so the client's access classification can be driven.
    private sealed class TokenRefused(Exception error) : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<string>(error);
    }

    // A thread-safe longpoll wire: it holds every request until `expected` are in flight together, then answers by the
    // cursor's root index (even: changes with backoff index+1; odd: 409). Holding past the bound fails the check.
    private sealed class ConcurrentLongpollWire(int expected) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _allInFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private int _inFlight;

        internal System.Collections.Concurrent.ConcurrentQueue<string> Polled { get; } = new();
        internal int PeakInFlight { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath != LongpollPath)
                throw new InvalidOperationException("Only the longpoll route is scripted: " + request.RequestUri.AbsolutePath);
            var cursor = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token))
                .RootElement.GetProperty("cursor").GetString()!;
            Polled.Enqueue(cursor);
            lock (_lock)
            {
                PeakInFlight = Math.Max(PeakInFlight, ++_inFlight);
                if (_inFlight == expected) _allInFlight.TrySetResult();
            }
            try
            {
                await _allInFlight.Task.WaitAsync(Fires, token).ConfigureAwait(false);
            }
            finally
            {
                lock (_lock) _inFlight--;
            }
            var index = int.Parse(cursor["cursor-rb".Length..], System.Globalization.CultureInfo.InvariantCulture);
            return index % 2 == 0
                ? Json(HttpStatusCode.OK, $"{{\"changes\":true,\"backoff\":{index + 1}}}")
                : Json(HttpStatusCode.Conflict, "{\"error_summary\":\"reset/..\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }

    // A token endpoint that refuses with 400; when given a source, it cancels it once the body is fully buffered.
    private sealed class RefusingTokenEndpoint(string body, CancellationTokenSource? cancelOnArrival)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            response.Content.LoadIntoBufferAsync().GetAwaiter().GetResult();
            cancelOnArrival?.Cancel();
            return Task.FromResult(response);
        }
    }

    // The stateful Dropbox wire the inventory and writer checks need, over the same /Pilot root.
    private sealed class WireRig : IDisposable
    {
        private readonly HttpClient _http;

        internal WireRig(IDropboxAccessTokenSource? tokens = null)
        {
            Wire = new DropboxWire();
            _http = new HttpClient(Wire);
            Store = new InMemoryStateStore();
            Client = new DropboxPilotClient(_http, tokens ?? new FixedTokenSource(), "/Pilot", Binding(),
                DropboxWire.RootId);
        }

        internal DropboxWire Wire { get; }
        internal InMemoryStateStore Store { get; }
        internal DropboxPilotClient Client { get; }

        internal DropboxInventory Inventory() => new(Root(), Side.B, Client, Store, "p1");

        internal DropboxWriter Writer() => new(Root(), Side.B, Client, Path.Combine(Path.GetTempPath(),
            "syncshark-v2-dropboxwatch-spool"));

        internal void SeedStored(params ItemState[] items)
        {
            var gen = Store.BeginGeneration(RootId, "snapshot");
            Store.WriteSnapshot(new Snapshot(RootId, Side.B, gen, "0", items, Array.Empty<string>()));
            Store.CompleteGeneration(gen, "0", true);
        }

        public void Dispose()
        {
            _http.Dispose();
            Wire.Dispose();
            Store.Dispose();
        }
    }
}
