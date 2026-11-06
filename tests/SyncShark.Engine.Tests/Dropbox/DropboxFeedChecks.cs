using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

// Over the 300-line review prompt, not split: the row owns this single physical file and all of its oracles.
/// <summary>Checks for DropboxFeed (`--engine-checks dropbox`), written by DropboxFeed's writer. The production
/// Store is the real engine database behind a forwarding double that can observe one exact call or fault it; the
/// HTTP handler stands in for Dropbox and the fake clock advances only while the feed really waits.</summary>
/// <remarks>Every case asserts the feed's own contract of design 5.7 and READING 8.2/8.6: page commit through
/// AcquireDropboxPage with its exact fences, readback versus replay after a lost reply, a stale page that changes
/// nothing, rollback of a failed local replay, continuation cycles, one longpoll with the provider maximum and
/// returned backoff, and one claimed listener wait with one registered reconnect per transient failure.</remarks>
public static class DropboxFeedChecks
{
    private const string NamespaceId = "namespace-fixture";
    private const string HomeNamespaceId = "home-fixture";
    private const string FolderId = "folder-fixture";
    private const string DescriptorPath = "/Parent";
    private const string LocalRoot = @"C:\Local";
    private const string ConnectionId = "connection-fixture";
    private const string PairId = "pair";
    private const string PairName = "Pair A";
    private const string CursorZero = "cursor-zero";
    private const string CursorOne = "cursor-one";
    private const string CursorTwo = "cursor-two";
    private const string CursorThree = "cursor-three";
    private const string StaleCursor = "cursor-stale";
    private const string DisabledCursor = "cursor-disabled";
    private const long Generation = 0;
    private const long InstalledEpoch = 1;
    private const long InstalledRevision = 2;
    private const long ResetEpoch = 2;
    private const long ResetRevision = 3;
    private const int NewerGeneration = 1;
    private const int FirstRequest = 1;
    private const int FirstPageRequest = 2;
    private const int DriveSteps = 10000;
    private const int ProviderWaitSeconds = 42;
    private const int ThrottleWaitSeconds = 30;
    private const int LongEntryLength = 300;
    private const int DirectorySerial = 1;
    private const int DirectoryFile = 2;

    // The adopted schedule without jitter, so a selected fallback gap is exact; its real numbers are its owner's checks.
    private static readonly FailureRetryOwner.BackoffSettings Adopted = new(1, 1.6, 0, 120);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan InitialWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProviderWait = TimeSpan.FromSeconds(ProviderWaitSeconds);
    private static readonly TimeSpan ThrottleWait = TimeSpan.FromSeconds(ThrottleWaitSeconds);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await InstallAndPageAsync();
        await ContinuationAsync();
        await LostReplyAsync();
        await StalePageAsync();
        await RolledBackReplayAsync();
        await LongpollBackoffAsync();
        await SignalledCursorDrainsAsync();
        await ListenerFailuresAsync();
        await OutagesAsync();
        await FiniteFailuresAsync();
        await ResetAndGenerationAsync();
        await ExpiredCursorAsync();
    }

    private static async Task InstallAndPageAsync()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (ordinal, _, _) => ordinal switch
        {
            FirstRequest => Json(new { cursor = CursorZero }),
            FirstPageRequest => Page(false, CursorOne,
                File(DescriptorPath, "Docs/Long.txt"),
                File(DescriptorPath, "Docs/Ignored.tmp"),
                Deleted(DescriptorPath, "Docs/Gone")),
            _ => CancelWith(fixture, Longpoll(false))
        };

        var error = await DriveAsync(fixture);
        Check.Require(error is OperationCanceledException, "The fixture cancels only after the observed longpoll.");

        var capture = fixture.Handler.Requests[0];
        using (var arg = JsonDocument.Parse(capture.Body))
        {
            var value = arg.RootElement;
            Check.Require(capture.Path.EndsWith(DropboxProtocol.ListFolderGetLatestCursor, StringComparison.Ordinal) &&
                value.GetProperty("path").GetString() == DescriptorPath &&
                value.GetProperty("recursive").GetBoolean() &&
                value.GetProperty("include_deleted").GetBoolean() &&
                value.GetProperty("include_mounted_folders").GetBoolean() &&
                value.GetProperty("include_non_downloadable_files").GetBoolean(),
                "A reset captures a deletion-aware cursor with the feed's exact provenance arguments.");
        }

        Check.Require(fixture.Acquires.Count == 1, "Each page commits exactly once through AcquireDropboxPage.");
        var commit = fixture.Acquires[0];
        Check.Require(commit.PairId == PairId && commit.Generation == Generation &&
            commit.Epoch == InstalledEpoch && commit.Revision == InstalledRevision &&
            commit.ExpectedCursor == CursorZero && commit.Cursor == CursorOne &&
            commit.Provenance == ExpectedProvenance(fixture, InstalledEpoch),
            "The page commit carries the installed generation, epoch, revision, provenance and expected cursor.");
        Check.Require(commit.Notices.Select(notice => notice.Path)
                .SequenceEqual(new[] { "Docs/Long.txt", "Docs/Gone" }) &&
            commit.Notices[0].Kind == ChangeKind.Changed && commit.Notices[0].Side == Side.Dropbox &&
            commit.Notices[1].Kind == ChangeKind.Deleted && commit.Notices[1].Observed is null &&
            commit.Notices[1].Id is null,
            "The page keeps provider order, drops an ignored live entry and keeps the tombstone path as a hint.");

        var source = StoredSource(fixture);
        Check.Require(source.Epoch == InstalledEpoch && source.Revision == InstalledRevision &&
            source.Cursor == CursorOne && source.Obligation == SourceObligation.Bootstrap &&
            source.Provenance == ExpectedProvenance(fixture, InstalledEpoch),
            "A first-ever pair installs epoch one, revision two and the bootstrap obligation, never an empty tree.");

        var poll = fixture.Handler.Requests[2];
        using var pollArg = JsonDocument.Parse(poll.Body);
        Check.Require(poll.Path.EndsWith(DropboxProtocol.ListFolderLongpoll, StringComparison.Ordinal) &&
            pollArg.RootElement.GetProperty("cursor").GetString() == CursorOne &&
            pollArg.RootElement.GetProperty("timeout").GetInt32() == DropboxApiLimits.LongpollMaxSeconds,
            "One longpoll is issued at the committed cursor with the provider's maximum wait.");

        // A terminal empty page at the same cursor is valid progress, not a cycle.
        using var same = new Fixture();
        same.Handler.Reply = (ordinal, _, _) => ordinal switch
        {
            FirstRequest => Json(new { cursor = CursorZero }),
            FirstPageRequest => Page(false, CursorZero),
            _ => CancelWith(same, Longpoll(false))
        };
        var sameError = await DriveAsync(same);
        Check.Require(sameError is OperationCanceledException && same.Acquires.Count == 1 &&
            same.Acquires[0].ExpectedCursor == CursorZero && same.Acquires[0].Cursor == CursorZero,
            "An empty terminal page that repeats its input cursor is accepted as progress.");
    }

    private static async Task ContinuationAsync()
    {
        using (var same = new Fixture())
        {
            same.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                _ => Page(true, CursorZero)
            };
            var error = await DriveAsync(same);
            Check.Require(error is InventoryIncompleteException && same.Handler.Requests.Count == FirstPageRequest &&
                StoredSource(same).Cursor == CursorZero,
                "A nonterminal page that repeats its input cursor is a classified source failure, not a page-count cutoff.");
        }

        // Brent's constant-space check sees the repeated nonterminal cursor after the checkpoint is refreshed.
        var cycle = new[] { CursorOne, CursorTwo, CursorThree, CursorOne, CursorTwo, CursorThree, CursorOne };
        using (var loop = new Fixture())
        {
            loop.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                _ => Page(true, cycle[ordinal - FirstPageRequest])
            };
            var error = await DriveAsync(loop);
            Check.Require(error is InventoryIncompleteException &&
                loop.Handler.Requests.Count == FirstPageRequest + cycle.Length - 1 &&
                loop.Acquires.Count == cycle.Length,
                "A longer continuation cycle is rejected in constant space rather than looping or relisting.");
            Check.Require(loop.Handler.Requests.Skip(FirstRequest)
                    .Select(request => CursorOf(request.Body))
                    .SequenceEqual(new[] { CursorZero, CursorOne, CursorTwo, CursorThree, CursorOne, CursorTwo, CursorThree }),
                "Every continuation retries the exact authoritative input cursor, and no root listing is repeated.");
        }
    }

    private static async Task LostReplyAsync()
    {
        using var fixture = new Fixture();
        var faulted = false;
        fixture.Hooks.AfterCall = method =>
        {
            if (method.Name != nameof(IStateStore.AcquireDropboxPage) || faulted) return;
            faulted = true;
            throw new IOException("lost-reply-fixture");
        };
        fixture.Handler.Reply = (ordinal, _, _) => ordinal switch
        {
            FirstRequest => Json(new { cursor = CursorZero }),
            FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/Item.txt")),
            _ => CancelWith(fixture, Longpoll(false))
        };

        var error = await DriveAsync(fixture);
        Check.Require(error is OperationCanceledException, "A committed page with a lost reply continues normally.");
        Check.Require(fixture.Acquires.Count == 1 && fixture.Acquires[0].Fault is IOException &&
            fixture.Handler.Requests.Count(request => IsContinue(request.Path)) == 1 &&
            StoredSource(fixture).Cursor == CursorOne,
            "A lost acknowledgment is resolved from the stored cursor and the page is never replayed.");
    }

    private static async Task StalePageAsync()
    {
        using var fixture = new Fixture();
        var mutated = false;
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.AcquireDropboxPage) || mutated) return;
            mutated = true;
            var current = fixture.Inner.ReadSource(PairId, Side.Dropbox)!.Value;
            fixture.Inner.AdvanceSource(PairId, Side.Dropbox, Generation, current.Epoch, current.Revision,
                current.Provenance, current.Cursor, current.Epoch, current.Revision, current.Provenance,
                StaleCursor, current.Obligation);
        };
        fixture.Handler.Reply = (ordinal, _, _) => ordinal switch
        {
            FirstRequest => Json(new { cursor = CursorZero }),
            FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/Stale.txt")),
            FirstPageRequest + 1 => Page(false, CursorTwo, File(DescriptorPath, "Docs/Current.txt")),
            _ => CancelWith(fixture, Longpoll(false))
        };

        var error = await DriveAsync(fixture);
        Check.Require(error is OperationCanceledException, "A stale page is retried from the authoritative cursor.");
        Check.Require(fixture.Acquires.Count == 2 && fixture.Acquires[0].Error is LiveAdmissionStaleException &&
            fixture.Acquires[0].Notices.Count == 0 &&
            fixture.Acquires[1].ExpectedCursor == StaleCursor && fixture.Acquires[1].Cursor == CursorTwo &&
            StoredSource(fixture).Cursor == CursorTwo,
            "A stale page changes nothing; the feed re-reads its fences and continues from the competing cursor.");
        Check.Require(CursorOf(fixture.Handler.Requests[FirstRequest].Body) == CursorZero &&
            CursorOf(fixture.Handler.Requests[FirstRequest + 1].Body) == StaleCursor,
            "The stale attempt is not replayed; the next page uses the authoritative cursor.");
    }

    private static async Task RolledBackReplayAsync()
    {
        using var fixture = new Fixture();
        fixture.Spool = () => new TruncatedSpool();
        fixture.Handler.Reply = (ordinal, _, _) => ordinal switch
        {
            FirstRequest => Json(new { cursor = CursorZero }),
            _ => Page(false, CursorOne,
                File(DescriptorPath, "Docs/" + new string('a', LongEntryLength)),
                File(DescriptorPath, "Docs/Second.txt"),
                File(DescriptorPath, "Docs/Third.txt"))
        };

        var error = await DriveAsync(fixture);
        Check.Require(error is IOException, "A local spool read failure stops the feed with its own cause.");
        Check.Require(fixture.Acquires.Count == 1 && fixture.Acquires[0].Error is IOException &&
            fixture.Acquires[0].Notices.Count > 0 &&
            fixture.Handler.Requests.Count == FirstPageRequest,
            "The failing replay had already yielded notices inside the page transaction, so ALL of them roll back.");
        var source = StoredSource(fixture);
        Check.Require(source.Epoch == InstalledEpoch && source.Revision == InstalledRevision &&
            source.Cursor == CursorZero,
            "A rolled-back page leaves the source epoch, revision and cursor exactly as they were.");
    }

    private static async Task LongpollBackoffAsync()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (ordinal, _, _) => ordinal switch
        {
            FirstRequest => Json(new { cursor = CursorZero }),
            FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/Item.txt")),
            FirstPageRequest + 1 => Longpoll(false, ProviderWaitSeconds),
            _ => CancelWith(fixture, Longpoll(false))
        };

        var error = await DriveAsync(fixture);
        Check.Require(error is OperationCanceledException, "Only the delayed second wait is interrupted.");
        var requests = fixture.Handler.Requests;
        Check.Require(requests.Count == FirstPageRequest + 2 &&
            requests.Skip(FirstPageRequest).All(request => IsLongpoll(request.Path)) &&
            requests.Skip(FirstPageRequest).All(request =>
                TimeoutOf(request.Body) == DropboxApiLimits.LongpollMaxSeconds &&
                CursorOf(request.Body) == CursorOne),
            "Each longpoll carries the committed cursor and the provider maximum wait.");

        var gap = requests[FirstPageRequest + 1].Sent - requests[FirstPageRequest].Sent;
        // The fake clock advances in ticks, so the delayed request lands on the first tick at or after the floor.
        Check.Require(gap >= ProviderWait && gap <= ProviderWait + TimeSpan.FromSeconds(1),
            "The accepted result.backoff alone governs the next wait, with no second generic delay.");
    }

    private static async Task SignalledCursorDrainsAsync()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (ordinal, _, _) => ordinal switch
        {
            FirstRequest => Json(new { cursor = CursorZero }),
            FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/First.txt")),
            FirstPageRequest + 1 => Longpoll(true),
            FirstPageRequest + 2 => Page(false, CursorTwo, File(DescriptorPath, "Docs/Second.txt")),
            _ => CancelWith(fixture, Longpoll(false))
        };

        var error = await DriveAsync(fixture);
        Check.Require(error is OperationCanceledException, "The fixture cancels after the signalled cursor drains.");
        var requests = fixture.Handler.Requests;
        Check.Require(requests.Count == FirstPageRequest + 3 &&
            requests[2].Path.EndsWith(DropboxProtocol.ListFolderLongpoll, StringComparison.Ordinal) &&
            IsContinue(requests[3].Path) && CursorOf(requests[3].Body) == CursorOne &&
            IsLongpoll(requests[4].Path) && CursorOf(requests[4].Body) == CursorTwo,
            "A signalled cursor is drained by a continuation before the feed waits on it again; no signalled-cursor spin.");
    }

    private static async Task ListenerFailuresAsync()
    {
        // A transient failure registers exactly one reconnect; a later accepted response resets the listener stage.
        using (var fixture = new Fixture())
        {
            fixture.WakeImmediately = true;
            fixture.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/First.txt")),
                FirstPageRequest + 1 => throw new HttpRequestException("network-fixture"),
                FirstPageRequest + 2 => Page(false, CursorTwo, File(DescriptorPath, "Docs/Second.txt")),
                FirstPageRequest + 3 => Longpoll(false),
                FirstPageRequest + 4 => throw new HttpRequestException("network-fixture"),
                FirstPageRequest + 5 => Page(false, CursorThree, File(DescriptorPath, "Docs/Third.txt")),
                _ => CancelWith(fixture, Longpoll(false))
            };

            var error = await DriveAsync(fixture);
            Check.Require(error is OperationCanceledException, "The fixture cancels after the second recovered failure.");
            var registrations = fixture.Registrations;
            Check.Require(registrations.Count == 2 &&
                registrations.All(registration => registration.Wait == InitialWait) &&
                registrations.All(registration => registration.Identity ==
                    (fixture.Account, DropboxProtocol.ListFolderLongpoll, fixture.Source)),
                "One transient longpoll failure registers exactly one listener wait for the exact account, route and source; an accepted response resets the stage to the initial delay.");
            Check.Require(fixture.Withdrawn == 2 && registrations.All(registration => registration.Disposed),
                "Each registered reconnect is withdrawn when its wake completes.");
            Check.Require(fixture.Handler.Requests.Count(request => IsLongpoll(request.Path)) == 4,
                "A listener failure makes one request; no nested retry resends it before the registered wait.");
        }

        // An applicable provider wait takes precedence over the generic fallback and is not shortened.
        using (var throttle = new Fixture())
        {
            throttle.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/Item.txt")),
                _ => Throttled(ThrottleWait)
            };
            throttle.Poll = observed =>
            {
                if (observed.Registrations.Count == 1) observed.Stop.Cancel();
            };

            var error = await DriveAsync(throttle);
            Check.Require(error is OperationCanceledException &&
                throttle.Handler.Requests.Count == FirstPageRequest + 1,
                "The throttled longpoll is not resent while its provider wait is outstanding.");
            var registration = throttle.Registrations.Single();
            Check.Require(registration.Wait >= ThrottleWait - TimeSpan.FromSeconds(1) &&
                registration.Wait <= ThrottleWait + Tick &&
                registration.Identity == (throttle.Account, DropboxProtocol.ListFolderLongpoll, throttle.Source),
                "The provider's Retry-After is adopted unchanged for the exact source instead of the generic fallback.");
        }

        // Cancellation while a reconnect is outstanding stops the feed and withdraws the registration.
        using (var cancelled = new Fixture())
        {
            cancelled.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/Item.txt")),
                _ => throw new HttpRequestException("network-fixture")
            };
            cancelled.Poll = observed =>
            {
                if (observed.Registrations.Count == 1) observed.Stop.Cancel();
            };

            var error = await DriveAsync(cancelled);
            Check.Require(error is OperationCanceledException && cancelled.Registrations.Count == 1 &&
                cancelled.Registrations[0].Disposed && cancelled.Withdrawn == 1 &&
                cancelled.Handler.Requests.Count == FirstPageRequest + 1,
                "Cancelling a waiting reconnect stops the feed, withdraws its registration and sends no further request.");
        }

        // A permanent failure is not a reconnect: the attempt owns no resend and the error surfaces once.
        using (var permanent = new Fixture())
        {
            permanent.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/Item.txt")),
                _ => Error(HttpStatusCode.Conflict, "path/not_found/..")
            };

            var error = await DriveAsync(permanent);
            Check.Require(error is DropboxApiException && permanent.Registrations.Count == 0 &&
                permanent.Handler.Requests.Count == FirstPageRequest + 1 && permanent.Outages == 0,
                "A permanent listener failure surfaces its one classified result, registers no automatic resend and opens no outage.");
        }
    }

    /// <summary>One outage per reconnect episode: opened by the first claimed transient failure, coalesced across
    /// later failures, and closed once on an accepted reply, on feed exit or on the finite caller's own exit.</summary>
    private static async Task OutagesAsync()
    {
        using (var coalesced = new Fixture())
        {
            coalesced.WakeImmediately = true;
            coalesced.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/First.txt")),
                FirstPageRequest + 1 => throw new HttpRequestException("network-fixture"),
                FirstPageRequest + 2 => throw new HttpRequestException("network-fixture"),
                FirstPageRequest + 3 => Page(false, CursorTwo, File(DescriptorPath, "Docs/Second.txt")),
                _ => CancelWith(coalesced, Longpoll(false))
            };

            var error = await DriveAsync(coalesced);
            Check.Require(error is OperationCanceledException &&
                coalesced.Outages == 1 && coalesced.OutagesEnded == 1,
                "Two transient failures inside one reconnect episode open one outage, and the accepted page closes it once.");
        }

        using (var cancelled = new Fixture())
        {
            cancelled.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/Item.txt")),
                _ => throw new HttpRequestException("network-fixture")
            };
            cancelled.Poll = observed =>
            {
                if (observed.Registrations.Count == 1) observed.Stop.Cancel();
            };

            var error = await DriveAsync(cancelled);
            Check.Require(error is OperationCanceledException && cancelled.Outages == 1 &&
                cancelled.OutagesEnded == 1,
                "Cancelling the feed while its outage is open closes that one outage on exit.");
        }

        using (var finite = new Fixture())
        {
            finite.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => throw new HttpRequestException("network-fixture"),
                FirstRequest + 1 => Error(HttpStatusCode.Conflict, "path/not_found/.."),
                _ => Page(false, CursorOne)
            };
            Exception? failure = null;
            var catchingUp = finite.Feed.CatchUpAsync(finite.Stop.Token);
            for (var step = 0; step < DriveSteps && !catchingUp.IsCompleted; step++)
            {
                await Task.Yield();
                finite.Time.Advance(Tick);
            }
            Check.Require(catchingUp.IsCompleted, "The finite outage case finishes inside the bounded drive.");
            try { await catchingUp; }
            catch (Exception error) { failure = error; }
            Check.Require(failure is DropboxApiException && finite.Outages == 1 && finite.OutagesEnded == 1,
                "A finite capture wait opens one outage and the finite caller's own exit closes it.");
        }
    }

    private static async Task FiniteFailuresAsync()
    {
        foreach (var capture in new[] { false, true })
        foreach (var permanent in new[] { false, true })
        {
            using var fixture = new Fixture();
            var failedRequest = capture ? FirstRequest : FirstPageRequest;
            fixture.Handler.Reply = (ordinal, _, _) => ordinal == failedRequest
                ? Error(permanent ? HttpStatusCode.Conflict : HttpStatusCode.InternalServerError,
                    permanent ? "path/not_found/.." : "internal_error/..")
                : capture && ordinal == FirstRequest + 1 || !capture && ordinal == FirstRequest
                    ? Json(new { cursor = CursorZero })
                    : Page(false, CursorOne);
            Exception? failure = null;
            var catchingUp = fixture.Feed.CatchUpAsync(fixture.Stop.Token);
            for (var step = 0; step < DriveSteps && !catchingUp.IsCompleted; step++)
            {
                await Task.Yield();
                fixture.Time.Advance(Tick);
            }
            Check.Require(catchingUp.IsCompleted, "Finite retry completes without a background deadline dispatcher.");
            try { await catchingUp; }
            catch (Exception error) { failure = error; }
            if (permanent)
            {
                Check.Require(failure is DropboxApiException && fixture.Registrations.Count == 0 &&
                    fixture.Handler.Requests.Count == failedRequest,
                    "A permanent finite feed failure surfaces without a reconnect or a silent retry loop.");
                continue;
            }
            Check.Require(fixture.Registrations.Count == 0,
                "A finite retry does not depend on a background listener deadline dispatcher.");
            if (!capture)
            {
                Check.Require(failure is LiveAdmissionStaleException && fixture.Acquires.Count == 0 &&
                    StoredSource(fixture).Cursor == CursorZero,
                    "A failed finite page holds admission and preserves the unpublished cursor.");
                await fixture.Feed.CatchUpAsync(fixture.Stop.Token);
                var pages = fixture.Handler.Requests.Where(request => IsContinue(request.Path)).ToArray();
                Check.Require(pages.Length == 2 && pages.All(request => CursorOf(request.Body) == CursorZero),
                    "The next finite opportunity retries only the same unpublished page, not a relist.");
            }
            else Check.Require(failure is null, "A transient cursor capture recovers through the existing install loop.");
            var requests = fixture.Handler.Requests;
            Check.Require(requests[failedRequest].Sent - requests[failedRequest - 1].Sent >= InitialWait,
                "The finite retry cannot resend before its adopted listener wait expires.");
            Check.Require(StoredSource(fixture).Cursor == CursorOne && fixture.Acquires.Count == 1,
                "Recovered finite acquisition commits the page exactly once.");
        }
    }

    private static async Task ExpiredCursorAsync()
    {
        foreach (var finite in new[] { false, true })
        {
            using var fixture = new Fixture();
            MarkRepaired(fixture);
            var initial = StoredSource(fixture);
            fixture.Store.AdvanceSource(PairId, Side.Dropbox, Generation, initial.Epoch, initial.Revision,
                initial.Provenance, initial.Cursor, initial.Epoch, initial.Revision, initial.Provenance,
                initial.Cursor, null);
            var notified = false;
            fixture.Feed.WorkCommitted = () =>
            {
                var source = StoredSource(fixture);
                if (source.Cursor is null && source.Obligation == SourceObligation.NormalRepair) notified = true;
            };
            fixture.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                FirstPageRequest => Json(new Dictionary<string, object>
                {
                    ["error_summary"] = "reset/..",
                    ["error"] = new Dictionary<string, object> { [".tag"] = "reset" }
                }, HttpStatusCode.Conflict),
                FirstPageRequest + 1 => Json(new { cursor = CursorTwo }),
                FirstPageRequest + 2 => Page(false, CursorThree),
                _ => CancelWith(fixture, Longpoll(false))
            };
            if (finite)
            {
                Exception? failure = null;
                try { await fixture.Feed.CatchUpAsync(CancellationToken.None); }
                catch (Exception error) { failure = error; }
                Check.Require(failure is LiveAdmissionStaleException && notified &&
                    StoredSource(fixture).Cursor is null && fixture.Registrations.Count == 0,
                    "A finite reset invalidates Repair coverage and durably requests Repair before returning.");
            }
            else
            {
                var error = await DriveAsync(fixture);
                Check.Require(error is OperationCanceledException && notified &&
                    StoredSource(fixture).Cursor == CursorThree &&
                    StoredSource(fixture).Obligation == SourceObligation.NormalRepair &&
                    fixture.Registrations.Count == 0,
                    "An expired cursor requests pair Repair and acquisition keeps running without a host-fatal error.");
            }
        }
    }

    private static async Task ResetAndGenerationAsync()
    {
        // A binding mismatch needs a fresh epoch and cursor that retains the existing obligation, never an empty tree.
        using (var reset = new Fixture())
        {
            var previous = new SourceProvenance(reset.Account, NamespaceId, FolderId, "/Other",
                true, true, true, true, Generation, InstalledEpoch);
            reset.Store.AdvanceSource(PairId, Side.Dropbox, Generation, 0, 1, null, null,
                InstalledEpoch, InstalledRevision, previous, DisabledCursor, SourceObligation.Bootstrap);
            reset.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/Item.txt")),
                _ => CancelWith(reset, Longpoll(false))
            };

            var error = await DriveAsync(reset);
            Check.Require(error is OperationCanceledException && reset.Acquires.Count == 1,
                "A mismatched binding re-captures a cursor and continues acquisition.");
            var source = StoredSource(reset);
            Check.Require(source.Epoch == ResetEpoch && source.Revision == ResetRevision &&
                source.Cursor == CursorOne && source.Obligation == SourceObligation.Bootstrap &&
                source.Provenance == ExpectedProvenance(reset, ResetEpoch),
                "The reset installs the next epoch and revision with the current binding and retains the obligation.");
        }

        // A repaired pair's lost interval installs NormalRepair from the one source owner, not a caller's fixed value.
        using (var repaired = new Fixture())
        {
            var previous = new SourceProvenance(repaired.Account, NamespaceId, FolderId, "/Other",
                true, true, true, true, Generation, InstalledEpoch);
            repaired.Store.AdvanceSource(PairId, Side.Dropbox, Generation, 0, 1, null, null,
                InstalledEpoch, InstalledRevision, previous, DisabledCursor, null);
            MarkRepaired(repaired);
            repaired.Handler.Reply = (ordinal, _, _) => ordinal switch
            {
                FirstRequest => Json(new { cursor = CursorZero }),
                FirstPageRequest => Page(false, CursorOne, File(DescriptorPath, "Docs/Item.txt")),
                _ => CancelWith(repaired, Longpoll(false))
            };

            var error = await DriveAsync(repaired);
            Check.Require(error is OperationCanceledException && repaired.Acquires.Count == 1 &&
                StoredSource(repaired).Obligation == SourceObligation.NormalRepair,
                "A repaired pair's lost interval installs the normal repair obligation.");
        }

        // A stored provenance from a newer pair generation ends this feed before it captures or writes anything.
        using (var newer = new Fixture())
        {
            var newerProvenance = new SourceProvenance(newer.Account, NamespaceId, FolderId,
                PathNames.TrimDropboxRoot(DescriptorPath), true, true, true, true, NewerGeneration, InstalledEpoch);
            newer.Store.AdvanceSource(PairId, Side.Dropbox, Generation, 0, 1, null, null,
                InstalledEpoch, InstalledRevision, newerProvenance, DisabledCursor, SourceObligation.NormalRepair);

            var error = await DriveAsync(newer);
            Check.Require(error is null && newer.Handler.Requests.Count == 0 && newer.Acquires.Count == 0 &&
                StoredSource(newer).Cursor == DisabledCursor,
                "A newer pair generation ends the feed without adopting the cursor or mutating the source.");
        }
    }

    // The real store owns first_repair_done; a second connection marks the one durable fact a completed Repair sets.
    private static void MarkRepaired(Fixture fixture)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(fixture.TempRoot.FullName, "state.db"), Pooling = false
            }.ToString());
        connection.Open();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE pair SET first_repair_done=1;";
        update.ExecuteNonQuery();
    }

    private static SourceProvenance ExpectedProvenance(Fixture fixture, long epoch) => new(
        fixture.Account, NamespaceId, FolderId, PathNames.TrimDropboxRoot(DescriptorPath),
        true, true, true, true, Generation, epoch);

    private static (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation,
        long Revision) StoredSource(Fixture fixture) =>
        fixture.Store.ReadSource(PairId, Side.Dropbox)!.Value;

    private static string? CursorOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("cursor").GetString();
    }

    private static int TimeoutOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("timeout").GetInt32();
    }

    private static bool IsContinue(string path) =>
        path.EndsWith(DropboxProtocol.ListFolderContinue, StringComparison.Ordinal);

    private static bool IsLongpoll(string path) =>
        path.EndsWith(DropboxProtocol.ListFolderLongpoll, StringComparison.Ordinal);

    private static HttpResponseMessage CancelWith(Fixture fixture, HttpResponseMessage response)
    {
        fixture.Stop.Cancel();
        return response;
    }

    // Only Task.Yield suspends the fixture's own loop, so the fake clock moves solely while the feed really waits.
    private static async Task<Exception?> DriveAsync(Fixture fixture)
    {
        var run = fixture.Feed.RunAsync(fixture.Stop.Token);
        for (var step = 0; step < DriveSteps && !run.IsCompleted; step++)
        {
            fixture.Poll?.Invoke(fixture);
            await Task.Yield();
            fixture.Time.Advance(Tick);
        }
        Check.Require(run.IsCompleted, "The feed must stop within the fixture's bounded drive; a hang is a defect.");
        try
        {
            await run;
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static PairConfig MakePair(string account) => new(PairId, PairName, LocalRoot, DescriptorPath,
        ConnectionId, new[] { "FileExtension:.tmp" }, Array.Empty<Exclusion>(), FirstRunPolicy.NewestWins, true, null,
        new RootBinding(LocalRoot, DescriptorPath, ConnectionId,
            new DirectoryIdentity(DirectorySerial, DirectoryFile, null),
            new DropboxRootIdentity(account, NamespaceId, HomeNamespaceId, FolderId)));

    private static Dictionary<string, object> File(string root, string path) => new()
    {
        [DropboxProtocol.Tag] = DropboxProtocol.TagFile,
        [DropboxProtocol.PathDisplay] = root.TrimEnd('/') + "/" + path,
        [DropboxProtocol.Id] = "id:" + path,
        [DropboxProtocol.Rev] = "rev-" + path.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [DropboxProtocol.Size] = 1L,
        [DropboxProtocol.ClientModified] = "2026-01-01T00:00:00Z",
        [DropboxProtocol.ContentHash] = new string('a', 64),
        [DropboxProtocol.IsDownloadable] = true
    };

    private static Dictionary<string, object> Deleted(string root, string path) => new()
    {
        [DropboxProtocol.Tag] = DropboxProtocol.TagDeleted,
        [DropboxProtocol.PathDisplay] = root.TrimEnd('/') + "/" + path
    };

    private static HttpResponseMessage Page(bool more, string cursor, params object[] entries) =>
        Json(new { entries, cursor, has_more = more });

    private static HttpResponseMessage Longpoll(bool changes, int? backoff = null) =>
        Json(new { changes, backoff });

    private static HttpResponseMessage Throttled(TimeSpan wait)
    {
        var response = Error(HttpStatusCode.TooManyRequests, "too_many_requests/..");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string summary) =>
        Json(new { error_summary = summary }, status);

    private static HttpResponseMessage Json(object data, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly List<Registration> _registrations = [];
        private readonly object _gate = new();
        private int _withdrawn;

        public Fixture()
        {
            TempRoot = Directory.CreateTempSubdirectory("SyncSharkFeed-");
            Inner = StateStore.Open(Path.Combine(TempRoot.FullName, "state.db"));
            Account = Guid.NewGuid().ToString("N");
            Source = Guid.NewGuid().ToString("N");
            Pair = MakePair(Account);
            Inner.SavePair(Pair);
            var proxy = DispatchProxy.Create<IStateStore, StoreProxy>();
            Hooks = (StoreProxy)(object)proxy;
            Hooks.Inner = Inner;
            Store = proxy;
            Time = new LedgerTime();
            Handler = new Handler(Time);
            _http = new HttpClient(Handler);
            var network = new FakeNetworkSignal();
            network.SetAvailable(true);
            var limiter = new AdaptiveLimiter(1, 1, Time, network, _ => { }, Adopted);
            var api = new DropboxApi(_http, new Tokens(), NamespaceId, limiter,
                _ => new ValueTask<Stream>(Spool()), Adopted, Time, Account, Source);
            var scope = new PathScope(Pair, [Pair], null);
            Feed = new DropboxFeed(Store, Pair, Generation, scope, api, new DropboxListing(api, Pair, scope),
                Adopted, Time, Register, OpenOutage, Source);
        }

        public DirectoryInfo TempRoot { get; }
        public IStateStore Inner { get; }
        public StoreProxy Hooks { get; }
        public IStateStore Store { get; }
        public LedgerTime Time { get; }
        public Handler Handler { get; }
        public PairConfig Pair { get; }
        public DropboxFeed Feed { get; }
        public CancellationTokenSource Stop { get; } = new();
        public string Account { get; }
        public string Source { get; }
        public Func<Stream> Spool { get; set; } = () => new MemoryStream();
        public bool WakeImmediately { get; set; }
        public Action<Fixture>? Poll { get; set; }
        public int Outages { get; private set; }
        public int OutagesEnded { get; private set; }

        private Func<Action> OpenOutage => () =>
        {
            lock (_gate) Outages++;
            return () => { lock (_gate) OutagesEnded++; };
        };

        public IReadOnlyList<Registration> Registrations
        {
            get { lock (_gate) return _registrations.ToList(); }
        }

        public int Withdrawn => Volatile.Read(ref _withdrawn);

        public IReadOnlyList<AcquireCall> Acquires => Hooks.Acquires;

        private IDisposable Register((string AccountId, string RouteName, string SourceIdentity) identity,
            TimeSpan wait, Func<CancellationToken, Task> wake)
        {
            var registration = new Registration(this, identity, wait);
            lock (_gate) _registrations.Add(registration);
            // A timer firing immediately models the wake owner reaching the same registered attempt at this tick.
            if (WakeImmediately) wake(CancellationToken.None);
            return registration;
        }

        public void Withdrew() => Interlocked.Increment(ref _withdrawn);

        public void Dispose()
        {
            Stop.Dispose();
            _http.Dispose();
            Inner.Dispose();
            Directory.Delete(TempRoot.FullName, true);
        }
    }

    private sealed class Registration(Fixture owner, (string AccountId, string RouteName, string SourceIdentity) identity,
        TimeSpan wait) : IDisposable
    {
        private int _disposed;

        public (string AccountId, string RouteName, string SourceIdentity) Identity { get; } = identity;
        public TimeSpan Wait { get; } = wait;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Withdrew();
        }
    }

    /// <summary>Forwards every store call to the real engine database unchanged, so the page CAS, source fences and
    /// notice rows stay the production ones, while letting a case observe one exact call or fault it. A fault in
    /// <see cref="BeforeCall"/> models an unchanged rollback, a fault in <see cref="AfterCall"/> models a lost reply.</summary>
    public class StoreProxy : DispatchProxy
    {
        private readonly List<AcquireCall> _acquires = [];
        private IStateStore? _inner;

        public IStateStore Inner
        {
            get => _inner ?? throw new InvalidOperationException("The store double is unbound.");
            set => _inner = value;
        }

        public Action<MethodInfo>? BeforeCall { get; set; }
        public Action<MethodInfo>? AfterCall { get; set; }

        public IReadOnlyList<AcquireCall> Acquires
        {
            get { lock (_acquires) return _acquires.ToList(); }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            BeforeCall?.Invoke(targetMethod);
            AcquireCall? call = null;
            if (targetMethod.Name == nameof(IStateStore.AcquireDropboxPage) && args is not null)
            {
                var index = Array.FindIndex(targetMethod.GetParameters(),
                    parameter => parameter.Name == "notices");
                var recorded = new List<ChangeNotice>();
                args[index] = Record((IEnumerable<ChangeNotice>)args[index]!, recorded);
                call = new AcquireCall((string)args[0]!, (long)args[1]!, (long)args[2]!, (long)args[3]!,
                    (SourceProvenance)args[4]!, (string?)args[5]!, (string)args[7]!, recorded);
                lock (_acquires) _acquires.Add(call);
            }
            object? result;
            try
            {
                result = targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException failure)
            {
                var error = failure.InnerException ?? failure;
                if (call is not null) call.Error = error;
                throw error;
            }
            try
            {
                AfterCall?.Invoke(targetMethod);
            }
            catch (Exception fault)
            {
                if (call is not null) call.Fault = fault;
                throw;
            }
            return result;
        }

        private static IEnumerable<ChangeNotice> Record(IEnumerable<ChangeNotice> source, List<ChangeNotice> recorded)
        {
            foreach (var notice in source)
            {
                recorded.Add(notice);
                yield return notice;
            }
        }
    }

    public sealed class AcquireCall(string pairId, long generation, long epoch, long revision,
        SourceProvenance provenance, string? expectedCursor, string cursor, List<ChangeNotice> notices)
    {
        public string PairId { get; } = pairId;
        public long Generation { get; } = generation;
        public long Epoch { get; } = epoch;
        public long Revision { get; } = revision;
        public SourceProvenance Provenance { get; } = provenance;
        public string? ExpectedCursor { get; } = expectedCursor;
        public string Cursor { get; } = cursor;
        public List<ChangeNotice> Notices { get; } = notices;
        public Exception? Error { get; set; }
        public Exception? Fault { get; set; }
    }

    /// <summary>A spool whose reads fail near its end, so the page transaction has already yielded earlier notices
    /// when the local read fails and every one of them must roll back with the cursor.</summary>
    private sealed class TruncatedSpool : MemoryStream
    {
        private long _failAt = -1;

        private long FailAt => _failAt >= 0 ? _failAt : _failAt = Length - 1;

        private void Guard(int count)
        {
            if (Position >= FailAt || Position + count > FailAt) throw new IOException("spool read failed");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Guard(count);
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Guard(buffer.Length);
            return base.Read(buffer);
        }

        public override int ReadByte()
        {
            Guard(1);
            return base.ReadByte();
        }
    }

    private sealed class Tokens : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("token-fixture");
    }

    /// <summary>The fake clock the limiter needs: the FakeTimeProvider's own UTC/timers plus timestamps and elapsed
    /// time in the same unit, so a provider floor expires exactly when the test advances the clock.</summary>
    private sealed class LedgerTime : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();

        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _clock.CreateTimer(callback, state, dueTime, period);

        public void Advance(TimeSpan by) => _clock.Advance(by);
    }

    private sealed record Request(string Path, string Body, DateTimeOffset Sent);

    private sealed class Handler(TimeProvider time) : HttpMessageHandler
    {
        private readonly List<Request> _requests = [];
        private readonly object _gate = new();

        public Func<int, string, string, HttpResponseMessage> Reply { get; set; } =
            (_, _, _) => throw new InvalidOperationException("Unexpected fixture request.");

        public IReadOnlyList<Request> Requests
        {
            get { lock (_gate) return _requests.ToList(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            int ordinal;
            lock (_gate)
            {
                _requests.Add(new Request(path, body, time.GetUtcNow()));
                ordinal = _requests.Count;
            }
            return Reply(ordinal, path, body);
        }
    }
}
