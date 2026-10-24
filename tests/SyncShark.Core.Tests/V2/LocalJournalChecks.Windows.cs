namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    internal static async Task ReplayWindowsAsync(string folder)
    {
        WindowRecordBoundary();
        WindowSizingPersistence(Path.Combine(folder, "sizing"));
        await WindowAuthorityAsync(Path.Combine(folder, "case-only"), true, false);
        await WindowAuthorityAsync(Path.Combine(folder, "delete-windows"), false, false);
        await WindowAuthorityAsync(Path.Combine(folder, "prepare-windows"), false, true);
        await ReplayWindowCaseAsync(Path.Combine(folder, "four"), 8, false);
        await ReplayWindowCaseAsync(Path.Combine(folder, "budget"), 2, false);
        await ReplayWindowCaseAsync(Path.Combine(folder, "crash"), 8, true);
        await OverflowWindowsAsync(Path.Combine(folder, "halve"), false);
        await OverflowWindowsAsync(Path.Combine(folder, "floor"), true);
        foreach (var cause in new[] { "wrap", "replace", "deleted" })
            await WindowDiscontinuityAsync(Path.Combine(folder, cause), cause);
        Console.WriteLine("PASS paged journal replay: four windows, budget, restart, halving, floor and discontinuities");
    }

    private static void WindowRecordBoundary()
    {
        var bytes = new byte[160];
        for (var i = 0; i < 2; i++)
        {
            var record = bytes.AsSpan(i * 80, 80);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record, 80);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(record[4..], 2);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(record[24..], 128 + i * 80);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(record[56..], 2);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(record[58..], 60);
            record[60] = (byte)'x';
        }
        var first = NtfsUsnJournal.ParseWindow(bytes, 128, 240, 288, out var consumed);
        LocalChecks.Require(first.Count == 1 && consumed == 208, "bisected record is not consumed and cursor reports the real boundary");
        var second = NtfsUsnJournal.ParseWindow(bytes.AsSpan(80), consumed, 288, 288, out var final);
        LocalChecks.Require(second.Count == 1 && final == 288, "next window consumes the retained record exactly once");
    }

    private static async Task ReplayWindowCaseAsync(string folder, int budget, bool crash)
    {
        var root = Path.Combine(folder, "root");
        LocalChecks.Seed(root, "anchor.txt", "unchanged");
        var settings = StorageChecks.Fast with { LocalReplayWindowsPerRun = budget };
        var start = new NtfsUsnCheckpoint(1, 0, 128, 0);
        var target = start with { NextUsn = start.NextUsn + JournalReplayWindow.ReplayWindowBytes * 7 / 2 };
        var reads = new List<long>();
        var persisted = new List<string>();
        using var initialStore = SqliteStateStore.Open(Path.Combine(folder, "store"), settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var store = initialStore;
        var local = LocalChecks.RootOf(root, "root-local");
        var pair = new PairDef("pair", "Pair A", local,
            new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System, settings: settings);
        inventory.WalkCheckpointForTest = () => (start, false);
        await SaveFull(inventory, store);
        for (var i = 0; i < 4; i++) LocalChecks.Seed(root, $"window-{i}.txt", "edit in journal window " + i);
        Configure(inventory);
        var didCrash = false;
        if (crash)
        {
            inventory.JournalWindowPersistedForTest = cursor => { persisted.Add(cursor); throw new IOException("injected between windows"); };
            try { await Read(); }
            catch (IOException error) when (error.Message == "injected between windows") { didCrash = true; }
            LocalChecks.Require(didCrash && store.Cursor(local.RootId) == persisted.Single(), "crash retains exactly the persisted window cursor");
            store.Dispose();
        }
        using var reopened = crash ? SqliteStateStore.Open(Path.Combine(folder, "store"), settings,
            TimeProvider.System, LocalItems.CaptureOptionalHash) : null;
        if (reopened is not null)
        {
            store = reopened;
            LocalChecks.Require(store.Cursor(local.RootId) == persisted.Single(), "reopening SQLite retains the committed replay boundary");
            inventory = LocalChecks.Inventory(root, store, TimeProvider.System, settings: settings);
            Configure(inventory);
        }
        var first = await Read();
        LocalChecks.Require(!first.CursorInvalid, "replay remains a delta");
        if (budget == 2)
            LocalChecks.Require(first.CursorAfter != target.Cursor && reads.Count == 2, "run budget ends after exactly two windows");
        store.WriteDelta(first);
        store.CompleteGeneration(first.Gen, first.CursorAfter, true);
        if (first.CursorAfter != target.Cursor)
        {
            var second = await Read();
            LocalChecks.Require(!second.CursorInvalid && second.CursorAfter == target.Cursor, "next run catches up as a delta");
            store.WriteDelta(second);
            store.CompleteGeneration(second.Gen, second.CursorAfter, true);
        }
        LocalChecks.Require(reads.Count == 4 && reads.Distinct().Count() == 4, "3.5 windows read exactly four nonoverlapping ranges, including restart");
        LocalChecks.Require(((ILocalGapStore)store).GapEpoch(local.RootId) is null, "large replay never begins a gap");
        LocalChecks.Require(store.Live(local.RootId).Count(item => item.Path.StartsWith("window-", StringComparison.Ordinal)) == 4,
            "edits in all windows including the end of the backlog reach the real inventory");
        LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) >= (budget == 2 ? 2 : 3), "earlier windows retain durable planning work");

        Task<Delta> Read() => inventory.ChangesSinceAsync(store.Cursor(local.RootId)!,
            store.BeginGeneration(local.RootId, "delta"), _ => true, default);
        void Configure(LocalInventory value)
        {
            value.WalkCheckpointForTest = () => (target, false);
            value.FullInventoryForTest = () => throw new InvalidOperationException("replay must not full-walk");
            value.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
                out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
            {
                reads.Add(from.NextUsn);
                var index = (from.NextUsn - start.NextUsn) / JournalReplayWindow.ReplayWindowBytes;
                var name = $"window-{index}.txt";
                var id = LocalItems.ParseObjectId(LocalChecks.ObjectIdOf(Path.Combine(root, name))).FileId;
                changes = [new(id, LocalItems.ParseObjectId(local.RootObjectId).FileId, from.NextUsn, 0x100, FileAttributes.Normal, name)];
                failure = null; kind = NtfsUsnReadFailure.Transient; return true;
            };
        }
    }

    private static async Task OverflowWindowsAsync(string folder, bool floor)
    {
        var root = Path.Combine(folder, "root");
        LocalChecks.Seed(root, "anchor.txt", "content");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var start = new NtfsUsnCheckpoint(1, 0, 128, 0);
        var target = start with { NextUsn = start.NextUsn + 2 * JournalReplayWindow.ReplayWindowBytes };
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System,
            settings: StorageChecks.Fast with { LocalDeltaMaxPaths = 1, LocalReplayWindowsPerRun = 64 });
        inventory.WalkCheckpointForTest = () => (start, false);
        await SaveFull(inventory, store);
        LocalChecks.Seed(root, "one.txt", "one"); LocalChecks.Seed(root, "two.txt", "two");
        LocalChecks.Seed(root, "early.txt", "persisted before overflow");
        var early = LocalItems.ParseObjectId(LocalChecks.ObjectIdOf(Path.Combine(root, "early.txt"))).FileId;
        var parent = LocalItems.ParseObjectId(LocalChecks.ObjectIdOf(root)).FileId;
        var one = LocalItems.ParseObjectId(LocalChecks.ObjectIdOf(Path.Combine(root, "one.txt"))).FileId;
        var two = LocalItems.ParseObjectId(LocalChecks.ObjectIdOf(Path.Combine(root, "two.txt"))).FileId;
        var sizes = new List<long>();
        inventory.WalkCheckpointForTest = () => (target, false);
        inventory.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
        {
            var size = through.NextUsn - from.NextUsn; sizes.Add(size);
            var overflow = from.NextUsn > start.NextUsn && (floor || size > JournalReplayWindow.MinimumWindowBytes * 2);
            changes = from.NextUsn == start.NextUsn
                ? [new(early, parent, from.NextUsn, 0x100, FileAttributes.Normal, "early.txt")]
                : overflow ? [new(one, parent, from.NextUsn, 0x100, FileAttributes.Normal, "one.txt"),
                    new(two, parent, from.NextUsn + 80, 0x100, FileAttributes.Normal, "two.txt")] : [];
            failure = null; kind = NtfsUsnReadFailure.Transient; return true;
        };
        var delta = await inventory.ChangesSinceAsync(start.Cursor, store.BeginGeneration("root-local", "delta"), _ => true, default);
        LocalChecks.Require(sizes.Contains(JournalReplayWindow.ReplayWindowBytes / 2), "overflow halves the window");
        if (floor)
        {
            var gap = ((ILocalGapStore)store).GapEpoch("root-local")!;
            LocalChecks.Require(delta.CursorInvalid && sizes.Last() == JournalReplayWindow.MinimumWindowBytes &&
                gap.Boundary == start.NextUsn + JournalReplayWindow.ReplayWindowBytes,
                "floor fallback protects only history after the persisted first window");
            LocalChecks.Require(store.Live("root-local", ["early.txt"]).Single().ObjectId ==
                LocalChecks.ObjectIdOf(Path.Combine(root, "early.txt")), "floor fallback retains the earlier window's persisted change");
        }
        else LocalChecks.Require(!delta.CursorInvalid && delta.CursorAfter == target.Cursor &&
            ((ILocalGapStore)store).GapEpoch("root-local") is null, "halving fits and replays without a gap");
    }

    private static async Task WindowDiscontinuityAsync(string folder, string cause)
    {
        var root = Path.Combine(folder, "root"); LocalChecks.Seed(root, "anchor.txt", "content");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var start = new NtfsUsnCheckpoint(1, 0, 128, 0);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.WalkCheckpointForTest = () => (start, false);
        await SaveFull(inventory, store);
        inventory.WalkCheckpointForTest = () => cause == "deleted" ? (null, true) :
            (new NtfsUsnCheckpoint(cause == "replace" ? 2UL : 1UL, cause == "wrap" ? 256 : 0, 512, 0), false);
        var delta = await inventory.ChangesSinceAsync(start.Cursor, store.BeginGeneration("root-local", "delta"), _ => true, default);
        LocalChecks.Require(delta.CursorInvalid && ((ILocalGapStore)store).GapEpoch("root-local") is not null,
            "real discontinuity still protects the full inventory: " + cause);
        var snapshot = await inventory.FullAsync(store.BeginGeneration("root-local", "full"), _ => true, null!, default);
        LocalChecks.Require(snapshot.Items.Any(item => item.Path == "anchor.txt"), "protected full walk retains existing file");
    }
}
