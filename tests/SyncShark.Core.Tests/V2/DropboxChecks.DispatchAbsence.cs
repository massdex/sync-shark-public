namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    // Provider-mechanics fixtures supply explicit authority; production queue defaults remain Unknown.
    // Keep the original recorder so checkpoint/order assertions still exercise the same code.
    private static async Task AuthorizedFixtureDeleteAsync(IEndpointWriter writer, SyncAction action, CancellationToken ct)
    {
        var previous = ProviderEffectContext.Current.Value;
        try
        {
            ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint => previous is null
                ? Task.CompletedTask : previous.RecordAsync(checkpoint.Kind, checkpoint.Id))
            { Action = action, RevalidateDelete = () => Task.FromResult(new AbsenceObservation(true, null)) };
            await writer.DeleteAsync(action, ct);
        }
        finally { ProviderEffectContext.Current.Value = previous; }
    }

    private static async Task DispatchAbsenceAsync()
    {
        await PacedFolderDeleteAsync();
        await MissingDispatchProofAsync();
        await SymmetricMoveOutSqliteAsync();
        foreach (var scenario in new[] { "one", "all", "unknown", "requeue", "paced-one", "paced-all" })
        {
            var clock = new global::ResilienceChecks.Clock();
            var limiter = global::ResilienceChecks.Limiter(clock);
            using var h = new Harness(limiter: limiter);
            var folder = Path.Combine(_root, "dispatch-" + scenario + "-" + Guid.NewGuid().ToString("N"));
            var local = new FakeEndpoint(Side.A, "local") { DropboxHashes = true };
            var source = new RecordingWriter(local);
            var settings = V2Settings.Default with { DeleteBatchSize = 3, DeleteBatchMaxAge = TimeSpan.FromSeconds(1) };
            var remote = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(folder, "spool"), isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
            var target = new QueuedDeleteWriter(remote, 3);
            var actions = new List<SyncAction>();
            var pair = new PairDef("dispatch", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
                Root(), [], [], true);
            using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                store.SavePair(pair);
                for (var i = 0; i < 3; i++)
                {
                    var path = "file-" + i;
                    var original = local.Seed(path, "same");
                    h.Wire.SeedFile(path, "same");
                    var victim = (await remote.ObserveAsync(path, default))!;
                    var adopt = new SyncAction("adopt-" + i, 12, ActionKind.Adopt, Side.B, path, null,
                        original, victim, false, "key-" + i);
                    store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, original, victim, null, null));
                    await local.DeleteAsync(new("remove-" + i, 6, ActionKind.Delete, Side.A, path, null, null, original, false, null), default);
                    actions.Add(new("delete-" + i, 6, ActionKind.Delete, Side.B, path, null, null, victim, true, "key-" + i));
                }
                var binding = Binding().Normalize();
                var gate = DropboxDeleteJobGate.For(binding.ConnectionId, binding.RootNamespaceId);
                gate.Throttled(); gate.Throttled();
                await gate.EnterAsync();
                var executor = new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
                var running = CollectAsync();
                try
                {
                    await target.Queued.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    Require(!h.Wire.Calls.Contains("/2/files/delete_batch"), "job gate blocks submission after initial absence admission");
                    if (scenario.StartsWith("paced-", StringComparison.Ordinal))
                    {
                        limiter.Resources.ProviderThrottled(new("dropbox:" + binding.ConnectionId,
                            SyncProviderOperation.Write), TimeSpan.FromMinutes(1));
                        clock.BeforeTimer = () =>
                        {
                            clock.BeforeTimer = null;
                            clock.Advance(TimeSpan.FromMinutes(1));
                            for (var i = 0; i < (scenario == "paced-all" ? 3 : 1); i++)
                                local.Seed("file-" + i, "recreated during transport pacing");
                        };
                    }
                    else if (scenario == "unknown") source.AbsenceUnknown = true;
                    else if (scenario == "requeue")
                    {
                        h.Wire.DeleteWriteRefusals["file-0"] = 1;
                        h.Wire.AfterDeleteWriteRefusal = () => local.Seed("file-0", "recreated after refusal");
                    }
                    else for (var i = 0; i < (scenario == "all" ? 3 : 1); i++) local.Seed("file-" + i, "recreated");
                }
                finally { gate.Exit(false); }
                var results = await running.WaitAsync(TimeSpan.FromSeconds(30));
                if (scenario.StartsWith("paced-", StringComparison.Ordinal))
                    Require(clock.BeforeTimer is null, "source recreation occurred inside the transport pacing wait");
                var blocked = scenario is "one" or "requeue" or "paced-one" ? 1 : 3;
                Require(results.Count(r => r.DeleteSourceUnconfirmed) == blocked,
                    "dispatch returns the existing source guard result for present and unknown sources");
                Require(h.Wire.DeleteBatchSizes.Sum() == 3 - blocked + (scenario == "requeue" ? 1 : 0),
                    "only still-absent members are posted; an entirely blocked batch sends nothing");
                if (scenario == "requeue")
                    Require(h.Wire.DeleteWriteRefusals["file-0"] == 0 && h.Wire.DeleteBatchSizes.Count == 1,
                        "definite refusal is requeued but fresh source blocks any second POST");
                Require(await remote.ObserveAsync("file-0", default) is not null,
                    "recreated or unconfirmed source keeps its remote survivor");

                async Task<List<ActionResult>> CollectAsync()
                {
                    var results = new List<ActionResult>();
                    await foreach (var result in executor.ApplyAsync(new(pair.PairId, 1, actions, []), source, target, default))
                        results.Add(result);
                    return results;
                }
            }
            using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            Require(reopened.SyncRetryAt(pair.PairId, "file-0") is not null &&
                reopened.Baselines(pair.PairId).Any(b => b.ObjectKey == "key-0") &&
                reopened.UnresolvedIntents(pair.PairId).Count == 0,
                "cold reopen retains blocked correspondence and retry but retires definitely unsubmitted intent");
            if (scenario.StartsWith("paced-", StringComparison.Ordinal))
                Require(reopened.OpenNotices(pair.PairId).Count(notice => notice.Path == "file-0") == 1,
                    "dispatch refusal retains exactly one notice for the refused item after cold reopen");
        }
    }

    private static async Task PacedFolderDeleteAsync()
    {
        foreach (var sourceReturns in new[] { true, false })
        {
            var clock = new global::ResilienceChecks.Clock();
            var limiter = global::ResilienceChecks.Limiter(clock);
            using var h = new Harness(limiter: limiter);
            var local = new FakeEndpoint(Side.A, "local");
            var original = local.Seed("small", "", ItemKind.Folder);
            h.Wire.SeedFolder("small");
            var remote = h.Writer();
            var victim = (await remote.ObserveAsync("small", default))!;
            var pair = new PairDef("folder-dispatch", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
                Root(), [], [], true);
            var statePath = Path.Combine(_root, "small-folder-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(statePath, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            store.SavePair(pair);
            var adopt = new SyncAction("adopt-folder", 12, ActionKind.Adopt, Side.B, "small", null,
                original, victim, false, "folder-key");
            store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, original, victim, null, null));
            await local.DeleteAsync(new("remove-folder", 23, ActionKind.Delete, Side.A, "small", null,
                null, original, false, null), default);
            limiter.Resources.ProviderThrottled(new("dropbox:" + Binding().Normalize().ConnectionId,
                SyncProviderOperation.Read), TimeSpan.FromMinutes(1));
            clock.BeforeTimer = () =>
            {
                clock.BeforeTimer = null;
                clock.Advance(TimeSpan.FromMinutes(1));
                if (sourceReturns) local.Seed("small", "", ItemKind.Folder);
                else h.Wire.SeedFile("small/child.txt", "must survive");
            };
            var action = new SyncAction("delete-folder", 23, ActionKind.Delete, Side.B, "small", null,
                null, victim, false, "folder-key") { PreserveIgnoredChildren = true };
            var results = new List<ActionResult>();
            var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission(),
                isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            await foreach (var result in executor.ApplyAsync(new(pair.PairId, 2, [action], []), local, remote, default))
                results.Add(result);
            Require(clock.BeforeTimer is null && !h.Wire.Calls.Contains("/2/files/delete_v2"),
                "folder source/contents change during transport pacing prevents the delete request");
            Require(results.Single().Outcome == Outcome.Skipped &&
                (!sourceReturns || results.Single().DeleteSourceUnconfirmed), "folder refusal uses the existing result semantics");
            Require(await remote.ObserveAsync("small", default) is not null &&
                (sourceReturns || await remote.ObserveAsync("small/child.txt", default) is not null),
                "the folder and its new child survive the refused delete");
        }
    }

    private static async Task MissingDispatchProofAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFile("unproved", "keep");
        var victim = (await writer.ObserveAsync("unproved", default))!;
        var previous = ProviderEffectContext.Current.Value;
        try
        {
            ProviderEffectContext.Current.Value = null;
            var refused = false;
            try { await writer.DeleteAsync(DeleteAction(victim.Path, victim), default); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused && !h.Wire.Calls.Contains("/2/files/delete_batch") &&
                await writer.ObserveAsync("unproved", default) is not null,
                "missing dispatch callback is Unknown and never submits a delete");
            h.Wire.SeedFolder("unproved-folder");
            var folder = (await writer.ObserveAsync("unproved-folder", default))!;
            await ExpectAsync<InvalidOperationException>(() => writer.DeleteAsync(DeleteAction(folder.Path, folder), default));
            Require(!h.Wire.Calls.Contains("/2/files/delete_v2") &&
                await writer.ObserveAsync(folder.Path, default) is not null,
                "missing folder action context never submits a delete");
        }
        finally { ProviderEffectContext.Current.Value = previous; }
    }

    private static async Task SymmetricMoveOutSqliteAsync()
    {
        // Recorded decision-table row 21 applies equally to both endpoint kinds.
        foreach (var sourceSide in new[] { Side.A, Side.B })
        {
            using var h = new Harness();
            var setup = await LocalWriterChecks.SetupAsync("move-out-" + sourceSide);
            var local = await LocalWriter.OpenAsync(LocalChecks.RootOf(setup.Root, "local"), Side.A,
                "move-out", LocalChecks.InstallationId, setup.Recovery, default, isStateDamage: StorageFailureClassifier.IsStateDamage);
            var remote = h.Writer();
            LocalChecks.Seed(setup.Root, "item", "same");
            h.Wire.SeedFile("item", "same");
            var a = (await local.ObserveAsync("item", default))!;
            var b = (await remote.ObserveAsync("item", default))!;
            var folder = Path.Combine(Path.GetDirectoryName(setup.Root)!, "state");
            using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                store.SavePair(new PairDef("move-out", "Pair A", LocalChecks.RootOf(setup.Root, "local"), Root(), [], [], true));
                var adopt = new SyncAction("adopt", 12, ActionKind.Adopt, Side.B, "item", null, a, b, false, "key");
                store.CommitResult("move-out", 1, adopt, new("adopt", Outcome.Done, a, b, null, null));
                if (sourceSide == Side.A) File.Move(Path.Combine(setup.Root, "item"), Path.Combine(Path.GetDirectoryName(setup.Root)!, "outside"));
                else h.Wire.MoveOutsideRoot("item", "Elsewhere/item");
                var target = sourceSide == Side.A ? Side.B : Side.A;
                var action = new SyncAction("delete", 6, ActionKind.Delete, target, "item", null, null,
                    target == Side.A ? a : b, true, "key");
                var results = new List<ActionResult>();
                await foreach (var result in new Executor(store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException)
                    .ApplyAsync(new("move-out", 2, [action], []), local, remote, default)) results.Add(result);
                Require(results.Single().Outcome == Outcome.Done && results.Single().Recovery is not null,
                    "confirmed move-out propagates recoverable removal on either side: " + results.Single().Reason);
            }
            using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            Require(reopened.Baselines("move-out").Count == 0, "move-out correspondence retires only after recoverable success");
        }
    }

    private sealed class QueuedDeleteWriter(IEndpointWriter inner, int expected) : IEndpointWriter
    {
        private int _queued;
        internal TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DeleteAsync(SyncAction action, CancellationToken ct)
        {
            var pending = inner.DeleteAsync(action, ct);
            if (Interlocked.Increment(ref _queued) == expected) Queued.TrySetResult();
            return pending;
        }
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) => inner.PutAsync(action, content, ct);
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => inner.PreserveAsync(victim, actionId, ct);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState source, CancellationToken ct) => inner.OpenReadAsync(source, ct);
    }
}
