using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Exercises disposable SQLite folder captures and frozen deletion settlement through real and fake storage.</summary>
public static class StoredFolderPlanChecks
{
    private const string Pair = "plan-pair";
    private static readonly DateTimeOffset Modified = DateTimeOffset.UnixEpoch;
    private const int LogBufferCharacters = 262144;
    private static readonly RowContext Context = new(false, false, false, null, false, false, false, false);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            using IStateStore store = StateStore.Open(Path.Combine(directory, "state.db"));
            store.SavePair(Config(directory, Pair));
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                await CaptureCasesAsync(store, side);
                await JoinCasesAsync(store, side);
                await PhaseCasesAsync(store, side);
                await CaptureFailureCasesAsync(store, side, directory);
                await CancellationCasesAsync(store, side);
                await SelectionCasesAsync(store, side);
                foreach (var reverse in new[] { false, true })
                    await FrozenFactsAsync(store, side, reverse);
                await FrozenPagesAsync(store, side);
                await GuardCasesAsync(store, side);
                await FootprintCasesAsync(store, side);
            }
            await CompletionKeepsUnrepresentedHistoryAsync(directory);
            ReincludePendingCustody(directory);
            ReincludeKeepsHistory(directory);
            await BatchSettlementUsesCurrentViewAsync(directory);
            await MappedRootAsync(store);
            await FormerlyCollidingSpellingsAsync(store);
            await SavedPathDriftAsync(store);
            await JoinedRestartAsync(directory);
            await LifetimeCasesAsync(store, directory);
            await ReadOnlyPreviewAsync(directory);
        }
        finally
        {
            // Disposable plans may leave read-only scratch files; normalize them before the recursive cleanup.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CompletionKeepsUnrepresentedHistoryAsync(string directory)
    {
        foreach (var real in new[] { true, false })
        foreach (var side in Enum.GetValues<Side>())
        {
            using var capture = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
            using IStateStore store = real ? StateStore.Open(Path.Combine(directory, Path.GetRandomFileName())) : new InMemoryStateStore();
            var pair = Config(directory, Pair);
            capture.SavePair(pair);
            store.SavePair(pair);
            SourceFixture.AcquireJournal(store, pair);
            var root = Item("root", folder: true);
            var savedRoot = SyncedEntries.From(root, root);
            capture.Commit(Pair, StateChange.Empty with { Record = [savedRoot] });
            store.Commit(Pair, StateChange.Empty with { Record = [savedRoot] });
            var row = Row.ForOneSide(side, PathNames.KeyFor(side, root.Path), root, savedRoot, [], null, Context);
            using var plan = capture.OpenFolderPlan(Pair, row, side);
            await plan.CaptureAsync(Source([]), default);
            foreach (var child in plan.Children(default)) MarkRemoved(plan, child);
            plan.Complete(true, default);
            var operation = store.BeginFolderRemoval(Pair, plan, 1, default);
            var effects = store.ReadyFolderEffects(Pair, operation, -1, int.MaxValue).ToArray();
            var submissionId = store.ArmFolderSubmission(Pair, effects);
            var submission = store.FolderSubmission(submissionId);
            store.AdvanceFolderSubmission(submission, submission with
            {
                Generation = submission.Generation + 1,
                Phase = FolderSubmission.SubmissionPhase.Terminal
            }, effects.Select((effect, index) => (FolderMemberResult)new FolderMemberResult.Receipt(index, effect.Expected!)).ToArray());
            foreach (var item in store.FolderRemovalItems(Pair, operation, -1, int.MaxValue).ToArray())
            {
                var settled = item with
                {
                    Generation = item.Generation + 1, EffectGeneration = item.EffectGeneration + 1,
                    EffectState = FolderRemovalItem.EffectStatus.Settled, Phase = FolderRemovalItem.RecoveryPhase.Settled
                };
                foreach (var malformed in new[]
                {
                    settled with { Verdict = FolderVerdict.Survives },
                    settled with { FollowUpState = FolderRemovalItem.FollowUpStatus.Pending },
                    settled with { StageAllocation = new StageAllocation(operation, item.Ordinal, "allocation", "stage") },
                    settled with { Stage = new StageProof("stage", "volume", "file", 1, "hash") }
                })
                {
                    Exception? error = null;
                    try { store.SettleFolderRemovalItem(Pair, operation, item.Ordinal, item.Generation, malformed,
                        StateChange.Empty with { ForgetSubtrees = [(side, savedRoot.KeyOn(side))] }); }
                    catch (InvalidOperationException e) { error = e; }
                    Check.Require(error?.GetType() == typeof(InvalidOperationException) &&
                        store.FolderRemovalItems(Pair, operation, -1, int.MaxValue).Single(i => i.Ordinal == item.Ordinal) == item &&
                        store.SyncedByKey(Pair, Side.Local, savedRoot.LocalKey) == savedRoot,
                        $"Malformed verdict, follow-up or stage custody is a programmer error without settlement ({real}, {side}).");
                }
                store.SettleFolderRemovalItem(Pair, operation, item.Ordinal, item.Generation, settled, StateChange.Empty);
            }
            var state = store.FolderRemoval(Pair, operation);
            var checking = state with { Generation = state.Generation + 1, Phase = FolderRemovalPhase.Checking };
            store.AdvanceFolderRemoval(Pair, operation, state.Generation, state.Phase, checking);
            var cross = SyncedEntries.From(Item(side == Side.Local ? "outside/child" : "root/child"),
                Item(side == Side.Dropbox ? "outside/child" : "root/child"));
            store.Commit(Pair, StateChange.Empty with { Record = [cross] });
            var retirement = StateChange.Empty with
            {
                ForgetSubtrees = [(Side.Local, savedRoot.LocalKey), (Side.Dropbox, savedRoot.DropboxKey)]
            };
            var refused = false;
            try { store.CompleteFolderRemoval(Pair, operation, checking.Generation, retirement, null); }
            catch (FolderStateRefusedException) { refused = true; }
            Check.Require(refused && store.FolderRemoval(Pair, operation).Active &&
                store.SyncedByKey(Pair, Side.Local, savedRoot.LocalKey) == savedRoot &&
                store.SyncedByKey(Pair, Side.Local, cross.LocalKey) == cross &&
                store.SyncedByKey(Pair, Side.Dropbox, cross.DropboxKey) == cross,
                $"Folder completion refuses opposite-prefix history absent from its frozen members ({real}, {side}).");
            var generation = store.ReadPairExecution(Pair)!.Value.Generation;
            refused = false;
            try { store.SavePairDecision(pair, generation, pair, null, false, savedRoot.LocalKey); }
            catch (InvalidOperationException) { refused = true; }
            Check.Require(refused && store.ReadPairExecution(Pair)!.Value.Generation == generation &&
                store.SyncedByKey(Pair, Side.Local, cross.LocalKey) == cross &&
                store.FolderRemoval(Pair, operation).Active,
                $"Re-inclusion refuses active folder custody without resetting history ({real}, {side}).");
            store.Commit(Pair, StateChange.Empty with { ForgetSelf = [(Side.Local, cross.LocalKey)] });
            var changed = savedRoot with { LocalId = "changed-local-id" };
            store.Commit(Pair, StateChange.Empty with { Record = [changed] });
            refused = false;
            try { store.CompleteFolderRemoval(Pair, operation, checking.Generation, retirement, null); }
            catch (FolderStateRefusedException) { refused = true; }
            Check.Require(refused && store.FolderRemoval(Pair, operation).Active &&
                store.SyncedByKey(Pair, Side.Local, savedRoot.LocalKey) == changed &&
                store.SyncedByKey(Pair, Side.Dropbox, savedRoot.DropboxKey) == changed &&
                store.SyncedByKey(Pair, Side.Dropbox, cross.DropboxKey) is null,
                "A changed exact saved preimage refuses completion without changing history.");
        }
    }

    private static async Task BatchSettlementUsesCurrentViewAsync(string directory)
    {
        foreach (var real in new[] { true, false })
        foreach (var side in Enum.GetValues<Side>())
        foreach (var record in new[] { true, false })
        {
            using var capture = StateStore.Open(Path.Combine(directory, Path.GetRandomFileName()));
            using IStateStore store = real ? StateStore.Open(Path.Combine(directory, Path.GetRandomFileName())) : new InMemoryStateStore();
            var pair = Config(directory, Pair);
            capture.SavePair(pair);
            store.SavePair(pair);
            SourceFixture.AcquireJournal(store, pair);
            var root = Item("root", folder: true);
            var child = Item("root/child");
            var savedRoot = SyncedEntries.From(root, root);
            var savedChild = SyncedEntries.From(child, child);
            var initial = StateChange.Empty with { Record = [savedRoot, savedChild] };
            capture.Commit(Pair, initial);
            store.Commit(Pair, initial);
            using var plan = capture.OpenFolderPlan(Pair,
                Row.ForOneSide(side, PathNames.KeyFor(side, root.Path), root, savedRoot, [], null, Context), side);
            await plan.CaptureAsync(Source([Present(child)]), default);
            Complete(plan);
            var operation = store.BeginFolderRemoval(Pair, plan, 2, default);
            var effects = store.ReadyFolderEffects(Pair, operation, -1, int.MaxValue).ToArray();
            var submissionId = store.ArmFolderSubmission(Pair, effects);
            var submission = store.FolderSubmission(submissionId);
            store.AdvanceFolderSubmission(submission, submission with
            {
                Generation = submission.Generation + 1, Phase = FolderSubmission.SubmissionPhase.Terminal
            }, effects.Select((effect, index) => (FolderMemberResult)new FolderMemberResult.Receipt(index, effect.Expected!)).ToArray());
            var members = store.FolderRemovalItems(Pair, operation, -1, int.MaxValue).ToArray();
            var childMember = members.Single(item => item.Path == child.Path);
            var rootMember = members.Single(item => item.Path == root.Path);
            FolderRemovalItem Advanced(FolderRemovalItem item) => item with { Generation = item.Generation + 1 };
            var changed = savedChild with { LocalId = "changed-child" };
            var first = record ? StateChange.Empty with { Record = [changed] } :
                StateChange.Empty with { ForgetSelf = [(Side.Local, savedChild.LocalKey)] };
            var result = store.SettleFolderRemovalItems(Pair, operation,
            [
                (childMember.Generation, Advanced(childMember), first),
                (rootMember.Generation, Advanced(rootMember), StateChange.Empty with
                { ForgetSubtrees = [(Side.Local, savedRoot.LocalKey), (Side.Dropbox, savedRoot.DropboxKey)] })
            ]);
            Check.Require(result.SequenceEqual(new[] { true, !record }) &&
                store.SyncedByKey(Pair, Side.Local, savedChild.LocalKey) == (record ? changed : null) &&
                store.SyncedByKey(Pair, Side.Local, savedRoot.LocalKey) == (record ? savedRoot : null),
                $"Batch validation sees earlier accepted preimages and removals ({real}, {side}, {record}).");
        }
    }

    private static void ReincludeKeepsHistory(string directory)
    {
        foreach (var real in new[] { true, false })
        foreach (var side in Enum.GetValues<Side>())
        foreach (var keep in new[] { true, false })
        {
            var database = Path.Combine(directory, Path.GetRandomFileName());
            using IStateStore store = real ? StateStore.Open(database) : new InMemoryStateStore();
            var pair = Config(directory, Pair) with { Exclusions = [new Exclusion(side, "Root", keep)] };
            store.SavePair(pair);
            SourceFixture.AcquireJournal(store, pair);
            var rows = new[] { SyncedEntries.From(Item("Root", folder: true), Item("Root", folder: true)),
                SyncedEntries.From(Item("Root/Child"), Item("Root/Child")),
                SyncedEntries.From(Item("Root/CrossLocal"), Item("Outside/CrossLocal")),
                SyncedEntries.From(Item("Outside/CrossRemote"), Item("Root/CrossRemote")),
                SyncedEntries.From(Item("Sibling"), Item("Sibling")) };
            store.Commit(Pair, StateChange.Empty with { Record = rows });
            var firstRepairDone = store.FirstRepairDone(Pair);
            var generation = store.ReadPairExecution(Pair)!.Value.Generation;
            store.SavePairDecision(pair, generation, pair with { Exclusions = [] }, null, false, PathNames.Key("root"));
            Check.Require(store.Pair(Pair)!.Exclusions.Count == 0 &&
                store.ReadPairExecution(Pair)!.Value.Generation == generation + 1 && store.FirstRepairDone(Pair) == firstRepairDone,
                "Re-inclusion changes scope and generation without restarting first Repair.");
            foreach (var before in rows)
            {
                Check.Require(store.SyncedByKey(Pair, Side.Local, before.LocalKey) == before &&
                    store.SyncedByKey(Pair, Side.Dropbox, before.DropboxKey) == before,
                    $"Re-inclusion keeps root, same-area, cross-boundary and sibling history ({real}, {side}, {keep}).");
                if (real)
                {
                    using var reopened = StateStore.Open(database);
                    Check.Require(reopened.SyncedByKey(Pair, Side.Local, before.LocalKey) == before,
                        "Re-included history survives reopening real storage.");
                }
            }
        }
    }

    private static void ReincludePendingCustody(string directory)
    {
        foreach (var real in new[] { true, false })
        {
            using IStateStore store = real ? StateStore.Open(Path.Combine(directory, Path.GetRandomFileName())) : new InMemoryStateStore();
            var pair = Config(directory, Pair);
            store.SavePair(pair);
            SourceFixture.AcquireJournal(store, pair);
            store.SavePairDecision(pair, store.ReadPairExecution(Pair)!.Value.Generation,
                pair, null, false, PathNames.Key("outside"));
            var local = Item("root/file");
            var saved = SyncedEntries.From(local, local);
            store.Commit(Pair, StateChange.Empty with { Record = [saved] });
            var pending = new PendingEffect(1, Guid.NewGuid().ToString(), 0, PendingKind.LocalDelete,
                PendingPhase.Armed, local.Path, new RemovalProof(local, "volume", local.ContentHash!), saved,
                null, null, null, null, false, null);
            store.ArmPending(Pair, pending);
            var generation = store.ReadPairExecution(Pair)!.Value.Generation;
            var refused = false;
            try { store.SavePairDecision(pair, generation, pair, null, false, PathNames.Key("root")); }
            catch (InvalidOperationException) { refused = true; }
            Check.Require(refused && store.ReadPairExecution(Pair)!.Value.Generation == generation &&
                store.Pending(Pair) is CurrentPending current && current.Value == pending &&
                store.SyncedByKey(Pair, Side.Local, saved.LocalKey) == saved,
                $"Re-inclusion refuses pending file custody without resetting history ({real}).");
        }
    }

    private static async Task CaptureCasesAsync(IStateStore store, Side side)
    {
        var child = Item("root/branch/child");
        var adjacent = Item("root/branch0/child");
        var sibling = Item("root/branchx");
        var root = Item("root", folder: true);
        var skipped = Item("root/skipped") with { Skip = TextKey.SkipLink.ToString() };
        var cases = new (Listed[] Events, Entry[] Expected)[]
        {
            ([], []),
            ([Present(Item("root/a")), Present(Item("root/a") with { Size = 9 })],
                [Item("root/a") with { Size = 9 }]),
            ([Present(Item("root/A")), Present(Item("root/a"))], [Item("root/a")]),
            ([new("root/branch", null), Present(child)], [child]),
            ([Present(child), new("root/branch", null)], []),
            ([Present(child), Present(Item("root/branch", true)), Present(adjacent), new("root/branch", null)],
                [adjacent]),
            ([Present(child), Present(adjacent), new("root/branch", null)], [adjacent]),
            // #129: a capture that began empty still holds the folder's children when its file arrives.
            ([Present(Item("root/branch", true)), Present(child), Present(Item("root/branch"))],
                [Item("root/branch")]),
            ([Present(child), Present(Item("root/branch"))], [Item("root/branch")]),
            ([Present(Item("root/branch")), Present(child), Present(Item("root/branch"))],
                [Item("root/branch")]),
            ([Present(Item("root/branch", true)), Present(Item("root/branch/deep/leaf")), Present(adjacent),
                Present(sibling), Present(Item("root/branch"))],
                [Item("root/branch"), adjacent, sibling]),
            ([Present(Item("root/A%_é", true)), Present(Item("root/A%_é/雪")), Present(Item("root/A%_é0")),
                Present(Item("root/a%_é"))],
                [Item("root/a%_é"), Item("root/A%_é0")]),
            ([Present(child), new("root/branch", null), Present(child)], [child]),
            ([Present(child), Present(root), Present(skipped)], [root, child, skipped]),
            ([Present(skipped)], [skipped]),
        };
        foreach (var (events, expected) in cases)
        {
            using var plan = store.OpenFolderPlan(Pair, Folder(side), side);
            await plan.CaptureAsync(Source(events), CancellationToken.None);
            Check.Require(plan.Entries(CancellationToken.None).SequenceEqual(expected),
                "capture preserves final native-key inventory, payload and parent-first order");
            Check.Require(plan.Entries(CancellationToken.None).SequenceEqual(expected),
                "inventory may be streamed again without consuming the classification pass");
        }

        store.BeginScan(Pair, Generation(store), includeDeleted: false);
        var writer = store.OpenScan(Pair, side, ScanTestSettings.BatchRows);
        writer.AddAsync(Present(Item("root/branch", true)), default).GetAwaiter().GetResult();
        writer.AddAsync(Present(child), default).GetAwaiter().GetResult();
        await writer.CompleteAsync();
        writer = store.OpenScan(Pair, side, ScanTestSettings.BatchRows);
        writer.AddAsync(Present(Item("root/branch")), default).GetAwaiter().GetResult();
        await writer.CompleteAsync();
        Check.Require(store.ScanEntry(Pair, side, PathNames.Key(child.Path)) is null,
            "ordinary reopened scans remove replaced-folder descendants");
        using var isolated = store.OpenFolderPlan(Pair, Folder(side), side);
        await isolated.CaptureAsync(Source([Present(Item("root/private"))]), CancellationToken.None);
        Check.Require(store.ScanEntry(Pair, side, PathNames.Key("root/branch")) is { IsFolder: false } &&
            store.ScanEntry(Pair, side, PathNames.Key("root/private")) is null,
            "a plan never replaces or writes the ordinary scan");
    }

    private static async Task JoinCasesAsync(IStateStore store, Side side)
    {
        store.ForgetAllSynced(Pair);
        var records = new[]
        {
            Saved("root", "root", true),
            Saved("root/present", "root/present"),
            Saved("root/absent", "root/absent"),
            Saved("root/local-only", "outside/local-only"),
            Saved("outside/dropbox-only", "root/dropbox-only"),
            Saved("outside/linked", "root/linked"),
            Saved("root0/boundary", "root0/boundary"),
        };
        store.Commit(Pair, StateChange.Empty with { Record = records });
        var entries = new[] { Item("root", true), Item("root/present"), Item("root/new"), Item("root/linked") };
        using var plan = store.OpenFolderPlan(Pair, Folder(side, records[0]), side);
        await plan.CaptureAsync(Source(entries.Select(Present)), CancellationToken.None);
        var children = new List<(string Key, string NativeKey, Entry? Entry, SyncedEntry? Synced)>();
        foreach (var child in plan.Children(CancellationToken.None))
        {
            children.Add(child);
            MarkRemoved(plan, child);
        }
        var present = children.Where(child => child.Entry is not null).ToArray();
        Check.Require(present.Select(child => child.Entry!.Path).SequenceEqual(
                ["root/linked", "root/new", "root/present"]),
            "present children are native-key ordered, exclude the seeded root and include unbound entries");
        Check.Require(present.Single(child => child.Entry!.Path == "root/present").Synced == records[1] &&
            present.Single(child => child.Entry!.Path == "root/new").Synced is null &&
            present.Single(child => child.Entry!.Path == "root/linked").Synced ==
                (side == Side.Dropbox ? records[5] : null),
            "present joins use the deleting side's effective saved key, including explicit overrides");
        var expectedAbsent = side == Side.Local ? new[] { records[2], records[3] } : new[] { records[2], records[4] };
        var absent = children.Where(child => child.Entry is null).ToArray();
        Check.Require(absent.Length == expectedAbsent.Length && expectedAbsent.All(record =>
                absent.Count(child => child.Synced == record) == 1),
            "the anti-join emits every inherited/override saved-only child exactly once and no missing-side outsider");
        Check.Require(children.Take(present.Length).All(child => child.Entry is not null) &&
            children.Skip(present.Length).All(child => child.Entry is null),
            "classification exhausts present rows before saved-only rows");
        foreach (var child in children)
        {
            var path = child.Entry?.Path ?? child.Synced!.PathOn(side);
            Check.Require(child.Key == PathNames.KeyFor(side, path) && child.NativeKey == PathNames.Key(path),
                "candidate keys retain separate policy and native identities");
        }
        plan.Complete(true, CancellationToken.None);
        Check.Require(plan.DeleteWhole && !plan.Branches(CancellationToken.None).Any() &&
            !plan.Survivors(CancellationToken.None).Any(), "an exhausted all-removed plan is a whole deletion");
        foreach (var record in records)
            Check.Require(store.SyncedByKey(Pair, Side.Local, record.LocalKey) == record,
                "capturing and completing a plan never forgets a durable binding");
    }

    private static async Task PhaseCasesAsync(IStateStore store, Side side)
    {
        store.ForgetAllSynced(Pair);
        var key = PathNames.Key("root/a");
        var survivor = new FolderChild(Item("root/a"), null, FolderVerdict.Survives);
        using (var plan = store.OpenFolderPlan(Pair, Folder(side), side))
        {
            Reject(() => plan.MarkClassified(key, key, survivor, true, CancellationToken.None));
            Reject(() => plan.RemovedChildren(CancellationToken.None).ToArray());
            Reject(() => plan.SelectBranch(1, CancellationToken.None));
            Reject(() => plan.HasSurvivorAtOrBelow(key, CancellationToken.None));
            Reject(() => plan.HasSelectedAncestor(key, CancellationToken.None));
            Reject(() => plan.ForgetTouchesUnreadable(CancellationToken.None));
            Reject(() => plan.Entries(CancellationToken.None).ToArray());
            Reject(() => plan.PrepareRemoval(Folder(side)));
            Reject(() => plan.Children(CancellationToken.None).ToArray());
            Reject(() => plan.PrepareBranches(CancellationToken.None));
            Reject(() => plan.Complete(true, CancellationToken.None));
            Reject(() => _ = plan.DeleteWhole);
            Reject(() => plan.Branches(CancellationToken.None).ToArray());
            Reject(() => plan.Survivors(CancellationToken.None).ToArray());
            await plan.CaptureAsync(Source([Present(Item("root/a")), Present(Item("root/b"))]), CancellationToken.None);
            await ThrowsAsync<InvalidOperationException>(() => plan.CaptureAsync(Source([]), CancellationToken.None));
            Reject(() => plan.PrepareRemoval(Folder(side, path: "other")));
            var refreshed = Folder(side, Saved("root", "root", true));
            plan.PrepareRemoval(refreshed);
            Check.Require(plan.Folder == refreshed && plan.Entries(CancellationToken.None).Count() == 2,
                "Preparation refreshes only the same root and preserves the captured child inventory.");
            using (var entries = plan.Entries(CancellationToken.None).GetEnumerator())
                Check.Require(entries.MoveNext(), "an inventory cursor can be disposed early");
            Check.Require(plan.Entries(CancellationToken.None).Count() == 2,
                "early inventory disposal releases its reader and permits another inventory pass");
            Reject(() => plan.MarkClassified(key, key, survivor, true, CancellationToken.None));
            using (var children = plan.Children(CancellationToken.None).GetEnumerator())
                Check.Require(children.MoveNext(), "classification has a first child");
            Reject(() => plan.Complete(true, CancellationToken.None));
        }
        using (var plan = store.OpenFolderPlan(Pair, Folder(side), side))
        {
            await plan.CaptureAsync(Source([Present(Item("root/a")), Present(Item("root/b"))]), CancellationToken.None);
            foreach (var child in plan.Children(CancellationToken.None))
            {
                if (child.Entry!.Path != "root/a")
                {
                    MarkRemoved(plan, child);
                    continue;
                }
                Reject(() => plan.MarkClassified(child.Key, child.NativeKey + "x", survivor, true, CancellationToken.None));
                Reject(() => plan.MarkClassified(child.Key, child.NativeKey,
                    survivor with { OnDeletingSide = child.Entry! with { Size = 99 } }, true, CancellationToken.None));
                plan.MarkClassified(child.Key, child.NativeKey, survivor, true, CancellationToken.None);
                Reject(() => plan.MarkClassified(child.Key, child.NativeKey, survivor, true, CancellationToken.None));
            }
            Reject(() => plan.MarkClassified(key, key, survivor, true, CancellationToken.None));
            Reject(() => plan.RemovedChildren(CancellationToken.None).ToArray());
            Reject(() => plan.Complete(true, CancellationToken.None));
            plan.PrepareBranches(CancellationToken.None);
            Reject(() => plan.SelectBranch(1, CancellationToken.None));
            Reject(() => plan.HasSurvivorAtOrBelow(key, CancellationToken.None));
            Reject(() => plan.HasSelectedAncestor(key, CancellationToken.None));
            using (var removed = plan.RemovedChildren(CancellationToken.None).GetEnumerator())
            {
                Check.Require(removed.MoveNext(), "selection has a first removed child");
                var id = removed.Current.Id;
                Reject(() => plan.SelectBranch(id + 1, CancellationToken.None));
                plan.SelectBranch(id, CancellationToken.None);
                Reject(() => plan.SelectBranch(id, CancellationToken.None));
            }
            Reject(() => plan.Complete(false, CancellationToken.None));
        }
        using (var plan = store.OpenFolderPlan(Pair, Folder(side), side))
        {
            await plan.CaptureAsync(Source([]), CancellationToken.None);
            foreach (var _ in plan.Children(CancellationToken.None)) { }
            Reject(() => plan.PrepareBranches(CancellationToken.None));
            plan.Complete(true, CancellationToken.None);
            Reject(() => plan.PrepareRemoval(Folder(side)));
        }
        var disposed = store.OpenFolderPlan(Pair, Folder(side), side);
        await disposed.CaptureAsync(Source([]), CancellationToken.None);
        Complete(disposed);
        disposed.Dispose();
        Reject(() => disposed.MarkClassified(key, key, survivor, true, CancellationToken.None));
        Reject(() => disposed.PrepareBranches(CancellationToken.None));
        Reject(() => disposed.RemovedChildren(CancellationToken.None).ToArray());
        Reject(() => disposed.SelectBranch(1, CancellationToken.None));
        Reject(() => disposed.HasSurvivorAtOrBelow(key, CancellationToken.None));
        Reject(() => disposed.HasSelectedAncestor(key, CancellationToken.None));
        Reject(() => disposed.Complete(true, CancellationToken.None));
        Reject(() => disposed.ForgetTouchesUnreadable(CancellationToken.None));
        Reject(() => _ = disposed.DeleteWhole);
        Reject(() => disposed.Entries(CancellationToken.None).ToArray());
        Reject(() => disposed.Children(CancellationToken.None).ToArray());
        Reject(() => disposed.Branches(CancellationToken.None).ToArray());
        Reject(() => disposed.Survivors(CancellationToken.None).ToArray());
        await ThrowsAsync<InvalidOperationException>(() => disposed.CaptureAsync(Source([]), CancellationToken.None));
    }

    // shortcut: commit/decode/projection/selection faults need a storage fault seam; add with the first such seam.
    private static async Task CaptureFailureCasesAsync(IStateStore store, Side side, string directory)
    {
        store.ForgetAllSynced(Pair);
        var saved = Saved("root/kept", "root/kept");
        store.Commit(Pair, StateChange.Empty with { Record = [saved] });
        foreach (var count in new[] { 0, 1, 7 })
        {
            var failure = new IOException("synthetic listing failure");
            using (var plan = store.OpenFolderPlan(Pair, Folder(side), side))
            {
                var actual = await ThrowsAsync<IOException>(() =>
                    plan.CaptureAsync(FailAfter(count, failure), CancellationToken.None));
                Check.Require(ReferenceEquals(actual, failure), "MoveNext failure preserves the source exception");
                RefuseFailedCapture(plan, directory);
            }
            Check.Require(store.SyncedByKey(Pair, Side.Local, saved.LocalKey) == saved,
                "failed capture preserves the saved binding");
        }
        var sourceFailure = new IOException("synthetic source failure");
        foreach (var source in new IAsyncEnumerable<Listed>[]
                 { new FaultingSource(sourceFailure, acquisition: true), new FaultingSource(sourceFailure, acquisition: false) })
        {
            using var plan = store.OpenFolderPlan(Pair, Folder(side), side);
            var actual = await ThrowsAsync<IOException>(() => plan.CaptureAsync(source, CancellationToken.None));
            Check.Require(ReferenceEquals(actual, sourceFailure), "acquisition and disposal errors are not hidden");
            RefuseFailedCapture(plan, directory);
        }
        using (var plan = store.OpenFolderPlan(Pair, Folder(side), side))
        {
            var unreadable = Item("root/blocked", true) with { Skip = TextKey.SkipUnreadable.ToString() };
            await ThrowsAsync<InventoryIncompleteException>(() => plan.CaptureAsync(
                Source([Present(Item("root/kept")), Present(unreadable)]), CancellationToken.None));
            RefuseFailedCapture(plan, directory);
        }
        using (var plan = store.OpenFolderPlan(Pair, Folder(side), side))
        {
            await ThrowsAsync<SqliteException>(() => plan.CaptureAsync(
                Source([Present(Item("root/valid")), Present(Item("root/invalid") with { Id = null! })]),
                CancellationToken.None));
            RefuseFailedCapture(plan, directory);
        }
        using (var cancellation = new CancellationTokenSource())
        using (var plan = store.OpenFolderPlan(Pair, Folder(side), side))
        {
            await ThrowsAsync<OperationCanceledException>(() =>
                plan.CaptureAsync(CancelAfterItem(cancellation), cancellation.Token));
            RefuseFailedCapture(plan, directory);
        }
        using (var plan = store.OpenFolderPlan(Pair, Folder(side), side))
        {
            await plan.CaptureAsync(Source([Present(Item("root/recovered"))]), CancellationToken.None);
            Check.Require(plan.Entries(CancellationToken.None).Single().Path == "root/recovered",
                "the next plan is fresh after repeated capture failures");
            Complete(plan);
            Check.Require(plan.DeleteWhole, "the next plan can finish after repeated capture failures");
        }
        Check.Require(store.SyncedByKey(Pair, Side.Local, saved.LocalKey) == saved,
            "failed and recovered planning perform no saved forgetting");
    }

    private static async Task CancellationCasesAsync(IStateStore store, Side side)
    {
        store.ForgetAllSynced(Pair);
        var saved = Saved("root/saved", "root/saved");
        store.Commit(Pair, StateChange.Empty with { Record = [saved] });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ct = cancellation.Token;
        foreach (var phase in new[] { "inventory", "projection", "selection", "completion", "results" })
        {
            using var plan = store.OpenFolderPlan(Pair, Folder(side), side);
            await plan.CaptureAsync(Source([Present(Item("root/keep")), Present(Item("root/remove"))]), CancellationToken.None);
            if (phase == "inventory")
            {
                Throws<OperationCanceledException>(() => plan.Entries(ct).ToArray());
                Throws<OperationCanceledException>(() => plan.Children(ct).ToArray());
                continue;
            }
            foreach (var child in plan.Children(CancellationToken.None))
            {
                if (child.Entry?.Path == "root/keep")
                    plan.MarkClassified(child.Key, child.NativeKey,
                        new FolderChild(child.Entry, child.Synced, FolderVerdict.Survives), true, CancellationToken.None);
                else
                    MarkRemoved(plan, child);
            }
            if (phase == "projection")
            {
                Throws<OperationCanceledException>(() => plan.PrepareBranches(ct));
                continue;
            }
            plan.PrepareBranches(CancellationToken.None);
            if (phase == "selection")
            {
                Throws<OperationCanceledException>(() => plan.RemovedChildren(ct).ToArray());
                continue;
            }
            foreach (var branch in plan.RemovedChildren(CancellationToken.None))
                plan.SelectBranch(branch.Id, CancellationToken.None);
            if (phase == "completion")
            {
                Throws<OperationCanceledException>(() => plan.Complete(false, ct));
                continue;
            }
            plan.Complete(false, CancellationToken.None);
            Throws<OperationCanceledException>(() => plan.Branches(ct).ToArray());
            Throws<OperationCanceledException>(() => plan.Survivors(ct).ToArray());
        }
        Check.Require(store.SyncedByKey(Pair, Side.Local, saved.LocalKey) == saved,
            "cancellation at inventory, classification, projection, selection, completion and result reads never forgets saved state");
    }

    private static async Task SelectionCasesAsync(IStateStore store, Side side)
    {
        store.ForgetAllSynced(Pair);
        var cases = new (string[] Paths, string[] Survivors, string[] Branches)[]
        {
            ([], [], []),
            (["root/new"], [], []),
            (["root/edit", "root/new"], ["root/edit"], ["root/new"]),
            (["root/sub", "root/sub/edit", "root/sub/ignored", "root/top"],
                ["root/sub/edit"], ["root/sub/ignored", "root/top"]),
            (["root/restored", "root/unchanged", "root/unsynced"], ["root/restored"],
                ["root/unchanged", "root/unsynced"]),
            (["root/keep", "root/b", "root/b/x", "root/b/y", "root/other"], ["root/keep"],
                ["root/b", "root/other"]),
            (["root/z", "root/s/deep/x", "root/s/keep", "root/s/z", "root/s", "root/s/deep", "root/top"],
                ["root/s/keep"], ["root/s/deep", "root/s/z", "root/top", "root/z"]),
            (["root/keep"], ["root/keep"], []),
        };
        foreach (var (paths, survivors, branches) in cases)
        foreach (var reverse in new[] { false, true })
        {
            var entries = paths.Select(path => Item(path,
                paths.Any(other => PathNames.IsUnderKey(PathNames.Key(other), PathNames.Key(path)))));
            using var plan = await BuildAsync(store, Folder(side), side, reverse ? entries.Reverse() : entries, survivors);
            Check.Require(plan.DeleteWhole == (survivors.Length == 0), "only survivor-free plans delete the whole folder");
            Check.Require(plan.Branches(CancellationToken.None).Select(child => ChildPath(child, side)).SequenceEqual(branches),
                "maximal branches protect every survivor ancestor and execute deepest first with policy-key ties");
            Check.Require(plan.Survivors(CancellationToken.None).Select(child => ChildPath(child, side))
                    .SequenceEqual(survivors.OrderBy(PathNames.Key, StringComparer.Ordinal)),
                "follow-ups retain precisely the marked survivors in key order");
        }
    }

    private static async Task MappedRootAsync(IStateStore store)
    {
        store.ForgetAllSynced(Pair);
        using var plan = await BuildAsync(store, Folder(Side.Dropbox, path: "a:b"), Side.Dropbox,
            [Item("a:b/sub", true), Item("a:b/sub/keep"), Item("a:b/gone")], "a:b/sub/keep");
        Check.Require(!plan.DeleteWhole && plan.Branches(CancellationToken.None).Single().OnDeletingSide!.Path == "a:b/gone" &&
            plan.Survivors(CancellationToken.None).Single().OnDeletingSide!.Path == "a:b/sub/keep",
            "a mapped Dropbox root retains the survivor and its parent using policy keys without changing native effect paths");
    }

    private static async Task FormerlyCollidingSpellingsAsync(IStateStore store)
    {
        store.ForgetAllSynced(Pair);
        var saved = Saved("root/a_b", "root/a:b");
        store.Commit(Pair, StateChange.Empty with { Record = [saved] });
        using var plan = store.OpenFolderPlan(Pair, Folder(Side.Dropbox), Side.Dropbox);
        var entries = new[] { Item("root/z:keep"), Item("root/a_b"), Item("root/a:b") };
        await plan.CaptureAsync(Source(entries.Select(Present)), CancellationToken.None);
        Check.Require(plan.Entries(CancellationToken.None).Select(entry => entry.Path)
            .SequenceEqual(["root/a:b", "root/a_b", "root/z:keep"]),
            "formerly colliding spellings stay distinct in native order; no substitution collapses them");
        var joined = new List<(string Key, string NativeKey, Entry? Entry, SyncedEntry? Synced)>();
        foreach (var child in plan.Children(CancellationToken.None))
        {
            joined.Add(child);
            if (child.Entry!.Path == "root/z:keep")
                plan.MarkClassified(child.Key, child.NativeKey,
                    new FolderChild(child.Entry, child.Synced, FolderVerdict.Survives), true, CancellationToken.None);
            else if (child.Entry!.Path == "root/a:b")
                plan.MarkClassified(child.Key, child.NativeKey,
                    new FolderChild(child.Entry, child.Synced, FolderVerdict.Survives), false, CancellationToken.None);
            else
                MarkRemoved(plan, child);
        }
        Check.Require(joined.Count == 3, "every distinct native spelling is one candidate, with no saved-only duplicate");
        var raw = joined.Single(child => child.Entry!.Path == "root/a:b");
        var underscore = joined.Single(child => child.Entry!.Path == "root/a_b");
        Check.Require(raw.Key == saved.DropboxKey && raw.Synced == saved,
            "the exact raw Dropbox key joins the preserved binding");
        Check.Require(underscore.Synced is null,
            "a valid underscore is a distinct path, never an inverse-mapping hint for another spelling");
        plan.PrepareBranches(CancellationToken.None);
        foreach (var branch in plan.RemovedChildren(CancellationToken.None))
        {
            Check.Require(!plan.HasSurvivorAtOrBelow(branch.Key, CancellationToken.None),
                "a survivor protects only its own distinct key");
            plan.SelectBranch(branch.Id, CancellationToken.None);
        }
        plan.Complete(false, CancellationToken.None);
        Check.Require(plan.Branches(CancellationToken.None).Select(child => child.OnDeletingSide!.Path)
            .SequenceEqual(["root/a_b"]), "only the unbound underscore literal is selected for deletion");
        Check.Require(plan.Survivors(CancellationToken.None).Single().OnDeletingSide!.Path == "root/z:keep",
            "follow-ups retain only the survivor marked with followUp=true");
    }

    private static async Task JoinedRestartAsync(string directory)
    {
        var database = Path.Combine(directory, "restart.db");
        var saved = Saved("root/a_b", "root/a:b");
        using (IStateStore first = StateStore.Open(database))
        {
            first.SavePair(Config(directory, Pair));
            first.Commit(Pair, StateChange.Empty with { Record = [saved] });
        }
        using IStateStore reopened = StateStore.Open(database);
        Check.Require(reopened.SyncedByKey(Pair, Side.Local, saved.LocalKey) == saved &&
            reopened.SyncedByKey(Pair, Side.Dropbox, saved.DropboxKey) == saved,
            "a restart preserves the saved join binding, its paths and its IDs on both sides");
        using var plan = reopened.OpenFolderPlan(Pair, Folder(Side.Dropbox), Side.Dropbox);
        var skipped = Item("root/a:b") with { Skip = TextKey.SkipReservedName.ToString() };
        await plan.CaptureAsync(Source([Present(skipped), Present(Item("root/a_b"))]), CancellationToken.None);
        var children = new List<(string Key, string NativeKey, Entry? Entry, SyncedEntry? Synced)>();
        foreach (var child in plan.Children(CancellationToken.None))
        {
            children.Add(child);
            MarkRemoved(plan, child);
        }
        var joined = children.Single(child => child.Entry!.Path == "root/a:b");
        Check.Require(joined.Synced == saved && joined.Entry!.Skip == TextKey.SkipReservedName.ToString(),
            "a skipped invalid Dropbox name is present evidence that keeps its stored binding, never absence");
        Check.Require(children.Single(child => child.Entry!.Path == "root/a_b").Synced is null,
            "the distinct underscore spelling is never inferred from the raw binding");
        plan.Complete(true, CancellationToken.None);
    }

    private static async Task SavedPathDriftAsync(IStateStore store)
    {
        store.ForgetAllSynced(Pair);
        var deep = Saved("outside/deep", "root/z/deep") with { DropboxKey = PathNames.Key("root/a") };
        var shallow = Saved("outside/shallow", "root/a") with { DropboxKey = PathNames.Key("root/z/deep") };
        store.Commit(Pair, StateChange.Empty with { Record = [deep, shallow] });
        using var plan = await BuildAsync(store, Folder(Side.Dropbox), Side.Dropbox, [Item("root/keep")], "root/keep");
        var branches = plan.Branches(CancellationToken.None).ToArray();
        // Saved-only rows exist on neither side, so the stored key is their only identity.
        Check.Require(branches.Length == 2 && branches[0].Synced == shallow && branches[1].Synced == deep &&
            branches.All(child => child.OnDeletingSide is null),
            "saved-only depth and effect ordering use the stored keys");
    }

    private static async Task FrozenFactsAsync(IStateStore store, Side side, bool reverse)
    {
        store.ForgetAllSynced(Pair);
        var first = side == Side.Local ? Saved("root/a", "elsewhere", true) : Saved("elsewhere", "root/a", true);
        var later = side == Side.Local ? Saved("root/b", "elsewhere/branch", true) : Saved("elsewhere/branch", "root/b", true);
        var kept = side == Side.Local ? Saved("root/keep", "elsewhere/kept") : Saved("elsewhere/kept", "root/keep");
        var unrelated = Saved("other/unrelated", "other/unrelated");
        var records = new[] { first, later, kept, unrelated };
        store.Commit(Pair, StateChange.Empty with { Record = reverse ? records.Reverse().ToArray() : records });
        var entries = new[] { Item("root/a", true), Item("root/b", true), Item("root/keep") };
        using var plan = await BuildAsync(store, Folder(side), side, reverse ? entries.Reverse() : entries, "root/keep");
        using var branches = plan.Branches(CancellationToken.None).GetEnumerator();
        Check.Require(branches.MoveNext() && branches.Current.Synced == first, "the first key-tied branch carries its original binding");
        store.Commit(Pair, StateChange.Empty with
        {
            ForgetSubtrees = [(Side.Local, first.LocalKey), (Side.Dropbox, first.DropboxKey)],
        });
        Check.Require(store.SyncedByKey(Pair, Side.Local, first.LocalKey) is null &&
            store.SyncedByKey(Pair, Side.Local, later.LocalKey) is null &&
            store.SyncedByKey(Pair, Side.Local, kept.LocalKey) is null,
            "both exact forget ranges erase the later branch and survivor outside the first deleting-side subtree");
        Check.Require(branches.MoveNext() && branches.Current.Synced == later &&
            branches.Current.OnDeletingSide == entries[1] && !branches.MoveNext(),
            "advancing after a durable commit still reads the later branch's frozen original facts");
        var survivor = plan.Survivors(CancellationToken.None).Single();
        Check.Require(survivor.Synced == kept && survivor.OnDeletingSide == entries[2] &&
            store.SyncedByKey(Pair, Side.Local, unrelated.LocalKey) == unrelated,
            "follow-ups retain erased saved facts while unrelated durable bindings survive");
    }

    private static async Task FrozenPagesAsync(IStateStore store, Side side)
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        var pageSize = SettingsParser.Parse(document.RootElement.GetRawText(), null).StoragePageSize;
        store.ForgetAllSynced(Pair);
        var entries = Enumerable.Range(0, pageSize + 1).Select(index => Item("root/page-" + index.ToString("D6"))).ToArray();
        using var plan = await BuildAsync(store, Folder(side), side, entries);
        var frozen = plan.FrozenEvidence(pageSize, CancellationToken.None).ToArray();
        Check.Require(frozen.Length == entries.Length && frozen.Select(row => row.Ordinal)
            .SequenceEqual(Enumerable.Range(1, entries.Length).Select(index => (long)index)),
            "frozen paging preserves every row and ordinal across the page boundary");
        var connection = (SqliteConnection)plan.GetType().GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(plan)!;
        foreach (var take in new[] { StateSchema.OneRow, pageSize, pageSize + 1 })
        {
            var statements = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
            try
            {
                Check.Require(plan.FrozenEvidence(take, CancellationToken.None).SequenceEqual(frozen),
                    "configured paging preserves frozen rows");
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            Check.Require(statements == (entries.Length + take - 1) / take + 1,
                "real frozen-evidence statements equal configured pages plus one terminal read");
        }
        foreach (var invalid in new[] { 0, -1 })
        {
            try { plan.FrozenEvidence(invalid, CancellationToken.None); throw new Exception("invalid page accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
        using var cancellation = new CancellationTokenSource();
        using var iterator = plan.FrozenEvidence(pageSize, cancellation.Token).GetEnumerator();
        Check.Require(iterator.MoveNext(), "first buffered frozen row is available");
        cancellation.Cancel();
        try { iterator.MoveNext(); throw new Exception("buffered rows must honor cancellation"); }
        catch (OperationCanceledException) { }
        using var disposed = plan.FrozenEvidence(pageSize, CancellationToken.None).GetEnumerator();
        Check.Require(disposed.MoveNext(), "completed plan remains readable after cancellation between rows");
        plan.Dispose();
        try { disposed.MoveNext(); throw new Exception("buffered rows must honor plan disposal"); }
        catch (ObjectDisposedException) { }
    }

    // The guard's answer with these paths recorded; each ask leaves the store as it found it.
    private static bool Guards(IStateStore store, FolderPlan plan, params string[] paths)
    {
        foreach (var path in paths) UnreadableMember.Record(store, Pair, path);
        var touched = plan.ForgetTouchesUnreadable(CancellationToken.None);
        foreach (var path in paths) UnreadableMember.Release(store, Pair, path);
        return touched;
    }

    private static async Task GuardCasesAsync(IStateStore store, Side side)
    {
        foreach (var whole in new[] { false, true })
        foreach (var inherited in new[] { false, true })
        {
            store.ForgetAllSynced(Pair);
            var root = side == Side.Local ? Saved("root/branch", "elsewhere", true) : Saved("elsewhere", "root/branch", true);
            var protectedPath = inherited
                ? side == Side.Local ? "elsewhere/child" : "root/branch/child"
                : "unreadable/child";
            var protectedRecord = Saved(protectedPath, side == Side.Local ? "elsewhere/child" : "root/branch/child");
            store.Commit(Pair, StateChange.Empty with { Record = [root, protectedRecord] });
            var folder = whole ? Folder(side, root, "root/branch") : Folder(side);
            var entries = whole ? Array.Empty<Entry>() : new[] { Item("root/branch", true), Item("root/keep") };
            using var plan = await BuildAsync(store, folder, side, entries,
                whole ? Array.Empty<string>() : new[] { "root/keep" });
            Check.Require(!plan.ForgetTouchesUnreadable(CancellationToken.None),
                "an empty unreadable set never blocks a completed plan");
            Check.Require(Guards(store, plan, PathNames.Parent(protectedPath)),
                "the frozen whole/branch guard protects cross-linked inherited and override bindings outside the listing");
            Check.Require(store.SyncedByKey(Pair, Side.Local, root.LocalKey) == root &&
                store.SyncedByKey(Pair, Side.Local, protectedRecord.LocalKey) == protectedRecord,
                "a guard reads both ranges without trimming or forgetting either binding");
            Check.Require(!Guards(store, plan, "unreadable0"),
                "an adjacent prefix does not overlap a protected binding");
        }
        store.ForgetAllSynced(Pair);
        var selected = new[] { "root/first", "root/second" };
        store.Commit(Pair, StateChange.Empty with { Record = selected.Select((path, index) =>
            side == Side.Local ? Saved(path, "mapped/branch" + index, true) :
                Saved("mapped/branch" + index, path, true)).ToArray() });
        using (var partial = store.OpenFolderPlan(Pair, Folder(side), side))
        {
            await partial.CaptureAsync(Source([Present(Item(selected[0], true)), Present(Item(selected[1], true)), Present(Item("root/keep"))]), default);
            foreach (var child in partial.Children(default))
            {
                if (child.Entry!.Path == "root/keep")
                    partial.MarkClassified(child.Key, child.NativeKey,
                        new FolderChild(child.Entry, child.Synced, FolderVerdict.Survives), true, default);
                else MarkRemoved(partial, child);
            }
            partial.PrepareBranches(default);
            foreach (var branch in partial.RemovedChildren(default)) partial.SelectBranch(branch.Id, default);
            partial.Complete(deleteWhole: false, default);
            Check.Require(!partial.ForgetTouchesUnreadable(default), "completed partial plan is clear without members");
            var member = side == Side.Local ? "root/second/blocked" : "mapped/branch1/blocked";
            Check.Require(Guards(store, partial, member), "partial guard reaches the second selected mapped root");
            Check.Require(partial.FrozenEvidence(10, default).Count() == 3,
                "partial guard preserves the completed plan and its survivor");
        }
        store.ForgetAllSynced(Pair);
        using var unsaved = store.OpenFolderPlan(Pair, Folder(side), side);
        await unsaved.CaptureAsync(Source([]), CancellationToken.None);
        Complete(unsaved);
        Check.Require(Guards(store, unsaved, "root/not-yet-recorded") == (side == Side.Local),
            "an unsaved whole plan has only its current deleting-side root, including unknown local descendants");
    }

    private static async Task FootprintCasesAsync(IStateStore store, Side side)
    {
        store.ForgetAllSynced(Pair);
        var gone = side == Side.Local
            ? Saved("root/gone", "outside/gone", folder: true)
            : Saved("outside/gone", "root/gone", folder: true);
        var kept = side == Side.Local ? Saved("root/keep", "outside/kept") : Saved("outside/kept", "root/keep");
        store.Commit(Pair, StateChange.Empty with { Record = [gone, kept] });
        using (var plan = await BuildAsync(store, Folder(side), side,
                   new[] { Item("root/gone", true), Item("root/keep") }, Array.Empty<string>()))
        {
            Check.Require(plan.Branches(CancellationToken.None).Single().OnDeletingSide!.Path == "root/gone" &&
                plan.Survivors(CancellationToken.None).Single().OnDeletingSide!.Path == "root/keep",
                "the selected branch and the content-changed sibling classify independently");
            Check.Require(!Guards(store, plan, kept.LocalKey),
                "a surviving sibling's binding sits outside the actual selected-forget footprint");
            Check.Require(Guards(store, plan, gone.LocalKey),
                "unreadability at the selected branch's mapped path refuses the plan");
            Check.Require(Guards(store, plan, gone.LocalKey + "/child"),
                "unreadability below the selected mapping still overlaps it");
            Check.Require(Guards(store, plan, PathNames.Parent(gone.LocalKey), gone.LocalKey),
                "nested and overlapping unreadable prefixes both refuse the plan");
        }
        store.ForgetAllSynced(Pair);
        using (var plan = await BuildAsync(store, Folder(side), side,
                   new[] { Item("root/gone") }, Array.Empty<string>()))
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() =>
                plan.ForgetTouchesUnreadable(cancellation.Token));
            Reject(() => plan.ForgetTouchesUnreadable(CancellationToken.None));
        }
    }

    private static async Task LifetimeCasesAsync(IStateStore store, string directory)
    {
        store.ForgetAllSynced(Pair);
        var sentinel = Path.Combine(directory, "foreign.plan");
        File.WriteAllText(sentinel, "unowned");
        string planPath;
        var plan = store.OpenFolderPlan(Pair, Folder(Side.Local), Side.Local);
        try
        {
            await plan.CaptureAsync(Source([Present(Item("root/stale"))]), CancellationToken.None);
            Complete(plan);
            planPath = Directory.EnumerateFiles(directory, "*.plan*.db").Single();
            File.SetAttributes(planPath, File.GetAttributes(planPath) | FileAttributes.ReadOnly);
        }
        finally
        {
            plan.Dispose();
        }
        try
        {
            Check.Require(File.Exists(planPath), "a refused cleanup leaves owned scratch without failing completed work");
            using var second = store.OpenFolderPlan(Pair, Folder(Side.Local), Side.Local);
            await second.CaptureAsync(Source([Present(Item("root/fresh"))]), CancellationToken.None);
            var secondPath = Directory.EnumerateFiles(directory, "*.plan*.db").Single(path => path != planPath);
            Check.Require(second.Entries(CancellationToken.None).Single().Path == "root/fresh" && File.Exists(planPath),
                "a new plan has independent inventory while an earlier plan's cleanup remains refused");
            second.Dispose();
            Check.Require(!File.Exists(secondPath) && File.Exists(planPath),
                "normal disposal deletes only its own plan file");
        }
        finally
        {
            File.SetAttributes(planPath, FileAttributes.Normal);
        }
        store.BeginScan(Pair, Generation(store), includeDeleted: false);
        Check.Require(File.Exists(planPath) && File.ReadAllText(sentinel) == "unowned",
            "scan reset preserves independently owned plan scratch and foreign files");

        const string otherPair = "other-plan-pair";
        store.SavePair(Config(directory, otherPair));
        using (var first = store.OpenFolderPlan(Pair, Folder(Side.Local), Side.Local))
        using (var other = store.OpenFolderPlan(otherPair, Folder(Side.Dropbox), Side.Dropbox))
        {
            await first.CaptureAsync(Source([Present(Item("root/first"))]), CancellationToken.None);
            await other.CaptureAsync(Source([Present(Item("root/other"))]), CancellationToken.None);
            Check.Require(first.Entries(CancellationToken.None).Single().Path == "root/first" &&
                other.Entries(CancellationToken.None).Single().Path == "root/other",
                "different pairs own independent private plan files and inventories");
        }
        File.WriteAllText(planPath, "leftover after drained operation");
        store.RemovePair(Pair);
        Check.Require(File.Exists(planPath) && File.ReadAllText(sentinel) == "unowned",
            "pair removal does not sweep independent plan leftovers or foreign files");
        File.Delete(planPath);
        File.Delete(sentinel);
    }

    private static async Task ReadOnlyPreviewAsync(string directory)
    {
        var database = Path.Combine(directory, "preview.db");
        string planPath;
        using (IStateStore writable = StateStore.Open(database))
        {
            writable.SavePair(Config(directory, Pair));
            var abandoned = writable.OpenFolderPlan(Pair, Folder(Side.Local), Side.Local);
            try
            {
                await abandoned.CaptureAsync(Source([Present(Item("root/stale"))]), CancellationToken.None);
                Complete(abandoned);
                planPath = Directory.EnumerateFiles(directory, "preview.db.plan*.db").Single();
                File.SetAttributes(planPath, FileAttributes.ReadOnly);
            }
            finally
            {
                abandoned.Dispose();
            }
            File.SetAttributes(planPath, FileAttributes.Normal);
        }
        var before = File.ReadAllBytes(database);
        var durableSidecars = new[] { database + "-wal", database + "-shm" };
        var filesBefore = Directory.EnumerateFiles(directory)
            .Where(path => !durableSidecars.Contains(path, StringComparer.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        File.SetAttributes(database, FileAttributes.ReadOnly);
        try
        {
            using IStateStore readOnly = StateStore.OpenReadOnly(database)!;
            using (var preview = readOnly.OpenFolderPlan(Pair, Folder(Side.Local), Side.Local))
            {
                await preview.CaptureAsync(Source([Present(Item("root", true)), Present(Item("root/preview"))]), CancellationToken.None);
                Check.Require(preview.Entries(CancellationToken.None).Select(entry => entry.Path)
                    .SequenceEqual(["root", "root/preview"]),
                    "read-only preview captures independent writable disposable inventory including its root");
            }
            Check.Require(File.Exists(planPath) && File.ReadAllBytes(database).SequenceEqual(before),
                "preview preserves older plan scratch without changing the read-only durable database bytes");
        }
        finally
        {
            File.SetAttributes(database, FileAttributes.Normal);
        }
        // SQLite's WAL/SHM sidecars belong to the durable database, not the disposable plan.
        Check.Require(Directory.EnumerateFiles(directory).Where(path => !durableSidecars.Contains(path, StringComparer.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(filesBefore),
            "read-only preview creates no lasting scan, journal or metadata file");
        File.Delete(planPath);
    }

    private static async Task<FolderPlan> BuildAsync(IStateStore store, Row folder, Side side,
        IEnumerable<Entry> entries, params string[] survivors)
    {
        var local = new FakeSide(Side.Local);
        var dropbox = new FakeSide(Side.Dropbox);
        var provider = side == Side.Local ? local : dropbox;
        store.BeginScan(Pair, Generation(store), includeDeleted: false);
        var scan = store.OpenScan(Pair, side, ScanTestSettings.BatchRows);
        foreach (var entry in entries)
        {
            provider.Seed(entry, [1]);
            // Unsaved survivors are late arrivals, absent from Repair's earlier scan.
            if (!survivors.Contains(entry.Path, StringComparer.Ordinal)) scan.AddAsync(Present(entry), default).GetAwaiter().GetResult();
        }
        await scan.CompleteAsync();
        var pair = store.Pair(Pair)!;
        SourceFixture.AcquireJournal(store, pair);
        var settings = new EngineSettings("", "", "", "", "", RecycleFallback.Fail, default,
            FirstRunPolicy.NewestWins, default, "", LogBufferCharacters, ScanTestSettings.BatchRows) { JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes, LogReadBufferCharacters = ScanTestSettings.LogReadBufferCharacters };
        var context = new ExecutorContext(local, dropbox, new FakeRecovery(), store, pair, new PathScope(pair, [pair]),
            settings, new ReplaceTurn(), new FakeAdmission(), true, _ => { },
            ReserveDownloadAsync: (_, _, _, _) => throw new NotSupportedException(),
            Time: TimeProvider.System,
            ReadDeletionEvidenceAsync: (_, _, _) => throw new NotSupportedException(),
            BeginDownloadAttempt: source => new RequestAttempt(source, AttemptOwner.Transfer),
            CompleteDownloadAttemptAsync: (_, _) => Task.FromResult(false),
            UploadAdmissionIdentity: (AccountId: "account", RouteName: "route", SourceIdentity: "source"),
            ItemAttempts: new UnusedItemAttempts(), Opportunity: new object(), ExpectedGeneration: Generation(store))
        { ScanId = ScanFixture.Token(store, Pair) };
        return await new FolderPlanBuilder(context).BuildAsync(folder, side, new HashSet<string>(), false, CancellationToken.None);
    }

    private static void Complete(FolderPlan plan)
    {
        foreach (var child in plan.Children(CancellationToken.None)) MarkRemoved(plan, child);
        plan.Complete(true, CancellationToken.None);
    }

    private static void RefuseFailedCapture(FolderPlan plan, string directory)
    {
        Check.Require(!Directory.EnumerateFiles(directory, "*.plan*.db").Any(), "failed capture discards its scratch");
        Reject(() => plan.Entries(CancellationToken.None).ToArray());
        Reject(() => plan.Children(CancellationToken.None).ToArray());
        Reject(() => plan.Complete(true, CancellationToken.None));
        Reject(() => plan.Branches(CancellationToken.None).ToArray());
    }

    private static async IAsyncEnumerable<Listed> Source(IEnumerable<Listed> events,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var item in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static async IAsyncEnumerable<Listed> FailAfter(int count, IOException failure)
    {
        await Task.CompletedTask;
        for (var i = 0; i < count; i++) yield return Present(Item("root/child-" + i));
        throw failure;
    }

    private static async IAsyncEnumerable<Listed> CancelAfterItem(CancellationTokenSource cancellation)
    {
        await Task.CompletedTask;
        yield return Present(Item("root/first"));
        cancellation.Cancel();
        yield return Present(Item("root/second"));
    }

    private sealed class FaultingSource(IOException failure, bool acquisition) : IAsyncEnumerable<Listed>, IAsyncEnumerator<Listed>
    {
        private bool _yielded;

        public Listed Current => Present(Item("root/child"));

        public IAsyncEnumerator<Listed> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            acquisition ? throw failure : this;

        public ValueTask<bool> MoveNextAsync()
        {
            var next = !_yielded;
            _yielded = true;
            return ValueTask.FromResult(next);
        }

        public ValueTask DisposeAsync() => ValueTask.FromException(failure);
    }

    private static void Reject(Action action) => Throws<InvalidOperationException>(action);

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("expected " + typeof(T).Name);
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("expected " + typeof(T).Name);
    }

    private static long Generation(IStateStore store) => store.ReadPairExecution(Pair)!.Value.Generation;

    private static void MarkRemoved(FolderPlan plan,
        (string Key, string NativeKey, Entry? Entry, SyncedEntry? Synced) child) =>
        plan.MarkClassified(child.Key, child.NativeKey,
            new FolderChild(child.Entry, child.Synced, FolderVerdict.Removed), false, CancellationToken.None);

    private sealed class UnusedItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => throw new NotSupportedException();

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode) => throw new NotSupportedException();

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
            throw new NotSupportedException();
    }

    private static PairConfig Config(string directory, string id) =>
        SourceFixture.Bound(new(id, "Pair A", directory, "/Pair A", "connection", [], [], FirstRunPolicy.NewestWins, true, null));

    private static Row Folder(Side side, SyncedEntry? saved = null, string path = "root") =>
        Row.ForOneSide(side, PathNames.KeyFor(side, path), Item(path, true), saved, [], null, Context);

    private static Entry Item(string path, bool folder = false) =>
        new(path, folder, folder ? 0 : 1, Modified, "id-" + path, "revision", "hash", null);

    private static SyncedEntry Saved(string local, string dropbox, bool folder = false) =>
        new(PathNames.Key(local), local, PathNames.DropboxKey(dropbox), dropbox, folder,
            "local-" + local, folder ? 0 : 1, Modified.UtcTicks, "dropbox-" + dropbox, "saved-hash");

    private static Listed Present(Entry entry) => new(entry.Path, entry);

    private static string ChildPath(FolderChild child, Side side) => child.OnDeletingSide?.Path ?? child.Synced!.PathOn(side);
}
