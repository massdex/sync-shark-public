using System.Runtime.InteropServices;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task RetiredNativeIdentityAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        LocalChecks.Seed(directory, "item.txt", "original");
        var root = LocalChecks.RootOf(directory, "root-local");
        var pair = new PairDef("native-pair", "Pair A", root,
            new("remote", SyncProviders.Dropbox, "account", "/Remote", "id:remote"), [], [], true);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.SavePair(pair);
        var inventory = LocalChecks.Inventory(directory, store, TimeProvider.System);
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 200, 0);
        var snapshot = await inventory.FullAsync(store.BeginGeneration(root.RootId, "full"), _ => true, null!, default);
        store.WriteSnapshot(snapshot with { CursorAfter = checkpoint.Cursor });
        var original = store.Live(root.RootId, ["item.txt"]).Single();
        // Allocate the new copy while the original exists so NTFS cannot reuse its identity.
        LocalChecks.Seed(directory, "replacement.txt", "replacement");
        File.Delete(Path.Combine(directory, original.Path)); // This suite's own disposable fixture only.
        File.Move(Path.Combine(directory, "replacement.txt"), Path.Combine(directory, original.Path));
        using (var volume = NtfsUsnJournal.OpenGapVolume(directory))
        using (var retired = NtfsUsnJournal.OpenGapMetadata(volume, LocalItems.ParseObjectId(original.ObjectId).FileId))
            LocalChecks.Require(retired.IsInvalid && Marshal.GetLastWin32Error() == 87,
                "native retired NTFS reference reproduces OpenFileById error 87");
        var io = new LocalGapIo(store, StorageChecks.Fast, new HashSet<string>(), () => false)
        { CheckpointForCheck = _ => checkpoint };
        using (var probes = io.OpenProbes(root))
            LocalChecks.Require(probes.Probe(original) is null && probes.LastFailureReason == "open-failed(87)",
                "production metadata adapter reports the exact retired-ID cause");
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, checkpoint.Cursor, 100, true);
        await using var worker = new LocalGapWorker(gaps, io, StorageFailureClassifier.IsStateDamage);
        worker.Discover(root, gaps.GapEpoch(root.RootId)!, default);
        LocalChecks.Require((await io.HashAsync(root, original, default)).Outcome == LocalGapHashOutcome.IdentityChanged,
            "production content adapter classifies cached-ID 87 without a proof");
        LocalChecks.Require(store.OpenNotices(pair.PairId).Count == 0 && gaps.HasGapProtection(root.RootId),
            "native metadata and content paths retain protection without notice noise");
        var source = await LocalWriter.OpenAsync(root, Side.A, pair.PairId, LocalChecks.InstallationId,
            Path.Combine(folder, "recovery"), default, isStateDamage: StorageFailureClassifier.IsStateDamage);
        var absence = await source.IsAbsentBatchAsync([(original, original.Path)], default);
        LocalChecks.Require(absence.Single().Absent == false,
            "native retired-ID 87 plus a replacement at the path cannot authorize deletion");
        var delta = await inventory.ChangesForPathsAsync([original.Path], checkpoint.Cursor,
            store.BeginGeneration(root.RootId, "retry"), _ => true, default);
        LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Count == 0 &&
            delta.Changes.All(change => change.Kind != ChangeKind.Delete), "native replacement produces no deletion");
        store.WriteDelta(delta);
        LocalChecks.Require(store.Live(root.RootId, [original.Path]).Single().ObjectId != original.ObjectId,
            "guarded native inventory replaces the retired identity");
    }
}
