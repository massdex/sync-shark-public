using System.Collections.Concurrent;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Focused SQLite cold-reopen checks for D2/C12/C13: a generic RecoveryFirst effect that fails or is cancelled
/// after the archive, and an interrupted carried-ignored child cleanup whose retained copies stay discoverable.
/// </summary>
internal static class RecoveryFirstRestartChecks
{
    private const string PairId = LocalChecks.PairId;

    public static async Task RunAsync()
    {
        await RunChecks.HistoricalRecoveryAuthorityAsync().ConfigureAwait(false);
        await RunChecks.LocalStubRecoveryPolicyAsync().ConfigureAwait(false);
        await LocalOverwriteRestoresThroughCliAsync().ConfigureAwait(false);
        await GenericFailureColdReopenAsync().ConfigureAwait(false);
        await CancelledEffectColdReopenAsync().ConfigureAwait(false);
        await ThrottledAfterPreserveStaysProtectiveAsync().ConfigureAwait(false);
        await InterruptedChildCleanupRestoresAsync().ConfigureAwait(false);
        await FolderVictimNeedsNoReceiptAsync().ConfigureAwait(false);
        Console.WriteLine("v2 recovery-first restart checks passed.");
    }

    private static async Task LocalOverwriteRestoresThroughCliAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            var fixture = NewFixture("local-overwrite-" + side);
            var otherPath = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "other");
            Directory.CreateDirectory(otherPath);
            var other = LocalChecks.RootOf(otherPath, "root-other");
            var pair = fixture.Pair with { B = other };
            using var store = SqliteStateStore.Open(fixture.StatePath, V2Settings.Default, TimeProvider.System);
            store.SavePair(pair);
            var generation = store.BeginGeneration(pair.A.RootId, "fixture");
            var a = await OpenWriterAsync(fixture).ConfigureAwait(false);
            var b = await LocalWriter.OpenAsync(other, Side.B, PairId, LocalChecks.InstallationId,
                fixture.Recovery, CancellationToken.None).ConfigureAwait(false);
            LocalChecks.Seed(fixture.Root, "file.txt", "old bytes");
            LocalChecks.Seed(otherPath, "file.txt", "old bytes");
            var oldA = (await a.ObserveAsync("file.txt", default))!;
            var oldB = (await b.ObserveAsync("file.txt", default))!;
            LocalChecks.Seed(side == Side.A ? otherPath : fixture.Root, "file.txt", "new replacement bytes");
            var nowA = (await a.ObserveAsync("file.txt", default))!;
            var nowB = (await b.ObserveAsync("file.txt", default))!;
            var row = new PlanRow(new Baseline(PairId, "object-key", oldA, oldB, generation), nowA, nowB,
                new Evidence(false, false, false, false));
            var options = new PlanOptions(TimeSpan.Zero, false,
                new LabelRule("{name}-{side}{ext}", "yyyyMMdd", TimeSpan.Zero, "A", "B"), 10)
                { ProviderA = SyncProviders.Local, ProviderB = SyncProviders.Local };
            var plan = Planner.Decide(PairId, generation, [row], options);
            var action = plan.Actions.Single();
            LocalChecks.Require(action is { Kind: ActionKind.Update, RecoveryFirst: true } && action.Target == side,
                "local/local ordinary edit plans indexed Recovery on either target");
            var cloudPlan = Planner.Decide(PairId, generation, [row], options with
                { ProviderA = SyncProviders.Dropbox, ProviderB = SyncProviders.Dropbox });
            LocalChecks.Require(cloudPlan.Actions.Single() is { Kind: ActionKind.Update, RecoveryFirst: false },
                "ordinary Dropbox overwrite still relies on provider version history");
            // Direct/older callers cannot bypass the same owner by supplying an unannotated Update.
            plan = plan with { Actions = [action with { RecoveryFirst = false }] };
            var indexedBeforeRemoval = false;
            SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = _ => indexedBeforeRemoval =
                store.Recoveries(PairId).Any(record => record.ActionId == action.ActionId);
            var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission());
            try
            {
                await foreach (var result in executor.ApplyAsync(plan, a, b, default))
                    LocalChecks.Require(result is { Outcome: Outcome.Done, Recovery: not null },
                        "ordinary overwrite returns its retained original");
            }
            finally { SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = null; }
            LocalChecks.Require(indexedBeforeRemoval, "old bytes have an indexed receipt before removal");
            var receipt = store.Recoveries(PairId).Single();
            var provider = new FakeEndpointProvider().AddWriter(pair.A, a).AddWriter(pair.B, b);
            // Keep native proof real, but do not let machine activity/power pause this deterministic fixture.
            provider.AddInventory(pair.A, new LocalInventory(pair.A, Side.A, store, new HashSet<string>(), V2Settings.Default, TimeProvider.System)
                { GapIo = new LocalGapIo(store, V2Settings.Default, new HashSet<string>(), () => false) });
            provider.AddInventory(pair.B, new LocalInventory(pair.B, Side.B, store, new HashSet<string>(), V2Settings.Default, TimeProvider.System)
                { GapIo = new LocalGapIo(store, V2Settings.Default, new HashSet<string>(), () => false) });
            var clock = new RetryClock { Now = DateTimeOffset.UtcNow };
            var run = new SyncRun(store, executor, provider, V2Settings.Default, clock);
            store.MarkFirstSyncDone(PairId);
            var sourceWriter = new FailingLocalDownload(side == Side.A ? b : a);
            provider.AddWriter(side == Side.A ? pair.B : pair.A, sourceWriter);
            LocalChecks.Seed(side == Side.A ? otherPath : fixture.Root, "file.txt", "next replacement bytes");
            await run.IncrementalAsync(PairId, new RecordingProgress(), default);
            var targetRoot = side == Side.A ? fixture.Root : otherPath;
            LocalChecks.Require(File.ReadAllText(Path.Combine(targetRoot, "file.txt")) == "new replacement bytes" &&
                store.Recoveries(PairId).Count == 1 && sourceWriter.Downloads > 0,
                "production run source failure leaves target intact without consuming a Recovery receipt");
            sourceWriter.Fail = false;
            var pendingRetry = store.SyncRetryAt(PairId, "file.txt");
            LocalChecks.Require(pendingRetry is { Attempts: > 0 } && pendingRetry.RetryAfter > clock.Now,
                "failed source retains a durable bounded retry deadline");
            clock.Now = pendingRetry!.RetryAfter.AddTicks(1);
            await run.IncrementalAsync(PairId, new RecordingProgress(), default);
            LocalChecks.Require(File.ReadAllText(Path.Combine(targetRoot, "file.txt")) == "next replacement bytes",
                "production run retries the failed source without manual Recovery");
            var cli = new V2CliRuntime(null, store, run, new EndpointRootProbe(provider), fixture.Root);
            var invocation = SyncCliRegistry.Parse(["--cli", "recovery", "restore", "--pair", PairId,
                "--action-id", receipt.ActionId, "--as-copy", "--yes"]);
            var restored = (SyncCliRecoveryRestorePageData)await cli.RestoreRecoveryCommandAsync(invocation, default);
            var item = restored.Items.Single();
            LocalChecks.Require(item.Classification == "RESTORED", "existing Recovery CLI restores the old version");
            LocalChecks.Require(File.ReadAllText(Path.Combine(targetRoot, item.Destination!)) == "old bytes" &&
                File.ReadAllText(Path.Combine(targetRoot, "file.txt")) == "next replacement bytes",
                "restore-as-copy keeps the replacement and recovers the exact old bytes");
            store.Dispose();
            using var reopened = SqliteStateStore.Open(fixture.StatePath, V2Settings.Default, TimeProvider.System);
            var coldReceipt = reopened.Recoveries(PairId).Single(record => record.RecoveryId == receipt.RecoveryId);
            await using var archived = await ((IRecoveryRecordReader)(side == Side.A ? a : b))
                .OpenRecoveryRecordAsync(coldReceipt, default);
            using var text = new StreamReader(archived);
            LocalChecks.Require(await text.ReadToEndAsync() == "old bytes", "cold SQLite reopen retains exact original provenance");
        }
    }

    private sealed class RetryClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FailingLocalDownload(LocalWriter inner) : IEndpointWriter, ISyncDirectDownloadSource
    {
        internal bool Fail { get; set; } = true;
        internal int Downloads { get; private set; }
        public Task<SyncDownloadResult> DownloadToAsync(ItemState source, Stream destination, CancellationToken ct)
        {
            Downloads++;
            if (Fail) throw new IOException("simulated local source failure before verification");
            return inner.DownloadToAsync(source, destination, ct);
        }
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => inner.PreserveAsync(victim, actionId, ct);
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) => inner.PutAsync(action, content, ct);
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => inner.DeleteAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState source, CancellationToken ct) => inner.OpenReadAsync(source, ct);
    }

    private sealed record Fixture(string Root, string StatePath, string Recovery, RootDef Local, PairDef Pair);

    private static Fixture NewFixture(string name)
    {
        var root = LocalChecks.FreshRoot(name);
        var parent = Path.GetDirectoryName(root)!;
        var local = LocalChecks.RootOf(root, "root-local");
        var pair = new PairDef(PairId, name, local,
            new RootDef("root-b", SyncProviders.Dropbox, "acc", "/b", "id:root-b"), [], [], true);
        return new Fixture(root, Path.Combine(parent, "state"), Path.Combine(parent, "recovery"), local, pair);
    }

    private static async Task<LocalWriter> OpenWriterAsync(Fixture fixture,
        IReadOnlyList<SyncIgnoreRule>? rules = null) =>
        await LocalWriter.OpenAsync(fixture.Local, Side.A, PairId, LocalChecks.InstallationId, fixture.Recovery,
            CancellationToken.None, ignoreRules: rules).ConfigureAwait(false);

    private static async Task GenericFailureColdReopenAsync() =>
        await GenericColdReopenAsync("recovery-first-generic-failure", cancelSourceRead: false).ConfigureAwait(false);

    private static async Task CancelledEffectColdReopenAsync() =>
        await GenericColdReopenAsync("recovery-first-cancelled", cancelSourceRead: true).ConfigureAwait(false);

    // C12: the generic streaming path into a local target archives the displaced target before it reads the source.
    // A source failure or cancellation must leave the archive indexed before removal, and a cold reconcile must stay
    // protective. (The source is a generic endpoint, not a direct-download source, so this is not the deferred path.)
    private static async Task GenericColdReopenAsync(string name, bool cancelSourceRead)
    {
        var fixture = NewFixture(name);
        var store = SqliteStateStore.Open(fixture.StatePath, V2Settings.Default, TimeProvider.System);
        long generation = 0;
        try
        {
            store.SavePair(fixture.Pair);
            generation = store.BeginGeneration(fixture.Local.RootId, "fixture");
            var writer = await OpenWriterAsync(fixture).ConfigureAwait(false);
            LocalChecks.Seed(fixture.Root, "file.txt", "original-bytes");
            var victim = (await writer.ObserveAsync("file.txt", CancellationToken.None).ConfigureAwait(false))!;
            var planned = LocalChecks.Bytes("planned-new-bytes");
            var source = new ItemState("file.txt", ItemKind.File, "id:local:00000002:0000000000000002",
                "source-revision", LocalChecks.BlockHash(planned), planned.Length,
                new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero), ItemFlags.None);
            var action = new SyncAction("act-generic-" + (cancelSourceRead ? "cancel" : "fail"), 2, ActionKind.Update,
                Side.A, "file.txt", null, source, victim, true, null);
            var rowAtRemoval = false;
            SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = _ => rowAtRemoval =
                store.Recoveries(PairId).Any(row => string.Equals(row.ActionId, action.ActionId, StringComparison.Ordinal));
            ActionResult? result;
            var previous = ProviderEffectContext.Current.Value;
            try
            {
                var committer = new StoreCommitter(store, new SemaphoreSlim(1, 1), PairId, generation);
                ProviderEffectContext.Current.Value = committer.CreateProviderContext(action);
                using var cancel = new CancellationTokenSource();
                var runner = new ActionRunner(committer, V2Settings.Default, ExecutorChecks.Admission(), PairId,
                    writer, new FailingSource(cancelSourceRead ? cancel : null), [],
                    new ConcurrentDictionary<string, Outcome>());
                result = await runner.RunAsync(action, cancel.Token).ConfigureAwait(false);
            }
            finally
            {
                ProviderEffectContext.Current.Value = previous;
                SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = null;
            }

            LocalChecks.Require(rowAtRemoval,
                name + ": the Recovery row is durable before the original's removal");
            if (cancelSourceRead)
                LocalChecks.Require(result is null,
                    name + ": a cancelled caller commits no result; the durable row is the only receipt");
            else
                LocalChecks.Require(result is { Outcome: Outcome.Failed, Recovery: not null },
                    name + ": a generic source failure is Failed and carries the retained copy");
            LocalChecks.Require(!File.Exists(Path.Combine(fixture.Root, "file.txt")),
                name + ": the archived original was removed from the target");
            var archive = Path.Combine(fixture.Recovery, ".syncshark-recovery-" + PairId,
                LocalChecks.OperationId(action.ActionId) + ".recovery");
            LocalChecks.Require(File.Exists(archive),
                name + ": the retained copy exists at the action's exact operation path");
            LocalChecks.Require(store.UnresolvedIntents(PairId).Any(intent =>
                    string.Equals(intent.ActionId, action.ActionId, StringComparison.Ordinal)),
                name + ": the intent stays durable for reconciliation");
        }
        finally { store.Dispose(); }

        using var reopened = SqliteStateStore.Open(fixture.StatePath, V2Settings.Default, TimeProvider.System);
        var reopenedWriter = await OpenWriterAsync(fixture).ConfigureAwait(false);
        var intents = reopened.UnresolvedIntents(PairId);
        LocalChecks.Require(intents.Count == 1, name + ": the cold store still holds exactly the interrupted intent");
        var receipt = reopened.Recoveries(PairId).Single(row =>
            string.Equals(row.ActionId, intents[0].ActionId, StringComparison.Ordinal));
        await using (var archived = await ((IRecoveryRecordReader)reopenedWriter)
            .OpenRecoveryRecordAsync(receipt, CancellationToken.None).ConfigureAwait(false))
        {
            using var memory = new MemoryStream();
            await archived.CopyToAsync(memory).ConfigureAwait(false);
            LocalChecks.Require(memory.ToArray().SequenceEqual(LocalChecks.Bytes("original-bytes")),
                name + ": the cold store's receipt opens the exact original bytes");
        }
        var counted = new PreserveCounter(reopenedWriter);
        var reconciled = (await new Executor(reopened, V2Settings.Default, ExecutorChecks.Admission())
            .ReconcileAsync(intents, generation, counted, new FailingSource(), CancellationToken.None)
            .ConfigureAwait(false)).Single();
        LocalChecks.Require(reconciled.Outcome == Outcome.Uncertain && reconciled.Recovery is not null,
            name + ": an absent target with our archive must stay protective, never close as superseded: " +
            reconciled.Outcome + " " + reconciled.Reason);
        LocalChecks.Require(reopened.UnresolvedIntents(PairId).Any(intent =>
                string.Equals(intent.ActionId, intents[0].ActionId, StringComparison.Ordinal)),
            name + ": the protective intent remains unresolved so a later plan cannot read this as a user deletion");
        LocalChecks.Require(counted.Calls == 0,
            name + ": reconciliation used the receipt only and never preserved again");
    }

    // C13: an interrupted carried-ignored cleanup has already removed a child. Its Recovery row must exist before
    // removal, survive a cold store reopen, and remain restorable through the existing Recovery owner.
    private static async Task InterruptedChildCleanupRestoresAsync()
    {
        const string name = "recovery-first-child-cleanup";
        var fixture = NewFixture(name);
        var rules = new[]
        {
            new SyncIgnoreRule(SyncIgnoreKind.FileExtension, "tmp"),
            new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini")
        };
        var store = SqliteStateStore.Open(fixture.StatePath, V2Settings.Default, TimeProvider.System);
        RecoveryRecord? child = null;
        try
        {
            store.SavePair(fixture.Pair);
            var generation = store.BeginGeneration(fixture.Local.RootId, "full");
            Directory.CreateDirectory(Path.Combine(fixture.Root, "folder"));
            LocalChecks.Seed(fixture.Root, "folder/only.tmp", new string('a', 70 * 1024));
            LocalChecks.Seed(fixture.Root, "folder/desktop.ini", "small ignored shell metadata");
            var snapshot = await LocalChecks.Inventory(fixture.Root, store, TimeProvider.System)
                .FullAsync(generation, _ => true, new Progress<string>(), CancellationToken.None).ConfigureAwait(false);
            store.WriteSnapshot(snapshot);
            store.CompleteGeneration(generation, snapshot.CursorAfter, true);
            var folder = snapshot.Items.Single(item => item.Path == "folder");
            var action = new SyncAction("act-carried-cleanup", 6, ActionKind.Delete, Side.A, "folder", null, null,
                folder, false, null);
            var interrupted = false;
            SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = _ =>
            {
                if (interrupted) return;
                interrupted = true;
                throw new IOException("simulated interruption after the first carried child was removed");
            };
            var writer = await OpenWriterAsync(fixture, rules).ConfigureAwait(false);
            var previous = ProviderEffectContext.Current.Value;
            try
            {
                var committer = new StoreCommitter(store, new SemaphoreSlim(1, 1), PairId, generation);
                var context = committer.CreateProviderContext(action);
                // This mechanical cleanup fixture authorizes this exact delete while retaining the real
                // committer owner that durably indexes the carried child's archive before removal.
                context.RevalidateDelete = () => Task.FromResult(new AbsenceObservation(true, null));
                ProviderEffectContext.Current.Value = context;
                try { await writer.DeleteAsync(action, CancellationToken.None).ConfigureAwait(false); }
                catch (IOException) { /* the bound interruption above */ }
            }
            finally
            {
                ProviderEffectContext.Current.Value = previous;
                SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = null;
            }

            LocalChecks.Require(interrupted && !File.Exists(Path.Combine(fixture.Root, "folder", "only.tmp")),
                name + ": the interrupted cleanup removed the first carried child");
            LocalChecks.Require(Directory.Exists(Path.Combine(fixture.Root, "folder")),
                name + ": the enclosing folder was not removed after the interruption");
            var rows = store.Recoveries(PairId);
            child = rows.Single(row => string.Equals(row.Path, "folder/only.tmp", StringComparison.OrdinalIgnoreCase));
            LocalChecks.Require(child.Side == Side.A && child.Size == 70 * 1024 && child.Hash.Length == 64,
                name + ": the child's Recovery row names its exact side, size and content hash");
            LocalChecks.Require(rows.All(row => !string.Equals(row.Path, "folder/desktop.ini",
                    StringComparison.OrdinalIgnoreCase)),
                name + ": the decision-80 small FileName child keeps no archive row");
        }
        finally { store.Dispose(); }

        using var reopened = SqliteStateStore.Open(fixture.StatePath, V2Settings.Default, TimeProvider.System);
        var reopenedWriter = await OpenWriterAsync(fixture, rules).ConfigureAwait(false);
        var records = reopened.Recoveries(PairId);
        LocalChecks.Require(records.Count == 1 &&
            string.Equals(records[0].RecoveryId, child!.RecoveryId, StringComparison.Ordinal),
            name + ": the cold store still holds the carried child's Recovery row");
        var opposite = new FakeEndpoint(Side.B, fixture.Pair.B.RootId);
        var provider = new FakeEndpointProvider().Add(fixture.Pair.B, opposite).AddWriter(fixture.Pair.A, reopenedWriter);
        var run = new SyncRun(reopened, new Executor(reopened, V2Settings.Default, ExecutorChecks.Admission()), provider,
            V2Settings.Default, TimeProvider.System);
        var restored = (await run.RestoreRecoveryPageAsync(PairId,
                new RecoveryQuery(Id: child!.RecoveryId), asCopy: false, dryRun: false, CancellationToken.None)
            .ConfigureAwait(false)).Single();
        LocalChecks.Require(restored.Classification == "RESTORED",
            name + ": the interrupted child is restorable through the existing Recovery owner: " +
            restored.Classification + " " + restored.Detail);
        LocalChecks.Require(File.Exists(Path.Combine(fixture.Root, "folder", "only.tmp")) &&
            new FileInfo(Path.Combine(fixture.Root, "folder", "only.tmp")).Length == 70 * 1024 &&
            File.ReadAllText(Path.Combine(fixture.Root, "folder", "only.tmp")) == new string('a', 70 * 1024),
            name + ": the restored child holds its exact retained bytes");
    }

    // A source throttle arrives after the archive. Committing Skipped here would close the intent and let the next
    // plan read our own removal as a user deletion; the runner must settle it as protective Uncertain instead.
    private static async Task ThrottledAfterPreserveStaysProtectiveAsync()
    {
        const string name = "recovery-first-throttle";
        var fixture = NewFixture(name);
        using var store = SqliteStateStore.Open(fixture.StatePath, V2Settings.Default, TimeProvider.System);
        store.SavePair(fixture.Pair);
        var generation = store.BeginGeneration(fixture.Local.RootId, "fixture");
        var writer = await OpenWriterAsync(fixture).ConfigureAwait(false);
        LocalChecks.Seed(fixture.Root, "file.txt", "original-bytes");
        var victim = (await writer.ObserveAsync("file.txt", CancellationToken.None).ConfigureAwait(false))!;
        var planned = LocalChecks.Bytes("planned-new-bytes");
        var source = new ItemState("file.txt", ItemKind.File, "id:local:00000002:0000000000000002",
            "source-revision", LocalChecks.BlockHash(planned), planned.Length,
            new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero), ItemFlags.None);
        var action = new SyncAction("act-throttle", 2, ActionKind.Update, Side.A, "file.txt", null, source, victim,
            true, null);
        var rowAtRemoval = false;
        SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = _ => rowAtRemoval =
            store.Recoveries(PairId).Any(row => string.Equals(row.ActionId, action.ActionId, StringComparison.Ordinal));
        ActionResult? result;
        var previous = ProviderEffectContext.Current.Value;
        try
        {
            var committer = new StoreCommitter(store, new SemaphoreSlim(1, 1), PairId, generation);
            ProviderEffectContext.Current.Value = committer.CreateProviderContext(action);
            var runner = new ActionRunner(committer, V2Settings.Default, ExecutorChecks.Admission(), PairId,
                writer, new ThrottledSource(), [], new ConcurrentDictionary<string, Outcome>());
            result = await runner.RunAsync(action, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ProviderEffectContext.Current.Value = previous;
            SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = null;
        }

        LocalChecks.Require(rowAtRemoval, name + ": the real receipt exists before the original's removal");
        LocalChecks.Require(result is { Outcome: Outcome.Uncertain, Recovery: not null },
            name + ": a throttled effect after the archive stays protective, never Skipped: " + result?.Outcome);
        var archive = Path.GetFullPath(Path.Combine(fixture.Recovery, ".syncshark-recovery-" + PairId,
            LocalChecks.OperationId(action.ActionId) + ".recovery"));
        LocalChecks.Require(string.Equals(result!.Recovery!.Location, archive, StringComparison.OrdinalIgnoreCase),
            name + ": the protective result carries the real preserved reference");
        LocalChecks.Require(store.UnresolvedIntents(PairId).Any(intent =>
                string.Equals(intent.ActionId, action.ActionId, StringComparison.Ordinal)),
            name + ": the throttled item's intent remains for reconcile");
    }

    // Folder victims carry no bytes and are intentionally never archived, so a RecoveryFirst folder outcome must
    // resolve normally without any Recovery receipt (a file-only requirement).
    private static async Task FolderVictimNeedsNoReceiptAsync()
    {
        const string name = "recovery-first-folder";
        var source = new FakeEndpoint(Side.A, "ra");
        var target = new FakeEndpoint(Side.B, "rb");
        var folder = target.Seed("d", "", ItemKind.Folder, versionId: "v1");
        var mkdir = new SyncAction("act-folder-mkdir", 1, ActionKind.MkDir, Side.B, "d", null, null, folder, true, null);
        var made = await EffectResolver.ResolveAsync(mkdir, target, source, CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(made.Outcome == Outcome.Done,
            name + ": a RecoveryFirst folder MkDir resolves Done without a file receipt: " + made.Reason);
        var empty = new FakeEndpoint(Side.B, "rb2");
        var delete = new SyncAction("act-folder-delete", 32, ActionKind.Delete, Side.B, "old", null, null,
            new ItemState("old", ItemKind.Folder, "id:old", "v1", null, 0, DateTimeOffset.UnixEpoch, ItemFlags.None),
            true, null);
        var deleted = await EffectResolver.ResolveAsync(delete, empty, source, CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(deleted.Outcome == Outcome.Done,
            name + ": a RecoveryFirst rule-32 folder delete resolves Done without a file receipt: " + deleted.Reason);
    }

    // Counts PreserveAsync calls without changing behavior; reconciliation must use the indexed receipt.
    private sealed class PreserveCounter(IEndpointWriter inner) : IEndpointWriter
    {
        internal int Calls { get; private set; }
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct)
        {
            Calls++;
            return inner.PreserveAsync(victim, actionId, ct);
        }

        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) =>
            inner.PutAsync(a, content, ct);
        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => inner.MoveAsync(a, ct);
        public Task DeleteAsync(SyncAction a, CancellationToken ct) => inner.DeleteAsync(a, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct) => inner.OpenReadAsync(s, ct);
    }

    // A source read that reports a provider throttle after the target was already archived.
    private sealed class ThrottledSource : IEndpointWriter
    {
        public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct) =>
            throw new ProviderWriteThrottledException("simulated throttle after archive", new IOException("throttle"));

        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) =>
            Task.FromResult<ItemState?>(null);
    }

    // The generic source endpoint: a local-target update reads through OpenReadAsync. It fails or cancels the caller
    // exactly at that read, which is the crash-window shape after the archive and before any target write.
    private sealed class FailingSource(CancellationTokenSource? cancel = null) : IEndpointWriter
    {
        public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct)
        {
            cancel?.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new IOException("simulated generic source read failure");
        }

        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) =>
            Task.FromResult<ItemState?>(null);
    }
}
