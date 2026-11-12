using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using ListingChecks = Massdex.SyncShark.Engine.Tests.Dropbox.DropboxListingChecks;
using LocalListingChecks = Massdex.SyncShark.Engine.Tests.Local.LocalListingChecks;
using RepairChecks = Massdex.SyncShark.Engine.Tests.Run.RepairChecks;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for ResolutionRunner (`--engine-checks syncer`).</summary>
public static class ResolutionRunnerChecks
{
    // A real folder owner opens its frozen plan on the store, so every context needs a real database.
    private static readonly AsyncLocal<string?> StorageRoot = new();

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        StorageRoot.Value = Directory.CreateTempSubdirectory(nameof(ResolutionRunnerChecks)).FullName;
        try { await RunCasesAsync(); }
        finally { Directory.Delete(StorageRoot.Value!, recursive: true); }
    }

    private static async Task RunCasesAsync()
    {
        foreach (var cause in new[] { TextKey.TieEqualTimes, TextKey.TieUnreliableTime,
                     TextKey.TieCaseBoth, TextKey.TieKindBoth })
        foreach (var choice in new[] { ResolutionChoice.UseLocal, ResolutionChoice.UseDropbox, ResolutionChoice.Dismiss })
            await TieAsync(cause, choice);
        foreach (var cause in new[] { TextKey.TieEqualTimes, TextKey.TieUnreliableTime })
            await TieAsync(cause, ResolutionChoice.KeepBoth);
        foreach (var kind in new[] { IssueKind.Info, IssueKind.Skipped, IssueKind.NameClash, IssueKind.Tie, IssueKind.Failure })
        foreach (var unavailable in new[] { false, true })
            await DismissAsync(kind, unavailable);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            await RenameAsync(side, true, false);
            await RenameAsync(side, false, false);
            await RenameAsync(side, true, true);
            await RenameAsync(side, true, false, freedFolder: true);
        }
        await IncomingClashChoicesAsync();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var failCopy in new[] { false, true })
            await FolderOverFolderAsync(side, failCopy);
        await FolderRetryAsync();
        await UnknownMoveReadGateAsync();
        await UnknownMoveSuccessfulRetryAsync();
        await UnknownMoveActFailureAsync();
        await UnknownMoveRepeatedRetryAsync();
        await ObservedFolderRetryDebtAsync();
        RegisteredItemsWithin();
        await ObservedFolderFailurePathAsync();
        await UnknownMoveUnderFolderRetryAsync();
        var failures = new List<Exception>();
        foreach (var matched in new[] { false, true })
        {
            try { await CorruptHistoryRetryAsync(matched); }
            catch (Exception error) { failures.Add(new InvalidOperationException("Corrupt Retry matched=" + matched, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
        await InvalidAsync(false);
        await InvalidAsync(true);
        await StaleBeforeBadChoiceAsync();
        await LockAndCancellationAsync();
        await SelectedAdmissionAsync(false);
        await SelectedAdmissionAsync(true);
        await SelectedExecutionFreshnessAsync();
        await SelectedAncestorRefusalAsync();
        foreach (var failure in new[] { "none", "F1", "F2", "cancel", "gone" })
            await ListingOccurrencesAsync(failure);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var incoming in new[] { true, false })
        foreach (var fail in new[] { false, true })
            await FolderAsync(side, incoming, fail);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var failChild in new[] { false, true })
            await FolderTieAsync(side, failChild);
        foreach (var stale in new[] { false, true })
        foreach (var reverse in new[] { false, true })
            await MappedCandidatesAsync(stale, reverse);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
            await ThreeItemAsync(side, false);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var incoming in new[] { true, false })
        foreach (var fail in new[] { false, true })
        foreach (var occupantFolder in new[] { false, true })
            await ReplaceFileAsync(side, incoming, fail, occupantFolder);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
            await TieHoldsOrdinaryWorkAsync(side);
        foreach (var dropboxChanged in new[] { false, true })
            await StaleSelectedReplacementAsync(dropboxChanged);
        await SelectedTieReplacementAsync();
        await OtherNoticeHoldsReplacementAsync();
        await HeldAncestorReplacementAsync();
        await DifferingKeyHoldAsync();
        await RetainedSelectedChildAsync();
        await PlannedRetainedChildAsync();
        await FailedSelectedSendAsync();
        await SelectedUnreadableChildAsync();
        await UnboundRowHoldsAsync();
        await LocalFolderOtherHoldAsync();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            await ThreeItemAsync(side, true);
            await SamePathFollowUpAsync(side);
            await MoveFollowUpsAsync(side, true, false, 0, false, failListing: true);
        }
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var kind in new[] { IssueKind.Tie, IssueKind.NameClash })
        foreach (var failure in new[] { "root", "folder/a", "folder/nested", "folder/z", "cancel" })
            await FolderFailuresAsync(side, kind, failure);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var folder in new[] { false, true })
        foreach (var sameSide in new[] { false, true })
        foreach (var failure in new[] { (Root: 0, Throws: false), (Root: 1, Throws: false),
                     (Root: 2, Throws: false), (Root: 1, Throws: true), (Root: 2, Throws: true) })
            await MoveFollowUpsAsync(side, folder, sameSide, failure.Root, failure.Throws);
    }

    private static async Task UnknownMoveReadGateAsync()
    {
        var failures = new List<Exception>();
        foreach (var real in new[] { false, true })
        foreach (var bound in new[] { false, true })
        foreach (var scenario in new[] { "absent", "outside", "intermediate", "malformed", "cancel" })
        {
            if (bound && scenario == "intermediate") continue;
            try { await CheckAsync(real, bound, scenario); }
            catch (Exception error) { failures.Add(new InvalidOperationException(
                "Unknown-move read gate real=" + real + " bound=" + bound + " scenario=" + scenario, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);

        static async Task CheckAsync(bool real, bool bound, string scenario)
        {
            var original = Context(new FakeAdmission());
            using var realStore = original.Store;
            using var memory = real ? null : new InMemoryStateStore();
            IStateStore store = memory is null ? realStore : memory;
            if (memory is not null)
            {
                store.SavePair(original.Pair);
                SourceFixture.AcquireJournal(store, original.Pair);
            }
            var events = new List<RunEvent>();
            var context = original with { Store = store, Events = events.Add };
            if (bound)
            {
                var local = new Entry("Source", false, 0, DateTimeOffset.UnixEpoch, "local-id", "", "", null);
                var remoteEntry = local with { Id = "id:move" };
                store.Commit(context.Pair.PairId, StateChange.Empty with
                    { Record = [SyncedEntries.From(local, remoteEntry)] });
            }
            var move = new IssueSubject.UnknownMove("id:move", "Source", "Destination", ".syncshark-case", false);
            var subject = new IssueSubject([new(Side.Dropbox, move.Source, move.ExpectedId)], Side.Dropbox)
                { Move = move };
            store.Commit(context.Pair.PairId, StateChange.Empty with
                { Raise = [(IssueKind.Failure, TextKey.DropboxRefused, move.Source, subject)] });
            var issue = store.Issues(context.Pair.PairId).Single();
            using var cancellation = new CancellationTokenSource();
            using var fixture = new ListingChecks.Fixture(context.Pair, _ => scenario switch
            {
                "absent" => new HttpResponseMessage(HttpStatusCode.Conflict)
                    { Content = new StringContent("{\"error_summary\":\"path/not_found/..\"}") },
                "outside" => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                        ListingChecks.File("/Elsewhere", "Item", id: move.ExpectedId))) },
                "intermediate" => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                        ListingChecks.File(context.Pair.DropboxRoot, move.Intermediate!, id: move.ExpectedId))) },
                "malformed" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") },
                "cancel" => Cancel(),
                _ => throw new InvalidOperationException(scenario)
            });
            var remote = new DropboxSide(fixture.Api, context.Pair, context.Scope, context.Settings, fixture.Listing);
            context = context with { Dropbox = remote.WithListingContext(ListingChecks.BeginListing) };
            var probe = System.Reflection.DispatchProxy.Create<IStateStore, ChangeRunnerChecks.ForwardStore>();
            var counted = (ChangeRunnerChecks.ForwardStore)probe;
            counted.Inner = store;
            context = context with { Store = probe };
            var runner = Runner(context);
            SyncOutcome? outcome = null;
            Exception? caught = null;
            try { outcome = await runner.ResolveAsync(issue, ResolutionChoice.Retry, null, null, cancellation.Token); }
            catch (Exception error) { caught = error; }
            Check.Require(scenario == "cancel" ? caught is OperationCanceledException && outcome is null :
                caught is null && outcome is { None: NoneReason.Held, Raised: IssueKind.Failure },
                "Uncertain, out-of-scope and unbound intermediate reads keep Retry held; cancellation propagates.");
            Check.Require(!runner.Resolved && store.IssueById(context.Pair.PairId, issue.Id)?.Matches(issue) == true &&
                counted.Count(nameof(IStateStore.Commit)) == 0 && fixture.Handler.Requests.Count == 1 &&
                fixture.Handler.Requests[0].Path.EndsWith(DropboxProtocol.GetMetadata, StringComparison.Ordinal),
                "The by-ID gate sends exactly one metadata request, never closes or rewrites the original issue.");
            Check.Require(scenario == "malformed" ? events.Count == 1 && events[0] is
                    { Kind: RunEventKind.Failed, Error: ItemFailedException { InnerException: System.Text.Json.JsonException } } :
                events.Count == 0,
                "Malformed HTTP emits one failed event; keep and cancellation do not invent a Failure or event.");

            HttpResponseMessage Cancel()
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        }
    }

    // P2-1: an item error while settling is the Retry's outcome; the restore decided inside settlement stands.
    private static async Task UnknownMoveActFailureAsync()
    {
        foreach (var real in new[] { false, true })
        foreach (var cause in new[] { TextKey.ReadLocalFailed, TextKey.FailLocalInUse })
        {
            var original = Context(new FakeAdmission());
            using var realStore = original.Store;
            using var memory = real ? null : new InMemoryStateStore();
            IStateStore store = memory is null ? realStore : memory;
            if (memory is not null)
            {
                store.SavePair(original.Pair);
                SourceFixture.AcquireJournal(store, original.Pair);
            }
            var metadata = ListingChecks.File(original.Pair.DropboxRoot, "Source", id: "id:move");
            using var document = System.Text.Json.JsonSerializer.SerializeToDocument(metadata);
            var savedRemote = DropboxEntries.Parse(document.RootElement, original.Pair).Entry!;
            var saved = SyncedEntries.From(savedRemote with { Id = "local-id", ContentHash = "", Revision = "" }, savedRemote);
            var local = new Entry("Destination", false, savedRemote.Size, savedRemote.Modified, "local-id", "", "", null);
            ((FakeSide)original.Local).Seed(local, [1]);
            ((FakeSide)original.Local).FailNext("Destination", new ItemFailedException("Destination", cause, null));
            var move = new IssueSubject.UnknownMove(savedRemote.Id, "Source", "Destination", null, false);
            var subject = new IssueSubject([new(Side.Local, local.Path, local.Id),
                new(Side.Dropbox, move.Source, move.ExpectedId)], Side.Local) { Move = move };
            store.Commit(original.Pair.PairId, StateChange.Empty with
                { Record = [saved], Raise = [(IssueKind.Failure, TextKey.DropboxRefused, move.Source, subject)] });
            var issue = store.Issues(original.Pair.PairId).Single();
            using var fixture = new ListingChecks.Fixture(original.Pair, _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                    ListingChecks.File(original.Pair.DropboxRoot, "Destination", id: savedRemote.Id)))
            });
            var events = new List<RunEvent>();
            var remote = new DropboxSide(fixture.Api, original.Pair, original.Scope, original.Settings, fixture.Listing);
            var context = original with { Dropbox = remote.WithListingContext(ListingChecks.BeginListing),
                Store = store, Events = events.Add };
            var runner = Runner(context);
            var outcome = await runner.ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
            Check.Require(!runner.Resolved && outcome is { None: NoneReason.Held, Raised: IssueKind.Failure } &&
                store.Issues(original.Pair.PairId) is [var restored] && restored.Kind == IssueKind.Failure &&
                restored.Cause == TextKey.DropboxRefused && restored.Path == move.Source &&
                restored.Subject.Move == move && restored.Subject.HasSameItems(subject) &&
                store.SyncedById(original.Pair.PairId, Side.Dropbox, savedRemote.Id) == saved &&
                fixture.Handler.Requests.Count == 1 &&
                events.Count(item => item is { Kind: RunEventKind.Failed, Error: ItemFailedException }) == 1 &&
                events.All(item => item.Kind != RunEventKind.Effect),
                "A Local item error during settlement returns Held with the unknown move restored, not a raw exception; cause=" + cause);
        }
    }

    // 3.8: repeated Retries of an unproven move keep one unchanged Failure (no accumulation, no automatic wait: only
    // the user's Retry or a Repair reaches it again); once the identity is observed at its destination, Retry recovers.
    private static async Task UnknownMoveRepeatedRetryAsync()
    {
        foreach (var real in new[] { false, true })
        {
            var original = Context(new FakeAdmission());
            using var realStore = original.Store;
            using var memory = real ? null : new InMemoryStateStore();
            IStateStore store = memory is null ? realStore : memory;
            if (memory is not null)
            {
                store.SavePair(original.Pair);
                SourceFixture.AcquireJournal(store, original.Pair);
            }
            var metadata = ListingChecks.File(original.Pair.DropboxRoot, "Source", id: "id:move");
            using var document = System.Text.Json.JsonSerializer.SerializeToDocument(metadata);
            var savedRemote = DropboxEntries.Parse(document.RootElement, original.Pair).Entry!;
            var savedLocal = savedRemote with { Id = "local-id", ContentHash = "", Revision = "" };
            var saved = SyncedEntries.From(savedLocal, savedRemote);
            var local = savedLocal with { Path = "Destination" };
            ((FakeSide)original.Local).Seed(local, [1]);
            var move = new IssueSubject.UnknownMove(savedRemote.Id, "Source", "Destination", null, false);
            var subject = new IssueSubject([new(Side.Local, local.Path, local.Id),
                new(Side.Dropbox, move.Source, move.ExpectedId)], Side.Local) { Move = move };
            store.Commit(original.Pair.PairId, StateChange.Empty with
                { Record = [saved], Raise = [(IssueKind.Failure, TextKey.DropboxRefused, move.Source, subject)] });
            var issue = store.Issues(original.Pair.PairId).Single();
            const int unproven = 3;
            var replies = Enumerable.Repeat<Func<CancellationToken, HttpResponseMessage>>(_ => Missing(), unproven)
                .Concat([_ => Reply("Destination"), _ => Missing(), _ => Reply("Destination")]).ToArray();
            using var fixture = new ListingChecks.Fixture(original.Pair, replies);
            var events = new List<RunEvent>();
            var remote = new DropboxSide(fixture.Api, original.Pair, original.Scope, original.Settings, fixture.Listing);
            var probe = System.Reflection.DispatchProxy.Create<IStateStore, ChangeRunnerChecks.ForwardStore>();
            var counted = (ChangeRunnerChecks.ForwardStore)probe;
            counted.Inner = store;
            var context = original with { Dropbox = remote.WithListingContext(ListingChecks.BeginListing),
                Store = probe, Events = events.Add };
            for (var attempt = 1; attempt <= unproven; attempt++)
            {
                var runner = Runner(context);
                var outcome = await runner.ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
                Check.Require(!runner.Resolved && outcome is { None: NoneReason.Held, Raised: IssueKind.Failure } &&
                    store.Issues(original.Pair.PairId) is [var kept] && kept.Matches(issue) &&
                    counted.Count(nameof(IStateStore.Commit)) == 0 && fixture.Handler.Requests.Count == attempt &&
                    store.SyncedById(original.Pair.PairId, Side.Dropbox, savedRemote.Id) == saved && events.Count == 0,
                    "3.8 repeated failure: each unproven Retry costs one metadata read, commits nothing and keeps the same Failure; attempt=" + attempt);
            }
            var recovered = Runner(context);
            var final = await recovered.ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
            Check.Require(recovered.Resolved && final.Succeeded() && store.Issues(original.Pair.PairId).Count == 0 &&
                store.SyncedById(original.Pair.PairId, Side.Dropbox, savedRemote.Id) ==
                    SyncedEntries.Relink(saved, local, savedRemote with { Path = "Destination" }) &&
                fixture.Handler.Requests.Count == unproven + 3 &&
                fixture.Handler.Requests.All(request => request.Path.EndsWith(DropboxProtocol.GetMetadata, StringComparison.Ordinal)),
                "3.8 recovery: after repeated unproven Retries, an observed destination adopts with no move and clears the Failure.");

            HttpResponseMessage Reply(string path) => new(HttpStatusCode.OK) { Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(ListingChecks.File(original.Pair.DropboxRoot, path, id: savedRemote.Id))) };
            static HttpResponseMessage Missing() => new(HttpStatusCode.Conflict)
                { Content = new StringContent("{\"error_summary\":\"path/not_found/..\"}") };
        }
    }

    private static async Task UnknownMoveSuccessfulRetryAsync()
    {
        var failures = new List<Exception>();
        foreach (var real in new[] { false, true })
        foreach (var scenario in new[] { "adopt", "source-follow", "source-case", "remaining-hop", "observed-adopt" })
        {
            try { await CheckAsync(real, scenario); }
            catch (Exception error) { failures.Add(new InvalidOperationException(
                "Public unknown-move Retry real=" + real + " scenario=" + scenario, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);

        static async Task CheckAsync(bool real, string scenario)
        {
            var original = Context(new FakeAdmission());
            using var realStore = original.Store;
            using var memory = real ? null : new InMemoryStateStore();
            IStateStore store = memory is null ? realStore : memory;
            if (memory is not null)
            {
                store.SavePair(original.Pair);
                SourceFixture.AcquireJournal(store, original.Pair);
            }
            var destination = scenario is "source-case" or "remaining-hop" ? "SOURCE" : "Destination";
            var localPath = scenario == "observed-adopt" ? "Observed" : destination;
            var remotePath = scenario switch
            {
                "source-follow" or "source-case" => "Source",
                "remaining-hop" => destination + original.Settings.CaseRenameSuffix,
                _ => localPath
            };
            var metadata = ListingChecks.File(original.Pair.DropboxRoot, "Source", id: "id:move");
            using var document = System.Text.Json.JsonSerializer.SerializeToDocument(metadata);
            var savedRemote = DropboxEntries.Parse(document.RootElement, original.Pair).Entry!;
            var savedLocal = savedRemote with { Id = "local-id", ContentHash = "", Revision = "" };
            var saved = SyncedEntries.From(savedLocal, savedRemote);
            var local = savedLocal with { Path = localPath };
            ((FakeSide)original.Local).Seed(local, [1]);
            var localCalls = new CountingSide(original.Local);
            var move = new IssueSubject.UnknownMove(savedRemote.Id, "Source", destination,
                scenario == "remaining-hop" ? remotePath : null, false);
            var subject = new IssueSubject([new(Side.Local, local.Path, local.Id),
                new(Side.Dropbox, move.Source, move.ExpectedId)], Side.Local) { Move = move };
            store.Commit(original.Pair.PairId, StateChange.Empty with
            {
                Record = [saved],
                Raise = [(IssueKind.Failure, TextKey.DropboxRefused, move.Source, subject)]
            });
            var issue = store.Issues(original.Pair.PairId).Single();
            var replies = new List<Func<CancellationToken, HttpResponseMessage>> { _ => Reply(remotePath) };
            replies.Add(_ => remotePath == "Source" ? Reply("Source") : Missing());
            if (remotePath != "Source") replies.Add(_ => Reply(remotePath));
            if (scenario == "source-case") replies.Add(_ => Reply(destination + original.Settings.CaseRenameSuffix, moved: true));
            if (scenario is "source-follow" or "source-case" or "remaining-hop")
                replies.Add(_ => Reply(destination, moved: true));
            if (scenario == "remaining-hop") replies.Add(_ => Reply(destination));
            using var fixture = new ListingChecks.Fixture(original.Pair, replies.ToArray());
            var events = new List<RunEvent>();
            var remote = new DropboxSide(fixture.Api, original.Pair, original.Scope, original.Settings, fixture.Listing);
            var context = original with { Local = localCalls, Dropbox = remote.WithListingContext(ListingChecks.BeginListing),
                Store = store, Events = events.Add };
            var runner = Runner(context);
            var outcome = await runner.ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
            var finalPath = scenario == "observed-adopt" ? localPath : destination;
            var binding = store.SyncedById(context.Pair.PairId, Side.Dropbox, savedRemote.Id);
            Check.Require(runner.Resolved && outcome.Succeeded() && !store.Issues(context.Pair.PairId).Any() &&
                binding == SyncedEntries.Relink(saved, local, savedRemote with { Path = finalPath }) &&
                await original.Local.ReadOneAsync(finalPath, default) == local &&
                localCalls.Work == 0 && localCalls.Hashes == 0,
                "Public Retry resolves completed Adopt, Follow and case recovery without losing stamps or touching Local content.");
            var metadataReads = scenario == "remaining-hop" ? 4 : scenario is "source-follow" or "source-case" ? 2 : 3;
            var moves = scenario == "source-case" ? 2 : scenario is "source-follow" or "remaining-hop" ? 1 : 0;
            Check.Require(fixture.Handler.Requests.Count == metadataReads + moves &&
                fixture.Handler.Requests.Count(request => request.Path.EndsWith(DropboxProtocol.GetMetadata, StringComparison.Ordinal)) == metadataReads &&
                fixture.Handler.Requests.Count(request => request.Path.EndsWith(DropboxProtocol.Move, StringComparison.Ordinal)) == moves &&
                events.Count(item => item.Kind == RunEventKind.Effect) == (moves == 0 ? 0 : 1) &&
                events.All(item => item.Kind != RunEventKind.Failed),
                "Actual HTTP requests distinguish metadata, a two-RPC case move and the one-RPC remaining hop; adoption never resends a move.");
            using var first = System.Text.Json.JsonDocument.Parse(fixture.Handler.Requests[0].Body);
            Check.Require(first.RootElement.GetProperty("path").GetString() == move.ExpectedId,
                "Public Retry first observes the unknown identity, not the descriptor's stale source path.");
            var from = remotePath;
            foreach (var request in fixture.Handler.Requests.Where(request => request.Path.EndsWith(DropboxProtocol.Move, StringComparison.Ordinal)))
            {
                using var body = System.Text.Json.JsonDocument.Parse(request.Body);
                var target = scenario == "source-case" && from == "Source"
                    ? destination + original.Settings.CaseRenameSuffix : destination;
                Check.Require(body.RootElement.GetProperty("from_path").GetString() == context.Pair.DropboxRoot + "/" + from &&
                    body.RootElement.GetProperty("to_path").GetString() == context.Pair.DropboxRoot + "/" + target &&
                    !body.RootElement.GetProperty("autorename").GetBoolean(),
                    "Only the re-planned current path is moved, without overwriting or replaying the unknown request.");
                from = target;
            }

            HttpResponseMessage Reply(string path, bool moved = false)
            {
                var item = ListingChecks.File(original.Pair.DropboxRoot, path, id: savedRemote.Id);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    moved ? System.Text.Json.JsonSerializer.Serialize(new { metadata = item }) :
                        System.Text.Json.JsonSerializer.Serialize(item)) };
            }
            static HttpResponseMessage Missing() => new(HttpStatusCode.Conflict)
                { Content = new StringContent("{\"error_summary\":\"path/not_found/..\"}") };
        }
    }

    private static async Task FolderRetryAsync()
    {
        foreach (var history in new[] { "complete", "owed", "match" })
        foreach (var failed in new[] { false, true })
        {
            var original = Context();
            using var store = original.Store;
            var pair = original.Pair.PairId;
            var source = original.Dropbox;
            var target = original.Local;
            var parent = await source.EnsureFolderAsync("Parent", default);
            await source.EnsureFolderAsync("Parent/F1", default);
            await source.EnsureFolderAsync("Parent/F2", default);
            await PutAsync(source, "Parent/F2/Child", "child");
            await target.EnsureFolderAsync("Parent", default);
            var retained = await PutAsync(target, "Parent/LocalChild", "retained");
            var retainedHash = await target.ContentHashAsync(retained, default);
            store.Commit(pair, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, retained.Path,
                    new IssueSubject([new IssueItem(Side.Local, retained.Path, retained.Id)], null,
                        FirstRunDeletion: IssueSubject.FirstRunDeletionKind.Item))]
            });
            var blocked = failed;
            var context = original with
            {
                Store = new RepairedStore(store),
                ReadDeletionEvidenceAsync = (path, _, _) => Task.FromResult<DeletionEvidence>(
                    new DeletionEvidence.DeletedFile(path, "domain", "deleted-id", "revision",
                        DateTimeOffset.UtcNow.AddDays(-1), null, retainedHash, null)),
                Local = new ProxySide(target)
                {
                    EnsureFault = path => blocked && path == "Parent/F2"
                        ? new ItemFailedException(path, TextKey.FailChanged, null) : null
                }
            };
            if (history != "owed")
            {
                var token = store.BeginScan(pair, 0, true);
                var sink = store.OpenScan(pair, Side.Dropbox, ScanTestSettings.BatchRows);
                if (history == "match") await sink.AddAsync(new Listed(retained.Path, null), default);
                await sink.CompleteAsync();
                store.SaveDeletedHistory(pair, token, "history");
                await store.EndScanAsync(pair, token, context.Settings.StoragePageSize);
            }
            store.Commit(pair, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailChanged, "Parent",
                    new IssueSubject([new IssueItem(Side.Dropbox, "Parent", parent.Id)], null))]
            });
            var issue = store.Issues(pair).Single(value => value.Path == "Parent");
            var boundary = new TestSelectedBoundary(context);
            var ports = new SyncPorts(PassthroughRecoveryBoundary.Instance, RefusingExecutionBoundary.Instance, boundary, 0);
            ports = ports with
            {
                RunBatchAsync = (selection, side, scope, retries, settles, ct) =>
                    SyncJobs.RunBatchAsync(context, ports, selection, scope, ct, side, retries, settles)
            };
            var syncer = new Syncer(context, ports);
            await syncer.ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
            Check.Require(history == "owed"
                ? store.Issues(pair).Any(value => value.Path == retained.Path && value.Kind == IssueKind.Failure) &&
                    await source.ReadOneAsync(retained.Path, default) is null
                : history == "match" ? await target.ReadOneAsync(retained.Path, default) is null
                    : await source.ReadOneAsync(retained.Path, default) is not null,
                "actual Retry batch distinguishes history=" + history + " failed=" + failed + " issues=" + string.Join(",", store.Issues(pair).Select(i => i.Path + ":" + i.Cause)));
            if (failed)
            {
                Check.Require(await target.ReadOneAsync("Parent/F2/Child", default) is null &&
                    store.SyncedByKey(pair, Side.Dropbox, PathNames.DropboxKey("Parent/F2/Child")) is null,
                    "a failed F2 leaves its child uncopied and unacknowledged");
                var failure = store.Issues(pair).Single(value => value.Kind == IssueKind.Failure && value.Path == "Parent/F2");
                blocked = false;
                await syncer.ResolveAsync(failure, ResolutionChoice.Retry, null, null, default);
            }
            Check.Require(await target.ReadOneAsync("Parent/F2/Child", default) is not null &&
                store.SyncedByKey(pair, Side.Dropbox, PathNames.DropboxKey("Parent/F2/Child")) is not null &&
                !store.Issues(pair).Any(value => value.Kind == IssueKind.Failure && value.Path != retained.Path),
                "Retry after F2 succeeds copies and records its previously gated child and clears failure");
            Check.Require(store.SyncedByKey(pair, Side.Dropbox, PathNames.DropboxKey("Parent/F1")) is not null,
                "an independent successful sibling is retained");
        }
    }

    private static async Task ObservedFolderRetryDebtAsync()
    {
        var failures = new List<Exception>();
        foreach (var real in new[] { false, true })
        foreach (var (scenario, host) in new[] { ("IB-1", false), ("IB-2", false), ("IB-3", false), ("IU-1", false),
                     ("IU-3", false), ("IU-1", true), ("IU-D", false), ("IU-D", true) })
        {
            try { await CheckAsync(real, scenario, host); }
            catch (Exception error) { failures.Add(new InvalidOperationException(
                "Observed folder Retry real=" + real + " scenario=" + scenario + " host=" + host, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);

        static async Task CheckAsync(bool real, string scenario, bool host)
        {
            var original = Context(new FakeAdmission());
            using var originalStore = original.Store;
            // host: StoreItemAttempts has none of EngineHost's reservation, claim and completion fences, so the same
            // Retries also run through the real host item port, over that fixture's two stores.
            await using var rig = host ? await RepairChecks.ComposeRigAsync(
                Path.Combine(StorageRoot.Value!, Path.GetRandomFileName()), decorate: opened =>
                {
                    if (real) return new HookStore(opened);
                    opened.Dispose();
                    return new HookStore(new InMemoryStateStore());
                }) : null;
            var pair = rig?.Pair.PairId ?? original.Pair.PairId;
            using var database = real && rig is null ? StateStore.Open(Path.Combine(StorageRoot.Value!, Path.GetRandomFileName()),
                (attempt, actualPair, generation, _, _) => attempt != Guid.Empty && actualPair == pair &&
                    generation == original.ExpectedGeneration) : null;
            using var memory = real || rig is not null ? null : new InMemoryStateStore();
            IStateStore store = rig?.Store ?? (IStateStore?)database ?? memory!;
            // IU-D: Folder/Child already holds its own Local-only in-use episode when its ancestor Folder is retried.
            var descendant = scenario == "IU-D";
            Entry local, remote;
            string? childId = null;
            if (rig is null)
            {
                store.SavePair(original.Pair);
                SourceFixture.AcquireJournal(store, original.Pair);
                local = await original.Local.EnsureFolderAsync("Folder", default);
                remote = await original.Dropbox.EnsureFolderAsync("Folder", default);
                await PutAsync(original.Local, "Folder/a.txt", "readable child");
                await PutAsync(original.Dropbox, "Folder/a.txt", "readable child");
                if (descendant)
                {
                    childId = (await original.Local.EnsureFolderAsync("Folder/Child", default)).Id;
                    await original.Dropbox.EnsureFolderAsync("Folder/Child", default);
                }
            }
            else
            {
                // Seeded beneath the engine, as the host checks seed, so the check's own writes leave no engine state.
                var modified = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                var child = Path.Combine(rig.Pair.LocalRoot, "Folder", "a.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(child)!);
                File.WriteAllText(child, "readable child");
                File.SetLastWriteTimeUtc(child, modified.UtcDateTime);
                rig.Wire.SeedFolder("Folder");
                rig.Wire.SeedFile("Folder/a.txt", "readable child", modified);
                if (descendant)
                {
                    Directory.CreateDirectory(Path.Combine(rig.Pair.LocalRoot, "Folder", "Child"));
                    rig.Wire.SeedFolder("Folder/Child");
                }
                (local, remote, childId) = await rig.Host.DispatchSessionAsync(pair, () => null, async (session, ct) =>
                    ((await session.Context.Local.ReadOneAsync("Folder", ct))!,
                        (await session.Context.Dropbox.ReadOneAsync("Folder", ct))!,
                        descendant ? (await session.Context.Local.ReadOneAsync("Folder/Child", ct))!.Id : null), default);
            }
            store.Commit(pair, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.ReadDropboxFailed, local.Path,
                    new IssueSubject([new(Side.Local, local.Path, local.Id)], null,
                        FirstRunDeletion: IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice))]
            });
            var selected = store.Issues(pair).Single();
            Check.Require(store.SyncedById(pair, Side.Local, local.Id) is null &&
                store.SyncedById(pair, Side.Dropbox, remote.Id) is null,
                "The selected folder starts unbound on both sides with only Local first-run responsibility.");
            if (scenario == "IB-3")
            {
                local = local with { Id = "replacement-local-folder" };
                ((FakeSide)original.Local).Seed(local, null);
            }
            var blocked = !descendant;
            // IU-3: the folder is still in use at a second Retry, so its first episode must stay.
            var inUse = scenario is "IU-1" or "IU-3";
            var listingFailures = 0;
            // host: a really held folder fails the native Local listing itself, through the session's own inventory
            // reporter, rather than an exception injected after a completed listing.
            using var held = rig is not null && inUse ? LocalListingChecks.HoldDirectory(
                Path.Combine(rig.Pair.LocalRoot, "Folder")) : null;
            Exception? FailListing(string path, Side side)
            {
                if (rig is not null || !blocked || path != "Folder" || side != (inUse ? Side.Local : Side.Dropbox)) return null;
                listingFailures++;
                return new InventoryIncompleteException("Scoped listing incomplete", inUse
                    ? new ItemFailedException(path, TextKey.FailLocalInUse, null) : null);
            }
            var events = new List<RunEvent>();
            ExecutorContext Faulted(ExecutorContext sides) => sides with
            {
                Store = new RepairedStore(store), Events = events.Add,
                Local = new ProxySide(sides.Local)
                {
                    ListingFault = path => FailListing(path, Side.Local),
                    OnListing = rig is null ? null : () => { if (held is { IsClosed: false }) listingFailures++; }
                },
                Dropbox = new ProxySide(sides.Dropbox) { ListingFault = path => FailListing(path, Side.Dropbox) },
                ItemAttempts = rig is null ? new StoreItemAttempts(store, pair, sides.ExpectedGeneration) : sides.ItemAttempts
            };
            var context = Faulted(original);
            var syncer = RetrySyncer(context);
            // One host dispatch per step, as production runs each Retry: a reported subject stays consumed for its
            // dispatch's opportunity (EngineHost.ReserveItem).
            Task<T> InSessionAsync<T>(Func<Syncer, ExecutorContext, Task<T>> run) => rig is null ? run(syncer, context) :
                rig.Host.DispatchSessionAsync(pair, () => null, (session, _) =>
                {
                    var faulted = Faulted(session.Context);
                    return run(RetrySyncer(faulted), faulted);
                }, default);
            if (descendant)
            {
                var subject = new IssueSubject([new(Side.Local, "Folder/Child", childId!)], null);
                var epoch = store.ReadSource(pair, Side.Local)!.Value.Epoch;
                await InSessionAsync((_, sides) => sides.ItemAttempts.ExecuteAsync(sides.Opportunity, null, subject,
                    (attempt, ct) => Task.FromResult(sides.ItemAttempts.ReportFailure(attempt, epoch, "Folder/Child", subject,
                        TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null)), default));
            }
            var attempts = scenario is "IB-2" or "IU-3" ? 2 : descendant ? 0 : 1;
            string? episode = null;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var outcome = await InSessionAsync((retry, _) =>
                    retry.ResolveAsync(selected, ResolutionChoice.Retry, null, null, default));
                var issues = store.Issues(pair);
                selected = issues.Single(issue => issue.Kind == IssueKind.Failure && issue.Path == "Folder");
                Check.Require(outcome.Outcome.Raised == IssueKind.Failure && !outcome.Resolved && issues.Count == 1 &&
                    selected.Cause == (inUse ? TextKey.FailLocalInUse : TextKey.ReadDropboxFailed) &&
                    selected.Subject.Items is [{ Side: Side.Local, Path: "Folder" } item] && item.Id == local.Id &&
                    selected.Subject.FirstRunDeletion == (scenario == "IB-3"
                        ? IssueSubject.FirstRunDeletionKind.None : IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice),
                    "Incomplete Retry retains only the observed Local identity and its own debt, never early Info or replaced debt.");
                var bound = store.SyncedById(pair, Side.Local, local.Id);
                // A natively held folder cannot be opened at all, so its child is unlisted there, not absent.
                Check.Require(bound is not null && bound.LocalId == local.Id && bound.DropboxId == remote.Id &&
                    (store.SyncedByKey(pair, Side.Local, PathNames.Key("Folder/a.txt")) is null) == (held is not null) &&
                    listingFailures == attempt + 1,
                    "The public Retry records the observed root and readable child before the scoped inventory fails.");
                var retries = store.ReadItemRetryState(pair, [selected.Id]);
                Check.Require(inUse ? retries[selected.Id] is { } retry &&
                        retry.Token.Subject.HasSameItems(selected.Subject) &&
                        retry.Token.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice &&
                        events.Any(value => value.Cause == TextKey.FailLocalInUse && value.Path == "Folder") :
                    retries[selected.Id] is null,
                    "Only an observed in-use folder creates its exact retry episode and reports the in-use cause.");
                if (inUse) episode ??= retries[selected.Id]!.Token.EpisodeId;
                Check.Require(!inUse || retries[selected.Id]!.Token.EpisodeId == episode,
                    "A folder still in use keeps its first episode; an unacquired listing never ends it.");
            }
            blocked = false;
            held?.Dispose();
            if (descendant)
            {
                var child = store.Issues(pair).Single(issue => issue.Path == "Folder/Child");
                Check.Require(store.ReadItemRetryState(pair, [child.Id])[child.Id] is not null,
                    "The descendant's own in-use episode is registered before its ancestor's Retry.");
                if (rig is null) await PutAsync(original.Local, "Folder/Child/b.txt", "grandchild after recovery");
                else File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "Folder", "Child", "b.txt"), "grandchild after recovery");
                var copied = await InSessionAsync(async (retry, sides) =>
                {
                    await retry.ResolveAsync(selected, ResolutionChoice.Retry, null, null, default);
                    return await sides.Dropbox.ReadOneAsync("Folder/Child/b.txt", default) is not null;
                });
                Check.Require(store.ReadItemRetryState(pair, [child.Id]).GetValueOrDefault(child.Id) is null,
                    "An ancestor Retry settles the recovered descendant's own enrolled episode.");
                Check.Require(copied && store.SyncedByKey(pair, Side.Local, PathNames.Key("Folder/Child/b.txt")) is not null &&
                    !store.Issues(pair).Any(issue => issue.Cause == TextKey.FailChanged),
                    "The recovered descendant is not refused, so its new child syncs in the ancestor's Retry.");
            }
            if (inUse)
            {
                // A child that appears once the folder is free is synced by the same recovery Retry.
                if (rig is null) await PutAsync(original.Local, "Folder/b.txt", "child after recovery");
                else File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "Folder", "b.txt"), "child after recovery");
                var copied = await InSessionAsync(async (retry, sides) =>
                {
                    await retry.ResolveAsync(selected, ResolutionChoice.Retry, null, null, default);
                    return await sides.Dropbox.ReadOneAsync("Folder/b.txt", default) is not null;
                });
                Check.Require(store.ReadItemRetryState(pair, [selected.Id])[selected.Id] is null,
                    "A recovered in-use Retry settles the retained episode instead of accumulating pending work.");
                Check.Require(copied && store.SyncedByKey(pair, Side.Local, PathNames.Key("Folder/b.txt")) is not null &&
                    !store.Issues(pair).Any(issue => issue.Cause == TextKey.FailChanged),
                    "The recovered root is not refused, so its new child syncs in the same Retry.");
            }
            var (localId, remoteId) = await InSessionAsync(async (retry, sides) =>
            {
                var token = store.BeginScan(pair, sides.ExpectedGeneration, false);
                try
                {
                    foreach (var side in Enum.GetValues<Side>())
                    {
                        var sink = store.OpenScan(pair, side, sides.Settings.StoreBatchRows);
                        await foreach (var listed in sides.For(side).ReadAllAsync(string.Empty, new ListOptions(true, false), default))
                            await sink.AddAsync(listed, default);
                        await sink.CompleteAsync();
                    }
                    await retry.SyncScannedAsync(token, new ListedSkips([], [], [], []), null, default);
                }
                finally { await store.EndScanAsync(pair, token, sides.Settings.StoragePageSize); }
                return ((await sides.Local.ReadOneAsync("Folder", default))?.Id,
                    (await sides.Dropbox.ReadOneAsync("Folder", default))?.Id);
            });
            var final = store.Issues(pair);
            Check.Require(scenario == "IB-3" ? final.Count == 0 : final is
                [{ Kind: IssueKind.Info, Cause: TextKey.InfoDropboxHistoryUnchecked, Path: "Folder" }],
                "Complete Repair acknowledges the retained folder once; a replacement never inherits the old history notice.");
            Check.Require(store.RetainedFirstRun(pair).Count == 0 && listingFailures == attempts &&
                localId == local.Id && remoteId == remote.Id,
                "Recovery leaves both observed folders intact and no first-run backlog or repeated failing listing.");
        }
    }

    // B-148 CG7: an episode matches a later attempt subject only when that subject names every registered item; a
    // subject that merely overlaps it is refused, and without the opt-in the match stays exact (both stores).
    private static void RegisteredItemsWithin()
    {
        foreach (var real in new[] { false, true })
        {
            var original = Context(new FakeAdmission());
            using var originalStore = original.Store;
            var pair = original.Pair.PairId;
            var generation = original.ExpectedGeneration;
            using var database = real ? StateStore.Open(Path.Combine(StorageRoot.Value!, Path.GetRandomFileName()),
                (attempt, actualPair, actualGeneration, _, _) => attempt != Guid.Empty && actualPair == pair &&
                    actualGeneration == generation) : null;
            using var memory = real ? null : new InMemoryStateStore();
            IStateStore store = database is null ? memory! : database;
            store.SavePair(original.Pair);
            SourceFixture.AcquireJournal(store, original.Pair);
            var registered = new IssueSubject([new(Side.Local, "Folder", "local-1"), new(Side.Dropbox, "Folder", "remote-1")], null);
            store.RecordInUseFailure(pair, generation, store.ReadSource(pair, Side.Local)!.Value.Epoch, "Folder",
                registered, TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, Guid.NewGuid());
            var wider = new IssueSubject([.. registered.Items, new(Side.Dropbox, "Folder", "remote-2")], null);
            var overlapping = new IssueSubject([new(Side.Local, "Folder", "local-1"), new(Side.Dropbox, "Folder", "remote-2")], null);
            Check.Require(store.ReadItemRetryForSubject(pair, generation, "Folder", wider, registeredWithin: true)
                    ?.Token.Subject.HasSameItems(registered) == true,
                "real=" + real + ": an attempt naming every registered item matches that episode");
            foreach (var (subject, within) in new[] { (overlapping, true), (overlapping, false), (wider, false) })
                Check.Require(Refused(() => store.ReadItemRetryForSubject(pair, generation, "Folder", subject, within)),
                    "real=" + real + " within=" + within + ": an overlapping subject, or any unequal one by default, is refused");
        }

        static bool Refused(Action read)
        {
            try { read(); return false; }
            catch (LiveAdmissionStaleException) { return true; }
        }
    }

    // A public Retry whose batches run, as Repair wires them.
    private static Syncer RetrySyncer(ExecutorContext context)
    {
        var ports = new SyncPorts(PassthroughRecoveryBoundary.Instance, RefusingExecutionBoundary.Instance,
            new TestSelectedBoundary(context), 0);
        ports = ports with
        {
            RunBatchAsync = (selection, side, scope, retries, settles, ct) =>
                SyncJobs.RunBatchAsync(context, ports, selection, scope, ct, side, retries, settles)
        };
        return new Syncer(context, ports);
    }

    // B1: a scoped failure that names a child never takes the root's observed Local identity (child-path mismatch),
    // and a Dropbox-only observed folder, which has no Local entry, fails as an ordinary Failure (null guard).
    private static async Task ObservedFolderFailurePathAsync()
    {
        foreach (var real in new[] { false, true })
        foreach (var childPath in new[] { true, false })
        {
            var original = Context(new FakeAdmission());
            using var originalStore = original.Store;
            var pair = original.Pair.PairId;
            using var database = real ? StateStore.Open(Path.Combine(StorageRoot.Value!, Path.GetRandomFileName()),
                (attempt, actualPair, generation, _, _) => attempt != Guid.Empty && actualPair == pair &&
                    generation == original.ExpectedGeneration) : null;
            using var memory = real ? null : new InMemoryStateStore();
            IStateStore store = database is null ? memory! : database;
            store.SavePair(original.Pair);
            SourceFixture.AcquireJournal(store, original.Pair);
            var remote = await original.Dropbox.EnsureFolderAsync("Folder", default);
            await PutAsync(original.Dropbox, "Folder/a.txt", "readable child");
            Entry? local = null;
            if (childPath)
            {
                local = await original.Local.EnsureFolderAsync("Folder", default);
                await PutAsync(original.Local, "Folder/a.txt", "readable child");
            }
            var item = local is null ? new IssueItem(Side.Dropbox, remote.Path, remote.Id)
                : new IssueItem(Side.Local, local.Path, local.Id);
            store.Commit(pair, StateChange.Empty with
                { Raise = [(IssueKind.Failure, TextKey.ReadDropboxFailed, "Folder", new IssueSubject([item], null))] });
            var selected = store.Issues(pair).Single();
            var failingSide = childPath ? Side.Local : Side.Dropbox;
            var failedPath = childPath ? "Folder/a.txt" : "Folder";
            var cause = childPath ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed;
            var listingFailures = 0;
            Exception? FailListing(string path, Side side)
            {
                if (path != "Folder" || side != failingSide) return null;
                listingFailures++;
                return new InventoryIncompleteException("Scoped listing incomplete",
                    new ItemFailedException(failedPath, cause, null));
            }
            var context = original with
            {
                Store = new RepairedStore(store),
                Local = new ProxySide(original.Local) { ListingFault = path => FailListing(path, Side.Local) },
                Dropbox = new ProxySide(original.Dropbox) { ListingFault = path => FailListing(path, Side.Dropbox) },
                ItemAttempts = new StoreItemAttempts(store, pair, original.ExpectedGeneration)
            };
            var outcome = await RetrySyncer(context).ResolveAsync(selected, ResolutionChoice.Retry, null, null, default);
            var failures = store.Issues(pair).Where(issue => issue.Kind == IssueKind.Failure && issue.Cause == cause).ToList();
            Check.Require(listingFailures >= 1 && outcome.Outcome.Raised == IssueKind.Failure &&
                failures.Any(failure => failure.Path == failedPath && failure.Subject.Items.Count == 0),
                "B1: the scoped failure is stored at the path it names with no borrowed identity, and nothing escapes.");
            if (childPath)
                Check.Require(!failures.Any(failure => failure.Path == "Folder" ||
                        failure.Subject.Items.Any(value => value.Side == Side.Local && value.Id == local!.Id)),
                    "B1: a child's failure never re-raises the root or carries the root's observed Local identity.");
        }
    }

    // U3c, repeated (3.8): a folder Failure's Retry batch takes every open Failure under the folder as retried, but an
    // unknown move under it stays held on every Retry: no Dropbox write, the same unknown Failure and its old binding.
    private static async Task UnknownMoveUnderFolderRetryAsync()
    {
        foreach (var real in new[] { false, true })
        {
            var original = Context(new FakeAdmission());
            using var originalStore = original.Store;
            var pair = original.Pair.PairId;
            using var database = real ? StateStore.Open(Path.Combine(StorageRoot.Value!, Path.GetRandomFileName()),
                (attempt, actualPair, generation, _, _) => attempt != Guid.Empty && actualPair == pair &&
                    generation == original.ExpectedGeneration) : null;
            using var memory = real ? null : new InMemoryStateStore();
            IStateStore store = database is null ? memory! : database;
            store.SavePair(original.Pair);
            SourceFixture.AcquireJournal(store, original.Pair);
            var localFolder = await original.Local.EnsureFolderAsync("Folder", default);
            await original.Dropbox.EnsureFolderAsync("Folder", default);
            var localOld = await PutAsync(original.Local, "Folder/Old", "moved");
            var remote = await PutAsync(original.Dropbox, "Folder/Old", "moved");
            store.Commit(pair, StateChange.Empty with { Record = [SyncedEntries.From(localOld, remote)] });
            var binding = store.SyncedById(pair, Side.Dropbox, remote.Id);
            var local = await original.Local.MoveAsync(localOld, "Folder/New", default);
            var move = new IssueSubject.UnknownMove(remote.Id, "Folder/Old", local.Path, null, false);
            store.Commit(pair, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.DropboxRefused, local.Path, new IssueSubject(
                    [new(Side.Local, local.Path, local.Id), new(Side.Dropbox, remote.Path, remote.Id)], Side.Dropbox)
                    { Move = move })]
            });
            var unknown = store.Issues(pair).Single();
            store.Commit(pair, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailChanged, "Folder",
                    new IssueSubject([new IssueItem(Side.Local, "Folder", localFolder.Id)], null))]
            });
            var dropbox = new CountingSide(original.Dropbox);
            var context = original with
            {
                Store = new RepairedStore(store), Dropbox = dropbox,
                ItemAttempts = new StoreItemAttempts(store, pair, original.ExpectedGeneration)
            };
            var syncer = RetrySyncer(context);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var folder = store.Issues(pair).FirstOrDefault(issue => issue.Path == "Folder" && issue.Kind == IssueKind.Failure);
                if (folder is not null) await syncer.ResolveAsync(folder, ResolutionChoice.Retry, null, null, default);
                var still = store.IssueById(pair, unknown.Id);
                Check.Require(dropbox.Stages + dropbox.Publishes + dropbox.Ensures + dropbox.Deletes + dropbox.Moves == 0 &&
                    await original.Dropbox.ReadOneAsync("Folder/New", default) is null &&
                    await original.Dropbox.ReadOneAsync("Folder/Old", default) is not null &&
                    still is not null && still.Subject.Move == move && still.Cause == TextKey.DropboxRefused &&
                    still.Subject.HasSameItems(unknown.Subject) &&
                    store.SyncedById(pair, Side.Dropbox, remote.Id) == binding &&
                    store.Issues(pair).Count(issue => issue.Subject.Move is not null) == 1,
                    "U3c: Retry " + attempt + " of the folder plans nothing under the held unknown move and adds no second" +
                    " responsibility; real=" + real);
            }
        }
    }

    private static async Task CorruptHistoryRetryAsync(bool matched)
    {
        var paths = new[] { "Parent/A", "Parent/B", "Parent/C" };
        var original = Context(new FakeAdmission { Level = paths.Length });
        using var store = (StateStore)original.Store;
        var pair = original.Pair.PairId;
        var parent = await original.Dropbox.EnsureFolderAsync("Parent", default);
        await original.Local.EnsureFolderAsync("Parent", default);
        var entries = new List<Entry>();
        foreach (var path in paths)
        {
            var entry = await PutAsync(original.Local, path, "retained");
            entries.Add(entry);
            store.Commit(pair, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, path,
                    new IssueSubject([new IssueItem(Side.Local, path, entry.Id)], null,
                        FirstRunDeletion: IssueSubject.FirstRunDeletionKind.Item))]
            });
        }
        var hash = await original.Local.ContentHashAsync(entries[0], default);
        async Task SaveHistoryAsync(bool includeMatches)
        {
            var token = store.BeginScan(pair, 0, true);
            var sink = store.OpenScan(pair, Side.Dropbox, ScanTestSettings.BatchRows);
            if (includeMatches) await sink.AddAsync(new Listed(paths[0], null), default);
            await sink.AddAsync(new Listed(paths[1], null), default);
            await sink.CompleteAsync();
            store.SaveDeletedHistory(pair, token, "history");
            await store.EndScanAsync(pair, token, original.Settings.StoragePageSize);
        }
        await SaveHistoryAsync(matched);
        var prepared = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var corrupt = new CorruptRetryStore(store, paths[1], () =>
        {
            Check.Require(prepared, "The earlier Retry action reached freshness before corruption.");
            Massdex.SyncShark.Engine.Tests.Storage.DeletedHistoryChecks.CorruptSavedHistory(store);
        }, () => release.TrySetResult());
        var context = original with
        {
            Store = corrupt,
            ReadDeletionEvidenceAsync = (path, _, _) => Task.FromResult<DeletionEvidence>(
                new DeletionEvidence.DeletedFile(path, "domain", "deleted-id", "revision",
                    DateTimeOffset.UtcNow.AddDays(-1), null, hash, null)),
            Local = new ProxySide(original.Local)
            {
                BeforeRead = async (path, ct) =>
                {
                    if (path != paths[0] || prepared) return;
                    prepared = true;
                    await release.Task.WaitAsync(ct);
                }
            }
        };
        var boundary = new TestSelectedBoundary(context);
        var ports = new SyncPorts(PassthroughRecoveryBoundary.Instance, RefusingExecutionBoundary.Instance, boundary, 0);
        ports = ports with
        {
            RunBatchAsync = (selection, side, scope, retries, settles, ct) =>
                SyncJobs.RunBatchAsync(context, ports, selection, scope, ct, side, retries, settles)
        };
        var syncer = new Syncer(context, ports);
        async Task RetryAsync()
        {
            store.Commit(pair, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailChanged, "Parent",
                    new IssueSubject([new IssueItem(Side.Dropbox, "Parent", parent.Id)], null))]
            });
            var issue = store.Issues(pair).Single(value => value.Path == "Parent");
            await syncer.ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
        }
        await RetryAsync();
        Check.Require(corrupt.Injected && store.DeletedHistoryOwed(pair) &&
            store.Issues(pair).Any(value => value.Cause == TextKey.DeletedHistoryCorrupt),
            "Retry discovers corrupt saved history and retains its reacquisition diagnostic.");
        // A second Retry without acquisition must preserve the same obligations, not turn absence into authority.
        await RetryAsync();
        foreach (var entry in entries)
            Check.Require(await original.Local.ReadOneAsync(entry.Path, default) == entry &&
                await original.Dropbox.ReadOneAsync(entry.Path, default) is null &&
                store.Issues(pair).Any(value => value.Path == entry.Path && value.Kind == IssueKind.Failure),
                "Corruption holds prepared, detecting and later Retry rows without upload or deletion: " + entry.Path);
        await SaveHistoryAsync(matched);
        await RetryAsync();
        foreach (var entry in entries)
        {
            var deleted = entry.Path == paths[1] || matched && entry.Path == paths[0];
            Check.Require(deleted ? await original.Local.ReadOneAsync(entry.Path, default) is null
                : await original.Dropbox.ReadOneAsync(entry.Path, default) is not null,
                "Reacquired history settles each retained Retry obligation: " + entry.Path);
            Check.Require(!store.Issues(pair).Any(value => value.Path == entry.Path && value.Kind == IssueKind.Failure),
                "Successful history-backed Retry clears the retained failure.");
        }
    }

    private sealed class CorruptRetryStore(IStateStore inner, string detectingPath, Action inject, Action release)
        : ForwardingStateStore(inner)
    {
        public bool Injected { get; private set; }
        public override bool FirstRepairDone(string pairId) => true;
        public override bool SavedDeletedNameExact(string pairId, string key)
        {
            if (!Injected && key == PathNames.DropboxKey(detectingPath))
            {
                Injected = true;
                inject();
            }
            return base.SavedDeletedNameExact(pairId, key);
        }
        public override IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context) =>
            base.ScanRows(pairId, scanId, row =>
            {
                try { return context(row); }
                finally { if (row.Key == PathNames.Key(detectingPath)) release(); }
            });
    }

    private sealed class RepairedStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        public override bool FirstRepairDone(string pairId) => true;
    }

    private static async Task TieAsync(TextKey cause, ResolutionChoice choice)
    {
        var context = Context();
        using var store = context.Store;
        var local = cause == TextKey.TieKindBoth
            ? await context.Local.EnsureFolderAsync("Item", CancellationToken.None)
            : await PutAsync(context.Local, "Item", "local bytes");
        var dropbox = await PutAsync(context.Dropbox,
            cause == TextKey.TieCaseBoth ? "item" : "Item", "remote bytes");
        var issue = Raise(context, IssueKind.Tie, cause, local, dropbox, null);
        var outcome = await Runner(context).ResolveAsync(issue, choice, null, null, CancellationToken.None);
        if (choice == ResolutionChoice.Dismiss)
        {
            Check.Require(store.IssueById(context.Pair.PairId, issue.Id)?.Dismissed == true,
                "Dismiss retains the tie with its dismissed flag.");
            Check.Require(await context.Local.ReadOneAsync(local.Path, CancellationToken.None) == local &&
                await context.Dropbox.ReadOneAsync(dropbox.Path, CancellationToken.None) == dropbox,
                "Dismiss changes neither subject item.");
            return;
        }
        Check.Require(outcome.Raised is not (IssueKind.Failure or IssueKind.NameClash) && !Open(context, issue),
            "Each supported tie resolution closes on success.");
        if (choice == ResolutionChoice.KeepBoth)
        {
            Check.Require(await context.Local.ReadOneAsync(local.Path, CancellationToken.None) == local &&
                await context.Dropbox.ReadOneAsync(dropbox.Path, CancellationToken.None) == dropbox,
                "Keep-both preserves both originals.");
            Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path))?.DropboxPath != dropbox.Path,
                "Keep-both links the local original to its labelled copy.");
            return;
        }
        var winner = choice == ResolutionChoice.UseLocal ? local : dropbox;
        var loser = choice == ResolutionChoice.UseLocal ? context.Dropbox : context.Local;
        var result = await loser.ReadOneAsync(winner.Path, CancellationToken.None);
        Check.Require(result is not null && result.IsFolder == winner.IsFolder,
            "The selected side determines the resulting item kind.");
        if (cause == TextKey.TieCaseBoth)
            Check.Require(result!.Path == winner.Path, "Case resolution adopts the selected spelling.");
        else if (!winner.IsFolder)
            Check.Require(await loser.ContentHashAsync(result!, CancellationToken.None) ==
                await context.For(choice == ResolutionChoice.UseLocal ? Side.Local : Side.Dropbox)
                    .ContentHashAsync(winner, CancellationToken.None), "Content resolution copies the selected bytes.");
    }

    private static async Task DismissAsync(IssueKind kind, bool unavailable)
    {
        var context = Context(new FakeAdmission { Level = 1 });
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item", "local");
        var dropbox = await PutAsync(context.Dropbox, "item", "remote");
        var cause = kind == IssueKind.Info ? TextKey.InfoKeptBoth :
            kind == IssueKind.Skipped ? TextKey.SkipLink :
            kind == IssueKind.Failure ? TextKey.PublishFailed :
            kind == IssueKind.Tie ? TextKey.TieEqualTimes : TextKey.ClashNameTaken;
        var issue = Raise(context, kind, cause, local, dropbox, null);
        await context.Local.DeleteAsync(local, null, CancellationToken.None);
        await PutAsync(context.Local, local.Path, "replacement");
        await context.Dropbox.DeleteAsync(dropbox, null, CancellationToken.None);
        if (unavailable)
        {
            ((FakeSide)context.Local).FailNext(local.Path, new InvalidOperationException("Dismiss must not read Local."));
            ((FakeSide)context.Dropbox).FailNext(dropbox.Path, new InvalidOperationException("Dismiss must not read Dropbox."));
        }
        SyncOutcome? outcome = null;
        TextKey? refusal = null;
        var events = new List<RunEvent>();
        context = context with { Events = events.Add };
        try
        {
            outcome = await Runner(context).ResolveAsync(issue, ResolutionChoice.Dismiss, null, null, CancellationToken.None);
        }
        catch (EngineRequestException error)
        {
            refusal = error.Key;
        }
        if (kind == IssueKind.Failure)
        {
            // The stored row is exactly unchanged: same identity, cause, path, time and subject items, never dismissed.
            var retained = store.IssueById(context.Pair.PairId, issue.Id);
            Check.Require(refusal == TextKey.ResolveBadChoice && outcome is null &&
                retained is not null && retained with { Subject = issue.Subject } == issue &&
                retained.Subject.HasSameItems(issue.Subject) && events.Count == 0,
                "Failure Dismiss refuses before observation, dismissal, closure or any effect, even with a stale subject.");
        }
        else
        {
            // Dismissing an Info notice deletes it; every other kind keeps the row with its dismissed flag.
            var dismissed = store.IssueById(context.Pair.PairId, issue.Id);
            Check.Require(refusal is null && outcome is { Raised: null } &&
                (kind == IssueKind.Info ? dismissed is null : dismissed?.Dismissed == true),
                "Dismiss works with changed and missing identities, even when the providers cannot be read.");
        }
        Check.Require(((FakeAdmission)context.Admission).Admitted == 0,
            "Dismiss performs no provider action.");
    }

    private static async Task RenameAsync(Side side, bool incoming, bool occupied, bool freedFolder = false)
    {
        var context = Context();
        using var store = context.Store;
        var local = freedFolder && side == Side.Dropbox
            ? await context.Local.EnsureFolderAsync("item", CancellationToken.None)
            : await PutAsync(context.Local, "item", "local");
        var dropbox = freedFolder && side == Side.Local
            ? await context.Dropbox.EnsureFolderAsync("item", CancellationToken.None)
            : await PutAsync(context.Dropbox, "item", "remote");
        var otherSide = PathNames.Other(side);
        if (freedFolder)
        {
            await PutAsync(context.For(otherSide), "item/a", "first");
            await context.For(otherSide).EnsureFolderAsync("item/nested", CancellationToken.None);
            await PutAsync(context.For(otherSide), "item/nested/b", "second");
        }
        if (occupied) await PutAsync(context.For(side), "renamed", "occupant");
        var issue = Raise(context, IssueKind.NameClash,
            freedFolder ? TextKey.ClashFileFolder : TextKey.ClashNameTaken, local, dropbox, incoming ? side : null);
        var choice = incoming ? ResolutionChoice.RenameIncoming :
            side == Side.Local ? ResolutionChoice.RenameLocal : ResolutionChoice.RenameDropbox;
        var visits = new List<(Side Side, string Path)>();
        var localProbe = new RecordingSide(context.Local, () => !Open(context, issue), visits);
        var dropboxProbe = new RecordingSide(context.Dropbox, () => !Open(context, issue), visits);
        context = context with { Local = localProbe, Dropbox = dropboxProbe };
        var outcome = await Runner(context).ResolveAsync(issue, choice, "renamed", null, CancellationToken.None);
        List<(Side, string)> expected = occupied ? [] : [(side, "renamed"), (otherSide, "item")];
        if (freedFolder && !occupied)
            expected.AddRange(new[] { (otherSide, "item/a"), (otherSide, "item/nested"), (otherSide, "item/nested/b") });
        Check.Require(Visited(visits, [.. expected]),
            "A successful rename follows its destination, then the freed name and all its opposite-side folder descendants. visits=" +
            string.Join("|", visits.Select(visit => visit.Side + ":" + visit.Path)));
        var moved = (side == Side.Local ? localProbe : dropboxProbe).Moves;
        Check.Require(occupied
                ? moved.Count == 0
                : moved.Count == 1 && moved[0] == ("item", "renamed"),
            "The unlinked rename is one provider move of the selected identity, or none when the destination is taken. side=" +
            side + " incoming=" + incoming + " occupied=" + occupied + " freedFolder=" + freedFolder + " selectedMoves=" +
            string.Join("|", moved.Select(move => move.From + "->" + move.To)) + " localMoves=" +
            string.Join("|", localProbe.Moves.Select(move => move.From + "->" + move.To)) + " dropboxMoves=" +
            string.Join("|", dropboxProbe.Moves.Select(move => move.From + "->" + move.To)) + " raised=" + outcome.Raised);
        if (occupied)
        {
            Check.Require(outcome.Raised == IssueKind.NameClash && Open(context, issue),
                "A refused rename keeps the original resolution actionable.");
            return;
        }
        Check.Require(!Open(context, issue) &&
            (await context.For(side).ReadOneAsync("renamed", CancellationToken.None))?.Id ==
                (side == Side.Local ? local.Id : dropbox.Id), "Rename moves the selected identity and closes the issue.");
    }

    // B-122 X4/X5 (DESIGN-B122-4 §4): an incoming clash's choices. With the occupant present, rename and replace are
    // RenameAsync and ReplaceFileAsync; Dismiss keeps the hold (engine.md:360). After the occupant is deleted, rename
    // and replace refuse as stale (Resolutions.cs:174-183) and Dismiss still holds.
    private static async Task IncomingClashChoicesAsync()
    {
        foreach (var deleted in new[] { false, true })
        foreach (var choice in deleted ? new[] { ResolutionChoice.RenameIncoming, ResolutionChoice.ReplaceExisting,
                     ResolutionChoice.Dismiss } : new[] { ResolutionChoice.Dismiss })
        {
            var context = Context();
            using var store = context.Store;
            var local = await PutAsync(context.Local, "item", "incoming");
            var dropbox = await PutAsync(context.Dropbox, "item", "occupant");
            var issue = Raise(context, IssueKind.NameClash, TextKey.ClashNameTaken, local, dropbox, Side.Local);
            if (deleted) await context.Dropbox.DeleteAsync(dropbox, null, CancellationToken.None);
            TextKey? refusal = null;
            try
            {
                await Runner(context).ResolveAsync(issue, choice, choice == ResolutionChoice.RenameIncoming ? "renamed" : null,
                    null, CancellationToken.None);
            }
            catch (EngineRequestException error)
            {
                refusal = error.Key;
            }
            var kept = store.IssueById(context.Pair.PairId, issue.Id);
            var label = $"X{(deleted ? 5 : 4)} {choice}: ";
            Check.Require(await context.Local.ReadOneAsync("item", CancellationToken.None) == local &&
                await context.Local.ReadOneAsync("renamed", CancellationToken.None) is null &&
                (deleted ? await context.Dropbox.ReadOneAsync("item", CancellationToken.None) is null :
                    await context.Dropbox.ReadOneAsync("item", CancellationToken.None) == dropbox),
                label + "the incoming item and the occupant are untouched");
            if (choice == ResolutionChoice.Dismiss)
                Check.Require(refusal is null && kept?.Dismissed == true &&
                    store.HeldCovers(context.Pair.PairId, PathNames.Key("item"), null),
                    label + "Dismiss keeps the clash, dismissed, and it still holds the name");
            else
                Check.Require(refusal == TextKey.ResolveSubjectStale && Open(context, issue),
                    label + "refuses as stale and leaves the clash open; refusal=" + refusal);
        }
    }

    private static async Task ReplaceFileAsync(Side side, bool incoming, bool failCopy, bool occupantFolder)
    {
        var context = Context();
        using var store = context.Store;
        var local = occupantFolder && side == Side.Dropbox
            ? await context.Local.EnsureFolderAsync("item", CancellationToken.None)
            : await PutAsync(context.Local, "item", "local");
        var dropbox = occupantFolder && side == Side.Local
            ? await context.Dropbox.EnsureFolderAsync("item", CancellationToken.None)
            : await PutAsync(context.Dropbox, "item", "remote");
        var issue = Raise(context, IssueKind.NameClash,
            occupantFolder ? TextKey.ClashFileFolder : TextKey.ClashNameTaken, local, dropbox, incoming ? side : null);
        var source = context.For(side);
        var destination = (FakeSide)context.For(side == Side.Local ? Side.Dropbox : Side.Local);
        if (occupantFolder) await PutAsync(destination, "item/old-child", "old child");
        var otherIssue = occupantFolder && side == Side.Local ? null :
            occupantFolder ? Raise(context, IssueKind.Tie, TextKey.TieEqualTimes,
                await PutAsync(context.Local, "other", "local"), await PutAsync(context.Dropbox, "other", "remote"), null) :
                Raise(context, IssueKind.Tie, TextKey.TieEqualTimes, local, dropbox, null);
        var effects = new List<string>();
        var failures = new List<RunEvent>();
        context = context with
        {
            Events = entry =>
            {
                if (entry.Kind == RunEventKind.Issue) failures.Add(entry);
                if (entry.Kind != RunEventKind.Effect) return;
                effects.Add(Massdex.SyncShark.Engine.Run.RunLog.Present(entry).What);
                Check.Require(Open(context, issue), "Every action commits while the selected issue is still open.");
            }
        };
        var occupant = side == Side.Local ? dropbox : local;
        var replacingSide = new ReplacingSide(destination, occupant, failCopy, occupantFolder);
        context = side == Side.Local ? context with { Dropbox = replacingSide } : context with { Local = replacingSide };
        var choice = incoming ? ResolutionChoice.ReplaceExisting :
            side == Side.Local ? ResolutionChoice.KeepLocal : ResolutionChoice.KeepDropbox;
        var runner = Runner(context);
        var outcome = await runner.ResolveAsync(issue, choice, null, null, CancellationToken.None);
        var written = await destination.ReadOneAsync("item", CancellationToken.None);
        var selectedOpen = Open(context, issue);
        var removalStates = string.Join("|", store.ActiveFolderRemovals(context.Pair.PairId, CancellationToken.None)
            .Select(operation => operation.OperationId + ":" + operation.Phase + ":" + operation.Cause));
        var issueStates = string.Join("|", store.Issues(context.Pair.PairId)
            .Select(item => item.Id + ":" + item.Kind + ":" + item.Path + ":" + item.Cause));
        var currentLocal = await context.Local.ReadOneAsync("item", CancellationToken.None);
        var currentDropbox = await context.Dropbox.ReadOneAsync("item", CancellationToken.None);
        Check.Require(selectedOpen == failCopy && (otherIssue is null || Open(context, otherIssue)),
            "Resolution closes only its selected issue, and only after all actions succeed. selectedOpen=" +
            selectedOpen + " failCopy=" + failCopy + " side=" + side + " incoming=" + incoming +
            " occupantFolder=" + occupantFolder + " resolved=" + runner.Resolved + " raised=" + outcome.Raised +
            " none=" + outcome.None + " deferred=" + outcome.Deferred + " submitted=" + replacingSide.Submitted.Count +
            " deleted=" + replacingSide.Deleted.Count + " removals=" + removalStates + " issues=" + issueStates +
            " local=" + (currentLocal is null ? "absent" : currentLocal.IsFolder ? "folder" : "file") +
            " dropbox=" + (currentDropbox is null ? "absent" : currentDropbox.IsFolder ? "folder" : "file"));
        // A kind change removes the exact occupant through a real provider route: a Dropbox folder receipt or the
        // side's delete; a Local occupant stays until the stage validates, so a refused stage deletes nothing.
        var removedByProvider = occupantFolder && side == Side.Local &&
            (replacingSide.Submitted.Any(effects => effects.Any(effect => effect.Expected == occupant)) ||
             replacingSide.Deleted.Contains(occupant));
        var deleted = occupantFolder && side == Side.Dropbox && !failCopy
            ? new[] { occupant } : Array.Empty<Entry>();
        Check.Require(replacingSide.Deleted.SequenceEqual(deleted) &&
            (occupantFolder && side == Side.Local
                ? removedByProvider && replacingSide.Deleted.All(entry => entry == occupant)
                : !removedByProvider),
            "Only a kind change removes the exact occupant through the side's delete or a folder receipt, " +
            "and a local stage refusal preserves its occupant.");
        if (occupantFolder)
            Check.Require((await destination.ReadOneAsync("item/old-child", CancellationToken.None) is null) ==
                (deleted.Length != 0 || removedByProvider),
                "Replacing a folder removes its whole old subtree exactly when the occupant is removed.");
        if (failCopy)
        {
            var expectedWritten = occupantFolder && side == Side.Local ? null : occupant;
            // A Dropbox folder occupant is gone through the caller's folder phase before the copy fails.
            var expectedEffects = occupantFolder && side == Side.Local
                ? new[] { EffectKind.DeletedDropbox.ToString() }
                : Array.Empty<string>();
            Check.Require(outcome.Raised == IssueKind.Failure && written == expectedWritten &&
                effects.SequenceEqual(expectedEffects),
                "A failed copy keeps the issue open; only a kind change has already removed its occupant.");
            Check.Require(await source.ReadOneAsync("item", CancellationToken.None) == (side == Side.Local ? local : dropbox),
                "A failed replacement preserves the incoming file.");
            Check.Require(ReferenceEquals(failures.Single().Error, replacingSide.Failure),
                "A failed resolution retains original provider evidence for RunLog, including DropboxRefused.");
        }
        else
        {
            var expectedEffects = occupantFolder && side == Side.Local
                ? new[] { EffectKind.DeletedDropbox.ToString(), EffectKind.Uploaded.ToString() }
                : new[] { (occupantFolder ? EffectKind.Replaced :
                    side == Side.Local ? EffectKind.Uploaded : EffectKind.Downloaded).ToString() };
            Check.Require(written is not null && effects.SequenceEqual(expectedEffects) && outcome.Raised is null,
                "Kind changes delete then create; same-kind files still use one replacing transfer.");
            Check.Require(await destination.ContentHashAsync(written!, CancellationToken.None) ==
                await source.ContentHashAsync(side == Side.Local ? local : dropbox, CancellationToken.None),
                "The final file contains the selected source bytes.");
        }
    }

    // An unrelated Tie notice holds the row for ordinary work: the path is observed but never copied over.
    private static async Task TieHoldsOrdinaryWorkAsync(Side side)
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item", "local");
        var dropbox = await PutAsync(context.Dropbox, "item", "remote");
        Raise(context, IssueKind.Tie, TextKey.TieEqualTimes, local, dropbox, null);
        var jobs = SyncJobs.Create(context, new SyncPorts(PassthroughRecoveryBoundary.Instance,
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        await jobs.Items.SyncPathAsync(side, "item", CancellationToken.None);
        Check.Require(await context.Local.ReadOneAsync("item", CancellationToken.None) == local &&
            await context.Dropbox.ReadOneAsync("item", CancellationToken.None) == dropbox &&
            store.Issues(context.Pair.PairId).Any(issue => issue.Kind == IssueKind.Tie && !issue.Dismissed),
            "An unrelated Tie notice holds ordinary work without replacing either item.");
    }

    // A kind tie's selected side replaces a Dropbox folder through the selected owner: the recorded binding is the
    // precondition, the tie closes only after the real receipt, and the winning Local file replaces the folder.
    private static async Task SelectedTieReplacementAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item", "winner");
        var occupant = await context.Dropbox.EnsureFolderAsync("item", CancellationToken.None);
        var oldChild = await PutAsync(context.Dropbox, "item/old-child", "old child");
        // Both sides were folders when the binding was written; the Local side then became this file (kind tie).
        var previousLocal = new Entry("item", true, 0, DateTimeOffset.UnixEpoch, "previous-local-folder", "", "", null);
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(previousLocal, occupant),
                SyncedEntries.From(previousLocal with
                    { Path = "item/old-child", Id = "previous-local-child", IsFolder = false }, oldChild)]
        });
        var issue = Raise(context, IssueKind.Tie, TextKey.TieKindBoth, local, occupant, null);
        var otherIssue = Raise(context, IssueKind.NameClash, TextKey.ClashNameTaken,
            await PutAsync(context.Local, "other", "local"), await PutAsync(context.Dropbox, "other", "remote"), null);
        var replacing = new ReplacingSide(context.Dropbox, occupant, false, true);
        context = context with { Dropbox = replacing };
        var outcome = await Runner(context).ResolveAsync(issue, ResolutionChoice.UseLocal, null, null, CancellationToken.None);
        var written = await context.Dropbox.ReadOneAsync("item", CancellationToken.None);
        Check.Require(!Open(context, issue) && Open(context, otherIssue) && outcome.Raised is null && written is { IsFolder: false },
            "A kind tie's UseLocal resolution closes only its selected tie and the winning file replaces the Dropbox folder. raised=" +
            outcome.Raised + " written=" + (written is null ? "absent" : written.IsFolder ? "folder" : "file"));
        Check.Require(replacing.Submitted.Any(effects => effects.Any(effect => effect.Expected == occupant)) ||
            replacing.Deleted.Contains(occupant),
            "The selected Dropbox folder is removed once through a real provider route.");
    }

    // Changing a selected identity after the notice was raised refuses the replacement with no provider effect. The
    // first read (planning) sees the selected entry; the removal's own re-observation sees the changed one.
    private static async Task StaleSelectedReplacementAsync(bool dropboxChanged)
    {
        var context = Context();
        using var store = context.Store;
        var rawLocal = (FakeSide)context.Local;
        var rawDropbox = (FakeSide)context.Dropbox;
        var local = await PutAsync(rawLocal, "item", "winner");
        var occupant = await rawDropbox.EnsureFolderAsync("item", CancellationToken.None);
        var issue = Raise(context, IssueKind.NameClash, TextKey.ClashFileFolder, local, occupant, Side.Local);
        var deleting = new ReplacingSide(rawDropbox, occupant, false, true);
        context = dropboxChanged
            ? context with { Dropbox = new RaceSide(deleting, "item", occupant with { Id = "changed-folder" }) }
            : context with
            {
                Dropbox = deleting,
                Local = new RaceSide(rawLocal, "item", local with { Id = "changed-local" })
            };
        var outcome = await Runner(context).ResolveAsync(issue, ResolutionChoice.ReplaceExisting, null, null,
            CancellationToken.None);
        Check.Require(Open(context, issue) && outcome.Raised == IssueKind.Failure &&
            deleting.Submitted.Count == 0 && deleting.Deleted.Count == 0 &&
            await rawDropbox.ReadOneAsync("item", CancellationToken.None) is { IsFolder: true } &&
            await rawLocal.ReadOneAsync("item", CancellationToken.None) is { IsFolder: false },
            "A selected identity changed after the notice refuses the replacement with no effect. dropboxChanged=" +
            dropboxChanged + " open=" + Open(context, issue) + " raised=" + outcome.Raised +
            " submitted=" + deleting.Submitted.Count + " deleted=" + deleting.Deleted.Count);
    }

    // A second holding notice on the same row refuses the selected replacement before any provider effect.
    private static async Task OtherNoticeHoldsReplacementAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item", "winner");
        var occupant = await context.Dropbox.EnsureFolderAsync("item", CancellationToken.None);
        var issue = Raise(context, IssueKind.NameClash, TextKey.ClashFileFolder, local, occupant, Side.Local);
        Raise(context, IssueKind.Tie, TextKey.TieEqualTimes, local, occupant, null);
        var deleting = new ReplacingSide(context.Dropbox, occupant, false, true);
        context = context with { Dropbox = deleting };
        var outcome = await Runner(context).ResolveAsync(issue, ResolutionChoice.ReplaceExisting, null, null,
            CancellationToken.None);
        Check.Require(outcome.None == NoneReason.Held && Open(context, issue) &&
            deleting.Submitted.Count == 0 && deleting.Deleted.Count == 0 &&
            await context.Local.ReadOneAsync("item", CancellationToken.None) == local &&
            await context.Dropbox.ReadOneAsync("item", CancellationToken.None) == occupant,
            "Another holding notice on the row refuses the selected replacement before any effect. none=" +
            outcome.None + " submitted=" + deleting.Submitted.Count + " deleted=" + deleting.Deleted.Count);
    }

    private static async Task DifferingKeyHoldAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "local-name", "winner");
        var remote = await context.Dropbox.EnsureFolderAsync("remote-name", CancellationToken.None);
        store.Commit(context.Pair.PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(local with { IsFolder = true }, remote)] });
        var selected = Raise(context, IssueKind.Tie, TextKey.TieKindBoth, local, remote, null);
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, remote.Path,
                new IssueSubject([new(Side.Dropbox, remote.Path, remote.Id)], null))]
        });
        var deleting = new ReplacingSide(context.Dropbox, remote, false, true);
        context = context with { Dropbox = deleting };
        var outcome = await Runner(context).ResolveAsync(selected, ResolutionChoice.UseLocal, null, null, CancellationToken.None);
        Check.Require(outcome.None == NoneReason.Held && Open(context, selected) &&
            deleting.Submitted.Count == 0 && deleting.Deleted.Count == 0 &&
            await context.Dropbox.ReadOneAsync(remote.Path, CancellationToken.None) == remote,
            "A hold on the differently named Dropbox folder prevents the selected removal.");
    }

    private static async Task SelectedUnreadableChildAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await context.Local.EnsureFolderAsync("folder", CancellationToken.None);
        var remote = await context.Dropbox.EnsureFolderAsync("folder", CancellationToken.None);
        var child = await PutAsync(context.Dropbox, "folder/x", "protected");
        UnreadableMember.Record(store, context.Pair.PairId, "folder/x");
        var jobs = SyncJobs.Create(context, new SyncPorts(PassthroughRecoveryBoundary.Instance,
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        var row = Row.ForOneSide(Side.Local, PathNames.Key(local.Path), local, null, [], null, RowContext.Neutral)
            with { Dropbox = remote };
        try { await jobs.Items.Folders.RemoveSelectedReplacementAsync(row, CancellationToken.None); }
        catch (ItemFailedException error) when (error.Cause == TextKey.ReadLocalFailed) { }
        Check.Require(await context.Dropbox.ReadOneAsync(child.Path, CancellationToken.None) == child &&
            await context.Dropbox.ReadOneAsync(remote.Path, CancellationToken.None) == remote,
            "Unreadable Local content protects an unrecorded Dropbox child from selected replacement.");
    }

    private static async Task UnboundRowHoldsAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            var context = Context();
            using var store = context.Store;
            var local = await context.Local.EnsureFolderAsync("local-name", CancellationToken.None);
            var remote = await context.Dropbox.EnsureFolderAsync("remote-name", CancellationToken.None);
            var selected = Raise(context, IssueKind.NameClash, TextKey.ClashNameTaken, local, remote, Side.Local);
            var entry = side == Side.Local ? local : remote;
            store.Commit(context.Pair.PairId, StateChange.Empty with
            { Raise = [(IssueKind.Tie, TextKey.TieKindBoth, entry.Path, new IssueSubject([new(side, entry.Path, entry.Id)], null))] });
            var row = Row.ForOneSide(Side.Dropbox, "unrelated-key", remote, null, [], null, RowContext.Neutral) with { Local = local };
            Check.Require(store.HeldByOtherIssue(context.Pair.PairId, row, selected.Id),
                "A Synced-null row checks both actual entry paths even when neither is Row.Key.");
        }
    }

    private static async Task LocalFolderOtherHoldAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await context.Local.EnsureFolderAsync("item", CancellationToken.None);
        var remote = await PutAsync(context.Dropbox, "item", "winner");
        var selected = Raise(context, IssueKind.NameClash, TextKey.ClashFileFolder, local, remote, Side.Dropbox);
        var other = Raise(context, IssueKind.Tie, TextKey.TieKindBoth, local, remote, null);
        var deleting = new ReplacingSide(context.Local, local, false, true);
        context = context with { Local = deleting };
        var outcome = await Runner(context).ResolveAsync(selected, ResolutionChoice.ReplaceExisting, null, null, CancellationToken.None);
        Check.Require(outcome.None == NoneReason.Held && Open(context, selected) && Open(context, other) &&
            deleting.Deleted.Count == 0 && await context.Local.ReadOneAsync(local.Path, CancellationToken.None) == local,
            "An unrelated Tie prevents the Local-folder kind replacement before any removal.");
    }

    private static async Task PlannedRetainedChildAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item", "winner");
        var remote = await context.Dropbox.EnsureFolderAsync("item", CancellationToken.None);
        var child = await context.Dropbox.EnsureFolderAsync("item/nested", CancellationToken.None);
        var saved = SyncedEntries.From(local with { IsFolder = true }, remote);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var nested = context.Pair with
        {
            PairId = "nested-pair", LocalRoot = Path.Combine(context.Pair.LocalRoot, "item", "nested"),
            DropboxRoot = context.Pair.DropboxRoot.TrimEnd('/') + "/item/nested"
        };
        context = context with { Scope = new PathScope(context.Pair, [context.Pair, nested]) };
        var jobs = SyncJobs.Create(context, new SyncPorts(PassthroughRecoveryBoundary.Instance,
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        var row = Row.ForOneSide(Side.Local, saved.LocalKey, local, saved, [], null, RowContext.Neutral) with { Dropbox = remote };
        var outcome = await jobs.Items.Dispatch.DispatchPreparedAsync(new SyncAction(ActionKind.ReplaceKind, row,
            Side: Side.Dropbox, Target: row.Dropbox!.Path), CancellationToken.None);
        Check.Require(outcome.Cause == TextKey.FailChanged &&
            store.ActiveFolderRemovals(context.Pair.PairId, CancellationToken.None).Any() &&
            await context.Dropbox.ReadOneAsync(child.Path, CancellationToken.None) == child,
            "A planned replacement with retained content keeps its original FailChanged cause.");
    }

    private static async Task FailedSelectedSendAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item", "winner");
        var remote = await context.Dropbox.EnsureFolderAsync("item", CancellationToken.None);
        var selected = Raise(context, IssueKind.NameClash, TextKey.ClashFileFolder, local, remote, Side.Local);
        var provider = new ReplacingSide(context.Dropbox, remote, false, true, failDelete: true);
        context = context with { Dropbox = provider };
        var outcome = await Runner(context).ResolveAsync(selected, ResolutionChoice.ReplaceExisting, null, null, CancellationToken.None);
        var operation = store.ActiveFolderRemovals(context.Pair.PairId, CancellationToken.None).Single();
        Check.Require(provider.Submitted.Count == 1 && operation.Cause == TextKey.DropboxRefused &&
            outcome.Cause == operation.Cause && Open(context, selected) &&
            await context.Dropbox.ReadOneAsync(remote.Path, CancellationToken.None) == remote,
            "A failed selected send keeps its operation's provider cause rather than claiming protected content: " +
            $"submitted={provider.Submitted.Count}, operation={operation.Cause}, outcome={outcome.Cause}, open={Open(context, selected)}.");
    }

    private static async Task RetainedSelectedChildAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item", "winner");
        var remote = await context.Dropbox.EnsureFolderAsync("item", CancellationToken.None);
        var child = await context.Dropbox.EnsureFolderAsync("item/nested", CancellationToken.None);
        var nested = context.Pair with
        {
            PairId = "nested-pair", LocalRoot = Path.Combine(context.Pair.LocalRoot, "item", "nested"),
            DropboxRoot = context.Pair.DropboxRoot.TrimEnd('/') + "/item/nested"
        };
        context = context with { Scope = new PathScope(context.Pair, [context.Pair, nested]) };
        var selected = Raise(context, IssueKind.NameClash, TextKey.ClashFileFolder, local, remote, Side.Local);
        var outcome = await Runner(context).ResolveAsync(selected, ResolutionChoice.ReplaceExisting, null, null, CancellationToken.None);
        Check.Require(outcome.Raised == IssueKind.Failure && outcome.Cause == TextKey.FailReplacementRetained && Open(context, selected) &&
            store.ActiveFolderRemovals(context.Pair.PairId, CancellationToken.None).Any() &&
            await context.Dropbox.ReadOneAsync(remote.Path, CancellationToken.None) == remote &&
            await context.Dropbox.ReadOneAsync(child.Path, CancellationToken.None) == child,
            "A non-Complete selected removal retains its nested root and does not run the replacement or close the notice.");
    }

    // A holding notice on an ancestor refuses the selected replacement of its descendant before any effect.
    private static async Task HeldAncestorReplacementAsync()
    {
        var context = Context();
        using var store = context.Store;
        var groupLocal = await context.Local.EnsureFolderAsync("group", CancellationToken.None);
        var groupDropbox = await context.Dropbox.EnsureFolderAsync("group", CancellationToken.None);
        var local = await PutAsync(context.Local, "group/item", "winner");
        var occupant = await context.Dropbox.EnsureFolderAsync("group/item", CancellationToken.None);
        var issue = Raise(context, IssueKind.NameClash, TextKey.ClashFileFolder, local, occupant, Side.Local);
        Raise(context, IssueKind.Tie, TextKey.TieEqualTimes, groupLocal, groupDropbox, null);
        var deleting = new ReplacingSide(context.Dropbox, occupant, false, true);
        context = context with { Dropbox = deleting };
        var outcome = await Runner(context).ResolveAsync(issue, ResolutionChoice.ReplaceExisting, null, null,
            CancellationToken.None);
        Check.Require(outcome.None == NoneReason.Held && Open(context, issue) &&
            deleting.Submitted.Count == 0 && deleting.Deleted.Count == 0 &&
            await context.Dropbox.ReadOneAsync("group/item", CancellationToken.None) == occupant,
            "A holding notice on an ancestor refuses the selected replacement before any effect. none=" +
            outcome.None + " submitted=" + deleting.Submitted.Count + " deleted=" + deleting.Deleted.Count);
    }

    private static async Task FolderAsync(Side side, bool incoming, bool failChild)
    {
        var context = Context();
        using var store = context.Store;
        var source = context.For(side);
        var destination = context.For(side == Side.Local ? Side.Dropbox : Side.Local);
        var folder = await source.EnsureFolderAsync("folder", CancellationToken.None);
        await PutAsync(source, "folder/a", "first");
        await source.EnsureFolderAsync("folder/nested", CancellationToken.None);
        await PutAsync(source, "folder/nested/b", "second");
        ((FakeSide)source).Seed(new Entry("folder/skipped", false, 0, default, "skipped-file", "", "",
            TextKey.SkipOnlineOnly.ToString()), null);
        ((FakeSide)source).Seed(new Entry("folder/skipped-folder", true, 0, default, "skipped-folder", "", "",
            TextKey.SkipLink.ToString()), null);
        var occupant = await PutAsync(destination, "folder", "occupant");
        var issue = Raise(context, IssueKind.NameClash, TextKey.ClashFileFolder,
            side == Side.Local ? folder : occupant, side == Side.Local ? occupant : folder, incoming ? side : null);
        var replacingSide = new ReplacingSide(destination, occupant, false, true,
            stageFault: path => failChild && path == "folder/nested/b"
                ? new ItemFailedException(path, TextKey.PublishFailed, null) : null);
        context = side == Side.Local ? context with { Dropbox = replacingSide } : context with { Local = replacingSide };
        var visits = new List<(Side Side, string Path)>();
        context = context with
        {
            Local = new RecordingSide(context.Local, () => !Open(context, issue), visits),
            Dropbox = new RecordingSide(context.Dropbox, () => !Open(context, issue), visits)
        };
        var runner = Runner(context);
        var choice = incoming ? ResolutionChoice.ReplaceExisting :
            side == Side.Local ? ResolutionChoice.KeepLocal : ResolutionChoice.KeepDropbox;
        var outcome = await runner.ResolveAsync(issue, choice, null, null, CancellationToken.None);
        var folderFailures = store.Issues(context.Pair.PairId).Where(item => item.Kind == IssueKind.Failure).ToArray();
        Check.Require(!Open(context, issue),
            "A child failure never reopens the resolved folder conflict. side=" + side + " incoming=" + incoming +
            " failChild=" + failChild + " resolved=" + runner.Resolved + " raised=" + outcome.Raised +
            " failures=" + string.Join("|", folderFailures.Select(item => item.Path + ":" + item.Cause)) +
            " issues=" + string.Join("|", store.Issues(context.Pair.PairId)
                .Select(item => item.Id + ":" + item.Kind + ":" + item.Path + ":" + item.Cause +
                    ":" + item.Dismissed)) + " expectedId=" + issue.Id + ":" + issue.Kind + ":" + issue.Path);
        Check.Require(replacingSide.Deleted.SequenceEqual(new[] { occupant }) &&
            (await destination.ReadOneAsync(folder.Path, CancellationToken.None))?.IsFolder == true,
            "A folder winner deletes the exact file occupant via the side's bin/history path before creation.");
        Check.Require(Visited(visits, (side, "folder/a"), (side, "folder/nested"), (side, "folder/nested/b")) &&
            await destination.ReadOneAsync("folder/a", CancellationToken.None) is not null,
            "Recursive resolution visits every readable descendant, skips files and folders marked Skip, and keeps earlier copies. visits=" +
            string.Join("|", visits.Select(visit => visit.Side + ":" + visit.Path)));
        if (failChild) Check.Require(outcome.Raised == IssueKind.Failure &&
            await destination.ReadOneAsync("folder/nested/b", CancellationToken.None) is null,
            "A late descendant failure is returned without reviving the obsolete root conflict.");
        else Check.Require(await destination.ReadOneAsync("folder/nested/b", CancellationToken.None) is not null,
            "Recursive resolution copies every descendant after the root closes.");
    }

    // The real two-action root (EX-01, A28): the occupant folder goes first; the notice closes only when the incoming
    // folder is created, and its children then run as independent follow-ups.
    private static async Task FolderOverFolderAsync(Side side, bool failCopy)
    {
        var context = Context();
        using var store = context.Store;
        var source = context.For(side);
        var destination = context.For(PathNames.Other(side));
        var folder = await source.EnsureFolderAsync("folder", CancellationToken.None);
        await PutAsync(source, "folder/a", "first");
        var occupant = await destination.EnsureFolderAsync("folder", CancellationToken.None);
        if (side == Side.Local) await PutAsync(destination, "folder/unrecorded", "old content");
        var issue = Raise(context, IssueKind.NameClash, TextKey.ClashNameTaken,
            side == Side.Local ? folder : occupant, side == Side.Local ? occupant : folder, side);
        var replacing = new ReplacingSide(destination, occupant, false, true, failEnsure: failCopy);
        context = side == Side.Local ? context with { Dropbox = replacing } : context with { Local = replacing };
        var visits = new List<(Side Side, string Path)>();
        context = context with
        {
            Local = new RecordingSide(context.Local, () => !Open(context, issue), visits),
            Dropbox = new RecordingSide(context.Dropbox, () => !Open(context, issue), visits)
        };
        var runner = Runner(context);
        var outcome = await runner.ResolveAsync(issue, ResolutionChoice.ReplaceExisting, null, null, CancellationToken.None);
        Check.Require(Open(context, issue) == failCopy && runner.Resolved != failCopy &&
            (outcome.Raised == IssueKind.Failure) == failCopy,
            "A failing second root action leaves the notice open; both succeeding closes it.");
        (Side Side, string Path)[] followedPaths = failCopy
            ? Array.Empty<(Side, string)>() : new[] { (side, "folder/a") };
        Check.Require(Visited(visits, [.. followedPaths]),
            "Children follow only a completed root. visits=" +            string.Join("|", visits.Select(visit => visit.Side + ":" + visit.Path)));
        Check.Require(side == Side.Local
                ? replacing.Submitted.Count == 1 && replacing.Submitted[0].Single().Expected == occupant
                : replacing.Deleted.SequenceEqual(new[] { occupant }),
            "The occupant folder is removed once through its side's real route (Dropbox receipt or Local bin).");
        if (side == Side.Local)
            Check.Require(await destination.ReadOneAsync("folder/unrecorded", CancellationToken.None) is null,
                "The selected same-kind Dropbox replacement removes an unrecorded occupant child.");
    }

    private static async Task FolderTieAsync(Side winner, bool failChild)
    {
        var context = Context();
        using var store = context.Store;
        var local = winner == Side.Local
            ? await context.Local.EnsureFolderAsync("folder", CancellationToken.None)
            : await PutAsync(context.Local, "folder", "local");
        var dropbox = winner == Side.Dropbox
            ? await context.Dropbox.EnsureFolderAsync("folder", CancellationToken.None)
            : await PutAsync(context.Dropbox, "folder", "remote");
        var source = context.For(winner);
        await PutAsync(source, "folder/a", "first");
        await PutAsync(source, "folder/b", "second");
        var issue = Raise(context, IssueKind.Tie, TextKey.TieKindBoth, local, dropbox, null);
        // A failing descendant is reported from its own side's observation, after the path is visited.
        if (failChild) ((FakeSide)source).FailNext("folder/b",
            new ItemFailedException("folder/b", TextKey.PublishFailed, null));
        var visits = new List<(Side Side, string Path)>();
        context = context with
        {
            Local = new RecordingSide(context.Local, () => !Open(context, issue), visits),
            Dropbox = new RecordingSide(context.Dropbox, () => !Open(context, issue), visits)
        };
        var runner = Runner(context);
        var outcome = await runner.ResolveAsync(issue,
            winner == Side.Local ? ResolutionChoice.UseLocal : ResolutionChoice.UseDropbox, null, null, CancellationToken.None);
        Check.Require(!Open(context, issue),
            "A folder-winning kind tie stays closed even when a descendant fails.");
        Check.Require(Visited(visits, (winner, "folder/a"), (winner, "folder/b")),
            "A folder-winning kind tie syncs every descendant of the winning folder. visits=" +
            string.Join("|", visits.Select(visit => visit.Side + ":" + visit.Path)));
        if (failChild)
            Check.Require(outcome.Raised == IssueKind.Failure &&
                await context.For(PathNames.Other(winner)).ReadOneAsync("folder/b", CancellationToken.None) is null,
                "A failed descendant reports failure separately from the completed root choice.");
        else
            Check.Require(await context.For(PathNames.Other(winner)).ReadOneAsync("folder/b", CancellationToken.None) is not null,
                "A folder-winning kind tie copies every descendant after the root closes.");
    }

    private static async Task MappedCandidatesAsync(bool stale, bool reverse)
    {
        var context = Context();
        using var store = context.Store;
        var first = await PutAsync(context.Dropbox, "a:b", "first");
        var extra = await PutAsync(context.Dropbox, "a?b", "extra");
        Check.Require(PathNames.DropboxKey(first.Path) != PathNames.DropboxKey(extra.Path),
            "Distinct Dropbox names keep distinct frozen keys.");
        var items = new IssueItem[]
        {
            new(Side.Dropbox, first.Path, first.Id), new(Side.Dropbox, extra.Path, extra.Id)
        };
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashMappedNames, first.Path,
                new IssueSubject(reverse ? items.Reverse().ToArray() : items, null))]
        });
        var issue = store.Issues(context.Pair.PairId).Single();
        if (stale)
        {
            await context.Dropbox.DeleteAsync(extra, null, CancellationToken.None);
            extra = await PutAsync(context.Dropbox, extra.Path, "replacement");
        }
        var visits = new List<(Side Side, string Path)>();
        var localProbe = new RecordingSide(context.Local, () => !Open(context, issue), visits);
        var dropboxProbe = new RecordingSide(context.Dropbox, () => !Open(context, issue), visits);
        context = context with { Local = localProbe, Dropbox = dropboxProbe };
        var rejected = false;
        try
        {
            await Runner(context)
                .ResolveAsync(issue, ResolutionChoice.RenameDropbox, "renamed", null, CancellationToken.None);
        }
        catch (EngineRequestException error)
        {
            rejected = error.Key == TextKey.ResolveSubjectStale;
        }
        Check.Require(rejected == stale && Open(context, issue) == stale,
            "Every extra Dropbox candidate participates in subject validation before resolution.");
        (Side Side, string Path)[] followedPaths = stale
            ? Array.Empty<(Side, string)>()
            : new[] { (Side.Dropbox, "renamed"), (Side.Local, first.Path) };
        Check.Require(Visited(visits, [.. followedPaths]) &&
            dropboxProbe.Moves.Count == (stale ? 0 : 1) && (stale || dropboxProbe.Moves[0] == (first.Path, "renamed")),
            "Mapped-candidate follow-ups retain the original provider path, not its local mapped key, and the rename is " +
            "one provider move. visits=" + string.Join("|", visits.Select(visit => visit.Side + ":" + visit.Path)));
        Check.Require((await context.Dropbox.ReadOneAsync(stale ? first.Path : "renamed", CancellationToken.None))?.Id == first.Id,
            "A stale extra prevents mutation; a matching subject renames the issue-path item regardless of subject order.");
        Check.Require(stale || await context.Dropbox.ReadOneAsync(first.Path, CancellationToken.None) is null,
            "The selected identity moves away from its original mapped name.");
        Check.Require(await context.Dropbox.ReadOneAsync(extra.Path, CancellationToken.None) == extra,
            "Renaming a mapped candidate leaves the other candidate untouched.");
    }

    private static async Task ThreeItemAsync(Side incoming, bool replace)
    {
        var context = Context();
        using var store = context.Store;
        var source = context.For(incoming);
        var destination = context.For(PathNames.Other(incoming));
        var moved = await PutAsync(source, "moved", "incoming");
        var counterpart = await PutAsync(destination, "old", "counterpart");
        var occupant = await PutAsync(destination, moved.Path, "occupant");
        var beforeMove = moved with { Path = counterpart.Path };
        var saved = incoming == Side.Local ? SyncedEntries.From(beforeMove, counterpart) : SyncedEntries.From(counterpart, beforeMove);
        var items = new IssueItem[]
        {
            new(PathNames.Other(incoming), counterpart.Path, counterpart.Id),
            new(incoming, moved.Path, moved.Id),
            new(PathNames.Other(incoming), occupant.Path, occupant.Id)
        };
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Record = [saved],
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, occupant.Path, new IssueSubject(items, incoming))]
        });
        var issue = store.Issues(context.Pair.PairId).Single();
        if (replace)
        {
            var replacing = new ReplacingSide(destination, occupant, false);
            context = incoming == Side.Local ? context with { Dropbox = replacing } : context with { Local = replacing };
        }
        var visits = new List<(Side Side, string Path)>();
        context = context with
        {
            Local = new RecordingSide(context.Local, () => !Open(context, issue), visits),
            Dropbox = new RecordingSide(context.Dropbox, () => !Open(context, issue), visits)
        };
        var outcome = await Runner(context).ResolveAsync(issue,
            replace ? ResolutionChoice.ReplaceExisting : ResolutionChoice.RenameIncoming,
            replace ? null : "renamed", null, CancellationToken.None);
        (Side Side, string Path)[] followedPaths = replace
            ? Array.Empty<(Side, string)>()
            : new[] { (incoming, "renamed"), (PathNames.Other(incoming), moved.Path) };
        Check.Require(Visited(visits, [.. followedPaths]) &&
            new[] { occupant.Path, counterpart.Path }.Where(path => !followedPaths.Any(expected => expected.Path == path))
                .All(path => !visits.Any(visit => visit.Path == path)),
            "A moved identity follows up its captured source path, not the occupant or old counterpart. visits=" +
            string.Join("|", visits.Select(visit => visit.Side + ":" + visit.Path)));
        Check.Require(!Open(context, issue) && outcome.Raised is null &&
            await destination.ReadOneAsync(counterpart.Path, CancellationToken.None) == counterpart,
            "A three-item refused-rename subject validates every identity and leaves the old counterpart untouched.");
        if (replace)
        {
            var written = await destination.ReadOneAsync(occupant.Path, CancellationToken.None);
            Check.Require(written is not null && await destination.ContentHashAsync(written, CancellationToken.None) ==
                await source.ContentHashAsync(moved, CancellationToken.None),
                "Replace targets the issue-path occupant, not the subject's first same-side counterpart.");
        }
        else
            Check.Require((await source.ReadOneAsync("renamed", CancellationToken.None))?.Id == moved.Id &&
                await destination.ReadOneAsync(occupant.Path, CancellationToken.None) == occupant,
                "Rename-incoming accepts the second observed Local identity without changing either destination entry.");
    }

    private static async Task InvalidAsync(bool stale)
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item", "local");
        var dropbox = await PutAsync(context.Dropbox, "item", "remote");
        var issue = Raise(context, IssueKind.Tie, TextKey.TieEqualTimes, local, dropbox, null);
        if (stale)
        {
            await context.Local.DeleteAsync(local, null, CancellationToken.None);
            await PutAsync(context.Local, local.Path, "replacement");
        }
        var rejected = false;
        try
        {
            await Runner(context).ResolveAsync(issue,
                stale ? ResolutionChoice.UseLocal : ResolutionChoice.ReplaceExisting, null, null, CancellationToken.None);
        }
        catch (EngineRequestException error)
        {
            rejected = error.Key == (stale ? TextKey.ResolveSubjectStale : TextKey.ResolveBadChoice);
        }
        Check.Require(rejected && Open(context, issue), "Stale subjects and bad choices leave the issue open.");
    }

    private static async Task StaleBeforeBadChoiceAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item", "local");
        var dropbox = await PutAsync(context.Dropbox, "item", "remote");
        var issue = Raise(context, IssueKind.Tie, TextKey.TieEqualTimes, local, dropbox, null);
        await context.Local.DeleteAsync(local, null, CancellationToken.None);
        await PutAsync(context.Local, local.Path, "replacement");
        var key = TextKey.ResolveBadChoice;
        try
        {
            await Runner(context).ResolveAsync(issue, ResolutionChoice.ReplaceExisting, null, null, CancellationToken.None);
        }
        catch (EngineRequestException error)
        {
            key = error.Key;
        }
        Check.Require(key == TextKey.ResolveSubjectStale && Open(context, issue),
            "A stale subject is reported before an unsupported choice.");
    }

    private static async Task LockAndCancellationAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item", "local");
        var dropbox = await PutAsync(context.Dropbox, "item", "remote");
        var issue = Raise(context, IssueKind.NameClash, TextKey.ClashNameTaken, local, dropbox, Side.Local);
        await using var hold = await PathLocks.Shared.EnterAsync(
            LockKeys.ForPaths(context.Pair, [(Side.Local, "renamed")]), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var resolution = Runner(context).ResolveAsync(issue, ResolutionChoice.RenameIncoming, "renamed", null, cancellation.Token);
        Check.Require(!resolution.IsCompleted, "A selected rename target is included in the resolution hold.");
        cancellation.Cancel();
        var cancelled = false;
        try { await resolution; } catch (OperationCanceledException) { cancelled = true; }
        Check.Require(cancelled && Open(context, issue) &&
            await context.Local.ReadOneAsync(local.Path, CancellationToken.None) == local,
            "Cancellation while waiting propagates without mutation or closure.");
    }

    // CV-ENTRY: one selected boundary execution receives the exact notice and a non-empty root footprint, and a
    // refused boundary performs no effect and leaves the notice open.
    private static async Task SelectedAdmissionAsync(bool refuse)
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "Item", "local");
        var dropbox = await PutAsync(context.Dropbox, "Item", "remote");
        var issue = Raise(context, IssueKind.Tie, TextKey.TieEqualTimes, local, dropbox, null);
        var boundary = new TestSelectedBoundary(context, refuse);
        var effects = new List<RunEvent>();
        context = context with
        {
            Events = entry =>
            {
                if (entry.Kind != RunEventKind.Effect) return;
                Check.Require(boundary.RequireCalls > 0, "The selected lease Require runs before any root effect.");
                effects.Add(entry);
            }
        };
        SyncOutcome? outcome = null;
        var stale = false;
        try
        {
            outcome = await Runner(context, boundary)
                .ResolveAsync(issue, ResolutionChoice.UseLocal, null, null, CancellationToken.None);
        }
        catch (LiveAdmissionStaleException error)
        {
            stale = error.Reason == LiveAdmissionStaleReason.SelectionChanged;
        }
        if (refuse)
        {
            Check.Require(stale && effects.Count == 0 && Open(context, issue),
                "A refused selected admission performs no effect and leaves the original notice open.");
            return;
        }
        Check.Require(!stale && outcome is not null && boundary.Executions == 1 &&
            boundary.Expected?.Id == issue.Id && boundary.Footprint.Count != 0 &&
            boundary.RequiredGeneration == context.ExpectedGeneration && boundary.RequiredKeys.Count != 0,
            "One selected phase receives the exact notice, generation and complete non-empty lock footprint.");
        Check.Require(effects.Count == 1 && !Open(context, issue),
            "The admitted root effect and closure complete inside that single selected phase.");
    }

    private static async Task SelectedExecutionFreshnessAsync()
    {
        foreach (var (side, observedResult) in new[] { (Side.Local, false), (Side.Dropbox, false), (Side.Local, true) })
        {
            var context = Context();
            using var store = context.Store;
            var local = await PutAsync(context.Local, "Item", "local");
            var remote = await PutAsync(context.Dropbox, "Item", "remote");
            var issue = Raise(context, IssueKind.Tie, TextKey.TieEqualTimes, local, remote, null);
            var before = System.Text.Json.JsonSerializer.Serialize(issue);
            var events = new List<RunEvent>();
            var observed = local with { ContentHash = await context.Local.ContentHashAsync(local, default) };
            var target = new ReplacingSide(context.For(side), side == Side.Local ? local : remote,
                false, stageFault: observedResult ? null : path => new ChangedUnderneathException(path),
                publishResult: observedResult ? new RemovalResult(RemovalOutcome.NoEffect, null, null, TextKey.FailChanged)
                    { ObservedLocal = observed } : null);
            context = side == Side.Local ? context with { Local = target, Events = events.Add } :
                context with { Dropbox = target, Events = events.Add };
            var runner = Runner(context);
            LiveAdmissionStaleException? stale = null;
            try
            {
                await runner.ResolveAsync(issue, side == Side.Local ? ResolutionChoice.UseDropbox : ResolutionChoice.UseLocal,
                    null, null, default);
            }
            catch (LiveAdmissionStaleException error) { stale = error; }
            Check.Require(stale?.Reason == LiveAdmissionStaleReason.FootprintChanged && !runner.Resolved &&
                (observedResult ? target.Publications == 1 : target.Failure is ChangedUnderneathException),
                "A selected execution race reports the same stale refusal as a freshness failure without replaying the choice.");
            Check.Require(System.Text.Json.JsonSerializer.Serialize(store.IssueById(context.Pair.PairId, issue.Id)) == before &&
                store.Issues(context.Pair.PairId).Count == 1 && events.Count == 0 &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path)) is null &&
                await context.Local.ReadOneAsync(local.Path, default) == local &&
                await context.Dropbox.ReadOneAsync(remote.Path, default) == remote,
                "Selected stale execution keeps the exact issue, both entries and unsaved binding unchanged.");
            Check.Require(SyncCli.DescribeException(stale!).Contains(nameof(LiveAdmissionStaleException), StringComparison.Ordinal),
                "The existing CLI failure formatter makes the selected stale refusal visible.");
            Console.WriteLine($"selected execution freshness {side}: stale={stale!.Reason}, issue unchanged, effects=0");
        }
    }

    private static async Task SelectedAncestorRefusalAsync()
    {
        foreach (var kind in new[] { IssueKind.Tie, IssueKind.NameClash })
        {
            var context = Context();
            using var store = context.Store;
            var pair = context.Pair;
            var parent = await context.Local.EnsureFolderAsync("Parent", default);
            await context.Dropbox.EnsureFolderAsync("Parent", default);
            var local = await PutAsync(context.Local, "Parent/Item", "local");
            var remote = await PutAsync(context.Dropbox, "Parent/Item", "remote");
            var issue = Raise(context, kind, kind == IssueKind.Tie ? TextKey.TieEqualTimes : TextKey.ClashNameTaken,
                local, remote, null);
            var before = System.Text.Json.JsonSerializer.Serialize(issue);
            var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
            var source = store.ReadSource(pair.PairId, Side.Local)!.Value;
            var domain = pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var position = store.ReadJournal(domain)!;
            Check.Require(store.AcquireJournalPage(domain, position, position,
                [(pair.PairId, generation, source.Epoch, source.Revision, false)],
                [(pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Changed, parent.Path, null,
                    parent.Id, parent, false, false))]) is not null,
                "The pending ancestor is acquired through the journal source.");
            var pending = store.PendingAncestors(pair.PairId, generation, Side.Local, [PathNames.Key(parent.Path)]).Single();
            var events = new List<RunEvent>();
            var localCalls = new CountingSide(context.Local);
            var remoteCalls = new CountingSide(context.Dropbox);
            context = context with { Local = localCalls, Dropbox = remoteCalls, Events = events.Add };
            var boundary = new TestSelectedBoundary(context);
            var syncer = new Syncer(context, new SyncPorts(PassthroughRecoveryBoundary.Instance,
                RefusingExecutionBoundary.Instance, boundary, 0));
            ResolutionOutcome? outcome = null;
            LiveAdmissionStaleException? stale = null;
            try
            {
                outcome = await syncer.ResolveAsync(issue,
                    kind == IssueKind.Tie ? ResolutionChoice.UseLocal : ResolutionChoice.KeepLocal, null, null, default);
            }
            catch (LiveAdmissionStaleException error) { stale = error; }
            Check.Require(stale?.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged && outcome is null &&
                stale.AncestorBlocker?.Selection == pending && boundary.Executions == 1 && boundary.RequireCalls > 0,
                "The real selected resolution reaches its pending-ancestor stale exit: " + kind);
            Check.Require(System.Text.Json.JsonSerializer.Serialize(store.IssueById(pair.PairId, issue.Id)) == before &&
                store.Issues(pair.PairId).Count == 1 && events.Count == 0 &&
                store.PendingAncestors(pair.PairId, generation, Side.Local, [PathNames.Key(parent.Path)]).Single() == pending,
                "The ancestor refusal keeps the exact selected notice and pending work without a false Failure: " + kind);
            Check.Require(localCalls.Work == 0 && remoteCalls.Work == 0 &&
                await context.Local.ReadOneAsync(local.Path, default) == local &&
                await context.Dropbox.ReadOneAsync(remote.Path, default) == remote &&
                store.SyncedByKey(pair.PairId, Side.Local, PathNames.Key(local.Path)) is null,
                "A refused selected resolution performs no provider effect or binding write: " + kind);
        }
    }

    private static async Task ListingOccurrencesAsync(string failure)
    {
        var context = Context();
        using var store = context.Store;
        using var cancellation = new CancellationTokenSource();
        var source = (FakeSide)context.Dropbox;
        var folder = await source.EnsureFolderAsync("folder", default);
        await source.EnsureFolderAsync("folder/X", default);
        await source.EnsureFolderAsync("folder/X/a", default);
        var child = await PutAsync(source, "folder/X/a/b", "child");
        var occupant = await PutAsync(context.Local, "folder", "occupant");
        var issue = Raise(context, IssueKind.Tie, TextKey.TieKindBoth, occupant, folder, null);
        var root = context.Pair.DropboxRoot;
        object[] input = failure == "gone"
            ? [ListingChecks.Folder(root, "folder/X/a", "F1"), ListingChecks.Deleted(root, "folder/X/a"),
                ListingChecks.File(root, child.Path, id: "C"), ListingChecks.Folder(root, "folder/X", "X")]
            : [ListingChecks.File(root, child.Path, id: "C"), ListingChecks.Folder(root, "folder/X/a", "F1"),
                ListingChecks.Folder(root, "folder/X/a", "F2"), ListingChecks.Folder(root, "folder/X", "X")];
        using var fixture = new ListingChecks.Fixture(context.Pair,
            _ => ListingChecks.Page(true, "next", input[..^1]),
            _ => ListingChecks.Page(false, "end", input[^1]));
        Listed? current = null;
        var firstRead = false;
        var calls = new List<string>();
        var probe = new ProxySide(source)
        {
            ReadFault = path =>
            {
                if (!firstRead || current is not { Entry: { } entry } || current.Path != path) return null;
                firstRead = false;
                var occurrence = entry.Id;
                calls.Add(occurrence);
                return occurrence == failure ? new ItemFailedException(path, TextKey.ReadDropboxFailed, null) : null;
            }
        };
        context = context with { Dropbox = new RecordingSide(probe, () => false, [], Listing) };
        SyncOutcome? outcome = null;
        var cancelled = false;
        try { outcome = await Runner(context).ResolveAsync(issue, ResolutionChoice.UseDropbox, null, null, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        string[] expected = failure switch
        {
            "F1" or "cancel" => ["X", "F1"],
            "F2" => ["X", "F1", "F2"],
            "gone" => ["X", "C"],
            _ => ["X", "F1", "F2", "C"]
        };
        Check.Require(calls.SequenceEqual(expected) && cancelled == (failure == "cancel") && !Open(context, issue),
            "Real listing callbacks are occurrence-ordered: a failed folder gates later occurrences/children, not its closed root tie.");
        var failures = store.Issues(context.Pair.PairId).Where(item => item.Kind == IssueKind.Failure).ToArray();
        var failed = failure is "F1" or "F2";
        Check.Require(failed ? outcome?.Raised == IssueKind.Failure && failures.Length == 1 &&
            failures[0].Path == "folder/X/a" : failures.Length == 0,
            "A failed folder retains one visible failure rather than acknowledging its subtree.");
        Check.Require((await context.Local.ReadOneAsync(child.Path, default) is not null) == (failure is "none" or "gone") &&
            (store.SyncedByKey(context.Pair.PairId, Side.Dropbox, PathNames.Key(child.Path)) is not null) == (failure is "none" or "gone"),
            "The gated or cancelled child remains uncopied and unsynced; successful callbacks settle it.");
        if (failure == "F2")
        {
            // Retry uses the current provider subtree, not the failed listing occurrence stream.
            var retryContext = context with { Dropbox = source, Store = new RepairedStore(store) };
            var ports = new SyncPorts(PassthroughRecoveryBoundary.Instance, RefusingExecutionBoundary.Instance,
                new TestSelectedBoundary(retryContext), 0);
            ports = ports with
            {
                RunBatchAsync = (selection, side, scope, retries, settles, ct) =>
                    SyncJobs.RunBatchAsync(retryContext, ports, selection, scope, ct, side, retries, settles)
            };
            await new Syncer(retryContext, ports).ResolveAsync(failures.Single(), ResolutionChoice.Retry,
                null, null, default);
            Check.Require(await context.Local.ReadOneAsync(child.Path, default) is not null &&
                store.SyncedByKey(context.Pair.PairId, Side.Dropbox, PathNames.Key(child.Path)) is not null &&
                !store.Issues(context.Pair.PairId).Any(item => item.Kind == IssueKind.Failure),
                "actual Retry after the second same-path folder occurrence copies and records the gated child and clears failure");
        }

        async IAsyncEnumerable<Listed> Listing(string under, ListOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var listed in fixture.Listing.ReadAll(under, options, ListingChecks.BeginListing(), ct))
            {
                current = listed;
                firstRead = listed.Entry is not null;
                yield return listed;
                if (failure == "cancel" && listed.Entry?.Id == "F1") cancellation.Cancel();
            }
            current = null;
        }
    }

    private static async Task FolderFailuresAsync(Side side, IssueKind kind, string failure)
    {
        var context = Context();
        using var store = context.Store;
        using var cancellation = new CancellationTokenSource();
        var source = context.For(side);
        var destination = context.For(PathNames.Other(side));
        var folder = await source.EnsureFolderAsync("folder", CancellationToken.None);
        await PutAsync(source, "folder/a", "first");
        await source.EnsureFolderAsync("folder/nested", CancellationToken.None);
        await PutAsync(source, "folder/nested/child", "dependent");
        await PutAsync(source, "folder/z", "last");
        var occupant = await PutAsync(destination, folder.Path, "occupant");
        var issue = Raise(context, kind, kind == IssueKind.Tie ? TextKey.TieKindBoth : TextKey.ClashFileFolder,
            side == Side.Local ? folder : occupant, side == Side.Local ? occupant : folder, null);
        var replacing = new ReplacingSide(destination, occupant, false, true, failure == "root");
        context = side == Side.Local ? context with { Dropbox = replacing } : context with { Local = replacing };
        if (failure is "folder/a" or "folder/nested" or "folder/z")
            ((FakeSide)source).FailNext(failure, new ItemFailedException(failure, TextKey.PublishFailed, null));
        var effects = new List<RunEvent>();
        context = context with
        {
            Events = entry =>
            {
                if (entry.Kind != RunEventKind.Effect) return;
                if (entry.Path == folder.Path)
                    Check.Require(Open(context, issue), "The root choice commits before its conflict closes.");
                else
                    Check.Require(effects.Any(effect => Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What == EffectKind.Replaced.ToString()),
                        "A child starts only after the root replacement effect. side=" + side + " kind=" + kind +
                        " failure=" + failure + " path=" + entry.Path);
                if (failure == "cancel" && entry.Path == "folder/nested") cancellation.Cancel();
                effects.Add(entry);
            }
        };
        var visits = new List<(Side Side, string Path)>();
        context = context with
        {
            Local = new RecordingSide(context.Local, () => !Open(context, issue), visits),
            Dropbox = new RecordingSide(context.Dropbox, () => !Open(context, issue), visits)
        };
        var runner = Runner(context);
        var choice = kind == IssueKind.Tie
            ? side == Side.Local ? ResolutionChoice.UseLocal : ResolutionChoice.UseDropbox
            : side == Side.Local ? ResolutionChoice.KeepLocal : ResolutionChoice.KeepDropbox;
        SyncOutcome? result = null;
        var cancelled = false;
        try { result = await runner.ResolveAsync(issue, choice, null, null, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check.Require(Open(context, issue) == (failure == "root"),
            "Only a failed root choice retains the root conflict; child failure and cancellation do not revive it.");
        var expected = failure == "root" ? Array.Empty<string>() : failure == "cancel"
            ? new[] { "folder/a", "folder/nested" } : failure == "folder/nested"
            ? new[] { "folder/a", "folder/nested", "folder/z" }
            : new[] { "folder/a", "folder/nested", "folder/nested/child", "folder/z" };
        Check.Require(Visited(visits, [.. expected.Select(path => (side, path))]) &&
            (failure != "folder/nested" || !visits.Any(visit => visit.Path == "folder/nested/child")) &&
            (failure != "cancel" || !visits.Any(visit => visit.Path is "folder/z" or "folder/nested/child")),
            "First, middle and last child failures keep independent siblings eligible but suppress dependent descendants. visits=" +
            string.Join("|", visits.Select(visit => visit.Side + ":" + visit.Path)));
        Check.Require(cancelled == (failure == "cancel") &&
            (cancelled ? result is null : result?.Raised == IssueKind.Failure),
            "Cancellation propagates and every item failure remains visible in the returned outcome.");
        var failures = store.Issues(context.Pair.PairId).Where(item => item.Kind == IssueKind.Failure).ToArray();
        Check.Require(cancelled ? failures.Length == 0 : failures.Length == 1 &&
            failures[0].Path == (failure == "root" ? folder.Path : failure),
            "A failed child owns its Failure; no second Failure is attached to the resolved root.");
        Check.Require(failure == "root"
                ? effects.Count == 0 && await destination.ReadOneAsync(folder.Path, CancellationToken.None) == occupant
                : (await destination.ReadOneAsync(folder.Path, CancellationToken.None))?.IsFolder == true,
            "A refused root deletion changes nothing; later failure preserves the completed root replacement.");
    }

    private static async Task MoveFollowUpsAsync(Side side, bool folder, bool sameSide, int failedRoot, bool throws,
        bool failListing = false)
    {
        var context = Context();
        using var store = context.Store;
        var source = context.For(side);
        var moved = folder ? await source.EnsureFolderAsync("source", CancellationToken.None)
            : await PutAsync(source, "source", "incoming");
        if (folder)
        {
            if (failListing) await PutAsync(source, "source/a", "first");
            await PutAsync(source, "source/excluded", "excluded");
            await PutAsync(source, "source/held", "held");
            await PutAsync(source, "source/z", "independent");
        }
        var occupantSide = sameSide ? side : PathNames.Other(side);
        var counterpart = (FakeSide)context.For(PathNames.Other(side));
        var occupant = await PutAsync(context.For(occupantSide), "taken", "occupant");
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, occupant.Path,
                new IssueSubject([new(side, moved.Path, moved.Id),
                    new(occupantSide, occupant.Path, occupant.Id)], side))]
        });
        var issue = store.Issues(context.Pair.PairId).Single();
        var target = side == Side.Dropbox ? "renamed:name" : "renamed";
        if (folder)
        {
            var held = (await source.ReadOneAsync("source/held", CancellationToken.None))!;
            store.Commit(context.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Tie, TextKey.TieEqualTimes, target + "/held",
                    new IssueSubject([new(side, target + "/held", held.Id)], null))]
            });
            var pair = context.Pair with { Exclusions = [new Exclusion(side, target + "/excluded", true)] };
            context = context with { Pair = pair, Scope = new PathScope(pair, [pair]) };
        }
        if (failListing)
        {
            var failing = new ReplacingSide(source, occupant, false, failListing: true);
            context = side == Side.Local ? context with { Local = failing } : context with { Dropbox = failing };
        }
        var visits = new List<(Side Side, string Path)>();
        var effects = new List<RunEvent>();
        context = context with
        {
            Events = entry =>
            {
                if (entry.Kind != RunEventKind.Effect) return;
                if (effects.Count == 0)
                    Check.Require(Open(context, issue) && visits.Count == 0,
                        "The move commits before root closure and before either follow-up.");
                else
                    Check.Require(!Open(context, issue), "Follow-up effects occur after the selected notice closes.");
                effects.Add(entry);
                // The follow-up fault is armed only after the root move, which itself touches the same paths.
                if (entry.Path == target && failedRoot == 1)
                    ((FakeSide)source).FailNext(target, new ItemFailedException(target, TextKey.PublishFailed, null));
                if (entry.Path == target && failedRoot == 2)
                    counterpart.FailNext(moved.Path,
                        new ItemFailedException(moved.Path, TextKey.PublishFailed, null));
            }
        };
        context = context with
        {
            Local = new RecordingSide(context.Local, () => !Open(context, issue), visits),
            Dropbox = new RecordingSide(context.Dropbox, () => !Open(context, issue), visits)
        };
        var outcome = await Runner(context)
            .ResolveAsync(issue, ResolutionChoice.RenameIncoming, target, null, CancellationToken.None);
        var expected = new List<(Side, string)> { (side, target) };
        if (failListing) expected.Add((side, target + "/a"));
        else if (folder && failedRoot != 1)
            expected.AddRange(new[] { (side, target + "/excluded"), (side, target + "/held"), (side, target + "/z") });
        expected.Add((PathNames.Other(side), moved.Path));
        Check.Require(Visited(visits, [.. expected]),
            "File and folder moves visit both exact roots; failure or held children do not suppress independent work. visits=" +
            string.Join("|", visits.Select(visit => visit.Side + ":" + visit.Path)));
        Check.Require(!Open(context, issue) && effects.Count != 0 && effects[0].Path == target &&
            (await source.ReadOneAsync(target, CancellationToken.None))?.Id == moved.Id &&
            await context.For(occupantSide).ReadOneAsync(occupant.Path, CancellationToken.None) == occupant,
            "Follow-up failure never undoes the move, changes the occupant or reopens the root clash.");
        if (folder && !failListing && failedRoot != 1)
        {
            var destinationRoot = PathNames.LocalSpelling(target);
            Check.Require(await counterpart.ReadOneAsync(destinationRoot + "/held", CancellationToken.None) is null &&
                await counterpart.ReadOneAsync(destinationRoot + "/excluded", CancellationToken.None) is null &&
                await counterpart.ReadOneAsync(destinationRoot + "/z", CancellationToken.None) is not null,
                "Real held and excluded children stay uncopied while the independent child completes.");
        }
        Check.Require(failedRoot == 0 && !failListing ? outcome.Raised is null : outcome.Raised == IssueKind.Failure,
            "Either failed follow-up is returned even if a later independent follow-up succeeds.");
        if (throws || failListing)
        {
            var failures = store.Issues(context.Pair.PairId).Where(item => item.Kind == IssueKind.Failure).ToArray();
            Check.Require(failures.Length == 1 && failures[0].Path == (failedRoot == 1 || failListing ? target : moved.Path),
                "Thrown follow-up failures belong to the attempted path, not the old clash path.");
            if (failListing) Check.Require(failures[0].Cause == Text.ReadFailedOn(side),
                "An incomplete descendant listing records the source side's read failure at the folder.");
        }
    }

    private static async Task SamePathFollowUpAsync(Side side)
    {
        var context = Context();
        using var store = context.Store;
        // The two sides hold matching content, so the real follow-ups settle while the paths stay the subject.
        var local = await PutAsync(context.Local, "item", "same");
        var dropbox = await PutAsync(context.Dropbox, "item", "same");
        var issue = Raise(context, IssueKind.NameClash, TextKey.ClashNameTaken, local, dropbox, side);
        var visits = new List<(Side Side, string Path)>();
        context = context with
        {
            Local = new RecordingSide(context.Local, () => !Open(context, issue), visits),
            Dropbox = new RecordingSide(context.Dropbox, () => !Open(context, issue), visits)
        };
        var outcome = await Runner(context)
            .ResolveAsync(issue, ResolutionChoice.RenameIncoming, "item", null, CancellationToken.None);
        // Both follow-ups land on the same path from their distinct sides; each side is read, in either order.
        Check.Require(outcome.Raised is null && !Open(context, issue) &&
            visits.Any(visit => visit == (side, "item")) &&
            visits.Any(visit => visit == (PathNames.Other(side), "item")),
            "Identical destination and original paths retain both follow-ups on their distinct sides. visits=" +
            string.Join("|", visits.Select(visit => visit.Side + ":" + visit.Path)));
        Check.Require(await context.Local.ReadOneAsync(local.Path, CancellationToken.None) == local &&
            await context.Dropbox.ReadOneAsync(dropbox.Path, CancellationToken.None) == dropbox,
            "A same-path rename preserves both observed items.");
    }

    /// <summary>True when the expected follow-ups were visited in order, among the counterpart reads each one makes.</summary>
    private static bool Visited(List<(Side Side, string Path)> visits, params (Side Side, string Path)[] expected)
    {
        if (expected.Length == 0) return visits.Count == 0;
        var index = 0;
        foreach (var visit in visits)
            if (index < expected.Length && visit == expected[index]) index++;
        return index == expected.Length;
    }

    private sealed class ReplacingSide(ISide inner, Entry occupant, bool failStage, bool replaceKind = false,
        bool failDelete = false, bool failListing = false, bool failEnsure = false,
        Func<string, Exception?>? stageFault = null, RemovalResult? publishResult = null) : ISide
    {
        public int Publications { get; private set; }
        public List<Entry> Deleted { get; } = [];
        public List<IReadOnlyList<FolderRemovalEffect>> Submitted { get; } = [];
        public Exception? Failure { get; private set; }
        public Side Side => inner.Side;
        public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var listed in inner.ReadAllAsync(under, options, ct))
            {
                yield return listed;
                if (failListing) throw new InventoryIncompleteException("Listing failed after its first child.", null);
            }
        }
        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => inner.ReadOneAsync(path, ct);
        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            inner.OpenReadAsync(entry, attempt, ct);
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);
        public async Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
        {
            // A local replacement validates its stage before removing the occupant; Dropbox removes first. A
            // follow-up descendant carries no occupant, so the boundary check applies only at the occupant's path.
            if (path == occupant.Path)
                Check.Require(await inner.ReadOneAsync(path, ct) == (replaceKind && Side == Side.Dropbox ? null : occupant),
                    "A Dropbox kind replacement deletes before staging; a local replacement keeps the occupant until its stage validates.");
            if (stageFault?.Invoke(path) is { } fault) { Failure = fault; throw fault; }
            if (failStage)
            {
                Failure = Side == Side.Dropbox
                    ? DropboxErrors.Map(DropboxProtocol.Upload,
                        new DropboxApiException(HttpStatusCode.BadRequest, "invalid_revision/"), false, path).Error
                    : new ItemFailedException(path, TextKey.PublishFailed, new IOException("Local publish refused"));
                throw Failure;
            }
            return await inner.StageAsync(path, content, source, ct);
        }
        public async Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct)
        {
            // The Local adapter recycles an expected folder into the bin at publish time (LocalSide.PublishAsync),
            // after the stage is closed and verified; the transfer passes the observed occupant, not a null.
            if (Side == Side.Local && expected is { IsFolder: true })
            {
                var removed = await DeleteAsync(expected, null, ct);
                if (removed.Outcome != RemovalOutcome.Removed) return removed;
                expected = null;
            }
            Check.Require(expected == (replaceKind ? null : occupant),
                "Kind replacement publishes create-only; same-kind replacement expects the exact observed occupant.");
            Publications++;
            return publishResult ?? await inner.PublishAsync(staged, expected, selection, ct);
        }
        public async Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
        {
            if (path == occupant.Path)
                Check.Require(replaceKind &&
                    (Deleted.SequenceEqual(new[] { occupant }) || Submitted.Any(effects =>
                        effects.Any(effect => effect.Expected == occupant))) &&
                    await inner.ReadOneAsync(path, ct) is null,
                    "Kind replacement removes its exact occupant (folder receipt or the side's delete) before creating the folder.");
            if (failEnsure) throw new ItemFailedException(path, TextKey.PublishFailed, null);
            return await inner.EnsureFolderAsync(path, ct);
        }
        public async Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
        {
            Check.Require(replaceKind && expected == occupant && Deleted.Count == 0,
                "Only a kind change deletes, once, through the provider's existing safe deletion method.");
            if (failDelete) throw new ItemFailedException(expected.Path, TextKey.PublishFailed, null);
            var result = await inner.DeleteAsync(expected, selection, ct);
            Deleted.Add(expected);
            return result;
        }
        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) => inner.MoveAsync(expected, newPath, ct);
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
        {
            Submitted.Add(effects);
            if (failDelete)
            {
                // A refused send with no proof of no effect: the attempt is completed as an uncertain write, so the
                // operation stays Unknown with its provider cause rather than retiring as a known no-effect.
                var error = new ItemFailedException(effects.Count == 0 ? string.Empty :
                    effects[0].Expected?.Path ?? string.Empty, TextKey.DropboxRefused, null);
                attempt.TryComplete(RequestResult.UnknownWrite, error, null, noEffectProven: false);
                throw error;
            }
            return inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        }
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct) =>
            inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }

    private static ExecutorContext Context(IAdmission? admission = null)
    {
        var pair = SourceFixture.Bound(new PairConfig("pair", "Pair A", Path.GetTempPath(), "/remote", "account", [], [],
            FirstRunPolicy.NewestWins, true, null));
        using var stream = typeof(EngineSettings).Assembly.GetManifestResourceStream(ResourceNames.Settings)!;
        using var reader = new StreamReader(stream);
        var settings = SettingsParser.Parse(reader.ReadToEnd(), null);
        var store = StateStore.Open(Path.Combine(StorageRoot.Value!, Path.GetRandomFileName()));
        store.SavePair(pair);
        // A real follow-up over a saved Local file reads its dirty evidence, which needs the source boundary.
        SourceFixture.AcquireJournal(store, pair);
        var local = new FakeSide(Side.Local);
        return new ExecutorContext(
            Local: local, Dropbox: new FakeSide(Side.Dropbox),
            Recovery: new FakeRecovery { Evidence = new DriveEvidence(1, null, null), Hash = local.ContentHashAsync },
            Store: store, Pair: pair, Scope: new PathScope(pair, [pair]), Settings: settings,
            Runtime: new ReplaceTurn(),
            Admission: admission ?? new AdaptiveLimiter(1, 1, TimeProvider.System, new FakeNetworkSignal(),
                _ => { }, settings.Backoff),
            Priority: false, Events: _ => { },
            ReserveDownloadAsync: (_, _, _, _) => ValueTask.FromResult<IAsyncDisposable>(NoReservation.Instance),
            Time: TimeProvider.System,
            ReadDeletionEvidenceAsync: (path, _, _) => Task.FromResult<DeletionEvidence>(
                new DeletionEvidence.Unknown(path, TextKey.FailChanged)),
            BeginDownloadAttempt: source => new RequestAttempt(source, AttemptOwner.Transfer),
            CompleteDownloadAttemptAsync: (_, _) => Task.FromResult(false),
            UploadAdmissionIdentity: (AccountId: "account", RouteName: "route", SourceIdentity: "source"),
            ItemAttempts: new PermittedItemAttempts(), Opportunity: new object(), ExpectedGeneration: 0)
        {
            CaptureFolderCursorAsync = _ => Task.FromResult("fixture-cursor"),
            ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
        };
    }

    // A checked Dropbox folder removal needs its post-window feed; these fixtures remove nothing unexpectedly.
    private static async IAsyncEnumerable<Listed> NoFolderChangesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    private static ResolutionRunner Runner(ExecutorContext context) => Runner(context, new TestSelectedBoundary(context));

    private static ResolutionRunner Runner(ExecutorContext context, TestSelectedBoundary boundary)
    {
        var transfers = new Transfers(context);
        var keepBoth = new KeepBothRunner(context, transfers);
        var refusing = RefusingExecutionBoundary.Instance;
        var jobs = SyncJobs.Create(context, new SyncPorts(PassthroughRecoveryBoundary.Instance, refusing, boundary, 0));
        var executor = new ActionExecutor(context, transfers, new Parents(context), keepBoth);
        return new ResolutionRunner(context, executor, keepBoth, jobs.Items, boundary);
    }

    /// <summary>One selected root phase: the boundary admits the exact footprint, holds its lease only across the
    /// callback, and refuses before it when told to.</summary>
    private sealed class TestSelectedBoundary(ExecutorContext context, bool refuse = false) : ISelectedExecutionBoundary
    {
        public int Executions { get; private set; }
        public Issue? Expected { get; private set; }
        public IReadOnlyList<LiveEffectScope> Footprint { get; private set; } = [];
        public int RequireCalls { get; internal set; }
        public long RequiredGeneration { get; internal set; } = -1;
        public IReadOnlyList<string> RequiredKeys { get; internal set; } = [];

        public async Task<T> ExecuteAsync<T>(Issue expected, IReadOnlyList<LiveEffectScope> footprint,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            Executions++;
            Expected = expected;
            Footprint = footprint;
            if (refuse) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            var current = context.Store.IssueById(context.Pair.PairId, expected.Id);
            if (current is null || current with { Subject = expected.Subject } != expected ||
                !current.Subject.HasSameItems(expected.Subject))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            await using var locks = await PathLocks.Shared.EnterAsync(
                LockKeys.ForScopes(context.Pair, footprint), ct);
            await using var permit = await context.Admission.AdmitAsync(context.Priority, ct);
            return await execute(new TestExecution(context, this, context.ExpectedGeneration), ct);
        }
    }

    /// <summary>The borrowed lease inside one selected phase; it verifies pair/generation and records the footprint.</summary>
    private sealed class TestExecution(ExecutorContext context, TestSelectedBoundary boundary, long generation)
        : IAdmittedExecution
    {
        public void Require(string pairId, long requiredGeneration, IReadOnlyList<string> requiredLockKeys)
        {
            if (pairId != context.Pair.PairId || requiredGeneration != generation)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            boundary.RequireCalls++;
            boundary.RequiredGeneration = requiredGeneration;
            boundary.RequiredKeys = requiredLockKeys;
        }

        public void RequireScopes(string pairId, long requiredGeneration, IReadOnlyList<PathLockScope> scopes) =>
            Require(pairId, requiredGeneration, scopes.Select(scope => scope.Key).ToArray());

        public void Revalidate() { }

        public ValueTask ReleaseAdmissionAsync() => throw new NotSupportedException(nameof(ReleaseAdmissionAsync));

        public ValueTask DisposeAsync() => default;
    }

    // The real admission seam's folder removal runs inside a lease; these fixtures run the callback directly,
    // as FolderDeleteRunnerChecks' passthrough boundary does.
    private sealed class PassthroughRecoveryBoundary : IRecoveryExecutionBoundary
    {
        internal static readonly PassthroughRecoveryBoundary Instance = new();

        public void RequireCurrent() { }

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            ExecuteAsync(operation, execute, ct);

        public Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            RunAsync(execute, ct);

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            RunAsync(execute, ct);

        private static async Task<T> RunAsync<T>(Func<IAdmittedExecution, CancellationToken, Task<T>> execute,
            CancellationToken ct)
        {
            await using var lease = new Lease();
            return await execute(lease, ct);
        }

        private sealed class Lease : IAdmittedExecution
        {
            public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys) { }
            public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes) { }
            public void Revalidate() { }
            public ValueTask ReleaseAdmissionAsync() => default;
            public ValueTask DisposeAsync() => default;
        }
    }

    /// <summary>Records every (side, path) read once the gate opens, so real follow-up visits replace the old fake syncPath.</summary>
    private sealed class RecordingSide(ISide inner, Func<bool> recording, List<(Side Side, string Path)> visits,
        Func<string, ListOptions, CancellationToken, IAsyncEnumerable<Listed>>? listing = null) : ISide
    {
        public Side Side => inner.Side;
        public List<(string From, string To)> Moves { get; } = [];

        private void Visit(string path)
        {
            if (recording()) visits.Add((Side, path));
        }

        public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Visit(under);
            await foreach (var listed in listing?.Invoke(under, options, ct) ?? inner.ReadAllAsync(under, options, ct))
                yield return listed;
        }

        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
        {
            Visit(path);
            return inner.ReadOneAsync(path, ct);
        }

        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            inner.OpenReadAsync(entry, attempt, ct);
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);
        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) =>
            inner.StageAsync(path, content, source, ct);
        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct) => inner.PublishAsync(staged, expected, selection, ct);
        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);
        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) =>
            inner.DeleteAsync(expected, selection, ct);
        public async Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
        {
            var moved = await inner.MoveAsync(expected, newPath, ct);
            Moves.Add((expected.Path, newPath));
            return moved;
        }
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct) =>
            inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }

    /// <summary>Returns the selected entry for the first read and a changed entry afterwards, so a mid-flight change
    /// is observed only by the removal's own re-observation.</summary>
    private sealed class RaceSide(ISide inner, string path, Entry after) : ISide
    {
        private int _reads;
        public Side Side => inner.Side;

        public async Task<Entry?> ReadOneAsync(string read, CancellationToken ct) =>
            read == path && _reads++ >= 1 ? after : await inner.ReadOneAsync(read, ct);
        public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var listed in inner.ReadAllAsync(under, options, ct)) yield return listed;
        }
        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            inner.OpenReadAsync(entry, attempt, ct);
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);
        public Task<IStaged> StageAsync(string path2, Stream content, Entry source, CancellationToken ct) =>
            inner.StageAsync(path2, content, source, ct);
        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct) => inner.PublishAsync(staged, expected, selection, ct);
        public Task<Entry> EnsureFolderAsync(string path2, CancellationToken ct) => inner.EnsureFolderAsync(path2, ct);
        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) =>
            inner.DeleteAsync(expected, selection, ct);
        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
            inner.MoveAsync(expected, newPath, ct);
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct) =>
            inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }

    /// <summary>Grants the fixture's registered opportunity and never fabricates a success for any other entry.</summary>
    private sealed class PermittedItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            if (opportunity is null) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            return execute(Guid.NewGuid(), ct);
        }

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null, Action<Issue>? committedIssue = null) => throw new NotSupportedException();

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null) =>
            throw new NotSupportedException();
    }

    private sealed class NoReservation : IAsyncDisposable
    {
        public static readonly NoReservation Instance = new();
        public ValueTask DisposeAsync() => default;
    }

    private static Issue Raise(ExecutorContext context, IssueKind kind, TextKey cause, Entry local, Entry dropbox, Side? incoming)
    {
        context.Store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Raise = [(kind, cause, local.Path, new IssueSubject(
                [new(Side.Local, local.Path, local.Id), new(Side.Dropbox, dropbox.Path, dropbox.Id)], incoming))]
        });
        return context.Store.Issues(context.Pair.PairId).Single(issue => issue.Kind == kind);
    }

    // A real store reuses a freed row id for a later issue, so an issue is still open only by its exact identity.
    private static bool Open(ExecutorContext context, Issue issue) =>
        context.Store.Issues(context.Pair.PairId).Any(item =>
            item.Id == issue.Id && item.Kind == issue.Kind && item.Path == issue.Path);

    private static async Task<Entry> PutAsync(ISide side, string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var source = new Entry(path, false, bytes.LongLength, DateTimeOffset.UnixEpoch, "", "", "", null);
        using var content = new MemoryStream(bytes);
        await using var staged = await side.StageAsync(path, content, source, CancellationToken.None);
        return (await side.PublishAsync(staged, null, null, CancellationToken.None)).Final!;
    }
}
