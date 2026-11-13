using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>A shared, identity-bound local index. Complete NTFS journal continuity authorizes
/// changed-path refresh; unavailable journals, gaps, policy changes, uncertainty and safety checks
/// use the existing complete scanner. The index is disposable and never grants effect authority.
/// The index holds verified metadata only (no file bytes are read); the observation coordinator hashes
/// the qualifying paths after both endpoint inventories are known.</summary>
public static class SyncLocalInventoryIndex
{
    // One process-wide index per physical volume, shared by all account/pair views. Initial
    // population reads only explicitly selected roots; journal continuity is shared at the volume.
    private static readonly ConcurrentDictionary<uint, VolumeIndex> Volumes = new();

    internal sealed record JournalSource(Func<string, NtfsUsnCheckpoint?> Checkpoint,
        Func<string, NtfsUsnCheckpoint, NtfsUsnCheckpoint, CancellationToken, IReadOnlyList<NtfsUsnChange>?> Changes);
    private static readonly JournalSource NativeJournal = new(
        path => NtfsUsnJournal.TryReadCheckpoint(path, out var checkpoint) ? checkpoint : null,
        (path, before, after, token) => NtfsUsnJournal.TryReadChanges(path, before, after, out var changes, token) ? changes : null);

    public static Task<PilotLocalScan> ScanAsync(SyncPair pair, PilotDirectoryLease root,
        string trigger, Func<string, int, Task>? onInventory, CancellationToken cancellationToken = default,
        Func<SyncComparisonProgress, Task>? onDiscoveryProgress = null,
        SyncTransferResourceController? inventoryResources = null) =>
        ScanWithJournalAsync(pair, root, trigger, onInventory, NativeJournal, cancellationToken, onDiscoveryProgress, inventoryResources);

    // Only the OS journal observations are replaceable by the focused fixture;
    // the production scanner, selection, incremental merge and full fallback run intact.
    internal static async Task<PilotLocalScan> ScanWithJournalAsync(SyncPair pair, PilotDirectoryLease root,
        string trigger, Func<string, int, Task>? onInventory, JournalSource journal,
        CancellationToken cancellationToken = default,
        Func<SyncComparisonProgress, Task>? onDiscoveryProgress = null,
        SyncTransferResourceController? inventoryResources = null)
    {
        root.VerifyBinding();
        var key = Path.GetFullPath(root.Path);
        var volume = Volumes.GetOrAdd(pair.LocalVolumeSerialNumber, _ => new VolumeIndex());
        var index = volume.Roots.GetOrAdd(key, _ => new RootIndex());
        await index.Gate.WaitAsync(cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            // The disposable index is shared by every account/pair view of one physical root, so the owning
            // pair, its provider and that provider's registered content algorithm belong in the compatibility
            // key beside the existing volume/root/selection/ignore fields. A provider, registered-algorithm
            // or owning-pair rebind therefore forces the complete scanner, which hashes the held bytes with
            // the newly registered algorithm, instead of reusing another binding's retained hashes.
            var policy = JsonSerializer.Serialize(new { pair.LocalVolumeSerialNumber, pair.LocalRootFileId,
                pair.SelectionDefaultIncluded, Provider = pair.Provider,
                ContentAlgorithm = SyncContentIdentity.AlgorithmFor(pair.Provider), pair.Id,
                Rules = SyncSelectionPolicy.Normalize(pair.SelectionRules),
                IgnoreRules = SyncIgnorePolicy.Normalize(pair.IgnoreRules) });
            var selection = SyncSelectionPolicy.Compile(pair);
            var notificationPaths = index.Notifications.Capture(root.Path, out var notificationContinuity);
            var before = journal.Checkpoint(root.Path);
            var hasJournal = before is not null;
            var safety = trigger.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
                         trigger.Contains("periodic", StringComparison.OrdinalIgnoreCase) ||
                         trigger.Contains("full", StringComparison.OrdinalIgnoreCase) ||
                         // An armed FileSystemWatcher has no synchronous high-water
                         // checkpoint: a completed edit may still await its callback.
                         // An explicit manual inventory without a native journal
                         // therefore reads the current held files, even if no callback
                         // has arrived. Event-triggered monitoring remains incremental.
                         (!hasJournal && trigger.Equals("manual", StringComparison.OrdinalIgnoreCase));
            var incremental = !safety && index.Policy == policy && index.Scan is { Decisions.Count: 0 } &&
                              DateTimeOffset.UtcNow - index.FullScanAt < TimeSpan.FromMinutes(15);
            PilotLocalScan? result = null;
            if (incremental)
            {
                IReadOnlyList<string>? paths = null;
                if (hasJournal && index.Checkpoint is not null &&
                    journal.Changes(root.Path, index.Checkpoint, before!, cancellationToken) is { } changes)
                {
                    paths = ResolveChangedPaths(pair.LocalRootFileId, index.Identities!, index.Directories!, changes);
                    // A single MFT parent cannot resolve hard-link ambiguity. Skip that
                    // useless whole-volume read and inspect only the held selected root.
                    if (paths is null && !changes.Any(change => (change.Reason & 0x00010000) != 0) &&
                        SyncDiscoveryBatch.IsAvailable)
                    {
                        var discoveryStarted = DateTimeOffset.UtcNow;
                        Task ReportDiscovery(SyncDiscoveryProgress progress) => onDiscoveryProgress is null
                            ? Task.CompletedTask : onDiscoveryProgress(new(progress.Description, root.Path,
                                (int)Math.Min(progress.RecordsRead, int.MaxValue), 0, discoveryStarted, DateTimeOffset.UtcNow,
                                $"Reading the drive's metadata index to locate changed paths in this folder: {progress.RecordsRead:N0} index records read. No files are being transferred. Automatic discovery falls back to this folder after two seconds.", "index records read"));
                        var view = await SyncDiscoveryBatch.ReadViewAsync(pair, root, before!, cancellationToken, ReportDiscovery);
                        if (view is not null)
                            paths = ResolveChangedPaths(pair.LocalRootFileId, index.Identities!, index.Directories!, changes,
                                view.Items.Values.ToDictionary(item => item.FileId));
                    }
                }
                // A continuously armed Windows directory notification stream provides an independent
                // changed-path source when raw-volume journal access is unavailable. Overflow, restart,
                // unknown events or root changes invalidate it; periodic safety scans remain mandatory.
                else if (!hasJournal && notificationContinuity) paths = notificationPaths;
                if (paths is not null && paths.Count <= 10_000)
                {
                    try
                    {
                        if (paths.Count == 0)
                        {
                            // A continuous journal/watcher with no relevant changes is a
                            // successful idle check, not a request to stat/hash every file.
                            result = index.Scan! with { FilesHashed = 0, HashesReused = 0,
                                PathsInspected = 0, Incremental = true };
                        }
                        else
                        {
                            foreach (var path in paths) VerifyAncestors(root, path);
                            if (onInventory is not null) await onInventory("Refreshing Changed Items", paths.Count);
                            var refreshed = await new PilotLocalScanner
                                { InventoryScheduling = inventoryResources?.ForEndpoints([]), MetadataOnly = true }.ScanAsync(root,
                                cancellationToken: cancellationToken,
                                includePath: selection.IsIncluded, traverseDirectory: selection.ShouldTraverseDirectory,
                                onInventory: onInventory, refreshPaths: paths, contentProvider: pair.Provider, expectedPairId: pair.Id);
                            result = PilotLocalScanner.MergeIncremental(index.Scan!, refreshed, paths,
                                cancellationToken, out _);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Never turn a failed scoped observation into absence. Re-establish the
                        // entire held root and retain any inaccessible/reparse scope as uncertainty.
                    }
                }
            }
            if (result is null)
            {
                if (incremental && onDiscoveryProgress is not null)
                {
                    var now = DateTimeOffset.UtcNow;
                    await onDiscoveryProgress(new("Reading Local Inventory", root.Path, 0, 0, now, now,
                        "Change tracking could not supply a complete bounded result. Checking files in this selected folder only; no whole-drive scan or file transfer is running."));
                }
                // A complete, restarted or uncertain inventory re-reads every held file's metadata. Bytes
                // are hashed afterwards by the observation coordinator, only where a hash can change a decision.
                result = await new PilotLocalScanner
                    { InventoryScheduling = inventoryResources?.ForEndpoints([]), MetadataOnly = true }.ScanAsync(root,
                    cancellationToken: cancellationToken,
                    includePath: selection.IsIncluded, traverseDirectory: selection.ShouldTraverseDirectory,
                    onInventory: onInventory, contentProvider: pair.Provider, expectedPairId: pair.Id);
                index.FullScanAt = DateTimeOffset.UtcNow;
            }
            root.VerifyBinding();
            var sameEntries = ReferenceEquals(index.Scan?.Entries, result.Entries);
            index.Scan = result;
            // Keep identity/path projections between events instead of rebuilding them on every
            // monitor pulse. The complete scanner still establishes the initial trusted view.
            if (!sameEntries)
            {
                index.Identities = result.Entries.Values.Where(entry => entry.FileId.HasValue)
                    .GroupBy(entry => entry.FileId!.Value).ToDictionary(group => group.Key, group => group.ToArray());
                index.Directories = result.Entries.Values.Where(entry => entry.Kind == PilotEntryKind.Directory && entry.FileId.HasValue)
                    .GroupBy(entry => entry.FileId!.Value).ToDictionary(group => group.Key, group => group.Select(entry => entry.RelativePath).ToArray());
                index.Directories[pair.LocalRootFileId] = [string.Empty];
            }
            index.Policy = policy;
            index.VolumeSerial = pair.LocalVolumeSerialNumber;
            index.RootFileId = pair.LocalRootFileId;
            // Retain the checkpoint BEFORE reading files, so concurrent changes are revisited.
            index.Checkpoint = hasJournal ? before : null;
            index.LastUsed = DateTimeOffset.UtcNow;
            if (volume.Roots.Count > 128)
                foreach (var stale in volume.Roots.Where(entry => entry.Value != index &&
                             entry.Value.Gate.CurrentCount == 1 &&
                             DateTimeOffset.UtcNow - entry.Value.LastUsed > TimeSpan.FromMinutes(30)).Take(16))
                    if (volume.Roots.TryRemove(stale.Key, out var removed)) removed.Notifications.Dispose();
            return result with { Elapsed = stopwatch.Elapsed };
        }
        finally { index.Gate.Release(); }
    }

    internal static IReadOnlyList<string>? ResolveChangedPaths(ulong rootFileId, PilotLocalScan retained,
        IReadOnlyList<NtfsUsnChange> changes, IReadOnlyDictionary<ulong, SyncDiscoveryItem>? discovery = null)
    {
        var identities = retained.Entries.Values.Where(entry => entry.FileId.HasValue)
            .GroupBy(entry => entry.FileId!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        var directories = retained.Entries.Values.Where(entry => entry.Kind == PilotEntryKind.Directory &&
            entry.FileId.HasValue).GroupBy(entry => entry.FileId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.RelativePath).ToArray());
        directories[rootFileId] = [string.Empty];
        return ResolveChangedPaths(rootFileId, identities, directories, changes, discovery);
    }

    private static IReadOnlyList<string>? ResolveChangedPaths(ulong rootFileId,
        IReadOnlyDictionary<ulong, PilotLocalEntry[]> identities, IReadOnlyDictionary<ulong, string[]> directories,
        IReadOnlyList<NtfsUsnChange> changes, IReadOnlyDictionary<ulong, SyncDiscoveryItem>? discovery = null)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            // A new hard-link name can lie inside this root even when the MFT's
            // one reported parent lies outside it. Never infer that it is unrelated.
            if ((change.Reason & 0x00010000) != 0) return null;
            if (change.FileId == rootFileId)
            {
                if ((change.Reason & 0x00113200) != 0) return null;
                continue;
            }
            if (identities.TryGetValue(change.FileId, out var previous))
                foreach (var entry in previous) paths.Add(entry.RelativePath);
            var hint = discovery?.GetValueOrDefault(change.FileId);
            if (hint is not null)
            {
                if (hint.Usn < change.Usn || hint.LinkCount != 1) return null;
                // This adds a target for the ordinary scanner; it never supplies
                // content, bypasses selection rules or declares any old path absent.
                paths.Add(hint.RelativePath);
            }
            if (!directories.TryGetValue(change.ParentFileId, out var parents)) continue;
            if (change.Name.Length == 0)
            {
                if (hint is not null) continue;
                // Unprivileged Windows journals may omit names. Known non-structural file
                // identities can still refresh exactly; new/renamed names require parent inventory.
                if (previous is not null && (change.Reason & 0x00003100) == 0) continue;
                foreach (var parent in parents) { if (parent.Length == 0) return null; paths.Add(parent); }
                continue;
            }
            if (change.Name is "." or ".." ||
                change.Name.IndexOfAny(['/', '\\', ':', '\0']) >= 0) return null;
            foreach (var parent in parents)
                paths.Add(PilotPath.NormalizeRelative(parent.Length == 0 ? change.Name : parent + "/" + change.Name));
        }
        // Rescanning one changed directory includes its descendants, with the existing reparse and
        // identity guards. Do not open every child again as an independent refresh root.
        return paths.OrderBy(path => path.Count(character => character == '/')).ThenBy(path => path,
                StringComparer.OrdinalIgnoreCase).Aggregate(new List<string>(), (roots, path) =>
                { if (!roots.Any(parent => SyncDirectoryStoragePolicy.Contains(parent, path))) roots.Add(path); return roots; });
    }

    internal static bool TryResolveMonitorPaths(SyncPair pair, IReadOnlyList<NtfsUsnChange> changes,
        out IReadOnlyList<string>? paths)
    {
        paths = null;
        if (!Volumes.TryGetValue(pair.LocalVolumeSerialNumber, out var volume) ||
            !volume.Roots.TryGetValue(Path.GetFullPath(pair.LocalRoot), out var index) ||
            !index.Gate.Wait(0)) return false;
        try
        {
            if (index.Scan is not { Decisions.Count: 0 } || index.Checkpoint is null ||
                index.VolumeSerial != pair.LocalVolumeSerialNumber || index.RootFileId != pair.LocalRootFileId)
                return false;
            paths = ResolveChangedPaths(pair.LocalRootFileId, index.Identities!, index.Directories!, changes);
            return paths is not null;
        }
        finally { index.Gate.Release(); }
    }

    private static void VerifyAncestors(PilotDirectoryLease root, string relative)
    {
        var parent = Path.GetDirectoryName(Path.Combine(root.Path, relative.Replace('/', Path.DirectorySeparatorChar)));
        while (parent is not null && !string.Equals(parent, root.Path, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var attributes = File.GetAttributes(parent);
                if (PilotPath.IsUnsafeAttributes(attributes) &&
                    !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(parent, out _))
                    throw new IOException("A changed path crosses a foreign reparse point.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            parent = Path.GetDirectoryName(parent);
        }
        root.VerifyBinding();
    }

    private sealed class VolumeIndex
    {
        public ConcurrentDictionary<string, RootIndex> Roots { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RootIndex
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public PilotLocalScan? Scan;
        public Dictionary<ulong, PilotLocalEntry[]>? Identities;
        public Dictionary<ulong, string[]>? Directories;
        public NtfsUsnCheckpoint? Checkpoint;
        public string? Policy;
        public DateTimeOffset FullScanAt;
        public DateTimeOffset LastUsed;
        public uint VolumeSerial;
        public ulong RootFileId;
        public NotificationIndex Notifications { get; } = new();
    }

    private sealed class NotificationIndex : IDisposable
    {
        private readonly object _gate = new();
        private FileSystemWatcher? _watcher;
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private bool _continuity;

        public IReadOnlyList<string> Capture(string root, out bool continuous)
        {
            lock (_gate)
            {
                continuous = _watcher is not null && _continuity;
                if (_watcher is null || !_continuity)
                {
                    _watcher?.Dispose(); _paths.Clear();
                    _watcher = new FileSystemWatcher(root)
                    { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                                       NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.Security };
                    _watcher.Changed += (_, e) => Record(root, e.FullPath);
                    _watcher.Created += (_, e) => Record(root, e.FullPath);
                    _watcher.Deleted += (_, e) => Record(root, e.FullPath);
                    _watcher.Renamed += (_, e) => { Record(root, e.OldFullPath); Record(root, e.FullPath); };
                    _watcher.Error += (_, _) => { lock (_gate) _continuity = false; };
                    _continuity = true;
                    try { _watcher.EnableRaisingEvents = true; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { _continuity = false; }
                }
                var result = _paths.OrderBy(path => path.Count(character => character == '/')).Aggregate(new List<string>(), (list, path) =>
                { if (!list.Any(parent => SyncDirectoryStoragePolicy.Contains(parent, path))) list.Add(path); return list; });
                _paths.Clear(); return result;
            }
        }

        private void Record(string root, string fullPath)
        {
            lock (_gate)
            {
                try
                {
                    var path = Path.GetRelativePath(root, fullPath);
                    if (path == "." || _paths.Count >= 10_000) { _continuity = false; return; }
                    _paths.Add(PilotPath.NormalizeRelative(path));
                }
                catch (Exception error) when (error is ArgumentException or IOException) { _continuity = false; }
            }
        }
        public void Dispose() { lock (_gate) { _watcher?.Dispose(); _watcher = null; _continuity = false; } }
    }
}
