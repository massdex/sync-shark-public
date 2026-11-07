using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for Parents (`--engine-checks syncer`), written by Parents's writer.</summary>
public static class ParentsChecks
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        using var store = new InMemoryStateStore();
        var local = new FakeSide(Side.Local);
        var dropbox = new FakeSide(Side.Dropbox);
        var context = Context(local, dropbox, store);
        store.SavePair(context.Pair);
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Record =
            [
                Link("a_b", "a:b"),
                Link("LocalParent", "RemoteParent"),
                Link("LocalParent/Nested", "RemoteParent/Linked")
            ]
        });
        var parents = new Parents(context);
        foreach (var (to, from, expected) in new[]
        {
            (Side.Local, "", ""),
            (Side.Dropbox, "", ""),
            (Side.Local, "child:name", "child:name"),
            (Side.Dropbox, "child_name", "child_name"),
            (Side.Local, "fresh:folder/child:name", "fresh:folder/child:name"),
            (Side.Dropbox, "fresh/child.txt", "fresh/child.txt"),
            (Side.Dropbox, "a_b/child.txt", "a:b/child.txt"),
            (Side.Dropbox, "a_b/new/child.txt", "a:b/new/child.txt"),
            (Side.Dropbox, "plain_name/child.txt", "plain_name/child.txt"),
            (Side.Dropbox, "con.txt/child.txt", "con.txt/child.txt"),
            (Side.Local, "a:b/new:folder/child:name", "a_b/new:folder/child:name"),
            (Side.Dropbox, "LocalParent/child.txt", "RemoteParent/child.txt"),
            (Side.Local, "RemoteParent/child.txt", "LocalParent/child.txt"),
            (Side.Dropbox, "LocalParent/Nested/new/child.txt", "RemoteParent/Linked/new/child.txt"),
            (Side.Local, "RemoteParent/Linked/new/child.txt", "LocalParent/Nested/new/child.txt")
        })
        {
            Check.Require(parents.Destination(to, from) == expected,
                $"Destination to {to} for {from} must be {expected}.");
        }

        var bound = Link("Left/item", "Right/other");
        Check.Require(parents.Counterpart(Side.Dropbox, "Ignored", bound) == "Right/other" &&
            parents.Counterpart(Side.Local, "Ignored", bound) == "Left/item",
            "Counterpart uses the saved path on the requested side, not the supplied path");
        Check.Require(parents.Counterpart(Side.Local, "RemoteParent/fresh:name", null) == "LocalParent/fresh:name" &&
            parents.Counterpart(Side.Dropbox, "LocalParent/fresh_name", null) == "RemoteParent/fresh_name",
            "Without a binding, Counterpart maps through the nearest linked ancestor with raw names");

        var nullFaulted = false;
        try { await new Parents(context).EnsureAsync(Side.Local, null!, CancellationToken.None); }
        catch (NullReferenceException) { nullFaulted = true; }
        Check.Require(nullFaulted,
            "Ensure with a null path still faults with NullReferenceException, as the old boundary search did");

        CounterpartCallChecks();
        SavedBindingChecks();

        foreach (var target in new[] { Side.Local, Side.Dropbox })
        {
            await NestedCreationChecksAsync(target, 2);
            await NestedCreationChecksAsync(target, 3);
            await AncestorAskSequenceAsync(target);
            await CreationJoinChecksAsync(target);
            await EnsureChecksAsync(target);
            await KnownTargetChecksAsync(target);
            await CreateRaceChecksAsync(target);
            await BatchRecordChecksAsync(target);
            await IgnoredFolderKeepReceiptAsync(target);
            await ReadCountChecksAsync(target);
            await FailureChecksAsync(target);
            await SkippedAncestorAsync(target);
        }
    }

    private static async Task NestedCreationChecksAsync(Side target, int depth)
    {
        using var store = new InMemoryStateStore();
        await NestedCreationChecksAsync(target, depth, store, 0);
    }

    internal static async Task OwnFolderBudgetAsync(string directory, int smallItems, int largeItems)
    {
        foreach (var target in new[] { Side.Local, Side.Dropbox })
        {
            (int Reads, int Ensures, int Asks)? previous = null;
            int? previousStatements = null;
            foreach (var count in new[] { smallItems, largeItems })
            {
                using var store = Massdex.SyncShark.Engine.Storage.StateStore.Open(
                    Path.Combine(directory, "own-folder-" + target + "-" + count + ".db"));
                var connection = (Microsoft.Data.Sqlite.SqliteConnection)store.GetType().GetField("_connection",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
                var statements = 0;
                void Start() => SQLitePCL.raw.sqlite3_trace(connection.Handle,
                    (SQLitePCL.delegate_trace)((_, _) => Interlocked.Increment(ref statements)), null);
                var costs = await NestedCreationChecksAsync(target, 3, store, count, Start);
                SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null);
                Console.WriteLine($"A224 side={target} unrelated={count} depth=3 reads={costs.Reads} ensures={costs.Ensures} asks={costs.Asks} statements={statements}; row-visits/decodes unavailable");
                Check.Require(previous is null || previous == costs && previousStatements == statements,
                    "A224 fixed involved concurrent ancestors cost no more as unrelated pair entries grow");
                previous = costs;
                previousStatements = statements;
            }
        }
    }

    private static async Task<(int Reads, int Ensures, int Asks)> NestedCreationChecksAsync(
        Side target, int depth, IStateStore store, int unrelated, Action? startCounting = null)
    {
        var counted = new ReadCounter(new FakeSide(target));
        var asks = new AskCounter(store);
        var context = CountedContext(target, counted, asks);
        var pair = SourceFixture.Bound(context.Pair);
        context = context with { Pair = pair, Scope = new PathScope(pair, [pair]) };
        store.SavePair(pair);
        SourceFixture.AcquireJournal(store, pair);
        var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
        var source = store.ReadSource(pair.PairId, target)!.Value;
        SourceProvenance? provenance = null;
        if (target == Side.Dropbox)
        {
            provenance = new SourceProvenance("account-id", "namespace-id", "folder-id", pair.DropboxRoot,
                true, true, true, true, generation, source.Epoch);
            store.AdvanceSource(pair.PairId, target, generation, source.Epoch, source.Revision, source.Provenance,
                source.Cursor, source.Epoch, source.Revision, provenance, "initial", source.Obligation);
        }
        if (unrelated != 0)
            store.Commit(pair.PairId, StateChange.Empty with
            {
                Record = Enumerable.Range(0, unrelated).Select(index => Link("Unrelated/" + index, "Unrelated/" + index)).ToArray()
            });
        startCounting?.Invoke();
        var paths = Enumerable.Range(1, depth).Select(level => string.Join("/", Enumerable.Repeat("Parent", level))).ToArray();
        await context.For(PathNames.Other(target)).EnsureFolderAsync(paths[^1], default);
        var arrived = paths.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = paths.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var observed = paths.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        // Each re-ask sees the next echo before that folder is allowed to commit; thread scheduling cannot skip it.
        asks.BeforeAsk = side =>
        {
            if (side == target && asks.Asks(side) < depth)
                arrived[asks.Asks(side)].Task.GetAwaiter().GetResult();
        };
        asks.AfterAsk = (side, _) =>
        {
            if (side == target && asks.Asks(side) <= depth)
                observed[asks.Asks(side) - 1].TrySetResult();
        };
        counted.AfterEnsure = async (entry, ct) =>
        {
            var index = Array.IndexOf(paths, entry.Path);
            var notice = new ChangeNotice(target, ChangeKind.Changed, entry.Path, null, entry.Id, entry, false, false);
            var current = store.ReadSource(pair.PairId, target)!.Value;
            if (target == Side.Local)
            {
                var position = store.ReadJournal("1");
                store.AcquireJournalPage("1", position, position!,
                    [(pair.PairId, generation, current.Epoch, current.Revision, false)], [(pair.PairId, notice)]);
            }
            else store.AcquireDropboxPage(pair.PairId, generation, current.Epoch, current.Revision, provenance!,
                current.Cursor, [notice], entry.Path);
            arrived[index].SetResult();
            await release[index].Task.WaitAsync(ct);
        };
        var parents = new Parents(context);
        var creating = parents.EnsureAsync(target, paths[^1] + "/First", null, default);
        await arrived[0].Task;
        var incoming = File(paths[^1] + "/Second");
        var row = new Row(PathNames.Key(incoming.Path), target == Side.Dropbox ? incoming : null,
            target == Side.Local ? incoming : null, null, [], null, RowContext.Neutral with { Destination = incoming.Path });
        var fence = new AncestorFence(context);
        fence.CoverListedWork(0);
        var action = new Massdex.SyncShark.Engine.Planner.SyncAction(
            Massdex.SyncShark.Engine.Planner.SyncAction.CopyOn(false, target), row, Target: incoming.Path);
        var waiting = fence.BeforeExecutionAsync(action, parents, default).AsTask();
        for (var index = 0; index < depth; index++)
        {
            await arrived[index].Task;
            await observed[index].Task;
            Check.Require(store.SyncedByKey(pair.PairId, target, PathNames.KeyFor(target, paths[index])) is null,
                "A218/A227/A228: echo precedes each folder binding");
            release[index].SetResult();
        }
        await creating;
        Check.Require(await waiting == AncestorNews.Restated && !fence.AncestorRefused,
            "A227/A228/A229: nested concurrent creation reaches matching durable bindings in both directions");
        var unbound = asks.Log
            .Where(entry => entry.Side == target && entry.Answer.News == AncestorNews.Unbound)
            .Select(entry => entry.Answer.UnboundKey!).ToArray();
        Check.Require(unbound.SequenceEqual(paths
                .Select(path => PathNames.KeyFor(target, path))),
            "A227/A228: every re-ask observes the next folder's echo, never a repeated key");
        Check.Require(asks.Asks(target) <= depth + 1 && asks.Asks(PathNames.Other(target)) <= 1,
            $"A224/A227/A228: asks stay within depth+1 per side (D={depth}, {target}: " +
            $"{asks.Asks(target)}/{asks.Asks(PathNames.Other(target))})");
        Check.Require(counted.Ensures.SequenceEqual(paths), "A224: ordering creates each missing parent once");
        return (counted.Reads.Count, counted.Ensures.Count, asks.Log.Count);
    }

    private static async Task CreationJoinChecksAsync(Side target)
    {
        foreach (var cancel in new[] { false, true })
        foreach (var fail in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var counted = new ReadCounter(new FakeSide(target));
            var context = CountedContext(target, counted, store);
            store.SavePair(context.Pair);
            await context.For(PathNames.Other(target)).EnsureFolderAsync("Parent", default);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            counted.AfterEnsure = async (_, ct) =>
            {
                arrived.SetResult();
                await release.Task.WaitAsync(ct);
                if (fail) throw new InvalidOperationException("synthetic create failure");
            };
            var parents = new Parents(context);
            var creating = parents.EnsureAsync(target, "Parent/Child", null, default);
            await arrived.Task;
            using var cancellation = new CancellationTokenSource();
            var joining = parents.JoinCreationAsync(target, PathNames.KeyFor(target, "Parent"), cancellation.Token);
            Check.Require(!joining.IsCompleted && store.SyncedByKey(context.Pair.PairId, target,
                PathNames.KeyFor(target, "Parent")) is null, "A218: an effect is not a committed binding");
            if (cancel)
            {
                cancellation.Cancel();
                try { await joining; Check.Require(false, "A232 cancellation"); }
                catch (OperationCanceledException) { }
            }
            release.SetResult();
            try { await creating; Check.Require(!fail, "A231 failure propagated"); }
            catch (InvalidOperationException) when (fail) { }
            if (!cancel) await joining;
            Check.Require((store.SyncedByKey(context.Pair.PairId, target, PathNames.KeyFor(target, "Parent")) is not null) == !fail,
                "A219/A231/A232: only a committed binding survives; waiter cancellation does not cancel the creator");
            Check.Require(parents.JoinCreationAsync(target, PathNames.KeyFor(target, "Parent"), default).IsCompletedSuccessfully,
                "A230: no completed creation entry is retained");
        }

        // A failed record releases the waiter but leaves no binding, and the create effect still happened.
        using (var inner = new InMemoryStateStore())
        {
            var failing = new CommitFailStore(inner) { FailNextCommit = true };
            var counted = new ReadCounter(new FakeSide(target));
            var context = CountedContext(target, counted, failing);
            inner.SavePair(context.Pair);
            await context.For(PathNames.Other(target)).EnsureFolderAsync("Parent", default);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            counted.AfterEnsure = async (_, ct) =>
            {
                arrived.SetResult();
                await release.Task.WaitAsync(ct);
            };
            var parents = new Parents(context);
            var creating = parents.EnsureAsync(target, "Parent/Child", null, default);
            await arrived.Task;
            var joining = parents.JoinCreationAsync(target, PathNames.KeyFor(target, "Parent"), default);
            release.SetResult();
            try { await creating; Check.Require(false, "A219: a failed record propagates"); }
            catch (InvalidOperationException) { }
            await joining;
            Check.Require(await context.For(target).ReadOneAsync("Parent", default) is not null,
                "A219/A231: the create effect happened before the failed record");
            Check.Require(inner.SyncedByKey(context.Pair.PairId, target, PathNames.KeyFor(target, "Parent")) is null,
                "A219/A231: a failed Commit leaves no binding, and its waiter is released without one");
        }

        // A218 held record: the child waits while the record is held and proceeds once the binding lands.
        using (var inner = new InMemoryStateStore())
        {
            var held = new HoldCommitStore(inner);
            var counted = new ReadCounter(new FakeSide(target));
            var context = CountedContext(target, counted, held);
            inner.SavePair(context.Pair);
            await context.For(PathNames.Other(target)).EnsureFolderAsync("Parent", default);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            held.Release = release;
            counted.AfterEnsure = (_, _) =>
            {
                arrived.SetResult();
                return Task.CompletedTask;
            };
            var parents = new Parents(context);
            var creating = Task.Run(() => parents.EnsureAsync(target, "Parent/Child", null, default));
            await arrived.Task;
            var joining = parents.JoinCreationAsync(target, PathNames.KeyFor(target, "Parent"), default);
            Check.Require(!joining.IsCompleted, "A218: the child waits while the record is held");
            Check.Require(inner.SyncedByKey(context.Pair.PairId, target, PathNames.KeyFor(target, "Parent")) is null,
                "A218: the effect is not a binding");
            release.SetResult();
            await creating;
            await joining;
            Check.Require(inner.SyncedByKey(context.Pair.PairId, target, PathNames.KeyFor(target, "Parent")) is not null,
                "A218: the released record lets the waiter proceed");
        }

        // A219/A231 both levels: a second-level create failure leaves the first binding committed and the rest absent.
        using (var inner = new InMemoryStateStore())
        {
            var counted = new ReadCounter(new FakeSide(target));
            var context = CountedContext(target, counted, inner);
            inner.SavePair(context.Pair);
            await context.For(PathNames.Other(target)).EnsureFolderAsync("Parent/Parent", default);
            var ensures = 0;
            counted.AfterEnsure = (_, _) =>
            {
                if (++ensures == 2) throw new InvalidOperationException("synthetic second-level create failure");
                return Task.CompletedTask;
            };
            var parents = new Parents(context);
            try
            {
                await parents.EnsureAsync(target, "Parent/Parent/Child", null, default);
                Check.Require(false, "A219: the second-level failure propagates");
            }
            catch (InvalidOperationException) { }
            Check.Require(inner.SyncedByKey(context.Pair.PairId, target, PathNames.KeyFor(target, "Parent")) is not null,
                "A219/A231: the first level committed before the second-level failure");
            Check.Require(inner.SyncedByKey(context.Pair.PairId, target, PathNames.KeyFor(target, "Parent/Parent")) is null,
                "A219/A231: the failed second level has no binding");
        }
    }

    /// <summary>A227/A230: the fence's re-ask loop, forced through a scripted per-side answer sequence. Each new
    /// unbound key is joined once; asks stay at depth+1 per side; a repeated key or a Changed answer refuses,
    /// and a definite mismatch refuses on the first ask without touching Parents.</summary>
    private static async Task AncestorAskSequenceAsync(Side target)
    {
        const string path = "L1/L2/L3/Child";
        var keys = PathNames.Ancestors(path).Reverse().Select(ancestor => PathNames.KeyFor(target, ancestor)).ToArray();
        Check.Require(keys.Length == 3, "A227: the fixture supplies three distinct ancestor keys");
        AncestorAnswer Unbound(int level) => new(AncestorNews.Unbound, keys[level]);

        async Task<(AncestorNews Result, bool Refused, int Asks)> Drive(params AncestorAnswer[] answers)
        {
            using var inner = new InMemoryStateStore();
            var store = new ScriptedAskStore(inner, target, answers);
            var context = Context(new FakeSide(Side.Local), new FakeSide(Side.Dropbox), store);
            var row = new Row(PathNames.Key(path), null, null, null, [], null,
                RowContext.Neutral with { Destination = path });
            var action = new Massdex.SyncShark.Engine.Planner.SyncAction(
                Massdex.SyncShark.Engine.Planner.SyncAction.CopyOn(false, target), row, Target: path);
            var fence = new AncestorFence(context);
            fence.CoverListedWork(0);
            var result = await fence.BeforeExecutionAsync(action, new Parents(context), default);
            Check.Require(store.Asks(PathNames.Other(target)) <= 1,
                $"A224/A227: the non-target side is asked at most once ({target})");
            return (result, fence.AncestorRefused, store.Asks(target));
        }

        var full = await Drive(Unbound(0), Unbound(1), Unbound(2), new AncestorAnswer(AncestorNews.Restated));
        Check.Require(full.Result == AncestorNews.Restated && !full.Refused && full.Asks == 4,
            $"A227/A228: three joined ancestors ask depth+1 times per side (got {full.Asks}, {target})");

        var repeat = await Drive(Unbound(0), Unbound(0));
        Check.Require(repeat.Result == AncestorNews.Changed && repeat.Refused && repeat.Asks == 2,
            "A230: a repeated unbound key refuses after one extra ask without a wait");

        var mismatch = await Drive(new AncestorAnswer(AncestorNews.Changed));
        Check.Require(mismatch.Result == AncestorNews.Changed && mismatch.Refused && mismatch.Asks == 1,
            "A230: a definite mismatch refuses on the first ask, before any join");

        var late = await Drive(Unbound(0), new AncestorAnswer(AncestorNews.Changed));
        Check.Require(late.Result == AncestorNews.Changed && late.Refused && late.Asks == 2,
            "A230: a mismatch after a join still refuses");
    }

    private static async Task EnsureChecksAsync(Side target)
    {
        using var store = new InMemoryStateStore();
        var local = new FakeSide(Side.Local);
        var dropbox = new FakeSide(Side.Dropbox);
        var context = Context(local, dropbox, store);
        store.SavePair(context.Pair);
        var parents = new Parents(context);
        var other = target == Side.Local ? Side.Dropbox : Side.Local;
        var targetSide = context.For(target);
        var otherSide = context.For(other);
        var targetParent = target == Side.Local ? "LocalParent" : "RemoteParent";
        var otherParent = target == Side.Local ? "RemoteParent" : "LocalParent";
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Record = [Link("LocalParent", "RemoteParent")]
        });
        await otherSide.EnsureFolderAsync(otherParent, CancellationToken.None);
        await otherSide.EnsureFolderAsync(PathNames.Child(otherParent, "Child"), CancellationToken.None);
        var targetChild = PathNames.Child(targetParent, "Child");
        var item = PathNames.Child(targetChild, "item.txt");

        await parents.EnsureAsync(target, item, CancellationToken.None);
        foreach (var suffix in new[] { "", "Child" })
        {
            var localPath = suffix.Length == 0 ? "LocalParent" : PathNames.Child("LocalParent", suffix);
            var dropboxPath = suffix.Length == 0 ? "RemoteParent" : PathNames.Child("RemoteParent", suffix);
            var l = await local.ReadOneAsync(localPath, CancellationToken.None);
            var d = await dropbox.ReadOneAsync(dropboxPath, CancellationToken.None);
            Check.Require(l is { IsFolder: true } && d is { IsFolder: true },
                $"Ensure to {target} must leave both parent folders present for {suffix}.");
            var saved = store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(localPath));
            Check.Require(saved is not null && saved.LocalPath == localPath && saved.DropboxPath == dropboxPath
                && saved.IsFolder && saved.LocalId == l!.Id && saved.DropboxId == d!.Id
                && saved.LocalKey == PathNames.Key(localPath) && saved.DropboxKey == PathNames.DropboxKey(dropboxPath),
                $"Ensure to {target} must record the actual parents with correctly oriented sides.");
            Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Dropbox, PathNames.DropboxKey(dropboxPath)) == saved,
                "The parent record must be found through either side's key.");
        }
        Check.Require(await targetSide.ReadOneAsync(item, CancellationToken.None) is null,
            "Ensure must create only ancestors, not the requested item.");
        var recorded = store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key("LocalParent/Child"));
        await parents.EnsureAsync(target, item, CancellationToken.None);
        Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key("LocalParent/Child")) == recorded,
            "Repeating Ensure must preserve the existing parent record.");

        await parents.EnsureAsync(target, "Absent/Child/item.txt", CancellationToken.None);
        foreach (var path in new[] { "Absent", "Absent/Child" })
        {
            Check.Require(await targetSide.ReadOneAsync(path, CancellationToken.None) is { IsFolder: true },
                "An ancestor must be created even when the other side is absent.");
            Check.Require(store.SyncedByKey(context.Pair.PairId, target, PathNames.Key(path)) is null,
                "A parent absent on the other side must not be recorded.");
        }

        var existing = await targetSide.EnsureFolderAsync("Existing", CancellationToken.None);
        await otherSide.EnsureFolderAsync("Existing", CancellationToken.None);
        await parents.EnsureAsync(target, "Existing/item.txt", CancellationToken.None);
        Check.Require(await targetSide.ReadOneAsync("Existing", CancellationToken.None) == existing,
            "An existing ancestor must be left unchanged.");
        Check.Require(store.SyncedByKey(context.Pair.PairId, target, PathNames.Key("Existing")) is null,
            "Ensure must skip existing ancestors rather than record them.");

        var root = await targetSide.ReadOneAsync("", CancellationToken.None);
        foreach (var path in new[] { "", "root-item.txt" })
        {
            await parents.EnsureAsync(target, path, CancellationToken.None);
        }
        Check.Require(await targetSide.ReadOneAsync("", CancellationToken.None) == root,
            "Ensure must not create or change the pair root.");
        Check.Require(await targetSide.ReadOneAsync("root-item.txt", CancellationToken.None) is null,
            "A root-level item has no ancestors to create.");
    }

    private static async Task FailureChecksAsync(Side target)
    {
        foreach (var failure in new[] { "target-read", "target-create", "other-read" })
        foreach (var failAtRoot in new[] { false, true })
        foreach (var error in new Exception[]
        {
            new ItemFailedException(nameof(FailureChecksAsync), TextKey.ReadLocalFailed, null),
            new ChangedUnderneathException(nameof(FailureChecksAsync)),
            new OperationCanceledException(), new InvalidOperationException()
        })
        {
            using var store = new InMemoryStateStore();
            var targetSide = new FakeSide(target);
            var counted = new ReadCounter(targetSide);
            var context = CountedContext(target, counted, store);
            store.SavePair(context.Pair);
            var otherSide = (FakeSide)context.For(PathNames.Other(target));
            var root = nameof(FailureChecksAsync);
            var child = PathNames.Child(root, nameof(Entry));
            var leaf = PathNames.Child(child, nameof(Entry.Path));
            var item = PathNames.Child(leaf, nameof(Entry.Id));
            await otherSide.EnsureFolderAsync(leaf, CancellationToken.None);
            var failedPath = failAtRoot ? root : child;
            if (failure == "target-create")
            {
                counted.EnsureFailure = (failedPath, error);
            }
            else
            {
                (failure == "target-read" ? targetSide : otherSide).FailNext(failedPath, error);
            }
            var propagated = false;
            try { await new Parents(context).EnsureAsync(target, item, CancellationToken.None); }
            catch (Exception actual) when (ReferenceEquals(actual, error)) { propagated = true; }
            Check.Require(propagated, "Parent creation propagates the original error.");
            Check.Require(counted.Ensures.SequenceEqual(failure == "target-read"
                    ? Array.Empty<string>() : failAtRoot ? new[] { root } : new[] { root, child }),
                "Upward read failures create nothing; later failures stop creation at the failed folder.");
            var rootRecord = store.SyncedByKey(context.Pair.PairId, target, PathNames.Key(root));
            Check.Require((rootRecord is null) == (failAtRoot || failure == "target-read"),
                "An upward read failure creates nothing; a deeper creation or other-side failure preserves the completed parent record.");
            if (rootRecord is not null)
            {
                var created = await targetSide.ReadOneAsync(root, CancellationToken.None);
                var present = await otherSide.ReadOneAsync(root, CancellationToken.None);
                Check.Require(rootRecord == (target == Side.Local
                        ? SyncedEntries.From(created!, present!)
                        : SyncedEntries.From(present!, created!)),
                    "The committed prefix retains the actual folder identities after a later failure.");
            }
            Check.Require(store.SyncedByKey(context.Pair.PairId, target, PathNames.Key(child)) is null &&
                await targetSide.ReadOneAsync(leaf, CancellationToken.None) is null &&
                await targetSide.ReadOneAsync(item, CancellationToken.None) is null,
                "A failed parent is not recorded and no later ancestor or item is created.");
            Check.Require((await targetSide.ReadOneAsync(failedPath, CancellationToken.None) is not null) == (failure == "other-read"),
                "An other-side read failure preserves the parent already created on the target.");
            Check.Require(store.Issues(context.Pair.PairId).Count == 0,
                "Parents leaves failure notices to the action executor.");
        }
    }

    private static async Task SkippedAncestorAsync(Side target)
    {
        using var store = new InMemoryStateStore();
        var context = Context(new FakeSide(Side.Local), new FakeSide(Side.Dropbox), store);
        store.SavePair(context.Pair);
        var skipped = new Entry(nameof(SkippedAncestorAsync), true, 0, DateTimeOffset.UnixEpoch,
            nameof(SkippedAncestorAsync), "", "", nameof(Entry.Skip));
        ((FakeSide)context.For(target)).Seed(skipped, null);
        var otherSide = (FakeSide)context.For(target == Side.Local ? Side.Dropbox : Side.Local);
        var untouched = new InvalidOperationException();
        otherSide.FailNext(skipped.Path, untouched);
        await new Parents(context).EnsureAsync(target, PathNames.Child(skipped.Path, nameof(Entry)), CancellationToken.None);
        Check.Require(await context.For(target).ReadOneAsync(skipped.Path, CancellationToken.None) == skipped &&
            store.SyncedByKey(context.Pair.PairId, target, PathNames.Key(skipped.Path)) is null,
            "An existing skipped ancestor is neither recreated nor recorded.");
        var notRead = false;
        try { await otherSide.ReadOneAsync(skipped.Path, CancellationToken.None); }
        catch (InvalidOperationException actual) when (ReferenceEquals(actual, untouched)) { notRead = true; }
        Check.Require(notRead, "An existing ancestor needs no other-side read.");
    }

    private static async Task ReadCountChecksAsync(Side target)
    {
        // A replace of a target that already exists prepares no parents at all.
        {
            using var store = new InMemoryStateStore();
            var targetSide = new FakeSide(target);
            var counted = new ReadCounter(targetSide);
            var context = CountedContext(target, counted, store);
            store.SavePair(context.Pair);
            var item = PathNames.Child(PathNames.Child(nameof(ReadCountChecksAsync), nameof(Entry)), nameof(Entry.Path));
            targetSide.Seed(File(item), null);
            await targetSide.EnsureFolderAsync(PathNames.Child(nameof(ReadCountChecksAsync), nameof(Entry)), CancellationToken.None);

            await new Parents(context).EnsureAsync(target, item, CancellationToken.None);
            var ancestors = counted.Reads.Where(read => read != PathNames.Key(item)).ToList();
            Check.Require(ancestors.Count == 0,
                $"Ensure to {target} of an existing target must read no ancestor; read: {string.Join(", ", ancestors)}.");
            Check.Require(counted.Reads.Count == 1, "Ensure of an existing target reads only the target itself.");
        }

        // A create under an existing parent reads that parent once and stops there.
        {
            using var store = new InMemoryStateStore();
            var targetSide = new FakeSide(target);
            var counted = new ReadCounter(targetSide);
            var context = CountedContext(target, counted, store);
            store.SavePair(context.Pair);
            var parent = PathNames.Child(nameof(ReadCountChecksAsync), nameof(Entry));
            var item = PathNames.Child(parent, nameof(Entry.Path));
            await targetSide.EnsureFolderAsync(parent, CancellationToken.None);

            await new Parents(context).EnsureAsync(target, item, CancellationToken.None);
            var ancestors = counted.Reads.Where(read => read != PathNames.Key(item)).ToList();
            Check.Require(ancestors.Count == 1 && ancestors[0] == PathNames.Key(parent),
                $"Ensure to {target} under an existing parent reads exactly that parent; read: {string.Join(", ", ancestors)}.");
            Check.Require(await targetSide.ReadOneAsync(item, CancellationToken.None) is null,
                "Ensure must create only ancestors, not the requested item.");
        }

        // A deep create with only its immediate parent missing stops at the nearest existing ancestor.
        {
            using var store = new InMemoryStateStore();
            var targetSide = new FakeSide(target);
            var counted = new ReadCounter(targetSide);
            var context = CountedContext(target, counted, store);
            store.SavePair(context.Pair);
            var existing = "A/B/C/D/E";
            var parent = PathNames.Child(existing, "Missing");
            var item = PathNames.Child(parent, "item.txt");
            await targetSide.EnsureFolderAsync(existing, CancellationToken.None);

            await new Parents(context).EnsureAsync(target, item, CancellationToken.None);
            Check.Require(counted.Reads.SequenceEqual(new[] { item, parent, existing }.Select(PathNames.Key)),
                "A deep create reads the target, missing parent and nearest existing ancestor once, in upward order.");
            Check.Require(counted.Ensures.SequenceEqual(new[] { parent }),
                "Only the missing parent is ensured; existing ancestors are untouched.");
            Check.Require(await targetSide.ReadOneAsync(parent, CancellationToken.None) is { IsFolder: true } &&
                await targetSide.ReadOneAsync(item, CancellationToken.None) is null,
                "The deep create prepares its parent, not the target item.");
        }

        // A create under a missing three-level chain still creates the whole chain.
        {
            using var store = new InMemoryStateStore();
            var targetSide = new FakeSide(target);
            var counted = new ReadCounter(targetSide);
            var context = CountedContext(target, counted, store);
            store.SavePair(context.Pair);
            var first = nameof(ReadCountChecksAsync);
            var second = PathNames.Child(first, nameof(Entry));
            var third = PathNames.Child(second, nameof(Entry.Path));
            var item = PathNames.Child(third, nameof(Entry.Id));

            await new Parents(context).EnsureAsync(target, item, CancellationToken.None);
            foreach (var folder in new[] { first, second, third })
            {
                Check.Require(await targetSide.ReadOneAsync(folder, CancellationToken.None) is { IsFolder: true },
                    $"Ensure to {target} must create the missing ancestor {folder}.");
                Check.Require(store.SyncedByKey(context.Pair.PairId, target, PathNames.Key(folder)) is null,
                    "A parent absent on the other side must not be recorded.");
            }
            Check.Require(await targetSide.ReadOneAsync(item, CancellationToken.None) is null,
                "Ensure must create only ancestors, not the requested item.");
            Check.Require(counted.Reads.SequenceEqual(new[] { item, third, second, first }.Select(PathNames.Key)),
                "Ensure reads each missing ancestor once, upwards, without reading the pair root.");
            Check.Require(counted.Ensures.SequenceEqual(new[] { first, second, third }),
                "Ensure creates the missing chain downwards without ensuring the pair root.");
        }
    }

    /// <summary>BATCHING B5/P1: an observed target, an observed absence and the unknown-target entry point.</summary>
    private static async Task KnownTargetChecksAsync(Side target)
    {
        // A still-observed target already has its parents: no read and no create is needed.
        {
            using var store = new InMemoryStateStore();
            var targetSide = new ReadCounter(new FakeSide(target));
            var context = CountedContext(target, targetSide, store);
            store.SavePair(context.Pair);
            var item = PathNames.Child(nameof(KnownTargetChecksAsync), nameof(Entry.Path));
            await new Parents(context).EnsureAsync(target, item, Folder(item), CancellationToken.None);
            Check.Require(targetSide.Reads.Count == 0 && targetSide.Ensures.Count == 0,
                $"B5: an observed target on {target} prepares no parents and reads nothing.");
            Check.Require(await targetSide.ReadOneAsync(PathNames.Parent(item), CancellationToken.None) is null,
                "B5: a parent missing behind the observed target is not created.");
        }

        // A retained skipped target is also an authoritative observation: no Local counterpart is opened.
        {
            using var store = new InMemoryStateStore();
            var targetSide = new ReadCounter(new FakeSide(target));
            var context = CountedContext(target, targetSide, store);
            store.SavePair(context.Pair);
            var item = PathNames.Child(nameof(KnownTargetChecksAsync), nameof(Entry.Path));
            await new Parents(context).EnsureAsync(target, item, Folder(item) with { Skip = nameof(Entry.Skip) },
                CancellationToken.None);
            Check.Require(targetSide.Reads.Count == 0 && targetSide.Ensures.Count == 0,
                "B5/NAME: a retained skipped target is not re-read and creates no parents.");
        }

        // Null here is an actual observed absence, not expected=null for a conditional create.
        {
            using var store = new InMemoryStateStore();
            var targetSide = new ReadCounter(new FakeSide(target));
            var context = CountedContext(target, targetSide, store);
            store.SavePair(context.Pair);
            var otherSide = (FakeSide)context.For(PathNames.Other(target));
            var parent = PathNames.Child(nameof(KnownTargetChecksAsync), nameof(Entry));
            var item = PathNames.Child(parent, nameof(Entry.Path));
            await otherSide.EnsureFolderAsync(parent, CancellationToken.None);
            await new Parents(context).EnsureAsync(target, item, null, CancellationToken.None);
            Check.Require(targetSide.Reads.SequenceEqual(PathNames.Ancestors(item).Select(PathNames.Key)),
                "B5: an observed absence reads only the ancestors, never the target itself.");
            var pairId = context.Pair.PairId;
            Check.Require(store.SyncedByKey(pairId, target, PathNames.KeyFor(target, parent)) is { IsFolder: true } &&
                store.SyncedByKey(pairId, target, PathNames.KeyFor(target, nameof(KnownTargetChecksAsync))) is { IsFolder: true },
                "B5/U-BATCH: parents prepared from an observed absence are recorded on return.");
        }

        // The known-target overload keeps the null-path fault and needs no ancestors for a root-level path.
        {
            using var store = new InMemoryStateStore();
            var targetSide = new ReadCounter(new FakeSide(target));
            var context = CountedContext(target, targetSide, store);
            store.SavePair(context.Pair);
            var parents = new Parents(context);
            var nullFaulted = false;
            try { await parents.EnsureAsync(target, null!, null, CancellationToken.None); }
            catch (NullReferenceException) { nullFaulted = true; }
            Check.Require(nullFaulted, "B5: the known-target overload keeps the null-path fault.");
            await parents.EnsureAsync(target, "", null, CancellationToken.None);
            await parents.EnsureAsync(target, "root-item.txt", null, CancellationToken.None);
            Check.Require(targetSide.Reads.Count == 0 && targetSide.Ensures.Count == 0 &&
                await targetSide.ReadOneAsync("root-item.txt", CancellationToken.None) is null,
                "B5: a root path or a root-level item has no ancestors to prepare.");
        }
    }

    /// <summary>BATCHING B5/P5 C15: a parent appearing between the read and the create.</summary>
    private static async Task CreateRaceChecksAsync(Side target)
    {
        // The upward read saw nothing; the folder then appeared with an actual identity.
        {
            using var store = new InMemoryStateStore();
            var targetSide = new ReadCounter(new FakeSide(target));
            var context = CountedContext(target, targetSide, store);
            store.SavePair(context.Pair);
            var otherSide = (FakeSide)context.For(PathNames.Other(target));
            var folder = nameof(CreateRaceChecksAsync);
            var counterpart = await otherSide.EnsureFolderAsync(folder, CancellationToken.None);
            var appeared = Folder(folder) with { Id = nameof(Entry.Id) };
            targetSide.EnsureResult = appeared;
            await new Parents(context).EnsureAsync(target, PathNames.Child(folder, nameof(Entry.Path)), null,
                CancellationToken.None);
            var saved = store.SyncedByKey(context.Pair.PairId, target, PathNames.KeyFor(target, folder));
            Check.Require(saved is { IsFolder: true } &&
                saved.LocalId == (target == Side.Local ? appeared.Id : counterpart.Id) &&
                saved.DropboxId == (target == Side.Dropbox ? appeared.Id : counterpart.Id),
                "B5: a parent appearing between the read and the create is recorded with its actual identities.");
        }

        // The same race with a file occupying the parent path refuses the create and records nothing.
        {
            using var store = new InMemoryStateStore();
            var targetSide = new ReadCounter(new FakeSide(target));
            var context = CountedContext(target, targetSide, store);
            store.SavePair(context.Pair);
            var otherSide = (FakeSide)context.For(PathNames.Other(target));
            var folder = nameof(CreateRaceChecksAsync);
            await otherSide.EnsureFolderAsync(folder, CancellationToken.None);
            targetSide.EnsureFailure = (folder, new PathOccupiedException(folder));
            var refused = false;
            try
            {
                await new Parents(context).EnsureAsync(target, PathNames.Child(folder, nameof(Entry.Path)), null,
                    CancellationToken.None);
            }
            catch (PathOccupiedException) { refused = true; }
            Check.Require(refused &&
                store.SyncedByKey(context.Pair.PairId, target, PathNames.KeyFor(target, folder)) is null,
                "B5/P5: an occupied parent path refuses the create and records nothing.");
        }
    }

    /// <summary>BATCHING 4.7: folder Records stay on the synchronous Commit path, one per prepared parent.</summary>
    private static async Task BatchRecordChecksAsync(Side target)
    {
        using var store = new StoreCounters(new InMemoryStateStore());
        var targetSide = new ReadCounter(new FakeSide(target));
        var context = CountedContext(target, targetSide, store);
        store.SavePair(context.Pair);
        var otherSide = (FakeSide)context.For(PathNames.Other(target));
        var parent = PathNames.Child(nameof(BatchRecordChecksAsync), nameof(Entry));
        await otherSide.EnsureFolderAsync(parent, CancellationToken.None);
        store.Reset();
        await new Parents(context).EnsureAsync(target, PathNames.Child(parent, nameof(Entry.Path)), null,
            CancellationToken.None);
        Check.Require(store.CommitCalls == 2 && store.RecordCommitCalls == 0 && store.RecordCommittedMembers == 0,
            "4.7: each prepared folder parent is one synchronous Commit, never the async record drain.");
        Check.Require(targetSide.Mutations == 0 && targetSide.Ensures.Count == 2,
            "P2-10: parent preparation reads and creates folders only; no staging, publication or admission work.");
        var pairId = context.Pair.PairId;
        Check.Require(store.SyncedByKey(pairId, target, PathNames.KeyFor(target, parent)) is { IsFolder: true } &&
            store.SyncedByKey(pairId, target, PathNames.KeyFor(target, nameof(BatchRecordChecksAsync))) is { IsFolder: true },
            "4.7: both folder Records are durable when Ensure returns.");
    }

    /// <summary>CV-D09: the optional ignored-folder keep rules compose in PathScope and never filter parent prep.</summary>
    private static async Task IgnoredFolderKeepReceiptAsync(Side target)
    {
        const string ignoreSpec = nameof(SyncIgnoreKind.FileExtension) + ":tmp";
        PairConfig Pair(IReadOnlyList<string>? keepRules) => new("pair-a", "Pair A", "C:\\PairA", "/PairA",
            "connection-a", [ignoreSpec], [], FirstRunPolicy.NewestWins, true, null, null, keepRules);
        var kept = File("kept.tmp");
        Check.Require(new PathScope(Pair([ignoreSpec]), [Pair([ignoreSpec])], "").CoversIgnoredContents([kept]),
            "D09: ignored contents covered by the saved keep selectors are covered.");
        Check.Require(!new PathScope(Pair(null), [Pair(null)], "").CoversIgnoredContents([kept]),
            "D09: missing or null keep rules select nothing.");
        Check.Require(!new PathScope(Pair([]), [Pair([])], "").CoversIgnoredContents([kept]),
            "D09: an empty replacement omits every keep selector.");
        Check.Require(!new PathScope(Pair([ignoreSpec]), [Pair([ignoreSpec])], "").CoversIgnoredContents([File("keep.txt")]),
            "D09: an entry the ignore rules never ignored is not covered.");

        using var store = new InMemoryStateStore();
        var context = Context(new FakeSide(Side.Local), new FakeSide(Side.Dropbox), store, [ignoreSpec]);
        store.SavePair(context.Pair);
        var otherSide = (FakeSide)context.For(PathNames.Other(target));
        var folder = nameof(IgnoredFolderKeepReceiptAsync);
        await otherSide.EnsureFolderAsync(folder, CancellationToken.None);
        await new Parents(context).EnsureAsync(target, PathNames.Child(folder, nameof(Entry.Path)), null,
            CancellationToken.None);
        Check.Require(context.Pair.IgnoredFolderKeepRules is not null &&
            await context.For(target).ReadOneAsync(folder, CancellationToken.None) is { IsFolder: true },
            "D09: saved keep rules do not suppress or alter parent preparation.");
    }

    /// <summary>CV-NAMES: stored raw spellings and frozen keys are the parent mapping's authority.</summary>
    private static void SavedBindingChecks()
    {
        using var store = new InMemoryStateStore();
        var context = Context(new FakeSide(Side.Local), new FakeSide(Side.Dropbox), store);
        store.SavePair(context.Pair);
        var saved = new SyncedEntry(PathNames.Key("Left/mixed:case"), "Left/mixed:case",
            PathNames.DropboxKey("Right/Mixed:Case"), "Right/Mixed:Case", true,
            nameof(Entry.Id), 0, 0, nameof(Entry), "");
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var parents = new Parents(context);
        Check.Require(parents.Counterpart(Side.Dropbox, "Ignored", saved) == "Right/Mixed:Case" &&
            parents.Counterpart(Side.Local, "Ignored", saved) == "Left/mixed:case",
            "CV-NAMES: Counterpart returns each side's stored raw spelling, never a substituted one.");
        Check.Require(parents.Destination(Side.Dropbox, "left/MIXED:CASE/child.txt") == "Right/Mixed:Case/child.txt",
            "CV-NAMES: Destination finds the saved ancestor by its frozen key and keeps its stored spelling.");
        var frozen = saved with { LocalKey = nameof(Entry.Id) };
        Check.Require(frozen.KeyOn(Side.Local) == frozen.LocalKey && frozen.LocalKey != PathNames.Key(frozen.LocalPath),
            "CV-NAMES: KeyOn returns the stored key instead of recomputing it.");
    }

    private static ExecutorContext CountedContext(Side target, ReadCounter counted, IStateStore store)
    {
        ISide local = target == Side.Local ? counted : new FakeSide(Side.Local);
        ISide dropbox = target == Side.Dropbox ? counted : new FakeSide(Side.Dropbox);
        return Context(local, dropbox, store);
    }

    private static Entry File(string path) =>
        new(path, false, 0, DateTimeOffset.UnixEpoch, path, "", "", null);

    private static Entry Folder(string path) =>
        new(path, true, 0, DateTimeOffset.UnixEpoch, path, "", "", null);

    // These call counts were first measured by running the same fixtures against base 0066f738's
    // Destination in a scratch build ("RemoteParent/Nested/Child" = 2 store lookups, saved binding = 0); the checks
    // hold the same numbers without a frozen copy of the old expressions.
    private static void CounterpartCallChecks()
    {
        using var countedStore = new StoreCounters(new InMemoryStateStore());
        var local = new ReadCounter(new FakeSide(Side.Local));
        var dropbox = new ReadCounter(new FakeSide(Side.Dropbox));
        var context = Context(local, dropbox, countedStore);
        countedStore.SavePair(context.Pair);
        countedStore.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Record = [Link("LocalParent", "RemoteParent")]
        });
        var parents = new Parents(context);

        countedStore.Reset();
        var nested = parents.Counterpart(Side.Local, "RemoteParent/Nested/Child", null);
        Check.Require(nested == "LocalParent/Nested/Child" && countedStore.Commands == 2 &&
            local.Reads.Count == 0 && dropbox.Reads.Count == 0,
            $"Counterpart's unbound nested path keeps the baseline 2 store lookups and no provider reads (got {countedStore.Commands}).");

        var saved = Link("Left/Item", "Right/Other");
        countedStore.Reset();
        var bound = parents.Counterpart(Side.Dropbox, "Ignored/Deep/Path", saved);
        Check.Require(bound == "Right/Other" && countedStore.Commands == 0 &&
            local.Reads.Count == 0 && dropbox.Reads.Count == 0,
            "Counterpart's saved branch makes zero destination lookups, matching the baseline expression");
    }

    private static ExecutorContext Context(ISide local, ISide dropbox, IStateStore store,
        IReadOnlyList<string>? keepRules = null)
    {
        var pair = new PairConfig("pair-a", "Pair A", "C:\\PairA", "/PairA", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null, null, keepRules);
        var settings = new EngineSettings("", "", "", "", "", RecycleFallback.Fail, 1,
            FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows) { JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes, LogReadBufferCharacters = ScanTestSettings.LogReadBufferCharacters };
        return new ExecutorContext(
            Local: local, Dropbox: dropbox, Recovery: new FakeRecovery(), Store: store, Pair: pair,
            Scope: new PathScope(pair, [pair], settings.DefaultIgnoreRules), Settings: settings,
            Runtime: new ReplaceTurn(), Admission: new FakeAdmission(), Priority: false, Events: _ => { },
            ReserveDownloadAsync: RefuseReservation, Time: new FakeTimeProvider(),
            ReadDeletionEvidenceAsync: RefuseDeletionEvidence, BeginDownloadAttempt: RefuseAttempt,
            CompleteDownloadAttemptAsync: RefuseAttemptCompletion,
            UploadAdmissionIdentity: ("account-a", "route-a", "source-a"),
            ItemAttempts: new RefusingItemAttempts(), Opportunity: new object(), ExpectedGeneration: 0);
    }

    private static ValueTask<IAsyncDisposable> RefuseReservation(string pairId, string path, long size,
        CancellationToken ct) => throw new InvalidOperationException("Parents performs no download reservation.");

    private static Task<DeletionEvidence> RefuseDeletionEvidence(string path, bool isFolder, CancellationToken ct) =>
        throw new InvalidOperationException("Parents reads no deletion evidence.");

    private static RequestAttempt RefuseAttempt(object operation) =>
        throw new InvalidOperationException("Parents begins no download attempt.");

    private static Task<bool> RefuseAttemptCompletion(RequestAttempt attempt, CancellationToken ct) =>
        throw new InvalidOperationException("Parents completes no download attempt.");

    /// <summary>Parents never reports item attempts; any call is the required diagnostic refusal.</summary>
    private sealed class RefusingItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            throw new InvalidOperationException("Parents executes no item attempt.");

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode) =>
            throw new InvalidOperationException("Parents reports no item failure.");

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
            throw new InvalidOperationException("Parents completes no item attempt.");
    }

    private static SyncedEntry Link(string localPath, string dropboxPath) =>
        new(PathNames.Key(localPath), localPath, PathNames.DropboxKey(dropboxPath), dropboxPath, true,
            localPath, 0, 0, dropboxPath, "");

    /// <summary>An ISide that records each single-item read, for the parent-read counts.</summary>
    private sealed class ReadCounter(ISide inner) : ISide
    {
        public List<string> Reads { get; } = [];
        public List<string> Ensures { get; } = [];
        public (string Path, Exception Error)? EnsureFailure { get; set; }
        public Entry? EnsureResult { get; set; }
        public Func<Entry, CancellationToken, Task>? AfterEnsure { get; set; }

        /// <summary>Content-effects forwarded; parent preparation must leave this at zero (P2-10).</summary>
        public int Mutations { get; private set; }

        public Side Side => inner.Side;

        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
            inner.ReadAllAsync(under, options, ct);

        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
        {
            Reads.Add(PathNames.Key(path));
            return inner.ReadOneAsync(path, ct);
        }

        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            inner.OpenReadAsync(entry, attempt, ct);

        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);

        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
        {
            Mutations++;
            return inner.StageAsync(path, content, source, ct);
        }

        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct)
        {
            Mutations++;
            return inner.PublishAsync(staged, expected, selection, ct);
        }

        public async Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
        {
            Ensures.Add(path);
            if (EnsureFailure is { } failure && failure.Path == path) throw failure.Error;
            var result = EnsureResult is { } existing && PathNames.Same(existing.Path, path)
                ? existing : await inner.EnsureFolderAsync(path, ct);
            if (AfterEnsure is { } after) await after(result, ct);
            return result;
        }

        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
        {
            Mutations++;
            return inner.DeleteAsync(expected, selection, ct);
        }

        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
        {
            Mutations++;
            return inner.MoveAsync(expected, newPath, ct);
        }

        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
        {
            Mutations++;
            return inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        }

        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct) =>
            inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }

    /// <summary>Counts LaterAncestorNews asks and their answers, so the fence's re-ask behaviour is observable.</summary>
    private sealed class AskCounter : ForwardingStateStore
    {
        private readonly object _gate = new();
        private readonly List<(Side Side, AncestorAnswer Answer)> _log = [];

        public AskCounter(IStateStore inner) : base(inner) { }

        public Action<Side>? BeforeAsk { get; set; }
        public Action<Side, AncestorAnswer>? AfterAsk { get; set; }

        public IReadOnlyList<(Side Side, AncestorAnswer Answer)> Log
        {
            get { lock (_gate) return _log.ToArray(); }
        }

        public int Asks(Side side)
        {
            lock (_gate) return _log.Count(entry => entry.Side == side);
        }

        public override AncestorAnswer LaterAncestorNews(string pairId, long expectedGeneration, Side side,
            IReadOnlyList<string> ancestorKeys, long coveredThrough)
        {
            BeforeAsk?.Invoke(side);
            var answer = base.LaterAncestorNews(pairId, expectedGeneration, side, ancestorKeys, coveredThrough);
            lock (_gate) _log.Add((side, answer));
            AfterAsk?.Invoke(side, answer);
            return answer;
        }
    }

    /// <summary>Answers the scripted side from a fixed sequence and every other side with None.</summary>
    private sealed class ScriptedAskStore : ForwardingStateStore
    {
        private readonly Side _scripted;
        private readonly Queue<AncestorAnswer> _answers;
        private readonly Dictionary<Side, int> _asks = [];

        public ScriptedAskStore(IStateStore inner, Side scripted, params AncestorAnswer[] answers) : base(inner)
        {
            _scripted = scripted;
            _answers = new(answers);
        }

        public int Asks(Side side) => _asks.GetValueOrDefault(side);

        public override AncestorAnswer LaterAncestorNews(string pairId, long expectedGeneration, Side side,
            IReadOnlyList<string> ancestorKeys, long coveredThrough)
        {
            _asks[side] = Asks(side) + 1;
            if (side != _scripted) return new(AncestorNews.None);
            return _answers.Count == 0 ? new(AncestorNews.Restated) : _answers.Dequeue();
        }
    }

    /// <summary>Fails the next plain Commit once, for the failed-record path.</summary>
    private sealed class CommitFailStore : ForwardingStateStore
    {
        public CommitFailStore(IStateStore inner) : base(inner) { }

        public bool FailNextCommit { get; set; }

        public override void Commit(string pairId, StateChange change, Action<Issue>? committedIssue)
        {
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new InvalidOperationException("synthetic commit failure");
            }
            base.Commit(pairId, change, committedIssue);
        }
    }

    /// <summary>Holds every plain Commit until the test releases it, for the held-record path.</summary>
    private sealed class HoldCommitStore : ForwardingStateStore
    {
        public HoldCommitStore(IStateStore inner) : base(inner) { }

        public TaskCompletionSource? Release { get; set; }

        public override void Commit(string pairId, StateChange change, Action<Issue>? committedIssue)
        {
            if (Release is { } release) release.Task.GetAwaiter().GetResult();
            base.Commit(pairId, change, committedIssue);
        }
    }
}
