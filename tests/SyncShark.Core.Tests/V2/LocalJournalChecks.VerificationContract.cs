using System.Buffers.Binary;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task VerificationContractAsync(string folder)
    {
        VerificationTruthTable();
        var elapsed = TimeSpan.Zero;
        var budget = new VerificationBudget(() => elapsed);
        LocalChecks.Require(budget.Admit(VerificationBudget.MaximumBytes + 1) && !budget.CanStart,
            "one oversized file can progress independently, then pass yields");
        budget = new VerificationBudget(() => elapsed);
        LocalChecks.Require(budget.Admit(1), "first file admitted");
        elapsed = VerificationBudget.MaximumTime;
        LocalChecks.Require(!budget.Admit(1), "elapsed budget prevents another file, never abandons a reader");
        elapsed = TimeSpan.Zero;
        budget = new VerificationBudget(() => elapsed);
        LocalChecks.Require(budget.Admit(VerificationBudget.MaximumBytes - 1) && !budget.Admit(2),
            "remaining byte admission cannot overshoot except first file");
        budget = new VerificationBudget(() => TimeSpan.Zero);
        for (var i = 0; i < 512; i++) LocalChecks.Require(budget.Admit(0), "512 item admission boundary");
        LocalChecks.Require(!budget.CanStart && !budget.Admit(0), "513th item is refused");
        budget = new VerificationBudget(() => TimeSpan.Zero);
        LocalChecks.Require(budget.Admit(long.MaxValue) && !budget.Admit(long.MaxValue) && !budget.CanStart,
            "maximum-sized first item saturates without overflow or another admission");
        LocalChecks.Require(NtfsUsnJournal.CanProveContents(FileAttributes.ReparsePoint, 0x9000001a) &&
            !NtfsUsnJournal.CanProveContents(FileAttributes.ReparsePoint | FileAttributes.Offline, 0x9000001a) &&
            !NtfsUsnJournal.CanProveContents(FileAttributes.ReparsePoint | (FileAttributes)0x400000, 0x9000001a) &&
            !NtfsUsnJournal.CanProveContents(FileAttributes.ReparsePoint | (FileAttributes)0x40000, 0x9000001a) &&
            !NtfsUsnJournal.CanProveContents(FileAttributes.ReparsePoint, 0xa000000c),
            "only fully hydrated cloud contents are provable; unknown/offline/link contents stay protected");
        foreach (var kind in new[] { "truncated", "unsupported", "overflow", "name" })
        {
            var bytes = new byte[kind == "truncated" ? 4 : 60];
            if (bytes.Length == 60)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, kind == "overflow" ? uint.MaxValue : 60);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), kind == "unsupported" ? (ushort)3 : (ushort)2);
            }
            try { NtfsUsnJournal.ParseChanges(bytes, 0, 100); throw new InvalidOperationException("poison record accepted"); }
            catch (IOException error)
            {
                LocalChecks.Require(NtfsUsnJournal.ClassifyReadFailure(error) == NtfsUsnReadFailure.Permanent,
                    "unsupported/malformed journal history is permanent, not an endless transient retry");
            }
        }
        LocalChecks.Require(NtfsUsnJournal.ClassifyReadFailure(new IOException("temporarily unavailable")) == NtfsUsnReadFailure.Transient,
            "ordinary IO outage remains transient");

        using (var cloudStore = SqliteStateStore.Open(Path.Combine(folder, "cloud-store"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var stamp = DateTimeOffset.UtcNow;
            var cloudRoot = new RootDef("cloud-local", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
            var cloudItem = new ItemState("hydrated.txt", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 11), null,
                new string('a', 64), 1, stamp, ItemFlags.None);
            cloudStore.WriteSnapshot(new(cloudRoot.RootId, Side.A, cloudStore.BeginGeneration(cloudRoot.RootId, "full"),
                "0000000000000001:200", [cloudItem], []));
            var gaps = (ILocalGapStore)cloudStore;
            gaps.BeginGap(cloudRoot.RootId, "0000000000000001:200", 100, true);
            var io = new GapCheckIo(stamp, "0000000000000001:200") { RequiresHash = true, UsnOverride = 99 };
            await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
            worker.Discover(cloudRoot, gaps.GapEpoch(cloudRoot.RootId)!, default);
            gaps.PromoteGapDiscovery(cloudRoot.RootId);
            LocalChecks.Require(gaps.GapHashPage(cloudRoot.RootId, gaps.GapEpoch(cloudRoot.RootId)!, 10).Count == 1 &&
                gaps.HasGapProtection(cloudRoot.RootId), "hydrated cloud metadata before boundary still requires content proof");
            await worker.HashPage(cloudRoot, gaps.GapEpoch(cloudRoot.RootId)!, default);
            io.UsnOverride = 100;
            var horizon = gaps.GapResultWatermark(cloudRoot.RootId);
            LocalChecks.Require(gaps.GapResults(cloudRoot.RootId, horizon, 10).Single().Proof.File.RequiresHash,
                "hydrated proof requirement survives durable hash staging");
            LocalGapWorker.Promote(gaps, cloudStore, io, cloudRoot, horizon, new(1, 0, 200, 0), _ => true, _ => { }, default, StorageFailureClassifier.IsStateDamage);
            LocalChecks.Require(cloudStore.Live(cloudRoot.RootId).Single().Hash == new string('b', 64) &&
                !gaps.HasGapProtection(cloudRoot.RootId), "validated hash proof can discharge hydrated cloud debt");
        }

        foreach (IStateStore admission in new IStateStore[] { new InMemoryStateStore() })
        {
            var observed = new ItemState("review/new.txt", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 25), null, null, 3,
                DateTimeOffset.UnixEpoch, ItemFlags.None);
            var cursorBefore = admission.Cursor("review-root");
            LocalChecks.Require(admission.DeferIndexObservation("review-root", Side.A, observed, null, "review budget"),
                "previously unknown review entity admitted protected");
            LocalChecks.Require(admission.ByObject("review-root", observed.ObjectId) is { Hash: null } held && held.Flags.HasFlag(ItemFlags.NeedsRehash) &&
                admission.Cursor("review-root") == cursorBefore, "observation admission neither proves contents nor advances root cursor");
            LocalChecks.Require(!admission.DeferIndexObservation("review-root", Side.A, observed, null, "stale admission"),
                "captured-prior CAS rejects a concurrent observation");
        }

        var root = Path.Combine(folder, "root");
        LocalChecks.Seed(root, "file.txt", "content");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.WalkCheckpointForTest = () => (new(1, 0, 100, 0), false);
        var def = LocalChecks.RootOf(root, "root-local");
        // Publication needs a pair consumer; the durable planning queue is per pair, not global.
        // Save it before the snapshot so its first insert cannot clear the root's committed cursor.
        var pair = new PairDef("verify-contract", "verify", def,
            new RootDef("verify-contract-remote", SyncProviders.Dropbox, null, "/", "unused"), [], [], true);
        store.SavePair(pair);
        await SaveFull(inventory, store);
        var reviewObserved = new ItemState("new-review.txt", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 999), null, null, 7,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        store.BeginGeneration("review-root", "fixture");
        var beforeReviewCursor = store.Cursor("review-root");
        LocalChecks.Require(store.DeferIndexObservation("review-root", Side.A, reviewObserved, null, "review budget") &&
            store.Cursor("review-root") == beforeReviewCursor && store.ByObject("review-root", reviewObserved.ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash),
            "SQLite observed-entity admission is protective and cursor-neutral");
        LocalChecks.Require(!store.DeferIndexObservation("review-root", Side.A, reviewObserved, null, "stale review"),
            "SQLite stale-prior admission does not overwrite retained observation");
        var item = store.Live("root-local", ["file.txt"]).Single();
        inventory.GapIo = new VerificationTestIo((_, _, _) => throw new InvalidOperationException("sync must never hash contents"));
        var identity = LocalItems.ParseObjectId(def.RootObjectId);
        using (var lease = PilotDirectoryLease.Open(root, new(identity.Volume, identity.FileId)))
        {
            var delta = new Delta(def.RootId, Side.A, store.BeginGeneration(def.RootId, "delta"), "", "",
                [new(ChangeKind.Upsert, item.ObjectId, item.Path, item, null)], false, []);
            var pending = await inventory.RehashAsync(lease, delta, [item.ObjectId], default);
            LocalChecks.Require(pending.Changes.Single().State is { Hash: null } held && held.Flags.HasFlag(ItemFlags.NeedsRehash),
                "current delta cannot publish stale content before independent indexing");
            store.WriteDelta(pending);
        }
        await inventory.VerifyPendingAsync(_ => true, _ => throw new InvalidOperationException("unverified touch"), default);
        LocalChecks.Require(store.HasPendingRehashVerification(def.RootId), "durable unverified wake remains after sync");
        foreach (IStateStore contractStore in new IStateStore[] { store, new InMemoryStateStore() })
        {
            if (!ReferenceEquals(contractStore, store))
                contractStore.WriteSnapshot(new(def.RootId, Side.A, contractStore.BeginGeneration(def.RootId, "full"), "", [item], []));
            contractStore.RequireRehash(def.RootId, item, "race start");
            var raceInventory = new LocalInventory(def, Side.A, contractStore, new HashSet<string>(), V2Settings.Default, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage);
            raceInventory.GapIo = new VerificationTestIo((_, selected, _) =>
            {
                contractStore.RequireRehash(def.RootId, selected, "newer write DURING reader");
                var id = LocalItems.ParseObjectId(selected.ObjectId);
                return Task.FromResult(new LocalGapHashResult(LocalGapHashOutcome.Verified, new string('a', 64),
                    new("0000000000000001:200", new(id.Volume, id.FileId, 150, selected.Size, selected.Mtime.UtcDateTime.ToFileTimeUtc()))));
            });
            await raceInventory.VerifyIndexingPassAsync(default);
            LocalChecks.Require(contractStore.ByObject(def.RootId, item.ObjectId) is { Hash: null } retained &&
                retained.Flags.HasFlag(ItemFlags.NeedsRehash), "reader interleaving cannot discharge newer debt on either store contract");

            raceInventory.GapIo = new VerificationTestIo((_, selected, _) =>
            {
                var capturedEvidence = contractStore.RehashRevision(def.RootId, selected.ObjectId);
                var oldOrder = contractStore.RehashWatermark(def.RootId);
                contractStore.RehashCandidates(def.RootId, 1, oldOrder, true);
                LocalChecks.Require(contractStore.RehashWatermark(def.RootId) > oldOrder &&
                    contractStore.RehashRevision(def.RootId, selected.ObjectId) == capturedEvidence,
                    "concurrent selection rotates only scheduling, never content evidence");
                var id = LocalItems.ParseObjectId(selected.ObjectId);
                return Task.FromResult(new LocalGapHashResult(LocalGapHashOutcome.Verified, new string('b', 64),
                    new("0000000000000001:200", new(id.Volume, id.FileId, 150, selected.Size, selected.Mtime.UtcDateTime.ToFileTimeUtc()))));
            });
            LocalChecks.Require((await raceInventory.HashAsync([item.Path], default)).ContainsKey(item.Path),
                "concurrent SELECT alone cannot reject the foreground proof on either store");

            raceInventory.GapIo = new VerificationTestIo((_, selected, _) =>
            {
                // Model the independent indexer selecting, completing and handing off this entity
                // while the foreground reader is still returning its independently validated proof.
                contractStore.RehashCandidates(def.RootId, 1, contractStore.RehashWatermark(def.RootId), true);
                var currentRevision = contractStore.RehashRevision(def.RootId, selected.ObjectId);
                LocalChecks.Require(contractStore.CompleteIndexingRehash(def.RootId, selected, new string('b', 64), currentRevision),
                    "concurrent worker publishes its own guarded proof");
                contractStore.AcknowledgeRehash(def.RootId, contractStore.RehashWatermark(def.RootId), _ => true);
                var id = LocalItems.ParseObjectId(selected.ObjectId);
                return Task.FromResult(new LocalGapHashResult(LocalGapHashOutcome.Verified, new string('b', 64),
                    new("0000000000000001:200", new(id.Volume, id.FileId, 150, selected.Size, selected.Mtime.UtcDateTime.ToFileTimeUtc()))));
            });
            var accepted = await raceInventory.HashAsync([item.Path], default);
            LocalChecks.Require(accepted.TryGetValue(item.Path, out var acceptedHash) && acceptedHash == new string('b', 64) &&
                contractStore.RehashWatermark(def.RootId) == 0,
                "same verified proof after concurrent handoff succeeds without resurrecting or mutating queue debt");
            var current = contractStore.ByObject(def.RootId, item.ObjectId)!;
            LocalChecks.Require(!contractStore.CompleteIndexingRehash(def.RootId, current, new string('c', 64), 1),
                "idempotent readback rejects differing content");
            contractStore.RequireRehash(def.RootId, current, "new write after concurrent completion");
            LocalChecks.Require(!contractStore.CompleteIndexingRehash(def.RootId, current, new string('b', 64), 1),
                "idempotent readback cannot release a newer unverified write");
        }
        var revision = store.RehashRevision(def.RootId, item.ObjectId);
        store.RequireRehash(def.RootId, item, "newer concurrent journal write");
        LocalChecks.Require(!store.CompleteIndexingRehash(def.RootId, item, new string('a', 64), revision),
            "concurrent metadata-identical journal write invalidates older proof completion");
        using (var stop = new CancellationTokenSource())
        {
            inventory.GapIo = new VerificationTestIo((_, _, _) =>
            {
                stop.Cancel();
                return Task.FromResult(new LocalGapHashResult(LocalGapHashOutcome.Unverifiable));
            });
            try { await inventory.VerifyIndexingPassAsync(stop.Token); throw new InvalidOperationException("cancelled proof published"); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            LocalChecks.Require(store.HasPendingRehashVerification(def.RootId), "cancelled indexing retains durable protected debt");
        }
        inventory.GapIo = new LocalGapIo(store, V2Settings.Default, new HashSet<string>(), () => false);
        await inventory.VerifyIndexingPassAsync(default);
        // VerifyIndexingPassAsync verifies only; publication is the index owner's step
        // (LocalIndexingRuns.PassAsync selects the ready page and publishes it). Mirror it here.
        store.PublishIndexPlanning(def.RootId, store.RehashCandidates(def.RootId, 512,
            store.RehashWatermark(def.RootId), needsVerification: false));
        var deferred = store.AdmitDeferredPlanning(pair.PairId, long.MaxValue, rotate: false).ToArray();
        var published = store.CountDeferredPlanning(pair.PairId);
        LocalChecks.Require(!store.HasPendingRehashVerification(def.RootId) &&
            store.RehashPendingCount(def.RootId) == 0 && published == 1 && deferred.Length == 1 &&
            deferred[0].Paths.Contains(item.Path) && deferred[0].AllAliases &&
            store.IndexPlanningWatermark(def.RootId) > 0,
            "the bounded indexing pass verifies and publishes to the durable planning queue");
        await inventory.VerifyPendingAsync(_ => true,
            _ => throw new InvalidOperationException("foreground sync must not touch aliases"), default);
        LocalChecks.Require(store.CountDeferredPlanning(pair.PairId) == published &&
            store.RehashPendingCount(def.RootId) == 0,
            "foreground sync publication is a no-op after the indexing pass");
        var failed = inventory.JournalReadFailed(1, def.RootId, "0000000000000001:100", new(1, 0, 200, 0), 100,
            "unsupported record fixture", NtfsUsnReadFailure.Permanent);
        LocalChecks.Require(failed.CursorInvalid && ((ILocalGapStore)store).GapEpoch(def.RootId) is { TrustFileUsn: false } &&
            store.Cursor(def.RootId) == "0000000000000001:200" && failed.IncompleteReasons[""] .Contains("unsupported record fixture"),
            "permanent history failure protects root, keeps cause and advances to protected full fallback");
    }
    private static void VerificationTruthTable()
    {
        LocalChecks.Require((int)VerificationPolicy.BlockedFlags == 153 &&
            (int)VerificationPolicy.WriteEvidenceBlocked == 1055 &&
            (int)VerificationPolicy.SettlementBlocked == 415, "verification masks retain distinct authority");
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + VerificationSql.NeededSql("f") + ", " + VerificationSql.EligibleSql("f") +
            " FROM (SELECT $kind AS kind, $hash AS hash, $flags AS flags) f";
        var kindParameter = command.Parameters.AddWithValue("$kind", 0);
        var hashParameter = command.Parameters.AddWithValue("$hash", DBNull.Value);
        var flagsParameter = command.Parameters.AddWithValue("$flags", 0);
        foreach (var kind in Enum.GetValues<ItemKind>())
        foreach (var hash in new string?[] { null, new string('a', 64) })
        for (var mask = 0; mask < 2048; mask++)
        {
            var flags = (ItemFlags)mask;
            var item = new ItemState("file.txt", kind, "object", null, hash, 1, DateTimeOffset.UnixEpoch, flags);
            kindParameter.Value = (int)kind;
            hashParameter.Value = (object?)hash ?? DBNull.Value;
            flagsParameter.Value = mask;
            using var row = command.ExecuteReader();
            LocalChecks.Require(row.Read(), "SQL truth table returns one row");
            var needed = row.GetInt64(0) != 0;
            var eligible = row.GetInt64(1) != 0;
            LocalChecks.Require(needed == VerificationPolicy.NeedsVerification(item) &&
                VerificationPolicy.Eligible(item) == (kind == ItemKind.File && needed && eligible),
                "domain eligibility equals file AND NeededSql AND EligibleSql for every relevant flag combination");
            LocalChecks.Require(VerificationPolicy.CanAcknowledge(hash, flags) == (hash is not null && (mask & 415) == 0),
                "settlement requires non-null proof and its own mask");
            var prior = item with { Flags = ItemFlags.NeedsRehash };
            LocalChecks.Require(VerificationPolicy.NeedsWriteProof(item, prior, false) ==
                (kind == ItemKind.File && (mask & 1055) == 0), "write proof uses its independent mask");
            LocalChecks.Require(!VerificationPolicy.NeedsWriteProof(item, null, true), "missing prior never fabricates write proof");
        }
    }
}
