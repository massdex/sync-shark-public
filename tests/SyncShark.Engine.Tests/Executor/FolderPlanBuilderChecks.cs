using System.Runtime.CompilerServices;
using System.Text;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks folder planning against disposable SQLite stores and synthetic provider contents.</summary>
public static class FolderPlanBuilderChecks
{
    // One plan asks once at or above its folder and once for its range (V3-05 "Folder plan").
    private const int UnreadableAsks = 2;

    // engine-settings.json's production default; the eleventh positional EngineSettings operand.
    private const int LogBufferCharacters = 262144;

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                await ClassificationsAsync(directory, side);
                await LiveMembershipAsync(directory, side);
                await MovedInAsync(directory, side);
                await ProtectedDescendantsAsync(directory, side, restored: false);
                await ProtectedDescendantsAsync(directory, side, restored: true);
                await NestedRootsAsync(directory, side);
                await MappedNestedDriftAsync(directory, side);
                await EmptyFolderAsync(directory, side);
                await ListingFailureAsync(directory, side);
                await ScratchOwnershipAsync(directory, side);
                await UnreadableChildrenAsync(directory, side);
                await UnreadableIdentityAsync(directory, side);
                foreach (var partial in new[] { false, true })
                    foreach (var mapped in new[] { false, true })
                        await CrossLinkedGuardAsync(directory, side, partial, mapped);
            }
            await MappedRecordAsync(directory);
            await MappedCollisionAsync(directory);
            await SavedNestedDriftAsync();
            await ScanQueryBudgetAsync(directory);
            await LocalHashCountsAsync(directory);
            await DropboxHashCountAsync(directory);
            await ReservedStageAsync(directory);
            Measured();
            // Re-observation/fallback and failed restore execution belong to Syncer/runner checks.
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ClassificationsAsync(string directory, Side side)
    {
        foreach (var test in new[]
        {
            (Name: "unchanged", Saved: true, Scanned: true, Restored: false, Verdict: FolderVerdict.Removed),
            (Name: "unsynced", Saved: false, Scanned: true, Restored: false, Verdict: FolderVerdict.Removed),
            (Name: "arrived", Saved: false, Scanned: false, Restored: false, Verdict: FolderVerdict.Survives),
            (Name: "changed", Saved: true, Scanned: true, Restored: false, Verdict: FolderVerdict.Survives),
            (Name: "metadata", Saved: true, Scanned: true, Restored: false, Verdict: FolderVerdict.Removed),
            (Name: "size", Saved: true, Scanned: true, Restored: false, Verdict: FolderVerdict.Survives),
            (Name: "flipped", Saved: true, Scanned: true, Restored: false, Verdict: FolderVerdict.Survives),
            (Name: "flipped-file", Saved: true, Scanned: true, Restored: false, Verdict: FolderVerdict.Survives),
            (Name: "restored", Saved: true, Scanned: true, Restored: true, Verdict: FolderVerdict.Survives),
            (Name: "ignored", Saved: false, Scanned: false, Restored: false, Verdict: FolderVerdict.Removed),
            (Name: "excluded", Saved: false, Scanned: false, Restored: false, Verdict: FolderVerdict.Removed),
            (Name: "skipped", Saved: false, Scanned: false, Restored: false, Verdict: FolderVerdict.Removed),
            (Name: "skipped-folder", Saved: false, Scanned: false, Restored: false, Verdict: FolderVerdict.Removed)
        })
        {
            var pair = Pair() with
            {
                IgnoreRules = ["FileExtension:ignored"],
                Exclusions = [new Exclusion(side, "Parent/excluded", true)]
            };
            using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
            var counted = new StoreCounters(store);
            var context = Context(pair, [pair], counted);
            var source = context.For(side);
            var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
            var path = test.Name == "ignored" ? "Parent/child.ignored" : "Parent/" + test.Name;
            var entry = test.Name is "flipped" or "skipped-folder"
                ? await source.EnsureFolderAsync(path, CancellationToken.None)
                : await FileAsync(source, path);
            var synced = await SavedAsync(source, entry);
            if (test.Name is "skipped" or "skipped-folder")
            {
                entry = entry with { Skip = nameof(TextKey.SkipLink) };
                ((FakeSide)source).Seed(entry, null);
            }
            synced = test.Name switch
            {
                "changed" => synced with { DropboxContentHash = "different-content", LocalModifiedTicks = DateTimeOffset.MinValue.UtcTicks },
                "metadata" => synced with { LocalModifiedTicks = DateTimeOffset.MinValue.UtcTicks, DropboxId = "old-id" },
                "size" => synced with { LocalSize = entry.Size + 1, DropboxContentHash = "different-content" },
                "flipped" => synced with { IsFolder = false },
                "flipped-file" => synced with { IsFolder = true },
                _ => synced
            };
            if (test.Saved)
                store.Commit(pair.PairId, StateChange.Empty with { Record = [synced] });
            await ScanAsync(store, pair.PairId, side, test.Scanned ? [entry] : []);
            context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
            IReadOnlySet<string> restored = test.Restored ? new HashSet<string> { synced.LocalKey } : new HashSet<string>();
            counted.Reset();
            using (var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side, restored,
                false, CancellationToken.None))
            {
                Check.Require(counted.PlanPhases[nameof(FolderPlan.Children)].Items == 1 &&
                    counted.PlanPhases[nameof(FolderPlan.Children)].Succeeded == 1 && counted.CommitCalls == 0,
                    $"{side}/{test.Name}: exactly one child is classified in one complete pass without durable effects");
                Check.Require(plan.DeleteWhole == (test.Verdict == FolderVerdict.Removed),
                    $"{side}/{test.Name}: expected deletion verdict");
                Check.Require(!plan.Branches(CancellationToken.None).Any(),
                    $"{side}/{test.Name}: whole deletion or one protected child needs no branches");
                if (test.Verdict == FolderVerdict.Survives && !test.Restored)
                {
                    var child = plan.Survivors(CancellationToken.None).Single();
                    Check.Require(child.OnDeletingSide == entry && child.Synced == (test.Saved ? synced : null) &&
                        child.Verdict == test.Verdict, $"{side}/{test.Name}: freezes the joined survivor");
                }
                else
                    Check.Require(!plan.Survivors(CancellationToken.None).Any(),
                        $"{side}/{test.Name}: removed or already restored children have no follow-up");
                Check.Require(store.SyncedByKey(pair.PairId, Side.Local, synced.LocalKey) == (test.Saved ? synced : null),
                    $"{side}/{test.Name}: planning preserves saved evidence");
                Check.Require(await source.ReadOneAsync(path, CancellationToken.None) == entry,
                    $"{side}/{test.Name}: planning has no side effects");
            }
            if (test.Verdict == FolderVerdict.Removed)
            {
                var anchor = await source.EnsureFolderAsync("Parent/Anchor", CancellationToken.None);
                using var partial = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side,
                    new HashSet<string> { PathNames.KeyFor(side, anchor.Path) }, false,
                    CancellationToken.None);
                Check.Require(!partial.DeleteWhole && partial.Branches(CancellationToken.None).Single() ==
                    new FolderChild(entry, test.Saved ? synced : null, FolderVerdict.Removed),
                    $"{side}/{test.Name}: the removed branch freezes the exact present/saved join");
            }
        }
    }

    // Design 3.6: a Repair plan consults the recorded scan for an unsaved child (InThisScan decides),
    // while a live plan treats that same in-scope child as outside its scan without any Storage.InScan probe.
    private static async Task LiveMembershipAsync(string directory, Side side)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var counted = new StoreCounters(store);
        var context = Context(pair, [pair], counted);
        var source = context.For(side);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var fresh = await FileAsync(source, "Parent/fresh");
        // The child is recorded in the scan, so a Repair plan would see InScan=true.
        await ScanAsync(store, pair.PairId, side, [fresh]);
        context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
        var folder = Folder(parent, side);

        counted.Reset();
        using (var repair = await new FolderPlanBuilder(context).BuildAsync(folder, side, new HashSet<string>(),
            false, CancellationToken.None))
        {
            Check.Require(repair.DeleteWhole && !repair.Survivors(CancellationToken.None).Any() &&
                !repair.Branches(CancellationToken.None).Any(),
                $"{side}: Repair classifies a scanned unsaved child as removable");
        }
        // AJ-04 checks identity before capture and reuses that absence during classification.
        Check.Require(counted.Commands == 2 + UnreadableAsks,
            $"{side}: Repair probes the recorded scan membership for the unsaved child ({counted.Commands} store commands)");

        counted.Reset();
        using (var live = await new FolderPlanBuilder(context).BuildAsync(folder, side, new HashSet<string>(),
            true, CancellationToken.None))
        {
            Check.Require(!live.DeleteWhole && live.Survivors(CancellationToken.None).Single() ==
                new FolderChild(fresh, null, FolderVerdict.Survives),
                $"{side}: a live plan keeps an unsaved in-scope child outside its scan");
        }
        Check.Require(counted.Commands == 1 + UnreadableAsks,
            $"{side}: a live plan never probes Storage.InScan ({counted.Commands} store command)");
    }

    private static async Task MovedInAsync(string directory, Side side)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var context = Context(pair, [pair], store);
        var source = context.For(side);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var child = await FileAsync(source, "Parent/incoming");
        var original = await SavedAsync(source, child);
        var saved = original with { LocalPath = "Outside/original", LocalKey = PathNames.Key("Outside/original"),
            DropboxPath = "Outside/original", DropboxKey = PathNames.DropboxKey("Outside/original") };
        store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
        foreach (var occupied in new[] { false, true })
        {
            var displaced = occupied ? original with { LocalId = "displaced-local", DropboxId = "displaced-remote" } : null;
            if (displaced is not null) store.Commit(pair.PairId, StateChange.Empty with { Record = [displaced] });
            using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side,
                new HashSet<string>(), true, CancellationToken.None);
            Check.Require(!plan.DeleteWhole && plan.Survivors(CancellationToken.None).Single() ==
                new FolderChild(child, displaced, FolderVerdict.Survives),
                $"{side}: moved-in unchanged bytes survive, occupied={occupied}");
        }
    }

    private static async Task ProtectedDescendantsAsync(string directory, Side side, bool restored)
    {
        var pair = Pair() with { IgnoreRules = ["FileExtension:ignored"] };
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var counted = new StoreCounters(store);
        var context = Context(pair, [pair], counted);
        var source = context.For(side);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var branch = await source.EnsureFolderAsync("Parent/Branch", CancellationToken.None);
        var survivor = await FileAsync(source, "Parent/Branch/survivor");
        var unchanged = await FileAsync(source, "Parent/Branch/unchanged");
        var unsynced = await FileAsync(source, "Parent/Branch/unsynced");
        var ignored = await FileAsync(source, "Parent/Branch/child.ignored");
        var savedSurvivor = await SavedAsync(source, survivor);
        if (!restored)
            savedSurvivor = savedSurvivor with
            {
                DropboxContentHash = "previous-content",
                LocalModifiedTicks = DateTimeOffset.MinValue.UtcTicks
            };
        var savedParent = await SavedAsync(source, parent);
        store.Commit(pair.PairId, StateChange.Empty with
        {
            Record = [savedParent, await SavedAsync(source, branch), savedSurvivor, await SavedAsync(source, unchanged)]
        });
        await ScanAsync(store, pair.PairId, side, [branch, survivor, unchanged, unsynced]);
        context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
        var folder = Folder(parent, side) with { Synced = savedParent };
        if (restored)
        {
            var counterpart = await context.For(side == Side.Local ? Side.Dropbox : Side.Local)
                .EnsureFolderAsync("Parent", CancellationToken.None);
            folder = side == Side.Local ? folder with { Dropbox = counterpart } : folder with { Local = counterpart };
        }
        var restoredKeys = new HashSet<string>();
        if (restored)
            restoredKeys.Add(savedSurvivor.LocalKey);
        counted.Reset();
        using var plan = await new FolderPlanBuilder(context).BuildAsync(folder, side, restoredKeys,
            false, CancellationToken.None);
        Check.Require(counted.PlanPhases[nameof(FolderPlan.Children)].Items == 5,
            "The parent is not a child, and ignored contents are captured");
        Check.Require(!plan.DeleteWhole, "An edited or restored child prevents whole-folder deletion");
        var branches = plan.Branches(CancellationToken.None).ToArray();
        Check.Require(branches.All(child => child.OnDeletingSide != branch),
            "The unchanged intermediate folder is protected");
        Check.Require(branches.Length == 3 && branches.Select(child => child.OnDeletingSide?.Path).ToHashSet()
            .SetEquals(new[] { unchanged.Path, unsynced.Path, ignored.Path }),
            "Only unchanged, unsynced and ignored siblings are deletion branches");
        Check.Require(branches.All(child => child.Verdict == FolderVerdict.Removed),
            "Every selected sibling has a removed verdict");
        if (restored)
            Check.Require(!plan.Survivors(CancellationToken.None).Any(), "An already restored child is not followed up again");
        else
            Check.Require(plan.Survivors(CancellationToken.None).Single() ==
                new FolderChild(survivor, savedSurvivor, FolderVerdict.Survives),
                "The edited child retains its original saved evidence for follow-up");
    }

    private static async Task NestedRootsAsync(string directory, Side side)
    {
        var pair = Pair();
        var nested = pair with
        {
            PairId = "nested-pair",
            LocalRoot = side == Side.Local ? Path.Combine(pair.LocalRoot, "Parent", "Inner") : Path.Combine(pair.LocalRoot, "Other"),
            DropboxRoot = side == Side.Dropbox ? pair.DropboxRoot + "/Parent/Inner" : pair.DropboxRoot + "/Other"
        };
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var context = Context(pair, [pair, nested], store);
        var source = context.For(side);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var root = await source.EnsureFolderAsync("Parent/Inner", CancellationToken.None);
        var child = await FileAsync(source, "Parent/Inner/child");
        var sibling = await FileAsync(source, "Parent/Inner-other");
        await ScanAsync(store, pair.PairId, side, [sibling]);
        context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
        using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side,
            new HashSet<string>(), false, CancellationToken.None);
        var survivors = plan.Survivors(CancellationToken.None).ToArray();
        Check.Require(!plan.DeleteWhole && survivors.Length == 2 &&
            survivors.All(item => item.Verdict == FolderVerdict.Survives) &&
            survivors.Select(item => item.OnDeletingSide).ToHashSet().SetEquals(new[] { root, child }),
            "A nested root and its descendants survive");
        Check.Require(plan.Branches(CancellationToken.None).Single() ==
            new FolderChild(sibling, null, FolderVerdict.Removed),
            "A similarly named sibling is not inside the nested root");
    }

    // A saved-only nested root needs path-based protection even when its stored Dropbox key has drifted.
    private static async Task MappedNestedDriftAsync(string directory, Side side)
    {
        var pair = Pair();
        var nested = pair with
        {
            PairId = "nested-drift",
            LocalRoot = side == Side.Local ? Path.Combine(pair.LocalRoot, "Parent", "Inner") : Path.Combine(pair.LocalRoot, "Other"),
            DropboxRoot = side == Side.Dropbox ? pair.DropboxRoot + "/Parent/Inner" : pair.DropboxRoot + "/Other"
        };
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var context = Context(pair, [pair, nested], store);
        var source = context.For(side);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var root = await source.EnsureFolderAsync("Parent/Inner", CancellationToken.None);
        var saved = await SavedAsync(source, root);
        if (side == Side.Dropbox)
            saved = saved with { DropboxKey = PathNames.DropboxKey("Parent/other") };
        store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
        await source.DeleteAsync(root, null, CancellationToken.None);
        var scope = new PathScope(pair, [pair, nested]);
        var nestedKey = PathNames.KeyFor(side, root.Path);
        Check.Require(scope.HoldsNestedRoot(side, root.Path),
            $"{side}: the saved child is the nested root");
        if (side == Side.Dropbox)
            Check.Require(!PathNames.IsSelfOrUnderKey(saved.KeyOn(side), nestedKey) &&
                !PathNames.IsSelfOrUnder(nestedKey, saved.KeyOn(side)),
                "Dropbox: the drifted stored key alone would not protect the nested root");
        using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side, new HashSet<string>(),
            false, CancellationToken.None);
        Check.Require(!plan.DeleteWhole &&
            !plan.Branches(CancellationToken.None).Any() && !plan.Survivors(CancellationToken.None).Any(),
            $"{side}: a saved-only nested root protects its path without a present-entry follow-up");
        Check.Require(store.SyncedByKey(pair.PairId, side, saved.KeyOn(side)) == saved,
            $"{side}: nested-root protection retains the original saved binding");
    }

    // Against the real store: the saved-descendant query finds the row by its drifted stored key
    // (still under the deleted parent), and the row's saved path strictly beneath a nested root must stop deletion.
    private static async Task SavedNestedDriftAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            var pair = Pair();
            var nested = pair with
            {
                PairId = "saved-nested-drift",
                LocalRoot = Path.Combine(pair.LocalRoot, "Other"),
                DropboxRoot = pair.DropboxRoot + "/Parent/Inner"
            };
            using var store = StateStore.Open(Path.Combine(folder, "nested-drift.db"));
            var context = Context(pair, [pair, nested], store);
            var source = context.For(Side.Dropbox);
            var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
            var savedPath = "Parent/Inner/child";
            var driftedKey = PathNames.DropboxKey("Parent/other");
            Check.Require(PathNames.IsSelfOrUnderKey(driftedKey, PathNames.DropboxKey(parent.Path)) &&
                !PathNames.IsSelfOrUnderKey(driftedKey, PathNames.DropboxKey("Parent/Inner")),
                "the drifted key is under the deleted parent but outside the nested root");
            var saved = new SyncedEntry(PathNames.Key(savedPath), savedPath, driftedKey, savedPath, false,
                "saved-id", 1, DateTimeOffset.UnixEpoch.AddDays(1).UtcTicks, "saved-id", "hash");
            store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
            using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, Side.Dropbox), Side.Dropbox,
                new HashSet<string>(), false, CancellationToken.None);
            Check.Require(!plan.Survivors(CancellationToken.None).Any(),
                "a saved-only nested descendant has no follow-up");
            Check.Require(!plan.DeleteWhole && !plan.Branches(CancellationToken.None).Any(),
                "the protected descendant stops whole-folder deletion and selects no branch");
            Check.Require(store.SyncedByKey(pair.PairId, Side.Dropbox, driftedKey) == saved,
                "the saved record survives planning");
            Check.Require(await source.ReadOneAsync(parent.Path, CancellationToken.None) == parent,
                "planning deletes nothing on the provider");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task EmptyFolderAsync(string directory, Side side)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var context = Context(pair, [pair], store);
        var source = context.For(side);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var absent = await FileAsync(source, "Parent/absent");
        var saved = await SavedAsync(source, absent);
        store.Commit(pair.PairId, StateChange.Empty with { Record = [await SavedAsync(source, parent), saved] });
        await source.DeleteAsync(absent, null, CancellationToken.None);
        using (var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side,
            new HashSet<string>(), false, CancellationToken.None))
        {
            Check.Require(!plan.Survivors(CancellationToken.None).Any(),
                "A saved-only descendant has no follow-up");
            Check.Require(store.SyncedByKey(pair.PairId, side, saved.KeyOn(side)) == saved,
                "Planning retains the saved-only record");
            Check.Require(plan.DeleteWhole, "No survivors permits whole-folder deletion");
        }
        var restored = await source.EnsureFolderAsync("Parent/restored", CancellationToken.None);
        using var partial = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side,
            new HashSet<string> { PathNames.KeyFor(side, restored.Path) }, false,
            CancellationToken.None);
        Check.Require(!partial.DeleteWhole && partial.Branches(CancellationToken.None).Single() ==
            new FolderChild(null, saved, FolderVerdict.Removed),
            "The saved-only descendant retains its joined binding when selected as a partial deletion branch");
    }

    private static async Task ListingFailureAsync(string directory, Side side)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var context = Context(pair, [pair], store);
        var source = (FakeSide)context.For(side);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var child = await FileAsync(source, "Parent/child");
        var saved = new[] { await SavedAsync(source, parent), await SavedAsync(source, child) };
        store.Commit(pair.PairId, StateChange.Empty with { Record = saved });
        var error = new InventoryIncompleteException(Text.Get(
            side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed, (TextArg.Path, parent.Path)), null);
        source.FailNext(parent.Path, error);
        var propagated = false;
        try
        {
            using var unexpected = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side,
                new HashSet<string>(), false, CancellationToken.None);
        }
        catch (ItemFailedException caught) when (ReferenceEquals(caught.InnerException, error))
        {
            propagated = caught.Path == parent.Path &&
                caught.Cause == (side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed);
        }
        Check.Require(propagated, $"{side}: the folder-plan listing error becomes an item failure");
        Check.Require(await source.ReadOneAsync(parent.Path, CancellationToken.None) == parent &&
            await source.ReadOneAsync(child.Path, CancellationToken.None) == child,
            $"{side}: a failed listing deletes neither the folder nor its child");
        Check.Require(saved.All(entry => store.SyncedByKey(pair.PairId, Side.Local, entry.LocalKey) == entry) &&
            store.Issues(pair.PairId).Count == 0 && ((FakeAdmission)context.Admission).Admitted == 0,
            $"{side}: failed planning preserves records and has no mutation side effects");
    }

    // Design 3.8/FREEZE 5.1: every plan owns one unique scratch path and the builder disposes it on success and failure.
    private static async Task ScratchOwnershipAsync(string directory, Side side)
    {
        var pair = Pair();
        var database = Path.Combine(directory, Path.GetRandomFileName());
        using var store = StateStore.Open(database);
        var context = Context(pair, [pair], store);
        var source = (FakeSide)context.For(side);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        await FileAsync(source, "Parent/child");
        await ScanAsync(store, pair.PairId, side, []);
        context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
        using (var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side,
            new HashSet<string>(), false, CancellationToken.None))
        {
            Check.Require(plan.Survivors(CancellationToken.None).Single().OnDeletingSide?.Path == "Parent/child",
                $"{side}: the completed plan classifies its one child");
        }
        Check.Require(!Plans(directory, database).Any(),
            $"{side}: a completed plan deletes its own unique scratch");

        var error = new InventoryIncompleteException(Text.Get(
            side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed, (TextArg.Path, parent.Path)), null);
        source.FailNext(parent.Path, error);
        var failed = false;
        try
        {
            using var unexpected = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side,
                new HashSet<string>(), false, CancellationToken.None);
        }
        catch (ItemFailedException caught) when (ReferenceEquals(caught.InnerException, error))
        {
            failed = true;
        }
        Check.Require(failed && !Plans(directory, database).Any(),
            $"{side}: a failed plan disposes its own scratch without touching foreign data");
    }

    private static IEnumerable<string> Plans(string directory, string database) =>
        Directory.EnumerateFiles(directory, Path.GetFileName(database) + StateSchema.PlanFileSuffix + "*" + StateSchema.DatabaseExtension);

    private static async Task UnreadableChildrenAsync(string directory, Side side)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var countedStore = new StoreCounters(store);
        var local = new CountingSide(new FakeSide(Side.Local));
        var dropbox = new CountingSide(new FakeSide(Side.Dropbox));
        var context = Context(pair, [pair], countedStore, local, dropbox);
        var source = context.For(side);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var branch = await source.EnsureFolderAsync("Parent/Unreadable", CancellationToken.None);
        var present = await FileAsync(source, "Parent/Unreadable/present");
        var sibling = await FileAsync(source, "Parent/sibling");
        var unreadablePath = side == Side.Local ? branch.Path : "Elsewhere";
        var savedPresent = (await SavedAsync(source, present)) with
        {
            LocalPath = unreadablePath + "/present",
            LocalKey = PathNames.Key(unreadablePath + "/present"),
            LocalModifiedTicks = DateTimeOffset.MinValue.UtcTicks
        };
        var savedAbsent = savedPresent with
        {
            LocalPath = unreadablePath + "/absent",
            LocalKey = PathNames.Key(unreadablePath + "/absent"),
            DropboxPath = "Parent/Unreadable/absent",
            DropboxKey = PathNames.DropboxKey("Parent/Unreadable/absent")
        };
        var savedSibling = await SavedAsync(source, sibling);
        store.Commit(pair.PairId, StateChange.Empty with { Record = [savedPresent, savedAbsent, savedSibling] });
        await ScanAsync(store, pair.PairId, side, [branch, present, sibling]);
        context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
        UnreadableMember.Record(store, pair.PairId, unreadablePath);
        local.Hashes = 0;
        dropbox.Hashes = 0;
        countedStore.Reset();
        using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, side), side,
            new HashSet<string>(), false, CancellationToken.None);
        Check.Require(!plan.DeleteWhole && !plan.Survivors(CancellationToken.None).Any() &&
            plan.Branches(CancellationToken.None).Single() == new FolderChild(sibling, savedSibling, FolderVerdict.Removed),
            $"{side}: present and saved-only unreadable children survive without follow-ups; only the sibling is removed");
        Check.Require(local.Hashes == 0 && dropbox.Hashes == 0 && countedStore.CommitCalls == 0 &&
            local.Deletes == 0 && dropbox.Deletes == 0,
            $"{side}: unreadable protection avoids uncertain local hashes and all mutation effects");
        Check.Require(store.SyncedByKey(pair.PairId, Side.Local, savedPresent.LocalKey) == savedPresent &&
            store.SyncedByKey(pair.PairId, Side.Local, savedAbsent.LocalKey) == savedAbsent,
            $"{side}: unreadable protection preserves both saved bindings");
    }

    private static async Task UnreadableIdentityAsync(string directory, Side side)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var counted = new StoreCounters(store);
        var local = new CountingSide(new FakeSide(Side.Local));
        var dropbox = new CountingSide(new FakeSide(Side.Dropbox));
        var context = Context(pair, [pair], counted, local, dropbox);
        var parent = await context.For(side).EnsureFolderAsync("Parent", default);
        var saved = (await SavedAsync(context.For(side), parent)) with
        {
            LocalPath = "Parent", LocalKey = PathNames.Key("Parent")
        };
        store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
        UnreadableMember.Record(store, pair.PairId, "Parent");
        counted.Reset();
        var refused = false;
        try
        {
            using var plan = await new FolderPlanBuilder(context).BuildAsync(
                Folder(parent, side) with { Synced = saved }, side, new HashSet<string>(), false, default);
        }
        catch (ItemFailedException error)
        {
            refused = error.Path == parent.Path && error.Cause == TextKey.ReadLocalFailed;
        }
        Check.Require(local.Listings == 0 && dropbox.Listings == 0 && local.Hashes == 0 && dropbox.Hashes == 0 &&
            counted.Commands == 1 && counted.PlanPhases.Count == 0,
            "AJ-04: unreadable identity refuses before opening scratch, listing or hashing with one identity read.");
        Check.Require(refused && await context.For(side).ReadOneAsync(parent.Path, default) == parent &&
            store.SyncedByKey(pair.PairId, Side.Local, saved.LocalKey) == saved,
            "EX-10: an unreadable folder identity refuses without deleting the folder or forgetting its binding.");
    }

    private static async Task MappedRecordAsync(string directory)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var context = Context(pair, [pair], store);
        var parent = await context.Dropbox.EnsureFolderAsync("Parent", CancellationToken.None);
        var child = await FileAsync(context.Dropbox, "Parent/remote-child");
        var saved = await SavedAsync(context.Dropbox, child);
        saved = saved with { LocalPath = "Parent/local-child", LocalKey = PathNames.Key("Parent/local-child") };
        store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
        await ScanAsync(store, pair.PairId, Side.Dropbox, [child]);
        context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
        using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, Side.Dropbox), Side.Dropbox,
            new HashSet<string> { saved.LocalKey }, false, CancellationToken.None);
        Check.Require(!plan.DeleteWhole &&
            !plan.Branches(CancellationToken.None).Any() && !plan.Survivors(CancellationToken.None).Any(),
            "Join by the deleting side's key, but find restored linked rows by their local row key without another follow-up");
        Check.Require(store.SyncedByKey(pair.PairId, Side.Dropbox, saved.DropboxKey) == saved,
            "Planning preserves the mapped saved binding");
    }

    private static async Task MappedCollisionAsync(string directory)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var context = Context(pair, [pair], store);
        var parent = await context.Dropbox.EnsureFolderAsync("Parent", CancellationToken.None);
        // Core name mapping is dormant; the inventory carries these non-transferable Dropbox names as skipped.
        // D37 phase 2 restores the mapping and the mapped-collision survivor contract with it.
        var colon = (await FileAsync(context.Dropbox, "Parent/item:")) with { Skip = nameof(TextKey.SkipReservedName) };
        var question = (await FileAsync(context.Dropbox, "Parent/item?")) with { Skip = nameof(TextKey.SkipReservedName) };
        ((FakeSide)context.Dropbox).Seed(colon, [1]);
        ((FakeSide)context.Dropbox).Seed(question, [1]);
        var absent = await FileAsync(context.Dropbox, "Parent/absent");
        var savedColon = (await SavedAsync(context.Dropbox, colon)) with { DropboxContentHash = "previous-content" };
        var savedAbsent = await SavedAsync(context.Dropbox, absent);
        store.Commit(pair.PairId, StateChange.Empty with { Record = [savedColon, savedAbsent] });
        await context.Dropbox.DeleteAsync(absent, null, CancellationToken.None);
        await ScanAsync(store, pair.PairId, Side.Dropbox, [colon, question]);
        context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
        using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, Side.Dropbox), Side.Dropbox,
            new HashSet<string>(), false, CancellationToken.None);
        Check.Require(!plan.Survivors(CancellationToken.None).Any() && plan.DeleteWhole &&
            !plan.Branches(CancellationToken.None).Any(),
            "Dormant name mapping: non-transferable Dropbox names are skipped, never mapped survivors, " +
            "so the whole folder is deleted without a branch.");
    }

    // Repair scan evidence is probed only for a child without a saved record.
    private static async Task ScanQueryBudgetAsync(string directory)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var counted = new StoreCounters(store);
        var context = Context(pair, [pair], counted);
        var source = context.For(Side.Dropbox);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var savedChild = await FileAsync(source, "Parent/saved");
        await FileAsync(source, "Parent/fresh");
        store.Commit(pair.PairId, StateChange.Empty with { Record = [await SavedAsync(source, savedChild)] });
        await ScanAsync(store, pair.PairId, Side.Dropbox, []);
        context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
        counted.Reset();
        using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, Side.Dropbox), Side.Dropbox,
            new HashSet<string>(), false, CancellationToken.None);
        Check.Require(!plan.DeleteWhole &&
            plan.Survivors(CancellationToken.None).Single().OnDeletingSide?.Path == "Parent/fresh" &&
            plan.Branches(CancellationToken.None).Single().OnDeletingSide == savedChild,
            "The saved child is removable while the fresh unsaved child survives");
        Check.Require(counted.Commands == 2 + UnreadableAsks,
            $"One plan open plus one scan probe for the single unsaved child ({counted.Commands} store commands)");
    }

    private static void Measured()
    {
        var childKey = PathNames.Child(PathNames.Child("Parent", "Inner"), "child");
        var rootKey = PathNames.Child("Parent", "Inner");
        Check.Require(PathNames.IsSelfOrUnderKey(childKey, rootKey), "key probe precondition: the child is under the root");
        var keyOwner = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(PathNames.IsSelfOrUnderKey(childKey, rootKey));
        });
        var keyInline = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
                Sink(childKey == rootKey || childKey.StartsWith(rootKey + PathNames.PathSeparator, StringComparison.Ordinal));
        });
        Check.Require(keyOwner <= keyInline,
            $"IsSelfOrUnderKey allocates no more than the removed inline Under ({keyOwner} B vs {keyInline} B)");

        var pair = Pair();
        var nested = pair with
        {
            PairId = "measured-nested",
            LocalRoot = Path.Combine(pair.LocalRoot, "Parent", "Inner"),
            DropboxRoot = pair.DropboxRoot + "/Parent/Inner"
        };
        var scope = new PathScope(pair, [pair, nested]);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var path = "Parent/Inner/child";
            var key = PathNames.KeyFor(side, path);
            var roots = scope.NestedRoots(side);
            Check.Require(scope.OverlapsNestedRoot(side, path, key), $"{side}: nested-overlap probe precondition");
            var overlapOwner = AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++) Sink(scope.OverlapsNestedRoot(side, path, key));
            });
            var overlapInline = AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++)
                    Sink(scope.HoldsNestedRoot(side, path) ||
                        roots.Any(root => Under(key, PathNames.KeyFor(side, root))));
            });
            Check.Require(overlapOwner <= overlapInline,
                $"{side}: OverlapsNestedRoot allocates no more than the removed inline test " +
                $"({overlapOwner} B vs {overlapInline} B)");
        }

        var saved = new SyncedEntry("Parent/local", "Parent/local", "Parent/dropbox", "Parent/dropbox",
            false, "id", 1, 1, "id", "hash");
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var keyOnOwner = AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++) StringSink = saved.KeyOn(side);
            });
            var keyOnInline = AllocProbe.Measure(index =>
            {
                for (var i = 0; i < 1_000; i++) StringSink = side == Side.Local ? saved.LocalKey : saved.DropboxKey;
            });
            Check.Require(keyOnOwner == keyOnInline,
                $"{side}: KeyOn returns the stored reference ({keyOnOwner} B vs {keyOnInline} B)");
        }

        var entry = new Entry("Parent/child", false, 1, DateTimeOffset.UnixEpoch, "id", "", "", null);
        var rowContext = new RowContext(false, false, false, Side.Dropbox, false, false, true, false);
        var forOneSide = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
                Sink(Row.ForOneSide(Side.Dropbox, "Parent/child", entry, null, [], null, rowContext));
        });
        var directRow = AllocProbe.Measure(index =>
        {
            for (var i = 0; i < 1_000; i++)
                Sink(new Row("Parent/child", null, entry, null, [], null, rowContext));
        });
        Check.Require(forOneSide == directRow,
            $"ForOneSide makes the same one Row allocation ({forOneSide} B vs {directRow} B)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(bool value) => BoolSink = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(Row? value) => RowSink = value;

    private static bool Under(string key, string parent) =>
        key == parent || key.StartsWith(parent + PathNames.PathSeparator, StringComparison.Ordinal);

    internal static bool BoolSink;
    internal static string? StringSink;
    internal static Row? RowSink;

    private static async Task LocalHashCountsAsync(string directory)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var countedStore = new StoreCounters(store);
        var counted = new CountingSide(new FakeSide(Side.Local));
        var context = Context(pair, [pair], countedStore, counted, new FakeSide(Side.Dropbox));
        var source = context.For(Side.Local);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var unchanged = await FileAsync(source, "Parent/unchanged");
        var uncertain = await FileAsync(source, "Parent/uncertain");
        var savedUnchanged = await SavedAsync(source, unchanged);
        var savedUncertain = (await SavedAsync(source, uncertain)) with
        {
            LocalModifiedTicks = DateTimeOffset.MinValue.UtcTicks
        };
        store.Commit(pair.PairId, StateChange.Empty with { Record = [savedUnchanged, savedUncertain] });
        await ScanAsync(store, pair.PairId, Side.Local, [unchanged, uncertain]);
        context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
        counted.Hashes = 0;
        countedStore.Reset();
        using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, Side.Local), Side.Local,
            new HashSet<string>(), false, CancellationToken.None);
        Check.Require(countedStore.PlanPhases[nameof(FolderPlan.Children)].Items == 2 && plan.DeleteWhole &&
            !plan.Survivors(CancellationToken.None).Any(), "Both equal-content local children are removable");
        Check.Require(counted.Hashes == 1,
            "An unchanged record hashes nothing; a same-size uncertain local file hashes exactly once");
    }

    private static async Task DropboxHashCountAsync(string directory)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var counted = new CountingSide(new FakeSide(Side.Dropbox));
        var context = Context(pair, [pair], store, new FakeSide(Side.Local), counted);
        var source = context.For(Side.Dropbox);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var changed = await FileAsync(source, "Parent/changed");
        var saved = (await SavedAsync(source, changed)) with { DropboxContentHash = "different-content" };
        store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
        await ScanAsync(store, pair.PairId, Side.Dropbox, [changed]);
        context = context with { ScanId = ScanFixture.Token(store, pair.PairId) };
        counted.Hashes = 0;
        using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, Side.Dropbox), Side.Dropbox,
            new HashSet<string>(), false, CancellationToken.None);
        Check.Require(!plan.DeleteWhole && plan.Survivors(CancellationToken.None).Single() ==
            new FolderChild(changed, saved, FolderVerdict.Survives),
            "A Dropbox metadata change survives without local hashing");
        Check.Require(counted.Hashes == 0, "Dropbox classification never hashes local content");
    }

    private static async Task CrossLinkedGuardAsync(string directory, Side side, bool partial, bool mapped)
    {
        var pair = Pair();
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var countedStore = new StoreCounters(store);
        var local = new CountingSide(new FakeSide(Side.Local));
        var dropbox = new CountingSide(new FakeSide(Side.Dropbox));
        var context = Context(pair, [pair], countedStore, local, dropbox);
        var source = context.For(side);
        var parent = await source.EnsureFolderAsync("Parent", CancellationToken.None);
        var target = partial
            ? await source.EnsureFolderAsync("Parent/Branch", CancellationToken.None)
            : parent;
        var saved = await SavedAsync(source, target);
        if (mapped)
            saved = side == Side.Local
                ? saved with { DropboxPath = "Elsewhere", DropboxKey = PathNames.DropboxKey("Elsewhere") }
                : saved with { LocalPath = "Elsewhere", LocalKey = PathNames.Key("Elsewhere") };
        var protectedLocal = side == Side.Local ? "Unreadable/child" : saved.LocalPath + "/Unreadable/child";
        var protectedDropbox = side == Side.Local ? saved.DropboxPath + "/child" : "Outside/child";
        var protectedSaved = new SyncedEntry(PathNames.Key(protectedLocal), protectedLocal,
            PathNames.DropboxKey(protectedDropbox), protectedDropbox, false,
            "protected-local", 1, DateTimeOffset.UnixEpoch.UtcTicks, "protected-dropbox", "hash");
        store.Commit(pair.PairId, StateChange.Empty with { Record = [saved, protectedSaved] });
        var restored = new HashSet<string>();
        Entry? survivor = null;
        if (partial)
        {
            survivor = await source.EnsureFolderAsync("Parent/Survivor", CancellationToken.None);
            restored.Add(PathNames.KeyFor(side, survivor.Path));
        }
        var folder = Folder(parent, side) with { Synced = partial ? null : saved };
        UnreadableMember.Record(store, pair.PairId, PathNames.Parent(protectedLocal));
        Check.Require(!PathNames.IsSelfOrUnderKey(protectedSaved.KeyOn(side), PathNames.KeyFor(side, parent.Path)),
            $"{side}/{partial}/{mapped}: the protected binding is outside the deleting-side child universe");
        countedStore.Reset();
        var refused = false;
        try
        {
            using var unexpected = await new FolderPlanBuilder(context).BuildAsync(folder, side, restored,
                false, CancellationToken.None);
        }
        catch (ItemFailedException error)
        {
            refused = error.Path == parent.Path && error.Cause == Text.ReadFailedOn(Side.Local) &&
                error.InnerException is null;
        }
        Check.Require(refused, $"{side}/{partial}/{mapped}: the exact cross-linked forget footprint refuses the plan");
        Check.Require(countedStore.PlanPhases[nameof(FolderPlan.Complete)].Succeeded == 1 &&
            countedStore.PlanPhases[nameof(FolderPlan.ForgetTouchesUnreadable)].Succeeded == 1 &&
            countedStore.PlanPhases[nameof(FolderPlan.Dispose)].Succeeded == 1,
            $"{side}/{partial}/{mapped}: the frozen plan is guarded and disposed before the refusal escapes");
        Check.Require(countedStore.CommitCalls == 0 && local.Deletes == 0 && dropbox.Deletes == 0 &&
            local.Hashes == 0 && dropbox.Hashes == 0,
            $"{side}/{partial}/{mapped}: a refused folder-only plan neither hashes, deletes nor forgets");
        Check.Require(store.SyncedByKey(pair.PairId, Side.Local, saved.LocalKey) == saved &&
            store.SyncedByKey(pair.PairId, Side.Local, protectedSaved.LocalKey) == protectedSaved,
            $"{side}/{partial}/{mapped}: both the selected binding and the cross-linked protected binding are retained");
        Check.Require(await source.ReadOneAsync(parent.Path, CancellationToken.None) == parent &&
            await source.ReadOneAsync(target.Path, CancellationToken.None) == target &&
            (survivor is null || await source.ReadOneAsync(survivor.Path, CancellationToken.None) == survivor),
            $"{side}/{partial}/{mapped}: guard refusal preserves every synthetic provider object");
        UnreadableMember.Release(store, pair.PairId, PathNames.Parent(protectedLocal));
        using var readable = await new FolderPlanBuilder(context).BuildAsync(folder, side, restored,
            false, CancellationToken.None);
        Check.Require(readable.DeleteWhole == !partial && !readable.Survivors(CancellationToken.None).Any(),
            $"{side}/{partial}/{mapped}: the next readable attempt can plan the same deletion without a persistent hold");
        if (partial)
            Check.Require(readable.Branches(CancellationToken.None).Single() ==
                new FolderChild(target, saved, FolderVerdict.Removed),
                $"{side}/{partial}/{mapped}: the readable partial retry retains the selected branch's two-sided binding");
        else
            Check.Require(!readable.Branches(CancellationToken.None).Any(),
                $"{side}/{partial}/{mapped}: the readable whole deletion needs no branch projection");
    }

    private static async Task ReservedStageAsync(string directory)
    {
        var localRoot = Path.Combine(directory, Path.GetRandomFileName());
        var excluded = Path.Combine(localRoot, "Parent", "Excluded");
        Directory.CreateDirectory(excluded);
        var stage = StageNames.For(Path.Combine(excluded, "target"));
        var lookalike = Path.Combine(excluded, ".syncshark-not-an-id.stage");
        await File.WriteAllTextAsync(stage, "stage-content");
        File.SetAttributes(stage, FileAttributes.Hidden);
        await File.WriteAllTextAsync(lookalike, "user-content");
        var pair = Pair() with
        {
            LocalRoot = localRoot,
            Exclusions = [new Exclusion(Side.Local, "Parent/Excluded", true)]
        };
        using var store = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
        var countedStore = new StoreCounters(store);
        var context = Context(pair, [pair], countedStore);
        var local = new CountingSide(new LocalSide(pair, context.Scope, context.Settings,
            new PendingNotes(countedStore, pair.PairId, context.Settings.StoragePageSize, mutation: false),
            directory,
            (_, _, _, _) => throw new InvalidOperationException("Planning must not report an inventory failure")));
        context = context with { Local = local };
        var parent = await local.ReadOneAsync("Parent", CancellationToken.None) ??
            throw new InvalidOperationException("The synthetic parent must exist");
        countedStore.Reset();
        using var plan = await new FolderPlanBuilder(context).BuildAsync(Folder(parent, Side.Local), Side.Local,
            new HashSet<string> { PathNames.Key("Parent/Excluded") }, false, CancellationToken.None);
        var ordinaryPath = "Parent/Excluded/" + Path.GetFileName(lookalike);
        Check.Require(countedStore.PlanPhases[nameof(FolderPlan.Children)].Items == 2,
            "Out-of-scope capture suppresses the reserved stage but retains the ordinary lookalike");
        Check.Require(!plan.DeleteWhole && !plan.Survivors(CancellationToken.None).Any() &&
            plan.Branches(CancellationToken.None).Single().OnDeletingSide?.Path == ordinaryPath,
            "Neither a survivor follow-up nor a selected user-entry effect exposes the reserved stage");
        Check.Require(local.Listings == 1 && local.Hashes == 0 && local.Deletes == 0 && File.Exists(stage),
            "Stage suppression belongs to the single LocalSide listing, without planning effects or cleanup");
    }

    private static PairConfig Pair() => new("pair", "Pair A", Path.Combine(Path.GetTempPath(), "engine-folder-plan"),
        "/PairRoot", "connection", [], [], FirstRunPolicy.NewestWins, true, null);

    private static ExecutorContext Context(PairConfig pair, IReadOnlyList<PairConfig> pairs, IStateStore store) =>
        Context(pair, pairs, store, new FakeSide(Side.Local), new FakeSide(Side.Dropbox));

    private static ExecutorContext Context(PairConfig pair, IReadOnlyList<PairConfig> pairs, IStateStore store,
        ISide local, ISide dropbox)
    {
        pair = SourceFixture.Bound(pair);
        store.SavePair(pair);
        SourceFixture.AcquireJournal(store, pair);
        var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
        return new ExecutorContext(
            Local: local, Dropbox: dropbox, Recovery: new FakeRecovery(), Store: store,
            Pair: pair, Scope: new PathScope(pair, pairs), Settings: Settings(), Runtime: new ReplaceTurn(),
            Admission: new FakeAdmission(), Priority: false,
            Events: _ => throw new InvalidOperationException("Planning must not emit mutation events"),
            ReserveDownloadAsync: (_, _, _, _) =>
                throw new InvalidOperationException("Planning must not reserve a download"),
            Time: TimeProvider.System,
            ReadDeletionEvidenceAsync: (_, _, _) =>
                throw new InvalidOperationException("Planning must not read deletion evidence"),
            BeginDownloadAttempt: _ =>
                throw new InvalidOperationException("Planning must not begin a download attempt"),
            CompleteDownloadAttemptAsync: (_, _) =>
                throw new InvalidOperationException("Planning must not complete a download attempt"),
            UploadAdmissionIdentity: (AccountId: "account", RouteName: "route", SourceIdentity: "source"),
            ItemAttempts: new RefusingItemAttempts(), Opportunity: new object(), ExpectedGeneration: generation)
        { ScanId = ScanFixture.TokenOrNone(store, pair.PairId) };
    }

    private static EngineSettings Settings() => new("", "", "", "", "", RecycleFallback.Fail, 1,
        FirstRunPolicy.NewestWins, 1, "", LogBufferCharacters, ScanTestSettings.BatchRows);

    private sealed class RefusingItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            throw new NotSupportedException("Folder planning runs no item attempt");

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode) =>
            throw new NotSupportedException("Folder planning reports no item failure");

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
            throw new NotSupportedException("Folder planning completes no item attempt");
    }

    private sealed class CountingSide(ISide inner) : ISide
    {
        public int Hashes { get; set; }
        public int Listings { get; private set; }
        public int Deletes { get; private set; }

        public Side Side => inner.Side;

        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct)
        {
            Listings++;
            return inner.ReadAllAsync(under, options, ct);
        }

        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => inner.ReadOneAsync(path, ct);

        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            inner.OpenReadAsync(entry, attempt, ct);

        public async Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
        {
            Hashes++;
            return await inner.ContentHashAsync(entry, ct);
        }

        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) =>
            inner.StageAsync(path, content, source, ct);

        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct) => inner.PublishAsync(staged, expected, selection, ct);

        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);

        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
        {
            Deletes++;
            return inner.DeleteAsync(expected, selection, ct);
        }

        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
            inner.MoveAsync(expected, newPath, ct);

        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);

        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct) =>
            inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }

    private static Row Folder(Entry entry, Side side) => new(PathNames.Key(entry.Path),
        side == Side.Local ? entry : null, side == Side.Dropbox ? entry : null, null, [], null,
        new RowContext(false, false, false, null, false, false, true, false));

    private static async Task<Entry> FileAsync(ISide side, string path)
    {
        var bytes = Encoding.UTF8.GetBytes("file-content");
        var source = new Entry(path, false, bytes.Length, DateTimeOffset.UnixEpoch.AddDays(1), "source-id", "", "", null);
        using var content = new MemoryStream(bytes);
        await using var staged = await side.StageAsync(path, content, source, CancellationToken.None);
        return (await side.PublishAsync(staged, null, null, CancellationToken.None)).Final!;
    }

    private static async Task<SyncedEntry> SavedAsync(ISide side, Entry entry)
    {
        var localPath = side.Side == Side.Local ? entry.Path : PathNames.LocalSpelling(entry.Path);
        var hash = entry.IsFolder ? "" : await side.ContentHashAsync(entry, CancellationToken.None);
        return new SyncedEntry(PathNames.Key(localPath), localPath, PathNames.DropboxKey(entry.Path), entry.Path,
            entry.IsFolder, entry.Id, entry.Size, entry.Modified.UtcTicks, entry.Id, hash);
    }

    private static async Task ScanAsync(IStateStore store, string pairId, Side side, IReadOnlyList<Entry> entries)
    {
        var generation = store.ReadPairExecution(pairId)!.Value.Generation;
        store.BeginScan(pairId, generation, includeDeleted: false);
        var sink = store.OpenScan(pairId, side, ScanTestSettings.BatchRows);
        foreach (var entry in entries)
            sink.AddAsync(new Listed(entry.Path, entry), default).GetAwaiter().GetResult();
        await sink.CompleteAsync();
    }
}
