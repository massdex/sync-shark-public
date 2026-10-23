namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task DeleteTaxonomyChecksAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var authority in new[] { "choice", "rebuildable", "review", "frozen", "conflict" })
        {
            var (_, a, b, executor) = Rig();
            var victim = (side == Side.A ? a : b).Seed("x", "loser");
            (side == Side.A ? b : a).Seed("x", "winner");
            var action = Act(ActionKind.Delete, side, "x", expected: victim, rf: true, rule: 32);
            action = authority switch
            {
                "choice" => action with { ChoiceRemove = true },
                "rebuildable" => action with { RebuildableRemove = true },
                "review" => action with { Rule = 40, RemovalReviewChoice = true },
                "frozen" => action with { Rule = 27, FrozenChoiceId = "choice-id" },
                _ => action with { Rule = 10, ActionId = "nch-explicit-delete", SuppressReconciledBaseline = true }
            };
            var target = new RecordingWriter(side == Side.A ? a : b);
            var results = await Apply(executor, Plan(action), side == Side.A ? target : a, side == Side.B ? target : b);
            Require(results.Single().Outcome == Outcome.Done && target.Preserves == 1 && target.Deletes == 1,
                "durable explicit authority removes only its conditional victim despite present opposite source");
        }
        foreach (var rule in new[] { 6, 19, 23, 40, 12 })
        {
            var (_, a, b, executor) = Rig();
            var victim = b.Seed("x", "keep");
            var target = new RecordingWriter(b);
            var results = await Apply(executor, Plan(Act(ActionKind.Delete, Side.B, "x", expected: victim, rf: true,
                rule: rule)), a, target);
            Require(results.Single().DeleteSourceUnconfirmed && target.Deletes == 0 && target.Preserves == 0,
                "unmarked evidence/proposals and unpaired rule12 are not explicit removal authority");
        }
        await AdoptCleanupSurvivorChecksAsync();
        await DeletePairedMoveChecksAsync();
        await DeleteRenamedSourceBothDirectionsAsync();
    }

    internal static async Task AdoptCleanupSurvivorChecksAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var change in new[] { "missing", "replaced", "unchanged", "during-intent", "edited" })
        {
            var folder = Path.Combine(Environment.GetEnvironmentVariable("SYNCSHARK_TEST_ROOT") ?? Path.GetTempPath(),
                "adopt-cleanup-" + Guid.NewGuid().ToString("N"));
            var a = new FakeEndpoint(Side.A, "ra");
            var b = new FakeEndpoint(Side.B, "rb");
            var source = side == Side.A ? b : a;
            var endpoint = side == Side.A ? a : b;
            var incoming = source.Seed("new", "same");
            var old = endpoint.Seed("old", "same");
            var occupant = endpoint.Seed("new", "same");
            // Match production normalization: Adopt always targets B, even for a move on A.
            var adopt = Act(ActionKind.Adopt, Side.B, "new",
                source: side == Side.A ? occupant : incoming,
                expected: side == Side.A ? incoming : occupant, key: "adopt-key", rule: 12);
            var delete = Act(ActionKind.Delete, side, "old", source: incoming, expected: old, rf: true, rule: 12);
            if (change is "missing" or "replaced") await RemoveSourceAsync(endpoint, "new");
            if (change == "replaced") endpoint.Seed("new", "changed");
            if (change == "edited")
                Require(endpoint.Seed("new", "changed").ObjectId == occupant.ObjectId,
                    "edited survivor fixture preserves identity while changing bytes");
            using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
            {
                store.SavePair(Pair());
                var target = new RecordingWriter(endpoint);
                var recording = new RecordingStore(store)
                {
                    AfterIntent = action =>
                    {
                        if (change == "during-intent" && action.ActionId == delete.ActionId)
                            RemoveSourceAsync(endpoint, "new").GetAwaiter().GetResult();
                    }
                };
                var results = await Apply(new Executor(recording, Settings(2), Admission()), Plan(adopt, delete),
                    side == Side.A ? target : a, side == Side.B ? target : b);
                Require(results.Single(r => r.ActionId == adopt.ActionId).Outcome == Outcome.Done,
                    "ordinary Adopt remains metadata-only: " + results.Single(r => r.ActionId == adopt.ActionId).Reason);
                if (change == "edited")
                    Require(results.Single(r => r.ActionId == delete.ActionId).Reason?.Contains("destination changed", StringComparison.Ordinal) == true,
                        "edited survivor explains destination change, not merely a missing effect");
                Require(target.Deletes == (change == "unchanged" ? 1 : 0) &&
                    target.Preserves == (change == "unchanged" ? 1 : 0),
                    "cleanup requires the exact surviving destination before destructive preservation");
            }
            using (var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
            {
                if (change != "unchanged")
                {
                    Require(await endpoint.ObserveAsync("old", default) is not null &&
                        reopened.SyncRetryAt("p1", "old") is not null,
                        "old copy and retry survive cold reopen after stale Adopt");
                    Require(reopened.OpenNotices("p1").Count(n => n.ReasonKey == DeleteSourceGuard.ReasonKey) == 1,
                        "stale Adopt cleanup has one durable protective notice");
                }
            }
        }
    }

    private static async Task DeletePairedMoveChecksAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var paired in new[] { false, true })
        {
            var (_, a, b, executor) = Rig();
            var source = side == Side.A ? b : a;
            var endpoint = side == Side.A ? a : b;
            var incoming = source.Seed("new", "same");
            var old = endpoint.Seed("old", "same");
            var occupant = endpoint.Seed("new", "same");
            var delete = Act(ActionKind.Delete, side, "old", source: incoming, expected: old, rf: true, rule: 12);
            var adopt = Act(ActionKind.Adopt, side, "new", source: incoming, expected: occupant, rule: 12);
            var target = new RecordingWriter(endpoint);
            var results = await Apply(executor, paired ? Plan(adopt, delete) : Plan(delete),
                side == Side.A ? target : a, side == Side.B ? target : b);
            Require(results.Single(result => result.ActionId == delete.ActionId).Outcome ==
                (paired ? Outcome.Done : Outcome.Skipped) && target.Deletes == (paired ? 1 : 0),
                "rule12 move completion requires successful same-plan identity-matched Adopt");
        }
        foreach (var failUpdate in new[] { false, true })
        {
            var (_, a, b, executor) = Rig();
            var incoming = a.Seed("new", "incoming");
            var old = b.Seed("old", "incoming");
            var winner = b.Seed("new", "winner");
            if (failUpdate) a.FailNextWrites = 1;
            var update = Act(ActionKind.Update, Side.A, "new", source: winner, expected: incoming, rf: true, rule: 4);
            var delete = Act(ActionKind.Delete, Side.B, "old", source: incoming, expected: old, rf: true, rule: 4);
            var target = new RecordingWriter(b);
            var results = await Apply(executor, Plan(update, delete), a, target);
            Require(results.Single(result => result.ActionId == delete.ActionId).Outcome ==
                (failUpdate ? Outcome.Skipped : Outcome.Done) && target.Deletes == (failUpdate ? 0 : 1),
                "rule4 old-copy removal requires successful identity-matched Update, not merely its presence in plan");
        }
        foreach (var validSource in new[] { false, true })
        {
            var (_, a, b, executor) = Rig();
            var incoming = a.Seed("new", "incoming");
            var old = b.Seed("old", "incoming");
            var occupant = b.Seed("new", "loser");
            if (!validSource) await RemoveSourceAsync(a, "new");
            var delete = Act(ActionKind.Delete, Side.B, "new", source: incoming, expected: occupant, rf: true, rule: 4);
            var move = Act(ActionKind.Move, Side.B, "new", from: "old", source: incoming, expected: old, rule: 4);
            var target = new RecordingWriter(b);
            var results = await Apply(executor, Plan(delete, move), a, target);
            Require(results.Single(result => result.ActionId == delete.ActionId).Outcome ==
                (validSource ? Outcome.Done : Outcome.Skipped),
                "rule4 vacating delete needs fresh read-only admission of matching Move");
            Require(target.Preserves == (validSource ? 1 : 0) && target.Deletes == (validSource ? 1 : 0),
                "collision loser is preserved before any admitted delete");
        }
    }

    private static async Task DeleteRenamedSourceBothDirectionsAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            var (store, a, b, executor) = Rig();
            var source = side == Side.A ? b : a;
            var original = source.Seed("old", "same");
            var victim = (side == Side.A ? a : b).Seed("old", "same");
            SeedBaseline(store, "kx", side == Side.A ? victim : original, side == Side.A ? original : victim);
            await source.MoveAsync(Act(ActionKind.Move, side == Side.A ? Side.B : Side.A, "new",
                from: "old", source: original, expected: original), default);
            var target = new RecordingWriter(side == Side.A ? a : b);
            var results = await Apply(executor, Plan(Act(ActionKind.Delete, side, "old", expected: victim,
                rf: true, key: "kx", rule: 6)), side == Side.A ? target : a, side == Side.B ? target : b);
            Require(results.Single().DeleteSourceUnconfirmed && target.Deletes == 0 && target.Preserves == 0 &&
                store.Baselines("p1").Count == 1,
                "global historical identity prevents deletion when source moved outside absent old path in either direction");
        }
    }
}
