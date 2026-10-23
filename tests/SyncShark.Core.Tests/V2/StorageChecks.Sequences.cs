namespace Massdex.SyncShark.V2;

/// <summary>Round-3 checks: the real store accepts the executor's legitimate result sequences end to end.</summary>
internal static partial class StorageChecks
{
    // the executor expands a folder delete into per-item deletes, including descendants that
    // never had a baseline of their own (no object key); the real store must commit them.
    private static async Task Executor_FolderDeleteExpansion_RealStore(string root)
    {
        using var store = OpenSqlite(root, "seq-folder-delete", new TestClock());
        store.SavePair(Pair("p1"));
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var folder = b.Seed("d", "", ItemKind.Folder, versionId: "v1");
        var sub = b.Seed("d/e", "", ItemKind.Folder, versionId: "v1");
        var tracked = b.Seed("d/x", "one", versionId: "v1");
        var untracked = b.Seed("d/e/y", "two", versionId: "v1");
        var gen = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, gen, "1", [folder, sub, tracked, untracked], []));
        store.CompleteGeneration(gen, "1", true);
        SeedBaseline(store, "p1", "kd", a.Seed("d", "", ItemKind.Folder, versionId: "v1"), folder);
        SeedBaseline(store, "p1", "kx", a.Seed("d/x", "one", versionId: "v1"), tracked);
        // This sequence exercises an explicitly chosen whole-folder removal, including untracked children,
        // not absence propagation (the source copies above deliberately still exist).
        var delete = new SyncAction("del-d", 23, ActionKind.Delete, Side.B, "d", null, null, folder, true, "kd")
            { ChoiceRemove = true };

        var results = await ApplyReal(store, a, b, delete);
        Require(results.Count == 4 && results.All(r => r.Outcome == Outcome.Done),
            "the real store commits every expanded delete, baseline or not: " +
            string.Join(", ", results.Select(r => r.ActionId + "=" + r.Outcome + " " + r.Reason)));
        Require(store.Baselines("p1").Count == 0, "the folder's and the tracked file's baselines are removed");
        Require(store.UnresolvedIntents("p1").Count == 0, "no expanded delete is left unresolved");
    }

    // Planner rule 28 and the executor's move resolution: a Move's Source may already carry newer content than the
    // moved item; its Update follows. The real store must accept both results.
    private static async Task Executor_MoveThenUpdate_RealStore(string root)
    {
        using var store = OpenSqlite(root, "seq-move-update", new TestClock());
        store.SavePair(Pair("p1"));
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var current = a.Seed("y", "new-content", versionId: "v2");
        var before = b.Seed("x", "old-content", versionId: "v1");
        var baseA = current with { Path = "x", Hash = before.Hash, Size = before.Size, VersionId = "v1" };
        SeedBaseline(store, "p1", "k1", baseA, before);

        var move = new SyncAction("mv", 28, ActionKind.Move, Side.B, "y", "x", current, before, false, "k1");
        var moved = (await ApplyReal(store, a, b, move)).Single();
        var afterMove = store.Baselines("p1").Single();
        Require(moved.Outcome == Outcome.Done && afterMove.A.Path == "y" && afterMove.B.Path == "y" &&
            afterMove.A.Hash == before.Hash && afterMove.A.VersionId == "v1",
            "a move whose source content differs records the moved content, so the newer side stays edited");

        var update = new SyncAction("up", 28, ActionKind.Update, Side.B, "y", null, current, before with { Path = "y" },
            false, "k1");
        var updated = (await ApplyReal(store, a, b, update)).Single();
        var final = store.Baselines("p1").Single();
        Require(updated.Outcome == Outcome.Done && final.A.Hash == current.Hash && final.B.Hash == current.Hash &&
            final.A.VersionId == "v2" && final.B.Path == "y",
            "the following update commits the new content on both sides: " + updated.Reason);
    }

    // Done and Skipped (incl. superseded) intents are deleted in the commit's transaction;
    // started, uncertain and failed ones are kept.
    private static void Intents_ResolvedDeletedAtCommit(string root)
    {
        using var store = OpenSqlite(root, "intents-resolved", new TestClock());
        store.SavePair(Pair("p1"));
        var outcomes = new[] { Outcome.Done, Outcome.Skipped, Outcome.Failed, Outcome.Uncertain };
        foreach (var outcome in outcomes)
        {
            var action = new SyncAction("i-" + outcome, 6, ActionKind.Delete, Side.B, "x.txt", null, null, null, false, "k");
            store.RecordIntent("p1", action);
            store.CommitResult("p1", 1, action, new ActionResult(action.ActionId, outcome, null, null, null, null));
        }
        store.RecordIntent("p1", new SyncAction("i-Started", 6, ActionKind.Delete, Side.B, "y.txt", null, null, null,
            false, "k2"));
        var kept = store.UnresolvedIntents("p1").Select(intent => intent.ActionId).Order(StringComparer.Ordinal);
        Require(store.IntentRows() == 3 && kept.SequenceEqual(["i-Failed", "i-Started", "i-Uncertain"]),
            $"only started, uncertain and failed intents remain stored ({store.IntentRows()} rows)");
    }

    // Decision 26 with the ReasonKey contract: one notice per (pair, path ignoring case, rule, ReasonKey); Detail is
    // display text. Open + re-raise adds nothing; closed + re-raise reopens the same notice.
    private static void Notices_OnePerProblem(string root)
    {
        using var store = OpenSqlite(root, "notices-reason", new TestClock());
        store.SavePair(Pair("p1"));
        store.AddNotices([new Notice("p1", 4, NoticeKind.AutoConflict, "Docs/x.txt", "first text", "conflict")]);
        store.AddNotices([new Notice("p1", 4, NoticeKind.AutoConflict, "docs/X.TXT", "second text", "conflict")]);
        var open = store.OpenNotices("p1");
        Require(open.Count == 1 && open[0].ReasonKey == "conflict" && open[0].Detail == "first text",
            "re-raising an open problem (other case, other detail) adds nothing and the reason key round-trips");
        store.AddNotices([new Notice("p1", 4, NoticeKind.AutoConflict, "docs/x.txt", "text", "other-reason")]);
        Require(store.OpenNotices("p1").Count == 2, "another reason key on the same path is another problem");
        store.CloseNotice(open[0].NoticeId!, "keep-both");
        Require(store.OpenNotices("p1").Count == 1, "closing a notice removes it from the open list");
        store.AddNotices([new Notice("p1", 4, NoticeKind.AutoConflict, "DOCS/x.txt", "third text", "conflict")]);
        var reopened = store.OpenNotices("p1").Single(n => n.ReasonKey == "conflict");
        Require(reopened.NoticeId == open[0].NoticeId && reopened.Detail == "third text",
            "re-raising a closed problem reopens the same notice with the current text");
    }

    private static async Task<List<ActionResult>> ApplyReal(IStateStore store, FakeEndpoint a, FakeEndpoint b,
        params SyncAction[] actions)
    {
        var admission = new SyncTransferResourceController(new SyncTransferSettings(ResourceMode: "Performance"),
            TimeProvider.System, () => new SyncResourcePressure(0, 0.1, 8L << 30));
        var executor = new Executor(store, Fast, admission);
        var results = new List<ActionResult>();
        await foreach (var result in executor.ApplyAsync(new Plan("p1", 1, actions, []), a, b,
                           CancellationToken.None))
            results.Add(result);
        return results;
    }
}
