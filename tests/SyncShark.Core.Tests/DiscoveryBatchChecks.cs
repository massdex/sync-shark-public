using Massdex.SyncShark;

internal static class DiscoveryBatchChecks
{
    internal static async Task RunAsync(string folder)
    {
        folder = Path.Combine(folder, "batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
        var count = 0;
        void Check(bool pass, string label)
        { if (!pass) throw new IOException(label); count++; Console.WriteLine("PASS " + label); }
        var paths = new[] { "a", "b", "excluded" }.Select(name => Path.Combine(folder, name)).ToArray();
        foreach (var path in paths) Directory.CreateDirectory(path);
        using var a = PilotDirectoryLease.Open(paths[0]);
        using var b = PilotDirectoryLease.Open(paths[1]);
        using var excluded = PilotDirectoryLease.Open(paths[2]);
        SyncPair Pair(PilotDirectoryLease lease, string id) => new(id, id, lease.Path,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/" + id, "id:" + id, ConnectionId: id);
        var pa = Pair(a, "account-a"); var pb = Pair(b, "account-b"); var px = Pair(excluded, "excluded");
        var checkpoint = new NtfsUsnCheckpoint(17, 1, 100, 1);
        var reads = 0;
        IReadOnlyList<SyncDiscoveryRoot>? captured = null;
        SyncDiscoveryResult Result(IReadOnlyList<SyncDiscoveryRoot> roots) => new(
            roots.Select(root => new SyncDiscoveryView(root, new Dictionary<string, SyncDiscoveryItem>())).ToArray(),
            checkpoint, checkpoint with { NextUsn = 110 });
        Task<SyncDiscoveryResult?> Read(IReadOnlyList<SyncDiscoveryRoot> roots, CancellationToken token)
        { token.ThrowIfCancellationRequested(); reads++; captured = roots; return Task.FromResult<SyncDiscoveryResult?>(Result(roots)); }

        using (SyncDiscoveryBatch.Begin([pa, pb, pa with { Id = "duplicate" },
                   px with { FilesOnDemandEnabled = true }, pa with { Id = "wrong-volume", LocalVolumeSerialNumber = pa.LocalVolumeSerialNumber + 1 },
                   px with { Id = "wrong-identity", LocalRootFileId = px.LocalRootFileId + 1 }], Read))
        {
            var views = await Task.WhenAll(SyncDiscoveryBatch.ReadViewAsync(pa, a, checkpoint, default),
                SyncDiscoveryBatch.ReadViewAsync(pb, b, checkpoint, default));
            Check(reads == 1 && captured?.Count == 2, "Real physical-volume cache makes one request for two admitted roots; duplicate, FOD and invalid identities are excluded");
            Check(views[0]?.Root.Path == a.Path && views[1]?.Root.Path == b.Path,
                "Each account/pair receives only its own requested root from the shared result");
            Check(await SyncDiscoveryBatch.ReadViewAsync(px, excluded, checkpoint, default) is null && reads == 1,
                "A root absent from the cached request cannot borrow another pair's metadata");
            Check(await SyncDiscoveryBatch.ReadViewAsync(pa with { LocalRootFileId = pa.LocalRootFileId + 1 }, a, checkpoint, default) is null,
                "A mismatched requested identity cannot receive a cached root view");
            Check(await SyncDiscoveryBatch.ReadViewAsync(pa, a, checkpoint with { JournalId = 18 }, default) is null,
                "Replaced journal rejects the cached metadata result");
            Check(await SyncDiscoveryBatch.ReadViewAsync(pa, a, checkpoint with { LowestValidUsn = 101 }, default) is null,
                "Journal low-water gap rejects metadata older than the retained checkpoint");
            Check(await SyncDiscoveryBatch.ReadViewAsync(pa, a, checkpoint with { NextUsn = 0 }, default) is null,
                "Journal first-USN gap rejects a result that cannot cover the retained cursor");
        }
        foreach (var cancelled in new[] { false, true })
        {
            reads = 0;
            using var cancellation = new CancellationTokenSource();
            using (SyncDiscoveryBatch.Begin([pa, pb], (_, token) =>
            {
                reads++;
                if (cancelled) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                throw new IOException("Bounded metadata transport failure");
            }))
            {
                var firstCancelled = false;
                SyncDiscoveryView? failed = null;
                try { failed = await SyncDiscoveryBatch.ReadViewAsync(pa, a, checkpoint, cancellation.Token); }
                catch (OperationCanceledException) { firstCancelled = true; }
                Check(cancelled ? firstCancelled : failed is null, "Failed/cancelled batch request never publishes a partial root view");
                var secondCancelled = false;
                try { failed = await SyncDiscoveryBatch.ReadViewAsync(pb, b, checkpoint, default); }
                catch (OperationCanceledException) { secondCancelled = true; }
                Check(reads == 1 && !secondCancelled && failed is null,
                    "Failed/cancelled cached request is not repeated and does not cancel an independent pair; no partial view is published");
            }
            using (SyncDiscoveryBatch.Begin([pa], Read))
                Check(await SyncDiscoveryBatch.ReadViewAsync(pa, a, checkpoint, default) is not null,
                    "A fresh batch can use a legitimate request after failure or cancellation");
        }
        using (SyncDiscoveryBatch.Begin([pa], (_, _) => throw new IOException("Must not be reached")))
            Check(await SyncDiscoveryBatch.ReadViewAsync(pa with { LocalRootFileId = pa.LocalRootFileId + 1 }, a, checkpoint, default) is null,
                "The actual held requested-root identity rejects before the lower-level reader");

        // Exercise ScanWithJournalAsync, the same body called by public ScanAsync.
        // Only journal/metadata observations are controlled: leases, scanner,
        // selection, hash computation, incremental merge and full fallback are real.
        foreach (var mode in new[] { "valid", "journal-replaced", "journal-gap", "unavailable", "slow", "hard-link", "peer-cancelled" })
        {
            var path = Path.Combine(folder, "inventory-" + mode); Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "retained.txt"), "retained");
            using var lease = PilotDirectoryLease.Open(path); var pair = Pair(lease, mode);
            IReadOnlyList<NtfsUsnChange> changes = [];
            var journal = new SyncLocalInventoryIndex.JournalSource(_ => checkpoint, (_, _, _, _) => changes);
            var first = await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "initial", null, journal);
            Check(!first.Incremental && first.Entries.Count == 1, "Real initial inventory establishes the retained " + mode + " root");
            var added = Path.Combine(path, "discovered.txt"); await File.WriteAllTextAsync(added, "new metadata target");
            var identity = WindowsCloudFilesProvider.ReadStorageFileIdentity(added, pair.Id);
            changes = [new(identity.LocalId, pair.LocalRootFileId, 99, 0x100, FileAttributes.Normal, "")];
            if (mode == "hard-link") changes = [new(0xf000000000000001, 0xf000000000000002,
                99, 0x10000, FileAttributes.Normal, "unrelated-parent")];
            reads = 0;
            var progress = new List<SyncComparisonProgress>();
            var boundedReadCancelled = false;
            using var peerCancellation = new CancellationTokenSource();
            using (SyncDiscoveryBatch.Begin([pair], async (roots, token) =>
            {
                reads++;
                if (mode == "peer-cancelled")
                {
                    peerCancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                if (mode == "unavailable") return null;
                if (mode == "slow")
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    { boundedReadCancelled = true; throw; }
                }
                var hint = new SyncDiscoveryItem("discovered.txt", identity.LocalId, pair.LocalRootFileId, 100,
                    FileAttributes.Normal, 1, new FileInfo(added).Length, File.GetLastWriteTimeUtc(added));
                var before = mode == "journal-replaced" ? checkpoint with { JournalId = 18 } : checkpoint;
                var after = mode == "journal-gap" ? checkpoint with { FirstUsn = 101, NextUsn = 110 } : checkpoint;
                return new([new(roots.Single(),
                    new Dictionary<string, SyncDiscoveryItem> { [hint.RelativePath] = hint })], before, after);
            }))
            {
                if (mode == "peer-cancelled")
                {
                    var cancelledPeer = false;
                    try { await SyncDiscoveryBatch.ReadViewAsync(pair, lease, checkpoint, peerCancellation.Token); }
                    catch (OperationCanceledException) when (peerCancellation.IsCancellationRequested)
                    { cancelledPeer = true; }
                    Check(cancelledPeer, "The original caller's own cancellation remains effective");
                }
                var scan = await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "monitor", null, journal,
                    onDiscoveryProgress: update => { progress.Add(update); return Task.CompletedTask; });
                Check(reads == (mode == "hard-link" ? 0 : 1) && scan.Entries.Count == 2 && scan.Entries["discovered.txt"].ContentHash is not null,
                    "Actual batch-to-inventory consumer reaches the real scanner and preserves both files for " + mode);
                Check(mode == "valid" ? scan.Incremental && scan.PathsInspected == 1 : !scan.Incremental && scan.PathsInspected == 2,
                    "Valid hints refresh one path; rejected/unavailable metadata triggers full ordinary inventory for " + mode);
                if (mode == "slow") Check(boundedReadCancelled,
                    "A slow automatic drive read is cancelled at the bounded budget before root-scoped fallback");
                if (mode is "slow" or "hard-link" or "peer-cancelled") Check(progress.Any(item => item.RelativePath == lease.Path &&
                    item.Detail.Contains("selected folder only", StringComparison.Ordinal)),
                    "Fallback explains the actual selected-folder operation for " + mode);
            }
        }
        Console.WriteLine($"{count} actual Discovery batch/inventory consumer checks passed. Metadata/journal inputs controlled; real roots, leases, volume cache and scanner; no elevated scan/provider effect.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
