namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ItemEligibilityContentAsync(string folder)
    {
        var rootPath = Path.Combine(folder, "root");
        LocalChecks.Seed(rootPath, "Parent/item.txt", "resident content");
        var root = LocalChecks.RootOf(rootPath, "root-local");
        var remote = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "id:remote");
        var pair = new PairDef("eligibility-pair", "Pair A", root, remote, [], [], true);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.SavePair(pair);
        var inventory = LocalChecks.Inventory(rootPath, store, TimeProvider.System);
        store.WriteSnapshot(await inventory.FullAsync(store.BeginGeneration(root.RootId, "full"), _ => true, null!, default));
        var item = store.Live(root.RootId, ["Parent/item.txt"]).Single() with { Flags = ItemFlags.None, Hash = null };
        var reads = 0;
        var io = new LocalGapIo(store, StorageChecks.Fast, new HashSet<string>(), () => false)
        { AfterContentReadForCheck = _ => reads++ };
        foreach (var ignored in new[] { false, true })
        {
            store.SavePair(ignored ? pair with { IgnoreRules = ["FolderName:Parent"] } : pair with { Excluded = ["Parent"] });
            store.RequireRehash(root.RootId, item, "fixture content debt");
            inventory.GapIo = io;
            await inventory.VerifyIndexingPassAsync(default);
            Require(reads == 0 && store.RehashPendingCount(root.RootId) == 0 &&
                store.ByObject(root.RootId, item.ObjectId)!.Hash is null,
                "excluded/ignored proof scheduling retires without a content read or invented verification");
            store.SavePair(pair with { PairId = "included-pair" });
            Require(!io.Excluded(root, item), "another pair sharing the root still owns content work");
            store.SavePair(pair with { PairId = "included-pair", Excluded = ["Parent"] });
            store.SavePair(pair);
        }
        store.SavePair(pair with { PairId = "included-pair" });
        // Both pairs now include the file. A stale Flags=None record must not open a newly offline file.
        var fullPath = Path.Combine(rootPath, "Parent", "item.txt");
        var attributes = File.GetAttributes(fullPath);
        try
        {
            File.SetAttributes(fullPath, attributes | FileAttributes.Offline);
            Require((File.GetAttributes(fullPath) & FileAttributes.Offline) != 0, "offline fixture attribute persisted");
            var stub = await io.HashAsync(root, item, true, default);
            Require(stub.Outcome == LocalGapHashOutcome.NotPairWork && stub.Reason == "stub" && reads == 0,
                "fresh scanner metadata rejects a stale resident record without opening content");
        }
        finally { File.SetAttributes(fullPath, attributes); }
        var parent = Path.GetDirectoryName(fullPath)!;
        var parentAttributes = File.GetAttributes(parent);
        try
        {
            File.SetAttributes(parent, parentAttributes | FileAttributes.Offline);
            Require((File.GetAttributes(parent) & FileAttributes.Offline) != 0, "offline parent fixture attribute persisted");
            var stub = await io.HashAsync(root, item, true, default);
            Require(stub.Outcome == LocalGapHashOutcome.NotPairWork && stub.Reason == "stub" && reads == 0,
                "an online-only parent is classified without traversing or recalling its child");
            Require(NtfsUsnJournal.TryReadCheckpoint(rootPath, out var checkpoint), "the C: fixture has journal evidence");
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(root.RootId, checkpoint!.Cursor, checkpoint.NextUsn, true);
            var epoch = gaps.GapEpoch(root.RootId)!;
            var pending = store.ByObject(root.RootId, item.ObjectId)!;
            gaps.GapDiscovered(root.RootId, epoch, pending, true);
            gaps.GapDiscoveryDone(root.RootId, epoch);
            await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
            await worker.HashPage(root, epoch, default);
            gaps.PromoteGapDiscovery(root.RootId);
            Require(reads == 0 && !gaps.GapObligations(root.RootId, [item.ObjectId]).Contains(item.ObjectId) &&
                store.ByObject(root.RootId, item.ObjectId)!.Hash is null,
                "fresh stub classification retires gap scheduling without a read or invented content proof");
        }
        finally { File.SetAttributes(parent, parentAttributes); }
        store.RequireRehash(root.RootId, item, "resident proof");
        var revision = store.RehashRevision(root.RootId, item.ObjectId);
        var failure = CachedIdentityRefresh.OpenFailure(store, root, item, 5);
        Require(failure.Failure is { Stage: ContentProofFailure.StageKind.ContentOpen, NativeError: 5 } &&
            ContentProofFailure.Decode(failure.Diagnostic)?.NativeError == 5,
            "content-open native evidence survives structured persistence encoding");
        Require(store.FailIndexingRehash(root.RootId, item, revision, failure.Diagnostic!), "failure is recorded against its exact revision");
        var notice = store.OpenNotices(pair.PairId).Single(row => row.Path == item.Path && NoticeChoices.IsIndexFailure(row));
        Require(notice.Detail.Contains("ContentOpen") && notice.Detail.Contains("native error 5") &&
            NoticeChoices.IsAdmissible(notice, NoticeChoices.RetryNow) && NoticeChoices.IsAdmissible(notice, NoticeChoices.Ignore),
            "per-file proof failure exposes its exact cause and shared Retry/Ignore actions");
        io.EligibilityHeld = (_, _, _, _) => "held:protection";
        var held = await io.HashAsync(root, item, true, default);
        Require(held.Outcome == LocalGapHashOutcome.Held && held.Failure is null && reads == 0,
            "a hold defers content proof without reporting an indexing failure or opening content");
        io.EligibilityHeld = null;
        var resident = await io.HashAsync(root, item, true, default);
        Require(resident is { Outcome: LocalGapHashOutcome.Verified, Hash: not null, Proof: not null } && reads > 0,
            "included resident content gets a real native proof after fresh classification");
        Require(store.CompleteIndexingRehash(root.RootId, item, resident.Hash!, revision) &&
            store.ByObject(root.RootId, item.ObjectId)!.Hash == resident.Hash,
            "only the accepted content proof publishes verified content");
        Require(!store.OpenNotices(pair.PairId).Any(row => row.NoticeId == notice.NoticeId),
            "accepted proof closes its item failure without waiting for unrelated failures");
        var changedSelectionIo = new LocalGapIo(store, StorageChecks.Fast, new HashSet<string>(), () => false)
        {
            AfterContentReadForCheck = _ =>
            {
                store.SavePair(pair with { Excluded = ["Parent"] });
                store.SavePair(pair with { PairId = "included-pair", Excluded = ["Parent"] });
            }
        };
        var excludedAfterRead = await changedSelectionIo.HashAsync(root, item, true, default);
        Require(excludedAfterRead.Outcome == LocalGapHashOutcome.NotPairWork && excludedAfterRead.Failure is null,
            "post-read selection rejection retires work rather than recording an eligibility failure");
        Console.WriteLine("PASS ItemEligibilityContent");
    }
}
