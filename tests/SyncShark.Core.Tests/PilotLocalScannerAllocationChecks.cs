using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using Massdex.SyncShark;

internal static class PilotLocalScannerAllocationChecks
{
    internal static async Task<int> MeasureReadOnlyAsync(string root, bool concurrent = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var lease = PilotDirectoryLease.Open(root);
        var watch = Stopwatch.StartNew();
        var inspected = 0;
        var currentPath = "";
        long bytes = 0;
        var completedHashes = 0;
        using var progress = SyncByteProgressStream.Begin(root, item =>
        {
            if (item.Total is > 0 and { } total && item.Bytes == total)
            { Interlocked.Increment(ref completedHashes); Interlocked.Add(ref bytes, total); }
            return Task.CompletedTask;
        });
        try
        {
            var controller = concurrent ? new SyncTransferResourceController(
                new SyncTransferSettings(ResourceMode: "Performance", MaximumConcurrentFiles: 32)) : null;
            var scan = await new PilotLocalScanner { InventoryScheduling = controller?.ForEndpoints([]) }
                .ScanAsync(lease, cancellationToken: deadline.Token,
                onInventory: (path, count) =>
                { currentPath = path; inspected = count; return Task.CompletedTask; });
            Console.WriteLine($"COMPLETE actual scanner; elapsed {watch.Elapsed.TotalSeconds:F3}s; inspected {scan.PathsInspected}; " +
                $"fresh hashes {scan.FilesHashed}; bytes {scan.TotalBytes}; uncertainty {scan.Decisions.Count}; " +
                $"concurrent={concurrent}; no durable inventory, product feedback, provider calls, or file effects. Existing filesystem-cache warmth is not controlled.");
            return scan.Decisions.Count == 0 ? 0 : 1;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Console.WriteLine($"INCOMPLETE actual scanner; elapsed {watch.Elapsed.TotalSeconds:F3}s; inspected {inspected}; " +
                $"nonempty hash byte-completion reports {completedHashes}; bytes {bytes}; current {currentPath}; " +
                "30-second deadline unchanged; no durable inventory, product feedback, provider calls, or file effects.");
            return 130;
        }
    }

    internal static async Task ConcurrentAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh concurrent scanner fixture.");
        Directory.CreateDirectory(root);
        const int count = 32;
        for (var index = 0; index < count; index++)
            await File.WriteAllTextAsync(Path.Combine(root, $"item-{index:D2}.txt"), "bytes-" + index);
        using var lease = PilotDirectoryLease.Open(root);
        var controller = new SyncTransferResourceController(new SyncTransferSettings(
            ResourceMode: "Performance", MaximumConcurrentFiles: 4), TimeProvider.System,
            () => new SyncResourcePressure(0, .2, 8L * 1024 * 1024 * 1024));
        var sharingRetries = 0;
        var scanner = new PilotLocalScanner { InventoryScheduling = controller.ForEndpoints([]),
            SharingRetryForTest = _ => sharingRetries++ };
        var active = 0;
        var peak = 0;
        var gate = new object();
        using (SyncByteProgressStream.Begin(root, async item =>
        {
            if (item.Bytes == 0)
            {
                lock (gate) { active++; peak = Math.Max(peak, active); }
                await Task.Delay(10);
            }
            else if (item.Bytes == item.Total) lock (gate) active--;
        }))
        {
            var parallel = await scanner.ScanAsync(lease);
            Require(peak > 1 && peak <= 4 && active == 0 && controller.Snapshot().ActiveFiles == 0,
                "Real held-byte inspection overlaps within the shared resource ceiling and drains all admissions");
            var serial = await new PilotLocalScanner().ScanAsync(lease);
            Require(parallel.FilesHashed == count && parallel.HashesReused == 0 && parallel.Decisions.Count == 0 &&
                    parallel.TotalBytes == serial.TotalBytes && parallel.Entries.SequenceEqual(serial.Entries),
                "Parallel and serial production scanners retain identical exact entries, ordering and fresh hashes");
        }
        var recoveryClock = new InspectionClock();
        var pressure = new SyncResourcePressure(.1, .995, 128L * 1024 * 1024);
        var recovering = new SyncTransferResourceController(new(ResourceMode: "Performance", MaximumConcurrentFiles: 4),
            recoveryClock, () => pressure);
        var recoveringScanner = new PilotLocalScanner { InventoryScheduling = recovering.ForEndpoints([]) };
        active = peak = 0;
        var recoveryStarted = 0;
        using (SyncByteProgressStream.Begin(root, async item =>
        {
            if (item.Bytes == 0)
            {
                if (Interlocked.Exchange(ref recoveryStarted, 1) == 0)
                { pressure = new(.1, .97, 512L * 1024 * 1024); recoveryClock.Advance(); }
                lock (gate) { active++; peak = Math.Max(peak, active); }
                await Task.Delay(20);
            }
            else if (item.Bytes == item.Total) lock (gate) active--;
        }))
        {
            var recovered = await recoveringScanner.ScanAsync(lease);
            Require(peak > 1 && peak <= 4 && active == 0 && recovered.FilesHashed == count &&
                    recovered.Decisions.Count == 0 && recovering.Snapshot() is
                    { ActiveFiles: 0, ActiveInspections: 0, AllowedFiles: 1, AllowedInspections: 4,
                      FilesPerSecond: 0, BytesPerSecond: 0 },
                "The actual scanner selects explicit inspection recovery after pressure clears, without changing transfer limits or fabricating synced files");
        }
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(60)))
        using (SyncByteProgressStream.Begin(root, item => item.Bytes == 0
            ? Task.Delay(1000, deadline.Token) : Task.CompletedTask))
        {
            var cancelled = false;
            try { await scanner.ScanAsync(lease, cancellationToken: deadline.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && controller.Snapshot().ActiveFiles == 0,
                "Cancellation returns no complete inventory and drains every resource lease before root release");
        }
        // Opening every file exclusively after cancellation proves actual handles closed,
        // not just that an accounting counter reached zero.
        foreach (var path in Directory.GetFiles(root))
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var link = Path.Combine(root, "item-00a.txt");
        if (!CreateHardLink(link, Path.Combine(root, "item-00.txt"), IntPtr.Zero))
            throw new IOException("The hard-link fixture could not be created.");
        // Adjacent names plus held hashing callbacks exercise actual intra-scan
        // exclusive-handle contention, rather than two sequential hard-link reads.
        using (SyncByteProgressStream.Begin(root, item => item.Bytes == 0 ? Task.Delay(50) : Task.CompletedTask))
        {
            var linked = await scanner.ScanAsync(lease);
            Require(sharingRetries > 0 && linked.Decisions.Count == 0 && linked.FilesHashed == count + 1 &&
                    linked.Entries["item-00a.txt"].FileId == linked.Entries["item-00.txt"].FileId &&
                    linked.Entries["item-00a.txt"].ContentHash == linked.Entries["item-00.txt"].ContentHash,
                "Own concurrent readers do not turn two hard-link names into a false inaccessible-file decision");
        }
        using (var held = new FileStream(Path.Combine(root, "item-01.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = await scanner.ScanAsync(lease);
            Require(blocked.Decisions.Any(item => item.RelativePath == "item-01.txt" && item.Kind == PilotDecisionKind.Blocked) &&
                    !blocked.Entries.ContainsKey("item-01.txt") && controller.Snapshot().ActiveFiles == 0,
                "An externally locked file remains explicit uncertainty; concurrent inspection never invents absence or success");
        }
        await OrderedDirectoryResultsAsync(Path.Combine(root, "ordered-results"));
        Console.WriteLine("PASS real concurrent scanner success, identical fresh evidence, cancellation/handles, hard-link and locked-file refusal");
    }

    private static async Task OrderedDirectoryResultsAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "b-directory", "d-subdirectory"));
        await File.WriteAllTextAsync(Path.Combine(root, "a-first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(root, "c-later.txt"), "later");
        await File.WriteAllTextAsync(Path.Combine(root, "b-directory", "nested.txt"), "nested");
        await File.WriteAllTextAsync(Path.Combine(root, "b-directory", "d-subdirectory", "child.txt"), "child");
        using var rootLease = PilotDirectoryLease.Open(root);
        var controller = new SyncTransferResourceController(new(ResourceMode: "Performance", MaximumConcurrentFiles: 4),
            TimeProvider.System, () => new(.1, .2, 8L * 1024 * 1024 * 1024));
        var scanner = new PilotLocalScanner { InventoryScheduling = controller.ForEndpoints([]) };
        var laterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (SyncByteProgressStream.Begin(root, async item =>
        {
            if (item.Bytes != 0) return;
            if (item.Path == "c-later.txt") laterStarted.TrySetResult();
            if (item.Path == "a-first.txt")
            {
                try { await laterStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException error)
                { throw new InvalidOperationException("A verified directory must not drain the first hash before a later file can start.", error); }
            }
        }))
        {
            var parallel = await scanner.ScanAsync(rootLease);
            var serial = await new PilotLocalScanner().ScanAsync(rootLease);
            Require(laterStarted.Task.IsCompletedSuccessfully && parallel.FilesHashed == 4 &&
                    parallel.Entries.Count == 6 && parallel.TotalBytes == serial.TotalBytes &&
                    parallel.Entries.SequenceEqual(serial.Entries) && parallel.Decisions.SequenceEqual(serial.Decisions) &&
                    controller.Snapshot() is { ActiveFiles: 0, ActiveInspections: 0 },
                "File-directory-file and nested results overlap, preserve serial publication order, and count only fresh file hashes");
        }
        var parallelExcluded = await scanner.ScanAsync(rootLease,
            includePath: path => !path.StartsWith("b-directory/", StringComparison.Ordinal));
        var serialExcluded = await new PilotLocalScanner().ScanAsync(rootLease,
            includePath: path => !path.StartsWith("b-directory/", StringComparison.Ordinal));
        Require(parallelExcluded.FilesHashed == 2 && parallelExcluded.Entries.Count == 4 &&
                parallelExcluded.Entries.SequenceEqual(serialExcluded.Entries) &&
                parallelExcluded.Decisions.SequenceEqual(serialExcluded.Decisions),
            "Excluded file bytes are never opened while the directory-only selection catalog stays complete and ordered");

        static bool RejectTraversal(string path) => path == "b-directory"
            ? throw new IOException("fixture traversal metadata unavailable") : true;
        var parallelRejected = await scanner.ScanAsync(rootLease, traverseDirectory: RejectTraversal);
        var serialRejected = await new PilotLocalScanner().ScanAsync(rootLease, traverseDirectory: RejectTraversal);
        Require(parallelRejected.Entries.ContainsKey("b-directory") && parallelRejected.Decisions.Count == 1 &&
                parallelRejected.FilesHashed == 2 && parallelRejected.Entries.SequenceEqual(serialRejected.Entries) &&
                parallelRejected.Decisions.SequenceEqual(serialRejected.Decisions),
            "A later directory traversal failure retains its verified catalog entry and exact ordered uncertainty without relabelling earlier readers");
        var refreshed = await scanner.ScanAsync(rootLease, refreshPaths: ["b-directory"]);
        var serialRefreshed = await new PilotLocalScanner().ScanAsync(rootLease, refreshPaths: ["b-directory"]);
        var retained = await new PilotLocalScanner().ScanAsync(rootLease);
        var merged = PilotLocalScanner.MergeIncremental(retained, refreshed, ["b-directory"]);
        Require(refreshed.FilesHashed == 2 && refreshed.Entries.Count == 4 &&
                refreshed.Entries.SequenceEqual(serialRefreshed.Entries) &&
                merged.Entries.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .SequenceEqual(retained.Entries.OrderBy(item => item.Key, StringComparer.Ordinal)),
            "Scheduled directory/subtree refresh preserves exact descendants and merge leaves unrelated retained entries unchanged");

        foreach (var callbackFailure in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var progress = SyncByteProgressStream.Begin(root, async item =>
            {
                if (item.Bytes == 0 && item.Path == "a-first.txt")
                { firstStarted.TrySetResult(); await release.Task.WaitAsync(cancellation.Token); }
            });
            var refused = false;
            try
            {
                await scanner.ScanAsync(rootLease, cancellationToken: cancellation.Token,
                    onInventory: async (path, _) =>
                    {
                        if (path != "c-later.txt") return;
                        await firstStarted.Task.WaitAsync(cancellation.Token);
                        if (callbackFailure)
                        { release.TrySetResult(); throw new InvalidOperationException("fixture inventory callback failed"); }
                        cancellation.Cancel();
                    });
            }
            catch (OperationCanceledException) when (!callbackFailure) { refused = true; }
            catch (InvalidOperationException error) when (callbackFailure && error.Message == "fixture inventory callback failed")
            { refused = true; }
            Require(refused && controller.Snapshot() is { ActiveFiles: 0, ActiveInspections: 0 },
                "Cancellation and inventory callback failure across directories publish no complete inventory and drain every admission");
            foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }

        var collisionRoot = Path.Combine(root, "canonical-collision");
        Directory.CreateDirectory(collisionRoot);
        Directory.CreateDirectory(Path.Combine(collisionRoot, "\u00e9"));
        await File.WriteAllTextAsync(Path.Combine(collisionRoot, "e\u0301"), "colliding file");
        using var collisionLease = PilotDirectoryLease.Open(collisionRoot);
        var parallelCollision = await scanner.ScanAsync(collisionLease);
        var serialCollision = await new PilotLocalScanner().ScanAsync(collisionLease);
        Require(parallelCollision.Entries.Count == 0 && parallelCollision.Decisions.Count == 2 &&
                parallelCollision.Entries.SequenceEqual(serialCollision.Entries) &&
                parallelCollision.Decisions.SequenceEqual(serialCollision.Decisions),
            "NFC-equivalent file/directory names remain blocked with identical serial and concurrent collision decisions");
    }

    private sealed class InspectionClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        internal void Advance() => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(4).Ticks);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh scanner allocation fixture.");
        Directory.CreateDirectory(root);
        var fileBytes = Encoding.UTF8.GetBytes("A tiny file must not allocate the provider's four-MiB hash block.\n");
        const int count = 128;
        for (var index = 0; index < count; index++)
            await File.WriteAllBytesAsync(Path.Combine(root, $"file-{index:D3}.txt"), fileBytes);
        using var lease = PilotDirectoryLease.Open(root);
        var scanner = new PilotLocalScanner();
        // Warm the actual scanner/held-handle/hash path, not a surrogate stream benchmark.
        await scanner.ScanAsync(lease, refreshPaths: ["file-000.txt"]);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var scan = await scanner.ScanAsync(lease);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Require(scan.FilesHashed == count && scan.HashesReused == 0 && scan.Entries.Count == count &&
                scan.TotalBytes == (long)count * fileBytes.Length && scan.Decisions.Count == 0 &&
                scan.Entries.Values.All(entry => entry.ContentHash == DropboxContentHash.Compute(fileBytes)),
            "The real complete scan still freshly hashes and retains every exact file's bytes");
        Console.WriteLine($"Scanner measured allocation: {allocated:N0} bytes for {count} freshly hashed tiny files; elapsed {watch.Elapsed.TotalSeconds:F3}s");
        Require(allocated < 64L * 1024 * 1024,
            "Tiny-file scanning must not allocate a four-MiB FileStream buffer for each file");

        var path = Path.Combine(root, "file-000.txt");
        var modified = File.GetLastWriteTimeUtc(path);
        var replacement = (byte[])fileBytes.Clone();
        replacement[0] ^= 1;
        await File.WriteAllBytesAsync(path, replacement);
        File.SetLastWriteTimeUtc(path, modified);
        var fresh = await scanner.ScanAsync(lease, refreshPaths: ["file-000.txt"]);
        Require(fresh.FilesHashed == 1 && fresh.HashesReused == 0 &&
                fresh.Entries["file-000.txt"].ContentHash == DropboxContentHash.Compute(replacement) &&
                fresh.Entries["file-000.txt"].ContentHash != scan.Entries["file-000.txt"].ContentHash,
            "Same-size edits with restored timestamps remain fresh byte-identity changes");
        Console.WriteLine("PASS real scanner bounded allocation and same-size/restored-time freshness");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
