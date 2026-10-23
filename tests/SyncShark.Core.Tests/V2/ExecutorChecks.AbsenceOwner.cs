namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task AbsenceOwnerSqliteAsync()
    {
        foreach (var scenario in new[] { "unknown", "restore", "access", "state" })
        {
            var folder = Path.Combine(Environment.GetEnvironmentVariable("SYNCSHARK_TEST_ROOT") ?? Path.GetTempPath(),
                "absence-owner-" + Guid.NewGuid().ToString("N"));
            var a = new FakeEndpoint(Side.A, "ra");
            var b = new FakeEndpoint(Side.B, "rb");
            var original = a.Seed("item", "same");
            var victim = b.Seed("item", "same");
            var action = Act(ActionKind.Delete, Side.B, "item", expected: victim, rf: true, key: "key");
            using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
            {
                store.SavePair(Pair());
                var adopt = Act(ActionKind.Adopt, Side.B, "item", source: original, expected: victim, key: "key", rule: 12);
                store.CommitResult("p1", 1, adopt, new(adopt.ActionId, Outcome.Done, original, victim, null, null));
                await RemoveSourceAsync(a, "item");
                var source = new AbsenceFaultWriter(a, scenario);
                var target = new RecordingWriter(b);
                if (scenario == "restore")
                    store.SaveSyncRetry("p1", new("item", SqliteStateStore.InterruptedDeleteRestoreKind, 0,
                        "restore pending", DateTimeOffset.MinValue));
                var halted = false;
                try
                {
                    var results = await Apply(new Executor(store, Settings(2), Admission()), Plan(action), source, target);
                    Require(results.Single().DeleteSourceUnconfirmed, "unknown or pending restoration blocks delete");
                }
                catch (EndpointAccessException) when (scenario == "access") { halted = true; }
                catch (StateDamagedException) when (scenario == "state") { halted = true; }
                Require(halted == (scenario is "access" or "state"), "authority/state loss escapes instead of item retries");
                Require(target.Deletes == 0 && target.Preserves == 0, "no destructive effect with unconfirmed authority");
                if (scenario == "restore")
                {
                    store.RecordIntent("p1", action);
                    await RemoveSourceAsync(b, "item");
                    var result = await new Executor(store, Settings(2), Admission()).ReconcileAsync([action], 2, a, b, default);
                    Require(result.Single().Outcome == Outcome.Uncertain, "crash reconcile shares pending-restore guard");
                }
            }
            using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
            Require(reopened.Baselines("p1").Count == 1, "blocked absence preserves correspondence across reopen");
            if (scenario is "access" or "state")
                Require(reopened.SyncRetryAt("p1", "item") is null, "authority loss writes no per-item retry debt");
        }
    }

    private static async Task LocalMissingDispatchProofAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("local-no-proof");
        LocalChecks.Seed(setup.Root, "item", "keep");
        var victim = (await setup.Writer.ObserveAsync("item", default))!;
        var action = Act(ActionKind.Delete, Side.A, "item", expected: victim);
        var previous = ProviderEffectContext.Current.Value;
        try
        {
            foreach (var mode in new[] { "missing", "mismatch", "no-callback" })
            {
                ProviderEffectContext.Current.Value = mode == "missing" ? null : new ProviderEffectContext(_ => Task.CompletedTask)
                {
                    Action = mode == "mismatch" ? action with { ActionId = "other" } : action,
                    RevalidateDelete = mode == "no-callback" ? null : () => Task.FromResult(new AbsenceObservation(true, null))
                };
                var refused = false;
                try { await setup.Writer.DeleteAsync(action, default); }
                catch (DeleteSourceUnconfirmedException) { refused = true; }
                Require(refused && File.Exists(Path.Combine(setup.Root, "item")), "missing/mismatched local authority is Unknown");
            }
        }
        finally { ProviderEffectContext.Current.Value = previous; }
    }

    private static async Task LocalDispatchAbsenceSqliteAsync()
    {
        await LocalMissingDispatchProofAsync();
        foreach (var window in new[] { "intent", "archive" })
        {
            var setup = await LocalWriterChecks.SetupAsync("local-dispatch-" + window);
            LocalChecks.Seed(setup.Root, "item", "same");
            var victim = (await setup.Writer.ObserveAsync("item", default))!;
            // The native local writer uses Dropbox block hashes, not the fake's default SHA-256.
            var b = new FakeEndpoint(Side.B, "rb") { DropboxHashes = true };
            var original = b.Seed("item", "same");
            Require(Content.SameContent(victim, original) == true,
                "native local and fake remote seed equal known content before baseline adoption");
            var source = new RecordingWriter(b);
            var folder = Path.Combine(Path.GetDirectoryName(setup.Root)!, "state");
            using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
            {
                store.SavePair(Pair());
                var adopt = Act(ActionKind.Adopt, Side.B, "item", source: victim, expected: original, key: "key", rule: 12);
                store.CommitResult("p1", 1, adopt, new(adopt.ActionId, Outcome.Done, victim, original, null, null));
                await RemoveSourceAsync(b, "item");
                var action = Act(ActionKind.Delete, Side.A, "item", expected: victim, rf: true, key: "key");
                var recording = new RecordingStore(store) { AfterIntent = _ =>
                { if (window == "intent") source.AbsenceUnknown = true; } };
                try
                {
                    if (window == "archive") SyncLocalRecovery.RecoveryCreatedBeforeBindingForTest = () => source.AbsenceUnknown = true;
                    var results = await Apply(new Executor(recording, Settings(2), Admission()), Plan(action), setup.Writer, source);
                    Require(results.Single().DeleteSourceUnconfirmed && File.Exists(Path.Combine(setup.Root, "item")),
                        "source becoming unknown during intent/archive wait cannot remove local survivor");
                }
                finally { SyncLocalRecovery.RecoveryCreatedBeforeBindingForTest = null; }
            }
            using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
            Require(reopened.Baselines("p1").Count == 1 && reopened.SyncRetryAt("p1", "item") is not null,
                "local dispatch refusal retains SQLite correspondence and retry after reopen");
        }
    }

    private sealed class AbsenceFaultWriter(IEndpointWriter inner, string scenario) : IEndpointWriter, ISourceAbsenceProbe
    {
        public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct) => scenario switch
        {
            "access" => throw new EndpointAccessException("Reconnect test endpoint"),
            "state" => throw new StateDamagedException("Damaged test state"),
            "unknown" => Task.FromResult<bool?>(null),
            _ => Task.FromResult<bool?>(true)
        };
        public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState item, CancellationToken ct) => inner.OpenReadAsync(item, ct);
        public Task<ItemState> PutAsync(SyncAction action, Stream bytes, CancellationToken ct) => inner.PutAsync(action, bytes, ct);
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => inner.DeleteAsync(action, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState item, string id, CancellationToken ct) => inner.PreserveAsync(item, id, ct);
    }
}
