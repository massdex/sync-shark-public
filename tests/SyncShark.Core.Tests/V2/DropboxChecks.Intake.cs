using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Focused intake invariants: the delta collapse is keyed/linear, one poison entry is scoped, and two
/// live identities at one case-insensitive path are never silently collapsed. Plus the RQ-052
/// answered-run fact that the held-evidence owner counts (the budget itself is SyncRun-owned).
/// </summary>
internal static partial class DropboxChecks
{
    private static async Task IntakeChecksAsync()
    {
        await IntakeCollapseLinearAsync();
        await IntakeListingConcurrencyAsync();
        await IntakeFullPoisonScopedAsync();
        await IntakeFullCollisionIdentitiesAsync();
        await IntakeFullExactSpellingCollisionAsync();
        await IntakeFullCrossPageCollisionAsync();
        await IntakeFullSameIdentityRenameAsync();
        await IntakeFullSameIdentityDuplicateAsync();
        await IntakeDeltaReplacementIdentityAsync();
        await IntakeDeltaExactSpellingReplacementAsync();
        await IntakeDeltaSameIdentityRenameAsync();
        await IntakeCatchupReplacementAsync();
        await IntakeHistoryTransientRetryAsync();
        await IntakeHistoryAnsweredFactAsync();
        await IntakeHistoryPermanentUnansweredDeliveredAsync();
        await IntakeOversizedSessionErrorRemainsUncertainAsync();
        await IntakeHistoryRetrySurvivesSqliteReopenAsync();
    }

    private static async Task IntakeOversizedSessionErrorRemainsUncertainAsync()
    {
        using var h = new Harness();
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
        {
            Content = new ByteArrayContent(new byte[DropboxApiLimits.ErrorBodyMaxBytes + 1])
        };
        var failure = typeof(DropboxPilotClient).GetMethod("ThrowUploadSessionFailureAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Exception? observed = null;
        try
        {
            await (Task)failure.Invoke(h.Client, [response, "Submitted session could not be confirmed.", CancellationToken.None])!;
        }
        catch (Exception error) { observed = error; }
        Require(observed is DropboxEffectUncertainException { InnerException: InvalidDataException },
            "an oversized post-transmit session refusal remains uncertain, never safe to replay");
    }

    // C27/F4: the old list scan was quadratic (FindIndex/RemoveAll over every pending change). A feed of
    // Include the production SQLite descendant lookup rather than measuring only an empty fake.
    private static Task IntakeCollapseLinearAsync()
    {
        using var h = new Harness();
        var state = Path.Combine(_root, "collapse-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        store.SavePair(new PairDef("p1", "collapse fixture",
            new RootDef("ra", SyncProviders.Local, null, state, "id:ra"), Root(), [], [], true));
        var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, "p1");
        var collapse = typeof(DropboxInventory).GetMethod("Collapse", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The keyed collapse seam is missing.");
        const int count = 10_000;
        var feed = new List<DropboxChange>(count);
        for (var index = 0; index < count; index++)
        {
            var path = $"bulk/{index:D6}.txt";
            feed.Add(new DropboxChange(path, FileEntry(path, $"id:bulk-{index}")));
        }
        var watch = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var collapsed = (List<DropboxChange>)collapse.Invoke(inventory, [feed])!;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        watch.Stop();
        Require(collapsed.Count == count && collapsed.Select(change => change.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == count,
            "ten thousand unrelated upserts survive exactly once");
        Require(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"keyed collapse with SQLite stays within the fast bound ({count} entries in {watch.ElapsedMilliseconds} ms)");
        Console.WriteLine($"PASS keyed delta collapse: items={count}; elapsed-ms={watch.ElapsedMilliseconds}; allocated-bytes={allocated}");
        return Task.CompletedTask;
    }

    // C31/F3: one unreadable live entry is scoped with a real reason; healthy siblings still list and
    // the path is never inferred absent. The old behavior failed the whole root.
    private static async Task IntakeFullPoisonScopedAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFile("healthy.txt", "bytes");
        h.Wire.SeedListingRaw(new Dictionary<string, object>
        {
            [".tag"] = "file",
            ["name"] = "poison.txt",
            ["path_display"] = "/Pilot/poison.txt",
            ["path_lower"] = "/pilot/poison.txt"
            // no id/rev/size: malformed metadata, not a representable live item
        });
        var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        Require(snapshot.Items.Select(item => item.Path).SequenceEqual(["healthy.txt"]),
            "the healthy sibling is still published beside a malformed entry");
        Require(snapshot.IncompleteSubtrees.SequenceEqual(["poison.txt"]) &&
            snapshot.IncompleteReasons.TryGetValue("poison.txt", out var reason) &&
            reason.Contains("metadata", StringComparison.OrdinalIgnoreCase) &&
            reason.Contains("Nothing at this path", StringComparison.Ordinal),
            "the malformed entry is scoped with its exact real reason, not a whole-root failure");
        Require(!snapshot.IncompleteSubtrees.Contains(string.Empty),
            "a known bad entry does not make the root incomplete");
        Console.WriteLine("PASS malformed listing entry is scoped");
    }

    // C32/F9: two different live ids at one case-insensitive path are neither published nor selected.
    private static async Task IntakeFullCollisionIdentitiesAsync()
    {
        using var h = new Harness();
        h.Wire.SeedListingRaw(RawFileEntry("Clash.txt", "id:clash-a"));
        h.Wire.SeedListingRaw(RawFileEntry("clash.txt", "id:clash-b"));
        var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        Require(snapshot.Items.All(item => !PairPath.Same(item.Path, "clash.txt")),
            "distinct live identities at one path are never silently collapsed into a publisher");
        Require(snapshot.IncompleteSubtrees.Count == 1 &&
            PairPath.Same(snapshot.IncompleteSubtrees[0], "clash.txt") &&
            snapshot.IncompleteReasons.Values.Single().Contains("two different items", StringComparison.Ordinal),
            "both spellings remain protected under one exact collision reason");
        Console.WriteLine("PASS distinct live identities stay protected");
    }

    // C32 review: two different ids at the EXACT same spelling are also a collision, not last-wins.
    private static async Task IntakeFullExactSpellingCollisionAsync()
    {
        using var h = new Harness();
        h.Wire.SeedListingRaw(RawFileEntry("same.txt", "id:same-a", "rev-a"));
        h.Wire.SeedListingRaw(RawFileEntry("same.txt", "id:same-b", "rev-b"));
        var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        Require(snapshot.Items.All(item => item.Path != "same.txt"),
            "an exact-spelling duplicate with a different id never silently overwrites the first object");
        Require(snapshot.IncompleteSubtrees.SequenceEqual(["same.txt"]) &&
            snapshot.IncompleteReasons["same.txt"].Contains("two different items", StringComparison.Ordinal),
            "the exact-spelling collision is scoped with the collision reason");
        Console.WriteLine("PASS exact-spelling distinct identities collide");
    }

    // C32 review: the two colliding entries can arrive in different listing pages (MergeLocal boundary).
    private static async Task IntakeFullCrossPageCollisionAsync()
    {
        using var h = new Harness();
        h.Wire.DeletedPageSize = 1; // the full walk asks include_deleted=true, so each page holds one entry
        h.Wire.SeedListingRaw(RawFileEntry("page.txt", "id:page-a", "rev-a"));
        h.Wire.SeedListingRaw(RawFileEntry("page.txt", "id:page-b", "rev-b"));
        var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        Require(snapshot.Items.All(item => item.Path != "page.txt"),
            "a cross-page exact-spelling collision is not collapsed to whichever page merged last");
        Require(snapshot.IncompleteSubtrees.SequenceEqual(["page.txt"]) &&
            snapshot.IncompleteReasons["page.txt"].Contains("two different items", StringComparison.Ordinal),
            "the cross-page collision is scoped with the collision reason");
        Console.WriteLine("PASS cross-page distinct identities collide");
    }

    // C32: repeating the same id/spelling keeps one row and is not a collision.
    private static async Task IntakeFullSameIdentityDuplicateAsync()
    {
        using var h = new Harness();
        h.Wire.SeedListingRaw(RawFileEntry("Dup.txt", "id:dup", "rev-a"));
        h.Wire.SeedListingRaw(RawFileEntry("Dup.txt", "id:dup", "rev-b"));
        var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        Require(snapshot.Items.Count == 1 && snapshot.Items[0].Path == "Dup.txt" &&
            snapshot.Items[0].ObjectId == "id:dup" && snapshot.IncompleteSubtrees.Count == 0,
            "an exact duplicate of one object preserves one row and is not a collision");
        Console.WriteLine("PASS same-identity duplicate preserves one row");
    }

    // C32: a case-only rename of ONE object is the same identity and updates the stored spelling.
    private static async Task IntakeFullSameIdentityRenameAsync()
    {
        using var h = new Harness();
        h.Wire.SeedListingRaw(RawFileEntry("Rename.txt", "id:rename", "rev-1"));
        h.Wire.SeedListingRaw(RawFileEntry("rename.txt", "id:rename", "rev-2"));
        var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        var item = snapshot.Items.Single();
        Require(item.Path == "rename.txt" && item.ObjectId == "id:rename",
            "the later spelling of the same object is its current name");
        Require(snapshot.IncompleteSubtrees.Count == 0,
            "a same-identity rename is not a collision");
        Console.WriteLine("PASS same-identity rename updates the spelling");
    }

    // C32 ordered-feed semantics: the change feed is ordered, so a later different id at one
    // case-insensitive path is a sequential replacement. Its last observation wins, the prior id is
    // never inferred deleted, and no collision (or rename-one notice) is raised.
    private static async Task IntakeDeltaReplacementIdentityAsync()
    {
        using var h = new Harness();
        h.Wire.SeedChangePage("identity-start",
            [h.Wire.ChangeFile("Name.txt", "one"), h.Wire.ChangeFile("name.txt", "two")], false, "identity-end");
        var delta = await h.Inventory.ChangesSinceAsync("identity-start", 1, _ => true, CancellationToken.None);
        Require(delta.Changes.Count == 1 && delta.Changes[0].Kind == ChangeKind.Upsert &&
            delta.Changes[0].Path == "name.txt",
            "a later different id at one ordered-feed path is its last observation");
        Require(delta.Changes.All(change => change.Kind != ChangeKind.Delete),
            "the replaced prior id is never inferred deleted");
        Require(delta.IncompleteReasons.Count == 0 && delta.IncompleteSubtrees.Count == 0 &&
            !h.Known.OpenNotices("p1").Any(),
            "an ordered-feed replacement is not a collision and raises no notice");
        Console.WriteLine("PASS delta different-id replacement keeps the last observation");
    }

    // The same exact spelling with two different ids is also a replacement in the ordered feed.
    private static async Task IntakeDeltaExactSpellingReplacementAsync()
    {
        using var h = new Harness();
        h.Wire.SeedChangePage("exact-start",
            [RawFileChange("same.txt", "id:exact-a", "rev-a"), RawFileChange("same.txt", "id:exact-b", "rev-b")],
            false, "exact-end");
        var delta = await h.Inventory.ChangesSinceAsync("exact-start", 1, _ => true, CancellationToken.None);
        Require(delta.Changes.Count == 1 && delta.Changes[0].Kind == ChangeKind.Upsert &&
            delta.Changes[0].Path == "same.txt" && delta.Changes[0].ObjectId == "id:exact-b",
            "the exact-spelling ordered feed keeps only its last observation");
        Require(delta.Changes.All(change => change.Kind != ChangeKind.Delete) &&
            delta.IncompleteReasons.Count == 0 && delta.IncompleteSubtrees.Count == 0,
            "an exact-spelling ordered replacement is not a collision and infers no deletion");
        Console.WriteLine("PASS delta exact-spelling replacement keeps the last observation");
    }

    private static async Task IntakeDeltaSameIdentityRenameAsync()
    {
        using var h = new Harness();
        h.Wire.SeedChangePage("rename-start",
            [RawFileChange("Rename.txt", "id:rename", "rev-1"), RawFileChange("rename.txt", "id:rename", "rev-2")],
            false, "rename-end");
        var delta = await h.Inventory.ChangesSinceAsync("rename-start", 1, _ => true, CancellationToken.None);
        Require(delta.IncompleteReasons.Count == 0 && delta.Changes.Count == 1 &&
            delta.Changes[0].Kind == ChangeKind.Upsert && delta.Changes[0].Path == "rename.txt" &&
            delta.Changes[0].ObjectId == "id:rename",
            "a same-identity delta rename keeps the latest spelling and one observation");
        Console.WriteLine("PASS delta same-identity rename keeps the latest spelling");
    }

    // C32 ordered-feed semantics on the full-walk catch-up: a different id replayed at one path is
    // that path's last observation (a replacement), not a collision, and the prior id is not deleted.
    private static async Task IntakeCatchupReplacementAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFile("swap.txt", "before");
        h.Wire.SeedChangePage(h.Wire.LatestCursor,
            [RawFileEntry("swap.txt", "id:swap-new", "rev-new")], false, "catchup-end");
        var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        var item = snapshot.Items.Single();
        Require(item.Path == "swap.txt" && item.ObjectId == "id:swap-new",
            "the ordered catch-up keeps the later different id as the path's last observation");
        Require(snapshot.IncompleteSubtrees.Count == 0 && snapshot.IncompleteReasons.Count == 0,
            "a different id in the ordered catch-up is a replacement, not a collision");
        Require(h.Known.ReadDeletionClassification(Root()) is { } classification &&
            !classification.Exact.Contains("swap.txt"),
            "the replaced prior id is never inferred deleted");
        Console.WriteLine("PASS ordered catch-up different-id replacement");
    }

    // RQ-052 / C32 held lane: an exception-shaped permanent history failure (permanent, unanswered)
    // answers the pass so the restored hold can count it and reach its decision; a transient timeout
    // stays unanswered for the retry ladder.
    private static async Task IntakeHistoryPermanentUnansweredDeliveredAsync()
    {
        using var h = new Harness();
        h.Wire.SeedDeletedEntry("held.txt");
        h.Wire.OnRevisionAsync = _ => throw new InvalidDataException("permanent response shape");
        _ = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        var callbacks = new List<SyncRemoteDeletionEvidence>();
        await h.Inventory.ResolveDeletionEvidenceAsync(["held.txt"], (_, evidence) => callbacks.Add(evidence),
            CancellationToken.None);
        Require(callbacks.Count == 1 &&
            callbacks[0] is { LookupFailed: true, PermanentFailure: true, Answered: false, DeletedUtc: null },
            "a permanent unanswered history failure is delivered to the held lane as terminal Unknown");
        h.Wire.OnRevisionAsync = _ => throw new TaskCanceledException("scripted request timeout");
        await h.Inventory.ResolveDeletionEvidenceAsync(["held.txt"], (_, evidence) => callbacks.Add(evidence),
            CancellationToken.None);
        Require(callbacks.Count == 1,
            "a transient unanswered history failure stays suppressed for the retry ladder");
        Require(h.Known.DeletionEvidenceRows("p1", RootId).Count == 0,
            "terminal Unknown is never cached as successful history");
        Console.WriteLine("PASS permanent unanswered history answers the held lane");
    }

    // C31/RQ-052: a transient history failure keeps automatic retry. The inventory records the fact and
    // enqueues the existing held/ReprobeEvidence row; the next run re-probes automatically. It does not
    // consume a failed-run allowance or demand a user decision.
    private static async Task IntakeHistoryTransientRetryAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFile("gone.txt", "same");
        SeedStored(h, Stored("gone.txt", "id:gone", "h"));
        h.Wire.SeedDeletedEntry("gone.txt");
        h.Wire.SeedChangePage("transient-start", [h.Wire.ChangeDeleted("/Pilot/gone.txt")], false, "transient-end");
        UseFastHistoryRetryClock(h);
        h.Wire.OnRevisionAsync = _ => throw new TaskCanceledException("scripted request timeout");

        var first = await h.Inventory.ChangesSinceAsync("transient-start", 1, _ => true, CancellationToken.None);
        Require(first.PendingDeletionEvidence.ContainsKey("gone.txt") &&
            first.Changes.Count == 1 && first.Changes[0].Kind == ChangeKind.Delete && first.Changes[0].DeletedUtc is null,
            "a timed-out history read is pending unknown evidence, never a proven deletion time");
        Require(first.ReleasedDeletionEvidence.Contains("gone.txt") &&
            h.Known.DeletionEvidenceRows("p1", RootId).Single(row => row.Path == "gone.txt").Failure
                is { Attempts: 1, Released: true },
            "the failed read is a durable fact and the feed may advance behind the item hold");
        var held = h.Known.SyncRetryAt("p1", "gone.txt");
        Require(held is { Kind: "deletion-evidence", Held: not null, ReprobeEvidence: true, EvidenceReleased: false } &&
            held.RetryAfter == DateTimeOffset.MinValue,
            "the item is enqueued due-now for the existing held-evidence owner, not a permanent decision");

        // Released feed rows are not re-probed by replaying ChangesSinceAsync. The Run-held lane
        // consumes the durable due-now row through this production evidence entry point.
        h.Wire.OnRevisionAsync = null;
        h.Wire.RevisionContentHash = new string('b', 64);
        var callbacks = new Dictionary<string, SyncRemoteDeletionEvidence>(StringComparer.OrdinalIgnoreCase);
        var retryPath = held!.Path;
        await h.Inventory.ResolveDeletionEvidenceAsync([retryPath],
            (path, evidence) => callbacks[path] = evidence, CancellationToken.None);
        var resolvedRow = h.Known.DeletionEvidenceRows("p1", RootId).Single(row => row.Path == "gone.txt");
        Require(callbacks.Count == 1 && callbacks[retryPath] is { LookupFailed: false, DeletedUtc: not null, ContentHash: not null } &&
            resolvedRow.Failure is null && resolvedRow.DeletedUtc is not null && resolvedRow.ContentHash is not null &&
            callbacks[retryPath].DeletedUtc == resolvedRow.DeletedUtc && callbacks[retryPath].ContentHash == resolvedRow.ContentHash,
            "the automatic held-lane probe delivers successful evidence and updates the cache without a user decision");
        Console.WriteLine("PASS transient deletion history keeps automatic retry");
    }

    // The provider fact the held-evidence owner counts: answered errors are answers; unreachable, refused
    // sessions and timeouts are not.
    private static async Task IntakeHistoryAnsweredFactAsync()
    {
        async Task<SyncRemoteDeletionEvidence> Probe(Exception failure)
        {
            using var h = new Harness();
            h.Wire.SeedFile("held.txt", "same");
            h.Wire.SeedDeletedEntry("held.txt");
            UseFastHistoryRetryClock(h);
            h.Wire.OnRevisionAsync = _ => throw failure;
            var evidence = await h.Client.LookupDeletionEvidenceForV2Async(["held.txt"], CancellationToken.None);
            return evidence["held.txt"];
        }

        var answered = await Probe(new HttpRequestException("server error", null, HttpStatusCode.InternalServerError));
        Require(answered.LookupFailed && answered.Answered && !answered.PermanentFailure,
            "an answered server error counts as an answer but is not permanent");
        var unreachable = await Probe(new HttpRequestException("dns", new SocketException((int)SocketError.HostNotFound)));
        Require(unreachable.LookupFailed && !unreachable.Answered,
            "an unreachable provider is not an answer");
        var timeout = await Probe(new TaskCanceledException("scripted timeout"));
        Require(timeout.LookupFailed && !timeout.Answered,
            "a request timeout is not an answer");
        var transportTimeout = await Probe(new ProviderTransportTimeoutException("bounded body idle timeout"));
        Require(transportTimeout.LookupFailed && !transportTimeout.Answered && !transportTimeout.PermanentFailure,
            "a bounded transport-body timeout is a retryable unavailable history, not an answer");
        var malformed = await Probe(new JsonException("malformed revision history body"));
        Require(malformed.LookupFailed && malformed.Answered && malformed.PermanentFailure,
            "a malformed history body Dropbox returned is an answered permanent failure");
        var preSend = await Probe(new InvalidDataException("pre-send identity validation"));
        Require(preSend.LookupFailed && !preSend.Answered,
            "a generic pre-send validation failure is not counted as a provider answer");
        Console.WriteLine("PASS answered deletion-history fact distinguishes answers from outages");
    }

    // Production SQLite composition: the transient fact and the due-now held/ReprobeEvidence row survive
    // a cold database reopen, so the next run can let its existing owner decide.
    private static async Task IntakeHistoryRetrySurvivesSqliteReopenAsync()
    {
        var state = Path.Combine(_root, "intake-sqlite");
        Directory.CreateDirectory(state);
        using var h = new Harness();
        h.Wire.SeedFile("gone.txt", "same");
        h.Wire.SeedDeletedEntry("gone.txt");
        h.Wire.SeedChangePage("sqlite-start", [h.Wire.ChangeDeleted("/Pilot/gone.txt")], false, "sqlite-end");
        UseFastHistoryRetryClock(h);
        h.Wire.OnRevisionAsync = _ => throw new TaskCanceledException("scripted request timeout");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(new PairDef("p1", "intake fixture",
                new RootDef("ra", SyncProviders.Local, null, state, "id:ra"), Root(), [], [], true));
            var seed = store.BeginGeneration(RootId, "snapshot");
            store.WriteSnapshot(new Snapshot(RootId, Side.B, seed, "c0", [Stored("gone.txt", "id:gone", new string('a', 64))], []));
            store.CompleteGeneration(seed, "c0", true);
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, "p1");
            var delta = await inventory.ChangesSinceAsync("sqlite-start", store.BeginGeneration(RootId, "delta"),
                _ => true, CancellationToken.None);
            Require(delta.PendingDeletionEvidence.ContainsKey("gone.txt") &&
                delta.ReleasedDeletionEvidence.Contains("gone.txt"),
                "the production store records the transient hold and releases the feed");
        }
        using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        Require(reopened.DeletionEvidenceRows("p1", RootId).Single(row => row.Path == "gone.txt").Failure
                is { Attempts: 1, Released: true },
            "the item-owned transient fact survives a cold reopen");
        Require(reopened.SyncRetryAt("p1", "gone.txt") is
                { Kind: "deletion-evidence", Held: not null, ReprobeEvidence: true, EvidenceReleased: false } row &&
            row.RetryAfter == DateTimeOffset.MinValue,
            "the due-now held/ReprobeEvidence row survives a cold reopen for the existing owner");
        Require(reopened.Live(RootId, ["gone.txt"]).Count == 1,
            "the stored live row remains protected, never inferred absent");
        Console.WriteLine("PASS transient evidence retry survives SQLite reopen");
    }

    private static PilotRemoteEntry FileEntry(string path, string id, string revision = "rev-1") =>
        new(path, PilotEntryKind.File, id, revision, 1, new string('a', 64));

    private static Dictionary<string, object> RawFileEntry(string relative, string id, string revision = "rev-1") =>
        new()
        {
            [".tag"] = "file",
            ["name"] = relative[(relative.LastIndexOf('/') + 1)..],
            ["path_display"] = "/Pilot/" + relative,
            ["path_lower"] = ("/Pilot/" + relative).ToLowerInvariant(),
            ["id"] = id,
            ["rev"] = revision,
            ["size"] = 1L,
            ["content_hash"] = new string('a', 64),
            ["server_modified"] = "2026-10-01T00:00:00Z",
            ["client_modified"] = "2026-10-01T00:00:00Z"
        };

    private static object RawFileChange(string relative, string id, string revision) => RawFileEntry(relative, id, revision);
}
