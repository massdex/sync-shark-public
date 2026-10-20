using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task QuarantineReusedNameSyncAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "reused-name-sync"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var original = fx.A.Seed("old.txt", "held bytes");
        store.WriteSnapshot(new("ra", Side.A, store.BeginGeneration("ra", "seed"), "", [original], []));
        store.HoldQuarantine("ra", original);
        store.AddNotices([new("p1", 30, NoticeKind.UserDecision, original.Path, "held", QuarantineFailure.NoticeKey,
            Quarantine: new(Side.A, null, original, true))]);
        var moved = await fx.A.MoveAsync(new("user-move", 30, ActionKind.Move, Side.A, "moved.txt", original.Path,
            original, original, false, null), default);
        var replacement = fx.A.Seed("old.txt", "new independent bytes");
        store.WriteSnapshot(new("ra", Side.A, store.BeginGeneration("ra", "partial"), "", [moved, replacement], ["locked"]));
        Require(store.Live("ra", ["old.txt"]).Single().ObjectId == replacement.ObjectId &&
            !store.Live("ra", ["old.txt"]).Single().Flags.HasFlag(ItemFlags.Quarantined), "partial listing stores reused name unheld");
        await FirstSyncAsync(fx.Run(), fx);
        Require((await fx.B.ObserveAsync("old.txt", default))?.Hash == replacement.Hash &&
            await fx.B.ObserveAsync("moved.txt", default) is null, "new file at reused name syncs while moved identity remains held");
    }

    private static void QuarantineIdentityReleaseChecks(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "identity-release"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var item = new ItemState("old.txt", ItemKind.File, "moving", null, new string('a', 64), 1, DateTimeOffset.UtcNow, ItemFlags.None);
        var moved = item with { Path = "new.txt" };
        var displaced = moved with { ObjectId = "other" };
        store.WriteSnapshot(new("ra", Side.A, store.BeginGeneration("ra", "seed"), "", [item], []));
        store.HoldQuarantine("ra", item);
        store.AddNotices([new("p1", 30, NoticeKind.UserDecision, item.Path, "moving hold", QuarantineFailure.NoticeKey,
            Quarantine: new(Side.A, null, item, true)),
            new("p1", 30, NoticeKind.UserDecision, moved.Path, "other hold", QuarantineFailure.NoticeKey,
            Quarantine: new(Side.A, null, displaced, true))]);
        store.WriteSnapshot(new("ra", Side.A, store.BeginGeneration("ra", "partial"), "", [moved], [""]));
        Require(store.Live("ra").Count(i => i.Flags.HasFlag(ItemFlags.Quarantined)) == 2,
            "uncovered old alias remains held before explicit release");
        Require(store.ClearQuarantine("ra", moved) && store.Live("ra").All(i => !i.Flags.HasFlag(ItemFlags.Quarantined)),
            "explicit release clears every alias of root/object identity");
        store.HoldQuarantine("ra", moved);
        store.WriteSnapshot(new("ra", Side.A, store.BeginGeneration("ra", "covered-old"), "", [moved], ["unrelated"]));
        var notices = store.OpenNotices("p1").Where(n => n.Path == moved.Path && n.Quarantine is not null).ToArray();
        Require(notices.Length == 2 && notices.Select(n => n.Quarantine!.ExpectedTarget!.ObjectId).ToHashSet().SetEquals(["moving", "other"]),
            "rename onto another identity's notice never merges review payloads");
    }

    private static void QuarantineCounterpartIdentityChecks(string root)
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, $"counterpart-identity-{side}"), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var rootId = side == Side.A ? "ra" : "rb";
            var held = new ItemState("held.txt", ItemKind.File, "held-entity", null, new string('a', 64),
                1, DateTimeOffset.UtcNow, ItemFlags.None);
            var counterpart = held with { ObjectId = "counterpart-old" };
            store.WriteSnapshot(new(rootId, side, store.BeginGeneration(rootId, "seed"), "", [held], []));
            store.HoldQuarantine(rootId, held);
            var original = new Notice("p1", 30, NoticeKind.UserDecision, held.Path, "held", QuarantineFailure.NoticeKey,
                Quarantine: new(side, counterpart, held, true));
            store.AddNotices([original]);
            var oldId = store.OpenNotices("p1").Single().NoticeId!;
            var refreshed = original with { Path = "HELD.txt", Quarantine = original.Quarantine! with
                { Source = counterpart with { ObjectId = "counterpart-new" } } };
            store.AddNotices([refreshed]);
            var remaining = store.OpenNotices("p1").Single();
            Require(remaining.NoticeId != oldId && remaining.Quarantine!.Source!.ObjectId == "counterpart-new",
                $"{side}: counterpart replacement supersedes the older review at the same path key");
            Require(store.Live(rootId).Single().Flags.HasFlag(ItemFlags.Quarantined),
                $"{side}: superseding a review never clears the held identity");
            store.AddNotices([refreshed]);
            Require(store.OpenNotices("p1").Count == 1, $"{side}: refreshed review replay stays singular");
            store.AddNotices([refreshed with { Quarantine = refreshed.Quarantine! with
                { ExpectedTarget = held with { ObjectId = "different-held-entity" } } }]);
            Require(store.OpenNotices("p1").Count == 2 && store.Live(rootId).Single().Flags.HasFlag(ItemFlags.Quarantined),
                $"{side}: a different held identity never supersedes the original identity's review or hold");
        }
    }

    private static void QuarantineSnapshotRenameChecks(string root)
    {
        QuarantineCounterpartIdentityChecks(root);
        QuarantineIdentityReleaseChecks(root);
        foreach (var oldFirst in new[] { true, false })
        foreach (var reuseOldName in new[] { false, true })
        foreach (var mode in new[] { "complete", "incomplete", "partial", "crash" })
        {
            var label = $"full-only rename oldFirst={oldFirst} reuse={reuseOldName} {mode}";
            var folder = Path.Combine(root, $"snapshot-rename-{oldFirst}-{reuseOldName}-{mode}");
            var settings = Settings with { StoreBatchRows = 1 };
            var store = SqliteStateStore.Open(folder, settings, TimeProvider.System);
            try
            {
                var fx = Build(suppliedStore: store);
                var oldPath = oldFirst ? "a/held.txt" : "z/held.txt";
                var newPath = oldFirst ? "z/held.txt" : "a/held.txt";
                var original = new ItemState(oldPath, ItemKind.File, "held-entity", null, new string('a', 64),
                    1, DateTimeOffset.UtcNow, ItemFlags.None);
                store.WriteSnapshot(new Snapshot("ra", Side.A, store.BeginGeneration("ra", "seed"), "seed", [original], []));
                store.HoldQuarantine("ra", original);
                store.AddNotices([new Notice("p1", 30, NoticeKind.UserDecision, oldPath, "held entity", QuarantineFailure.NoticeKey,
                    Quarantine: new(Side.A, null, original, true))]);
                var moved = original with { Path = newPath };
                var replacement = original with { ObjectId = "different-entity", Hash = new string('b', 64) };
                var items = reuseOldName ? new[] { moved, replacement } : new[] { moved };
                var snapshot = new Snapshot("ra", Side.A, store.BeginGeneration("ra", "rename"), "after", items, []);
                if (mode == "partial")
                {
                    var checkedHeld = false;
                    var absentHeld = snapshot with { Items = [], IncompleteSubtrees = ["unrelated/locked"],
                        VerifyAbsence = _ => { checkedHeld = true; return FileAbsence.Unknown; } };
                    store.WriteSnapshot(absentHeld);
                    Require(!checkedHeld && absentHeld.PendingDeletionEvidence.Count == 0 &&
                        store.Live("ra", [oldPath]).Single().Flags.HasFlag(ItemFlags.Quarantined),
                        label + " held row needs no absence check or misleading deletion evidence");
                    snapshot = snapshot with { IncompleteSubtrees = ["unrelated/locked"] };
                }
                if (mode == "incomplete")
                {
                    store.WriteSnapshot(snapshot with { IncompleteSubtrees = [""] });
                    var aliases = store.Live("ra").Where(i => i.ObjectId == original.ObjectId).ToArray();
                    Require(aliases.Length == 2 && aliases.All(i => i.Flags.HasFlag(ItemFlags.Quarantined)) &&
                        aliases.Any(i => i.Path == oldPath), label + " never retires old held alias during incomplete inventory");
                }
                if (mode == "crash")
                {
                    void Sql(string text)
                    {
                        using var connection = new SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(folder) + ";Pooling=False");
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText = text;
                        command.ExecuteNonQuery();
                    }
                    Sql($"""
                        CREATE TRIGGER fail_held_retirement BEFORE DELETE ON file_state
                        WHEN OLD.object_id='held-entity' AND
                          (SELECT path FROM dir WHERE dir_no=OLD.dir_no)='{oldPath[..oldPath.LastIndexOf('/')]}' AND
                          EXISTS(SELECT 1 FROM file_state f JOIN dir d ON d.dir_no=f.dir_no
                            WHERE f.object_id='held-entity' AND (f.flags & 128)!=0
                              AND d.path='{newPath[..newPath.LastIndexOf('/')]}')
                        BEGIN SELECT RAISE(ABORT, 'injected atomic publication failure'); END;
                        """);
                    var injected = false;
                    try { store.WriteSnapshot(snapshot); }
                    catch (SqliteException error) when (error.Message.Contains("injected atomic publication failure", StringComparison.Ordinal))
                    { injected = true; }
                    Require(injected, label + " fails inside publication after the new held row is written");
                    void AssertRollback()
                    {
                        var rows = store.Live("ra");
                        Require(rows.Count == 1 && rows.Single().Path == oldPath &&
                            rows.Single().ObjectId == original.ObjectId && rows.Single().Flags.HasFlag(ItemFlags.Quarantined) &&
                            store.Tombstones("ra").Count == 0,
                            label + " full rollback keeps only old held alias, no replacement, new alias or tombstone");
                    }
                    AssertRollback();
                    store.Dispose();
                    store = SqliteStateStore.Open(folder, settings, TimeProvider.System);
                    AssertRollback();
                    Sql("DROP TRIGGER fail_held_retirement;");
                }
                store.WriteSnapshot(snapshot with { Gen = store.BeginGeneration("ra", "converge") });
                var retained = store.Live("ra").Where(i => i.ObjectId == original.ObjectId).ToArray();
                Require(retained.Length == 1 && retained[0].Path == newPath && retained[0].Flags.HasFlag(ItemFlags.Quarantined),
                    label + " converges to exactly one held row at the new path");
                var oldRows = store.Live("ra", [oldPath]);
                Require(reuseOldName ? oldRows.Single().ObjectId == replacement.ObjectId &&
                    !oldRows.Single().Flags.HasFlag(ItemFlags.Quarantined) : oldRows.Count == 0,
                    label + " retires stale alias without transferring entity hold to old-name replacement");
                Require(store.OpenNotices("p1").Any(n => n.Path == newPath && n.Quarantine?.ExpectedTarget?.ObjectId == original.ObjectId &&
                    n.Quarantine.ExpectedTarget.Path == newPath), label + " notice follows the held entity's new path");
                if (mode == "partial")
                {
                    Require(store.ClearQuarantine("ra", original), label + " release binds identity even at old path");
                    foreach (var notice in store.OpenNotices("p1").Where(n => n.Quarantine is not null))
                        store.CloseNotice(notice.NoticeId!, NoticeChoices.QuarantineExclude);
                    store.WriteSnapshot(snapshot with { Gen = store.BeginGeneration("ra", "edit"),
                        Items = items.Select(i => i.ObjectId == original.ObjectId ? i with { Size = 2 } : i).ToArray() });
                    Require(store.Live("ra").All(i => !i.Flags.HasFlag(ItemFlags.Quarantined)) &&
                        !store.OpenNotices("p1").Any(n => n.Quarantine is not null), label + " edit never silently re-holds released identity");
                }
            }
            finally { store.Dispose(); }
        }
    }
}
