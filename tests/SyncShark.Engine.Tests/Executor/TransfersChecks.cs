using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for Transfers (`--engine-checks syncer`), written by Transfers's writer.</summary>
public static class TransfersChecks
{
    private const int LogBufferCharacters = 262144;
    private const string RemovalHash = nameof(RemovalHash);
    private const string VolumeId = nameof(VolumeId);
    private const string Account = nameof(Account);
    private const string Route = nameof(Route);
    private const string SourceIdentity = nameof(SourceIdentity);
    private static readonly string LocalRoot = Path.GetFullPath(nameof(TransfersChecks));

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        foreach (var upload in new[] { true, false })
        foreach (var replacing in new[] { false, true })
            await CopyAsync(upload, replacing);
        foreach (var upload in new[] { true, false })
        foreach (var occupied in new[] { true, false })
            await RejectedPublishAsync(upload, occupied);
        await CommitFailureAsync();
        await InjectedFailuresAsync();
        await WaitingReplaceAsync();
        await RemovalTurnAsync();
        await PendingSlotAsync();
        await LatePendingSlotAsync();
        await NoSpaceAsync();
        await SourceNoSpaceAsync();
        await ConcurrentNoSpaceAsync();
        await DownloadAttemptsAsync();
        await ReservationLifetimeAsync();
        await StagedLifetimeAsync();
        await OutcomeGatingAsync();
        await FolderReplacementAsync();
        await SelectionForwardingAsync();
        await TimePreservationAsync();
        await CancellationAsync();
    }

    private static async Task CopyAsync(bool upload, bool replacing)
    {
        using var store = new InMemoryStateStore();
        var (context, _, _, sourceInner, targetInner) = ObservedContext(store, upload, new Volume());
        var bytes = new[] { byte.MinValue, byte.MaxValue };
        var sourceEntry = await SeedAsync(sourceInner, nameof(SeedAsync), bytes);
        var expected = replacing ? await SeedAsync(targetInner, nameof(CopyAsync), [byte.MaxValue]) : null;
        var targetPath = expected?.Path ?? nameof(CopyAsync);
        RemovalResult? published = null;
        var commitCount = default(int);
        var transfers = new Transfers(context);
        // Uploads and creates must finish even while another replacement owns the turn.
        await using var held = upload || !replacing ? await context.Runtime.EnterAsync(CancellationToken.None) : null;
        using var cancellation = new CancellationTokenSource();
        Entry result;
        if (upload)
        {
            result = await transfers.UpAsync(sourceEntry, targetPath, expected, cancellation.Token);
        }
        else
        {
            var outcome = await transfers.DownAsync(sourceEntry, targetPath, expected,
                expected is null ? null : Selection(expected), commit =>
                {
                    published = commit;
                    commitCount++;
                    if (replacing) RequireHeldTurn(context.Runtime);
                    // The successful publish must still commit when cancellation arrives before the callback returns.
                    cancellation.Cancel();
                }, cancellation.Token);
            Check.Require(outcome.Outcome == RemovalOutcome.Published && outcome.Final is not null, nameof(CopyAsync));
            result = outcome.Final!;
        }

        Check.Require(result == await targetInner.ReadOneAsync(targetPath, CancellationToken.None), nameof(CopyAsync));
        Check.Require(result.Path == targetPath && result.Modified == sourceEntry.Modified, nameof(CopyAsync));
        Check.Require(await BytesAsync(targetInner, result, bytes), nameof(CopyAsync));
        Check.Require(await BytesAsync(sourceInner, sourceEntry, bytes), nameof(CopyAsync));
        Check.Require(upload ? commitCount == 0 : commitCount == 1 && ReferenceEquals(published!.Final, result),
            nameof(CopyAsync));
        if (!upload && replacing) await RequireFreeTurnAsync(context.Runtime);
        RequireNoAdmission(context);
    }

    private static async Task RejectedPublishAsync(bool upload, bool occupied)
    {
        using var store = new InMemoryStateStore();
        var (context, _, _, sourceInner, targetInner) = ObservedContext(store, upload, new Volume());
        var sourceEntry = await SeedAsync(sourceInner, nameof(SeedAsync), [byte.MinValue]);
        var oldBytes = new[] { byte.MaxValue };
        var existing = await SeedAsync(targetInner, nameof(RejectedPublishAsync), oldBytes);
        var stale = existing with { Id = nameof(TransfersChecks), Revision = nameof(TransfersChecks), Size = existing.Size + 1 };
        var transfers = new Transfers(context);
        var committed = false;
        var rejected = false;
        try
        {
            if (upload)
                await transfers.UpAsync(sourceEntry, existing.Path, occupied ? null : stale, CancellationToken.None);
            else
                await transfers.DownAsync(sourceEntry, existing.Path, occupied ? null : stale,
                    occupied ? null : Selection(stale), _ => committed = true, CancellationToken.None);
        }
        catch (PathOccupiedException) when (occupied) { rejected = true; }
        catch (ChangedUnderneathException) when (!occupied) { rejected = true; }

        Check.Require(rejected && !committed, nameof(RejectedPublishAsync));
        Check.Require(existing == await targetInner.ReadOneAsync(existing.Path, CancellationToken.None), nameof(RejectedPublishAsync));
        Check.Require(await BytesAsync(targetInner, existing, oldBytes), nameof(RejectedPublishAsync));
        await RequireFreeTurnAsync(context.Runtime);
        RequireNoAdmission(context);
    }

    private static async Task CommitFailureAsync()
    {
        using var store = new InMemoryStateStore();
        var (context, _, _, sourceInner, targetInner) = ObservedContext(store, upload: false, new Volume());
        var bytes = new[] { byte.MinValue };
        var sourceEntry = await SeedAsync(sourceInner, nameof(SeedAsync), bytes);
        var expected = await SeedAsync(targetInner, nameof(CommitFailureAsync), [byte.MaxValue]);
        var failure = new InvalidOperationException();
        var propagated = false;
        try
        {
            await new Transfers(context).DownAsync(sourceEntry, expected.Path, expected, Selection(expected), _ =>
            {
                RequireHeldTurn(context.Runtime);
                throw failure;
            }, CancellationToken.None);
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { propagated = true; }

        Check.Require(propagated, nameof(CommitFailureAsync));
        var published = await targetInner.ReadOneAsync(expected.Path, CancellationToken.None);
        Check.Require(published is not null && await BytesAsync(targetInner, published, bytes), nameof(CommitFailureAsync));
        await RequireFreeTurnAsync(context.Runtime);
        RequireNoAdmission(context);
    }

    private static async Task InjectedFailuresAsync()
    {
        foreach (var upload in new[] { false, true })
        foreach (var replacing in new[] { false, true })
        foreach (var failOnRead in new[] { false, true })
        foreach (var error in new Exception[]
        {
            new ItemFailedException(nameof(InjectedFailuresAsync), TextKey.FailEarlierReplace, null),
            new ChangedUnderneathException(nameof(InjectedFailuresAsync)),
            new OperationCanceledException(), new InvalidOperationException()
        })
        {
            using var store = new InMemoryStateStore();
            var (context, _, _, sourceInner, targetInner) = ObservedContext(store, upload, new Volume());
            var bytes = new[] { byte.MinValue };
            var oldBytes = new[] { byte.MaxValue };
            var sourceEntry = await SeedAsync(sourceInner, nameof(SeedAsync), bytes);
            var targetPath = nameof(InjectedFailuresAsync);
            var expected = replacing ? await SeedAsync(targetInner, targetPath, oldBytes) : null;
            (failOnRead ? sourceInner : targetInner).FailNext(failOnRead ? sourceEntry.Path : targetPath, error);
            var committed = false;
            var propagated = false;
            // A failed read or stage must finish without waiting for another replacement's turn.
            await using (var held = await context.Runtime.EnterAsync(CancellationToken.None))
            {
                using var cancellation = new CancellationTokenSource();
                Task operation = upload
                    ? new Transfers(context).UpAsync(sourceEntry, targetPath, expected, cancellation.Token)
                    : new Transfers(context).DownAsync(sourceEntry, targetPath, expected,
                        expected is null ? null : Selection(expected), _ => committed = true, cancellation.Token);
                var finishedBeforeTurn = operation.IsCompleted;
                cancellation.Cancel();
                try { await operation; }
                catch (Exception actual) when (ReferenceEquals(actual, error)) { propagated = true; }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                Check.Require(finishedBeforeTurn && propagated && !committed,
                    "Read and stage failures propagate before entering the replace turn and never commit.");
                RequireHeldTurn(context.Runtime);
            }
            Check.Require(await targetInner.ReadOneAsync(targetPath, CancellationToken.None) == expected &&
                (expected is null || await BytesAsync(targetInner, expected, oldBytes)) &&
                await BytesAsync(sourceInner, sourceEntry, bytes),
                "A failed read or stage preserves source content and the previous destination.");
            await RequireFreeTurnAsync(context.Runtime);
            RequireNoAdmission(context);
        }
    }

    private static async Task WaitingReplaceAsync()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var (context, _, target, sourceInner, targetInner) = ObservedContext(store, upload: false, new Volume());
            var sourceEntry = await SeedAsync(sourceInner, nameof(SeedAsync), [byte.MinValue]);
            var oldBytes = new[] { byte.MaxValue };
            var expected = await SeedAsync(targetInner, nameof(WaitingReplaceAsync), oldBytes);
            var failure = new ItemFailedException(expected.Path, TextKey.FailEarlierReplace, null);
            using var cancellation = new CancellationTokenSource();
            var committed = false;
            Task<RemovalResult> operation;
            await using (var held = await context.Runtime.EnterAsync(CancellationToken.None))
            {
                operation = new Transfers(context).DownAsync(sourceEntry, expected.Path, expected, Selection(expected),
                    _ => committed = true, cancellation.Token);
                var waiting = !operation.IsCompleted;
                if (cancel || !waiting) cancellation.Cancel();
                else target.BeforePublish = _ => Task.FromException(failure);
                Check.Require(waiting, "A replacing download stages but waits to publish while the turn is held.");
            }
            var propagated = false;
            try { await operation; }
            catch (OperationCanceledException) when (cancel) { propagated = true; }
            catch (ItemFailedException actual) when (!cancel && ReferenceEquals(actual, failure)) { propagated = true; }
            Check.Require(propagated && !committed &&
                await targetInner.ReadOneAsync(expected.Path, CancellationToken.None) == expected &&
                await BytesAsync(targetInner, expected, oldBytes),
                "Cancellation while waiting and publish failure preserve the old file and never commit.");
            await RequireFreeTurnAsync(context.Runtime);
            RequireNoAdmission(context);
        }
    }

    private static async Task RemovalTurnAsync()
    {
        using var store = new InMemoryStateStore();
        var (context, _, target, sourceInner, targetInner) = ObservedContext(store, upload: false);
        var source = await SeedAsync(sourceInner, "incoming", [1]);
        var removed = await SeedAsync(targetInner, "removed", [2]);
        var replaced = await SeedAsync(targetInner, "replaced", [3]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.BeforeDelete = _ => { entered.SetResult(); return release.Task; };
        var transfers = new Transfers(context);
        var settled = false;
        var deletion = transfers.RemoveAsync(Side.Local, removed, Selection(removed), _ => settled = true, default);
        await entered.Task;
        target.BeforePublish = _ =>
        {
            Check.Require(settled, "A138: delete settlement precedes replacement publication.");
            return Task.CompletedTask;
        };
        var replacement = transfers.DownAsync(source, replaced.Path, replaced, Selection(replaced), _ => { }, default);
        Check.Require(!replacement.IsCompleted && target.Publishes == 0,
            "A138: a local delete owns the turn while replacement waits.");
        var pending = PendingEffectFor("pending", removed, nameof(RemovalTurnAsync));
        store.ArmPending(context.Pair.PairId, pending);
        var recovery = new Recovery(context, null!, null!).ResolvePendingAsync(new CurrentPending(pending),
            RecoveryChoice.Check, default);
        Check.Require(!recovery.IsCompleted && ((FakeRecovery)context.Recovery).Resolved.Count == 0,
            "A139: pending recovery shares the delete and replacement turn.");
        release.SetResult();
        await Task.WhenAll(deletion, replacement, recovery);
        Check.Require(IsNoPending(store, context.Pair.PairId), "A139: recovery settles before releasing its turn.");
        await RequireFreeTurnAsync(context.Runtime);
    }

    private static async Task PendingSlotAsync()
    {
        using var store = new InMemoryStateStore();
        var counters = new StoreCounters(store);
        var (context, source, target, sourceInner, targetInner) =
            ObservedContext(store, upload: false, new Volume(), counters: counters);
        var sourceEntry = await SeedAsync(sourceInner, "source", [1]);
        var expected = await SeedAsync(targetInner, "target", [2]);
        var pending = PendingEffectFor("other-target", expected, nameof(PendingSlotAsync));
        store.ArmPending(context.Pair.PairId, pending);
        var transfers = new Transfers(context);
        var commits = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            counters.Reset();
            var before = (source.Opens, target.Stages, target.Publishes);
            await RequireCauseAsync(() => transfers.DownAsync(sourceEntry, expected.Path, expected, Selection(expected),
                _ => commits++, default), TextKey.FailEarlierReplace, expected.Path);
            Check.Require(counters.Commands == 1 && counters.CommitCalls == 0 && commits == 0 &&
                before == (source.Opens, target.Stages, target.Publishes) &&
                SamePending(store, context.Pair.PairId, pending),
                "An unrecovered pending note blocks each replacement before content or staging, with one read.");
        }

        var remote = await SeedAsync(sourceInner, "upload", [3]);
        counters.Reset();
        var created = await transfers.DownAsync(sourceEntry, "create", null, null, _ => commits++, default);
        Check.Require(created.Outcome == RemovalOutcome.Published && counters.Commands == 0 && commits == 1 &&
            SamePending(store, context.Pair.PairId, pending),
            "Pending local replacement does not read the slot or block creates.");
        var uploaded = await transfers.UpAsync(expected, remote.Path, remote, default);
        Check.Require(uploaded.Path == remote.Path && counters.Commands == 0 &&
            SamePending(store, context.Pair.PairId, pending),
            "Pending local replacement does not block uploads.");

        var otherPair = context.Pair with { PairId = "other-pair" };
        store.SavePair(otherPair);
        counters.Reset();
        await new Transfers(context with
        {
            Pair = otherPair,
            Recovery = new FakeRecovery { Pending = () => counters.Pending(otherPair.PairId) }
        }).DownAsync(sourceEntry, expected.Path, expected,
            Selection(expected), _ => commits++, default);
        Check.Require(counters.Commands == 1 && commits == 2 && SamePending(store, context.Pair.PairId, pending),
            "A different pair's pending note does not block a healthy replacement.");

        expected = (await targetInner.ReadOneAsync(expected.Path, default))!;
        store.SettlePending(context.Pair.PairId, pending, StateChange.Empty);
        counters.Reset();
        await transfers.DownAsync(sourceEntry, expected.Path, expected, Selection(expected), _ => commits++, default);
        Check.Require(counters.Commands == 1 && counters.CommitCalls == 0 && commits == 3 &&
            IsNoPending(store, context.Pair.PairId),
            "A recovered slot allows replacement with only the early read and no pre-download note.");
        await RequireFreeTurnAsync(context.Runtime);
        RequireNoAdmission(context);
    }

    private static async Task LatePendingSlotAsync()
    {
        using var store = new InMemoryStateStore();
        var counters = new StoreCounters(store);
        var (context, source, target, sourceInner, targetInner) =
            ObservedContext(store, upload: false, new Volume(), counters: counters);
        var sourceEntry = await SeedAsync(sourceInner, "source", [1]);
        var expected = await SeedAsync(targetInner, "target", [2]);
        var pending = PendingEffectFor("other-target", expected, nameof(LatePendingSlotAsync));
        target.BeforePublish = _ =>
        {
            RequireHeldTurn(context.Runtime);
            // Model LocalSide's authoritative arm guard, not an extra Transfers preflight.
            try { store.ArmPending(context.Pair.PairId, PendingEffectFor(expected.Path, expected, nameof(LatePendingSlotAsync))); }
            catch (InvalidOperationException error)
            {
                throw new ItemFailedException(expected.Path, TextKey.FailEarlierReplace, error);
            }
            return Task.CompletedTask;
        };
        counters.Reset();
        var before = (source.Opens, target.Stages, target.Publishes);
        var committed = false;
        Task<RemovalResult> operation;
        await using (var held = await context.Runtime.EnterAsync(default))
        {
            operation = new Transfers(context).DownAsync(sourceEntry, expected.Path, expected, Selection(expected),
                _ => committed = true, default);
            Check.Require(counters.Commands == 1 && source.Opens == before.Opens + 1 &&
                target.Stages == before.Stages + 1 && target.Publishes == before.Publishes && !operation.IsCompleted,
                "A healthy replacement stages outside the replace turn after its single early slot read.");
            store.ArmPending(context.Pair.PairId, pending);
        }
        await RequireCauseAsync(() => operation, TextKey.FailEarlierReplace, expected.Path);
        Check.Require(!committed && target.Publishes == before.Publishes + 1 &&
            SamePending(store, context.Pair.PairId, pending) && counters.Commands == 1 &&
            await targetInner.ReadOneAsync(expected.Path, default) == expected &&
            await BytesAsync(targetInner, expected, [2]) && await BytesAsync(sourceInner, sourceEntry, [1]),
            "A slot occupied after staging is refused at publication without changing either file or the old note.");
        await RequireFreeTurnAsync(context.Runtime);
        RequireNoAdmission(context);
    }

    private static async Task NoSpaceAsync()
    {
        // The Dropbox quota latch is retained; a source failure proves nothing about destination space.
        foreach (var publish in new[] { false, true })
        foreach (var replacing in new[] { false, true })
        foreach (var size in new[] { 0, 2 })
        {
            using var store = new InMemoryStateStore();
            var (context, source, target, sourceInner, targetInner) =
                ObservedContext(store, upload: true, new Volume());
            var sourceEntry = await SeedAsync(sourceInner, "source", new byte[size]);
            var expected = replacing ? await SeedAsync(targetInner, "target", [1]) : null;
            var failure = new ItemFailedException("target", TextKey.FailDropboxNoSpace, null);
            Func<string, Task> fail = _ => Task.FromException(failure);
            if (publish) target.BeforePublish = fail;
            else target.BeforeStage = fail;
            var transfers = new Transfers(context);
            var commits = 0;
            await RequireCauseAsync(() => TransferAsync(transfers, true, sourceEntry, "target", expected, null,
                _ => commits++), TextKey.FailDropboxNoSpace, "target");
            Check.Require(commits == 0 && await targetInner.ReadOneAsync("target", default) == expected,
                "Destination stage/publish NoSpace preserves the old target and original failure, without commit.");
            target.BeforeStage = null;
            target.BeforePublish = null;
            foreach (var blockedSize in new[] { size, size + 1 })
            {
                var blocked = await SeedAsync(sourceInner, $"blocked-source-{blockedSize}", new byte[blockedSize]);
                var before = (source.Opens, target.Stages, target.Publishes);
                await RequireCauseAsync(() => TransferAsync(transfers, true, blocked, "blocked-target", null, null,
                    _ => commits++), TextKey.FailDropboxNoSpace, "blocked-target");
                Check.Require(before == (source.Opens, target.Stages, target.Publishes) && commits == 0,
                    "At or above the failed size, including zero, the Dropbox latch precedes source open and staging.");
            }
            if (size > 0)
            {
                var small = await SeedAsync(sourceInner, "small-source", new byte[size - 1]);
                await TransferAsync(transfers, true, small, "small-target", null, null, _ => commits++);
                Check.Require(await targetInner.ReadOneAsync("small-target", default) is not null,
                    "Uploads smaller than the Dropbox quota threshold remain eligible.");
                await RequireCauseAsync(() => TransferAsync(transfers, true, sourceEntry, "still-blocked", null, null,
                    _ => commits++), TextKey.FailDropboxNoSpace, "still-blocked");
            }
            var reverse = await SeedAsync(targetInner, "reverse-source", new byte[size]);
            await TransferAsync(transfers, false, reverse, "reverse-target", null, null, _ => commits++);
            await TransferAsync(new Transfers(context), true, sourceEntry, "next-session", null, null,
                _ => commits++);
            Check.Require(await sourceInner.ReadOneAsync("reverse-target", default) is not null &&
                await targetInner.ReadOneAsync("next-session", default) is not null,
                "The opposite destination and a fresh session have no inherited Dropbox latch.");
            await RequireFreeTurnAsync(context.Runtime);
            RequireNoAdmission(context);
        }

        // The local failed-size latch is deleted: a later candidate still observes space and can transfer.
        foreach (var point in new[] { "reserve", "stage", "publish" })
        foreach (var size in new[] { 0, 2 })
        {
            using var store = new InMemoryStateStore();
            var volume = new Volume();
            var (context, _, target, sourceInner, targetInner) =
                ObservedContext(store, upload: false, volume);
            var declared = point == "reserve" ? 1 : size;
            var sourceEntry = await SeedAsync(sourceInner, "source", new byte[declared]);
            var expected = await SeedAsync(targetInner, "target", [1]);
            var failure = new ItemFailedException("target", TextKey.FailLocalNoSpace, null);
            if (point == "reserve") volume.Available = 0;
            else if (point == "stage") target.BeforeStage = _ => Task.FromException(failure);
            else target.BeforePublish = _ => Task.FromException(failure);
            var transfers = new Transfers(context);
            var commits = 0;
            await RequireCauseAsync(() => TransferAsync(transfers, false, sourceEntry, "target", expected,
                Selection(expected), _ => commits++), TextKey.FailLocalNoSpace, "target");
            Check.Require(commits == 0, "A local NoSpace failure publishes nothing.");
            volume.Available = long.MaxValue;
            target.BeforeStage = null;
            target.BeforePublish = null;
            var spaceReadsBefore = volume.SpaceReads;
            var smaller = await SeedAsync(sourceInner, "smaller", new byte[Math.Max(0, size - 1)]);
            await TransferAsync(transfers, false, smaller, "smaller-target", null, null, _ => commits++);
            Check.Require(volume.SpaceReads > spaceReadsBefore &&
                await targetInner.ReadOneAsync("smaller-target", default) is not null,
                "A local NoSpace failure latches no size; a later candidate still observes space and transfers.");
            await RequireFreeTurnAsync(context.Runtime);
            RequireNoAdmission(context);
        }
    }

    private static async Task SourceNoSpaceAsync()
    {
        foreach (var upload in new[] { true, false })
        foreach (var duringCopy in new[] { false, true })
        foreach (var wrap in duringCopy ? new[] { false, true } : new[] { false })
        foreach (var cause in new[] { upload ? TextKey.FailLocalNoSpace : TextKey.FailDropboxNoSpace })
        {
            using var store = new InMemoryStateStore();
            var (context, source, target, sourceInner, targetInner) =
                ObservedContext(store, upload, new Volume());
            var sourceEntry = await SeedAsync(sourceInner, "source", [1, 2]);
            var failure = new ItemFailedException(sourceEntry.Path, cause, null);
            source.ReadFailure = failure;
            source.FailDuringCopy = duringCopy;
            target.WrapCopyFailure = wrap
                ? upload ? TextKey.FailDropboxNoSpace : TextKey.FailLocalNoSpace : null;
            var transfers = new Transfers(context);
            var commits = 0;
            var actual = await RequireCauseAsync(() => TransferAsync(transfers, upload, sourceEntry, "target", null,
                null, _ => commits++), target.WrapCopyFailure ?? cause, wrap ? "target" : sourceEntry.Path);
            Check.Require(ReferenceEquals(wrap ? actual.InnerException : actual, failure) && commits == 0 &&
                await targetInner.ReadOneAsync("target", default) is null,
                "Source failures retain the adapter exception and its source cause and publish nothing.");
            source.ReadFailure = null;
            source.FailDuringCopy = false;
            target.WrapCopyFailure = null;
            await TransferAsync(transfers, upload, sourceEntry, "target", null, null, _ => commits++);
            var result = await targetInner.ReadOneAsync("target", default);
            Check.Require(result is not null && await BytesAsync(targetInner, result, [1, 2]) &&
                await BytesAsync(sourceInner, sourceEntry, [1, 2]) && (upload || commits == 1),
                "A source failure during destination staging must not latch that destination.");
            await RequireFreeTurnAsync(context.Runtime);
            RequireNoAdmission(context);
        }
    }

    private static async Task ConcurrentNoSpaceAsync()
    {
        foreach (var publish in new[] { false, true })
        foreach (var smallFirst in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var (context, source, target, sourceInner, targetInner) =
                ObservedContext(store, upload: true, new Volume());
            var sources = new[]
            {
                await SeedAsync(sourceInner, "small-source", [1, 2]),
                await SeedAsync(sourceInner, "large-source", [1, 2, 3, 4]),
                await SeedAsync(sourceInner, "in-flight-source", [1, 2, 3])
            };
            var targets = new[] { "small-target", "large-target", "in-flight-target" };
            var releases = targets.ToDictionary(path => path,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            Func<string, Task> pause = path => releases[path].Task;
            if (publish) target.BeforePublish = pause;
            else target.BeforeStage = pause;
            var transfers = new Transfers(context);
            var commits = 0;
            var operations = sources.Select((source, index) => TransferAsync(transfers, true, source,
                targets[index], null, null, _ => Interlocked.Increment(ref commits))).ToArray();
            Check.Require(operations.All(task => !task.IsCompleted) && source.Opens == sources.Length,
                "All concurrent uploads have opened their sources before a threshold is known.");
            var cause = TextKey.FailDropboxNoSpace;
            var order = smallFirst ? new[] { 0, 1 } : new[] { 1, 0 };
            foreach (var index in order)
            {
                releases[targets[index]].SetException(new ItemFailedException(targets[index], cause, null));
                await RequireCauseAsync(() => operations[index], cause, targets[index]);
            }
            releases[targets[2]].SetResult();
            await operations[2];
            var finished = await targetInner.ReadOneAsync("in-flight-target", default);
            Check.Require(finished is not null && await BytesAsync(targetInner, finished, [1, 2, 3]),
                "An already in-flight upload survives concurrent destination failures.");
            target.BeforeStage = null;
            target.BeforePublish = null;
            var before = (source.Opens, target.Stages, target.Publishes);
            await RequireCauseAsync(() => TransferAsync(transfers, true, sources[0], "blocked", null, null,
                _ => commits++), cause, "blocked");
            Check.Require(before == (source.Opens, target.Stages, target.Publishes),
                "Concurrent failures retain the minimum failed size regardless of completion order.");
            var small = await SeedAsync(sourceInner, "eligible-source", [1]);
            await TransferAsync(transfers, true, small, "eligible-target", null, null, _ => commits++);
            Check.Require(await targetInner.ReadOneAsync(targets[0], default) is null &&
                await targetInner.ReadOneAsync(targets[1], default) is null,
                "Failed concurrent uploads leave no destination content.");
            RequireNoAdmission(context);
        }
    }

    private static async Task DownloadAttemptsAsync()
    {
        // Success: one transfer attempt, opened with it, claimed after the validated stage.
        using (var store = new InMemoryStateStore())
        {
            var attempts = new DownloadAttempts();
            var (context, source, _, sourceInner, targetInner) =
                ObservedContext(store, upload: false, new Volume(), attempts);
            var bytes = new[] { (byte)1, (byte)2, (byte)3 };
            var sourceEntry = await SeedAsync(sourceInner, "source", bytes);
            var published = 0;
            var result = await new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                _ => published++, default);
            Check.Require(result.Outcome == RemovalOutcome.Published && published == 1,
                nameof(DownloadAttemptsAsync));
            Check.Require(attempts.Begun.Count == 1 && attempts.Completed == 1 && attempts.Successes == 1,
                "A successful download begins and claims one transfer attempt.");
            Check.Require(attempts.Begun[0].Owner == AttemptOwner.Transfer &&
                ReferenceEquals(source.ReadAttempts[0], attempts.Begun[0]),
                "The remote open receives the exact transfer-owned attempt the transfer began.");
            Check.Require(await BytesAsync(targetInner,
                (await targetInner.ReadOneAsync("target", default))!, bytes), nameof(DownloadAttemptsAsync));
            RequireNoAdmission(context);
        }

        // Upload: no local reservation and no download attempt; Local accepts a null attempt.
        using (var store = new InMemoryStateStore())
        {
            var attempts = new DownloadAttempts();
            var volume = new Volume();
            var (context, source, _, sourceInner, _) =
                ObservedContext(store, upload: true, volume, attempts);
            var sourceEntry = await SeedAsync(sourceInner, "source", [1]);
            await new Transfers(context).UpAsync(sourceEntry, "target", null, default);
            Check.Require(attempts.Begun.Count == 0 && attempts.Completed == 0 &&
                source.ReadAttempts[0] is null && volume.Reservations == 0,
                "An upload begins no download attempt and takes no local reservation.");
            RequireNoAdmission(context);
        }

        // One permitted source retry: fresh attempt, same revision, fresh stage, full content from zero.
        using (var store = new InMemoryStateStore())
        {
            var attempts = new DownloadAttempts();
            attempts.Retry.Enqueue(true);
            var volume = new Volume();
            var (context, source, target, sourceInner, targetInner) =
                ObservedContext(store, upload: false, volume, attempts);
            var bytes = new[] { (byte)1, (byte)2 };
            var sourceEntry = await SeedAsync(sourceInner, "source", bytes);
            source.CopyFailures.Enqueue(
                new ItemFailedException(sourceEntry.Path, TextKey.FailLocalInUse, null));
            var published = 0;
            var result = await new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                _ => published++, default);
            Check.Require(result.Outcome == RemovalOutcome.Published && published == 1,
                "One permitted transfer source retry recovers and publishes once.");
            Check.Require(attempts.Begun.Count == 2 && attempts.Completed == 2 && source.Opens == 2 &&
                target.Stages == 2 && attempts.Begun[0] != attempts.Begun[1],
                "The retry uses a fresh attempt and a fresh stage, never a partial append.");
            Check.Require(source.ReadEntries[0] == source.ReadEntries[1] && volume.Reservations == 2,
                "The retry reopens the exact same source revision and reserves fresh space.");
            Check.Require(await BytesAsync(targetInner,
                (await targetInner.ReadOneAsync("target", default))!, bytes),
                "The retried stage restarts from zero and publishes the full content.");
            // Both grants retired: the same declared size fits a fresh observation.
            volume.Available = sourceEntry.Size;
            var followUp = await new Transfers(context).DownAsync(sourceEntry, "follow-up", null, null,
                _ => published++, default);
            Check.Require(followUp.Outcome == RemovalOutcome.Published,
                "Each attempt's reservation is retired before a later transfer is admitted.");
            RequireNoAdmission(context);
        }

        // Owner refusal: no nested or second source retry; the original failure propagates.
        using (var store = new InMemoryStateStore())
        {
            var attempts = new DownloadAttempts();
            var volume = new Volume();
            var (context, source, target, sourceInner, _) =
                ObservedContext(store, upload: false, volume, attempts);
            var sourceEntry = await SeedAsync(sourceInner, "source", new byte[2]);
            var failure = new ItemFailedException(sourceEntry.Path, TextKey.FailLocalInUse, null);
            source.ReadFailures.Enqueue(failure);
            var published = 0;
            var propagated = false;
            try
            {
                await new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                    _ => published++, default);
            }
            catch (ItemFailedException actual) when (ReferenceEquals(actual, failure)) { propagated = true; }
            Check.Require(propagated && published == 0 && attempts.Begun.Count == 1 &&
                attempts.Completed == 1 && attempts.Decisions[^1] == false && source.Opens == 1 &&
                target.Stages == 0,
                "An owner refusal permits no nested or second source retry and publishes nothing.");
            volume.Available = sourceEntry.Size;
            var recovered = await new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                _ => published++, default);
            Check.Require(recovered.Outcome == RemovalOutcome.Published && published == 1,
                "The refused attempt's reservation retires before a later transfer is admitted.");
            RequireNoAdmission(context);
        }

        // Two failures: at most one retry, the second refusal propagates.
        using (var store = new InMemoryStateStore())
        {
            var attempts = new DownloadAttempts();
            attempts.Retry.Enqueue(true);
            attempts.Retry.Enqueue(false);
            var volume = new Volume();
            var (context, source, target, sourceInner, _) =
                ObservedContext(store, upload: false, volume, attempts);
            var sourceEntry = await SeedAsync(sourceInner, "source", new byte[2]);
            source.ReadFailures.Enqueue(
                new ItemFailedException(sourceEntry.Path, TextKey.FailLocalInUse, null));
            source.ReadFailures.Enqueue(
                new ItemFailedException(sourceEntry.Path, TextKey.FailLocalInUseOrAccessDenied, null));
            var published = 0;
            var propagated = false;
            try
            {
                await new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                    _ => published++, default);
            }
            catch (ItemFailedException) { propagated = true; }
            Check.Require(propagated && published == 0 && attempts.Begun.Count == 2 &&
                attempts.Completed == 2 && source.Opens == 2 && target.Stages == 0,
                "A second source failure after one retry stops without a third attempt or publication.");
            RequireNoAdmission(context);
        }

        // A successful source read with a failed local stage never requests a source retry.
        using (var store = new InMemoryStateStore())
        {
            var attempts = new DownloadAttempts();
            var volume = new Volume();
            var (context, source, target, sourceInner, _) =
                ObservedContext(store, upload: false, volume, attempts);
            var sourceEntry = await SeedAsync(sourceInner, "source", [1, 2]);
            var failure = new ItemFailedException("target", TextKey.FailLocalNoSpace, null);
            target.StageFailureAfterCopy = failure;
            var published = 0;
            var propagated = false;
            try
            {
                await new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                    _ => published++, default);
            }
            catch (ItemFailedException actual) when (ReferenceEquals(actual, failure)) { propagated = true; }
            Check.Require(propagated && published == 0 && attempts.Begun.Count == 1 &&
                attempts.Completed == 1 && attempts.Successes == 1 && attempts.Decisions[^1] == false &&
                source.Opens == 1 && volume.Disposals == 1,
                "A validated-stage failure after a successful source read is not a source retry.");
            RequireNoAdmission(context);
        }

        // A stage failure before any source completion also asks the owner once and refuses.
        using (var store = new InMemoryStateStore())
        {
            var attempts = new DownloadAttempts();
            var volume = new Volume();
            var (context, source, target, sourceInner, _) =
                ObservedContext(store, upload: false, volume, attempts);
            var sourceEntry = await SeedAsync(sourceInner, "source", [1, 2]);
            var failure = new ItemFailedException("target", TextKey.FailLocalNoSpace, null);
            target.BeforeStage = _ => Task.FromException(failure);
            var published = 0;
            var propagated = false;
            try
            {
                await new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                    _ => published++, default);
            }
            catch (ItemFailedException actual) when (ReferenceEquals(actual, failure)) { propagated = true; }
            Check.Require(propagated && published == 0 && attempts.Begun.Count == 1 &&
                attempts.Completed == 1 && attempts.Decisions[^1] == false &&
                attempts.Successes == 0 && source.Opens == 1 && target.Stages == 1,
                "A stage failure before source completion is claimed once and never retried or published.");
            RequireNoAdmission(context);
        }
    }

    private static async Task ReservationLifetimeAsync()
    {
        // Pre-admission refusal opens no source and creates no stage.
        using (var store = new InMemoryStateStore())
        {
            var volume = new Volume(0);
            var (context, source, target, sourceInner, targetInner) =
                ObservedContext(store, upload: false, volume);
            var sourceEntry = await SeedAsync(sourceInner, "source", [1]);
            var published = 0;
            await RequireCauseAsync(() => new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                _ => published++, default), TextKey.FailLocalNoSpace, "target");
            Check.Require(published == 0 && source.Opens == 0 && target.Stages == 0 &&
                target.Publishes == 0 && await targetInner.ReadOneAsync("target", default) is null,
                "A refused local reservation opens no source, creates no stage and publishes nothing.");
        }

        // Success: one reservation, one fresh capacity observation, retired before publication.
        using (var store = new InMemoryStateStore())
        {
            var volume = new Volume(100);
            var (context, _, target, sourceInner, _) =
                ObservedContext(store, upload: false, volume);
            var sourceEntry = await SeedAsync(sourceInner, "source", new byte[60]);
            target.BeforePublish = _ =>
            {
                Check.Require(volume.Reservations == 1 && volume.Disposals == 1,
                    "The local grant is retired before publication.");
                return Task.CompletedTask;
            };
            var published = 0;
            await new Transfers(context).DownAsync(sourceEntry, "target", null, null, _ => published++, default);
            Check.Require(published == 1 && volume.Reservations == 1 && volume.Disposals == 1 &&
                volume.SpaceReads >= 1,
                "One local download takes one reservation and one fresh capacity observation.");
        }

        // P2-05 decisive checkpoints: an open stage keeps its declared grant; retirement precedes publication.
        using (var store = new InMemoryStateStore())
        {
            var volume = new Volume(100);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (context, source, target, sourceInner, _) =
                ObservedContext(store, upload: false, volume);
            var bytes = new byte[60];
            var sourceEntry = await SeedAsync(sourceInner, "a-source", bytes);
            source.WrapSource = (entry, _, inner) =>
                entry.Path == "a-source" ? new GatedStream(inner, 40, gate.Task) : inner;
            var enteredPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var publishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            target.BeforePublish = path =>
            {
                if (path != "a-target") return Task.CompletedTask;
                enteredPublish.TrySetResult();
                return publishGate.Task;
            };
            var committed = 0;
            var a = new Transfers(context).DownAsync(sourceEntry, "a-target", null, null,
                _ => committed++, default);
            Check.Require(!a.IsCompleted && volume.Reservations == 1 && volume.Disposals == 0 &&
                target.Stages == 1,
                "An open local stage keeps its declared reservation.");

            volume.Available = 60;
            var b40 = await SeedAsync(sourceInner, "b40", new byte[40]);
            var opensBefore = source.Opens;
            var stagesBefore = target.Stages;
            await RequireCauseAsync(() => new Transfers(context).DownAsync(b40, "b40-target", null, null,
                _ => committed++, default), TextKey.FailLocalNoSpace, "b40-target");
            Check.Require(source.Opens == opensBefore && target.Stages == stagesBefore,
                "A refused candidate opens no source and creates no stage.");

            volume.Available = 100;
            var granted = await new Transfers(context).DownAsync(b40, "b40-grant", null, null,
                _ => committed++, default);
            Check.Require(granted.Outcome == RemovalOutcome.Published,
                "The 40-byte candidate fits beside the open 60-byte grant.");
            var b41 = await SeedAsync(sourceInner, "b41", new byte[41]);
            await RequireCauseAsync(() => new Transfers(context).DownAsync(b41, "b41-refused", null, null,
                _ => committed++, default), TextKey.FailLocalNoSpace, "b41-refused");

            var disposedBeforeRelease = volume.Disposals;
            gate.SetResult();
            await enteredPublish.Task;
            Check.Require(volume.Disposals == disposedBeforeRelease + 1 && !a.IsCompleted,
                "After its validated stage the grant retires before the held publication.");

            volume.Available = 40;
            await new Transfers(context).DownAsync(b40, "b40-again", null, null, _ => committed++, default);
            await RequireCauseAsync(() => new Transfers(context).DownAsync(b41, "b41-again", null, null,
                _ => committed++, default), TextKey.FailLocalNoSpace, "b41-again");
            publishGate.SetResult();
            var final = await a;
            Check.Require(final.Outcome == RemovalOutcome.Published && committed >= 3,
                "The held publication completes after its accounting grant was retired.");
            RequireNoAdmission(context);
        }

        // P2-06: a failed stage retires only its own grant; F stays external, so same-size fits and larger refuses.
        using (var store = new InMemoryStateStore())
        {
            var volume = new Volume(60);
            var (context, _, target, sourceInner, _) =
                ObservedContext(store, upload: false, volume);
            var failure = new ItemFailedException("a-target", TextKey.FailLocalNoSpace, null);
            target.StageFailureAfterCopy = failure;
            var sourceA = await SeedAsync(sourceInner, "a-source", new byte[60]);
            await RequireCauseAsync(() => new Transfers(context).DownAsync(sourceA, "a-target", null, null,
                _ => { }, default), TextKey.FailLocalNoSpace, "a-target");
            Check.Require(volume.Reservations == 1 && volume.Disposals == 1,
                "A failed local stage retires only its own grant.");
            target.StageFailureAfterCopy = null;
            var b60 = await SeedAsync(sourceInner, "b60", new byte[60]);
            var ok = await new Transfers(context).DownAsync(b60, "b60-target", null, null, _ => { }, default);
            Check.Require(ok.Outcome == RemovalOutcome.Published,
                "After retirement a same-size candidate fits the external observation.");
            var b61 = await SeedAsync(sourceInner, "b61", new byte[61]);
            await RequireCauseAsync(() => new Transfers(context).DownAsync(b61, "b61-target", null, null,
                _ => { }, default), TextKey.FailLocalNoSpace, "b61-target");
        }

        // P2-09: cancellation while waiting at the accounting gate releases no lease and opens no source.
        using (var store = new InMemoryStateStore())
        {
            var volume = new Volume(100);
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            volume.ReservationGate = block.Task;
            var (context, source, target, sourceInner, _) =
                ObservedContext(store, upload: false, volume);
            var sourceEntry = await SeedAsync(sourceInner, "source", [1]);
            using var cancellation = new CancellationTokenSource();
            var operation = new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                _ => { }, cancellation.Token);
            Check.Require(!operation.IsCompleted && volume.Reservations == 1 &&
                volume.Disposals == 0 && source.Opens == 0 && target.Stages == 0,
                "A transfer waiting at the accounting gate holds no source or stage.");
            cancellation.Cancel();
            var propagated = false;
            try { await operation; }
            catch (OperationCanceledException) { propagated = true; }
            Check.Require(propagated && volume.Disposals == 0 && source.Opens == 0 && target.Stages == 0,
                "Cancellation at the gate performs no source, stage or reservation-retirement work.");
            volume.ReservationGate = null;
            var recovered = await new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                _ => { }, default);
            Check.Require(recovered.Outcome == RemovalOutcome.Published,
                "The accounting owner remains usable after cancellation.");
        }
    }

    private static async Task StagedLifetimeAsync()
    {
        foreach (var consumesSourceOnPublish in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var (context, source, target, sourceInner, _) =
                ObservedContext(store, upload: true, new Volume());
            var trace = new List<string>();
            source.Trace = trace.Add;
            target.Trace = trace.Add;
            var sourceEntry = await SeedAsync(sourceInner, "source", [1, 2]);
            target.WrapStaged = staged => new StagedWrapper(staged, consumesSourceOnPublish);
            var stagedCalls = 0;
            Func<ValueTask> staged = () =>
            {
                stagedCalls++;
                trace.Add("staged");
                return ValueTask.CompletedTask;
            };
            var result = await new Transfers(context).UpAsync(sourceEntry, "target", null, default, staged);
            Check.Require(result.Path == "target" && stagedCalls == 1,
                "The staged completion reports exactly once.");
            var order = trace.Where(step => step is "publish" or "source-disposed" or "staged").ToArray();
            Check.Require(consumesSourceOnPublish
                    ? order.SequenceEqual(new[] { "publish", "source-disposed", "staged" })
                    : order.SequenceEqual(new[] { "source-disposed", "staged", "publish" }),
                consumesSourceOnPublish
                    ? "A source-consuming stage keeps the source open until publication, then reports."
                    : "A direct stage is handed off and reported before publication.");
            RequireNoAdmission(context);
        }
    }

    private static async Task OutcomeGatingAsync()
    {
        foreach (var outcome in new[]
        {
            RemovalOutcome.NoEffect, RemovalOutcome.Retained, RemovalOutcome.Unknown,
            RemovalOutcome.WrongRestored, RemovalOutcome.Published
        })
        {
            using var store = new InMemoryStateStore();
            var counters = new StoreCounters(store);
            var (context, _, target, sourceInner, targetInner) =
                ObservedContext(store, upload: false, new Volume(), counters: counters);
            var sourceEntry = await SeedAsync(sourceInner, "source", [9]);
            var expected = await SeedAsync(targetInner, "target", [8]);
            var operationId = outcome == RemovalOutcome.NoEffect ? nameof(OutcomeGatingAsync) : null;
            var final = outcome == RemovalOutcome.Published
                ? expected with { Size = sourceEntry.Size, Id = nameof(OutcomeGatingAsync) }
                : null;
            var scripted = new RemovalResult(outcome, operationId, final,
                outcome == RemovalOutcome.Retained ? TextKey.FailLocalInUse : null);
            if (outcome == RemovalOutcome.NoEffect)
            {
                var pending = PendingEffectFor(expected.Path, expected, operationId!);
                target.ScriptedPublish = _ =>
                {
                    store.ArmPending(context.Pair.PairId, pending);
                    return scripted;
                };
            }
            else
            {
                target.ScriptedPublish = _ => scripted;
            }
            RemovalResult? committed = null;
            var commits = 0;
            var result = await new Transfers(context).DownAsync(sourceEntry, expected.Path, expected,
                Selection(expected), r => { committed = r; commits++; }, default);
            Check.Require(ReferenceEquals(result, scripted), "The classified effect is returned unchanged.");
            if (outcome == RemovalOutcome.Published)
            {
                Check.Require(commits == 1 && ReferenceEquals(committed, scripted),
                    "Only a verified publication invokes the one commit.");
            }
            else
            {
                Check.Require(commits == 0, "A non-published effect never invokes the commit.");
            }
            if (outcome == RemovalOutcome.NoEffect)
            {
                Check.Require(IsNoPending(store, context.Pair.PairId),
                    "A matching no-effect operation settles its armed note.");
            }
            await RequireFreeTurnAsync(context.Runtime);
            RequireNoAdmission(context);
        }

        // A no-effect result for a different operation leaves the armed note untouched.
        using (var store = new InMemoryStateStore())
        {
            var (context, _, target, sourceInner, targetInner) =
                ObservedContext(store, upload: false, new Volume());
            var sourceEntry = await SeedAsync(sourceInner, "source", [9]);
            var expected = await SeedAsync(targetInner, "target", [8]);
            var pending = PendingEffectFor(expected.Path, expected, nameof(OutcomeGatingAsync));
            target.ScriptedPublish = _ =>
            {
                store.ArmPending(context.Pair.PairId, pending);
                return new RemovalResult(RemovalOutcome.NoEffect, nameof(NoPending), null, null);
            };
            var commits = 0;
            await new Transfers(context).DownAsync(sourceEntry, expected.Path, expected, Selection(expected),
                _ => commits++, default);
            Check.Require(commits == 0 && SamePending(store, context.Pair.PairId, pending),
                "A no-effect result for a different operation leaves the armed note untouched.");
        }
    }

    private static async Task FolderReplacementAsync()
    {
        using var store = new InMemoryStateStore();
        var counters = new StoreCounters(store);
        var (context, _, target, sourceInner, targetInner) =
            ObservedContext(store, upload: false, new Volume(), counters: counters);
        var sourceEntry = await SeedAsync(sourceInner, nameof(SeedAsync), [4, 5]);
        var folder = new Entry(nameof(FolderReplacementAsync), true, 0, default,
            nameof(FolderReplacementAsync), "", "", null);
        targetInner.Seed(folder, null);
        RemovalResult? committed = null;
        var commits = 0;
        await using (var held = await context.Runtime.EnterAsync(default))
        {
            var result = await new Transfers(context).DownAsync(sourceEntry, folder.Path, folder, null,
                r => { committed = r; commits++; }, default);
            Check.Require(result.Outcome == RemovalOutcome.Published && commits == 1 &&
                ReferenceEquals(committed, result),
                "A local folder replacement completes without owning the file replace turn.");
        }
        Check.Require(target.Deletes == 1 && target.DeletedExpected == folder &&
            target.DeleteSelection is null && target.StagesAtDelete == 1,
            "A local folder replacement recycles the expected folder after staging with no file losing proof.");
        Check.Require(target.Publishes == 1 && target.LastExpected is null && target.LastSelection is null,
            "After recycling, the folder replacement publishes create-only.");
        Check.Require(counters.Commands == 0, "A folder replacement performs no pending-slot read.");
        Check.Require(await BytesAsync(targetInner,
            (await targetInner.ReadOneAsync(folder.Path, default))!, [4, 5]),
            nameof(FolderReplacementAsync));
        await RequireFreeTurnAsync(context.Runtime);
        RequireNoAdmission(context);

        // A refused recycle stops before create-only publication and commit.
        using var refusedStore = new InMemoryStateStore();
        var (refusedContext, _, refusedTarget, refusedSourceInner, refusedTargetInner) =
            ObservedContext(refusedStore, upload: false, new Volume());
        var refusedSource = await SeedAsync(refusedSourceInner, nameof(SeedAsync), [6]);
        var refusedFolder = folder with { Path = nameof(OutcomeGatingAsync) };
        refusedTargetInner.Seed(refusedFolder, null);
        refusedTarget.ScriptedDelete = _ =>
            new RemovalResult(RemovalOutcome.Retained, null, null, TextKey.FailLocalInUse);
        var refusedCommits = 0;
        var refusedResult = await new Transfers(refusedContext).DownAsync(refusedSource, refusedFolder.Path,
            refusedFolder, null, _ => refusedCommits++, default);
        Check.Require(refusedResult.Outcome == RemovalOutcome.Retained && refusedCommits == 0 &&
            refusedTarget.Publishes == 0,
            "A refused folder recycle stops before create-only publication and commit.");
        RequireNoAdmission(refusedContext);
    }

    private static async Task SelectionForwardingAsync()
    {
        using var store = new InMemoryStateStore();
        var (context, _, target, sourceInner, targetInner) =
            ObservedContext(store, upload: false, new Volume());
        var sourceEntry = await SeedAsync(sourceInner, nameof(SeedAsync), [1, 2]);
        var expected = await SeedAsync(targetInner, nameof(SelectionForwardingAsync), [3]);
        var saved = new SyncedEntry(nameof(SyncedEntry), expected.Path, nameof(SyncedEntry), "remote", false,
            expected.Id, expected.Size, 0, nameof(SyncedEntry), RemovalHash);
        var selection = new RemovalSelection(new RemovalProof(expected, VolumeId, "selected"), saved);
        Check.Require(selection.Proof.ContentHash != sourceEntry.ContentHash,
            "The selected losing hash is not the incoming source hash.");
        var commits = 0;
        await new Transfers(context).DownAsync(sourceEntry, expected.Path, expected, selection,
            _ => commits++, default);
        Check.Require(ReferenceEquals(target.LastSelection, selection) &&
            target.LastExpected == expected && commits == 1,
            "The exact selected losing proof and saved binding reach Local publication unchanged.");
        Check.Require(await BytesAsync(targetInner,
            (await targetInner.ReadOneAsync(expected.Path, default))!, [1, 2]),
            nameof(SelectionForwardingAsync));
        RequireNoAdmission(context);
    }

    private static async Task TimePreservationAsync()
    {
        using var store = new InMemoryStateStore();
        var (context, _, _, sourceInner, _) =
            ObservedContext(store, upload: false, new Volume());
        var modified = DateTimeOffset.UnixEpoch.AddDays(5);
        var bytes = new[] { (byte)7 };
        var sourceEntry = new Entry(nameof(TimePreservationAsync), false, bytes.LongLength, modified,
            nameof(TimePreservationAsync), nameof(TimePreservationAsync), "", null);
        await SeedEntryAsync(sourceInner, sourceEntry, bytes);
        var commits = 0;
        RemovalResult? committed = null;
        var result = await new Transfers(context).DownAsync(sourceEntry, "local-target", null, null,
            r => { committed = r; commits++; }, default);
        Check.Require(result.Final is not null && result.Final!.Modified == modified &&
            committed!.Final!.Modified == modified,
            "A download preserves the source content time, never substituting wall-clock time.");
        Check.Require(result.Outcome == RemovalOutcome.Published && commits == 1,
            "The keep-both caller's label download publishes and commits once.");
        RequireNoAdmission(context);
    }

    private static async Task CancellationAsync()
    {
        // Pre-cancelled: no reservation, attempt, source, stage or publication.
        using (var store = new InMemoryStateStore())
        {
            var attempts = new DownloadAttempts();
            var volume = new Volume();
            var (context, source, target, sourceInner, _) =
                ObservedContext(store, upload: false, volume, attempts);
            var sourceEntry = await SeedAsync(sourceInner, "source", [1]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var propagated = false;
            try
            {
                await new Transfers(context).DownAsync(sourceEntry, "target", null, null, _ => { },
                    cancellation.Token);
            }
            catch (OperationCanceledException) { propagated = true; }
            Check.Require(propagated && volume.Reservations == 0 && attempts.Begun.Count == 0 &&
                source.Opens == 0 && target.Stages == 0 && target.Publishes == 0 && volume.Disposals == 0,
                "A cancelled download performs no accounting, attempt, source, stage or publication work.");
            RequireNoAdmission(context);
        }

        // Cancellation during the source copy claims CallerStopped once and never retries or publishes.
        using (var store = new InMemoryStateStore())
        {
            var attempts = new DownloadAttempts();
            var volume = new Volume();
            var (context, source, _, sourceInner, _) =
                ObservedContext(store, upload: false, volume, attempts);
            var sourceEntry = await SeedAsync(sourceInner, "source", [1, 2, 3]);
            using var cancellation = new CancellationTokenSource();
            source.WrapSource = (_, _, inner) => new CancelAfterStream(inner, 1, cancellation);
            var published = 0;
            var propagated = false;
            try
            {
                await new Transfers(context).DownAsync(sourceEntry, "target", null, null,
                    _ => published++, cancellation.Token);
            }
            catch (OperationCanceledException) { propagated = true; }
            Check.Require(propagated && published == 0 && attempts.Begun.Count == 1 &&
                attempts.Completed == 1 && attempts.Decisions[^1] == false && attempts.Successes == 0 &&
                volume.Reservations == 1 && volume.Disposals == 1,
                "A caller-stopped source read is claimed once, releases its reservation and schedules no retry.");
            RequireNoAdmission(context);
        }
    }

    private static async Task TransferAsync(Transfers transfers, bool upload, Entry source, string target,
        Entry? expected, RemovalSelection? selection, Action<RemovalResult> commit)
    {
        if (upload)
        {
            _ = await transfers.UpAsync(source, target, expected, default);
            return;
        }
        _ = await transfers.DownAsync(source, target, expected, selection, commit, default);
    }

    private static async Task<ItemFailedException> RequireCauseAsync(Func<Task> operation, TextKey cause,
        string path)
    {
        try { await operation(); }
        catch (ItemFailedException error)
        {
            Check.Require(error.Cause == cause && error.Path == path,
                "Transfer failure keeps its cause and path.");
            return error;
        }
        throw new InvalidOperationException("The transfer should have failed.");
    }

    private static Task<ItemFailedException> RequireCauseAsync<T>(Func<Task<T>> operation, TextKey cause,
        string path) => RequireCauseAsync(async () => { await operation(); }, cause, path);

    private static void RequireHeldTurn(ReplaceTurn turn)
    {
        using var cancellation = new CancellationTokenSource();
        var waiting = turn.EnterAsync(cancellation.Token);
        var held = !waiting.IsCompleted;
        cancellation.Cancel();
        try
        {
            waiting.GetAwaiter().GetResult().DisposeAsync().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Check.Require(held, nameof(RequireHeldTurn));
    }

    private static async Task RequireFreeTurnAsync(ReplaceTurn turn)
    {
        using var cancellation = new CancellationTokenSource();
        var waiting = turn.EnterAsync(cancellation.Token);
        var free = waiting.IsCompletedSuccessfully;
        cancellation.Cancel();
        try
        {
            await using var held = await waiting;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Check.Require(free, nameof(RequireFreeTurnAsync));
    }

    private static void RequireNoAdmission(ExecutorContext context)
    {
        var admission = (FakeAdmission)context.Admission;
        Check.Require(admission.Admitted == 0 && admission.Completed == 0, nameof(RequireNoAdmission));
    }

    private static async Task<Entry> SeedAsync(ISide side, string path, byte[] bytes)
    {
        var source = new Entry(path, false, bytes.LongLength, DateTimeOffset.UnixEpoch, path, path, "", null);
        return await SeedEntryAsync(side, source, bytes);
    }

    private static async Task<Entry> SeedEntryAsync(ISide side, Entry source, byte[] bytes)
    {
        await using var content = new MemoryStream(bytes, writable: false);
        await using var stage = await side.StageAsync(source.Path, content, source, CancellationToken.None);
        var result = await side.PublishAsync(stage, null, null, CancellationToken.None);
        return result.Final ?? throw new InvalidOperationException(nameof(SeedEntryAsync));
    }

    private static async Task<bool> BytesAsync(ISide side, Entry entry, byte[] expected)
    {
        await using var content = await side.OpenReadAsync(entry, null, CancellationToken.None);
        using var result = new MemoryStream();
        await content.CopyToAsync(result);
        return result.ToArray().SequenceEqual(expected);
    }

    private static RemovalSelection Selection(Entry expected) =>
        new(new RemovalProof(expected, VolumeId, RemovalHash), null);

    private static PendingEffect PendingEffectFor(string path, Entry source, string operationId) =>
        new(1, operationId, 0, PendingKind.LocalReplace, PendingPhase.Armed, path,
            new RemovalProof(source with { Path = path }, VolumeId, RemovalHash), null, source,
            null, null, null, false, null);

    private static bool SamePending(InMemoryStateStore store, string pairId, PendingEffect expected) =>
        store.Pending(pairId) is CurrentPending current && current.Value == expected;

    private static bool IsNoPending(InMemoryStateStore store, string pairId) =>
        store.Pending(pairId) is NoPending;

    private static PairConfig Pair() => new(nameof(TransfersChecks), nameof(TransfersChecks), LocalRoot, "",
        nameof(FakeSide), [], [], FirstRunPolicy.NewestWins, true, null);

    private static ExecutorContext Context(InMemoryStateStore store, Volume? volume = null,
        DownloadAttempts? attempts = null)
    {
        volume ??= new Volume();
        attempts ??= new DownloadAttempts();
        var pair = Pair();
        if (store.Pair(pair.PairId) is null) store.SavePair(pair);
        var settings = new EngineSettings("", "", "", "", "", RecycleFallback.Fail, default,
            FirstRunPolicy.NewestWins, default, "", LogBufferCharacters, ScanTestSettings.BatchRows) { JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes, LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes };
        return new ExecutorContext(
            Local: new FakeSide(Side.Local), Dropbox: new FakeSide(Side.Dropbox), Recovery: new FakeRecovery(),
            Store: store, Pair: pair, Scope: new PathScope(pair, [pair]), Settings: settings,
            Runtime: new ReplaceTurn(), Admission: new FakeAdmission(), Priority: false, Events: _ => { },
            ReserveDownloadAsync: volume.ReserveAsync, Time: TimeProvider.System,
            ReadDeletionEvidenceAsync: static (_, _, _) =>
                throw new NotSupportedException(nameof(ExecutorContext.ReadDeletionEvidenceAsync)),
            BeginDownloadAttempt: attempts.Begin, CompleteDownloadAttemptAsync: attempts.CompleteAsync,
            UploadAdmissionIdentity: (Account, Route, SourceIdentity),
            ItemAttempts: new NoItemAttempts(), Opportunity: attempts.Opportunity, ExpectedGeneration: 0);
    }

    private static (ExecutorContext Context, TransferSide Source, TransferSide Target, FakeSide SourceInner,
        FakeSide TargetInner) ObservedContext(InMemoryStateStore store, bool upload, Volume? volume = null,
        DownloadAttempts? attempts = null, StoreCounters? counters = null)
    {
        var context = Context(store, volume, attempts);
        var sourceInner = new FakeSide(upload ? Side.Local : Side.Dropbox);
        var targetInner = new FakeSide(upload ? Side.Dropbox : Side.Local);
        var source = new TransferSide(sourceInner);
        var target = new TransferSide(targetInner);
        var local = upload ? source : target;
        var dropbox = upload ? target : source;
        context = context with
        {
            Local = local,
            Dropbox = dropbox,
            Store = counters is null ? store : (IStateStore)counters
        };
        ((FakeRecovery)context.Recovery).Pending = () => context.Store.Pending(context.Pair.PairId);
        return (context, source, target, sourceInner, targetInner);
    }

    private static RequestResult Classify(Exception error) =>
        error is OperationCanceledException ? RequestResult.CallerStopped : RequestResult.TransientFailure;

    /// <summary>The real accounting owner with synthetic volume observations, plus test-only call counts.</summary>
    private sealed class Volume
    {
        private readonly DownloadSpace _space;
        private int _serialReads;
        private int _spaceReads;
        private int _reservations;
        private int _disposals;

        public Volume(long available = long.MaxValue)
        {
            Available = available;
            _space = new DownloadSpace(
                _ => { Interlocked.Increment(ref _serialReads); return 1; },
                _ => { Interlocked.Increment(ref _spaceReads); return Available; });
        }

        public long Available { get; set; }
        public Task? ReservationGate { get; set; }
        public int SerialReads => Volatile.Read(ref _serialReads);
        public int SpaceReads => Volatile.Read(ref _spaceReads);
        public int Reservations => Volatile.Read(ref _reservations);
        public int Disposals => Volatile.Read(ref _disposals);

        public async ValueTask<IAsyncDisposable> ReserveAsync(string root, string target, long bytes,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _reservations);
            if (ReservationGate is { } gate) await gate.WaitAsync(ct).ConfigureAwait(false);
            return new CountedLease(await _space.ReserveAsync(root, target, bytes, ct).ConfigureAwait(false), this);
        }

        private sealed class CountedLease(IAsyncDisposable inner, Volume owner) : IAsyncDisposable
        {
            private int _retired;

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _retired, 1) == 0) Interlocked.Increment(ref owner._disposals);
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Test-owned download attempt owner: explicit authority, one claim per published completion.</summary>
    private sealed class DownloadAttempts
    {
        public object Opportunity { get; } = new();
        public List<RequestAttempt> Begun { get; } = [];
        public List<RequestAttempt> Completions { get; } = [];
        public List<bool> Decisions { get; } = [];
        public Queue<bool> Retry { get; } = new();
        public int Successes { get; private set; }
        public int Completed => Completions.Count;

        public RequestAttempt Begin(object source)
        {
            var attempt = new RequestAttempt(Opportunity, AttemptOwner.Transfer);
            Begun.Add(attempt);
            return attempt;
        }

        public Task<bool> CompleteAsync(RequestAttempt attempt, CancellationToken ct)
        {
            Completions.Add(attempt);
            if (!attempt.TryClaimCompletion())
            {
                Decisions.Add(false);
                return Task.FromResult(false);
            }
            var result = attempt.Completion!.Value.Result;
            if (result == RequestResult.Success) Successes++;
            var permit = result == RequestResult.TransientFailure && Retry.Count > 0 && Retry.Dequeue();
            Decisions.Add(permit);
            return Task.FromResult(permit);
        }
    }

    private sealed class NoItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            throw new NotSupportedException("Transfers performs no item-attempt registration.");

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
            IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            PendingEffect? expectedPending, ItemRetryToken? expectedEpisode) =>
            throw new NotSupportedException("Transfers performs no item-attempt registration.");

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
            throw new NotSupportedException("Transfers performs no item-attempt registration.");
    }

    // Keep scheduling and stream-fault injection here; FakeSide remains the content/version authority.
    private sealed class TransferSide(ISide inner) : ISide
    {
        public int Opens { get; private set; }
        public int Stages { get; private set; }
        public int Publishes { get; private set; }
        public int Deletes { get; private set; }
        public int StagesAtDelete { get; private set; }
        public ItemFailedException? ReadFailure { get; set; }
        public Queue<Exception> ReadFailures { get; } = new();
        public Queue<Exception> CopyFailures { get; } = new();
        public bool FailDuringCopy { get; set; }
        public TextKey? WrapCopyFailure { get; set; }
        public Func<string, Task>? BeforeStage { get; set; }
        public Func<string, Task>? BeforePublish { get; set; }
        public Func<string, Task>? BeforeDelete { get; set; }
        public Func<Entry, RequestAttempt?, Stream, Stream>? WrapSource { get; set; }
        public Func<IStaged, IStaged>? WrapStaged { get; set; }
        public Func<string, RemovalResult>? ScriptedPublish { get; set; }
        public Func<string, RemovalResult>? ScriptedDelete { get; set; }
        public Exception? StageFailureAfterCopy { get; set; }
        public Action<string>? Trace { get; set; }
        public List<RequestAttempt?> ReadAttempts { get; } = [];
        public List<Entry> ReadEntries { get; } = [];
        public RemovalSelection? LastSelection { get; private set; }
        public Entry? LastExpected { get; private set; }
        public Entry? DeletedExpected { get; private set; }
        public RemovalSelection? DeleteSelection { get; private set; }
        public Side Side => inner.Side;

        public async Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct)
        {
            Opens++;
            ReadEntries.Add(entry);
            ReadAttempts.Add(attempt);
            try
            {
                if (ReadFailures.Count > 0) throw ReadFailures.Dequeue();
                if (ReadFailure is { } failure)
                {
                    if (!FailDuringCopy) throw failure;
                    return new CompletionStream(new FailedReadStream(failure), attempt, Trace);
                }
                var stream = await inner.OpenReadAsync(entry, attempt, ct).ConfigureAwait(false);
                if (WrapSource is { } wrap) stream = wrap(entry, attempt, stream);
                if (CopyFailures.Count > 0) stream = new FailedReadStream(CopyFailures.Dequeue());
                else if (FailDuringCopy)
                    stream = new FailedReadStream(new InvalidOperationException(nameof(OpenReadAsync)));
                return new CompletionStream(stream, attempt, Trace);
            }
            catch (Exception error)
            {
                attempt?.TryComplete(Classify(error), error, null, true);
                throw;
            }
        }

        public async Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
        {
            Stages++;
            if (BeforeStage is { } before) await before(path).ConfigureAwait(false);
            try
            {
                var staged = await inner.StageAsync(path, content, source, ct).ConfigureAwait(false);
                Trace?.Invoke("stage");
                if (StageFailureAfterCopy is { } failure) throw failure;
                return WrapStaged?.Invoke(staged) ?? staged;
            }
            catch (ItemFailedException error) when (WrapCopyFailure is { } wrap)
            {
                throw new ItemFailedException(path, wrap, error);
            }
        }

        public async Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct)
        {
            if (Side == Side.Local && expected is { IsFolder: true })
            {
                if (selection is not null) throw new ArgumentException(null, nameof(selection));
                var removed = await DeleteAsync(expected, null, ct).ConfigureAwait(false);
                if (removed.Outcome != RemovalOutcome.Removed) return removed;
                expected = null;
            }
            Publishes++;
            LastExpected = expected;
            LastSelection = selection;
            Trace?.Invoke("publish");
            if (BeforePublish is { } before) await before(staged.Path).ConfigureAwait(false);
            // The wrapper only simulates source ownership; the provider publishes the stage it was handed.
            var publishable = staged is StagedWrapper wrapper ? wrapper.Inner : staged;
            return ScriptedPublish is { } scripted
                ? scripted(staged.Path)
                : await inner.PublishAsync(publishable, expected, selection, ct).ConfigureAwait(false);
        }

        public async Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection,
            CancellationToken ct)
        {
            Deletes++;
            DeletedExpected = expected;
            DeleteSelection = selection;
            StagesAtDelete = Stages;
            if (BeforeDelete is { } before) await before(expected.Path).ConfigureAwait(false);
            return ScriptedDelete is { } scripted
                ? scripted(expected.Path)
                : await inner.DeleteAsync(expected, selection, ct).ConfigureAwait(false);
        }

        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
            inner.ReadAllAsync(under, options, ct);
        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => inner.ReadOneAsync(path, ct);
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);
        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);
        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
            inner.MoveAsync(expected, newPath, ct);
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct) =>
            inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }

    private sealed class CompletionStream(Stream inner, RequestAttempt? attempt, Action<string>? trace) : Stream
    {
        private int _completed;
        private int _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                var read = inner.Read(buffer, offset, count);
                if (read == 0) Complete(RequestResult.Success, null);
                return read;
            }
            catch (Exception error)
            {
                Complete(Classify(error), error);
                throw;
            }
        }

        public override int Read(Span<byte> buffer)
        {
            try
            {
                var read = inner.Read(buffer);
                if (read == 0) Complete(RequestResult.Success, null);
                return read;
            }
            catch (Exception error)
            {
                Complete(Classify(error), error);
                throw;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try
            {
                var read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) Complete(RequestResult.Success, null);
                return read;
            }
            catch (Exception error)
            {
                Complete(Classify(error), error);
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                trace?.Invoke("source-disposed");
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        private void Complete(RequestResult result, Exception? error)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                attempt?.TryComplete(result, error, null, true);
        }
    }

    private sealed class FailedReadStream(Exception error) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw error;
        public override long Position { get => throw error; set => throw error; }
        public override void Flush() => throw error;
        public override int Read(byte[] buffer, int offset, int count) => throw error;
        public override int Read(Span<byte> buffer) => throw error;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromException<int>(error);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromException<int>(error);
        public override long Seek(long offset, SeekOrigin origin) => throw error;
        public override void SetLength(long value) => throw error;
        public override void Write(byte[] buffer, int offset, int count) => throw error;
    }

    private sealed class GatedStream(Stream inner, int prefix, Task gate) : Stream
    {
        private long _position;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= prefix) gate.GetAwaiter().GetResult();
            var max = _position < prefix ? (int)Math.Min((long)count, prefix - _position) : count;
            var read = inner.Read(buffer, offset, max);
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position >= prefix) await gate.WaitAsync(ct).ConfigureAwait(false);
            var max = _position < prefix ? (int)Math.Min((long)buffer.Length, prefix - _position) : buffer.Length;
            var read = await inner.ReadAsync(buffer[..max], ct).ConfigureAwait(false);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CancelAfterStream(Stream inner, int prefix, CancellationTokenSource cancellation) : Stream
    {
        private long _position;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= prefix) Cancel();
            var max = _position < prefix ? (int)Math.Min((long)count, prefix - _position) : count;
            var read = inner.Read(buffer, offset, max);
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position >= prefix) Cancel();
            var max = _position < prefix ? (int)Math.Min((long)buffer.Length, prefix - _position) : buffer.Length;
            var read = await inner.ReadAsync(buffer[..max], ct).ConfigureAwait(false);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private void Cancel()
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class StagedWrapper(IStaged inner, bool consumesSourceOnPublish) : IStaged
    {
        public IStaged Inner => inner;
        public string Path => inner.Path;
        public bool ConsumesSourceOnPublish { get; } = consumesSourceOnPublish;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
