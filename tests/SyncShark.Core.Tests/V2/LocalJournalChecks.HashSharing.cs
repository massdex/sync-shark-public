using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    // Opt-in native sharing test. F: need not have a journal: only USN/checkpoint evidence is injected;
    // identity/size/mtime, content reads, file-ID open, directory rename and path guards are native.
    internal static async Task HashSharingAsync(string folder)
    {
        LocalChecks.Require(OperatingSystem.IsWindows() &&
            Path.GetPathRoot(Path.GetFullPath(folder))!.Equals(@"F:\", StringComparison.OrdinalIgnoreCase),
            "content hash sharing fixture runs only on isolated Windows F drive");
        var rootPath = Path.Combine(folder, "root");
        var parent = Path.Combine(rootPath, "parent");
        var moved = Path.Combine(rootPath, "renamed");
        Directory.CreateDirectory(parent);
        const int size = 16 * 1024 * 1024;
        using (var seed = new FileStream(Path.Combine(parent, "large.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            seed.SetLength(size);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var root = LocalChecks.RootOf(rootPath, "hash-sharing");
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), StorageChecks.Fast, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage);
        var snapshot = await inventory.FullAsync(store.BeginGeneration(root.RootId, "full"), _ => true, null!, default);
        LocalChecks.Require(snapshot.IncompleteSubtrees.Count == 0, "native metadata inventory is complete for sharing fixture");
        store.WriteSnapshot(snapshot);
        var item = store.Live(root.RootId, ["parent/large.bin"]).Single();
        var settings = StorageChecks.Fast with { LocalGapBytesPerSecond = 16 * 1024 * 1024 };
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 200, 0);
        var stable = new LocalGapIo(store, settings, new HashSet<string>(), () => false)
        {
            CheckpointForCheck = _ => checkpoint,
            FileUsnForCheck = NativeEvidence
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var positive = await stable.HashAsync(root, item, timeout.Token);
        var expected = new ContentBlockHasher();
        var zeros = new byte[64 * 1024];
        for (var offset = 0; offset < size; offset += zeros.Length) expected.Append(zeros);
        LocalChecks.Require(positive is { Outcome: LocalGapHashOutcome.Verified } completed && completed.Hash == expected.Finish(),
            "unchanged large file hashes successfully through production file-ID streaming and path validation");

        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, checkpoint.Cursor, 100, true);
        var epoch = gaps.GapEpoch(root.RootId)!;
        gaps.GapDiscovered(root.RootId, epoch, item, true);
        gaps.GapDiscoveryDone(root.RootId, epoch);
        await UrgentSaveDuringHashAsync(root, item, store, settings, checkpoint, timeout.Token);
        var renamed = false;
        var racing = new LocalGapIo(store, settings, new HashSet<string>(), () => false)
        {
            CheckpointForCheck = _ => checkpoint,
            FileUsnForCheck = NativeEvidence,
            AfterContentReadForCheck = bytes =>
            {
                if (renamed) return;
                LocalChecks.Require(bytes > 0 && bytes < size, "parent rename occurs after streaming starts and before the hash finishes");
                Directory.Move(parent, moved);
                renamed = true;
            }
        };
        var result = await racing.HashAsync(root, item, timeout.Token);
        LocalChecks.Require(renamed && File.Exists(Path.Combine(moved, "large.bin")),
            "production file-ID content handle permits parent rename during a large hash");
        LocalChecks.Require(result.Outcome == LocalGapHashOutcome.Unverifiable && gaps.HasGapProtection(root.RootId) &&
            gaps.GapResults(root.RootId, gaps.GapResultWatermark(root.RootId), 128).Count == 0,
            "renamed path drops its hash result and retains protection without staging or promotion");
        Console.WriteLine("PASS native F file-ID content sharing (journal evidence injected; no journal creation)");
    }

    private static async Task UrgentSaveDuringHashAsync(RootDef root, ItemState item, SqliteStateStore store,
        V2Settings settings, NtfsUsnCheckpoint checkpoint, CancellationToken ct)
    {
        var path = Path.Combine(root.RootPath, item.Path.Replace('/', Path.DirectorySeparatorChar));
        var saves = 0;
        var io = new LocalGapIo(store, settings, new HashSet<string>(), () => true)
        {
            CheckpointForCheck = _ => checkpoint,
            FileUsnForCheck = NativeEvidence,
            AfterContentReadForCheck = _ =>
            {
                if (saves >= 3) return;
                using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    writer.WriteByte((byte)++saves);
                File.SetLastWriteTimeUtc(path, item.Mtime.UtcDateTime.AddSeconds(saves));
            }
        };
        var result = await io.HashAsync(root, item, urgent: true, ct);
        LocalChecks.Require(saves == 3 && result.Outcome == LocalGapHashOutcome.ChangedDuringRead,
            "native urgent read classifies repeated concurrent saves as deferral, not failure");
        File.SetLastWriteTimeUtc(path, item.Mtime.UtcDateTime);
    }

    private static NtfsFileUsn? NativeEvidence(SafeFileHandle file)
    {
        if (!PilotDirectoryLease.GetFileInformationByHandle(file, out var info)) return null;
        return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, 100,
            checked((long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow)),
            ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime);
    }
}
