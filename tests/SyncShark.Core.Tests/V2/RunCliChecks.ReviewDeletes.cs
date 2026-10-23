namespace Massdex.SyncShark.V2;

/// <summary>
/// CLI first-sync guard checks (decision 54 addendum, narrowed by an apply may not carry out a
/// first-sync CHOICE delete (rule 32, including a collapsed choice folder) that the reviewed prepare did not show.
/// Ordinary deletion propagation after a successful prepare is ongoing sync (RQ-043) and is not re-reviewed.
/// </summary>
internal static partial class RunCliChecks
{
    private const string EmptyChoices = "{}";

    internal static async Task RunReviewDeleteChecksAsync()
    {
        await Cli_ReviewDeletes_PostPrepareDeletionPropagatesAsync();
        await Cli_ReviewDeletes_PrepareMatchedDeletionPropagatesAsync();
        await Cli_ReviewDeletes_ChangedSurvivorWinsAsync();
        await Cli_ReviewDeletes_PrepareMatchedHeldLaneMissPropagatesAsync();
        await Cli_ReviewDeletes_PrepareMatchedHeldLaneMissChangedSurvivorAsync();
        await Cli_ReviewDeletes_PrepareMatchedHashPassNoCopyBackAsync();
        Cli_ReviewDeletes_EmptyPlanRetainsPreparedRetryEvidence();
        await Cli_ReviewDeletes_PrepareMatchedUnknownHashStaysProtectedAsync();
        await Cli_ReviewDeletes_HeldChildBlocksFolderDeleteAsync(false);
        await Cli_ReviewDeletes_HeldChildBlocksFolderDeleteAsync(true);
        await Cli_ReviewDeletes_ResolvedHeldChildKeepsFolderPendingAsync();
        await Cli_ReviewDeletes_NewChoiceVictimIsRefusedAsync();
        await Cli_ReviewDeletes_NewDeletionEvidenceVictimIsRefusedAsync();
        await Cli_ReviewDeletes_SameDeletesApplyAsync();
        await Cli_ReviewDeletes_FewerDeletesApplyAsync();
        await Cli_ReviewDeletes_CollapsedChoiceFolderGrowsIsRefusedAsync();
        await Cli_ReviewDeletes_ExecutionRecheckRefusesNewChildAsync();
        Cli_ReviewDeletes_LargeRecordIsFast();
        Cli_ReviewDeletes_MatchedRecordSize();
    }

    // Execution-time re-check (decision 54 addendum, the half the run performs under its own gate): a scan that adds
    // a child to a collapsed choice folder after the reviewed plan was taken makes the run refuse before any effect,
    // even though the plan object itself never changed.
    private static async Task Cli_ReviewDeletes_ExecutionRecheckRefusesNewChildAsync()
    {
        var fx = Build();
        fx.A.Seed("a", "", ItemKind.Folder);
        fx.A.Seed("a/x.txt", "hi");
        const string choices = """{"oneSided":[{"prefix":"a/","side":"Local","action":"remove"}]}""";
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            var reviewed = new HashSet<string>(
                FirstSyncChoicesState.Load(root, fx.Pair.PairId)!.DeleteVictims, StringComparer.OrdinalIgnoreCase);
            var approved = await PreviewAsync(fx, choices);
            // A queued plan preview scans in a new child after the reviewed plan was taken.
            fx.A.Seed("a/y.txt", "later");
            var parsed = FirstSyncChoices.Parse(choices, fx.Pair);
            await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, parsed.Winners, parsed.OneSided,
                new RecordingProgress(), CancellationToken.None);
            var refused = false;
            try
            {
                await fx.Run().ApplyAsync(fx.Pair.PairId, approved, new RecordingProgress(), CancellationToken.None,
                    reviewed);
            }
            catch (SyncRunRefusedException)
            {
                refused = true;
            }
            Require(!refused && fx.Store.FirstSyncDone(fx.Pair.PairId),
                "the unreviewed child blocks the folder effect without refusing first-sync completion");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "the refused execution makes no writer call");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_ExecutionRecheckRefusesNewChild");
    }

    // / RQ-043: an item matched on both sides at prepare (baselined by an earlier executed adopt)
    // that is deleted on one side afterwards is ordinary ongoing sync. The apply waits for no new review; it
    // propagates the delete to the other side and completes.
    private static async Task Cli_ReviewDeletes_PostPrepareDeletionPropagatesAsync()
    {
        var fx = Build();
        var aState = fx.A.Seed("d/x.txt", "same");
        var bState = fx.B.Seed("d/x.txt", "same");
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            // An earlier executed adopt matched the item on both sides; the prepare happened after it.
            fx.Store.CommitResult(fx.Pair.PairId, 1,
                new SyncAction("adopt", 16, ActionKind.Adopt, Side.B, "d/x.txt", null, aState, bState, false,
                    "key:d/x.txt"),
                new ActionResult("adopt", Outcome.Done, null, null, null, null));
            await fx.A.DeleteAsync(new SyncAction("seed-delete", 0, ActionKind.Delete, Side.A, "d/x.txt", null,
                null, aState, false, null), CancellationToken.None);
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete,
                "a post-prepare deletion applies as ordinary sync without a new prepare");
            Require(await fx.B.ObserveAsync("d/x.txt", default) is not null && fx.WriterB.Deletes == 0,
                "post-preview deletion is not executed by first-sync apply");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(await fx.B.ObserveAsync("d/x.txt", CancellationToken.None) is null,
                "the deletion propagated to the Dropbox side in ordinary sync");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_PostPrepareDeletionPropagates");
    }

    // finding 3: the prepare itself is the baseline. A path seen on both sides at prepare is
    // recorded in the prepare file; when one side is deleted after the prepare and the survivor is unchanged, the
    // reviewed apply plans an ordinary deletion propagation WITHOUT any persisted baseline row.
    private static async Task Cli_ReviewDeletes_PrepareMatchedDeletionPropagatesAsync()
    {
        var fx = Build();
        var aState = fx.A.Seed("d/x.txt", "same");
        fx.B.Seed("d/x.txt", "same");
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            Require(RecordText(root, fx.Pair.PairId).Contains("d/x.txt", StringComparison.Ordinal),
                "the prepare recorded the matched path and both side stamps");
            await fx.A.DeleteAsync(new SyncAction("seed-delete", 0, ActionKind.Delete, Side.A, "d/x.txt", null,
                null, aState, false, null), CancellationToken.None);
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete,
                "a deletion after prepare propagates from the prepare's matched set without a baseline row");
            Require(await fx.B.ObserveAsync("d/x.txt", default) is not null && fx.WriterB.Deletes == 0,
                "post-preview deletion is not executed by first-sync apply");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(await fx.B.ObserveAsync("d/x.txt", CancellationToken.None) is null,
                "the deletion propagated to the Dropbox side in ordinary sync");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_PrepareMatchedDeletionPropagates");
    }

    // finding 3, RQ-043: if the surviving side changed after prepare, the change wins and is copied
    // back instead of being deleted.
    private static async Task Cli_ReviewDeletes_ChangedSurvivorWinsAsync()
    {
        var fx = Build();
        var aState = fx.A.Seed("d/x.txt", "same");
        fx.B.Seed("d/x.txt", "same");
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            await fx.A.DeleteAsync(new SyncAction("seed-delete", 0, ActionKind.Delete, Side.A, "d/x.txt", null,
                null, aState, false, null), CancellationToken.None);
            fx.B.Seed("d/x.txt", "edited on B after prepare");
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete,
                "a changed survivor applies as an ordinary copy-back");
            Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0, "changed survivor is never deleted during apply");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            var restored = await fx.A.ObserveAsync("d/x.txt", CancellationToken.None);
            Require(restored is not null && await fx.B.ObserveAsync("d/x.txt", CancellationToken.None) is not null,
                "the changed side wins, so both sides hold it instead of the delete propagating");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_ChangedSurvivorWins");
    }

    // finding 2 (Astra re-review): a prepare-matched path never enters the deletion-history probe or
    // held lane. With a deferred-evidence cache MISS, the unchanged local survivor of a post-prepare Dropbox deletion
    // must still be planned as ordinary propagation (Recovery-first), not held.
    private static async Task Cli_ReviewDeletes_PrepareMatchedHeldLaneMissPropagatesAsync()
    {
        var fx = Build();
        fx.A.OmitVersionIds = true;
        var aState = fx.A.Seed("d/x.txt", "same");
        var bState = fx.B.Seed("d/x.txt", "same");
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            fx.Provider.AddInventory(fx.Pair.B, new MissedDeletionEvidenceInventory(fx.B));
            await fx.B.DeleteAsync(new SyncAction("seed-delete", 0, ActionKind.Delete, Side.B, "d/x.txt", null,
                null, bState, false, null), CancellationToken.None);
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete,
                "a prepare-matched path is planned instead of held on an evidence cache miss");
            Require(await fx.A.ObserveAsync("d/x.txt", default) is not null && fx.WriterA.Deletes == 0,
                "post-preview local survivor waits for ordinary sync");
            var deferred = fx.Store.SyncRetryAt(fx.Pair.PairId, "d/x.txt");
            Require(aState.Hash is not null && deferred?.Match is { } savedMatch &&
                savedMatch.A == aState.ObjectId + "|h:" + aState.Hash &&
                savedMatch.B.StartsWith(bState.ObjectId + "|", StringComparison.Ordinal),
                "the deferred prepared match retains the local identity/hash and real deleted source identity; retry=" +
                System.Text.Json.JsonSerializer.Serialize(deferred));
            var ordinary = await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(await fx.A.ObserveAsync("d/x.txt", CancellationToken.None) is null,
                "the unchanged local survivor is deleted as ordinary propagation; retry=" +
                System.Text.Json.JsonSerializer.Serialize(fx.Store.SyncRetryAt(fx.Pair.PairId, "d/x.txt")) +
                "; notices=" + System.Text.Json.JsonSerializer.Serialize(fx.Store.OpenNotices(fx.Pair.PairId)) +
                "; report=" + System.Text.Json.JsonSerializer.Serialize(ordinary));
            Require(fx.Store.Recoveries(fx.Pair.PairId).Count == 1, "the local delete is Recovery-first");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_PrepareMatchedHeldLaneMissPropagates");
    }

    // Same held-lane cache miss, but the survivor changed after prepare: the change wins (RQ-043) and is copied back.
    private static async Task Cli_ReviewDeletes_PrepareMatchedHeldLaneMissChangedSurvivorAsync()
    {
        var fx = Build();
        fx.A.OmitVersionIds = true;
        fx.A.Seed("d/x.txt", "same");
        var bState = fx.B.Seed("d/x.txt", "same");
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            fx.Provider.AddInventory(fx.Pair.B, new MissedDeletionEvidenceInventory(fx.B));
            await fx.B.DeleteAsync(new SyncAction("seed-delete", 0, ActionKind.Delete, Side.B, "d/x.txt", null,
                null, bState, false, null), CancellationToken.None);
            fx.A.Seed("d/x.txt", "edited locally after prepare");
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete,
                "a changed prepare-matched survivor is not held and applies as a copy-back");
            Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0, "changed survivor is never deleted");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(await fx.A.ObserveAsync("d/x.txt", CancellationToken.None) is not null &&
                await fx.B.ObserveAsync("d/x.txt", CancellationToken.None) is not null,
                "the changed side wins and both sides hold it");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_PrepareMatchedHeldLaneMissChangedSurvivor");
    }

    // finding 4 (Astra re-review): the prepare-matched states come from the FINAL planning rows after
    // the hash pass. A size-differing pair hashed during prepare must not look "changed" at apply because the prepare
    // recorded a pre-hash stamp.
    private static async Task Cli_ReviewDeletes_PrepareMatchedHashPassNoCopyBackAsync()
    {
        var fx = Build();
        fx.A.OmitVersionIds = true;
        fx.A.InventoryWithoutHashes = true;
        fx.B.InventoryWithoutHashes = true;
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var aState = fx.A.Seed("d/x.txt", "older", mtime: at);
        var bState = fx.B.Seed("d/x.txt", "newer longer content", mtime: at.AddMinutes(5));
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            // The apply scan carries hashes again, as the real store does after the prepare's hash checkpoint.
            fx.A.InventoryWithoutHashes = false;
            fx.B.InventoryWithoutHashes = false;
            await fx.B.DeleteAsync(new SyncAction("seed-delete", 0, ActionKind.Delete, Side.B, "d/x.txt", null,
                null, bState, false, null), CancellationToken.None);
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "d/x.txt")?.Match is { } hashedMatch &&
                aState.Hash is not null && hashedMatch.A == aState.ObjectId + "|h:" + aState.Hash,
                "the final prepare hash, not a synthetic provider revision, is retained for the local survivor");
            Require(result.Failed == 0 && result.FirstSyncComplete,
                "the hashed-at-prepare survivor matches its final-row stamp and applies");
            Require(await fx.A.ObserveAsync("d/x.txt", default) is not null && fx.WriterA.Deletes == 0,
                "post-preview survivor remains until ordinary sync");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(await fx.A.ObserveAsync("d/x.txt", CancellationToken.None) is null,
                "the deletion propagates; the survivor is not falsely copied back");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_PrepareMatchedHashPassNoCopyBack");
    }

    // A local metadata-only prepare stamp contains no historical content proof. Gaining a hash later
    // cannot invent historical equality: retain the survivor with a notice, without deleting or copying back.
    private static async Task Cli_ReviewDeletes_PrepareMatchedUnknownHashStaysProtectedAsync()
    {
        var fx = Build();
        fx.A.OmitVersionIds = true;
        fx.A.InventoryWithoutHashes = true;
        fx.B.InventoryWithoutHashes = true;
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var aState = fx.A.Seed("d/x.txt", "older", mtime: at);
        var bState = fx.B.Seed("d/x.txt", "newer longer content", mtime: at.AddMinutes(5));
        fx.A.Unhashable.Add("d/x.txt");
        fx.B.Unhashable.Add("d/x.txt");
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            Require(RecordText(root, fx.Pair.PairId).Contains("d/x.txt", StringComparison.Ordinal),
                "the prepare recorded the matched path");
            // Apply gains hashes while prepare saved a metadata stamp. A resumed apply obtains the
            // unchanged survivor's hash through HashAsync instead of a full listing.
            fx.A.InventoryWithoutHashes = false;
            fx.B.InventoryWithoutHashes = false;
            fx.A.Unhashable.Remove("d/x.txt");
            fx.B.Unhashable.Remove("d/x.txt");
            var deltaCalls = fx.A.DeltaCalls;
            await fx.B.DeleteAsync(new SyncAction("seed-delete", 0, ActionKind.Delete, Side.B, "d/x.txt", null,
                null, bState, false, null), CancellationToken.None);
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(fx.A.DeltaCalls > deltaCalls, "metadata-stamped apply resumed A from the prepare checkpoint");
            Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "d/x.txt")?.Match is { } unknownMatch &&
                unknownMatch.A.StartsWith(aState.ObjectId + "|m:", StringComparison.Ordinal),
                "the fixture preserves genuinely unknown historical content as a local metadata stamp");
            Require(result.Failed == 0 && result.FirstSyncComplete,
                "an unconfirmed metadata-stamped survivor remains safe after apply gains a hash");
            Require(await fx.A.ObserveAsync("d/x.txt", default) is not null && fx.WriterA.Deletes == 0,
                "unknown historical content never authorizes deletion during apply");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(await fx.A.ObserveAsync("d/x.txt", CancellationToken.None) is not null &&
                await fx.B.ObserveAsync("d/x.txt", CancellationToken.None) is null && fx.WriterA.Deletes == 0,
                "unknown prepared hash retains the survivor without false copyback or automatic deletion");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Count(notice =>
                notice.Path == "d/x.txt" && notice.ReasonKey == "content-unconfirmed") == 1,
                "unknown prepared content has one deduplicated unconfirmed notice");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_PrepareMatchedUnknownHashStaysProtected");
    }

    // third-review finding 1: a held child is hidden from planner rows, so an ordinary folder
    // delete must not be emitted over it. The folder is held with a pending-evidence notice, the child survives, and
    // a later run (after the evidence resolves) handles the folder.
    private static async Task Cli_ReviewDeletes_HeldChildBlocksFolderDeleteAsync(bool sleeping)
    {
        var fx = Build();
        fx.A.OmitVersionIds = true;
        var folder = fx.A.Seed("d", "", ItemKind.Folder);
        fx.A.Seed("d/keep.txt", "keep");
        var bFolder = fx.B.Seed("d", "", ItemKind.Folder);
        var bKeep = fx.B.Seed("d/keep.txt", "keep");
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            fx.Provider.AddInventory(fx.Pair.B, new MissedDeletionEvidenceInventory(fx.B));
            // Dropbox deletes the whole matched folder after the prepare.
            await fx.B.DeleteAsync(new SyncAction("seed-folder", 0, ActionKind.Delete, Side.B, "d", null, null,
                bFolder, false, null), CancellationToken.None);
            await fx.B.DeleteAsync(new SyncAction("seed-child", 0, ActionKind.Delete, Side.B, "d/keep.txt", null,
                null, bKeep, false, null), CancellationToken.None);
            // A new local child with a deferred-evidence cache miss: held and hidden from the planner's rows.
            fx.A.Seed("d/new.txt", "new");
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(await fx.A.ObserveAsync("d/new.txt", CancellationToken.None) is not null,
                "a held child under the folder is never deleted by an ordinary folder-delete expansion");
            Require(await fx.A.ObserveAsync("d", CancellationToken.None) is not null,
                "the folder stays while its held child is pending");
            Require(await fx.A.ObserveAsync("d/keep.txt", CancellationToken.None) is not null,
                "the matched post-preview delete waits for ordinary sync");
            Require(result.FirstSyncComplete, "the pending held child does not prevent completion");
            if (sleeping)
            {
                var retry = fx.Store.SyncRetryAt(fx.Pair.PairId, "d/new.txt")!;
                fx.Store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = DateTimeOffset.UtcNow.AddHours(1) });
            }
            var handoff = System.Text.Json.JsonSerializer.Serialize(fx.Store.ReadSyncRetries(fx.Pair.PairId));
            var following = await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(await fx.A.ObserveAsync("d/keep.txt", default) is null,
                "the next ordinary run propagates the matched child's delete; before=" + handoff +
                "; after=" + System.Text.Json.JsonSerializer.Serialize(new { Retry = fx.Store.ReadSyncRetries(fx.Pair.PairId), Report = following }));
            Require(await fx.A.ObserveAsync("d/new.txt", default) is not null && await fx.A.ObserveAsync("d", default) is not null,
                "ordinary propagation never expands the folder delete over its unreviewed child");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice =>
                notice.ReasonKey == "deletion-evidence-pending" && notice.Path == "d"),
                "the blocked ordinary folder delete retains its pending-evidence notice");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_HeldChildBlocksFolderDelete sleeping=" + sleeping);
    }

    // The reviewed held child resolves in apply, but its ancestor stays blocked for that apply. Durable
    // prepared matches let the next ordinary run delete the now-unblocked folder and matched child safely.
    private static async Task Cli_ReviewDeletes_ResolvedHeldChildKeepsFolderPendingAsync()
    {
        var fx = Build();
        fx.A.OmitVersionIds = true;
        fx.A.Seed("d", "", ItemKind.Folder);
        fx.A.Seed("d/keep.txt", "keep");
        var bFolder = fx.B.Seed("d", "", ItemKind.Folder);
        var bKeep = fx.B.Seed("d/keep.txt", "keep");
        // A local-only child with a deferred-evidence cache miss: held and reviewed at prepare.
        var heldChild = fx.A.Seed("d/new.txt", "new");
        fx.Provider.AddInventory(fx.Pair.B,
            new MissedDeletionEvidenceInventory(fx.B)
                { ResolveDeleted = DateTimeOffset.UtcNow.AddHours(1), ContentHash = heldChild.Hash });
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            await fx.B.DeleteAsync(new SyncAction("seed-folder", 0, ActionKind.Delete, Side.B, "d", null, null,
                bFolder, false, null), CancellationToken.None);
            await fx.B.DeleteAsync(new SyncAction("seed-child", 0, ActionKind.Delete, Side.B, "d/keep.txt", null,
                null, bKeep, false, null), CancellationToken.None);
            var first = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(await fx.A.ObserveAsync("d/new.txt", CancellationToken.None) is null,
                "the reviewed held child resolves and its delete finishes in this apply");
            Require(await fx.A.ObserveAsync("d", CancellationToken.None) is not null,
                "the evidence-blocked folder is not deleted in the same apply");
            Require(first.FirstSyncComplete,
                "an evidence-blocked folder is retained as ordinary retry work after completion");
            var handoff = System.Text.Json.JsonSerializer.Serialize(fx.Store.ReadSyncRetries(fx.Pair.PairId));
            var second = await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(second.Failed == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId),
                "ordinary sync replans the folder once nothing is held under it");
            var aFolderAfter = await fx.A.ObserveAsync("d", default);
            var bFolderAfter = await fx.B.ObserveAsync("d", default);
            Require(aFolderAfter is null && bFolderAfter is null,
                "the folder deletion propagates instead of the folder being recreated on Dropbox; before=" + handoff +
                "; after=" + System.Text.Json.JsonSerializer.Serialize(new
                {
                    AFolder = aFolderAfter, BFolder = bFolderAfter,
                    AChild = await fx.A.ObserveAsync("d/keep.txt", default),
                    Retry = fx.Store.ReadSyncRetries(fx.Pair.PairId),
                    Notices = fx.Store.OpenNotices(fx.Pair.PairId), Report = second
                }));
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_ResolvedHeldChildKeepsFolderPending");
    }

    // Decisions 71/78: a newly evidenced local-only victim is not ordinary sync. The real deferred
    // evidence path holds it pending for follow-up review, never deleting it under the old prepare.
    private static async Task Cli_ReviewDeletes_NewDeletionEvidenceVictimIsRefusedAsync()
    {
        var fx = Build();
        fx.A.Seed("d/x.txt", "local only");
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            Require(!RecordText(root, fx.Pair.PairId).Contains("d/x.txt", StringComparison.Ordinal),
                "the prepare showed no delete for the local-only file");
            fx.B.Seed("ordinary.txt", "unrelated cloud file");
            var local = (await fx.A.ObserveAsync("d/x.txt", CancellationToken.None))!;
            fx.Provider.AddInventory(fx.Pair.B, new MissedDeletionEvidenceInventory(fx.B)
                { ResolveDeleted = local.Mtime.AddHours(1), ContentHash = local.Hash });
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null,
                _ => { }, CancellationToken.None);
            Require(result.UnreviewedPending == 1 && result.FirstSyncComplete,
                "a deletion-evidence victim appearing after prepare stays pending for review");
            Require(fx.WriterA.Deletes == 0 && fx.WriterA.Preserves == 0 && fx.WriterB.Calls == 0 &&
                (await fx.A.ObserveAsync("d/x.txt", CancellationToken.None))?.Hash == local.Hash,
                "the unreviewed evidence victim is untouched even with an equal deleted-revision hash");
            Require(result.Failed == 0 && fx.WriterA.Puts == 1 &&
                await fx.A.ObserveAsync("ordinary.txt", CancellationToken.None) is not null,
                "unrelated work completes while the new victim stays pending");
            // A newly reviewed held path with different known content restores rather than deletes.
            fx.Provider.AddInventory(fx.Pair.B, new MissedDeletionEvidenceInventory(fx.B)
                { ResolveDeleted = local.Mtime.AddHours(1), ContentHash = new string('a', 64) });
            var restored = await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(restored.Failed == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.WriterB.Puts == 1 &&
                fx.WriterA.Deletes == 0 && fx.WriterA.Preserves == 0 &&
                (await fx.A.ObserveAsync("d/x.txt", CancellationToken.None))?.Hash == local.Hash &&
                (await fx.B.ObserveAsync("d/x.txt", CancellationToken.None))?.Hash == local.Hash &&
                fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.Kind == NoticeKind.EditBeatsDelete),
                "reviewed differing-hash evidence restores the local edit with a non-blocking notice");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_NewDeletionEvidenceVictimIsRefused");
    }

    // (b): a NEW one-sided victim of a user's remove choice, appearing after prepare, is still a
    // first-sync decision and is refused. An empty folder adds no file bytes, so the remove-growth budget check
    // cannot be the one refusing it: the delete-review guard is.
    private static async Task Cli_ReviewDeletes_NewChoiceVictimIsRefusedAsync()
    {
        foreach (var both in new[] { false, true })
        {
        var fx = Build();
        fx.A.Seed("d", "", ItemKind.Folder);
        fx.B.Seed("d", "", ItemKind.Folder);
        fx.A.Seed("d/x.txt", "a");
        var writes = new RecordingWriter(fx.A);
        fx.Provider.AddWriter(fx.Pair.A, writes);
        const string choices = """{"oneSided":[{"prefix":"d/","side":"Local","action":"remove"}]}""";
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            fx.A.Seed("d/new", "", ItemKind.Folder);
            if (both) fx.B.Seed("d/new", "", ItemKind.Folder);
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, default);
            Require(result.FirstSyncComplete, "new unreviewed victims do not refuse the run");
            var effects = writes.Calls.Where(call => !call.StartsWith("Observe:", StringComparison.Ordinal) &&
                !call.StartsWith("OpenRead:", StringComparison.Ordinal)).ToArray();
            Require(effects.SequenceEqual(new[] { "Preserve:d/x.txt", "Delete:d/x.txt" }) && fx.WriterB.Calls == 0,
                "the exact writer set is only recovery and deletion of the individually reviewed x.txt");
            Require(await fx.A.ObserveAsync("d", default) is not null && await fx.B.ObserveAsync("d", default) is not null &&
                await fx.A.ObserveAsync("d/new", default) is not null &&
                (await fx.B.ObserveAsync("d/new", default) is not null) == both,
                "the parent and every existing new child survive with no unreviewed writes");
            if (!both)
            {
                Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "d/new") is not null, "the unreviewed local child is deferred");
                await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
                Require(await fx.A.ObserveAsync("d/new", default) is not null && await fx.B.ObserveAsync("d/new", default) is not null,
                    "ordinary sync subsequently copies the new local child rather than deleting it");
            }
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_NewChoiceVictimIsRefused both=" + both);
        }
    }

    // The reviewed deletes are recorded by prepare and, when apply sees exactly the same ones, it applies them.
    private static async Task Cli_ReviewDeletes_SameDeletesApplyAsync()
    {
        var fx = Build();
        fx.A.Seed("d/one.txt", "a");
        fx.A.Seed("d/two.txt", "b");
        await SeedDeletedOnBAsync(fx, "d/one.txt", "d/two.txt");
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            var record = RecordText(root, fx.Pair.PairId);
            Require(record.Contains("d/one.txt", StringComparison.Ordinal) &&
                record.Contains("d/two.txt", StringComparison.Ordinal),
                "the prepare recorded both delete victims");
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete && fx.WriterA.Deletes == 2 && fx.WriterA.Preserves == 0,
                $"the same equal-hash deletes apply cleanly without Recovery (failed={result.Failed}, complete={result.FirstSyncComplete}, deletes={fx.WriterA.Deletes}, preserves={fx.WriterA.Preserves})");
            Require(await fx.A.ObserveAsync("d/one.txt", CancellationToken.None) is null &&
                await fx.A.ObserveAsync("d/two.txt", CancellationToken.None) is null,
                "the reviewed rule-19 deletes were carried out");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_SameDeletesApply");
    }

    // A delete that is no longer present at apply is not refused: fewer deletes than prepare showed is fine.
    private static async Task Cli_ReviewDeletes_FewerDeletesApplyAsync()
    {
        var fx = Build();
        fx.A.Seed("d/one.txt", "a");
        fx.A.Seed("d/two.txt", "b");
        await SeedDeletedOnBAsync(fx, "d/one.txt", "d/two.txt");
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            Require(RecordText(root, fx.Pair.PairId).Contains("d/two.txt", StringComparison.Ordinal),
                "the prepare recorded the delete the file later loses");
            // The second file disappears from the local side, so its rule-19 delete is gone at apply: fewer victims.
            await fx.A.DeleteAsync(new SyncAction("seed-gone", 0, ActionKind.Delete, Side.A, "d/two.txt", null, null,
                null, false, null), CancellationToken.None);
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete && fx.WriterA.Deletes == 1,
                "fewer deletes than prepare showed still apply after the apply re-preview drops the vanished victim");
            Require(await fx.A.ObserveAsync("d/one.txt", CancellationToken.None) is null,
                "the delete that is still present was carried out");
            Require(fx.WriterA.Preserves == 0 && await fx.A.ObserveAsync("d/two.txt", CancellationToken.None) is null,
                "equal content is deleted without Recovery and the vanished victim stays absent");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_FewerDeletesApply");
    }

    // A collapsed folder delete from a remove choice names its children in the record, so the same plan applies; a
    // new child under the folder after prepare is a new first-sync choice victim and is refused.
    private static async Task Cli_ReviewDeletes_CollapsedChoiceFolderGrowsIsRefusedAsync()
    {
        const string choices = """{"oneSided":[{"prefix":"a/","side":"Local","action":"remove"}]}""";
        var listed = Build();
        listed.A.Seed("a", "", ItemKind.Folder);
        listed.A.Seed("a/x.txt", "hi");
        var listedRoot = TempRoot();
        try
        {
            var runtime = Runtime(listed, listedRoot);
            await runtime.PrepareFirstSyncAsync(listed.Pair.PairId, _ => { }, CancellationToken.None, choices);
            Require(RecordText(listedRoot, listed.Pair.PairId).Contains("a/x.txt", StringComparison.Ordinal),
                "the collapsed choice folder delete's child is recorded as a victim");
            var result = await runtime.ApplyFirstSyncAsync(listed.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete,
                "a collapsed choice folder delete whose children are recorded applies");
        }
        finally
        {
            Cleanup(listedRoot);
        }

        var grown = Build();
        grown.A.Seed("a", "", ItemKind.Folder);
        grown.A.Seed("a/x.txt", "hi");
        var independent = grown.A.Seed("outside.txt", "independent");
        var writer = new RecordingWriter(grown.A);
        grown.Provider.AddWriter(grown.Pair.A, writer);
        var grownRoot = TempRoot();
        try
        {
            var runtime = Runtime(grown, grownRoot);
            await runtime.PrepareFirstSyncAsync(grown.Pair.PairId, _ => { }, CancellationToken.None, choices);
            // An empty folder adds no file bytes, so the remove-growth budget check passes and the delete-review
            // guard is what refuses the new collapsed-folder victim.
            grown.A.Seed("a/y", "", ItemKind.Folder);
            var result = await runtime.ApplyFirstSyncAsync(grown.Pair.PairId, choices, false, null, _ => { }, default);
            Require(result.FirstSyncComplete && result.Failed == 0, "grown collapsed choice does not refuse independent work");
            Require(writer.Calls.All(call => call.StartsWith("Observe:", StringComparison.Ordinal) ||
                call == "OpenRead:outside.txt") && grown.WriterB.Puts == 1 && grown.WriterB.Calls == 1,
                "zero writes under the held folder; only the independent reviewed copy executes");
            Require(await grown.A.ObserveAsync("a", default) is not null &&
                await grown.A.ObserveAsync("a/x.txt", default) is not null && await grown.A.ObserveAsync("a/y", default) is not null &&
                (await grown.B.ObserveAsync("outside.txt", default))?.Hash == independent.Hash,
                "all children of the collapsed choice survive while other reviewed work applies");
            Require(grown.Store.OpenNotices(grown.Pair.PairId).Any(notice => notice.Path == "a" && notice.ReasonKey == "reviewed-folder-grew"),
                "grown folder choice has an explicit re-review notice");
        }
        finally
        {
            Cleanup(grownRoot);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_CollapsedChoiceFolderGrowsIsRefused");
    }

    // The retained record round-trips a first-sync-sized victim set (the parent pair can delete tens of thousands).
    private static void Cli_ReviewDeletes_LargeRecordIsFast()
    {
        var root = TempRoot();
        try
        {
            var victims = Enumerable.Range(0, 20000)
                .Select(index => "A:dir-" + index / 100 + "/sub-" + index + ".txt").ToArray();
            var parsed = new FirstSyncChoices.Parsed(new Dictionary<string, Side?>(StringComparer.Ordinal), [], []);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            FirstSyncChoicesState.Save(root, "p1", parsed, [], victims, []);
            var loaded = FirstSyncChoicesState.Load(root, "p1");
            watch.Stop();
            Require(loaded is { DeleteVictimCount: 20000 } && loaded.DeleteVictims.Count == 20000,
                "the record round-trips 20000 delete victims");
            Require(watch.Elapsed < TimeSpan.FromSeconds(1),
                $"writing and reading 20000 victims stays under a second ({watch.ElapsedMilliseconds} ms)");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_LargeRecordIsFast");
    }

    // finding 3: the prepare record now carries every path matched on both sides. Report the file
    // size impact for a first-sync-sized set (~200k paths, realistic path lengths) before any compression is added.
    private static void Cli_ReviewDeletes_MatchedRecordSize()
    {
        var root = TempRoot();
        try
        {
            var parsed = new FirstSyncChoices.Parsed(new Dictionary<string, Side?>(StringComparer.Ordinal), [], []);
            var at = DateTimeOffset.UnixEpoch;
            var items = Enumerable.Range(0, 200_000).Select(index =>
            {
                var path = "projects/repo-" + index / 1000 + "/src/" + new string('a', 96) + index + ".bin";
                var local = new ItemState(path, ItemKind.File, "id:local:4242:" + index.ToString("x"), null, null,
                    4096, at.AddSeconds(index), ItemFlags.None);
                var cloud = new ItemState(path, ItemKind.File, "id:dbx:" + index.ToString("x"),
                    "rev-" + index, null, 4096, at.AddSeconds(index), ItemFlags.None);
                return new MatchedPair(path, local, cloud);
            }).ToArray();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            FirstSyncChoicesState.Save(root, "p1", parsed, [], [], [], null, items);
            var bytes = new FileInfo(Path.Combine(root, "v2", "first-sync-p1.choices.json")).Length;
            var saveMs = watch.ElapsedMilliseconds;
            watch.Restart();
            var loaded = FirstSyncChoicesState.Load(root, "p1");
            var loadMs = watch.ElapsedMilliseconds;
            Console.WriteLine("INFO matched record: 200000 paths, " + (bytes / (1024.0 * 1024.0)).ToString("F1") +
                " MiB, save " + saveMs + " ms, load " + loadMs + " ms");
            Require(loaded is { Matched.Count: 200000 }, "the matched set round-trips 200000 paths");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ReviewDeletes_MatchedRecordSize");
    }

    // A deferred deletion-evidence inventory with an empty cache: every probe is pending. Used to prove that a
    // prepare-matched path is never handed to the evidence lane finding 2).
    private sealed class MissedDeletionEvidenceInventory(FakeEndpoint inner) : IInventorySource, IDeferredDeletionEvidence
    {
        public IReadOnlyList<string> FolderDeletionCandidates(IReadOnlyList<string> paths) => [];
        public bool IsAtOrUnderExactTombstone(string path) => false;
        public Task<DeletionEvidencePreparation> PrepareDeletionEvidenceAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => Task.FromResult(new DeletionEvidencePreparation(
                new Dictionary<string, SyncRemoteDeletionEvidence>(StringComparer.OrdinalIgnoreCase), paths.ToArray()));
        /// <summary>When set, every probe resolves in-run with this Dropbox deletion time; otherwise none resolves.</summary>
        public DateTimeOffset? ResolveDeleted { get; init; }
        public string? ContentHash { get; init; }
        public IReadOnlyDictionary<string, string?> ContentHashes { get; init; } = new Dictionary<string, string?>();
        public Task ResolveDeletionEvidenceAsync(IReadOnlyList<string> paths,
            Action<string, SyncRemoteDeletionEvidence> resolved, CancellationToken ct)
        {
            // Default: never resolve, so a cache miss whose probe is still pending stays pending for a later run.
            if (ResolveDeleted is { } at)
                foreach (var path in paths)
                    resolved(path, new SyncRemoteDeletionEvidence(at, "probed", ContentHash: ContentHashes.GetValueOrDefault(path, ContentHash)));
            return Task.CompletedTask;
        }
        public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress,
            CancellationToken ct) => inner.FullAsync(gen, include, progress, ct);
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include,
            CancellationToken ct) => inner.ChangesSinceAsync(cursor, gen, include, ct);
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => inner.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => throw new InvalidOperationException("The deferred lane must not eager-probe.");
    }

    // Decision 78: these fixtures mean reviewed deletes, so history must confirm the exact local bytes.
    private static async Task SeedDeletedOnBAsync(Fixture fx, params string[] paths)
    {
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
            hashes[path] = (await fx.A.ObserveAsync(path, CancellationToken.None))!.Hash;
        fx.Provider.AddInventory(fx.Pair.B, new MissedDeletionEvidenceInventory(fx.B)
        {
            ResolveDeleted = DateTimeOffset.UtcNow.AddHours(1), ContentHashes = hashes
        });
    }

    private static string RecordText(string root, string pairId) =>
        File.ReadAllText(Path.Combine(root, "v2", "first-sync-" + pairId + ".choices.json"));
}
