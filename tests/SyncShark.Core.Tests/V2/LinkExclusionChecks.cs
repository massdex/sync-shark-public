using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Massdex.SyncShark.V2;

/// <summary>
/// a junction/symlink/mount point is never followed and is excluded on BOTH sides; the remote item at
/// the same path is untouched (even by a whole-pair remove choice), a first sync is not refused, and one standing
/// summary notice lists the links while any remain.
/// </summary>
internal static class LinkExclusionChecks
{
    private static V2Settings Settings => V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };

    public static async Task RunAsync()
    {
        Predicates();
        RebuildableContainerPredicates();
        await RebuildableCloudDeleteWithLocalLinksAsync();
        await FirstSyncExcludesLinkOnBothSidesAsync();
        await SyncedFolderBecomesLinkThenParentDeletedAsync();
        await StubsLeftAloneWhileReplacementIsOffAsync();
        Classification();
        StoreForgetsBaselinesWhenLinkIsWritten();
        await ApplyDropsEffectsUnderNewLocalLinkAsync();
        await ApplyDropsRemoteRemovalWhenLocalAppearsAsync();
        if (OperatingSystem.IsWindows())
            foreach (var changed in new[] { false, true }) await ApplyDropsEffectWhenLocalPathUncheckableAsync(changed);
        await InterleavedPreviewCannotPassStaleDeleteAsync();
    }

    private static (PairDef Pair, InMemoryStateStore Store, FakeEndpoint A, FakeEndpoint B, SyncRun Run) LocalPair(
        string localRoot)
    {
        var store = new InMemoryStateStore();
        var pair = new PairDef("plocal", "plocal", new RootDef("ra", SyncProviders.Local, null, localRoot, "id:ra"),
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"), [], [], true);
        store.SavePair(pair);
        var a = new FakeEndpoint(Side.A, pair.A.RootId);
        var b = new FakeEndpoint(Side.B, pair.B.RootId);
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b)
            .AddWriter(pair.A, new CountingWriter(a)).AddWriter(pair.B, new CountingWriter(b));
        var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), provider, Settings,
            TimeProvider.System);
        return (pair, store, a, b, run);
    }

    // Astra (dfab3c5) 1: a remote-only folder p is reviewed for removal; then local p appears with a junction inside.
    // The collapsed remote folder delete would be expanded from stored descendants, so it is dropped: p is no longer
    // one-sided and the next run re-plans it.
    private static async Task ApplyDropsRemoteRemovalWhenLocalAppearsAsync()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "v2-localappears-" + Guid.NewGuid().ToString("N"));
        var localRoot = Path.Combine(baseDir, "root");
        var outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(outside);
        var fx = LocalPair(localRoot);
        fx.B.Seed("p/j/file.txt", "remote content");
        var plan = await fx.Run.PreviewFirstSyncAsync(fx.Pair.PairId, null, [new OneSidedChoice("", Side.B, true)],
            new RecordingProgress(), CancellationToken.None);
        Require(plan.Actions.Any(x => x.Kind == ActionKind.Delete && x.Target == Side.B),
            "setup: the reviewed plan removes the remote-only p");
        Directory.CreateDirectory(Path.Combine(localRoot, "p"));
        var junction = Path.Combine(localRoot, "p", "j");
        LocalChecks.CreateJunction(junction, outside);
        try
        {
            await fx.Run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
            Require(await fx.B.ObserveAsync("p/j/file.txt", CancellationToken.None) is not null,
                "a local p that appeared after the preview (with a junction inside) stops every remote delete under p");
        }
        finally
        {
            LocalChecks.RemoveJunction(junction);
        }
    }

    // Astra (dfab3c5) 2: a local path that cannot be inspected is never "nothing there": the effect is dropped.
    [SupportedOSPlatform("windows")]
    private static async Task ApplyDropsEffectWhenLocalPathUncheckableAsync(bool changed)
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "v2-uncheckable-" + Guid.NewGuid().ToString("N"), "root");
        var locked = Path.Combine(localRoot, "locked");
        Directory.CreateDirectory(locked);
        var fx = LocalPair(localRoot);
        fx.A.Seed("locked", string.Empty, ItemKind.Folder);
        fx.B.Seed("locked", string.Empty, ItemKind.Folder);
        fx.B.Seed("locked/x.txt", "remote only");
        var plan = await fx.Run.PreviewFirstSyncAsync(fx.Pair.PairId, null, [new OneSidedChoice("", Side.B, true)],
            new RecordingProgress(), CancellationToken.None);
        Require(plan.Actions.Any(x => x.Kind == ActionKind.Delete && x.Target == Side.B && x.Path == "locked/x.txt"),
            "setup: the reviewed plan removes the remote-only locked/x.txt");
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        var info = new DirectoryInfo(locked);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            await fx.Run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
            Require(await fx.B.ObserveAsync("locked/x.txt", CancellationToken.None) is not null,
                "a local folder that cannot be listed at apply time drops the delete under it");
            Require(fx.Store.FirstSyncDone(fx.Pair.PairId) &&
                fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == "local-changed-since-review"),
                "a safely skipped removal completes first sync with a visible changed-path notice");
            Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "locked/x.txt") is
                { Kind: "reviewed-removal", ReviewedActions: { Count: > 0 } },
                "a lock retains the original stamped reviewed removal, never an ordinary copy-back");
            await fx.Run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(await fx.A.ObserveAsync("locked/x.txt", default) is null &&
                await fx.B.ObserveAsync("locked/x.txt", default) is not null,
                "a retry while still locked neither copies down nor deletes the reviewed survivor");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "locked/x.txt" &&
                notice.ReasonKey == "sync-retry" && NoticeOptions.Options(notice).Any(option => option.Choice == NoticeChoices.RetryNow)),
                "the locked-removal retry notice remains open and usable after the run");
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
        // Owner R2 ruling: a temporary lock must not reverse the reviewed remove into copy-down.
        if (changed) fx.B.Seed("locked/x.txt", "changed after the reviewed removal was parked");
        var retry = fx.Store.SyncRetryAt(fx.Pair.PairId, "locked/x.txt")!;
        fx.Store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
        var report = await fx.Run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(await fx.A.ObserveAsync("locked/x.txt", default) is null &&
            fx.Store.FirstSyncDone(fx.Pair.PairId) && report.Failed == 0,
            "unlocking never copies down the file the user chose to remove");
        if (!changed)
            Require(await fx.B.ObserveAsync("locked/x.txt", default) is null && report.Deferred == 0,
                "the unchanged stamped removal executes once the local folder unlocks");
        else
        {
            Require(await fx.B.ObserveAsync("locked/x.txt", default) is not null &&
                fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "locked/x.txt" &&
                    notice.ReasonKey == "reviewed-removal-skipped"),
                "changed stamps hold the removal with a notice instead of deleting or copying");
            await fx.Run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(await fx.A.ObserveAsync("locked/x.txt", default) is null &&
                await fx.B.ObserveAsync("locked/x.txt", default) is not null,
                "the changed reviewed removal stays held on later runs");
        }
    }

    // Astra (cec2a29) 1: the one-sided fact is captured in the plan. A second preview after the local item appears
    // updates the store, but the reviewed delete still carries OppositeAbsent and is dropped.
    private static async Task InterleavedPreviewCannotPassStaleDeleteAsync()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "v2-interleaved-" + Guid.NewGuid().ToString("N"), "root");
        Directory.CreateDirectory(localRoot);
        var fx = LocalPair(localRoot);
        fx.B.Seed("q/x.txt", "remote content");
        var reviewed = await fx.Run.PreviewFirstSyncAsync(fx.Pair.PairId, null, [new OneSidedChoice("", Side.B, true)],
            new RecordingProgress(), CancellationToken.None);
        Require(reviewed.Actions.Any(x => x is { Kind: ActionKind.Delete, Target: Side.B, OppositeAbsent: true }),
            "the reviewed remote deletes carry the local absence the planner saw");
        Directory.CreateDirectory(Path.Combine(localRoot, "q"));
        File.WriteAllText(Path.Combine(localRoot, "q", "x.txt"), "local now");
        fx.A.Seed("q/x.txt", "local now");
        await fx.Run.PreviewFirstSyncAsync(fx.Pair.PairId, null, [new OneSidedChoice("", Side.B, true)],
            new RecordingProgress(), CancellationToken.None);   // interleaved preview: the store now holds local q
        var report = await fx.Run.ApplyAsync(fx.Pair.PairId, reviewed, new RecordingProgress(), CancellationToken.None);
        Require(await fx.B.ObserveAsync("q/x.txt", CancellationToken.None) is not null && report.Deferred == 0 &&
                fx.Store.FirstSyncDone(fx.Pair.PairId) && await fx.A.ObserveAsync("q/x.txt", default) is not null,
            "a stale reviewed delete is dropped after an interleaved preview; first sync completes with both survivors");
    }

    // Astra fix 3: a known link tag wins over online-only bits; a cloud stub is still an online-only file.
    private static void Classification()
    {
        const FileAttributes recall = (FileAttributes)0x00400000;
        Require(PilotPath.ClassifyForeign(FileAttributes.Archive | FileAttributes.ReparsePoint | FileAttributes.Offline,
                0xA000000C) == ForeignObjectKind.Link &&
            PilotPath.ClassifyForeign(FileAttributes.Archive | FileAttributes.ReparsePoint | recall, 0xA0000003) ==
                ForeignObjectKind.Link,
            "a symlink or junction with OFFLINE/RECALL bits is a link");
        Require(PilotPath.ClassifyForeign(FileAttributes.Archive | FileAttributes.ReparsePoint | recall, 0x9000601A) ==
                ForeignObjectKind.OnlineOnlyFile &&
            PilotPath.ClassifyForeign(FileAttributes.Directory | FileAttributes.ReparsePoint, 0x9000601A) ==
                ForeignObjectKind.LocalCloud,
            "cloud placeholders keep their stub / fully-local classification");
    }

    // Astra fix 2: the store forgets the baselines at or under a link in the same write that records the link, so an
    // interruption right after link discovery cannot leave them behind (both store implementations).
    private static void StoreForgetsBaselinesWhenLinkIsWritten()
    {
        var data = Path.Combine(Path.GetTempPath(), "v2-linkforget-" + Guid.NewGuid().ToString("N"));
        using var sqlite = SqliteStateStore.Open(data, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
        foreach (var store in new IStateStore[] { new InMemoryStateStore(), sqlite })
        {
            var pair = NewPair(store);
            var file = new ItemState("p/nm/x.txt", ItemKind.File, "id:a:x", null, "aa", 1, DateTimeOffset.UnixEpoch,
                ItemFlags.None);
            var other = new ItemState("p/other.txt", ItemKind.File, "id:a:o", null, "bb", 1, DateTimeOffset.UnixEpoch,
                ItemFlags.None);
            var gen = store.BeginGeneration(pair.A.RootId, "full");
            store.WriteSnapshot(new Snapshot(pair.A.RootId, Side.A, gen, "0", [file, other], []));
            store.CompleteGeneration(gen, "0", true);
            foreach (var (item, id) in new[] { (file, "x"), (other, "o") })
            {
                var adopt = new SyncAction("adopt-" + id, 16, ActionKind.Adopt, Side.B, item.Path, null, item,
                    item with { ObjectId = "id:b:" + id }, false, "key-" + id);
                store.RecordIntent(pair.PairId, adopt);
                store.CommitResult(pair.PairId, gen, adopt,
                    new ActionResult(adopt.ActionId, Outcome.Done, adopt.Source, adopt.ExpectedTarget, null, null));
            }
            Require(store.Baselines(pair.PairId).Count == 2, "setup: two baselines");
            var delta = store.BeginGeneration(pair.A.RootId, "delta");
            // The run is "interrupted" right after the scan result is written: nothing else runs.
            store.WriteDelta(new Delta(pair.A.RootId, Side.A, delta, "0", "1",
                [new Change(ChangeKind.Delete, file.ObjectId, file.Path, null, DateTimeOffset.UnixEpoch),
                 new Change(ChangeKind.Upsert, "id:link", "p/nm", Link("p/nm"), null)], false, []));
            var left = store.Baselines(pair.PairId);
            Require(left.Count == 1 && left[0].ObjectKey == "key-o",
                $"{store.GetType().Name}: writing the link forgets the baselines under it at once, and only those");
        }
    }

    // Astra fix 1: a junction created on the local side between the preview and the apply. The store has never seen
    // it, so the apply re-checks each destructive effect's path and ancestors on the local disk and drops the remote
    // delete under it.
    private static async Task ApplyDropsEffectsUnderNewLocalLinkAsync()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "v2-newlink-" + Guid.NewGuid().ToString("N"));
        var localRoot = Path.Combine(baseDir, "root");
        var outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(outside);
        var store = new InMemoryStateStore();
        var pair = new PairDef("pnewlink", "pnewlink",
            new RootDef("ra", SyncProviders.Local, null, localRoot, "id:ra"),
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"), [], [], true);
        store.SavePair(pair);
        var a = new FakeEndpoint(Side.A, pair.A.RootId);
        var b = new FakeEndpoint(Side.B, pair.B.RootId);
        b.Seed("lib/x.txt", "remote content");
        b.Seed("keep-out.txt", "remote only");
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b)
            .AddWriter(pair.A, new CountingWriter(a)).AddWriter(pair.B, new CountingWriter(b));
        var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), provider, Settings,
            TimeProvider.System);
        var plan = await run.PreviewFirstSyncAsync(pair.PairId, null, [new OneSidedChoice("", Side.B, true)],
            new RecordingProgress(), CancellationToken.None);
        Require(plan.Actions.Any(x => x.Kind == ActionKind.Delete && x.Target == Side.B && x.Path == "lib/x.txt"),
            "setup: the reviewed plan removes the remote-only lib/x.txt");
        var junction = Path.Combine(localRoot, "lib");
        LocalChecks.CreateJunction(junction, outside);
        try
        {
            await run.ApplyAsync(pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
            Require(await b.ObserveAsync("lib/x.txt", CancellationToken.None) is not null,
                "a link created after the preview stops the remote delete under it");
            Require(await b.ObserveAsync("keep-out.txt", CancellationToken.None) is null,
                "other reviewed effects still apply");
            Require(store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == SyncRun.ChangedSinceReview.Key),
                "the changed-since-review notice is raised");
        }
        finally
        {
            LocalChecks.RemoveJunction(junction);
        }
        Console.WriteLine("PASS Astra fix 1: link created between preview and apply blocks the remote delete");
    }

    // Finding 1: a synced folder becomes a junction, then its parent is deleted locally. The old content under the
    // link path on the other side is never deleted: protection carries across the run the link disappears in, and the
    // baselines under the link were forgotten when it appeared (only copy-back can re-include it).
    private static async Task SyncedFolderBecomesLinkThenParentDeletedAsync()
    {
        var store = new InMemoryStateStore();
        var pair = NewPair(store);
        var a = new FakeEndpoint(Side.A, pair.A.RootId);
        var b = new FakeEndpoint(Side.B, pair.B.RootId);
        a.Seed("p/nm/x.txt", "linked content");
        a.Seed("p/other.txt", "sibling");
        var linking = new LinkingInventory(a, "p/nm") { Present = false };
        var writerB = new CountingWriter(b);
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b).AddInventory(pair.A, linking)
            .AddWriter(pair.A, new CountingWriter(a)).AddWriter(pair.B, writerB);
        var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), provider, Settings,
            TimeProvider.System);
        var plan = await run.PreviewFirstSyncAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        await run.ApplyAsync(pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(await b.ObserveAsync("p/nm/x.txt", CancellationToken.None) is not null, "setup: the folder content synced");

        // The folder becomes a junction: its old content vanishes locally and a link appears at its path.
        await a.DeleteAsync(Delete("p/nm/x.txt"), CancellationToken.None);
        linking.Present = true;
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(await b.ObserveAsync("p/nm/x.txt", CancellationToken.None) is not null,
            "the run the link appears in deletes nothing under it on the other side");

        // The parent folder is deleted locally, link included.
        await a.DeleteAsync(Delete("p/other.txt"), CancellationToken.None);
        linking.Present = false;
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(await b.ObserveAsync("p/nm/x.txt", CancellationToken.None) is not null,
            "after the link and its parent are deleted, the old linked content on the other side is never deleted");
        Require(await b.ObserveAsync("p/other.txt", CancellationToken.None) is null,
            "an ordinary sibling deletion still propagates");
        Console.WriteLine("PASS finding 1: link removal never authorises a delete of the old linked content");
    }

    // Decision 57 deferred: with replacement off (default), a stub and the other side's item at its path are left
    // alone, listed in one standing notice, and the first sync is not refused.
    private static async Task StubsLeftAloneWhileReplacementIsOffAsync()
    {
        var store = new InMemoryStateStore();
        var pair = NewPair(store);
        var a = new FakeEndpoint(Side.A, pair.A.RootId);
        var b = new FakeEndpoint(Side.B, pair.B.RootId);
        b.Seed("s.txt", "cloud copy");
        var stubbing = new LinkingInventory(a, "s.txt", ItemFlags.Placeholder);
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b).AddInventory(pair.A, stubbing)
            .AddWriter(pair.A, new CountingWriter(a)).AddWriter(pair.B, new CountingWriter(b));
        var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), provider, Settings,
            TimeProvider.System);
        var plan = await run.PreviewFirstSyncAsync(pair.PairId, null, [new OneSidedChoice("", Side.B, true)],
            new RecordingProgress(), CancellationToken.None);
        Require(plan.Actions.All(x => x.Path != "s.txt"),
            "a stub and the other side's item at its path get no action, even with a remove choice");
        Require(plan.Notices.Count(n => n.ReasonKey == LinkExclusions.StubReason.Key) == 1,
            "one standing 'online-only placeholders left alone' notice");
        Console.WriteLine("PASS decision 57 deferred: stubs left alone while replacement is off");
    }

    private static SyncAction Delete(string path) =>
        new("seed-delete-" + path, 0, ActionKind.Delete, Side.A, path, null, null, null, false, null);

    private static void Predicates()
    {
        var store = new InMemoryStateStore();
        var pair = NewPair(store);
        var gen = store.BeginGeneration(pair.A.RootId, "full");
        store.WriteSnapshot(new Snapshot(pair.A.RootId, Side.A, gen, "0", [Link("app/node_modules")], []));
        store.CompleteGeneration(gen, "0", true);
        var links = LinkExclusions.Of(store, pair, stubsLeftAlone: true);
        Require(links.Count == 1 && links.Covers("app/node_modules") && links.Covers("app/node_modules/x/y.js") &&
            !links.Covers("app/node_modules2") && !links.Covers("app"),
            "a link covers itself and its subtree only");
        Require(links.Contains("app") && !links.Contains("app/node_modules") && !links.Contains("other"),
            "a folder holding a link is protected from folder effects");
        var action = links.NarrowAction(_ => true);
        Require(!action("app") && !action("app/node_modules/x") && action("app/src.ts"),
            "the effect predicate rejects the link, its subtree and its containing folders");
        Console.WriteLine("PASS link exclusion predicates");
    }

    // BUG2: a chosen rebuildable folder's cloud-only delete ignores LOCAL links (at, above or under its path); cloud
    // links, left-alone stubs, pending decisions and the strict pair-boundary predicate still block it.
    private static void RebuildableContainerPredicates()
    {
        var store = new InMemoryStateStore();
        var pair = NewPair(store);
        void WriteFull(RootDef root, Side side, params ItemState[] items)
        {
            var gen = store.BeginGeneration(root.RootId, "full");
            store.WriteSnapshot(new Snapshot(root.RootId, side, gen, "0", items, []));
            store.CompleteGeneration(gen, "0", true);
        }
        WriteFull(pair.A, Side.A, Link("app/node_modules/pkg"), Link("self/node_modules"));
        var local = LinkExclusions.Of(store, pair, stubsLeftAlone: true);
        var allow = local.NarrowRebuildableDelete(_ => true);
        Require(allow("app/node_modules") && allow("self/node_modules") && !local.CoversExceptLocalLinks("self/node_modules"),
            "local links under or at a chosen cloud delete do not block it");
        Require(!local.NarrowAction(_ => true)("app/node_modules") && !local.NarrowAction(_ => true)("self/node_modules") &&
                local.Covers("self/node_modules/x"),
            "ordinary effects keep every local-link protection");
        Require(!local.NarrowRebuildableDelete(_ => false)("app/node_modules"),
            "rebuildable deletes never bypass the strict pair-boundary predicate");
        Require(!local.WithPending(["app/node_modules/decision"]).NarrowRebuildableDelete(_ => true)("app/node_modules") &&
                !local.WithPending(["app"]).NarrowRebuildableDelete(_ => true)("app/node_modules"),
            "pending decisions under or above still block a rebuildable cloud delete");
        WriteFull(pair.B, Side.B, Link("app/node_modules/pkg"), Link("self/node_modules"));
        var both = LinkExclusions.Of(store, pair, stubsLeftAlone: true);
        Require(!both.NarrowRebuildableDelete(_ => true)("app/node_modules") &&
                !both.NarrowRebuildableDelete(_ => true)("self/node_modules") &&
                !both.With(LinkExclusions.None).WithPending([]).NarrowRebuildableDelete(_ => true)("app/node_modules") &&
                both.CoversExceptLocalLinks("self/node_modules/x"),
            "a cloud link still blocks, including across the scan and pending unions");
        WriteFull(pair.B, Side.B);
        WriteFull(pair.A, Side.A, Link("app/node_modules/pkg") with { Kind = ItemKind.File, Flags = ItemFlags.Placeholder });
        var stub = LinkExclusions.Of(store, pair, stubsLeftAlone: true);
        Require(!stub.NarrowRebuildableDelete(_ => true)("app/node_modules"),
            "left-alone stubs still block a rebuildable cloud delete");
    }

    // BUG2 run level: the chosen cloud copy is removed whether the local folder CONTAINS a real junction or IS one,
    // for either endpoint order; only the reviewed selection passed to the apply grants it; nothing local is written.
    private static async Task RebuildableCloudDeleteWithLocalLinksAsync()
    {
        foreach (var selfLink in new[] { false, true })
        foreach (var cloudSide in new[] { Side.A, Side.B })
        // Each safety skip completes first sync: never reuse its approval for another negative or the positive.
        foreach (var scenario in Enumerable.Range(0, 12))
        {
            var baseDir = Path.Combine(Path.GetTempPath(), "v2-rebuildablelinks-" + Guid.NewGuid().ToString("N"));
            var localRoot = Path.Combine(baseDir, "root");
            var outside = Path.Combine(baseDir, "outside");
            Directory.CreateDirectory(Path.Combine(localRoot, "app"));
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "target.js"), "link target");
            var linkPath = selfLink ? "app/node_modules" : "app/node_modules/pkg";
            if (!selfLink) Directory.CreateDirectory(Path.Combine(localRoot, "app", "node_modules"));
            var junction = Path.Combine(localRoot, linkPath.Replace('/', Path.DirectorySeparatorChar));
            LocalChecks.CreateJunction(junction, outside);
            try
            {
                var store = new InMemoryStateStore();
                var local = new RootDef("local", SyncProviders.Local, null, localRoot, "id:local");
                var cloud = new RootDef("cloud", SyncProviders.Dropbox, "acc", "/cloud", "id:cloud");
                var localSide = cloudSide == Side.A ? Side.B : Side.A;
                var pair = new PairDef("rebuildablelinks", "rebuildablelinks",
                    cloudSide == Side.A ? cloud : local, cloudSide == Side.A ? local : cloud, [], [], true);
                store.SavePair(pair);
                var a = new FakeEndpoint(localSide, local.RootId);
                var b = new FakeEndpoint(cloudSide, cloud.RootId);
                a.Seed("app/package.json", "{}");
                if (!selfLink)
                {
                    a.Seed("app/node_modules", "", ItemKind.Folder);
                    a.Seed("app/node_modules/keep.js", "local");
                }
                b.Seed("app/package.json", "{}");
                b.Seed("app/node_modules", "", ItemKind.Folder);
                b.Seed("app/node_modules/cloud.js", "cloud");
                var localReads = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var cloudReads = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var writerA = new CountingWriter(a) { OnRead = localReads.Enqueue };
                var writerB = new CountingWriter(b) { OnRead = cloudReads.Enqueue };
                var linking = new LinkingInventory(a, linkPath);
                var provider = new FakeEndpointProvider().Add(local, a).Add(cloud, b)
                    .AddInventory(local, linking)
                    .AddWriter(local, writerA).AddWriter(cloud, writerB);
                var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), provider,
                    Settings, TimeProvider.System);
                var (plan, resolution) = await run.PreviewFirstSyncResolvedAsync(pair.PairId, null, null,
                    [new RebuildableChoice("js-packages")], new RecordingProgress(), CancellationToken.None);
                var removal = plan.Actions.Single(action => action.RebuildableRemove);
                var shape = (selfLink ? "self junction" : "descendant junction") + ", cloud side " + cloudSide;
                Require(removal.Target == cloudSide && removal.Path == "app/node_modules" &&
                        resolution.Selected.Select(folder => folder.Path).SequenceEqual(["app/node_modules"]),
                    "the chosen cloud delete is planned and selected (" + shape + ")");

                var (candidate, label) = scenario switch
                {
                    0 => (removal, "without the reviewed selection a flagged delete gets no exemption"),
                    1 => (removal with { Path = "app" }, "a flagged delete outside the selection is skipped"),
                    2 => (removal with { RebuildableRemove = false }, "ordinary cloud deletes keep local-link protection"),
                    3 => (removal with { FirstSyncChoice = false }, "unapproved deletes get no exemption"),
                    4 => (removal with { Target = localSide }, "local deletes get no exemption"),
                    5 => (removal with { Kind = ActionKind.Move }, "moves get no exemption"),
                    6 => (removal with { Source = removal.ExpectedTarget }, "source-bearing actions get no exemption"),
                    7 => (removal with { FromPath = linkPath }, "from-path actions get no exemption"),
                    8 => (removal with { ExpectedTarget = removal.ExpectedTarget! with { Kind = ItemKind.File } },
                        "file deletes get no exemption"),
                    9 => (removal, "an excluded subtree still blocks a rebuildable cloud delete"),
                    10 => (removal, "a nested pair still blocks a rebuildable cloud delete"),
                    _ => (removal, "reviewed cloud delete")
                };
                if (scenario == 9) store.SavePair(pair with { Excluded = ["app/node_modules/private"] });
                var nested = new PairDef("nested", "nested",
                    local with { RootId = "nested-local", RootObjectId = "id:nested-local",
                        RootPath = Path.Combine(baseDir, "nested-local") },
                    cloud with { RootId = "nested-cloud", RootObjectId = "id:nested-cloud",
                        RootPath = "/cloud/app/node_modules/nested" }, [], [], true);
                if (scenario == 10) store.SavePair(nested);
                // Positive: scenario 10 proves a saved nested pair reserves its area; a nested pair whose cloud root
                // has moved out of this pair no longer blocks the reviewed delete.
                if (scenario == 11) store.SavePair(nested with { B = nested.B with { RootPath = "/other" } });
                Require(!store.FirstSyncDone(pair.PairId), "each action starts with fresh first-sync approval");
                Require(!localReads.Any(p => PairPath.IsSelfOrUnder(p, linkPath)) &&
                        !linking.HashReads.Any(p => PairPath.IsSelfOrUnder(p, linkPath)),
                    "preview never reads or hashes through the local junction (" + shape + ")");
                localReads.Clear();
                cloudReads.Clear();
                if (scenario < 11)
                {
                    var report = await run.ApplyAsync(pair.PairId, plan with { Actions = [candidate] },
                        new RecordingProgress(), CancellationToken.None, null, scenario == 0 ? null : resolution);
                    Require(report.Skipped == 1 && report.FailureCount == 0 && report.Done == 0 &&
                            report.Uploaded == 0 && report.Downloaded == 0 && report.Deleted == 0 && report.Moved == 0 &&
                            store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "outside-pair-area" && n.Path == candidate.Path),
                        label + ": safety skip and outside-pair notice (" + shape + ")");
                    Require(writerA.Calls == 0 && writerB.Calls == 0 && localReads.IsEmpty && cloudReads.IsEmpty &&
                            !linking.HashReads.Any(p => PairPath.IsSelfOrUnder(p, linkPath)),
                        label + ": no writes, observations, content reads or hashes through the link (" + shape + ")");
                    Require(store.IntentById(pair.PairId, candidate.ActionId) is null && store.Baselines(pair.PairId).Count == 0 &&
                            store.UnresolvedIntents(pair.PairId).Count == 0,
                        label + ": no effect intent or baseline committed anywhere (" + shape + ")");
                    Require(await b.ObserveAsync("app/node_modules", CancellationToken.None) is not null &&
                            await b.ObserveAsync("app/node_modules/cloud.js", CancellationToken.None) is not null &&
                            Directory.Exists(junction) && File.ReadAllText(Path.Combine(outside, "target.js")) == "link target" &&
                            (selfLink || await a.ObserveAsync("app/node_modules/keep.js", CancellationToken.None) is not null),
                        label + ": cloud and local contents plus junction target unchanged (" + shape + ")");
                    continue;
                }
                await run.ApplyAsync(pair.PairId, plan, new RecordingProgress(), CancellationToken.None, null,
                    resolution);
                Require(await b.ObserveAsync("app/node_modules", CancellationToken.None) is null &&
                        await b.ObserveAsync("app/node_modules/cloud.js", CancellationToken.None) is null,
                    "the approved cloud folder and its descendants are removed (" + shape + ")");
                Require(writerA.Calls == 0 && Directory.Exists(junction) &&
                        !localReads.Any(p => PairPath.IsSelfOrUnder(p, linkPath)) &&
                        !linking.HashReads.Any(p => PairPath.IsSelfOrUnder(p, linkPath)) &&
                        File.ReadAllText(Path.Combine(outside, "target.js")) == "link target" &&
                        (selfLink || await a.ObserveAsync("app/node_modules/keep.js", CancellationToken.None) is not null),
                    "the local junction, its target and the local copy are untouched (" + shape + ")");
            }
            finally
            {
                LocalChecks.RemoveJunction(junction);
            }
        }
    }

    private static async Task FirstSyncExcludesLinkOnBothSidesAsync()
    {
        var store = new InMemoryStateStore();
        var pair = NewPair(store);
        var a = new FakeEndpoint(Side.A, pair.A.RootId);
        var b = new FakeEndpoint(Side.B, pair.B.RootId);
        a.Seed("keep.txt", "local");
        b.Seed("lib/x.txt", "remote under the link path");
        b.Seed("lib", string.Empty, ItemKind.Folder);
        b.Seed("only-remote.txt", "remote");
        var linking = new LinkingInventory(a, "lib");
        var writerA = new CountingWriter(a);
        var writerB = new CountingWriter(b);
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b).AddInventory(pair.A, linking)
            .AddWriter(pair.A, writerA).AddWriter(pair.B, writerB);
        var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), provider, Settings,
            TimeProvider.System);
        var removeAll = new[] { new OneSidedChoice("", Side.B, true) };

        var plan = await run.PreviewFirstSyncAsync(pair.PairId, null, removeAll, new RecordingProgress(),
            CancellationToken.None);
        Require(!plan.Actions.Any(x => PairPath.IsSelfOrUnder(x.Path, "lib") ||
                                        x.FromPath is not null && PairPath.IsSelfOrUnder(x.FromPath, "lib")),
            "nothing is planned at or under the link path on either side, even with a whole-pair remove choice");
        Require(plan.Actions.Any(x => x.Kind == ActionKind.Delete && x.Target == Side.B && x.Path == "only-remote.txt") &&
                plan.Actions.Any(x => x.Kind == ActionKind.CopyNew && x.Target == Side.B && x.Path == "keep.txt"),
            "the rest of the pair is planned as usual (first sync not refused)");
        var summaries = plan.Notices.Where(n => n.ReasonKey == LinkExclusions.Reason.Key).ToArray();
        Require(summaries.Length == 1 && summaries[0].Path.Length == 0 && summaries[0].Detail.StartsWith("1 link"),
            "one pair-level summary notice lists the links");

        await run.ApplyAsync(pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        Require((await b.ObserveAsync("lib/x.txt", CancellationToken.None)) is not null,
            "the remote item at the link path is untouched after the apply");
        Require(OpenSummaries(store, pair).Count == 1, "the links notice is stored and open after the apply");

        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(OpenSummaries(store, pair).Count == 1, "the standing links notice stays open while the link exists");

        linking.Present = false;
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(OpenSummaries(store, pair).Count == 0, "the links notice closes once no link remains");
        Console.WriteLine("PASS decision 59: links excluded on both sides, first sync proceeds, one standing notice");
    }

    private static List<Notice> OpenSummaries(IStateStore store, PairDef pair) =>
        store.OpenNotices(pair.PairId).Where(n => n.ReasonKey == LinkExclusions.Reason.Key).ToList();

    private static PairDef NewPair(IStateStore store)
    {
        var pair = new PairDef("plinks", "plinks",
            new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"), [], [], true);
        store.SavePair(pair);
        return pair;
    }

    private static ItemState Link(string path) =>
        new(path, ItemKind.Folder, "id:link:" + path, null, null, 0, DateTimeOffset.UnixEpoch, ItemFlags.Link);

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    /// <summary>A local inventory that also reports one link item, as the real scanner does for a junction.</summary>
    private sealed class LinkingInventory(FakeEndpoint inner, string linkPath, ItemFlags flag = ItemFlags.Link)
        : IInventorySource
    {
        private bool _reported;
        public bool Present { get; set; } = true;
        public System.Collections.Concurrent.ConcurrentQueue<string> HashReads { get; } = new();

        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p,
            CancellationToken ct)
        {
            var snapshot = await inner.FullAsync(gen, include, p, ct);
            _reported = Present;
            return Present ? snapshot with { Items = snapshot.Items.Append(Item()).ToArray() } : snapshot;
        }

        public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include,
            CancellationToken ct)
        {
            var delta = await inner.ChangesSinceAsync(cursor, gen, include, ct);
            if (Present == _reported || delta.CursorInvalid) return delta;
            _reported = Present;
            var change = Present
                ? new Change(ChangeKind.Upsert, "id:link:" + linkPath, linkPath, Item(), null)
                : new Change(ChangeKind.Delete, "id:link:" + linkPath, linkPath, null, DateTimeOffset.UtcNow);
            return delta with { Changes = delta.Changes.Append(change).ToArray() };
        }

        private ItemState Item() => flag == ItemFlags.Link
            ? Link(linkPath)
            : new(linkPath, ItemKind.File, "id:link:" + linkPath, null, null, 10, DateTimeOffset.UnixEpoch, flag);

        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            foreach (var path in paths) HashReads.Enqueue(path);
            return inner.HashAsync(paths, ct);
        }

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);
    }
}
