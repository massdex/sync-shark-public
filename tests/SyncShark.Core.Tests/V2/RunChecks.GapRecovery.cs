using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Real ScanSide/AdvanceCursors, SQLite and directory IO; all journal reads are injected.
    internal static async Task GapRecoveryChecksAsync(string folder)
    {
        var failures = new List<Exception>();
        foreach (var cause in new[] { "overflow", "retained", "replaced", "ahead", "untrusted", "replace-pending", "read-outage", "read-timeout", "cloud-outage", "preview", "evidence" })
        {
            try
            {
                if (cause == "evidence") await GapEvidenceSettlementAsync(Path.Combine(folder, cause));
                else await GapRecoveryAsync(Path.Combine(folder, cause), cause);
            }
            catch (Exception error) { failures.Add(new InvalidOperationException(cause + ": " + error.Message, error)); }
        }
        foreach (var (name, check) in new (string, Func<string, Task>)[]
        {
            ("catch-up-unreadable-subtree", CatchUpUnreadableSubtreeAsync),
            ("targeted-root-open", TargetedRootOpenFailureBecomesDebtAsync)
        })
        {
            try { await check(Path.Combine(folder, name)); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name + ": " + error.Message, error)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private sealed class GapWalkInterrupted : Exception { }

    private static async Task GapRecoveryAsync(string folder, string cause)
    {
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        LocalChecks.Seed(directory, "file.txt", "old");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var start = new NtfsUsnCheckpoint(1, 0, cause == "ahead" ? 900 : 100, 0);
        var checkpoint = new NtfsUsnCheckpoint(cause == "replaced" ? 2UL : 1UL,
            cause == "retained" ? 150 : 0, 200, 0);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings, TimeProvider.System, pairId: pair.PairId)
        { WalkCheckpointForTest = () => (start, false) };
        var gen = store.BeginGeneration(root.RootId, "full");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, default);
        var original = snapshot.Items.Single() with { Hash = DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("old")) };
        store.WriteSnapshot(snapshot with { Items = [original] });
        store.CompleteGeneration(gen, start.Cursor, true);
        var gaps = (ILocalGapStore)store;
        if (cause == "untrusted") gaps.BeginGap(root.RootId, "", 0, false);
        if (cause == "replace-pending")
        {
            gaps.BeginGap(root.RootId, checkpoint.Cursor, 100, true);
            checkpoint = new NtfsUsnCheckpoint(2, 0, 250, 0);
        }
        inventory.WalkCheckpointForTest = () => (checkpoint, false);
        var overflow = cause is "overflow" or "untrusted" or "read-outage" or "read-timeout" or "cloud-outage" or "preview";
        var reads = new List<long>();
        var emitWrite = false;
        inventory.JournalReadForTest = ReadJournal;
        bool ReadJournal(string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct)
        {
            reads.Add(from.NextUsn);
            changes = [];
            failure = null;
            kind = NtfsUsnReadFailure.OverLimit;
            if (overflow) { overflow = false; failure = "synthetic overflow"; return false; }
            if (emitWrite && from.NextUsn <= 260 && through.NextUsn > 260)
            {
                var identity = LocalItems.ParseObjectId(original.ObjectId);
                changes = [new(identity.FileId, LocalItems.ParseObjectId(root.RootObjectId).FileId,
                    260, 1, FileAttributes.Normal, original.Path)];
            }
            return true;
        }
        inventory.DirectoryCompletedForTest = _ =>
        {
            if (cause == "read-timeout") throw new TimeoutException("synthetic mid-walk read timeout");
            if (cause == "read-outage") throw new IOException("synthetic mid-walk outage");
            throw new GapWalkInterrupted();
        };
        fx.Provider.AddInventory(root, inventory);
        if (cause == "preview")
        {
            inventory.DirectoryCompletedForTest = null;
            var (serial, _) = LocalItems.ParseObjectId(root.RootObjectId);
            var grows = 0;
            var queries = 0;
            inventory.CapacityQueryForTest = _ => { queries++; return [1, 0, 200, 0, JournalSizing.Minimum,
                SyncSharkJournalNative.Delta, 1000 * JournalSizing.Minimum, serial]; };
            inventory.CapacityQueueForTest = (_, _, _) => grows++;
            var plan = await fx.Run().PreviewIncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(plan.Plan.RunUnavailable is null && queries > 0 && grows == 0 && inventory.ObserveOnly,
                "real preview pipeline observes overflow without ever scheduling journal growth");
            Require(store.PendingJournal(serial)?.State.Requested > JournalSizing.Minimum &&
                store.OpenNotices(pair.PairId).Any(JournalSizing.IsNotice), "preview retains capacity recommendation");
            Require(fx.WriterA.Calls + fx.WriterB.Calls == 0, "overflow preview executes no endpoint effect");
            return;
        }
        try
        {
            var report = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            if (cause is "read-outage" or "read-timeout")
                Require(report.Outcome == RunOutcomes.Partial && store.SyncRetryAt(pair.PairId, "") is { Kind: "scan" } &&
                    store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry"),
                    "read outage finishes Partial with durable root retry and notice");
        }
        catch (GapWalkInterrupted) { }
        var epoch = gaps.GapEpoch(root.RootId)!;
        Require(epoch is not null && store.GapInventoryPending(root.RootId) && store.Cursor(root.RootId) == checkpoint.Cursor,
            cause + ": interrupted run publishes checkpoint and retains full obligation");
        Require(epoch!.TrustFileUsn == (cause is "overflow" or "retained" or "read-outage" or "read-timeout" or "cloud-outage"), cause + ": correct trust after gap routing");
        // The injected non-IO exception above models an abrupt stop. Do not let its
        // synthetic run retry mask the independent opposite-root read obligation.
        if (cause == "cloud-outage") store.ClearSyncRetry(pair.PairId, "");
        if (cause is "read-outage" or "read-timeout")
        {
            var retry = store.SyncRetryAt(pair.PairId, "")!;
            store.SaveSyncRetry(pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
        }
        var refresh = store.BeginGeneration(root.RootId, "quarantine-refresh");
        store.CompleteGeneration(refresh, checkpoint.Cursor, true);
        Require(store.GapInventoryPending(root.RootId), cause + ": targeted refresh cannot clear obligation");
        // Publish independently completed verification BEFORE the outage run. Otherwise
        // the unrelated background gap hold would correctly prevent the later upload,
        // or promoting it on the recovery run would itself supply a dirty-path handoff.
        if (cause is "overflow" or "cloud-outage")
        {
            gaps.GapDiscovered(root.RootId, epoch, original, false);
            gaps.GapDiscoveryDone(root.RootId, epoch);
        }
        // Discard only this fixture's resumable walk to model a crash before a page checkpoint.
        store.ForgetLocalWalk(root.RootId);
        LocalChecks.Seed(directory, "file.txt", "new");
        File.SetLastWriteTimeUtc(Path.Combine(directory, "file.txt"), original.Mtime.UtcDateTime);
        checkpoint = checkpoint with { NextUsn = 300 };
        inventory.DirectoryCompletedForTest = null;
        emitWrite = true;
        reads.Clear();
        ScriptedInventory? cloud = null;
        if (cause == "cloud-outage")
        {
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            // A newly discovered entity has no prior rehash handoff to mask lost root planning debt.
            LocalChecks.Seed(directory, "gap-added.txt", "gap-created");
            fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
                LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default));
            cloud = new ScriptedInventory(fx.B) { FullError = new IOException("cloud root unavailable"),
                ChangesError = new IOException("cloud root unavailable") };
            fx.Provider.AddInventory(pair.B, cloud);
        }
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(gaps.GapEpoch(root.RootId)!.Epoch == epoch.Epoch, cause + ": next run preserves interrupted epoch");
        Require(reads.Count == 1 && reads[0] == (cause == "replace-pending" ? 250 : 200),
            cause + ": run catch-up replays advanced checkpoint, not newer walk start");
        Require(store.ByObject(root.RootId, original.ObjectId)?.Hash == DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("new")),
            cause + ": same-size same-mtime crash-window write is rehashed by the run path");
        Require(!store.GapInventoryPending(root.RootId) && store.Cursor(root.RootId) == checkpoint.Cursor,
            cause + ": completed full side settles obligation and commits fresh cursor");
        if (cause == "cloud-outage")
        {
            Require(fx.WriterB.Puts == 0 && store.SyncRetryAt(pair.PairId, "") is { Kind: "scan" },
                "cloud root outage blocks gap-time upload and retains root retry");
            cloud!.FullError = cloud.ChangesError = null;
            var retry = store.SyncRetryAt(pair.PairId, "")!;
            store.SaveSyncRetry(pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
            emitWrite = false; // No delta carries the edit again: only durable root retry can recover it.
            // The targeted root retry uses the same directory scanner as a full walk.
            // Do not poison that required reread with the full-walk interruption hook.
            await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require((await fx.B.ObserveAsync("file.txt", default))?.Hash == DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("new")) &&
                (await fx.B.ObserveAsync("gap-added.txt", default))?.Hash == DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("gap-created")) &&
                store.SyncRetryAt(pair.PairId, "") is null, "next run syncs gap-time edit and newly discovered file through root retry with an empty delta");
        }
        if (cause is "read-outage" or "read-timeout")
            Require(store.SyncRetryAt(pair.PairId, "") is null &&
                !store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path == ""),
                "successful full retry retires root read debt and notice");
    }
    private static async Task GapEvidenceSettlementAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        LocalChecks.Seed(directory, "held.txt", "keep");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var before = new NtfsUsnCheckpoint(1, 0, 100, 0);
        var covered = before with { NextUsn = 200 };
        var through = before with { NextUsn = 300 };
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings, TimeProvider.System)
            { WalkCheckpointForTest = () => (before, false) };
        var gen = store.BeginGeneration(root.RootId, "full");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, default);
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(gen, before.Cursor, true);
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, covered.Cursor, 100, true);
        var epoch = gaps.GapEpoch(root.RootId)!.Epoch;
        var evidence = new DeletionEvidenceRecord("held.txt", null, covered.Cursor);
        store.StoreDeletionEvidence(pair.PairId, root.RootId, [evidence]);
        gen = store.BeginGeneration(root.RootId, "full");
        var other = store.BeginGeneration(pair.B.RootId, "delta");
        var full = new SyncRun.SideScan(gen, through.Cursor, true, true, [], [],
            new Dictionary<string, DateTimeOffset?>(), [])
        {
            PendingDeletionEvidence = new Dictionary<string, string> { ["held.txt"] = "evidence pending" }
        };
        var remote = new SyncRun.SideScan(other, "0", true, false, [], [],
            new Dictionary<string, DateTimeOffset?>(), []);
        fx.Run().AdvanceCursors(new(full, remote));
        Require(!store.GapInventoryPending(root.RootId) && store.Cursor(root.RootId) == covered.Cursor,
            "completed full coverage settles independently of unreleased deletion evidence; effect cursor stays pinned");
        Require(store.DeletionEvidenceRows(pair.PairId, root.RootId).Single() == evidence &&
            gaps.GapEpoch(root.RootId)!.Epoch == epoch && gaps.HasGapProtection(root.RootId) &&
            store.Live(root.RootId).Single().Path == "held.txt",
            "settlement preserves durable deletion evidence, retained entity and gap protection");
        inventory.WalkCheckpointForTest = () => (through, false);
        inventory.DirectoryCompletedForTest = _ => throw new InvalidOperationException("evidence forced a repeated full inventory");
        // Finding 6: remove the file while its evidence is still pending. "No deletes" is otherwise
        // trivial because the file is still on disk; an unresolved absence must never be delete authority.
        File.Delete(Path.Combine(directory, "held.txt"));
        var reads = 0;
        inventory.JournalReadForTest = ReadJournal;
        bool ReadJournal(string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint to,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct)
        {
            Require(from.NextUsn == 200, "evidence replay starts at the retained checkpoint");
            reads++; changes = []; failure = null; kind = NtfsUsnReadFailure.Transient; return true;
        }
        fx.Provider.AddInventory(root, inventory);
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(reads == 1 && !store.GapInventoryPending(root.RootId) &&
            store.DeletionEvidenceRows(pair.PairId, root.RootId).Single() == evidence &&
            store.Live(root.RootId).Single().Path == "held.txt" &&
            fx.WriterA.Deletes + fx.WriterB.Deletes == 0,
            "next run uses bounded delta replay, retains the vanished entity and its evidence, and authorizes no deletion");
    }

    // Finding 2 (F12): a local full walk with one unreadable subfolder reaches the journal catch-up
    // path (the snapshot carries a checkpoint). The unread scope must survive that path: its retry and
    // notice stay open, planning protects it, the run is Partial, nothing is deleted, and a later
    // successful reread closes the debt. Real ACLs and scanner; journal reads are injected.
    [SupportedOSPlatform("windows")]
    private static async Task CatchUpUnreadableSubtreeAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        LocalChecks.Seed(directory, "a.txt", "alpha");
        LocalChecks.Seed(directory, "locked/hidden.txt", "hidden");
        var locked = Path.Combine(directory, "locked");
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        AddDirectoryRule(locked, deny);
        try
        {
            using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var root = LocalChecks.RootOf(directory, "ra");
            var pair = fx.Pair with { A = root };
            store.SavePair(pair);
            store.MarkFirstSyncDone(pair.PairId);
            var checkpoint = new NtfsUsnCheckpoint(1, 0, 200, 0);
            var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings,
                TimeProvider.System, pairId: pair.PairId) { WalkCheckpointForTest = () => (checkpoint, false) };
            inventory.JournalReadForTest = NoJournalChanges;
            fx.Provider.AddInventory(root, inventory);
            // The healthy sibling is uploaded from the real disk, not from the fake endpoint.
            fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
                LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default));
            var report = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(report.Failed == 0,
                "an unreadable subfolder on the catch-up path is never a run failure (failed=" + report.Failed +
                (report.Failures.Count > 0 ? ": " + report.Failures[0].Reason : "") + ")");
            Require(report.Outcome == RunOutcomes.Partial,
                "an unreadable subfolder on the catch-up path reports Partial, never Complete");
            Require(store.SyncRetryAt(pair.PairId, "locked") is { Kind: "scan" },
                "the catch-up path keeps the unread folder's durable retry");
            Require(store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path == "locked"),
                "the catch-up path keeps the unread folder's notice");
            Require(fx.WriterA.Deletes + fx.WriterB.Deletes == 0,
                "nothing inside the unread folder is deleted");
            Require(await fx.B.ObserveAsync("locked", default) is null &&
                await fx.B.ObserveAsync("locked/hidden.txt", default) is null,
                "nothing inside the unread folder is uploaded");
            Require(await fx.B.ObserveAsync("a.txt", default) is not null,
                "a readable sibling still syncs while the unread folder is held");
            RemoveDirectoryRule(locked, deny);
            store.SaveSyncRetry(pair.PairId,
                store.SyncRetryAt(pair.PairId, "locked")! with { RetryAfter = DateTimeOffset.MinValue });
            var recovered = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(recovered.Failed == 0 && store.SyncRetryAt(pair.PairId, "locked") is null &&
                !store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path == "locked"),
                "a later successful reread retires the unread folder's retry and closes its notice");
            Require(await fx.B.ObserveAsync("locked/hidden.txt", default) is not null,
                "the reread syncs the previously unread content");
        }
        finally
        {
            try { RemoveDirectoryRule(locked, deny); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Finding 4 leftover: an ArgumentException/Win32Exception opening the root for a targeted reread
    // is read debt plus a notice, never a failed run. The scan itself succeeds (journal injected);
    // only the targeted open fails.
    private static async Task TargetedRootOpenFailureBecomesDebtAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        LocalChecks.Seed(directory, "sub/file.txt", "content");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 100, 0);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings,
            TimeProvider.System, pairId: pair.PairId) { WalkCheckpointForTest = () => (checkpoint, false) };
        inventory.JournalReadForTest = NoJournalChanges;
        fx.Provider.AddInventory(root, inventory);
        // The fixture's file is read from the real disk, not from the fake endpoint.
        fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default));
        var first = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(first.Failed == 0,
            "fixture run reports no failure (failed=" + first.Failed +
            (first.Failures.Count > 0 ? ": " + first.Failures[0].Reason : "") + ")");
        Require(store.Cursor(root.RootId) is { Length: > 0 },
            "fixture commits a local cursor (saw '" + (store.Cursor(root.RootId) ?? "<null>") + "')");
        store.SaveSyncRetry(pair.PairId,
            new SyncRetry("sub", "scan", 1, "synthetic read debt", DateTimeOffset.MinValue));
        inventory.BeforeTargetedRefreshLeaseForTest = () => throw new ArgumentException("synthetic root open failure");
        var debt = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(debt.Failed == 0 && debt.Outcome == RunOutcomes.Partial,
            "an unopenable root during a targeted reread becomes read debt, not a run failure");
        Require(store.SyncRetryAt(pair.PairId, "sub") is { Kind: "scan" } &&
            store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path == "sub"),
            "the targeted retry and its notice survive the unopenable root");
        inventory.BeforeTargetedRefreshLeaseForTest = null;
        store.SaveSyncRetry(pair.PairId,
            store.SyncRetryAt(pair.PairId, "sub")! with { RetryAfter = DateTimeOffset.MinValue });
        var recovered = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(recovered.Failed == 0 && store.SyncRetryAt(pair.PairId, "sub") is null &&
            !store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path == "sub"),
            "a later successful targeted reread retires the retry and closes its notice");
    }

    private static bool NoJournalChanges(string rootPath, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
        out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind,
        CancellationToken ct)
    {
        changes = [];
        failure = null;
        kind = NtfsUsnReadFailure.Transient;
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void AddDirectoryRule(string path, FileSystemAccessRule rule)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        security.AddAccessRule(rule);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveDirectoryRule(string path, FileSystemAccessRule rule)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        security.RemoveAccessRule(rule);
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
