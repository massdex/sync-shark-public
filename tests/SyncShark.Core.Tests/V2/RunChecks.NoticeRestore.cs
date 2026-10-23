using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static async Task NoticeRestoreChecksAsync()
    {
        foreach (var mode in new[] { "swap", "copy", "changed", "missing", "pending", "after-effect", "legacy-after-effect", "name-refused", "round-trip", "pruned" })
        {
            var root = LocalChecks.FreshRoot("notice-restore-" + mode);
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = true;
            fx.B.DropboxHashes = true;
            var at = DateTimeOffset.UtcNow.AddMinutes(-10);
            var loser = fx.A.Seed("file.txt", "retained bytes", mtime: at);
            var winnerA = fx.A.Seed("file.txt", "winner bytes", mtime: at.AddMinutes(1));
            var winnerB = fx.B.Seed("file.txt", "winner bytes", mtime: at.AddMinutes(1));
            var archive = Path.Combine(root, "retained.recovery");
            await File.WriteAllBytesAsync(archive, Encoding.UTF8.GetBytes("retained bytes"));
            var original = new SyncAction("original", 4, ActionKind.Update, Side.A, "file.txt", null,
                winnerB, loser, true, "original-key");
            var notice = new Notice(fx.Pair.PairId, 4, NoticeKind.AutoConflict, "file.txt", "newest wins", "auto-conflict",
                RetainedVersion: new RetainedNoticeVersion(original.ActionId, Side.A, loser, winnerA));
            store.AddNotices([notice]);
            var noticeId = store.OpenNotices(fx.Pair.PairId).Single().NoticeId!;
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "run");
            store.CommitResult(fx.Pair.PairId, gen, original,
                new ActionResult(original.ActionId, Outcome.Done, winnerA, winnerB,
                    new RecoveryRef(Side.A, mode == "pruned" ? archive + ".unavailable" : archive, null, loser.Hash!, loser.Size), null));
            store.MarkFirstSyncDone(fx.Pair.PairId);
            fx.Provider.AddWriter(fx.Pair.A, new NoticeArchiveWriter(fx.WriterA, root));
            var run = fx.Run();
            if (mode == "copy")
                fx.A.Seed("Retained-" + ShortId.Hex16(fx.Pair.PairId, noticeId) + ".txt", "existing copy name");
            if (mode == "name-refused") fx.WriterA.PutError = new ProviderNameRefusedException(new IOException("name refused"));
            if (mode == "changed") fx.A.Seed("file.txt", "new user edit", mtime: at.AddMinutes(2));
            if (mode is "missing" or "pruned")
            {
                // Missing/corrupt fixtures do not delete any test or user data.
                if (mode == "missing") await File.WriteAllTextAsync(archive, "bad");
                var unavailable = await run.RestoreNoticeVersionAsync(fx.Pair.PairId, noticeId, false, CancellationToken.None);
                Require(unavailable.Outcome == Outcome.Skipped && unavailable.Reason!.Contains("left unchanged") &&
                    fx.WriterA.Calls == 0 && store.OpenNotices(fx.Pair.PairId).Count == 1,
                    "unverified retained bytes cannot change winner or close notice");
                continue;
            }
            if (mode is "pending" or "after-effect" or "legacy-after-effect")
            {
                var pending = new SyncAction(SyncRun.NoticeRestoreActionId(fx.Pair.PairId, noticeId), 4,
                    ActionKind.Update, Side.A, "file.txt", null, loser with { VersionId = null }, winnerA, true, null)
                { SuppressReconciledBaseline = true, ResolvedNoticeId = noticeId };
                store.RecordIntent(fx.Pair.PairId, pending);
                if (mode is "after-effect" or "legacy-after-effect")
                {
                    var receipt = await fx.Provider.Writer(fx.Pair.A).PreserveAsync(winnerA, pending.ActionId, CancellationToken.None);
                    if (mode == "after-effect")
                    {
                        // Use the production archive callback and committer: the receipt must be durable
                        // before publication, even when the subsequent result commit is interrupted.
                        using var gate = new SemaphoreSlim(1, 1);
                        var committer = new StoreCommitter(store, gate, fx.Pair.PairId, gen);
                        var context = new ProviderEffectContext(pending, committer, _ => Task.CompletedTask);
                        await context.RecordRecoveryAsync(pending.ActionId, winnerA.Path, receipt,
                            fx.Pair.PairId, CancellationToken.None);
                        Require(store.RecoveryForAction(fx.Pair.PairId, pending.ActionId)?.Location == receipt.Location,
                            "interrupted restore indexes the displaced winner before publication");
                    }
                    else
                    {
                        // A pre-receipt build could leave archived bytes but no indexed ownership.
                        Require(File.Exists(receipt.Location) &&
                            store.RecoveryForAction(fx.Pair.PairId, pending.ActionId) is null,
                            "legacy restore retains archive bytes without an indexed receipt");
                    }
                    using var restoredBytes = new MemoryStream(Encoding.UTF8.GetBytes("retained bytes"));
                    await fx.A.PutAsync(pending, restoredBytes, CancellationToken.None);
                }
                var interrupted = await run.RestoreNoticeVersionAsync(fx.Pair.PairId, noticeId, false, CancellationToken.None);
                if (mode == "legacy-after-effect")
                {
                    Require(interrupted.Outcome == Outcome.Uncertain && fx.WriterA.Puts == 0 &&
                        store.IntentById(fx.Pair.PairId, pending.ActionId) is not null &&
                        store.OpenNotices(fx.Pair.PairId).Any(item => item.NoticeId == noticeId),
                        "legacy restore without receipt stays protective and never replays writes");
                    continue;
                }
                Require(interrupted.Outcome == (mode == "pending" ? Outcome.Skipped : Outcome.Done) && fx.WriterA.Puts == 0,
                    "pending restore observes actual outcome, never blindly replays it");
            }
            var result = await run.RestoreNoticeVersionAsync(fx.Pair.PairId, noticeId, mode == "copy", CancellationToken.None);
            if (mode == "name-refused")
            {
                Require(result.Outcome == Outcome.Failed && !result.ProviderNameRefused &&
                    store.OpenNotices(fx.Pair.PairId).Any(item => item.NoticeId == noticeId),
                    "restore name refusal is failure, not successful handling that ignores the opposite live file");
                Require((await MapAsync(fx.A))["file.txt"] == winnerA.Hash,
                    "name refusal leaves current winner unchanged");
                continue;
            }
            if (mode == "changed")
            {
                Require(result.Outcome == Outcome.Skipped && fx.WriterA.Calls == 0 &&
                    store.OpenNotices(fx.Pair.PairId).Count == 1, "changed winner offers copy instead of overwriting user edit");
                continue;
            }
            Require(result.Outcome == Outcome.Done && !store.OpenNotices(fx.Pair.PairId).Any(item => item.NoticeId == noticeId),
                "proven restore atomically closes only its notice");
            var map = await MapAsync(fx.A);
            if (mode == "copy")
            {
                Require(map["file.txt"] == winnerA.Hash && map.Count == 3 && map.Values.Contains(loser.Hash!) &&
                    map["Retained-" + ShortId.Hex16(fx.Pair.PairId, noticeId) + ".txt"] ==
                        DropboxContentHash.Compute(Encoding.UTF8.GetBytes("existing copy name")),
                    "save-copy keeps winner and publishes retained content under a separate name");
                Require(fx.WriterA.Preserves == 0, "copy does not overwrite or archive winner");
            }
            else
            {
                Require(map["file.txt"] == loser.Hash && fx.WriterA.Preserves >= 1,
                    "swap restores loser only after preserving current winner");
                var successor = store.OpenNotices(fx.Pair.PairId).Single(item => item.ReasonKey == "retained-version-restored");
                Require(successor.RetainedVersion?.ActionId == SyncRun.NoticeRestoreActionId(fx.Pair.PairId, noticeId),
                    "displaced winner remains reachable through successor notice, including interrupted result commit");
                if (mode == "round-trip")
                {
                    var reversed = await run.RestoreNoticeVersionAsync(fx.Pair.PairId, successor.NoticeId!, false,
                        CancellationToken.None);
                    Require(reversed.Outcome == Outcome.Done && (await MapAsync(fx.A))["file.txt"] == winnerA.Hash,
                        "successor can restore previous winner before any ordinary sync");
                    var settled = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                    Require(settled.Uploaded == 0 && settled.Downloaded == 0 && settled.Deleted == 0 &&
                        (await MapAsync(fx.A))["file.txt"] == winnerA.Hash && (await MapAsync(fx.B))["file.txt"] == winnerB.Hash,
                        "round trip leaves ordinary sync with no content effects");
                    continue;
                }
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                Require((await MapAsync(fx.A))["file.txt"] == loser.Hash &&
                    (await MapAsync(fx.B))["file.txt"] == loser.Hash,
                    "next ordinary sync propagates explicit choice instead of reverting it");
            }
            var puts = fx.WriterA.Puts;
            var retry = await run.RestoreNoticeVersionAsync(fx.Pair.PairId, noticeId, mode == "copy", CancellationToken.None);
            Require(retry.Outcome == Outcome.Done && fx.WriterA.Puts == puts,
                "retry of closed notice performs no duplicate restore/copy");
        }
        await NoticeRestoreLinkedExclusionsAsync();
        Console.WriteLine("PASS NoticeRestoreChecks");
    }

    public static async Task NoticeRestoreLinkedExclusionsAsync()
    {
        foreach (var asCopy in new[] { false, true })
        foreach (var variant in new[] { "exclude", "ignore", "target" })
        foreach (var mappedLink in new[] { false, true })
        {
            var root = LocalChecks.FreshRoot("restore-linked-exclusion");
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var at = DateTimeOffset.UtcNow.AddMinutes(-10);
            var loser = fx.A.Seed("X_", "retained bytes", mtime: at);
            var winner = fx.A.Seed("X_", "winner bytes", mtime: at.AddMinutes(1));
            var literal = fx.B.Seed("X_", "winner bytes", mtime: at.AddMinutes(1));
            // Ignore rules cannot name a colon spelling (SyncIgnorePolicy rejects it), so the ignore variant
            // gives the other cloud entity a distinct valid name; only identity decides whether it is linked.
            var ignore = variant == "ignore";
            var otherName = ignore ? "Y.dat" : "X:";
            var mapped = fx.B.Seed(otherName, "winner bytes", mtime: at.AddMinutes(1));
            var counterpart = mappedLink ? mapped : literal;
            var archive = Path.Combine(root, "retained.recovery");
            await File.WriteAllBytesAsync(archive, Encoding.UTF8.GetBytes("retained bytes"));
            var original = new SyncAction("restore-linked-original", 4, ActionKind.Update, Side.A,
                "X_", null, counterpart, loser, true, "restore-linked-key");
            store.AddNotices([new Notice(fx.Pair.PairId, 4, NoticeKind.AutoConflict, "X_", "newest wins", "auto-conflict",
                RetainedVersion: new RetainedNoticeVersion(original.ActionId, Side.A, loser, winner))]);
            var noticeId = store.OpenNotices(fx.Pair.PairId).Single().NoticeId!;
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "run");
            store.CommitResult(fx.Pair.PairId, gen, original, new(original.ActionId, Outcome.Done, winner, counterpart,
                new RecoveryRef(Side.A, archive, null, loser.Hash!, loser.Size), null));
            store.MarkFirstSyncDone(fx.Pair.PairId);
            // "target" is the reachable reverse case: pair exclusions are both-sided, so excluding X_ excludes
            // the actual local target itself and blocks regardless of which cloud entity is linked.
            store.SavePair(ignore ? fx.Pair with { IgnoreRules = ["FileName:" + otherName] } :
                fx.Pair with { Excluded = [variant == "target" ? "X_" : otherName] });
            var writer = new NoticeArchiveWriter(fx.WriterA, root);
            fx.Provider.AddWriter(fx.Pair.A, writer);
            var result = await fx.Run().RestoreNoticeVersionAsync(fx.Pair.PairId, noticeId, asCopy, default);
            if (mappedLink || variant == "target")
            {
                Require(result.Outcome == Outcome.Skipped && result.Reason!.Contains("Include it before restoring"),
                    $"{variant}/{mappedLink}/{asCopy}: an excluded actual target or excluded linked counterpart blocks restore, including save-copy");
                Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 && writer.RecoveryReads == 0 &&
                    store.IntentById(fx.Pair.PairId, SyncRun.NoticeRestoreActionId(fx.Pair.PairId, noticeId)) is null,
                    "blocked counterpart causes no archive read, preserve, write or durable intent");
                Require(store.OpenNotices(fx.Pair.PairId).Single().NoticeId == noticeId &&
                    (await MapAsync(fx.A)).Single().Value == winner.Hash &&
                    (await MapAsync(fx.B)).Count == 2,
                    "blocked restore leaves notice open, winner intact and no labelled copy");
            }
            else
            {
                Require(result.Outcome == Outcome.Done && writer.RecoveryReads == 1,
                    "an excluded unlinked X: never blocks restoring the literal-linked X_ entity");
                Require(asCopy ? (await MapAsync(fx.A)).Count == 2 : (await MapAsync(fx.A))["X_"] == loser.Hash,
                    "allowed control actually restores bytes, not merely a skipped no-op");
            }
        }
        Console.WriteLine("PASS NoticeRestoreLinkedExclusions");
    }

    private sealed class NoticeArchiveWriter(IEndpointWriter inner, string directory) : IEndpointWriter, ILocalRetainedNoticeReader, ISourceAbsenceProbe, ILocalFreeSpaceAdmission
    {
        // Forward the up-front free-space admission so a test can exercise the executor's refusal branch on this
        // wrapper; the default is the inner writer's verdict, which is Granted unless a test configures one.
        public Task<FreeSpaceAdmission> AdmitFreeSpaceAsync(SyncAction action, CancellationToken ct) =>
            inner is ILocalFreeSpaceAdmission admission ? admission.AdmitFreeSpaceAsync(action, ct)
                : Task.FromResult(FreeSpaceAdmission.Granted);
        public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct) =>
            ((ISourceAbsenceProbe)inner).IsSourceAbsentAsync(historical, path, ct);
        public Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct) =>
            ((ISourceAbsenceProbe)inner).IsHistoricalSourcePresentAsync(historical, ct);
        public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) =>
            ((ISourceAbsenceProbe)inner).ObserveSourceMetadataAsync(path, ct);
        public int RecoveryReads { get; private set; }
        public Task<Stream> OpenRecoveryAsync(RecoveryRecord recovery, ItemState retained, CancellationToken ct)
        {
            RecoveryReads++;
            return Task.FromResult<Stream>(File.OpenRead(recovery.Location));
        }
        public async Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct)
        {
            var reference = await inner.PreserveAsync(victim, actionId, ct);
            // Child action ids carry ':' (folder action id + ordinal), which is not a valid Windows file name character.
            var path = Path.Combine(directory, actionId.Replace(':', '_') + ".recovery");
            if (!File.Exists(path))
            {
                await using var input = await inner.OpenReadAsync(victim, ct);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, ct);
            }
            return reference with { Location = path, VersionId = null };
        }
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) => inner.PutAsync(action, content, ct);
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => inner.DeleteAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct) => inner.OpenReadAsync(state, ct);
    }
}
