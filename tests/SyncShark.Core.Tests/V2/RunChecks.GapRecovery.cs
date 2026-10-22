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
            ("catch-up-fresh-fallback", CatchUpFreshFallbackUnreadableSubtreeAsync),
            ("catch-up-synced-folder-in-gap", GapRetainsEarlierSyncedUnreadableFolderAsync),
            ("catch-up-second-run", CatchUpRetrySurvivesSecondCatchUpAsync),
            ("sleeping-root-retry-replay", SleepingLocalRootRetryClearsAfterReplayAsync),
            ("other-root-outage-sleeping-retry", OtherRootOutageSkipsPairLoadFromSleepingRetryAsync),
            ("failed-due-root-retry", FailedDueRootRetryDoesNotLoadPairAsync),
            ("targeted-root-open", TargetedRootOpenFailureBecomesDebtAsync)
        })
        {
            try { await check(Path.Combine(folder, name)); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name + ": " + error.Message, error)); }
        }
        try { CatchUpEvidenceMergeTest(); }
        catch (Exception error) { failures.Add(new InvalidOperationException("catch-up-evidence-merge: " + error.Message, error)); }
        try { ScanFailureTextChecksTest(); }
        catch (Exception error) { failures.Add(new InvalidOperationException("scan-failure-text: " + error.Message, error)); }
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
            // F2: one notice per path, in plain language with a concrete fix. The raw scanner
            // diagnostic ("could not be enumerated safely") must not reach the user.
            var lockedNotice = store.OpenNotices(pair.PairId)
                .Single(n => n.ReasonKey == "sync-retry" && n.Path == "locked");
            Require(lockedNotice.Detail.Contains("could not open this folder", StringComparison.OrdinalIgnoreCase) &&
                lockedNotice.Detail.Contains("restore read access", StringComparison.OrdinalIgnoreCase) &&
                lockedNotice.Detail.Contains("choose retry", StringComparison.OrdinalIgnoreCase) &&
                !lockedNotice.Detail.Contains("enumerated safely", StringComparison.Ordinal),
                "the unreadable-subfolder notice is a plain cause plus fix, not the raw scanner text");
            Require(store.SyncRetryAt(pair.PairId, "locked") is { LastError: var lockedCause } &&
                lockedCause.Contains("could not be enumerated", StringComparison.OrdinalIgnoreCase),
                "the durable retry keeps the raw scanner cause for support and the next run");
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

    // Follow-up 2a: the CursorInvalid fresh-fallback branch of the local catch-up must carry the
    // fresh walk's unread scope exactly like the ordinary branches: retry and notice survive, the run
    // is Partial, nothing in the unread folder is deleted or uploaded, and a sibling still syncs.
    [SupportedOSPlatform("windows")]
    private static async Task CatchUpFreshFallbackUnreadableSubtreeAsync(string folder)
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
            // The catch-up replay itself is unavailable, which selects the fresh-fallback branch.
            inventory.JournalReadForTest = OverLimitJournal;
            fx.Provider.AddInventory(root, inventory);
            // The local inventory's content hash is the Dropbox content hash; make the fake provider
            // agree so an uploaded sibling carries the hash the planner compared.
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
                LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default));
            var report = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(report.Failed == 0, "the fresh-fallback catch-up path is never a run failure (failed=" + report.Failed +
                (report.Failures.Count > 0 ? ": " + report.Failures[0].Reason : "") + ")");
            Require(report.Outcome == RunOutcomes.Partial,
                "the fresh-fallback catch-up path with an unreadable subfolder reports Partial, never Complete");
            Require(store.SyncRetryAt(pair.PairId, "locked") is { Kind: "scan" },
                "the fresh-fallback branch keeps the unread folder's durable retry");
            Require(store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path == "locked"),
                "the fresh-fallback branch keeps the unread folder's notice");
            Require(fx.WriterA.Deletes + fx.WriterB.Deletes == 0,
                "nothing inside the unread folder is deleted on the fresh-fallback branch");
            Require(await fx.B.ObserveAsync("locked", default) is null &&
                await fx.B.ObserveAsync("locked/hidden.txt", default) is null,
                "nothing inside the unread folder is uploaded on the fresh-fallback branch");
            Require(store.Live(root.RootId, ["a.txt"]).Count == 1,
                "the fresh-fallback walk still stores the readable sibling");
            // Finding 2: the whole-root inventory obligation settles for everything the walk could
            // read. The unreadable folder becomes scoped debt with its own retry and notice, which
            // keep it protected without forcing a repeated full walk.
            Require(!store.GapInventoryPending(root.RootId),
                "a complete readable walk clears the whole-root gap obligation while one subfolder is unreadable");
            // Finding 1: once the gap's own metadata verification has discharged the verified readable
            // entity, a following delta run plans that sibling while the unreadable folder stays
            // protected with its retry and notice. Real gap store, real ACL, injected journal reads.
            var gaps = (ILocalGapStore)store;
            var epoch = gaps.GapEpoch(root.RootId)!;
            var sibling = store.Live(root.RootId, ["a.txt"]).Single();
            var siblingId = LocalItems.ParseObjectId(sibling.ObjectId);
            // F7: this drives the gap discharge through the store seams. It stands in for the
            // background worker's proved-unchanged result; it does not exercise the worker's real
            // journal proof or its per-file hash, though the promotion below is the real store path.
            gaps.GapDiscovered(root.RootId, epoch, sibling, requiresHash: false);
            gaps.GapDiscoveryDone(root.RootId, epoch);
            gaps.PromoteGapDiscovery(root.RootId);
            Require(!gaps.HasGapProtection(root.RootId),
                "the gap's metadata verification discharges verified readable entities");
            // Same length and mtime as the stored row: the write evidence selects the rehash lane.
            LocalChecks.Seed(directory, "a.txt", "bravo");
            File.SetLastWriteTimeUtc(Path.Combine(directory, "a.txt"), sibling.Mtime.UtcDateTime);
            var reads = new List<long>();
            inventory.JournalReadForTest = ReadJournal;
            bool ReadJournal(string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
                out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind,
                CancellationToken ct)
            {
                reads.Add(from.NextUsn);
                changes = [new(siblingId.FileId, LocalItems.ParseObjectId(root.RootObjectId).FileId,
                    260, 1, FileAttributes.Normal, "a.txt")];
                failure = null;
                kind = NtfsUsnReadFailure.Transient;
                return true;
            }
            var following = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(following.Failed == 0 && following.Outcome == RunOutcomes.Partial,
                "the following run is Partial while the unreadable folder is still debt");
            Require(reads.Count == 1,
                "the following run replays from the committed delta cursor instead of repeating the full walk");
            Require(await fx.B.ObserveAsync("a.txt", default) is { } synced &&
                synced.Hash == DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("bravo")),
                "the following run syncs the readable sibling");
            Require(fx.WriterA.Deletes + fx.WriterB.Deletes == 0 &&
                await fx.B.ObserveAsync("locked", default) is null &&
                await fx.B.ObserveAsync("locked/hidden.txt", default) is null,
                "the unreadable folder stays protected from planning, upload and deletion");
            Require(store.SyncRetryAt(pair.PairId, "locked") is { Kind: "scan" } &&
                store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path == "locked"),
                "the unreadable folder keeps its retry and notice across the following run");
            // Once access is restored the scoped debt is read, synced, and its notice closes.
            RemoveDirectoryRule(locked, deny);
            store.SaveSyncRetry(pair.PairId,
                store.SyncRetryAt(pair.PairId, "locked")! with { RetryAfter = DateTimeOffset.MinValue });
            var recovered = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(recovered.Failed == 0 && store.SyncRetryAt(pair.PairId, "locked") is null &&
                !store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path == "locked"),
                "restored access reads the folder, retires its retry and closes its notice");
            Require(await fx.B.ObserveAsync("locked/hidden.txt", default) is not null,
                "the restored folder's content syncs");
        }
        finally
        {
            try { RemoveDirectoryRule(locked, deny); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // F1 (review-gapscope-1): the core claim of the settle change is a folder that was SYNCED EARLIER
    // (stored rows and a baseline), then receives a same-size, same-mtime content edit and becomes
    // unreadable (an ACL deny) during a journal gap. The whole-root gap obligation must still settle
    // because the fresh walk covered every readable path, while the folder keeps gap protection, its
    // durable retry and its notice and nothing inside it is deleted or uploaded. Once access returns,
    // the gap's per-file check must observe the new content - the same-size, same-mtime write a
    // metadata-only re-read cannot see - sync it and close the notice. Real ACLs, scanner, store and
    // LocalWriter; journal reads are injected. This lives on a fresh fixture root.
    [SupportedOSPlatform("windows")]
    private static async Task GapRetainsEarlierSyncedUnreadableFolderAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        LocalChecks.Seed(directory, "a.txt", "alpha");
        LocalChecks.Seed(directory, "locked/hidden.txt", "old");
        var locked = Path.Combine(directory, "locked");
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
            AccessControlType.Deny);
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
            // The local inventory's content hash is the Dropbox content hash; make the fake endpoints
            // agree so a later upload can be compared with the local scan's hash.
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
                LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default));
            var first = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(first.Failed == 0, "the fixture syncs the folder before the gap (failed=" + first.Failed +
                (first.Failures.Count > 0 ? ": " + first.Failures[0].Reason : "") + ")");
            var held = store.Live(root.RootId, ["locked/hidden.txt"]).Single();
            var oldHash = LocalChecks.BlockHash(LocalChecks.Bytes("old"));
            Require(held.Hash == oldHash &&
                store.BaselinesTouching(pair.PairId, Side.A, [held.ObjectId], []).Count == 1 &&
                await fx.B.ObserveAsync("locked/hidden.txt", default) is { } uploaded && uploaded.Hash == oldHash,
                "the folder was synced earlier: it has a stored row, a baseline and the old content in the cloud");
            // Same length and mtime as the stored row, different content: only a content hash can see it.
            LocalChecks.Seed(directory, "locked/hidden.txt", "new");
            File.SetLastWriteTimeUtc(Path.Combine(locked, "hidden.txt"), held.Mtime.UtcDateTime);
            AddDirectoryRule(locked, deny);
            // The catch-up replay overflows, which starts a journal gap followed by a fresh full walk.
            inventory.JournalReadForTest = OverLimitJournal;
            var gapRun = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(gapRun.Failed == 0 && gapRun.Outcome == RunOutcomes.Partial,
                "the gap run with an unreadable synced folder is Partial, never a failure (failed=" + gapRun.Failed +
                (gapRun.Failures.Count > 0 ? ": " + gapRun.Failures[0].Reason : "") + ")");
            Require(!store.GapInventoryPending(root.RootId),
                "the whole-root gap obligation clears once the fresh walk covers every readable path");
            var gaps = (ILocalGapStore)store;
            var epoch = gaps.GapEpoch(root.RootId);
            Require(epoch is not null && gaps.HasGapProtection(root.RootId),
                "the unreadable, previously synced folder keeps gap protection");
            Require(store.SyncRetryAt(pair.PairId, "locked") is { Kind: "scan" },
                "the unreadable folder keeps its durable retry");
            Require(store.OpenNotices(pair.PairId).Count(n => n.ReasonKey == "sync-retry" && n.Path == "locked") == 1,
                "the unreadable folder keeps exactly one notice");
            Require(fx.WriterA.Deletes + fx.WriterB.Deletes == 0 &&
                await fx.B.ObserveAsync("locked/hidden.txt", default) is { } blocked && blocked.Hash == oldHash,
                "nothing inside the unreadable folder is deleted or uploaded while it is held");
            // Drive the gap's metadata verification through the store seams. This stands in for the
            // background worker (this fixture has no real journal to probe); the promotion is the real
            // store path. The readable sibling is proved unchanged; the unreadable file still needs a hash.
            var discovered = gaps.GapDiscoveryPage(root.RootId, epoch!, 128)
                .Select(item => (item, requiresHash: item.Path == "locked/hidden.txt")).ToArray();
            gaps.GapDiscoveredBatch(root.RootId, epoch!, discovered);
            gaps.GapDiscoveryDone(root.RootId, epoch!);
            gaps.PromoteGapDiscovery(root.RootId);
            Require(gaps.HasGapProtection(root.RootId),
                "after metadata discovery the unverified file still protects the folder while the readable sibling retires");
            var pending = gaps.GapHashPage(root.RootId, epoch!, 16);
            Require(pending.Count == 1 && pending[0].Path == "locked/hidden.txt",
                "only the unreadable folder's file still needs the gap's per-file check");
            // Access returns. The worker would now hash the file because metadata discovery could not
            // prove it unchanged; stage that result through the same store seams.
            RemoveDirectoryRule(locked, deny);
            var file = Path.Combine(locked, "hidden.txt");
            var newHash = DropboxContentHash.Compute(File.ReadAllBytes(file));
            var checkedItem = gaps.GapHashPage(root.RootId, epoch!, 16).Single();
            gaps.StageGapHash(root.RootId, epoch!, checkedItem, newHash,
                new LocalGapProof(epoch!.JournalCursor, new NtfsFileUsn(1,
                    LocalItems.ParseObjectId(checkedItem.ObjectId).FileId, 150, checkedItem.Size,
                    checkedItem.Mtime.UtcDateTime.ToFileTimeUtc())));
            var verified = gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 128).Single();
            Require(gaps.AcceptGapResult(root.RootId, verified) &&
                store.Live(root.RootId, ["locked/hidden.txt"]).Single().Hash == newHash,
                "the gap's per-file check publishes the new content hash for the same-size, same-mtime write");
            store.SaveSyncRetry(pair.PairId,
                store.SyncRetryAt(pair.PairId, "locked")! with { RetryAfter = DateTimeOffset.MinValue });
            inventory.JournalReadForTest = NoJournalChanges;
            var recovered = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(recovered.Failed == 0 && store.SyncRetryAt(pair.PairId, "locked") is null &&
                !store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path == "locked"),
                "restored access reads the folder, retires its retry and closes its notice");
            Require(await fx.B.ObserveAsync("locked/hidden.txt", default) is { } synced && synced.Hash == newHash,
                "the new content is synced once the gap's per-file check has observed it");
            Require(fx.WriterA.Deletes + fx.WriterB.Deletes == 0,
                "the recovery never deletes anything");
        }
        finally
        {
            try { RemoveDirectoryRule(locked, deny); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // F2/F4 (review-gapscope-1): every notice text carries a cause and a fix; the online-only
    // observation message's exact prefix is unchanged because notice closing and retry matching depend
    // on it; the journal text points at the separate capacity notice instead of promising an
    // enlargement this notice cannot offer.
    private static void ScanFailureTextChecksTest()
    {
        Require(ScanFailureText.Describe(OwnedPointerProof.ObservationMessage) == OwnedPointerProof.ObservationMessage,
            "the online-only observation message keeps its exact prefix");
        foreach (var diagnostic in new[]
        {
            "A local folder could not be enumerated safely.",
            "The selected local root could not be enumerated.",
            "parent listing failed at sub: Access is denied.",
        })
        {
            var text = ScanFailureText.Describe(diagnostic);
            Require(text.Contains("could not open this folder", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("restore read access", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("choose retry", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("enumerated", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("parent listing failed", StringComparison.OrdinalIgnoreCase),
                "a folder enumeration/listing diagnostic is a plain cause plus fix, never the raw text: " + text);
        }
        var journal = ScanFailureText.Describe("journal overflow while replaying the saved cursor");
        Require(!journal.Contains("let Sync Shark enlarge it", StringComparison.OrdinalIgnoreCase) &&
            journal.Contains("change-log capacity notice", StringComparison.OrdinalIgnoreCase),
            "the journal notice points at the change-log capacity notice for enlargement");
        var passthrough = ScanFailureText.Describe("A Defender history check was incomplete.");
        Require(passthrough.StartsWith("A Defender history check was incomplete.", StringComparison.Ordinal) &&
            passthrough.Contains("then choose retry", StringComparison.OrdinalIgnoreCase),
            "an unrecognised user-facing reason keeps its cause and gains a generic fix");
    }

    // Follow-up 2b: an already-existing retry must survive a SECOND catch-up run (the first run
    // created it; the second deliberately takes the catch-up path again while the folder is still
    // unreadable). The old test only re-read through the delta branch.
    [SupportedOSPlatform("windows")]
    private static async Task CatchUpRetrySurvivesSecondCatchUpAsync(string folder)
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
            fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
                LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default));
            var first = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(first.Outcome == RunOutcomes.Partial && store.SyncRetryAt(pair.PairId, "locked") is { Kind: "scan" },
                "the first catch-up run creates the unread folder's retry");
            // Model an interrupted run: without a committed cursor the next run takes a full walk
            // with a checkpoint again, i.e. the catch-up path, while the folder is still unreadable.
            store.ForgetCursor(root.RootId);
            var second = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(second.Outcome == RunOutcomes.Partial,
                "a second catch-up run while the folder is still unreadable reports Partial");
            Require(store.SyncRetryAt(pair.PairId, "locked") is { Kind: "scan" },
                "an existing retry survives a second catch-up run");
            Require(store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path == "locked"),
                "the existing notice survives a second catch-up run");
        }
        finally
        {
            try { RemoveDirectoryRule(locked, deny); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Follow-up 2c: a sleeping whole-root LOCAL retry must clear after a clean journal replay even
    // though its scheduled retry is not due (the replay from the unadvanced cursor is authoritative).
    private static async Task SleepingLocalRootRetryClearsAfterReplayAsync(string folder)
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
        fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default));
        var first = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(first.Failed == 0 && store.Cursor(root.RootId) is { Length: > 0 },
            "the fixture commits a local cursor before the sleeping-retry run");
        // Synthetic durable whole-root debt that is still sleeping on its backoff.
        store.SaveSyncRetry(pair.PairId,
            new SyncRetry(string.Empty, "scan", 1, "synthetic whole-root read debt", DateTimeOffset.UtcNow.AddMinutes(5)));
        store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.Info, string.Empty,
            "synthetic whole-root retry notice", "sync-retry")]);
        var replay = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(replay.Failed == 0 && store.SyncRetryAt(pair.PairId, string.Empty) is null,
            "a sleeping whole-root local retry clears after a clean journal replay");
        Require(!store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path.Length == 0),
            "the whole-root retry notice closes with the settled local debt");
    }

    // Finding 3: a whole-root retry from an earlier outage on one side, still sleeping on its backoff,
    // must not load the whole pair when the OTHER side newly loses its root on a delta read (the local
    // journal becomes temporarily unavailable, so the run is not a failed full side). Nothing can be
    // authorized while that root is unreadable and the debt cannot retire, so the stored rows are not
    // joined. Only the settling run, with both roots readable, loads the pair once and retires the debt.
    private static async Task OtherRootOutageSkipsPairLoadFromSleepingRetryAsync(string folder)
    {
        var dirA = Path.Combine(folder, "a");
        var dirB = Path.Combine(folder, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        LocalChecks.Seed(dirA, "seed.txt", "seed");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store, record: true, localLocal: true);
        var rootA = LocalChecks.RootOf(dirA, "ra");
        var rootB = LocalChecks.RootOf(dirB, "rb");
        var pair = fx.Pair with { A = rootA, B = rootB };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 100, 0);
        var journalLostB = false;
        var inventoryA = new LocalInventory(rootA, Side.A, store, new HashSet<string>(), Settings,
            TimeProvider.System, pairId: pair.PairId)
            { WalkCheckpointForTest = () => (checkpoint, false), JournalReadForTest = NoJournalChanges };
        var inventoryB = new LocalInventory(rootB, Side.B, store, new HashSet<string>(), Settings,
            TimeProvider.System, pairId: pair.PairId)
            { WalkCheckpointForTest = () => (journalLostB ? null : checkpoint, false), JournalReadForTest = NoJournalChanges };
        fx.Provider.AddInventory(rootA, inventoryA);
        fx.Provider.AddInventory(rootB, inventoryB);
        fx.Provider.AddWriter(rootA, await LocalWriter.OpenAsync(rootA, Side.A, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery-a"), default));
        fx.Provider.AddWriter(rootB, await LocalWriter.OpenAsync(rootB, Side.B, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery-b"), default));
        var baseline = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(baseline.Failed == 0 && store.Cursor(rootA.RootId) is { Length: > 0 } &&
            store.Cursor(rootB.RootId) is { Length: > 0 },
            "the fixture commits both local cursors before the outage");
        // Durable whole-root debt from an earlier outage on side A, still sleeping on its backoff.
        store.SaveSyncRetry(pair.PairId,
            new SyncRetry(string.Empty, "scan", 1, "synthetic whole-root read debt", DateTimeOffset.UtcNow.AddMinutes(5)));
        store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.Info, string.Empty,
            "synthetic whole-root retry notice", "sync-retry")]);
        // Side B newly loses its root on a delta read: the run stays a delta side, not a failed full side.
        journalLostB = true;
        var recorder = fx.Recorder!;
        var baselinesBefore = recorder.AllBaselineReads;
        var prefixesBefore = recorder.BaselinePrefixReads.Count;
        var outage = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(outage.Outcome == RunOutcomes.Partial && outage.Deferred >= 1,
            "the double whole-root debt keeps the run Partial");
        Require(recorder.AllBaselineReads == baselinesBefore,
            "the sleeping whole-root retry does not trigger a full pair load while the other root is unreadable");
        Require(!recorder.BaselinePrefixReads.Skip(prefixesBefore)
                .Any(read => read.Prefixes.Contains(string.Empty)),
            "planning never expands to the whole pair while the other root is unreadable");
        Require(store.SyncRetryAt(pair.PairId, string.Empty) is { Kind: "scan" },
            "the earlier whole-root debt survives the other side's outage");
        Require(store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path.Length == 0),
            "the whole-root retry notice stays open through the other side's outage");
        // Both roots readable again: the settling run plans the whole pair from stored rows, once, and
        // retires the debt. It must not be skipped as an empty run.
        journalLostB = false;
        var prefixesAtSettle = recorder.BaselinePrefixReads.Count;
        var settling = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(settling.Failed == 0 && store.SyncRetryAt(pair.PairId, string.Empty) is null &&
            !store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path.Length == 0),
            "the settling run retires the whole-root debt and closes its notice");
        Require(recorder.BaselinePrefixReads.Skip(prefixesAtSettle)
                .Any(read => read.Prefixes.Contains(string.Empty)),
            "only the settling run loads the whole pair from stored rows");
    }

    // Finding 2 (review-rc3-2): a DUE whole-root retry whose targeted re-read fails observes
    // nothing, so it must not add "" to the touched set and plan the whole pair without settling the
    // debt. The failed scope stays durable read debt with its notice; only a later successful due
    // re-read loads the pair and retires it.
    private static async Task FailedDueRootRetryDoesNotLoadPairAsync(string folder)
    {
        var fx = Build(record: true, localLocal: true);
        fx.A.Seed("seed.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        // A DUE root retry for side B only, so side A does not act on it.
        fx.Store.SaveSyncRetry(fx.Pair.PairId,
            new SyncRetry(string.Empty, "scan", 1, "synthetic failed root re-read", DateTimeOffset.MinValue)
                { ReadRoots = [fx.Pair.B.RootId] });
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.Info, string.Empty,
            "synthetic whole-root retry notice", "sync-retry")]);
        // The targeted whole-root re-read reports the root incomplete, without a failed full scan.
        fx.Provider.AddInventory(fx.Pair.B, new ScriptedInventory(fx.B) { Incomplete = true });
        var recorder = fx.Recorder!;
        var baselinesBefore = recorder.AllBaselineReads;
        var prefixesBefore = recorder.BaselinePrefixReads.Count;
        var failed = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(failed.Outcome == RunOutcomes.Partial && failed.Deferred >= 1,
            "a failed due whole-root re-read keeps the run Partial");
        Require(recorder.AllBaselineReads == baselinesBefore,
            "a failed due whole-root re-read does not trigger a full pair load");
        Require(!recorder.BaselinePrefixReads.Skip(prefixesBefore)
                .Any(read => read.Prefixes.Contains(string.Empty)),
            "a failed due whole-root re-read never expands planning to the whole pair");
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, string.Empty) is { Kind: "scan" } &&
            fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path.Length == 0),
            "the failed root re-read keeps its durable retry and notice");
        // A later successful due re-read is the settling run: it loads the pair once and retires the debt.
        fx.Provider.AddInventory(fx.Pair.B, new ScriptedInventory(fx.B));
        fx.Store.SaveSyncRetry(fx.Pair.PairId,
            fx.Store.SyncRetryAt(fx.Pair.PairId, string.Empty)! with { RetryAfter = DateTimeOffset.MinValue });
        var prefixesAtSettle = recorder.BaselinePrefixReads.Count;
        var settled = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(settled.Failed == 0 && fx.Store.SyncRetryAt(fx.Pair.PairId, string.Empty) is null &&
            !fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "sync-retry" && n.Path.Length == 0),
            "a successful due whole-root re-read retires the debt and closes its notice");
        Require(recorder.BaselinePrefixReads.Skip(prefixesAtSettle)
                .Any(read => read.Prefixes.Contains(string.Empty)),
            "only the settling re-read loads the whole pair");
    }

    // Follow-up 4: the catch-up merge must never list the same deletion evidence as both pending and
    // released. Released wins only where the replay genuinely released it; otherwise pending stands.
    private static void CatchUpEvidenceMergeTest()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["released.txt"] = "snapshot pending",
            ["held.txt"] = "snapshot pending"
        };
        var replay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["other.txt"] = "replay pending",
            ["held.txt"] = "replay pending"
        };
        var released = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "released.txt" };
        var merged = SyncRun.MergeCatchUpDeletionEvidence(snapshot, replay, released);
        Require(!merged.ContainsKey("released.txt") && merged.ContainsKey("held.txt") && merged.ContainsKey("other.txt"),
            "a genuinely released path is not also listed as pending, while unresolved evidence stays pending");
        Require(merged["held.txt"] == "replay pending", "the catch-up replay's pending reason wins per key");
        var notReleased = SyncRun.MergeCatchUpDeletionEvidence(snapshot, replay,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Require(notReleased.ContainsKey("released.txt"),
            "a pending path survives when the replay did not release it");
    }

    private static bool OverLimitJournal(string rootPath, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
        out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind,
        CancellationToken ct)
    {
        changes = [];
        failure = "synthetic overflow";
        kind = NtfsUsnReadFailure.OverLimit;
        return false;
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
        // Follow-up 3 / finding 4: the notice explains the cause in plain language with a fix hint and
        // there is exactly one notice for the path (deduplicated). The raw exception detail stays in
        // the durable retry's LastError, never in the user-facing notice.
        var causeNotices = store.OpenNotices(pair.PairId)
            .Where(n => n.ReasonKey == "sync-retry" && n.Path == "sub").ToArray();
        Require(causeNotices.Length == 1 &&
            causeNotices[0].Detail.Contains("could not open this folder", StringComparison.OrdinalIgnoreCase) &&
            causeNotices[0].Detail.Contains("make sure it still exists", StringComparison.OrdinalIgnoreCase) &&
            !causeNotices[0].Detail.Contains("ArgumentException", StringComparison.Ordinal),
            "the targeted root-open notice is a plain-language cause plus fix hint, deduplicated");
        Require(store.SyncRetryAt(pair.PairId, "sub") is { LastError: var cause } &&
            cause.Contains("ArgumentException", StringComparison.Ordinal) &&
            cause.Contains("synthetic root open failure", StringComparison.Ordinal),
            "the durable retry keeps the real exception cause for the next run");
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
