namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task DeleteSourceChecksAsync()
    {
        await FailureBoundaryChecks.RunAsync();
        foreach (var side in new[] { Side.A, Side.B })
        {
            var (store, a, b, executor) = Rig();
            var stateA = a.Seed("x", "same");
            var stateB = b.Seed("x", "same");
            SeedBaseline(store, "kx", stateA, stateB);
            var target = new RecordingWriter(side == Side.A ? a : b);
            var action = Act(ActionKind.Delete, side, "x", expected: side == Side.A ? stateA : stateB,
                rf: true, key: "kx");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await Apply(executor, Plan(action), side == Side.A ? target : a, side == Side.B ? target : b);
                Require(result.Single().Outcome == Outcome.Skipped, "present source blocks delete in both directions");
            }
            Require(target.Deletes == 0 && target.Preserves == 0, "no delete or Recovery mutation when source exists");
            Require(store.Baselines("p1").Count == 1, "source-present block retains baseline");
            Require(store.OpenNotices("p1").Count(n => n.ReasonKey == DeleteSourceGuard.ReasonKey) == 1,
                "repeated attempts keep one deduplicated source notice");
            await RemoveSourceAsync(side == Side.A ? b : a, "x");
            var retried = await Apply(executor, Plan(action), side == Side.A ? target : a, side == Side.B ? target : b);
            Require(retried.Single().Outcome == Outcome.Done && store.Baselines("p1").Count == 0,
                "verified absence permits retry and only then removes baseline");
            Require(!store.OpenNotices("p1").Any(n => n.ReasonKey == DeleteSourceGuard.ReasonKey),
                "successful retry closes source notice");
        }
        await AdmissionAbsenceBatchChecksAsync();
        await AdmissionAbsenceBatchFailureChecksAsync();
        await AdmissionAbsenceIsolatedMemberChecksAsync();
        await DeleteSourceFailureIsPerItemAsync();
        {
            var (_, a, b, executor) = Rig();
            var target = new RecordingWriter(b);
            var victim = b.Seed("unknown", "keep");
            var results = await Apply(executor, Plan(Act(ActionKind.Delete, Side.B, "unknown", expected: victim) with { DeletionSourceObjectId = "original-source" }),
                new RecordingWriter(a) { AbsenceUnknown = true }, target);
            Require(results.Single().Outcome == Outcome.Skipped && target.Deletes == 0,
                "unknown absence is never deletion evidence");
        }
        await DeleteSourceMovedChildKeepsParentAsync();
        {
            var (store, a, b, executor) = Rig();
            var source = b.Seed("matched", "same");
            var survivor = a.Seed("matched", "same");
            await b.MoveAsync(Act(ActionKind.Move, Side.B, "moved", from: "matched", source: source, expected: source), default);
            var target = new RecordingWriter(a);
            var preparedDelete = Act(ActionKind.Delete, Side.A, "matched", expected: survivor, rule: 6) with
                { DeletionSourceObjectId = source.ObjectId };
            var results = await Apply(executor, Plan(preparedDelete), target, b);
            Require(results.Single().DeleteSourceUnconfirmed && target.Deletes == 0 && store.SyncRetryAt("p1", "matched") is not null,
                "retained prepared Match local-target delete probes relocated opposite identity and leaves durable replan debt");
        }
        await DeleteTaxonomyChecksAsync();
        await DeleteMissingIdentityAndChoiceChecksAsync();
        await DeleteExpandedMissingIdentityChecksAsync();
        await DeleteReplacementSourceChecksAsync();
        await DeleteSourceRetrySurvivesReopenAsync();
        await AbsenceOwnerSqliteAsync();
        await LocalDispatchAbsenceSqliteAsync();
    }

    private static async Task AdmissionAbsenceBatchChecksAsync()
    {
        const int count = 384;
        var (store, a, b, executor) = Rig(Settings(64));
        var source = new RecordingWriter(a) { UnknownAbsencePath = "file-1" };
        var target = new RecordingWriter(b);
        var actions = new List<SyncAction>();
        for (var i = 0; i < count; i++)
        {
            var path = "file-" + i;
            var original = a.Seed(path, "same");
            var victim = b.Seed(path, "same");
            SeedBaseline(store, "key-" + i, original, victim);
            if (i != 0) await RemoveSourceAsync(a, path);
            actions.Add(Act(ActionKind.Delete, Side.B, path, expected: victim, rf: true, key: "key-" + i));
        }
        var results = await Apply(executor, Plan(actions.ToArray()), source, target);
        Require(results.Count(r => r.Outcome == Outcome.Done) == count - 2 && target.Deletes == count - 2 &&
            target.Preserves == count - 2, "batched admission deletes only proven-absent sources");
        Require(results.Count(r => r.DeleteSourceUnconfirmed) == 2 &&
            store.SyncRetryAt("p1", "file-0") is not null && store.SyncRetryAt("p1", "file-1") is not null &&
            store.OpenNotices("p1").Count > 0, "present and inconclusive sources retain notices and durable retries");
        Require(source.AbsenceBatchItems == count && source.AbsenceBatchCalls <= count / 10,
            "admission coalesces at least ten source probes per batch on average");
        Console.WriteLine($"Admission probe count: {count} items / {source.AbsenceBatchCalls} batches");
    }

    private static async Task AdmissionAbsenceBatchFailureChecksAsync()
    {
        foreach (var throws in new[] { true, false })
        {
            var (store, a, b, executor) = Rig(Settings(64));
            var source = new RecordingWriter(a) { ThrowAbsenceBatch = throws, ShortAbsenceBatch = !throws };
            var target = new RecordingWriter(b);
            var actions = new List<SyncAction>();
            for (var i = 0; i < 64; i++)
            {
                var path = "batch-failure-" + i;
                var original = a.Seed(path, "same");
                var victim = b.Seed(path, "same");
                SeedBaseline(store, path, original, victim);
                await RemoveSourceAsync(a, path);
                actions.Add(Act(ActionKind.Delete, Side.B, path, expected: victim, rf: true, key: path));
            }
            var results = await Apply(executor, Plan(actions.ToArray()), source, target);
            Require(results.Count == 64 && results.All(result => result.DeleteSourceUnconfirmed &&
                result.Outcome == Outcome.Skipped) && target.Deletes == 0 && target.Preserves == 0,
                "whole-batch failure or short result list never admits any member");
            Require(store.Baselines("p1").Count == 64 && actions.All(action =>
                store.SyncRetryAt("p1", action.Path) is not null), "every blocked batch member retains durable retry and baseline");
        }
    }

    private static async Task AdmissionAbsenceIsolatedMemberChecksAsync()
    {
        var (store, a, b, executor) = Rig(Settings(64));
        var source = new RecordingWriter(a) { UnknownAbsencePath = "ten-3", UnknownAbsenceReason = "rate limited" };
        var target = new RecordingWriter(b);
        var actions = new List<SyncAction>();
        for (var i = 0; i < 10; i++)
        {
            var path = "ten-" + i;
            var original = a.Seed(path, "same");
            var victim = b.Seed(path, "same");
            SeedBaseline(store, "ten-" + i, original, victim);
            await RemoveSourceAsync(a, path);
            actions.Add(Act(ActionKind.Delete, Side.B, path, expected: victim, rf: true, key: "ten-" + i));
        }
        var results = await Apply(executor, Plan(actions.ToArray()), source, target);
        Require(results.Count(r => r.Outcome == Outcome.Done) == 9 && target.Deletes == 9 && target.Preserves == 9,
            "one inconclusive member leaves the other nine deletion checks evaluated normally");
        Require(results.Count(r => r.DeleteSourceUnconfirmed) == 1 &&
            results.Single(r => r.DeleteSourceUnconfirmed).Outcome == Outcome.Skipped &&
            store.SyncRetryAt("p1", "ten-3") is not null,
            "the inconclusive member is blocked with a retry while its healthy siblings proceed");
        var blocked = store.ReadBlockedDeletes("p1").Items.Single(item => item.Path == "ten-3");
        Require(blocked.Detail.Contains("absence could not be verified: rate limited", StringComparison.Ordinal),
            "the blocked item keeps the real cause of its failed absence check");
        Require(store.OpenNotices("p1").Count(n => n.ReasonKey == SqliteStateStore.BlockedDeleteSummary) == 1,
            "one aggregated notice per pair, not one per failed member");
    }

    private static async Task DeleteSourceFailureIsPerItemAsync()
    {
        var (store, a, b, executor) = Rig();
        var victim = b.Seed("x", "old");
        var source = new RecordingWriter(a) { ObserveFails = true };
        var target = new RecordingWriter(b);
        var results = await Apply(executor, Plan(Act(ActionKind.Delete, Side.B, "x", expected: victim) with
            { DeletionSourceObjectId = "original-source" }, Act(ActionKind.MkDir, Side.B, "unrelated")), source, target);
        Require(results.Count(r => r.Outcome == Outcome.Skipped) == 1 && results.Count(r => r.Outcome == Outcome.Done) == 1,
            "source access failure holds only its delete, not the run");
        Require(target.Deletes == 0 && store.OpenNotices("p1").Count(n => n.ReasonKey == SqliteStateStore.BlockedDeleteSummary) == 1 &&
            !store.OpenNotices("p1").Any(n => n.ReasonKey == DeleteSourceGuard.ReasonKey),
            "source failure records one deferred summary without per-item notice flooding or deletion");
    }

    private static async Task DeleteSourceMovedChildKeepsParentAsync()
    {
        var (store, a, b, executor) = Rig();
        var original = a.Seed("d/x", "same");
        var child = b.Seed("d/x", "same");
        var folder = b.Seed("d", "", ItemKind.Folder);
        SeedBaseline(store, "kx", original, child);
        await a.MoveAsync(Act(ActionKind.Move, Side.A, "elsewhere", from: "d/x", source: original, expected: original), default);
        var gen = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, gen, "1", [folder, child], []));
        store.CompleteGeneration(gen, "1", true);
        var target = new RecordingWriter(b);
        var results = await Apply(executor, Plan(Act(ActionKind.Delete, Side.B, "d", expected: folder)), a, target);
        Require(results.All(r => r.Outcome == Outcome.Skipped) && target.Deletes == 0 && target.Preserves == 0,
            "source child relocated by identity keeps both target child and its ancestor");
        Require(store.Baselines("p1").Count == 1, "relocated child's baseline is retained");
    }

    private static async Task DeleteMissingIdentityAndChoiceChecksAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var choice in new[] { false, true })
        foreach (var evidence in new[] { "missing-id", "present", "absent" })
        {
            var (store, a, b, executor) = Rig();
            var original = (side == Side.A ? b : a).Seed("x", "same");
            var victim = (side == Side.A ? a : b).Seed("x", "same");
            if (evidence != "missing-id")
                SeedBaseline(store, "kx", side == Side.A ? victim : original, side == Side.A ? original : victim);
            if (evidence != "present") await RemoveSourceAsync(side == Side.A ? b : a, "x");
            var target = new RecordingWriter(side == Side.A ? a : b);
            var action = Act(ActionKind.Delete, side, "x", expected: victim, rf: true,
                key: evidence == "missing-id" ? null : "kx", rule: choice ? 32 : 6) with { ChoiceRemove = choice };
            var results = await Apply(executor, Plan(action), side == Side.A ? target : a, side == Side.B ? target : b);
            var allowed = choice || evidence == "absent";
            Require(results.Single().Outcome == (allowed ? Outcome.Done : Outcome.Skipped),
                "only absence propagation requires absent source; explicit removal remains conditional on its victim");
            Require(target.Deletes == (allowed ? 1 : 0) && target.Preserves == (allowed ? 1 : 0),
                "baseline-backed genuine deletion preserves then deletes; unproven deletion has no effects");
            Require(store.Baselines("p1").Count == (!allowed && evidence == "present" ? 1 : 0),
                "source-present baseline kept and genuinely deleted baseline removed");
            Require(allowed || (store.SyncRetryAt("p1", "x") is not null &&
                store.OpenNotices("p1").Count(n => n.ReasonKey ==
                    (evidence == "missing-id" ? "deletion-review" : DeleteSourceGuard.ReasonKey)) == 1),
                "missing identity creates a review decision; present source retains retry and one notice");
        }
    }

    private static async Task DeleteExpandedMissingIdentityChecksAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            var (store, a, b, executor) = Rig();
            var endpoint = side == Side.A ? a : b;
            var child = endpoint.Seed("d/x", "keep");
            var folder = endpoint.Seed("d", "", ItemKind.Folder);
            var sourceFolder = (side == Side.A ? b : a).Seed("d", "", ItemKind.Folder);
            SeedBaseline(store, "kd", side == Side.A ? folder : sourceFolder, side == Side.A ? sourceFolder : folder);
            await RemoveSourceAsync(side == Side.A ? b : a, "d");
            var root = side == Side.A ? "ra" : "rb";
            var gen = store.BeginGeneration(root, "snapshot");
            store.WriteSnapshot(new Snapshot(root, side, gen, "1", [folder, child], []));
            store.CompleteGeneration(gen, "1", true);
            var target = new RecordingWriter(endpoint);
            var results = await Apply(executor, Plan(Act(ActionKind.Delete, side, "d", expected: folder,
                rule: 23)), side == Side.A ? target : a, side == Side.B ? target : b);
            Require(results.All(r => r.Outcome == Outcome.Skipped) && target.Deletes == 0 && target.Preserves == 0,
                "expanded no-baseline child cannot use path-only absence and prevents ancestor removal");
            Require(store.SyncRetryAt("p1", "d/x") is { Kind: "decision" } && store.SyncRetryAt("p1", "d") is not null &&
                store.OpenNotices("p1").Any(n => n.Path == "d/x" && n.ReasonKey == "deletion-review"),
                "expanded unknown identity creates child review authority while ancestor remains blocked");
        }
    }

    private static async Task DeleteReplacementSourceChecksAsync()
    {
        foreach (var rule in new[] { 26, 32 })
        foreach (var scenario in new[] { "missing", "wrong-kind", "present" })
        {
            var (_, a, b, executor) = Rig();
            var replacement = a.Seed("x", "", ItemKind.Folder);
            var victim = b.Seed("x", "old");
            var present = scenario == "present";
            if (!present) await RemoveSourceAsync(a, "x");
            if (scenario == "wrong-kind") a.Seed("x", "unexpected file");
            var target = new RecordingWriter(b);
            var results = await Apply(executor, Plan(
                Act(ActionKind.Delete, Side.B, "x", expected: victim) with { Rule = rule },
                Act(ActionKind.MkDir, Side.B, "x", source: replacement) with { Rule = rule }), a, target);
            Require(target.Deletes == (present ? 1 : 0), "replacement delete requires planned source identity and kind");
            Require(present ? results.All(r => r.Outcome == Outcome.Done) : results.Any(r => r.Outcome == Outcome.Skipped),
                "replacement source presence permits intentional type replacement only");
        }
    }

    private static async Task DeleteSourceRetrySurvivesReopenAsync()
    {
        var folder = Path.GetFullPath("delete-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = new FakeEndpoint(Side.A, "ra");
            var b = new FakeEndpoint(Side.B, "rb");
            var source = a.Seed("x", "same");
            var victim = b.Seed("x", "same");
            using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
            {
                store.SavePair(Pair());
                var adopt = Act(ActionKind.Adopt, Side.B, "x", source: source, expected: victim, key: "kx");
                store.CommitResult("p1", 1, adopt, new ActionResult(adopt.ActionId, Outcome.Done, source, victim, null, null));
                var executor = new Executor(store, Settings(2), Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
                var results = await Apply(executor, Plan(Act(ActionKind.Delete, Side.B, "x", expected: victim, key: "kx")), a, b);
                Require(results.Single().DeleteSourceUnconfirmed, "source guard marks durable replan debt");
            }
            using (var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
            {
                Require(reopened.Baselines("p1").Count == 1 && reopened.SyncRetryAt("p1", "x") is { Kind: "sync" },
                    "reopening without another scan preserves both baseline and due retry");
                Require(reopened.OpenNotices("p1").Count(n => n.ReasonKey == DeleteSourceGuard.ReasonKey) == 1,
                    "guard notice survives reopening");
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task RemoveSourceAsync(FakeEndpoint source, string path)
    {
        var item = await source.ObserveAsync(path, default) ?? throw new InvalidOperationException("Missing fixture source");
        await source.DeleteAsync(Act(ActionKind.Delete, Side.A, path, expected: item), default);
    }
}
