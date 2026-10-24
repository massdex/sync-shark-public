namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task WindowAuthorityAsync(string folder, bool rename, bool preparing)
    {
        var root = Path.Combine(folder, "root");
        // Root-level case-only renames deliberately require full reconciliation. Use distinct
        // parents so each incremental parent refresh observes only its own window's rename.
        string OriginalPath(int i) => rename ? $"parent-{i}/File.txt" : $"File-{i}.txt";
        for (var i = 0; i < 4; i++) LocalChecks.Seed(root, OriginalPath(i), "content");
        var settings = StorageChecks.Fast;
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var local = LocalChecks.RootOf(root, "root-local");
        var pair = new PairDef("pair", "Pair A", local,
            new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
        store.SavePair(pair);
        var start = new NtfsUsnCheckpoint(1, 0, 128, 0);
        var target = start with { NextUsn = start.NextUsn + JournalReplayWindow.ReplayWindowBytes * 7 / 2 };
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System, settings: settings);
        inventory.WalkCheckpointForTest = () => (start, false);
        await SaveFull(inventory, store);
        var items = store.Live(local.RootId).Where(item => item.Kind == ItemKind.File).OrderBy(item => item.Path).ToArray();
        if (rename)
        {
            var before = items[0];
            var after = before with { Path = before.Path.ToLowerInvariant() };
            var change = new Change(ChangeKind.Upsert, after.ObjectId, after.Path, after, null);
            LocalChecks.Require(change.HasPreviousPath(before) && change.NeedsRunAuthority(before),
                "case-only file rename requires Run authority independently of parent-folder refresh");
        }
        if (preparing) store.ForgetCursor(local.RootId);
        else store.MarkFirstSyncDone(pair.PairId);
        var initialDebt = store.CountDeferredPlanning(pair.PairId);
        for (var i = 0; i < 4; i++)
        {
            var path = OriginalPath(i);
            if (preparing) LocalChecks.Seed(root, path, "changed content");
            else if (rename) File.Move(Path.Combine(root, path), Path.Combine(root, path.ToLowerInvariant()));
            else File.Delete(Path.Combine(root, path));
        }
        inventory.WalkCheckpointForTest = () => (target, false);
        var reads = 0;
        inventory.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
        {
            reads++;
            var index = (int)((from.NextUsn - start.NextUsn) / JournalReplayWindow.ReplayWindowBytes);
            var item = items[index];
            changes = [new(LocalItems.ParseObjectId(item.ObjectId).FileId,
                LocalItems.ParseObjectId(LocalChecks.ObjectIdOf(Path.GetDirectoryName(Path.Combine(root, item.Path))!)).FileId, from.NextUsn,
                preparing ? 0x1U : rename ? 0x2000U : 0x200U, FileAttributes.Normal,
                Path.GetFileName(rename && !preparing ? item.Path.ToLowerInvariant() : item.Path))];
            failure = null; kind = NtfsUsnReadFailure.Transient; return true;
        };
        var cursor = start.Cursor;
        for (var run = 0; run < (preparing ? 1 : 4); run++)
        {
            var delta = await inventory.ChangesSinceAsync(cursor, store.BeginGeneration(local.RootId, "delta"), _ => true, default);
            LocalChecks.Require(!delta.CursorInvalid, "bounded authority window remains an incremental delta");
            LocalChecks.Require(reads == run + 1, "authority-bearing replay returns after exactly one window");
            LocalChecks.Require(delta.JournalBacklog == (run < 3), "unread windows request immediate consecutive runs");
            LocalChecks.Require(store.Cursor(local.RootId) == (preparing ? null : cursor), "inventory does not checkpoint pending or authority-bearing window");
            if (preparing)
            {
                LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == initialDebt, "failed prepare cannot publish intermediate planning debt");
                LocalChecks.Require(!store.SupportsJournalWindows(local.RootId), "first sync without committed cursor is ineligible");
                store.MarkFirstSyncDone(pair.PairId);
                LocalChecks.Require(!store.SupportsJournalWindows(local.RootId), "completed pair still requires a committed cursor");
                store.CompleteGeneration(delta.Gen, cursor, true);
                LocalChecks.Require(store.SupportsJournalWindows(local.RootId), "completed pair and committed cursor enable windows");
                store.SavePair(pair with { PairId = "second-pair", Name = "Pair B" });
                LocalChecks.Require(!store.SupportsJournalWindows(local.RootId), "one unfinished pair on shared root disables checkpoints");
                break;
            }
            LocalChecks.Require(delta.Changes.Any(change => rename
                ? change.HasPreviousPath(store.ByObject(local.RootId, change.ObjectId))
                : change.Kind == ChangeKind.Delete), "Run receives case-only old spelling or deletion authority");
            store.WriteDelta(delta);
            store.CompleteGeneration(delta.Gen, delta.CursorAfter, true);
            cursor = delta.CursorAfter;
        }
        if (!preparing) LocalChecks.Require(cursor == target.Cursor, "four consecutive bounded runs consume the backlog without gaps");
    }
}
