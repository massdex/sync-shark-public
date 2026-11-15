using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for ExclusionRunner (`--engine-checks syncer`), written by ExclusionRunner's writer.</summary>
public static class ExclusionRunnerChecks
{
    // The folder runner pages at the data-owned storage bound, exactly as a session does.
    private static readonly int PageSize = ShippedPageSize();

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var storageRoot = Directory.CreateTempSubdirectory(nameof(ExclusionRunnerChecks)).FullName;
        try { await RunCasesAsync(storageRoot); }
        finally { Directory.Delete(storageRoot, recursive: true); }
    }

    private static async Task RunCasesAsync(string storageRoot)
    {
        foreach (var real in new[] { false, true })
        {
            foreach (var side in Enum.GetValues<Side>())
                await FileRemovalKeepsChildHistoryAsync(storageRoot, real, side);
            await FileRemovalKeepsChildHistoryAsync(storageRoot, real, Side.Local, pending: true);
        }
        CostProbe();
        await UnknownSavedHashAsync(storageRoot);
        foreach (var side in Enum.GetValues<Side>())
        {
            foreach (var folder in new[] { false, true })
                await CopiesThenRemovesAsync(storageRoot, side, folder);
            await CopiesFolderContentsAsync(storageRoot, side);
            await ArrivalAfterCaptureAsync(storageRoot, side);
            await ChangedAfterPreservationAsync(storageRoot, side);
            await RestartAfterPreservationAsync(storageRoot, side);
            await ChangedAfterCaptureAsync(storageRoot, side);
            await SavedEntriesAreNotCopiedAsync(storageRoot, side);
            foreach (var changed in new[] { false, true })
                foreach (var resized in new[] { false, true })
                    foreach (var folder in new[] { false, true })
                        await SavedContentAsync(storageRoot, side, changed, resized, folder);
            foreach (var deleted in new[] { false, true })
                foreach (var folder in new[] { false, true })
                    await KeptSideChangedAsync(storageRoot, side, deleted, folder);
            if (side == Side.Local)
                foreach (var fail in new[] { false, true })
                    await DirtyChildPreservedAsync(storageRoot, fail);
            await SavedCopyFailureAsync(storageRoot, side);
            foreach (var folder in new[] { false, true })
                await BothEditedAsync(storageRoot, side, folder, false);
            await BothEditedAsync(storageRoot, side, false, true);
            await BothEditedAsync(storageRoot, side, false, true, retainedLabel: true);
            await BothEditedAsync(storageRoot, side, false, false, changeLabel: true);
            foreach (var changedSide in Enum.GetValues<Side>())
                foreach (var wasFolder in new[] { false, true })
                    await KindSwapAsync(storageRoot, side, changedSide, wasFolder);
            await KindTieAsync(storageRoot, side);
            await PreviewNeverHashesAsync(storageRoot, side);
            await OccupiedTargetKeepsEverythingAsync(storageRoot, side);
            await NothingAsync(side);
            foreach (var prefix in new[] { false, true })
                await IgnoredContentsAsync(storageRoot, side, prefix);
            foreach (var (rule, name) in new[]
            {
                ("", nameof(Entry)),
                (nameof(SyncIgnoreKind.FileName) + PathLocks.KeySeparator + nameof(Entry), nameof(Entry)),
                (nameof(SyncIgnoreKind.FileNamePrefix) + PathLocks.KeySeparator + "~$", "~$" + nameof(Entry)),
            })
                await ProtectedContentsAsync(storageRoot, side, rule, name);
            await NestedContentsAsync(side);
            await NestedRootRefusedWithoutListingAsync(side);
            await VanishedChildRemovesItsSubtreeAsync(storageRoot, side);
            await KeepDoesNothingAsync(side);
            foreach (var skip in new[] { TextKey.SkipOnlineOnly, TextKey.SkipLink, TextKey.SkipSpecial, TextKey.SkipNotDownloadable })
                foreach (var folder in new[] { false, true })
                    await SkippedContentsAsync(storageRoot, side, skip, folder);
            foreach (var operation in new[] { nameof(ISide.ReadOneAsync), nameof(ISide.OpenReadAsync), nameof(ISide.StageAsync), nameof(ISide.DeleteAsync) })
                foreach (var changed in new[] { false, true })
                    await FailureKeepsSourceAsync(storageRoot, side, operation, changed);
            await RemovalRefusalKeepsSourceAsync(storageRoot, side);
            await CancellationKeepsSourceAsync(storageRoot, side);
            await ListingFailureKeepsSourceAsync(storageRoot, side);
            foreach (var late in new[] { false, true })
                foreach (var operation in new[] { nameof(ISide.ReadOneAsync), nameof(ISide.ContentHashAsync),
                    nameof(ISide.StageAsync), nameof(ISide.EnsureFolderAsync) })
                    await DrainFailuresAsync(storageRoot, side, operation, late);
            await HeldChildAsync(storageRoot, side);
            foreach (var preview in new[] { false, true })
                foreach (var cancel in new[] { false, true })
                    await AdmissionBeforeObservationAsync(storageRoot, side, preview, cancel);
            await CrossLinkedUnreadableAsync(storageRoot, side);
            await ReadOnlyPreviewAsync(storageRoot, side);
            await CaptureOrderAsync(storageRoot, side);
            foreach (var before in new[] { false, true })
                foreach (var reinsert in new[] { false, true })
                    await TombstoneCaptureAsync(storageRoot, side, before, reinsert);
            foreach (var (known, rootUnreadable) in new[] { (false, false), (true, false), (false, true) })
                await UnreadableCaptureAsync(storageRoot, side, known, rootUnreadable);
        }
        await SkippedRootSavedHoldAsync(storageRoot);
        await ReservedStageAsync(storageRoot);
        await RecordsAreDurableBeforeCompletionAsync(storageRoot);
        await DownloadRefusedKeepsSourceAsync(storageRoot);
        await FolderRemovalNotFabricatedAsync(storageRoot);
    }

    private static async Task FileRemovalKeepsChildHistoryAsync(string storageRoot, bool real, Side side, bool pending = false)
    {
        var database = Path.Combine(storageRoot, Path.GetRandomFileName());
        using IStateStore store = real ? StateStore.Open(database) : new InMemoryStateStore();
        var exclusion = new Exclusion(side, "Excluded", false);
        var context = Context(store, exclusion);
        using var planStore = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        planStore.SavePair(context.Pair);
        if (!real) context = context with { Store = new PlanCaptureStore(store, planStore) };
        var localRoot = await context.Local.EnsureFolderAsync("Excluded", default);
        var remoteRoot = await context.Dropbox.EnsureFolderAsync("Excluded", default);
        await context.Local.EnsureFolderAsync("Outside", default);
        await context.Dropbox.EnsureFolderAsync("Outside", default);
        var children = new List<SyncedEntry>();
        var survivors = new List<(Side Side, Entry Entry)>();
        foreach (var (localPath, remotePath, liveSide) in new[]
        {
            ("Excluded/Inside", "Excluded/Inside", Other(side)),
            ("Excluded/CrossLocal", "Outside/CrossLocal", Side.Dropbox),
            ("Outside/CrossRemote", "Excluded/CrossRemote", Side.Local)
        })
        {
            var local = await PutAsync(context.Local, localPath);
            var remote = await PutAsync(context.Dropbox, remotePath);
            children.Add(SyncedEntries.From(local, remote));
            survivors.Add((liveSide, liveSide == Side.Local ? local : remote));
            await context.For(Other(liveSide)).DeleteAsync(liveSide == Side.Local ? remote : local, null, default);
        }
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = children });
        await context.Local.DeleteAsync(localRoot, null, default);
        await context.Dropbox.DeleteAsync(remoteRoot, null, default);
        var localFile = await PutAsync(context.Local, exclusion.Path);
        var remoteFile = await PutAsync(context.Dropbox, exclusion.Path);
        var savedRoot = SyncedEntries.From(localFile, remoteFile);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [savedRoot] });
        var source = context.For(side);
        var expected = side == Side.Local ? localFile : remoteFile;
        foreach (var outcome in new[] { RemovalOutcome.NoEffect, RemovalOutcome.Unknown })
        {
            var probe = new ProbeSide(source)
            {
                Removal = new RemovalResult(outcome, null, null, TextKey.RecycleUnavailable)
            };
            var refused = side == Side.Local ? context with { Local = probe } : context with { Dropbox = probe };
            var retry = store.Issues(context.Pair.PairId).Select(issue => issue.Id).ToHashSet();
            Check.Require(!await Runner(refused).ApplyAsync(exclusion, retry, default) &&
                probe.Calls.Any(call => call.Operation == nameof(ISide.DeleteAsync)) &&
                await source.ReadOneAsync(exclusion.Path, default) == expected &&
                store.SyncedByKey(context.Pair.PairId, side, Key(side, exclusion.Path)) == savedRoot &&
                children.All(child => store.SyncedByKey(context.Pair.PairId, Side.Local, child.LocalKey) == child),
                "A no-effect or unknown file exclusion preserves root and child history for retry.");
        }
        var retries = store.Issues(context.Pair.PairId).Select(issue => issue.Id).ToHashSet();
        if (pending)
        {
            var hash = await source.ContentHashAsync(expected, default);
            var operation = Guid.NewGuid().ToString();
            var pendingSide = new ProbeSide(source)
            {
                Remove = async (entry, selection, ct) =>
                {
                    var armed = new PendingEffect(1, operation, 0, PendingKind.LocalDelete, PendingPhase.Armed, entry.Path,
                        new RemovalProof(entry, nameof(FileRemovalKeepsChildHistoryAsync), hash), savedRoot, null,
                        null, null, null, false, null);
                    store.ArmPending(context.Pair.PairId, armed);
                    var result = await source.DeleteAsync(entry, selection, ct);
                    store.AdvancePending(context.Pair.PairId, armed, armed with { Generation = armed.Generation + 1, Phase = PendingPhase.Removed });
                    return result with { OperationId = operation };
                }
            };
            context = context with { Local = pendingSide };
        }
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var applied = await Runner(context).ApplyAsync(exclusion, retries, default);
        Console.WriteLine($"file-exclusion real={real}; side={side}; pending={pending}; allocated={GC.GetTotalAllocatedBytes(precise: true) - allocationBefore}");
        Check.Require(applied &&
            await source.ReadOneAsync(exclusion.Path, default) is null &&
            store.SyncedByKey(context.Pair.PairId, Side.Local, savedRoot.LocalKey) is null &&
            store.SyncedByKey(context.Pair.PairId, Side.Dropbox, savedRoot.DropboxKey) is null &&
            store.Pending(context.Pair.PairId) is NoPending,
            $"Successful retry removes the excluded file and retires only its root binding ({real}, {side}); issues=" +
            string.Join("|", store.Issues(context.Pair.PairId).Select(issue => issue.Cause)));
        foreach (var child in children)
        {
            Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, child.LocalKey) == child &&
                store.SyncedByKey(context.Pair.PairId, Side.Dropbox, child.DropboxKey) == child,
                $"File exclusion retains same-area and cross-moved child history ({real}, {side}, {child.LocalPath}).");
            if (real)
            {
                using var reopened = StateStore.Open(database);
                Check.Require(reopened.SyncedByKey(context.Pair.PairId, Side.Local, child.LocalKey) == child,
                    "File-exclusion child history survives reopening real storage.");
            }
        }
        foreach (var (liveSide, entry) in survivors.Skip(1))
            Check.Require(await context.For(liveSide).ReadOneAsync(entry.Path, default) == entry &&
                await context.For(liveSide).ContentHashAsync(entry, default) ==
                children.Single(child => (liveSide == Side.Local ? child.LocalPath : child.DropboxPath) == entry.Path).DropboxContentHash,
                "File exclusion leaves the surviving child copy and its bytes unchanged.");
    }

    // Real plan capture keeps StoredFolderPlan on this path without moving binding settlement.
    private sealed class PlanCaptureStore(IStateStore inner, IStateStore capture) : ForwardingStateStore(inner)
    {
        public override FolderPlan OpenFolderPlan(string pairId, Row folder, Side deletingFrom) =>
            capture.OpenFolderPlan(pairId, folder, deletingFrom);
    }

    private static async Task RemovalRefusalKeepsSourceAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(RemovalRefusalKeepsSourceAsync), false);
        var context = Context(store, exclusion);
        var source = side == Side.Local ? context.Local : context.Dropbox;
        var target = side == Side.Local ? context.Dropbox : context.Local;
        var entry = await PutAsync(source, exclusion.Path);
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, entry.Path,
                new IssueSubject([new IssueItem(side, entry.Path, entry.Id)], null))]
        });
        var issue = store.Issues(context.Pair.PairId).Single();
        var probe = new ProbeSide(source)
        {
            Removal = new RemovalResult(RemovalOutcome.NoEffect, null, null, TextKey.RecycleUnavailable)
        };
        context = side == Side.Local ? context with { Local = probe } : context with { Dropbox = probe };
        var retry = new HashSet<long> { issue.Id };
        Check.Require(!await Runner(context).ApplyAsync(exclusion, retry, default) &&
            await source.ReadOneAsync(entry.Path, default) is not null &&
            await target.ReadOneAsync(entry.Path, default) is not null &&
            store.Issues(context.Pair.PairId).Single() is { Cause: TextKey.RecycleUnavailable } current &&
            current.Id == issue.Id,
            "a refused removal preserves both copies and updates the continuing Failure with its actual cause");
        probe.Removal = null;
        Check.Require(await Runner(context).ApplyAsync(exclusion, retry, default) &&
            await source.ReadOneAsync(entry.Path, default) is null && store.Issues(context.Pair.PairId).Count == 0,
            "retry completes the pending exclusion and closes its removal Failure");
    }

    // A provider receipt or queued record is not durable completion: ApplyAsync reports applied only after the
    // shared executor's own record/removal transaction, so a reopened store already holds the settled state.
    private static async Task RecordsAreDurableBeforeCompletionAsync(string storageRoot)
    {
        var database = Path.Combine(storageRoot, Path.GetRandomFileName());
        string pairId;
        using (var store = StateStore.Open(database))
        {
            var exclusion = new Exclusion(Side.Local, nameof(RecordsAreDurableBeforeCompletionAsync), false);
            var context = Context(store, exclusion);
            pairId = context.Pair.PairId;
            await context.Local.EnsureFolderAsync(exclusion.Path, default);
            var child = await PutAsync(context.Local, PathNames.Child(exclusion.Path, nameof(Entry)));
            Check.Require(await Runner(context).ApplyAsync(exclusion, null, default),
                nameof(RecordsAreDurableBeforeCompletionAsync));
            Check.Require(await context.Dropbox.ReadOneAsync(child.Path, default) is not null,
                "The copy exists once ApplyAsync reports applied.");
        }
        using (var reopened = StateStore.Open(database))
            Check.Require(reopened.SyncedByKey(pairId, Side.Local,
                    Key(Side.Local, nameof(RecordsAreDurableBeforeCompletionAsync))) is null &&
                reopened.SyncedByKey(pairId, Side.Dropbox,
                    Key(Side.Dropbox, nameof(RecordsAreDurableBeforeCompletionAsync))) is null &&
                reopened.Issues(pairId).Count == 0,
                "The settled records and removed binding survive the claim of completion.");
    }

    // P2/U-DOWNLOAD: an exclusion removes nothing until the shared download-space owner admits the copy; a refusal
    // keeps the source and raises its exact cause.
    private static async Task DownloadRefusedKeepsSourceAsync(string storageRoot)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(Side.Dropbox, nameof(DownloadRefusedKeepsSourceAsync), false);
        var context = Context(store, exclusion);
        var source = context.Dropbox;
        var target = context.Local;
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var path = PathNames.Child(exclusion.Path, nameof(Entry));
        var entry = await PutAsync(source, path);
        var effects = new List<RunEvent>();
        context = context with
        {
            Events = effects.Add,
            ReserveDownloadAsync = (_, _, _, _) =>
                throw new ItemFailedException(path, TextKey.FailLocalNoSpace, null)
        };
        Check.Require(!await Runner(context).ApplyAsync(exclusion, null, default), nameof(DownloadRefusedKeepsSourceAsync));
        var issue = store.Issues(context.Pair.PairId).Single();
        Check.Require(await source.ReadOneAsync(root.Path, default) == root &&
            await source.ReadOneAsync(path, default) == entry &&
            await target.ReadOneAsync(path, default) is null &&
            issue.Kind == IssueKind.Failure && issue.Cause == TextKey.FailLocalNoSpace &&
            effects.All(effect => Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What != EffectKind.DeletedDropbox.ToString()),
            "A refused download reservation keeps the source and its bytes.");
    }

    // A Dropbox folder replacement is the caller's folder phase; the executor refuses inside the caller-owned
    // footprint without a provider delete, folder submission or durable removal, and both sides keep their entries.
    private static async Task FolderRemovalNotFabricatedAsync(string storageRoot)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(Side.Local, nameof(FolderRemovalNotFabricatedAsync), false);
        var context = Context(store, exclusion);
        var path = exclusion.Path;
        var local = await PutAsync(context.Local, path);
        var dropbox = await context.Dropbox.EnsureFolderAsync(path, default);
        var row = new Row(PathNames.Key(path), local, dropbox, null, [], null,
            new RowContext(false, false, false, null, false, false, false, false));
        var dropboxProbe = new ProbeSide(context.Dropbox);
        context = context with { Dropbox = dropboxProbe };
        var transfers = new Transfers(context);
        var actions = new ActionExecutor(context, transfers, new Parents(context),
            new KeepBothRunner(context, transfers));
        var refused = false;
        try
        {
            await actions.ExecuteAsync(
                new SyncAction(ActionKind.ReplaceKind, row, Target: path, Side: Side.Dropbox), null, default);
        }
        catch (LiveAdmissionStaleException) { refused = true; }
        var fabricated = dropboxProbe.Calls.Any(call =>
            call.Operation is nameof(ISide.DeleteAsync) or nameof(ISide.SubmitFolderRemovalAsync));
        Check.Require(refused && !fabricated && !store.ActiveFolderRemovals(context.Pair.PairId, default).Any() &&
            await context.Local.ReadOneAsync(path, default) == local &&
            await context.Dropbox.ReadOneAsync(path, default) == dropbox &&
            store.Issues(context.Pair.PairId).Count == 0,
            "A Dropbox folder replacement refuses inside the caller's footprint without a fabricated removal.");
    }

    private static async Task DrainFailuresAsync(string storageRoot, Side side, string operation, bool late)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(DrainFailuresAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.For(side);
        var target = (FakeSide)context.For(Other(side));
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var failedPath = PathNames.Child(root.Path, late ? "Z-Failed" : "A-Failed");
        var folder = operation == nameof(ISide.EnsureFolderAsync);
        var failed = folder ? await source.EnsureFolderAsync(failedPath, default) : await PutAsync(source, failedPath);
        var dependent = folder ? await PutAsync(source, PathNames.Child(failedPath, "Child")) : null;
        var safe = await PutAsync(source, PathNames.Child(root.Path, "B-Safe"));
        var last = await PutAsync(source, PathNames.Child(root.Path, "Y-Safe"));
        if (operation is nameof(ISide.ReadOneAsync) or nameof(ISide.ContentHashAsync))
        {
            await target.EnsureFolderAsync(root.Path, default);
            var other = await PutAsync(target, failedPath);
            store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(side, failed, other)] });
            var bytes = await ReadBytesAsync(source, failed);
            bytes[0] ^= 1;
            source.Seed(failed with { Modified = failed.Modified.AddTicks(1),
                ContentHash = side == Side.Dropbox ? DropboxContentHash.Compute(bytes) : "" }, bytes);
            if (side == Side.Dropbox)
                target.Seed(other with { Modified = other.Modified.AddTicks(1) }, await ReadBytesAsync(target, other));
        }
        var sourceProbe = new ProbeSide(source);
        var targetProbe = new ProbeSide(target);
        var armed = true;
        var failures = 0;
        var failing = operation is nameof(ISide.ContentHashAsync) or nameof(ISide.ReadOneAsync)
            ? side == Side.Local ? sourceProbe : targetProbe
            : targetProbe;
        failing.Before = (name, path) =>
        {
            if (armed && name == operation && path == failedPath)
            {
                failures++;
                throw new ItemFailedException(path, TextKey.PublishFailed, null);
            }
        };
        var effects = new List<RunEvent>();
        context = side == Side.Local ? context with { Local = sourceProbe, Dropbox = targetProbe, Events = effects.Add }
            : context with { Dropbox = sourceProbe, Local = targetProbe, Events = effects.Add };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Check.Require(!await Runner(context).ApplyAsync(exclusion, null, default),
                "A failed preservation remains pending. side=" + side + " operation=" + operation +
                " late=" + late + " attempt=" + attempt + " failures=" + failures);
            Check.Require(await source.ReadOneAsync(root.Path, default) == root &&
                await target.ReadOneAsync(safe.Path, default) is not null &&
                await target.ReadOneAsync(last.Path, default) is not null &&
                store.Issues(context.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure && issue.Path == failedPath),
                "Early and late failures preserve the source while independent siblings finish.");
            Check.Require(failures == attempt + 1 && effects.All(effect => Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What != EffectKind.DeletedLocal.ToString() &&
                Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What != EffectKind.DeletedDropbox.ToString()),
                "Every pending attempt reaches its injected failure and removes nothing.");
            var copied = targetProbe.Calls.Count(call => call.Operation == nameof(ISide.StageAsync) &&
                (call.Path == safe.Path || call.Path == last.Path));
            Check.Require(copied == 2, "Successful sibling copies are not transferred again on repeated failure.");
            if (dependent is not null)
                Check.Require(await target.ReadOneAsync(dependent.Path, default) is null &&
                    !targetProbe.Calls.Any(call => call.Path == dependent.Path),
                    "A failed folder blocks its dependent descendants, not its siblings.");
        }
        var added = await PutAsync(source, PathNames.Child(root.Path, "NewChild"));
        armed = false;
        Check.Require(await Runner(context).ApplyAsync(exclusion, null, default), "Recovery drains old and newly discovered preservation work.");
        Check.Require(await source.ReadOneAsync(root.Path, default) is null &&
            await target.ReadOneAsync(failedPath, default) is not null &&
            await target.ReadOneAsync(added.Path, default) is not null &&
            store.Issues(context.Pair.PairId).All(issue => issue.Kind != IssueKind.Failure),
            "Successful removal closes the completed failures without losing the new child.");
        // Three attempts capture once each; the successful folder removal adds its plan and the root's
        // inventory check. Completed siblings are still copied exactly once.
        var listings = sourceProbe.Calls.Count(call => call.Operation == nameof(ISide.ReadAllAsync));
        Check.Require(targetProbe.Calls.Count(call => call.Operation == nameof(ISide.StageAsync) &&
            (call.Path == safe.Path || call.Path == last.Path)) == 2 &&
            listings == 4,
            "Each attempt captures once and never recopies completed siblings. readalls=" + listings);
        if (dependent is not null)
            Check.Require(await target.ReadOneAsync(dependent.Path, default) is not null, "The dependent child recovers with its folder.");
        RequireForgotten(context, root.Path, failedPath, safe.Path, last.Path, added.Path,
            PathNames.Child(failedPath, "Child"));
    }

    private static async Task AdmissionBeforeObservationAsync(string storageRoot, Side side, bool preview, bool cancel)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        using var cancellation = new CancellationTokenSource();
        var exclusion = new Exclusion(side, nameof(AdmissionBeforeObservationAsync), false);
        var context = Context(store, exclusion);
        var source = context.For(side);
        await source.EnsureFolderAsync(exclusion.Path, default);
        await PutAsync(source, PathNames.Child(exclusion.Path, "Child"));
        var local = new ProbeSide(context.Local);
        var dropbox = new ProbeSide(context.Dropbox);
        var admission = new FakeAdmission { Hold = true, Level = 1 };
        context = context with { Local = local, Dropbox = dropbox, Admission = admission };
        var keys = LockKeys.ForPaths(context.Pair,
            [(side, exclusion.Path), (Other(side), new Parents(context).Destination(Other(side), exclusion.Path))]);
        Task operation;
        await using (await PathLocks.Shared.EnterAsync(keys, default))
        {
            var runner = Runner(context);
            operation = preview ? (Task)runner.PreviewAsync(exclusion, cancellation.Token)
                : runner.ApplyAsync(exclusion, null, cancellation.Token);
            Check.Require(admission.Blocked == 1 && admission.Admitted == 0 && !operation.IsCompleted &&
                local.Calls.Count == 0 && dropbox.Calls.Count == 0,
                "The compound operation queues its single permit before a conflicting lock or any observation.");
            if (cancel) cancellation.Cancel();
            else admission.Hold = false;
        }
        var cancelled = false;
        try { await operation; }
        catch (OperationCanceledException) { cancelled = true; }
        // The folder and file preservation each complete once; only Dropbox removal adds a completion. The
        // exclusion's own compound never reacquires admission; a folder removal takes its own permit
        // after the exclusion releases this one.
        Check.Require(cancelled == cancel &&
            admission.Admitted == (cancel ? 0 : preview ? 1 : 2) &&
            admission.Blocked == 0 &&
            admission.Completed == (cancel || preview ? 0 : side == Side.Local ? 2 : 3),
            "A queued compound keeps its first permit ordering and effect completion accounting. admitted=" +
            admission.Admitted + " completed=" + admission.Completed);
        if (cancel)
            Check.Require(local.Calls.Count == 0 && dropbox.Calls.Count == 0 && store.Issues(context.Pair.PairId).Count == 0,
                "Queued cancellation performs no provider work or failure write.");
    }

    private static async Task HeldChildAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(HeldChildAsync), false);
        var context = Context(store, exclusion);
        var source = context.For(side);
        var target = context.For(Other(side));
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var held = await PutAsync(source, PathNames.Child(root.Path, "A-Held"));
        var safe = await PutAsync(source, PathNames.Child(root.Path, "Z-Safe"));
        store.Commit(context.Pair.PairId, StateChange.Empty with { Raise =
            [(IssueKind.Failure, TextKey.FailDropboxNoPermission, held.Path,
                new IssueSubject([new IssueItem(side, held.Path, held.Id)], null))] });
        var issue = store.Issues(context.Pair.PairId).Single();
        Check.Require(!await Runner(context).ApplyAsync(exclusion, null, default) &&
            await source.ReadOneAsync(root.Path, default) == root &&
            await target.ReadOneAsync(held.Path, default) is null &&
            await target.ReadOneAsync(safe.Path, default) is not null &&
            store.IssueById(context.Pair.PairId, issue.Id) is not null,
            "Held preservation stays pending while a safe sibling drains; its notice survives.");
        Check.Require(await Runner(context).ApplyAsync(exclusion, new HashSet<long> { issue.Id }, default) && store.Issues(context.Pair.PairId).Count == 0,
            "Explicitly omitting the hold permits recovery and closes the completed failure.");
        RequireForgotten(context, root.Path, held.Path, safe.Path);
    }

    private static async Task SkippedRootSavedHoldAsync(string storageRoot)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(Side.Dropbox, nameof(SkippedRootSavedHoldAsync), false);
        var context = Context(store, exclusion);
        var local = await context.Local.EnsureFolderAsync("Mapped", default);
        var dropbox = await context.Dropbox.EnsureFolderAsync(exclusion.Path, default);
        var saved = SyncedEntries.From(local, dropbox);
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Record = [saved],
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, local.Path,
                new IssueSubject([new IssueItem(Side.Local, local.Path, local.Id)], null))]
        });
        var failure = store.Issues(context.Pair.PairId).Single();
        ((FakeSide)context.Dropbox).Seed(dropbox with { Skip = nameof(TextKey.SkipNotDownloadable) }, null);
        var localProbe = new ProbeSide(context.Local);
        var dropboxProbe = new ProbeSide(context.Dropbox);
        context = context with { Local = localProbe, Dropbox = dropboxProbe };
        Check.Require(!await Runner(context).ApplyAsync(exclusion, null, default),
            "A skipped Dropbox root held through its saved Local key stays pending.");
        var remaining = store.Issues(context.Pair.PairId).Single();
        Check.Require(remaining.Id == failure.Id && remaining.Kind == failure.Kind &&
            remaining.Cause == failure.Cause && remaining.Path == failure.Path &&
            store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
            localProbe.Calls.Count == 0 && dropboxProbe.Calls.Count == 1 &&
            dropboxProbe.Calls.Single() == (nameof(ISide.ReadOneAsync), exclusion.Path),
            "The saved-key hold preserves its failure and binding without a duplicate blocked notice or provider effects.");
    }

    private static async Task CrossLinkedUnreadableAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(CrossLinkedUnreadableAsync), false);
        var context = Context(store, exclusion);
        var localRoot = await context.Local.EnsureFolderAsync(side == Side.Local ? exclusion.Path : "Elsewhere", default);
        var dropboxRoot = await context.Dropbox.EnsureFolderAsync(side == Side.Dropbox ? exclusion.Path : "Elsewhere", default);
        var rootBinding = SyncedEntries.From(localRoot, dropboxRoot);
        await context.Local.EnsureFolderAsync("Unreadable", default);
        var localChild = await PutAsync(context.Local, "Unreadable/Child");
        var dropboxChild = await PutAsync(context.Dropbox, PathNames.Child(dropboxRoot.Path, "Child"));
        var childBinding = SyncedEntries.From(localChild, dropboxChild);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [rootBinding, childBinding] });
        UnreadableMember.Record(store, context.Pair.PairId, "Unreadable");
        var effects = new List<RunEvent>();
        context = context with { Events = effects.Add };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Check.Require(!await Runner(context).ApplyAsync(exclusion,
                null, default), "A cross-linked unreadable forget footprint stays pending.");
            Check.Require(await context.Local.ReadOneAsync(localRoot.Path, default) == localRoot &&
                await context.Dropbox.ReadOneAsync(dropboxRoot.Path, default) == dropboxRoot &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, rootBinding.LocalKey) == rootBinding &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, childBinding.LocalKey) == childBinding &&
                store.Issues(context.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure && issue.Cause == TextKey.ReadLocalFailed) &&
                effects.All(effect => Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What != EffectKind.DeletedLocal.ToString() && Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What != EffectKind.DeletedDropbox.ToString()),
                "Neither provider deletion nor either forget range may discard the protected binding.");
        }
        UnreadableMember.Release(store, context.Pair.PairId, "Unreadable");
        Check.Require(await Runner(context).ApplyAsync(exclusion, null, default) && store.Issues(context.Pair.PairId).Count == 0,
            "The next readable attempt completes the exclusion and closes its failure.");
        RequireForgotten(context, localRoot.Path, dropboxRoot.Path, localChild.Path, dropboxChild.Path);
    }

    private static async Task ReadOnlyPreviewAsync(string storageRoot, Side side)
    {
        var folder = Directory.CreateDirectory(Path.Combine(storageRoot, Path.GetRandomFileName())).FullName;
        var database = Path.Combine(folder, ResourceNames.DatabaseFile);
        ExecutorContext context;
        var exclusion = new Exclusion(side, nameof(ReadOnlyPreviewAsync), false);
        using (var store = StateStore.Open(database))
        {
            context = Context(store, exclusion);
            await context.For(side).EnsureFolderAsync(exclusion.Path, default);
            await PutAsync(context.For(side), PathNames.Child(exclusion.Path, "Child"));
        }
        var before = File.ReadAllBytes(database);
        using (var store = StateStore.OpenReadOnly(database)!)
        {
            var local = new ProbeSide(context.Local);
            var dropbox = new ProbeSide(context.Dropbox);
            context = context with { Store = store, Local = local, Dropbox = dropbox };
            var preview = await Runner(context).PreviewAsync(exclusion, default);
            Check.Require(preview.CopyFiles == 1 && preview.RemoveFiles == 1 &&
                System.Text.Json.JsonSerializer.Serialize(store.Pair(context.Pair.PairId)) ==
                    System.Text.Json.JsonSerializer.Serialize(context.Pair) &&
                store.Issues(context.Pair.PairId).Count == 0 && store.Pending(context.Pair.PairId) is NoPending,
                "Read-only preview consumes capture without changing pair, issue or pending state.");
            Check.Require(local.Calls.Concat(dropbox.Calls).All(call => call.Operation is nameof(ISide.ReadOneAsync) or nameof(ISide.ReadAllAsync)),
                "Preview performs neither hashes nor provider effects.");
        }
        var files = Directory.GetFiles(folder);
        var names = string.Join(", ", files.Select(Path.GetFileName));
        Check.Require(File.ReadAllBytes(database).SequenceEqual(before),
            "Read-only preview leaves durable bytes unchanged. Remaining files: " + names);
        // SQLite's WAL/SHM sidecars belong to the durable database, not the disposable plan.
        Check.Require(!Directory.EnumerateFiles(folder, Path.GetFileName(database) + StateSchema.PlanFileSuffix + "*" + StateSchema.DatabaseExtension).Any(),
            "Read-only preview removes its disposable plan. Remaining files: " + names);
    }

    private static async Task CaptureOrderAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(CaptureOrderAsync), false);
        var counters = new StoreCounters(store);
        var context = Context(counters, exclusion);
        var source = context.For(side);
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var parent = await source.EnsureFolderAsync(PathNames.Child(root.Path, "Parent"), default);
        var child = await PutAsync(source, PathNames.Child(parent.Path, "Child"));
        var sourceProbe = new ProbeSide(source) { Listing = [new(child.Path, child), new(parent.Path, parent)] };
        var targetProbe = new ProbeSide(context.For(Other(side)))
        {
            Before = (_, _) => Check.Require(sourceProbe.ListingDisposed, "Capture must dispose before the first preservation effect.")
        };
        context = side == Side.Local ? context with { Local = sourceProbe, Dropbox = targetProbe }
            : context with { Dropbox = sourceProbe, Local = targetProbe };
        Check.Require(await Runner(context).ApplyAsync(exclusion, null, default), "A reordered listing still applies.");
        var effects = targetProbe.Calls.Where(call => call.Operation is nameof(ISide.EnsureFolderAsync) or nameof(ISide.StageAsync))
            .Select(call => call.Path).Distinct().ToArray();
        // Preservation's capture becomes the frozen plan; only the pre-effect inventory check lists again.
        var listings = sourceProbe.Calls.Count(call => call.Operation == nameof(ISide.ReadAllAsync));
        Check.Require(effects.SequenceEqual(new[] { root.Path, parent.Path, child.Path }) &&
            listings == 2 && counters.PlanPhases[nameof(FolderPlan.CaptureAsync)].Succeeded == 1 &&
            counters.PlanPhases[nameof(FolderPlan.CaptureAsync)].Items == effects.Length &&
            counters.PlanPhases[nameof(FolderPlan.FrozenEvidence)].Items == effects.Length - 1,
            "The seeded root and captured descendants execute parent-first without a superfluous exclusion listing. readalls=" +
            listings);
        RequireForgotten(context, root.Path, parent.Path, child.Path);
    }

    private static async Task TombstoneCaptureAsync(string storageRoot, Side side, bool before, bool reinsert)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(TombstoneCaptureAsync), false);
        var context = Context(store, exclusion);
        var source = context.For(side);
        await source.EnsureFolderAsync(exclusion.Path, default);
        var parent = await source.EnsureFolderAsync(PathNames.Child(exclusion.Path, "Branch"), default);
        var child = await PutAsync(source, PathNames.Child(parent.Path, "Child"));
        var adjacent = await PutAsync(source, PathNames.Child(exclusion.Path, "BranchAdjacent"));
        var listing = new List<Listed>();
        if (before) listing.Add(new(parent.Path, null));
        listing.Add(new(parent.Path, parent));
        listing.Add(new(child.Path, child));
        listing.Add(new(adjacent.Path, adjacent));
        if (!before) listing.Add(new(parent.Path, null));
        if (reinsert)
        {
            listing.Add(new(parent.Path, parent));
            listing.Add(new(child.Path, child));
        }
        var probe = new ProbeSide(source) { Listing = listing, ListingUses = 2 };
        context = side == Side.Local ? context with { Local = probe } : context with { Dropbox = probe };
        var preview = await Runner(context).PreviewAsync(exclusion, default);
        var present = before || reinsert;
        Check.Require(preview.CopyFiles == (present ? 2 : 1) && preview.RemoveFiles == preview.CopyFiles,
            "Tombstones clear only their current subtree; later reinsertions and adjacent prefixes survive.");
        // The provider keeps no subtree whose tombstone the exclusion observed; the folder owner then lists the
        // real contents rather than the capture's synthetic marker.
        if (!present) await source.DeleteAsync(parent, null, default);
        Check.Require(await Runner(context).ApplyAsync(exclusion, null, default), "The captured final inventory is applied.");
        var target = context.For(Other(side));
        // Preview and apply capture once each; the folder owner adds its plan and the root's inventory check.
        var listings = probe.Calls.Count(call => call.Operation == nameof(ISide.ReadAllAsync));
        Check.Require((await target.ReadOneAsync(parent.Path, default) is not null) == present &&
            (await target.ReadOneAsync(child.Path, default) is not null) == present &&
            await target.ReadOneAsync(adjacent.Path, default) is not null && probe.ListingDisposed &&
            listings == 3,
            "Preview and apply each consume exactly one complete capture with identical tombstone semantics. readalls=" +
            listings);
        RequireForgotten(context, exclusion.Path, parent.Path, child.Path, adjacent.Path);
    }

    private static async Task UnreadableCaptureAsync(string storageRoot, Side side, bool known, bool rootUnreadable)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(UnreadableCaptureAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.For(side);
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var child = await source.EnsureFolderAsync(PathNames.Child(root.Path, "Child"), default);
        var bytes = await PutAsync(source, PathNames.Child(child.Path, "Content"));
        var probe = new ProbeSide(source)
        {
            Listing = [new(bytes.Path, bytes), new(child.Path, child with { Skip = TextKey.SkipUnreadable.ToString() })]
        };
        var target = new ProbeSide(context.For(Other(side)));
        context = side == Side.Local ? context with { Local = probe, Dropbox = target }
            : context with { Dropbox = probe, Local = target };
        var recorded = rootUnreadable ? root.Path : child.Path;
        if (known || rootUnreadable) UnreadableMember.Record(store, context.Pair.PairId, recorded);
        for (var attempt = 0; attempt < 2; attempt++)
            Check.Require(!await Runner(context).ApplyAsync(exclusion,
                null, default), "Known, root or newly captured unreadability remains pending.");
        var failure = store.Issues(context.Pair.PairId).Single(issue => issue.Kind != IssueKind.Skipped);
        Check.Require(await source.ReadOneAsync(root.Path, default) == root &&
            await source.ReadOneAsync(bytes.Path, default) == bytes && target.Calls.Count == 0 &&
            probe.Calls.Count(call => call.Operation == nameof(ISide.ReadAllAsync)) == (known || rootUnreadable ? 0 : 2) &&
            probe.Calls.All(call => call.Operation is nameof(ISide.ReadOneAsync) or nameof(ISide.ReadAllAsync)) &&
            failure.Kind == IssueKind.Failure && failure.Path == exclusion.Path &&
            failure.Cause == Text.ReadFailedOn(known || rootUnreadable ? Side.Local : side),
            "Unreadability blocks capture or rejects it completely before hashes, preservation or removal.");
        context = side == Side.Local ? context with { Local = source } : context with { Dropbox = source };
        if (known || rootUnreadable) UnreadableMember.Release(store, context.Pair.PairId, recorded);
        Check.Require(await Runner(context).ApplyAsync(exclusion, null, default) && store.Issues(context.Pair.PairId).Count == 0,
            "Readable recovery completes and closes the temporary failure.");
        RequireForgotten(context, root.Path, child.Path, bytes.Path);
    }

    private static async Task ReservedStageAsync(string storageRoot)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(Side.Local, nameof(ReservedStageAsync), false);
        var context = Context(store, exclusion);
        var localRoot = Directory.CreateDirectory(Path.Combine(storageRoot, Path.GetRandomFileName())).FullName;
        var pair = context.Pair with { LocalRoot = localRoot };
        store.SavePair(pair);
        var scope = new PathScope(pair, [pair]);
        var local = new LocalSide(pair, scope, context.Settings,
            new PendingNotes(new NoPendingStore(), pair.PairId, context.Settings.StoragePageSize, mutation: true),
            Path.Combine(storageRoot, Path.GetRandomFileName()), (_, _, _, _) => { });
        var folder = Directory.CreateDirectory(Path.Combine(localRoot, exclusion.Path)).FullName;
        var ordinary = Path.Combine(folder, "ordinary.stage");
        await File.WriteAllBytesAsync(ordinary, [1]);
        var stage = StageNames.For(ordinary);
        await File.WriteAllBytesAsync(stage, [2]);
        var stageDirectory = Directory.CreateDirectory(StageNames.For(ordinary)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(stageDirectory, "Child"), [3]);
        var previousBin = LocalBin.SendOverride;
        try
        {
            LocalBin.SendOverride = path => { Directory.Delete(path, recursive: true); return true; };
            var effects = new List<RunEvent>();
            context = context with { Pair = pair, Scope = scope, Local = local, Events = effects.Add };
            var preview = await Runner(context).PreviewAsync(exclusion, default);
            Check.Require(preview.CopyFiles == 2 && preview.RemoveFiles == 2 && File.Exists(stage),
                "Capture suppresses only the reserved regular file, not ordinary lookalikes or stage-shaped folders.");
            Check.Require(await Runner(context).ApplyAsync(exclusion, null, default), "The ordinary lookalike is preserved before removal.");
            var copied = new List<Entry>();
            await foreach (var listed in context.Dropbox.ReadAllAsync(exclusion.Path, new ListOptions(true, true), default))
                if (listed.Entry is { IsFolder: false } entry) copied.Add(entry);
            var ordinaryPath = PathNames.Child(exclusion.Path, Path.GetFileName(ordinary));
            var directoryPath = PathNames.Child(exclusion.Path, Path.GetFileName(stageDirectory));
            Check.Require(copied.Count == 2 &&
                (await ReadBytesAsync(context.Dropbox, copied.Single(entry => entry.Path == ordinaryPath))).SequenceEqual(new byte[] { 1 }) &&
                (await ReadBytesAsync(context.Dropbox, copied.Single(entry => entry.Path == PathNames.Child(directoryPath, "Child"))))
                    .SequenceEqual(new byte[] { 3 }) &&
                await context.Dropbox.ReadOneAsync(directoryPath, default) is { IsFolder: true } &&
                effects.All(effect => Path.GetFileName(effect.Path) != Path.GetFileName(stage)),
                "The reserved file never uploads; the ordinary lookalike and stage-shaped folder keep their bytes.");
        }
        finally
        {
            LocalBin.SendOverride = previousBin;
        }
    }

    private static async Task CopiesThenRemovesAsync(string storageRoot, Side side, bool folder)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(CopiesThenRemovesAsync), false);
        var context = Context(store, exclusion);
        var runner = Runner(context);
        var source = context.For(side);
        var target = context.For(Other(side));
        var entry = folder ? await source.EnsureFolderAsync(exclusion.Path, default)
            : await PutAsync(source, exclusion.Path);
        var preview = await runner.PreviewAsync(exclusion, default);
        Check.Require(folder ? preview.CopyFiles == 0 && preview.RemoveFiles == 0 :
            preview.CopyFiles == 1 && preview.RemoveFiles == 1, nameof(preview.CopyFiles));
        Check.Require(preview.CopyBytes == entry.Size && preview.RemoveBytes == entry.Size, nameof(preview.CopyBytes));
        Check.Require(!preview.BlockedByProtected && preview.Side == side && preview.Path == exclusion.Path, nameof(preview));
        Check.Require(preview.RecoveryPlace == (side == Side.Local ? TextKey.RecoveryRecycleBin : TextKey.RecoveryDropboxHistory), nameof(preview.RecoveryPlace));
        Check.Require(await source.ReadOneAsync(exclusion.Path, default) == entry &&
            await target.ReadOneAsync(exclusion.Path, default) is null && store.Issues(context.Pair.PairId).Count == 0 &&
            ((FakeAdmission)context.Admission).Admitted == 1 &&
            ((FakeAdmission)context.Admission).Completed == 0, nameof(ExclusionRunner.PreviewAsync));

        var originalBytes = folder ? Array.Empty<byte>() : await ReadBytesAsync(source, entry);
        var effects = new List<RunEvent>();
        context = context with { Events = effects.Add };
        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await source.ReadOneAsync(exclusion.Path, default) is null, nameof(ExclusionRunner.ApplyAsync));
        var copied = await target.ReadOneAsync(exclusion.Path, default);
        Check.Require(copied is not null && copied.IsFolder == folder, nameof(copied));
        if (!folder)
            Check.Require((await ReadBytesAsync(target, copied!)).SequenceEqual(originalBytes), nameof(ISide.OpenReadAsync));
        RequireForgotten(context, exclusion.Path);
        // Preservation completes once; only Dropbox removal adds a completion. Preview and apply take one permit
        // each, and a folder removal then takes its own folder permit.
        var expectedCompletions = side == Side.Local ? 1 : 2;
        var admission = (FakeAdmission)context.Admission;
        Check.Require(admission.Admitted == (folder ? 3 : 2) &&
            admission.Completed == expectedCompletions,
            nameof(IAdmission.AdmitAsync) + " admitted=" + admission.Admitted +
            " completed=" + admission.Completed);
        Check.Require(Massdex.SyncShark.Engine.Run.RunLog.Present(effects.First()).What == (folder ? EffectKind.FolderCreated : side == Side.Local ? EffectKind.Uploaded : EffectKind.Downloaded).ToString() &&
            Massdex.SyncShark.Engine.Run.RunLog.Present(effects.Last()).What == (side == Side.Local ? EffectKind.DeletedLocal : EffectKind.DeletedDropbox).ToString(), nameof(RunEventKind.Effect));
        Check.Require(context.Scope.Excludes(side == Side.Dropbox ? exclusion.Path : null,
            side == Side.Local ? exclusion.Path : null, folder), nameof(PathScope.Excludes));
    }

    private static async Task ChangedAfterCaptureAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(ChangedAfterCaptureAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.For(side);
        var target = context.For(Other(side));
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var child = await source.EnsureFolderAsync(PathNames.Child(root.Path, "Child"), default);
        var changed = child with { Id = "replacement" };
        var injected = false;
        var probe = new ProbeSide(target) { Before = (operation, path) =>
        {
            if (injected || operation != nameof(ISide.EnsureFolderAsync) || path != root.Path) return;
            injected = true;
            source.Seed(changed, null);
        } };
        context = side == Side.Local ? context with { Dropbox = probe } : context with { Local = probe };
        Check.Require(!await Runner(context).ApplyAsync(exclusion, null, default) && injected &&
            await source.ReadOneAsync(child.Path, default) == changed &&
            await target.ReadOneAsync(child.Path, default) is null &&
            store.Issues(context.Pair.PairId).Any(issue => issue.Path == child.Path && issue.Cause == TextKey.FailChanged),
            "An unrecorded captured folder changed before copy is refused by the shared freshness owner.");
    }

    private static async Task ChangedAfterPreservationAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(ChangedAfterPreservationAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.For(side);
        var target = context.For(Other(side));
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var child = await PutAsync(source, PathNames.Child(root.Path, "Child"));
        var original = await ReadBytesAsync(source, child);
        var changedBytes = original.Concat(new byte[] { 1 }).ToArray();
        var changed = child with { Size = changedBytes.Length, Modified = child.Modified.AddTicks(1),
            ContentHash = side == Side.Dropbox ? DropboxContentHash.Compute(changedBytes) : "" };
        var injected = false;
        context = context with { Events = effect =>
        {
            if (injected || effect.Kind != RunEventKind.Effect || effect.Path != child.Path) return;
            injected = true;
            source.Seed(changed, changedBytes);
        } };
        Check.Require(!await Runner(context).ApplyAsync(exclusion, null, default) && injected &&
            await source.ReadOneAsync(child.Path, default) == changed &&
            (await ReadBytesAsync(source, changed)).SequenceEqual(changedBytes) &&
            await target.ReadOneAsync(child.Path, default) is { } kept &&
            (await ReadBytesAsync(target, kept)).SequenceEqual(original),
            "A source edited after its preservation copy refuses frozen removal without losing either version.");
    }

    private static async Task RestartAfterPreservationAsync(string storageRoot, Side side)
    {
        var database = Path.Combine(storageRoot, Path.GetRandomFileName());
        var exclusion = new Exclusion(side, nameof(RestartAfterPreservationAsync), false);
        ExecutorContext context;
        Entry child;
        using (var store = StateStore.Open(database))
        {
            context = Context(store, exclusion);
            await context.For(side).EnsureFolderAsync(exclusion.Path, default);
            child = await PutAsync(context.For(side), PathNames.Child(exclusion.Path, "Child"));
            using var cancellation = new CancellationTokenSource();
            var cancelled = false;
            try
            {
                await Runner(context with { Events = effect =>
                {
                    if (effect.Kind == RunEventKind.Effect && effect.Path == child.Path) cancellation.Cancel();
                } }).ApplyAsync(exclusion, null, cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check.Require(cancelled && await context.For(side).ReadOneAsync(child.Path, default) == child &&
                store.SyncedByKey(context.Pair.PairId, side, Key(side, child.Path)) is not null,
                "Interruption after durable preservation keeps the source and its completed copy record.");
        }
        using var reopened = StateStore.Open(database);
        var target = new ProbeSide(context.For(Other(side)));
        var resumed = new ExecutorContext(side == Side.Local ? context.Local : target,
            side == Side.Dropbox ? context.Dropbox : target, context.Recovery, reopened, context.Pair, context.Scope,
            context.Settings, new ReplaceTurn(), new FakeAdmission(), context.Priority, _ => { },
            context.ReserveDownloadAsync, context.Time, context.ReadDeletionEvidenceAsync,
            context.BeginDownloadAttempt, context.CompleteDownloadAttemptAsync, context.UploadAdmissionIdentity,
            context.ItemAttempts, new object(), context.ExpectedGeneration)
        {
            CaptureFolderCursorAsync = context.CaptureFolderCursorAsync,
            ReadFolderChangesAsync = context.ReadFolderChangesAsync,
            CompleteRecoveryAttemptAsync = context.CompleteRecoveryAttemptAsync
        };
        Check.Require(await Runner(resumed).ApplyAsync(exclusion, null, default) &&
            await resumed.For(side).ReadOneAsync(exclusion.Path, default) is null &&
            await target.ReadOneAsync(child.Path, default) is not null &&
            !target.Calls.Any(call => call.Operation == nameof(ISide.StageAsync)),
            "Restart rebuilds scratch from durable copies and finishes removal without retransferring the child.");
    }

    private static async Task ArrivalAfterCaptureAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(ArrivalAfterCaptureAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.For(side);
        var target = context.For(Other(side));
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var captured = await PutAsync(source, PathNames.Child(root.Path, "Captured"));
        var arrival = captured with { Path = PathNames.Child(root.Path, "Arrival"), Id = "arrival" };
        var bytes = await ReadBytesAsync(source, captured);
        var injected = false;
        var probe = new ProbeSide(target) { Before = (operation, path) =>
        {
            if (injected || operation != nameof(ISide.StageAsync) || path != captured.Path) return;
            injected = true;
            source.Seed(arrival, bytes);
        } };
        context = side == Side.Local ? context with { Dropbox = probe } : context with { Local = probe };
        Check.Require(!await Runner(context).ApplyAsync(exclusion, null, default) && injected &&
            await source.ReadOneAsync(arrival.Path, default) == arrival &&
            (await ReadBytesAsync(source, arrival)).SequenceEqual(bytes) &&
            await source.ReadOneAsync(root.Path, default) is not null,
            "A file saved after exclusion capture survives and leaves the exclusion pending on either side.");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var applied = await Runner(context).ApplyAsync(exclusion, null, default);
            if (attempt == 1) Check.Require(applied && await source.ReadOneAsync(root.Path, default) is null &&
                !store.ActiveFolderRemovals(context.Pair.PairId, default).Any(),
                "The following pass completes the preserved exclusion and releases the operation. side=" + side +
                " applied=" + applied + " active=" + string.Join(",", store.ActiveFolderRemovals(context.Pair.PairId, default)
                    .Select(operation => operation.Phase + "/" + operation.Cause)) +
                " issues=" + string.Join(",", store.Issues(context.Pair.PairId).Select(issue => issue.Cause)));
            var copied = await target.ReadOneAsync(arrival.Path, default);
            Check.Require(copied is not null && (await ReadBytesAsync(target, copied)).SequenceEqual(bytes) &&
                store.ActiveFolderRemovals(context.Pair.PairId, default).Count() <= 1,
                "Later exclusion attempts preserve the arrival and never accumulate duplicate folder operations.");
        }
        Check.Require(probe.Calls.Count(call => call.Operation == nameof(ISide.StageAsync) &&
            call.Path == arrival.Path) == 1, "A preserved arrival is not transferred again on repeated deferral.");
    }

    private static async Task CopiesFolderContentsAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(CopiesFolderContentsAsync), false);
        var context = Context(store, exclusion);
        var source = context.For(side);
        var target = context.For(Other(side));
        await source.EnsureFolderAsync(exclusion.Path, default);
        var child = PathNames.Child(exclusion.Path, nameof(Entry));
        var nested = PathNames.Child(exclusion.Path, nameof(PairConfig));
        var grandchild = PathNames.Child(nested, nameof(SyncedEntry));
        await source.EnsureFolderAsync(nested, default);
        await PutAsync(source, child);
        await PutAsync(source, grandchild);
        var preview = await Runner(context).PreviewAsync(exclusion, default);
        Check.Require(preview.CopyFiles == new[] { child, grandchild }.Length && preview.CopyBytes == preview.RemoveBytes,
            nameof(CopiesFolderContentsAsync));
        await Runner(context).ApplyAsync(exclusion, null, default);
        foreach (var path in new[] { exclusion.Path, child, nested, grandchild })
            Check.Require(await source.ReadOneAsync(path, default) is null && await target.ReadOneAsync(path, default) is not null,
                nameof(CopiesFolderContentsAsync));
        RequireForgotten(context, exclusion.Path, child, nested, grandchild);
    }

    private static async Task SavedEntriesAreNotCopiedAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(SavedEntriesAreNotCopiedAsync), false);
        var context = Context(store, exclusion);
        var local = await PutAsync(context.Local, exclusion.Path);
        var dropbox = await PutAsync(context.Dropbox, exclusion.Path);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, dropbox)] });
        var preview = await Runner(context).PreviewAsync(exclusion, default);
        Check.Require(preview.CopyFiles == 0 && preview.RemoveFiles == 1, nameof(SavedEntriesAreNotCopiedAsync));
        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await context.For(side).ReadOneAsync(exclusion.Path, default) is null &&
            await context.For(Other(side)).ReadOneAsync(exclusion.Path, default) == (side == Side.Local ? dropbox : local),
            nameof(SavedEntriesAreNotCopiedAsync));
        Check.Require(((FakeAdmission)context.Admission).Admitted == 2 &&
            ((FakeAdmission)context.Admission).Completed == (side == Side.Local ? 0 : 1), nameof(IAdmission.AdmitAsync));
    }

    private static async Task SavedContentAsync(string storageRoot, Side side, bool changed, bool resized, bool folder)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(SavedContentAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.For(side);
        var target = context.For(Other(side));
        // Underscored siblings require the Observer's shared Dropbox parent snapshot.
        var path = folder ? PathNames.Child(exclusion.Path, "Entry_Name") : exclusion.Path;
        // A linked item can have a different name on the other side.
        var targetPath = folder ? PathNames.Child(exclusion.Path, "Synced_Entry") : nameof(SyncedEntry);
        var siblingPath = PathNames.Child(exclusion.Path, "Sibling_Name");
        Entry? sibling = null;
        if (folder)
        {
            var root = await source.EnsureFolderAsync(exclusion.Path, default);
            var otherRoot = await target.EnsureFolderAsync(exclusion.Path, default);
            var sourceSibling = await PutAsync(source, siblingPath);
            sibling = await PutAsync(target, siblingPath);
            store.Commit(context.Pair.PairId, StateChange.Empty with { Record =
                [SyncedEntries.From(side, root, otherRoot), SyncedEntries.From(side, sourceSibling, sibling)] });
        }
        var original = await PutAsync(source, path);
        var other = await PutAsync(target, targetPath);
        var saved = side == Side.Local ? SyncedEntries.From(original, other) : SyncedEntries.From(other, original);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var bytes = await ReadBytesAsync(source, original);
        if (changed)
        {
            if (resized) bytes = [.. bytes, 0];
            else bytes[0] ^= 1;
        }
        var edited = original with { Size = bytes.LongLength, Modified = original.Modified.AddTicks(1),
            ContentHash = side == Side.Dropbox ? DropboxContentHash.Compute(bytes) : "" };
        source.Seed(edited, bytes);
        var events = new List<RunEvent>();
        context = context with { Events = events.Add };
        var runner = Runner(context);
        var preview = await runner.PreviewAsync(exclusion, default);
        var mayNeedSync = changed || side == Side.Local;
        Check.Require(preview.CopyFiles == (mayNeedSync ? 1 : 0) && preview.CopyBytes == (mayNeedSync ? edited.Size : 0),
            nameof(SavedContentAsync));
        Check.Require(store.SyncedByKey(context.Pair.PairId, side, Key(side, path)) == saved &&
            await target.ReadOneAsync(targetPath, default) == other && events.Count == 0 &&
            ((FakeAdmission)context.Admission).Admitted == 1 &&
            ((FakeAdmission)context.Admission).Completed == 0, nameof(ExclusionRunner.PreviewAsync));
        var copiedBeforeDelete = false;
        context = context with { Events = effect =>
        {
            events.Add(effect);
            if (Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What == (side == Side.Local ? EffectKind.DeletedLocal : EffectKind.DeletedDropbox).ToString())
                copiedBeforeDelete = !changed || events.Any(e => e.Path == targetPath &&
                    Massdex.SyncShark.Engine.Run.RunLog.Present(e).What == (side == Side.Local ? EffectKind.Uploaded : EffectKind.Downloaded).ToString());
        } };
        var dropbox = new ProbeSide(context.Dropbox);
        context = context with { Dropbox = dropbox };
        await Runner(context).ApplyAsync(exclusion, null, default);
        if (folder)
            Check.Require(dropbox.ParentListings == 0 && await target.ReadOneAsync(siblingPath, default) == sibling,
                "Exact-subject observation lists no siblings and preserves the unchanged one.");
        var copied = await target.ReadOneAsync(targetPath, default);
        Check.Require(copied is not null && (await ReadBytesAsync(target, copied)).SequenceEqual(bytes) &&
            (!changed ? copied == other : copied != other), nameof(SavedContentAsync));
        Check.Require(copiedBeforeDelete && await source.ReadOneAsync(exclusion.Path, default) is null &&
            store.Issues(context.Pair.PairId).Count == 0 &&
            store.SyncedByKey(context.Pair.PairId, side, Key(side, path)) is null, nameof(ExclusionRunner.ApplyAsync));
        RequireForgotten(context, exclusion.Path, path, targetPath, siblingPath);
    }

    // D26/6.7: a saved child whose bytes changed without changing its identity, size or timestamp is still a content
    // change once acquired dirty evidence says so. The exclusion copies those bytes to the kept side before removing
    // the folder; when that preservation fails, nothing is removed.
    private static async Task DirtyChildPreservedAsync(string storageRoot, bool fail)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(Side.Local, nameof(DirtyChildPreservedAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.Local;
        var target = (FakeSide)context.Dropbox;
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var otherRoot = await target.EnsureFolderAsync(exclusion.Path, default);
        var path = PathNames.Child(exclusion.Path, nameof(Entry));
        var original = await PutAsync(source, path);
        var other = await PutAsync(target, path);
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(root, otherRoot), SyncedEntries.From(original, other)]
        });
        var bytes = await ReadBytesAsync(source, original);
        bytes[0] ^= 1;
        // The entry keeps the saved id, size and timestamp; only the acquired evidence reveals the content change.
        source.Seed(original, bytes);
        AcquireDirty(store, context.Pair, original);
        if (fail) target.FailNext(path, new ItemFailedException(path, TextKey.PublishFailed, null));
        var applied = await Runner(context).ApplyAsync(exclusion, null, default);
        var kept = await target.ReadOneAsync(path, default);
        if (fail)
        {
            Check.Require(!applied && await source.ReadOneAsync(exclusion.Path, default) == root &&
                await source.ReadOneAsync(path, default) == original &&
                (await ReadBytesAsync(source, original)).SequenceEqual(bytes) &&
                kept == other &&
                store.SyncedByKey(context.Pair.PairId, Side.Local, Key(Side.Local, path)) is not null &&
                store.Issues(context.Pair.PairId).Single().Cause == TextKey.PublishFailed,
                "A refused dirty-child preservation removes nothing and keeps the changed bytes.");
            return;
        }
        Check.Require(applied && kept is not null && kept.Size == bytes.LongLength &&
            (await ReadBytesAsync(target, kept)).SequenceEqual(bytes) &&
            await source.ReadOneAsync(exclusion.Path, default) is null &&
            await source.ReadOneAsync(path, default) is null && store.Issues(context.Pair.PairId).Count == 0,
            "Dirty-acquired bytes are copied before the excluded folder is removed.");
        RequireForgotten(context, exclusion.Path, path);
    }

    // The acquired dirty fact for one Local identity, exactly as the source listener journals it.
    private static void AcquireDirty(IStateStore store, PairConfig pair, Entry local)
    {
        var domain = pair.Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var position = store.ReadJournal(domain) ?? throw new InvalidOperationException("The fixture journal exists.");
        var source = store.ReadSource(pair.PairId, Side.Local)!.Value;
        var member = (pair.PairId, store.ReadPairExecution(pair.PairId)!.Value.Generation, source.Epoch,
            source.Revision, false);
        var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, local.Path, null, local.Id, null, true, false);
        Check.Require(store.AcquireJournalPage(domain, position, position, [member],
            [(pair.PairId, notice)]) is not null, "The dirty child's acquired evidence is journaled.");
    }

    private static async Task KeptSideChangedAsync(string storageRoot, Side side, bool deleted, bool folder)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(KeptSideChangedAsync), false);
        var context = Context(store, exclusion);
        var source = context.For(side);
        var target = (FakeSide)context.For(Other(side));
        var path = folder ? PathNames.Child(exclusion.Path, nameof(Entry)) : exclusion.Path;
        Entry? otherRoot = null;
        if (folder)
        {
            var root = await source.EnsureFolderAsync(exclusion.Path, default);
            otherRoot = await target.EnsureFolderAsync(exclusion.Path, default);
            store.Commit(context.Pair.PairId, StateChange.Empty with { Record =
                [side == Side.Local ? SyncedEntries.From(root, otherRoot) : SyncedEntries.From(otherRoot, root)] });
        }
        var original = await PutAsync(source, path);
        var other = await PutAsync(target, path);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record =
            [side == Side.Local ? SyncedEntries.From(original, other) : SyncedEntries.From(other, original)] });
        var bytes = await ReadBytesAsync(target, other);
        if (deleted)
            await target.DeleteAsync(otherRoot ?? other, null, default);
        else
        {
            bytes[0] ^= 1;
            other = other with { Modified = other.Modified.AddTicks(1),
                ContentHash = target.Side == Side.Dropbox ? DropboxContentHash.Compute(bytes) : "" };
            target.Seed(other, bytes);
        }
        var events = new List<RunEvent>();
        await Runner(context with { Events = events.Add }).ApplyAsync(exclusion, null, default);
        Check.Require(await source.ReadOneAsync(exclusion.Path, default) is null &&
            await source.ReadOneAsync(path, default) is null && store.Issues(context.Pair.PairId).Count == 0,
            "Exclusion completes when only the kept side changed.");
        Check.Require(await target.ReadOneAsync(path, default) == (deleted ? null : other) &&
            (deleted || (await ReadBytesAsync(target, other)).SequenceEqual(bytes)),
            "The kept side's edit or deletion is preserved.");
        if (folder)
            Check.Require(await target.ReadOneAsync(exclusion.Path, default) == (deleted ? null : otherRoot),
                "A deleted kept folder is not recreated.");
        // A whole-folder removal is one root effect; skipped actions add none and all links are forgotten.
        var deletedKind = (side == Side.Local ? EffectKind.DeletedLocal : EffectKind.DeletedDropbox).ToString();
        Check.Require(events.Count == 1 && Massdex.SyncShark.Engine.Run.RunLog.Present(events[0]).What == deletedKind && events[0].Path == exclusion.Path &&
            store.SyncedByKey(context.Pair.PairId, side, Key(side, exclusion.Path)) is null,
            "Only the exclusion root is removed; skipped actions have no effects and all links are forgotten. side=" +
            side + " folder=" + folder + " deleted=" + deleted + " count=" + events.Count);
        RequireForgotten(context, exclusion.Path, path);
    }

    private static async Task SavedCopyFailureAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(SavedCopyFailureAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.For(side);
        var target = (FakeSide)context.For(Other(side));
        var original = await PutAsync(source, exclusion.Path);
        var other = await PutAsync(target, exclusion.Path);
        var saved = side == Side.Local ? SyncedEntries.From(original, other) : SyncedEntries.From(other, original);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var bytes = await ReadBytesAsync(source, original);
        bytes[0] ^= 1;
        var edited = original with { Modified = original.Modified.AddTicks(1),
            ContentHash = side == Side.Dropbox ? DropboxContentHash.Compute(bytes) : "" };
        source.Seed(edited, bytes);
        target.FailNext(exclusion.Path, new ItemFailedException(exclusion.Path, TextKey.PublishFailed, null));
        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await source.ReadOneAsync(exclusion.Path, default) == edited &&
            await target.ReadOneAsync(exclusion.Path, default) == other &&
            store.SyncedByKey(context.Pair.PairId, side, Key(side, exclusion.Path)) == saved &&
            store.Issues(context.Pair.PairId).Single().Cause == TextKey.PublishFailed, nameof(SavedCopyFailureAsync));
        await Runner(context).ApplyAsync(exclusion, null, default);
        var copied = await target.ReadOneAsync(exclusion.Path, default);
        Check.Require(copied is not null && (await ReadBytesAsync(target, copied)).SequenceEqual(bytes) &&
            await source.ReadOneAsync(exclusion.Path, default) is null && store.Issues(context.Pair.PairId).Count == 0,
            nameof(SavedCopyFailureAsync));
    }

    private static async Task BothEditedAsync(string storageRoot, Side side, bool folder, bool reuseLabel,
        bool changeLabel = false, bool retainedLabel = false)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(BothEditedAsync), false);
        using var settingsStream = typeof(EngineSettings).Assembly.GetManifestResourceStream(ResourceNames.Settings)!;
        using var settingsReader = new StreamReader(settingsStream);
        var clock = new CountingClock(DateTimeOffset.UnixEpoch);
        var context = Context(store, exclusion) with
        {
            Settings = SettingsParser.Parse(settingsReader.ReadToEnd(), null),
            Time = clock
        };
        var path = folder ? PathNames.Child(exclusion.Path, nameof(Entry)) : exclusion.Path;
        if (folder)
        {
            var localRoot = await context.Local.EnsureFolderAsync(exclusion.Path, default);
            var dropboxRoot = await context.Dropbox.EnsureFolderAsync(exclusion.Path, default);
            store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(localRoot, dropboxRoot)] });
        }
        var local = await PutAsync(context.Local, path);
        var dropbox = await PutAsync(context.Dropbox, path);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, dropbox)] });
        var localBytes = await ReadBytesAsync(context.Local, local);
        var dropboxBytes = localBytes.ToArray();
        localBytes[0] ^= 1;
        dropboxBytes[1] ^= 1;
        local = local with { Modified = local.Modified.AddTicks(1) };
        dropbox = dropbox with { ContentHash = DropboxContentHash.Compute(dropboxBytes) };
        ((FakeSide)context.Local).Seed(local, localBytes);
        ((FakeSide)context.Dropbox).Seed(dropbox, dropboxBytes);
        Entry? reused = null;
        if (reuseLabel)
        {
            var row = new Row(PathNames.Key(local.Path), local, dropbox, null, [], null,
                new RowContext(false, false, false, null, false, false, false, false));
            var destination = retainedLabel ? Other(side) : side;
            var labels = await new KeepBothRunner(context, new Transfers(context)).ChooseLabelsAsync(row, default, destination);
            Check.Require(labels.On(destination).Label is not null && labels.On(Other(destination)).Label is null,
                "Selecting one retained leg never chooses a label for the other side.");
            var bytes = destination == Side.Local ? dropboxBytes : localBytes;
            reused = (destination == Side.Local ? local : dropbox) with
            {
                Path = labels.On(destination).Label!, Id = "reused-label",
                ContentHash = destination == Side.Dropbox ? DropboxContentHash.Compute(bytes) : ""
            };
            ((FakeSide)context.For(destination)).Seed(reused, bytes);
        }
        Entry? alteredLabel = null;
        var events = new List<RunEvent>();
        context = context with { Events = effect =>
        {
            events.Add(effect);
            if (changeLabel && Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What == EffectKind.KeptBoth.ToString() && Massdex.SyncShark.Engine.Run.RunLog.Present(effect).Outcome == Other(side).ToString())
            {
                alteredLabel = (side == Side.Local ? dropbox : local) with
                {
                    Path = effect.Path, Size = 1, Modified = local.Modified.AddTicks(1),
                    ContentHash = side == Side.Local ? DropboxContentHash.Compute([1]) : ""
                };
                ((FakeSide)context.For(Other(side))).Seed(alteredLabel, [1]);
            }
        } };
        await Runner(context).ApplyAsync(exclusion, null, default);
        // P6 phase 1: the exclusion re-decision samples the shared current-time clock; differing contents are kept
        // side by side under the existing Info, never replaced by a time-only winner.
        Check.Require(clock.Reads > 0, "The exclusion re-decision samples the shared current-time clock (P6 phase 1).");
        if (changeLabel)
        {
            Check.Require(alteredLabel is not null &&
                await context.For(Other(side)).ReadOneAsync(alteredLabel.Path, default) == alteredLabel &&
                await context.For(side).ReadOneAsync(exclusion.Path, default) is null,
                "An edited retained label is never treated as temporary excluded-side content.");
        }
        var kept = new List<byte[]>();
        await foreach (var listed in context.For(Other(side)).ReadAllAsync("", new ListOptions(true, true), default))
            if (listed.Entry is { IsFolder: false } entry) kept.Add(await ReadBytesAsync(context.For(Other(side)), entry));
        Check.Require(kept.Count == Enum.GetValues<Side>().Length &&
            (changeLabel ? kept.Any(bytes => bytes.SequenceEqual(new byte[] { 1 })) :
                kept.Any(bytes => bytes.SequenceEqual(side == Side.Local ? localBytes : dropboxBytes))) &&
            kept.Any(bytes => bytes.SequenceEqual(side == Side.Local ? dropboxBytes : localBytes)),
            "Exclusion preserves both edited versions on the kept side.");
        Check.Require(await context.For(side).ReadOneAsync(exclusion.Path, default) is null,
            "Exclusion removes its original after keeping both versions.");
        await foreach (var listed in context.For(side).ReadAllAsync("", new ListOptions(true, true), default))
            Check.Require(!retainedLabel && reused is not null && listed.Entry == reused,
                "No label is created on the excluded side; its pre-existing sibling remains untouched.");
        if (reused is not null)
            Check.Require(await context.For(retainedLabel ? Other(side) : side).ReadOneAsync(reused.Path, default) == reused,
                "A pre-existing sibling is not replaced or removed.");
        Check.Require(events.Count(effect => Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What == EffectKind.KeptBoth.ToString()) == 1 &&
            events.Single(effect => effect.EffectKind == EffectKind.KeptBoth).Side == Other(side),
            "Only the retained keep-both leg emits an effect.");
        Check.Require(store.Issues(context.Pair.PairId).Single().Cause == TextKey.InfoKeptBoth &&
            store.SyncedByKey(context.Pair.PairId, side, Key(side, exclusion.Path)) is null,
            "Keep-both reports Info and leaves no links to removed copies.");
        var notice = store.Issues(context.Pair.PairId).Single();
        var rendered = Massdex.SyncShark.Engine.Edge.IssuePresentation.Render(notice,
            new(RetryPresentationState.Unavailable));
        var excludedLocation = (side == Side.Local ? "this PC" : "Dropbox") + " \"" + path + "\"";
        Check.Require(!rendered.Contains(excludedLocation, StringComparison.Ordinal) &&
            !rendered.Contains("counterpart", StringComparison.Ordinal) && !rendered.Contains("Linked", StringComparison.Ordinal),
            "Exclusion keep-both never names an excluded-side location or claims cross-side links.");
        RequireForgotten(context, [exclusion.Path, path, .. events.Where(e => Massdex.SyncShark.Engine.Run.RunLog.Present(e).What == EffectKind.KeptBoth.ToString()).Select(e => e.Path)]);
    }

    private static async Task KindSwapAsync(string storageRoot, Side excludedSide, Side changedSide, bool wasFolder)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(excludedSide, nameof(KindSwapAsync), false);
        var context = Context(store, exclusion);
        var local = wasFolder ? await context.Local.EnsureFolderAsync(exclusion.Path, default) : await PutAsync(context.Local, exclusion.Path);
        var dropbox = wasFolder ? await context.Dropbox.EnsureFolderAsync(exclusion.Path, default) : await PutAsync(context.Dropbox, exclusion.Path);
        var saved = SyncedEntries.From(local, dropbox);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var changed = (changedSide == Side.Local ? local : dropbox) with { IsFolder = !wasFolder };
        ((FakeSide)context.For(changedSide)).Seed(changed, wasFolder ? [1] : null);
        var events = new List<RunEvent>();
        await Runner(context with { Events = events.Add }).ApplyAsync(exclusion, null, default);
        Check.Require(await context.Local.ReadOneAsync(exclusion.Path, default) == (changedSide == Side.Local ? changed : local) &&
            await context.Dropbox.ReadOneAsync(exclusion.Path, default) == (changedSide == Side.Dropbox ? changed : dropbox) &&
            store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
            events.All(effect => effect.Kind == RunEventKind.Issue), "A kind swap removes or replaces neither side.");
        Check.Require(store.Issues(context.Pair.PairId).Single().Kind == IssueKind.NameClash,
            "A file/folder swap raises its normal name clash.");
    }

    private static async Task KindTieAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(KindTieAsync), false);
        var context = Context(store, exclusion);
        var local = await context.Local.EnsureFolderAsync(exclusion.Path, default);
        var dropbox = await context.Dropbox.EnsureFolderAsync(exclusion.Path, default);
        var saved = SyncedEntries.From(local, dropbox);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        local = local with { IsFolder = false, Size = 1 };
        dropbox = dropbox with { IsFolder = false, Size = 1, ContentHash = DropboxContentHash.Compute([1]) };
        ((FakeSide)context.Local).Seed(local, [0]);
        ((FakeSide)context.Dropbox).Seed(dropbox, [1]);
        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await context.Local.ReadOneAsync(exclusion.Path, default) == local &&
            await context.Dropbox.ReadOneAsync(exclusion.Path, default) == dropbox &&
            store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
            store.Issues(context.Pair.PairId).Single().Kind == IssueKind.Tie, "A tie removes nothing.");
    }

    private static async Task PreviewNeverHashesAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(PreviewNeverHashesAsync), false);
        var context = Context(store, exclusion);
        var local = await PutAsync(context.Local, exclusion.Path);
        var dropbox = await PutAsync(context.Dropbox, exclusion.Path);
        var saved = SyncedEntries.From(local, dropbox);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var changed = (side == Side.Local ? local : dropbox) with { Modified = local.Modified.AddTicks(1),
            ContentHash = side == Side.Dropbox ? nameof(Entry) : "" };
        // Metadata remains readable, but any attempt to read or hash this locked content fails.
        ((FakeSide)context.For(side)).Seed(changed, null);
        var preview = await Runner(context).PreviewAsync(exclusion, default);
        Check.Require(preview.CopyFiles == 1 && preview.CopyBytes == changed.Size && preview.RemoveFiles == 1 &&
            store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
            store.Issues(context.Pair.PairId).Count == 0 && ((FakeAdmission)context.Admission).Admitted == 1 &&
            ((FakeAdmission)context.Admission).Completed == 0,
            "Preview counts uncertain metadata without reading content or changing state.");
    }

    private static async Task OccupiedTargetKeepsEverythingAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(OccupiedTargetKeepsEverythingAsync), false);
        var context = Context(store, exclusion);
        var source = context.For(side);
        var target = context.For(Other(side));
        var folder = await source.EnsureFolderAsync(exclusion.Path, default);
        var path = PathNames.Child(exclusion.Path, nameof(Entry));
        var original = await PutAsync(source, path);
        await target.EnsureFolderAsync(exclusion.Path, default);
        var occupant = await PutAsync(target, path);
        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await source.ReadOneAsync(exclusion.Path, default) == folder &&
            await source.ReadOneAsync(path, default) == original && await target.ReadOneAsync(path, default) == occupant,
            nameof(OccupiedTargetKeepsEverythingAsync));
        var issue = store.Issues(context.Pair.PairId).Single(item => item.Kind == IssueKind.NameClash);
        Check.Require(issue.Cause == TextKey.ClashNameTaken && issue.Subject.Incoming == side &&
            issue.Subject.Items.Any(item => item.Id == occupant.Id && item.Side == Other(side)), nameof(Issue.Subject));
        await target.DeleteAsync(occupant, null, default);
        // Deleting the occupant removes the clash's former participant, so Repair retires the stored NameClash
        // before the exclusion is applied (engine.md 2.4: an open Tie/NameClash holds the row, and one whose
        // former participant identity set no longer exists retires).
        store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Close = [(issue.Path, new[] { IssueKind.NameClash })]
        });
        var applied = await Runner(context).ApplyAsync(exclusion, null, default);
        var gone = await source.ReadOneAsync(exclusion.Path, default);
        var kept = await target.ReadOneAsync(path, default);
        Check.Require(gone is null && kept is not null,
            nameof(ExclusionRunner.ApplyAsync) + " side=" + side + " applied=" + applied + " gone=" + (gone is null) +
            " kept=" + (kept is not null));
    }

    private static async Task NothingAsync(Side side)
    {
        foreach (var keep in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var exclusion = new Exclusion(side, nameof(NothingAsync), keep);
            var context = Context(store, exclusion);
            var entry = keep ? await PutAsync(context.For(side), exclusion.Path) : null;
            var preview = await Runner(context).PreviewAsync(exclusion, default);
            Check.Require(await Runner(context).ApplyAsync(exclusion, null, default), "Keep and already-absent exclusions are applied.");
            Check.Require(preview.CopyFiles == 0 && preview.RemoveFiles == 0 &&
                await context.For(side).ReadOneAsync(exclusion.Path, default) == entry &&
                ((FakeAdmission)context.Admission).Admitted == (keep ? 0 : 2) &&
                ((FakeAdmission)context.Admission).Completed == 0 && store.Issues(context.Pair.PairId).Count == 0, nameof(NothingAsync));
        }
    }

    private static async Task IgnoredContentsAsync(string storageRoot, Side side, bool prefix)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(IgnoredContentsAsync), false);
        // The prefix rule must carry an enclosed child exactly like the existing exact-name rule.
        var name = prefix ? "~$" + nameof(Entry) : nameof(Entry);
        var rule = (prefix ? nameof(SyncIgnoreKind.FileNamePrefix) + PathLocks.KeySeparator + "~$"
            : nameof(SyncIgnoreKind.FileName) + PathLocks.KeySeparator + nameof(Entry));
        var child = PathNames.Child(exclusion.Path, name);
        var context = Context(store, exclusion, [rule]);
        await context.For(side).EnsureFolderAsync(exclusion.Path, default);
        await PutAsync(context.For(side), child);
        var preview = await Runner(context).PreviewAsync(exclusion, default);
        Check.Require(!preview.BlockedByProtected && preview.CopyFiles == 0 && preview.RemoveFiles == 1, nameof(IgnoredContentsAsync));
        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await context.For(side).ReadOneAsync(exclusion.Path, default) is null &&
            await context.For(Other(side)).ReadOneAsync(exclusion.Path, default) is not null &&
            await context.For(Other(side)).ReadOneAsync(child, default) is null, nameof(IgnoredContentsAsync));
    }

    private static async Task ProtectedContentsAsync(string storageRoot, Side side, string rule, string childName)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(ProtectedContentsAsync), false);
        var context = Context(store, exclusion);
        var child = PathNames.Child(exclusion.Path, childName);
        var pair = context.Pair with
        {
            Exclusions = [exclusion, new Exclusion(Other(side), child, true)],
            IgnoreRules = rule.Length == 0 ? [] : [rule]
        };
        store.SavePair(pair);
        context = context with { Pair = pair, Scope = new PathScope(pair, [pair]) };
        var folder = await context.For(side).EnsureFolderAsync(exclusion.Path, default);
        var protectedEntry = await PutAsync(context.For(side), child);
        var preview = await Runner(context).PreviewAsync(exclusion, default);
        Check.Require(preview.BlockedByProtected, nameof(ExcludePreview.BlockedByProtected));
        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await context.For(side).ReadOneAsync(exclusion.Path, default) == folder &&
            await context.For(side).ReadOneAsync(child, default) == protectedEntry &&
            store.Issues(pair.PairId).Any(issue => issue.Cause == TextKey.FailExcludeHoldsProtected), nameof(ProtectedContentsAsync));
    }

    private static async Task NestedContentsAsync(Side side)
    {
        using var store = new InMemoryStateStore();
        var exclusion = new Exclusion(side, nameof(NestedContentsAsync), false);
        var context = Context(store, exclusion);
        var nested = PathNames.Child(exclusion.Path, nameof(PairConfig));
        var inner = context.Pair with
        {
            PairId = Guid.NewGuid().ToString(),
            LocalRoot = Path.Combine(context.Pair.LocalRoot, nested),
            DropboxRoot = PathNames.Child(context.Pair.DropboxRoot, nested),
            Exclusions = []
        };
        store.SavePair(inner);
        context = context with { Scope = new PathScope(context.Pair, [context.Pair, inner]) };
        var folder = await context.For(side).EnsureFolderAsync(exclusion.Path, default);
        var nestedFolder = await context.For(side).EnsureFolderAsync(nested, default);
        var child = await PutAsync(context.For(side), PathNames.Child(nested, nameof(Entry)));
        var preview = await Runner(context).PreviewAsync(exclusion, default);
        Check.Require(preview.BlockedByProtected && preview.CopyFiles == 0, nameof(NestedContentsAsync));
        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await context.For(side).ReadOneAsync(exclusion.Path, default) == folder &&
            await context.For(side).ReadOneAsync(nested, default) == nestedFolder &&
            await context.For(side).ReadOneAsync(child.Path, default) == child &&
            await context.For(Other(side)).ReadOneAsync(exclusion.Path, default) is null &&
            store.Issues(context.Pair.PairId).Any(issue => issue.Cause == TextKey.FailExcludeHoldsProtected), nameof(NestedContentsAsync));
    }

    private static async Task NestedRootRefusedWithoutListingAsync(Side side)
    {
        using var store = new InMemoryStateStore();
        var exclusion = new Exclusion(side, nameof(NestedRootRefusedWithoutListingAsync), false);
        var context = Context(store, exclusion);
        var nested = PathNames.Child(exclusion.Path, nameof(PairConfig));
        var inner = context.Pair with
        {
            PairId = Guid.NewGuid().ToString(),
            LocalRoot = Path.Combine(context.Pair.LocalRoot, nested),
            DropboxRoot = PathNames.Child(context.Pair.DropboxRoot, nested),
            Exclusions = []
        };
        store.SavePair(inner);
        context = context with { Scope = new PathScope(context.Pair, [context.Pair, inner]) };
        var source = (FakeSide)context.For(side);
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var noListing = new ProbeSide(source)
        {
            Before = (operation, _) =>
            {
                if (operation == nameof(ISide.ReadAllAsync))
                    throw new InvalidOperationException("The refusal must happen without a subtree listing.");
            }
        };
        context = side == Side.Local ? context with { Local = noListing } : context with { Dropbox = noListing };
        var preview = await Runner(context).PreviewAsync(exclusion, default);
        Check.Require(preview.BlockedByProtected && preview.CopyFiles == 0 && preview.RemoveFiles == 0,
            "A nested pair root refuses the exclusion in the preview without a subtree listing.");
        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await context.For(side).ReadOneAsync(exclusion.Path, default) == root &&
            store.Issues(context.Pair.PairId).Single().Cause == TextKey.FailExcludeHoldsProtected &&
            ((FakeAdmission)context.Admission).Admitted == 2 && ((FakeAdmission)context.Admission).Completed == 0,
            "A nested pair root refuses the exclusion without a subtree listing.");
    }

    private static async Task VanishedChildRemovesItsSubtreeAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(VanishedChildRemovesItsSubtreeAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.For(side);
        await source.EnsureFolderAsync(exclusion.Path, default);
        var child = PathNames.Child(exclusion.Path, nameof(Entry));
        await source.EnsureFolderAsync(child, default);
        await PutAsync(source, PathNames.Child(child, nameof(SyncedEntry)));
        var vanishing = new VanishingSide(source, child);
        context = side == Side.Local ? context with { Local = vanishing } : context with { Dropbox = vanishing };
        var preview = await Runner(context).PreviewAsync(exclusion, default);
        Check.Require(preview.CopyFiles == 0 && preview.RemoveFiles == 0,
            "A child listed as gone removes its accumulated descendant from the exclusion contents.");
        // The observed deletion is real before the folder owner lists the provider's contents.
        if (await source.ReadOneAsync(child, default) is { } vanished)
            await source.DeleteAsync(vanished, null, default);
        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await context.For(side).ReadOneAsync(exclusion.Path, default) is null &&
            await context.For(Other(side)).ReadOneAsync(exclusion.Path, default) is not null &&
            await context.For(Other(side)).ReadOneAsync(child, default) is null &&
            store.Issues(context.Pair.PairId).Count == 0,
            "A vanished child's subtree is neither copied nor retained.");
    }

    private static async Task KeepDoesNothingAsync(Side side)
    {
        using var store = new InMemoryStateStore();
        var exclusion = new Exclusion(side, nameof(KeepDoesNothingAsync), true);
        var context = Context(store, exclusion);
        var other = Other(side);
        var keys = LockKeys.ForPaths(context.Pair,
            [(side, exclusion.Path), (other, new Parents(context).Destination(other, exclusion.Path))]);
        Task<bool> apply;
        Task<ExcludePreview> preview;
        bool noLock;
        // A pre-held lock stalls any attempt to take it, so synchronous completion proves none was made.
        await using (await PathLocks.Shared.EnterAsync(keys, default))
        {
            var runner = Runner(context with { Local = new ThrowingSide(Side.Local), Dropbox = new ThrowingSide(Side.Dropbox) });
            apply = runner.ApplyAsync(exclusion, null, default);
            preview = runner.PreviewAsync(exclusion, default);
            noLock = apply.IsCompletedSuccessfully && preview.IsCompletedSuccessfully;
        }
        Check.Require(noLock, "A Keep exclusion takes no lock.");
        Check.Require(await apply, "Keep is an applied exclusion without provider work.");
        var result = await preview;
        Check.Require(result.Side == side && result.Path == exclusion.Path && !result.BlockedByProtected &&
            result.CopyFiles == 0 && result.RemoveFiles == 0 && result.CopyBytes == 0 && result.RemoveBytes == 0 &&
            result.RecoveryPlace == (side == Side.Local ? TextKey.RecoveryRecycleBin : TextKey.RecoveryDropboxHistory) &&
            store.Issues(context.Pair.PairId).Count == 0 && ((FakeAdmission)context.Admission).Admitted == 0,
            nameof(KeepDoesNothingAsync));
    }

    private static async Task SkippedContentsAsync(string storageRoot, Side side, TextKey skip, bool folder)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(SkippedContentsAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.For(side);
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var path = PathNames.Child(exclusion.Path, nameof(Entry));
        var entry = folder ? await source.EnsureFolderAsync(path, default) : await PutAsync(source, path);
        var descendant = folder ? await PutAsync(source, PathNames.Child(path, nameof(SyncedEntry))) : null;
        var skipped = entry with { Skip = skip.ToString() };
        source.Seed(skipped, null);
        var preview = await Runner(context).PreviewAsync(exclusion, default);
        Check.Require(preview.BlockedByProtected && ((FakeAdmission)context.Admission).Admitted == 1 &&
            store.Issues(context.Pair.PairId).Count == 0, nameof(SkippedContentsAsync));

        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await source.ReadOneAsync(exclusion.Path, default) == root &&
            await source.ReadOneAsync(path, default) == skipped &&
            await context.For(Other(side)).ReadOneAsync(exclusion.Path, default) is null,
            nameof(SkippedContentsAsync));
        if (descendant is not null)
            Check.Require(await source.ReadOneAsync(descendant.Path, default) == descendant, nameof(descendant));
        var issue = store.Issues(context.Pair.PairId).Single();
        Check.Require(issue.Kind == IssueKind.Failure && issue.Cause == TextKey.FailExcludeHoldsProtected &&
            issue.Path == exclusion.Path && ((FakeAdmission)context.Admission).Admitted == 2 &&
            ((FakeAdmission)context.Admission).Completed == 0 &&
            store.SyncedByKey(context.Pair.PairId, side, Key(side, exclusion.Path)) is null,
            nameof(TextKey.FailExcludeHoldsProtected));
        RequireForgotten(context, exclusion.Path, path, PathNames.Child(path, nameof(SyncedEntry)));
    }

    private static async Task FailureKeepsSourceAsync(string storageRoot, Side side, string operation, bool changed)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        var exclusion = new Exclusion(side, nameof(FailureKeepsSourceAsync), false);
        var context = Context(store, exclusion);
        var source = (FakeSide)context.For(side);
        var target = (FakeSide)context.For(Other(side));
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var path = PathNames.Child(exclusion.Path, nameof(Entry));
        var entry = await PutAsync(source, path);
        var rootFailure = operation is nameof(ISide.ReadOneAsync) or nameof(ISide.DeleteAsync);
        var failedPath = rootFailure ? exclusion.Path : path;
        var cause = changed ? TextKey.FailChanged : TextKey.PublishFailed;
        Exception error = changed ? new ChangedUnderneathException(failedPath)
            : new ItemFailedException(failedPath, cause, null);
        var effects = new List<RunEvent>();
        context = context with
        {
            Events = effect =>
            {
                effects.Add(effect);
                // Arm only after the last copy: the next root operation must be the removal.
                if (operation == nameof(ISide.DeleteAsync) && effect.Kind == RunEventKind.Effect && effect.Path == path)
                    source.FailNext(exclusion.Path, error);
            }
        };
        if (operation != nameof(ISide.DeleteAsync))
            (operation == nameof(ISide.StageAsync) ? target : source).FailNext(failedPath, error);

        await Runner(context).ApplyAsync(exclusion, null, default);
        Check.Require(await source.ReadOneAsync(exclusion.Path, default) == root &&
            await source.ReadOneAsync(path, default) == entry, nameof(FailureKeepsSourceAsync));
        var issue = store.Issues(context.Pair.PairId).Single();
        Check.Require(issue.Kind == IssueKind.Failure, nameof(Issue.Kind));
        Check.Require(issue.Cause == cause, nameof(Issue.Cause));
        Check.Require(issue.Path == failedPath, nameof(Issue.Path));
        Check.Require(effects.All(effect => Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What != EffectKind.DeletedLocal.ToString() &&
            Massdex.SyncShark.Engine.Run.RunLog.Present(effect).What != EffectKind.DeletedDropbox.ToString()), nameof(RunEventKind.Effect));
        var copiedRoot = await target.ReadOneAsync(exclusion.Path, default);
        var copiedChild = await target.ReadOneAsync(path, default);
        Check.Require((copiedRoot is not null) == (operation != nameof(ISide.ReadOneAsync)) &&
            (copiedChild is not null) == (operation == nameof(ISide.DeleteAsync)), nameof(ISide.ReadOneAsync));
        Check.Require((store.SyncedByKey(context.Pair.PairId, side, Key(side, exclusion.Path)) is not null) ==
            (copiedRoot is not null), nameof(IStateStore.SyncedByKey));
        Check.Require((store.SyncedByKey(context.Pair.PairId, side, Key(side, path)) is not null) ==
            (copiedChild is not null), nameof(IStateStore.SyncedByKey));
        // Root reads complete no actions. A child upload staging failure follows the root copy; each action
        // reports its own completion. A removal failure follows both copies and the folder's own admission.
        var admission = (FakeAdmission)context.Admission;
        Check.Require(admission.Admitted ==
                (operation == nameof(ISide.DeleteAsync) ? 2 : 1) &&
            admission.Completed == (operation == nameof(ISide.ReadOneAsync) ? 0 :
                operation == nameof(ISide.DeleteAsync) || operation == nameof(ISide.StageAsync) && side == Side.Local ? 2 : 1),
            nameof(IAdmission.ActionCompleted) + " admitted=" + admission.Admitted +
            " completed=" + admission.Completed);

        await Runner(context with { Events = effects.Add }).ApplyAsync(exclusion, null, default);
        Check.Require(await source.ReadOneAsync(exclusion.Path, default) is null &&
            store.Issues(context.Pair.PairId).Count == 0 &&
            store.SyncedByKey(context.Pair.PairId, side, Key(side, exclusion.Path)) is null,
            nameof(ExclusionRunner.ApplyAsync));
        var finalRoot = await target.ReadOneAsync(exclusion.Path, default);
        var finalChild = await target.ReadOneAsync(path, default);
        Check.Require(finalRoot is not null && finalChild is not null &&
            (copiedRoot is null || finalRoot == copiedRoot) && (copiedChild is null || finalChild == copiedChild),
            nameof(FailureKeepsSourceAsync));
        RequireForgotten(context, exclusion.Path, path);
    }

    private static async Task ListingFailureKeepsSourceAsync(string storageRoot, Side side)
    {
        foreach (var preview in new[] { false, true })
        {
            using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
            var exclusion = new Exclusion(side, nameof(ListingFailureKeepsSourceAsync), false);
            var context = Context(store, exclusion);
            var source = (FakeSide)context.For(side);
            var target = context.For(Other(side));
            var root = await source.EnsureFolderAsync(exclusion.Path, default);
            var child = await PutAsync(source, PathNames.Child(exclusion.Path, nameof(Entry)));
            var bytes = await ReadBytesAsync(source, child);
            var error = new InventoryIncompleteException(Text.Get(
                side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed, (TextArg.Path, root.Path)), null);
            IEnumerable<Listed> FailedListing()
            {
                yield return new(child.Path, child);
                throw error;
            }
            var failing = new ProbeSide(source) { Listing = FailedListing() };
            context = side == Side.Local ? context with { Local = failing } : context with { Dropbox = failing };
            var effects = new List<RunEvent>();
            context = context with { Events = effects.Add };
            var propagated = false;
            try
            {
                if (preview) await Runner(context).PreviewAsync(exclusion, default);
                else await Runner(context).ApplyAsync(exclusion, null, default);
            }
            catch (ItemFailedException caught) when (ReferenceEquals(caught.InnerException, error))
            {
                propagated = caught.Path == root.Path &&
                    caught.Cause == (side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed);
            }
            Check.Require(propagated == preview, "Only the read-only preview propagates the mapped item failure.");
            if (!preview)
            {
                var issue = store.Issues(context.Pair.PairId).Single();
                Check.Require(issue.Kind == IssueKind.Failure && issue.Path == root.Path &&
                    issue.Cause == (side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed) &&
                    Check.IsSettlementStream(effects, issue),
                    "Apply records the incomplete subtree listing as a per-item Failure.");
            }
            Check.Require(await source.ReadOneAsync(root.Path, default) == root &&
                await source.ReadOneAsync(child.Path, default) == child &&
                (await ReadBytesAsync(source, child)).SequenceEqual(bytes),
                "A failed exclusion listing removes nothing and preserves the child's bytes.");
            Check.Require(await target.ReadOneAsync(root.Path, default) is null &&
                await target.ReadOneAsync(child.Path, default) is null &&
                (preview ? effects.Count == 0 :
                    Check.IsSettlementStream(effects, store.Issues(context.Pair.PairId).Single())) &&
                store.Issues(context.Pair.PairId).Count == (preview ? 0 : 1) &&
                store.SyncedByKey(context.Pair.PairId, side, Key(side, root.Path)) is null &&
                ((FakeAdmission)context.Admission).Admitted == 1 && ((FakeAdmission)context.Admission).Completed == 0,
                "A failed exclusion listing performs no copy, removal or saved-record mutation.");
            RequireForgotten(context, root.Path, child.Path);
        }
    }

    private static async Task CancellationKeepsSourceAsync(string storageRoot, Side side)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, Path.GetRandomFileName()));
        using var cancellation = new CancellationTokenSource();
        var exclusion = new Exclusion(side, nameof(CancellationKeepsSourceAsync), false);
        var context = Context(store, exclusion);
        var source = context.For(side);
        var target = context.For(Other(side));
        var root = await source.EnsureFolderAsync(exclusion.Path, default);
        var path = PathNames.Child(exclusion.Path, nameof(Entry));
        var entry = await PutAsync(source, path);
        context = context with { Events = effect =>
        {
            if (effect.Kind == RunEventKind.Effect) cancellation.Cancel();
        } };
        var cancelled = false;
        try
        {
            await Runner(context).ApplyAsync(exclusion, null, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Check.Require(cancelled && await source.ReadOneAsync(exclusion.Path, default) == root &&
            await source.ReadOneAsync(path, default) == entry && await target.ReadOneAsync(path, default) is null &&
            store.Issues(context.Pair.PairId).Count == 0, nameof(CancellationKeepsSourceAsync));
        Check.Require(await target.ReadOneAsync(exclusion.Path, default) is not null &&
            store.SyncedByKey(context.Pair.PairId, side, Key(side, exclusion.Path)) is not null &&
            ((FakeAdmission)context.Admission).Admitted == 1 && ((FakeAdmission)context.Admission).Completed == 1,
            nameof(IAdmission.ActionCompleted));
        await Runner(context with { Events = _ => { } }).ApplyAsync(exclusion, null, default);
        Check.Require(await source.ReadOneAsync(exclusion.Path, default) is null &&
            await target.ReadOneAsync(path, default) is not null, nameof(ExclusionRunner.ApplyAsync));
    }

    // Measured evidence for the migrated per-item sites: each pair runs the owner call and the inline
    // expression it replaced over the same loop; NoInlining sinks keep both results alive.
    private static void CostProbe()
    {
        const string path = "Excluded/Child";
        var key = PathNames.Key(path);
        var candidate = PathNames.Key(path + "/Grand");
        var item = new Entry(path, false, 1, DateTimeOffset.UnixEpoch, "id", "", "", null);
        var folder = item with { IsFolder = true };
        var rowContext = new RowContext(false, false, false, null, false, false, false, false);
        var row = Row.ForOneSide(Side.Local, key, item, null, [], null, rowContext);
        var saved = SyncedEntries.From(item, item with { Id = "remote-id" });
        var upload = new SyncAction(ActionKind.Upload, row);
        var outcome = new SyncOutcome(ActionKind.Upload, null, null, IssueKind.Failure, false);
        using var store = new InMemoryStateStore();
        var parents = new Parents(Context(store, new Exclusion(Side.Local, nameof(CostProbe), false)));

        var under = Compare("PathNames.IsSelfOrUnderKey",
            index => SinkBool(BaselineSelfOrUnder(candidate, key)),
            index => SinkBool(PathNames.IsSelfOrUnderKey(candidate, key)));
        Check.Require(under.Migrated <= under.Baseline, "IsSelfOrUnderKey removes the prefix string: " + under.Migrated + " B");

        var guard = Compare("Exclusions.For saved skip",
            index => SinkBool(BaselineGuard(upload, Side.Dropbox)),
            index => SinkBool(Exclusions.For(Side.Dropbox, item, row, upload) is null));
        Check.Require(guard.Migrated <= guard.Baseline, "Exclusions.For skip adds no allocation: " + guard.Migrated + " B");

        var unsaved = Compare("Exclusions.For unsaved action",
            index => SinkBool(BaselineUnsaved(item, row, Side.Local)),
            index => SinkBool(Exclusions.For(Side.Local, item, row, null) is not null));
        Check.Require(unsaved.Migrated <= unsaved.Baseline, "Exclusions.For builds one action: " + unsaved.Migrated + " B");

        var blocked = Compare("Exclusions.Blocked",
            index => SinkBool(BaselineBlocked(row)), index => SinkBool(Exclusions.Blocked(row) is not null));
        Check.Require(blocked.Migrated <= blocked.Baseline, "Exclusions.Blocked builds one action: " + blocked.Migrated + " B");

        var mayCopy = Compare("Exclusions.MayCopyFile",
            index => SinkBool(BaselineMayCopy(item, saved, Side.Local)),
            index => SinkBool(Exclusions.MayCopyFile(item, saved, Side.Local)));
        Check.Require(mayCopy.Migrated <= mayCopy.Baseline, "MayCopyFile adds no allocation: " + mayCopy.Migrated + " B");

        var shouldList = Compare("Exclusions.ShouldList",
            index => SinkBool(BaselineShouldList(folder)), index => SinkBool(Exclusions.ShouldList(folder)));
        Check.Require(shouldList.Migrated <= shouldList.Baseline, "ShouldList adds no allocation: " + shouldList.Migrated + " B");

        var oneSide = Compare("Row.ForOneSide",
            index => SinkBool(BaselineRow(side: Side.Local, key, item, rowContext)),
            index => SinkBool(Row.ForOneSide(Side.Local, key, item, null, [], null, rowContext) is not null));
        Check.Require(oneSide.Migrated <= oneSide.Baseline, "ForOneSide builds one row: " + oneSide.Migrated + " B");

        var readFailed = Compare("Text.ReadFailedOn",
            index => SinkInt((int)BaselineReadFailed(Side.Dropbox)),
            index => SinkInt((int)Text.ReadFailedOn(Side.Dropbox)));
        Check.Require(readFailed.Migrated <= readFailed.Baseline, "ReadFailedOn adds no allocation");

        var raised = Compare("SyncOutcome.RaisedBlocks",
            index => SinkBool(BaselineRaised(outcome)), index => SinkBool(outcome.RaisedBlocks()));
        Check.Require(raised.Migrated <= raised.Baseline, "RaisedBlocks adds no allocation");

        var folderRow = Row.ForOneSide(Side.Local, key, folder, null, [], null, rowContext);
        var ensure = new SyncAction(ActionKind.EnsureDropboxFolder, folderRow);
        var toSync = Compare("Resolutions.FolderToSync",
            index => SinkBool(BaselineFolder(ensure, null) is not null),
            index => SinkBool(Resolutions.FolderToSync(ensure, null) is not null));
        Check.Require(toSync.Migrated <= toSync.Baseline, "FolderToSync adds no allocation");

        var comparer = Compare("PathLocks.KeyComparer",
            index => SinkObject(StringComparer.Ordinal), index => SinkObject(PathLocks.KeyComparer));
        Check.Require(comparer.Migrated <= comparer.Baseline, "KeyComparer is the one cached ordinal comparer");

        var afterEffect = Compare("IssueClosure.AfterEffect",
            index => SinkInt(BaselineAfterEffect().Length), index => SinkInt(IssueClosure.AfterEffect.Count));
        Check.Require(afterEffect.Migrated <= afterEffect.Baseline, "AfterEffect reuses the cached set: " + afterEffect.Migrated + " B");

        var counterpart = Compare("Parents.Counterpart saved branch",
            index => SinkString(saved.PathOn(Side.Dropbox)),
            index => SinkString(parents.Counterpart(Side.Dropbox, key, saved)));
        Check.Require(counterpart.Migrated <= counterpart.Baseline && counterpart.Migrated == 0,
            "the saved branch does no destination lookup: " + counterpart.Migrated + " B");
    }

    private static (long Baseline, long Migrated) Compare(string label, Action<int> baseline, Action<int> migrated)
    {
        var baselineBytes = AllocProbe.Measure(baseline, 10_000);
        var migratedBytes = AllocProbe.Measure(migrated, 10_000);
        Console.WriteLine("  " + label + ": baseline=" + baselineBytes + " B, migrated=" + migratedBytes +
            " B per 10000 calls");
        return (baselineBytes, migratedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkBool(bool value) { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkString(string? value) { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkInt(int value) { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkObject(object? value) { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineSelfOrUnder(string candidate, string prefix) =>
        candidate == prefix || candidate.StartsWith(prefix + PathNames.PathSeparator, StringComparison.Ordinal);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineGuard(SyncAction action, Side side) =>
        action.Kind is ActionKind.Upload or ActionKind.Download or ActionKind.DeleteLocal or ActionKind.DeleteDropbox
        && action.WrittenSide() == side;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineUnsaved(Entry item, Row row, Side side)
    {
        var kind = item.IsFolder
            ? side == Side.Local ? ActionKind.EnsureDropboxFolder : ActionKind.EnsureLocalFolder
            : side == Side.Local ? ActionKind.Upload : ActionKind.Download;
        return new SyncAction(kind, row, Target: row.Context.Destination) is not null;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineBlocked(Row row) =>
        new SyncAction(ActionKind.Notice, row, NoticeKind: IssueKind.Failure, Cause: TextKey.FailExcludeHoldsProtected) is not null;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineMayCopy(Entry item, SyncedEntry? saved, Side side) =>
        saved is null || saved.IsFolder || (side == Side.Dropbox
            ? Decide.DropboxChanged(item, saved) : Decide.LocalChanged(item, saved, null, localContentDirty: false) != false);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineShouldList(Entry root) => root.IsFolder && root.Skip is null;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineRow(Side side, string key, Entry? entry, RowContext context) =>
        new Row(key, side == Side.Local ? entry : null,
            side == Side.Dropbox ? entry : null, null, [], null, context) is not null;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TextKey BaselineReadFailed(Side side) =>
        side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BaselineRaised(SyncOutcome outcome) =>
        outcome.Raised is IssueKind.Failure or IssueKind.Tie or IssueKind.NameClash;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Side Side, Entry Entry)? BaselineFolder(SyncAction action, (Side Side, Entry Entry)? previous) =>
        action.Kind switch
        {
            ActionKind.EnsureDropboxFolder => Source(Side.Local, action.Row.Local),
            ActionKind.EnsureLocalFolder => Source(Side.Dropbox, action.Row.Dropbox),
            ActionKind.ReplaceKind when action.Side == Side.Dropbox => Source(Side.Local, action.Row.Local),
            ActionKind.ReplaceKind => Source(Side.Dropbox, action.Row.Dropbox),
            _ => previous
        };
    private static (Side Side, Entry Entry)? Source(Side side, Entry? entry) =>
        entry is { IsFolder: true } ? (side, entry) : null;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IssueKind[] BaselineAfterEffect() => [IssueKind.Failure];

    private static async Task<byte[]> ReadBytesAsync(ISide side, Entry entry)
    {
        await using var content = await side.OpenReadAsync(entry, null, default);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static void RequireForgotten(ExecutorContext context, params string[] paths)
    {
        foreach (var path in paths)
            foreach (var side in Enum.GetValues<Side>())
                Check.Require(context.Store.SyncedByKey(context.Pair.PairId, side, Key(side, path)) is null,
                    "Every known binding is forgotten on both sides: " + path);
    }

    private static async Task UnknownSavedHashAsync(string storageRoot)
    {
        using var store = StateStore.Open(Path.Combine(storageRoot, "unknown-exclusion.db"));
        var exclusion = new Exclusion(Side.Local, "unknown", false);
        var context = Context(store, exclusion);
        var local = await PutAsync(context.Local, exclusion.Path);
        var remote = await PutAsync(context.Dropbox, exclusion.Path);
        var saved = SyncedEntries.From(local, remote) with { DropboxContentHash = "" };
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var probe = new ProbeSide(context.Local);
        var events = new List<RunEvent>();
        ((FakeRecovery)context.Recovery).Hash = (_, _) => throw new InvalidOperationException("Unknown exclusion proof must not adopt current bytes.");
        var applied = await Runner(context with { Local = probe, Events = events.Add }).ApplyAsync(exclusion, null, default);
        Check.Require(!applied && await context.Local.ReadOneAsync(local.Path, default) == local &&
            !probe.Calls.Any(call => call.Operation == nameof(ISide.DeleteAsync)) &&
            store.Issues(context.Pair.PairId).Single() is { Kind: IssueKind.Failure, Cause: TextKey.FailChanged } issue &&
            Check.IsSettlementStream(events, issue),
            "AJ-01: exclusion unknown saved hash keeps the file, raises FailChanged and makes zero proof reads or removal calls, and one Issue followed by its cause-worded Wait.");
    }

    private static ExecutorContext Context(IStateStore store, Exclusion exclusion, IReadOnlyList<string>? ignores = null)
    {
        var id = Guid.NewGuid().ToString();
        var pair = SourceFixture.Bound(new PairConfig(id, nameof(ExclusionRunnerChecks),
            Path.Combine(Path.GetTempPath(), id), PathNames.PathSeparator + id, id, ignores ?? [], [exclusion],
            FirstRunPolicy.NewestWins, true, null));
        store.SavePair(pair);
        SourceFixture.AcquireJournal(store, pair);
        var attempts = new DownloadAttempts();
        return new ExecutorContext(new FakeSide(Side.Local), new FakeSide(Side.Dropbox),
            new FakeRecovery { Evidence = new DriveEvidence(1, null, null) }, store,
            pair, new PathScope(pair, [pair]), new EngineSettings("", "", "", "", "", default, default,
                FirstRunPolicy.NewestWins, default, "", 262144, ScanTestSettings.BatchRows) {
        JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes,
        LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes, StoragePageSize = PageSize },
            new ReplaceTurn(), new FakeAdmission(), false, _ => { },
            (_, _, _, _) => ValueTask.FromResult<IAsyncDisposable>(new SpaceLease()), new FakeTimeProvider(),
            RefuseDeletionEvidence, attempts.Begin, attempts.Complete,
            ("account", "route", "source"), new ItemAttemptsPort(), new object(),
            store.ReadPairExecution(pair.PairId)!.Value.Generation)
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

    private static int ShippedPageSize()
    {
        using var stream = typeof(EngineSettings).Assembly.GetManifestResourceStream(ResourceNames.Settings)!;
        using var reader = new StreamReader(stream);
        return SettingsParser.Parse(reader.ReadToEnd(), null).StoragePageSize;
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

    private static ExclusionRunner Runner(ExecutorContext context)
    {
        var transfers = new Transfers(context);
        var parents = new Parents(context);
        var keepBoth = new KeepBothRunner(context, transfers);
        var refusing = RefusingExecutionBoundary.Instance;
        var jobs = SyncJobs.Create(context, new SyncPorts(PassthroughRecoveryBoundary.Instance, refusing, refusing, 0));
        return new(context, parents, new ActionExecutor(context, transfers, parents, keepBoth),
            keepBoth, jobs.Items);
    }

    private static Task<DeletionEvidence> RefuseDeletionEvidence(string path, bool folder, CancellationToken ct) =>
        throw new InvalidOperationException("The exclusion path requests no deletion evidence.");

    private sealed class DownloadAttempts
    {
        // Every download in these fixtures is a complete response; source-retry selection is TransfersChecks' case.
        // Refusing the retry keeps an injected source read failure a failure at the exclusion boundary.
        public RequestAttempt Begin(object operation) => new(operation, AttemptOwner.Transfer);
        public Task<bool> Complete(RequestAttempt attempt, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class SpaceLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => default;
    }

    private sealed class ItemAttemptsPort : IItemAttempts
    {
        // The exclusion unit owns no stored retry lifecycle: the action callback runs once under a fresh attempt id
        // and no candidate is fabricated. The two lifecycle members are unused on this path and refuse loudly.
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => execute(Guid.NewGuid(), ct);
        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null, Action<Issue>? committedIssue = null) => throw Refused();
        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null) => throw Refused();
        private static InvalidOperationException Refused() => new("The exclusion path owns no item attempt lifecycle.");
    }

    private sealed class CountingClock(DateTimeOffset now) : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return now;
        }
    }

    private static async Task<Entry> PutAsync(ISide side, string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(nameof(ExclusionRunnerChecks));
        var source = new Entry(path, false, bytes.LongLength, DateTimeOffset.UnixEpoch, "", "", "", null);
        await using var stream = new MemoryStream(bytes);
        await using var staged = await side.StageAsync(path, stream, source, default);
        return (await side.PublishAsync(staged, null, null, default)).Final!;
    }

    private sealed class ProbeSide(ISide inner) : ISide
    {
        public Side Side => inner.Side;
        public List<(string Operation, string Path)> Calls { get; } = [];
        public Action<string, string>? Before { get; set; }
        public RemovalResult? Removal { get; set; }
        public Func<Entry, RemovalSelection?, CancellationToken, Task<RemovalResult>>? Remove { get; init; }
        public IEnumerable<Listed>? Listing { get; init; }
        // A synthetic listing models the exclusion's own capture; the folder owner then lists the real contents.
        public int ListingUses { get; init; } = int.MaxValue;
        private int _listings;
        public bool ListingDisposed { get; private set; }
        public int ParentListings { get; private set; }

        private void Record(string operation, string path)
        {
            Calls.Add((operation, path));
            Before?.Invoke(operation, path);
        }

        public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Record(nameof(ReadAllAsync), under);
            if (!options.Recursive) ParentListings++;
            ListingDisposed = false;
            try
            {
                if (Listing is { } listing && _listings++ < ListingUses)
                {
                    foreach (var item in listing)
                    {
                        ct.ThrowIfCancellationRequested();
                        yield return item;
                    }
                }
                else
                    await foreach (var item in inner.ReadAllAsync(under, options, ct)) yield return item;
            }
            finally { ListingDisposed = true; }
        }

        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
        {
            Record(nameof(ReadOneAsync), path);
            return inner.ReadOneAsync(path, ct);
        }

        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct)
        {
            Record(nameof(OpenReadAsync), entry.Path);
            return inner.OpenReadAsync(entry, attempt, ct);
        }

        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
        {
            Record(nameof(ContentHashAsync), entry.Path);
            return inner.ContentHashAsync(entry, ct);
        }

        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
        {
            Record(nameof(StageAsync), path);
            return inner.StageAsync(path, content, source, ct);
        }

        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct)
        {
            Record(nameof(PublishAsync), staged.Path);
            return inner.PublishAsync(staged, expected, selection, ct);
        }

        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
        {
            Record(nameof(EnsureFolderAsync), path);
            return inner.EnsureFolderAsync(path, ct);
        }

        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
        {
            Record(nameof(DeleteAsync), expected.Path);
            return Remove is { } remove ? remove(expected, selection, ct) :
                Removal is { } result ? Task.FromResult(result) : inner.DeleteAsync(expected, selection, ct);
        }

        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
        {
            Record(nameof(MoveAsync), expected.Path);
            return inner.MoveAsync(expected, newPath, ct);
        }

        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
        {
            Record(nameof(SubmitFolderRemovalAsync), submission.SubmissionId);
            return inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        }

        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount, RequestAttempt attempt,
            CancellationToken ct)
        {
            Record(nameof(ReadFolderRemovalAsync), jobId);
            return inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
        }
    }

    private sealed class VanishingSide(FakeSide inner, string vanished) : ISide
    {
        public Side Side => inner.Side;
        private int _listings;

        public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var listed in inner.ReadAllAsync(under, options, ct)) yield return listed;
            // Simulates a deletion observed after the child's descendants were listed. The exclusion's preview and
            // apply captures consume it; the folder owner then lists the provider's already-shrunk contents.
            if (_listings++ < 2) yield return new Listed(vanished, null);
        }

        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => inner.ReadOneAsync(path, ct);
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
        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
            inner.MoveAsync(expected, newPath, ct);
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount, RequestAttempt attempt,
            CancellationToken ct) => inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }

    private sealed class ThrowingSide(Side side) : ISide
    {
        public Side Side => side;

        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) => throw Unexpected();
        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => throw Unexpected();
        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) => throw Unexpected();
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => throw Unexpected();
        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) => throw Unexpected();
        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct) => throw Unexpected();
        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => throw Unexpected();
        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) =>
            throw Unexpected();
        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) => throw Unexpected();
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) => throw Unexpected();
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount, RequestAttempt attempt,
            CancellationToken ct) => throw Unexpected();

        private static InvalidOperationException Unexpected() => new("A Keep exclusion must make no provider call.");
    }

    private static Side Other(Side side) => side == Side.Local ? Side.Dropbox : Side.Local;
    private static string Key(Side side, string path) => side == Side.Local ? PathNames.Key(path) : PathNames.DropboxKey(path);
}
