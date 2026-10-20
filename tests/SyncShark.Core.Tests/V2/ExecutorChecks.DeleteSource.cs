namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task DeleteSourceChecksAsync()
    {
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
        await DeleteSourceFailureIsPerItemAsync();
        {
            var (_, a, b, executor) = Rig();
            var target = new RecordingWriter(b);
            var victim = b.Seed("unknown", "keep");
            var results = await Apply(executor, Plan(Act(ActionKind.Delete, Side.B, "unknown", expected: victim)),
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
        await DeleteReplacementSourceChecksAsync();
        await DeleteSourceRetrySurvivesReopenAsync();
    }

    private static async Task DeleteSourceFailureIsPerItemAsync()
    {
        var (store, a, b, executor) = Rig();
        var victim = b.Seed("x", "old");
        var source = new RecordingWriter(a) { ObserveFails = true };
        var target = new RecordingWriter(b);
        var results = await Apply(executor, Plan(Act(ActionKind.Delete, Side.B, "x", expected: victim),
            Act(ActionKind.MkDir, Side.B, "unrelated")), source, target);
        Require(results.Count(r => r.Outcome == Outcome.Skipped) == 1 && results.Count(r => r.Outcome == Outcome.Done) == 1,
            "source access failure holds only its delete, not the run");
        Require(target.Deletes == 0 && store.OpenNotices("p1").Any(n => n.ReasonKey == DeleteSourceGuard.ReasonKey),
            "source failure records a retryable notice without deleting");
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
                var executor = new Executor(store, Settings(2), Admission());
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
