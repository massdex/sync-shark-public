using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

public sealed record PilotLocalScan(
    IReadOnlyDictionary<string, PilotLocalEntry> Entries,
    IReadOnlyList<PilotDecision> Decisions,
    long TotalBytes,
    int FilesHashed = 0,
    int HashesReused = 0,
    TimeSpan? Elapsed = null,
    int PathsInspected = 0,
    bool Incremental = false,
    bool Complete = true);

public sealed record PilotLocalFileFingerprint(
    string RelativePath,
    uint VolumeSerialNumber,
    ulong FileId,
    long Size,
    DateTime LastWriteUtc);

public sealed class PilotLocalScanner
{
    internal static Action<int>? ParentListingRetainedForTest { get; set; }
    // FileStream buffering is an I/O choice, not the provider's four-MiB hash-block boundary.
    // Hashing reads larger chunks and bypasses this small buffer instead of allocating four MiB
    // for every tiny file. The held handle, exclusive sharing and fresh byte digest are unchanged.
    private const int ReadBufferSize = 64 * 1024;
    internal SyncTransferSchedulingScope? InventoryScheduling { get; init; }
    internal Action<string>? SharingRetryForTest { get; init; }
    // Two-phase inventory: the held, identity-verified metadata pass reads no file bytes. The caller
    // hashes only the paths whose content can change a decision (refreshPaths on a hashing scanner).
    internal bool MetadataOnly { get; init; }
    // The hash read chunk; the v2 local inventory passes its setting, other callers keep the default.
    internal int HashBufferBytes { get; init; } = SyncContentIdentity.DefaultReadBufferBytes;
    // Core v2 another provider's Windows cloud-files placeholder is classified from its
    // parent listing (attributes + reparse tag), never by opening it. Fully local -> an ordinary file/folder; an
    // online-only file with a cloud tag or a hidden reparse point -> an entry marked OnlineOnly (a stub: never
    // opened, never hashed, never absent); an online-only file with another tag -> an entry marked
    // ContentUnreadable. A junction/symlink/mount point -> an entry marked Link, never followed (decision 59).
    // Online-only folders and other reparse tags stay refused (not known).
    internal bool CloudFilesAware { get; init; }
    // Native B-169 proof is needed only when replacement is enabled; otherwise candidates stay left alone.
    internal bool ReplaceOnlineOnlyStubs { get; init; }
    private sealed record FileReadResult(PilotLocalEntry? Entry, PilotDecision? Decision, long Bytes,
        bool RetrySharing = false);
    // One enumerated child; in MetadataOnly the parent's listing already carries its exact metadata.
    private readonly record struct ChildEntry(string FullPath, DirectoryEntryInfo? Metadata);

    public async Task<PilotLocalScan> ScanAsync(PilotDirectoryLease rootLease,
        IReadOnlySet<string>? exactProductOwnedPaths = null, CancellationToken cancellationToken = default,
        Func<string, bool>? includePath = null,
        Func<string, bool>? traverseDirectory = null,
        Func<string, int, Task>? onInventory = null,
        IReadOnlyList<string>? refreshPaths = null,
        string? contentProvider = null, string? expectedPairId = null,
        bool directoriesOnly = false, int? maxEntries = null)
    {
        if (maxEntries is <= 0 or int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        var complete = true;
        var collected = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastProgressReport = TimeSpan.FromSeconds(-1);
        var lastProgressPath = string.Empty;
        // Per-item progress is time-throttled: at most one update per second, and the final count is
        // always emitted once the scan completes. This is reporting only; scanning itself is unchanged.
        async Task ReportProgressAsync(string path, int found)
        {
            if (onInventory is null) return;
            lastProgressPath = path;
            var elapsed = stopwatch.Elapsed;
            if (elapsed - lastProgressReport < TimeSpan.FromSeconds(1)) return;
            lastProgressReport = elapsed;
            try { await onInventory(path, found); }
            catch (Exception) { /* Display failures must not affect local inventory completeness. */ }
        }
        rootLease.VerifyBinding();
        var entries = new Dictionary<string, PilotLocalEntry>(StringComparer.OrdinalIgnoreCase);
        var canonicalPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var blockedCanonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<PilotDecision>();
        long totalBytes = 0;
        var filesHashed = 0;
        var entriesFound = 0;
        using var inspectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inspectionToken = inspectionCancellation.Token;
        // Commit every result in enumeration order. Verified directory metadata
        // occupies the same bounded queue without stopping independent file reads.
        var pendingResults = new Queue<(string Path, string Relative, Task<FileReadResult> Read, bool FileInspection)>();
        var maximumPending = InventoryScheduling?.MaximumPending ?? 1;

        async Task<SyncTransferResourceController.Lease?> AcquireAsync(bool canCommit = true)
        {
            if (InventoryScheduling is null) return null;
            while (true)
            {
                inspectionToken.ThrowIfCancellationRequested();
                if (InventoryScheduling.TryAcquireInspection() is { } lease) return lease;
                if (canCommit && pendingResults.Count > 0) await CommitNextAsync();
                else await Task.Delay(25, inspectionToken).ConfigureAwait(false);
            }
        }
        async Task CommitNextAsync()
        {
            var pending = pendingResults.Dequeue();
            var result = await pending.Read.ConfigureAwait(false);
            if (result.RetrySharing)
            {
                SharingRetryForTest?.Invoke(pending.Relative);
                // Two names can reference the same physical file. Drain our own held
                // readers before ONE read-only retry; never weaken exclusive sharing.
                await Task.WhenAll(pendingResults.Select(item => item.Read)).ConfigureAwait(false);
                var lease = await AcquireAsync(canCommit: false).ConfigureAwait(false);
                result = await ReadFileAsync(pending.Path, pending.Relative, lease).ConfigureAwait(false);
            }
            checked { totalBytes += result.Bytes; }
            if (result.Entry is { } entry)
            {
                if (pending.FileInspection && entry.ContentHash is not null) filesHashed++;
                AddEntry(entries, canonicalPaths, blockedCanonicalPaths, decisions, entry);
            }
            if (result.Decision is { } decision) decisions.Add(decision);
        }
        async Task DrainAsync()
        {
            while (pendingResults.Count > 0) await CommitNextAsync().ConfigureAwait(false);
        }
        async Task<FileReadResult> ReadFileAsync(string exactPath, string relative,
            SyncTransferResourceController.Lease? lease)
        {
            long length = 0;
            try
            {
                inspectionToken.ThrowIfCancellationRequested();
                rootLease.VerifyBinding();
                await using var stream = OpenExclusiveVerified(exactPath, rootLease, out var ownedCloud,
                    expectedPairId, contentProvider);
                length = stream.Length;
                var lastWrite = File.GetLastWriteTimeUtc(exactPath);
                if (!PilotDirectoryLease.GetFileInformationByHandle(stream.SafeFileHandle, out var identity))
                    throw new IOException("Local file identity became unreadable during inventory.");
                var fileId = ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow;
                string? hash = null;
                if (!MetadataOnly)
                    using (var currentFile = SyncByteProgressStream.WithPath(relative))
                        hash = await SyncContentIdentity.ComputeAsync(contentProvider, stream, HashBufferBytes,
                            inspectionToken);
                if (stream.Length != length || File.GetLastWriteTimeUtc(exactPath) != lastWrite)
                    throw new IOException("Local file identity changed during inventory.");
                return new(new PilotLocalEntry(relative, PilotEntryKind.File, exactPath,
                    length, lastWrite, hash, identity.VolumeSerialNumber, fileId,
                    ownedCloud?.State.Availability, ownedCloud?.Identity.DropboxRevision,
                    ownedCloud?.Identity.DropboxId), null, length);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new(null, new PilotDecision(relative, PilotDecisionKind.Blocked,
                    "Local file could not be read: " + SyncActivityText.Failure(exception)), length,
                    exception is IOException && (exception.HResult & 0xffff) == 32);
            }
            finally { lease?.Dispose(); } // Inspection admission never counts as a completed transfer.
        }

        var pendingDirectories = new Stack<string>();
        string[]? explicitChildren = refreshPaths?.Select(path => Path.Combine(rootLease.Path,
            PilotPath.NormalizeRelative(path).Replace('/', Path.DirectorySeparatorChar))).ToArray();
        if (refreshPaths is null) pendingDirectories.Push(rootLease.Path);
        // A journal-bound refresh resolves each exact path from its own containing directory listing,
        // cached per parent, and admits a vanished entry as absent (never opening the entry itself).
        var wantedByParent = (explicitChildren ?? []).GroupBy(path => Path.GetDirectoryName(path) ?? rootLease.Path,
            StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
                group => group.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, DirectoryEntryInfo[]>? parentListings = null;
        DirectoryEntryInfo? ResolveFromParent(string exactPath)
        {
            var parent = Path.GetDirectoryName(exactPath) ?? rootLease.Path;
            // Bounded previews resolve one exact child without caching a potentially million-name parent.
            if (maxEntries is not null || !wantedByParent.ContainsKey(parent))
            {
                try
                {
                    if (!string.Equals(parent, rootLease.Path, StringComparison.OrdinalIgnoreCase))
                        ReadDirectoryIdentity(parent, rootLease);
                    return WindowsDirectoryListing.List(parent).FirstOrDefault(entry =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return string.Equals(entry.Name, Path.GetFileName(exactPath), StringComparison.OrdinalIgnoreCase);
                    });
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
            }
            parentListings ??= new(StringComparer.OrdinalIgnoreCase);
            if (!parentListings.TryGetValue(parent, out var listing))
            {
                try
                {
                    // Stream the directory once, retaining only the bounded refresh set, not every sibling.
                    var wanted = wantedByParent[parent];
                    parentListings[parent] = listing = WindowsDirectoryListing.List(parent).Where(entry =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return wanted.Contains(entry.Name);
                    }).ToArray();
                    ParentListingRetainedForTest?.Invoke(listing.Length);
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
            }
            return listing.FirstOrDefault(entry =>
                string.Equals(entry.Name, Path.GetFileName(exactPath), StringComparison.OrdinalIgnoreCase));
        }
        try
        {
        while (pendingDirectories.Count > 0 || explicitChildren is not null)
        {
            var currentDirectory = explicitChildren is null ? pendingDirectories.Pop() : rootLease.Path;
            await ReportProgressAsync(Path.GetRelativePath(rootLease.Path, currentDirectory), entriesFound);
            var requested = explicitChildren;
            ChildEntry[] children;
            try
            {
                rootLease.VerifyBinding();
                if (maxEntries is not null && requested is null &&
                    !string.Equals(currentDirectory, rootLease.Path, StringComparison.OrdinalIgnoreCase))
                    ReadDirectoryIdentity(currentDirectory, rootLease);
                explicitChildren = null;
                if (requested is not null)
                    children = requested.Select(path => new ChildEntry(path, null)).ToArray();
                else if (MetadataOnly)
                    children = WindowsDirectoryListing.List(currentDirectory)
                        .Take(maxEntries is { } metadataLimit ? metadataLimit - collected + 1 : int.MaxValue)
                        .Select(entry => new ChildEntry(Path.Combine(currentDirectory, entry.Name), entry))
                        .ToArray();
                else
                    children = (maxEntries is null
                            ? Directory.GetFileSystemEntries(currentDirectory)
                            : Directory.EnumerateFileSystemEntries(currentDirectory))
                        .Take(maxEntries is { } fileLimit ? fileLimit - collected + 1 : int.MaxValue)
                        .Select(path => new ChildEntry(path, null)).ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (string.Equals(currentDirectory, rootLease.Path, StringComparison.OrdinalIgnoreCase))
                    throw exception is Win32Exception
                        ? new IOException("The selected local root could not be enumerated.", exception)
                        : exception;
                var blocked = PilotPath.NormalizeRelative(Path.GetRelativePath(rootLease.Path, currentDirectory));
                decisions.Add(new PilotDecision(blocked, PilotDecisionKind.Blocked,
                    "A local folder could not be enumerated safely."));
                continue;
            }

            foreach (var child in children.OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                if (maxEntries is { } limit && collected >= limit)
                {
                    complete = false;
                    pendingDirectories.Clear();
                    break;
                }
                collected++;
                var fullPath = child.FullPath;
                cancellationToken.ThrowIfCancellationRequested();
                rootLease.VerifyBinding();
                var exactPath = Path.GetFullPath(fullPath);
                if (IsProductOwnedPath(exactPath, exactProductOwnedPaths)) continue;
                var relative = PilotPath.NormalizeRelative(Path.GetRelativePath(rootLease.Path, exactPath));
                var include = includePath?.Invoke(relative) ?? true;
                // Neither an observation nor a traversal target: prune before reading attributes or cloud state.
                if (!include && !(traverseDirectory?.Invoke(relative) ?? true)) continue;
                entriesFound++;
                await ReportProgressAsync(relative, entriesFound);
                FileAttributes attributes;
                DirectoryEntryInfo? metadata = child.Metadata;
                try
                {
                    if (metadata is null && MetadataOnly) metadata = ResolveFromParent(exactPath);
                    if (metadata is null && MetadataOnly) continue; // vanished: admitted as absent
                    attributes = metadata?.Attributes ?? File.GetAttributes(exactPath);
                }
                catch (Exception exception) when (refreshPaths is not null &&
                    exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Absence is admitted only by the journal-bound caller. Ordinary enumeration
                    // retains its uncertainty behavior if an item disappears during the scan.
                    continue;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await DrainAsync();
                    if (include || (traverseDirectory?.Invoke(relative) ?? true))
                        decisions.Add(new PilotDecision(relative, PilotDecisionKind.Blocked,
                            "Local path attributes could not be read safely."));
                    continue;
                }

                // Identity-only consumers never need file contents. Keep the same
                // verified directory/reparse traversal, but do not open/hash files.
                if (directoriesOnly && (attributes & FileAttributes.Directory) == 0) continue;

                if (CloudFilesAware && expectedPairId is null && PilotPath.IsUnsafeAttributes(attributes))
                {
                    DirectoryEntryInfo? listed;
                    try { listed = metadata ?? ResolveFromParent(exactPath); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                        or Win32Exception) { listed = null; }
                    // Astra fix 3: a known link tag wins over online-only bits (a file symlink can carry OFFLINE/RECALL).
                    var kind = listed is null ? ForeignObjectKind.None
                        : PilotPath.ClassifyForeign(listed.Attributes, listed.ReparseTag);
                    if (listed is not null && kind == ForeignObjectKind.Link)
                    {
                        // Decision 59: a junction, symlink or mount point is recorded as a link and never followed;
                        // Run excludes it and its path on both sides of the pair.
                        await DrainAsync();
                        if (include)
                            AddEntry(entries, canonicalPaths, blockedCanonicalPaths, decisions,
                                new PilotLocalEntry(relative,
                                    (listed.Attributes & FileAttributes.Directory) != 0
                                        ? PilotEntryKind.Directory : PilotEntryKind.File,
                                    exactPath, 0, listed.LastWriteTimeUtc, null,
                                    rootLease.Identity.VolumeSerialNumber, (ulong)listed.FileId, Link: true));
                        continue;
                    }
                    else if (listed is not null && kind == ForeignObjectKind.LocalCloud)
                    {
                        // Fully local placeholder: its bytes and children are ordinary local data.
                        attributes = listed.Attributes & ~FileAttributes.ReparsePoint;
                        metadata ??= MetadataOnly ? listed : null;
                    }
                    else if (listed is not null && kind == ForeignObjectKind.OnlineOnlyFile)
                    {
                        // With replacement off, candidates remain placeholders excluded on both sides.
                        // With it on, native state must prove a fully dehydrated, unmodified placeholder;
                        // all other cases exist but are unreadable, never delete authority.
                        await DrainAsync();
                        if (include)
                        {
                            var stub = PilotPath.IsOnlineOnlyStub(listed.Attributes, listed.ReparseTag) &&
                                (!ReplaceOnlineOnlyStubs ||
                                 WindowsCloudFilesProvider.IsFullyDehydratedStub(exactPath, (ulong)listed.FileId));
                            AddEntry(entries, canonicalPaths, blockedCanonicalPaths, decisions,
                                new PilotLocalEntry(relative, PilotEntryKind.File, exactPath, listed.Size,
                                    listed.LastWriteTimeUtc, null, rootLease.Identity.VolumeSerialNumber,
                                    (ulong)listed.FileId,
                                    stub ? SyncCloudFileAvailability.OnlineOnly : null,
                                    ContentUnreadable: !stub));
                        }
                        continue;
                    }
                }

                if (PilotPath.IsUnsafeAttributes(attributes))
                {
                    await DrainAsync();
                    if (expectedPairId is not null &&
                        WindowsCloudFilesProvider.TryReadOwnedPlaceholder(exactPath, out var cloud) &&
                        cloud is not null && cloud.Identity.ObjectKind != "root" &&
                        cloud.Identity.IsBoundTo(expectedPairId, contentProvider) &&
                        cloud.SyncRootFileId == rootLease.Identity.FileId &&
                        string.Equals(cloud.Identity.RelativePath, relative, StringComparison.OrdinalIgnoreCase))
                    {
                        var expectedDirectory = cloud.Identity.ObjectKind == "directory";
                        if (expectedDirectory != ((attributes & FileAttributes.Directory) != 0))
                        {
                            decisions.Add(new PilotDecision(relative, PilotDecisionKind.Blocked,
                                "The Cloud Files placeholder kind differs from its signed Sync Shark identity."));
                            continue;
                        }
                        // A placeholder's identity describes its last cloud version, not necessarily
                        // its current local bytes. Never suppress an edit just because its old hash
                        // is still present. Fully offline files can be hashed without recalling data.
                        var physicalMetadataChanged = !expectedDirectory &&
                            (new FileInfo(exactPath).Length != cloud.Identity.LogicalSize ||
                             cloud.Identity.ModifiedUtc is { } modified &&
                             File.GetLastWriteTimeUtc(exactPath) != modified.UtcDateTime);
                        if (!expectedDirectory && (!cloud.State.IsInSync || physicalMetadataChanged))
                        {
                            if (!include) continue;
                            if (cloud.State.Availability != SyncCloudFileAvailability.AvailableOffline)
                            {
                                decisions.Add(new PilotDecision(relative, PilotDecisionKind.Blocked,
                                    "The edited Cloud Files item is not fully available on this device; " +
                                    "its old cloud checksum was not accepted and scanning did not download it."));
                                continue;
                            }
                            var cloudReadLease = await AcquireAsync();
                            pendingResults.Enqueue((exactPath, relative,
                                ReadFileAsync(exactPath, relative, cloudReadLease), true));
                            if (pendingResults.Count >= maximumPending) await CommitNextAsync();
                            continue;
                        }
                        if (include || expectedDirectory)
                            AddEntry(entries, canonicalPaths, blockedCanonicalPaths, decisions,
                                new PilotLocalEntry(relative,
                                expectedDirectory ? PilotEntryKind.Directory : PilotEntryKind.File,
                                exactPath, cloud.Identity.LogicalSize,
                                cloud.Identity.ModifiedUtc?.UtcDateTime ??
                                (expectedDirectory
                                    ? Directory.GetLastWriteTimeUtc(exactPath)
                                    : File.GetLastWriteTimeUtc(exactPath)),
                                cloud.Identity.ContentHash, rootLease.Identity.VolumeSerialNumber,
                                cloud.FileId, cloud.State.Availability, cloud.Identity.DropboxRevision,
                                cloud.Identity.DropboxId));
                        if (expectedDirectory && (traverseDirectory?.Invoke(relative) ?? true))
                            pendingDirectories.Push(exactPath);
                        continue;
                    }
                    if (include || (attributes & FileAttributes.Directory) != 0 &&
                        (traverseDirectory?.Invoke(relative) ?? true))
                        decisions.Add(PilotPath.IsOnlineOnlyAttributes(attributes)
                            ? new PilotDecision(relative, PilotDecisionKind.OnlineOnly,
                                "An online-only placeholder owned by another provider was not hydrated or followed.")
                            : new PilotDecision(relative, PilotDecisionKind.Blocked,
                                "A reparse point was not followed; its subtree remains outside this pair."));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    PilotLocalEntry? directoryEntry = null;
                    PilotDecision? directoryDecision = null;
                    try
                    {
                        // Directory identity is bounded catalog metadata. Retain the directory itself even
                        // when its contents are excluded so the folder picker can expose it without hashing
                        // or treating any excluded file as an effect-eligible observation.
                        if (metadata is { } listedDirectory)
                        {
                            // MetadataOnly: identity comes from the parent listing; only the root opens.
                            directoryEntry = new PilotLocalEntry(relative, PilotEntryKind.Directory,
                                exactPath, 0, listedDirectory.LastWriteTimeUtc, null,
                                rootLease.Identity.VolumeSerialNumber, (ulong)listedDirectory.FileId);
                        }
                        else
                        {
                            var identity = ReadDirectoryIdentity(exactPath, rootLease);
                            directoryEntry = new PilotLocalEntry(relative, PilotEntryKind.Directory,
                                exactPath, 0, Directory.GetLastWriteTimeUtc(exactPath), null,
                                identity.VolumeSerialNumber, identity.FileId);
                        }
                        // Folder selection is editable after a pair is saved, so the directory-only catalog
                        // must remain complete even when the current effect policy excludes this subtree.
                        // Files beneath it are still skipped by includePath and are never opened or hashed.
                        if (traverseDirectory?.Invoke(relative) ?? true)
                            pendingDirectories.Push(exactPath);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        directoryDecision = new PilotDecision(relative, PilotDecisionKind.Blocked,
                            "A local folder moved, changed identity, or became inaccessible during inventory.");
                    }
                    pendingResults.Enqueue((exactPath, relative,
                        Task.FromResult(new FileReadResult(directoryEntry, directoryDecision, 0)), false));
                    // Await outside the metadata catch: a failed earlier file must
                    // never be relabelled as a directory-identity failure.
                    if (pendingResults.Count >= maximumPending) await CommitNextAsync();
                    continue;
                }

                if (!include) continue;

                if (metadata is { } listedFile)
                {
                    // MetadataOnly: synthesize the same entry from the parent listing, never opening it.
                    var listedEntry = new PilotLocalEntry(relative, PilotEntryKind.File, exactPath,
                        listedFile.Size, listedFile.LastWriteTimeUtc, null,
                        rootLease.Identity.VolumeSerialNumber, (ulong)listedFile.FileId);
                    pendingResults.Enqueue((exactPath, relative,
                        Task.FromResult(new FileReadResult(listedEntry, null, listedFile.Size)), true));
                    if (pendingResults.Count >= maximumPending) await CommitNextAsync();
                    continue;
                }

                // Concurrency affects read-only admission, not freshness or effect authority.
                // Every worker opens the same exclusive verified handle and hashes held bytes.
                var readLease = await AcquireAsync();
                var read = InventoryScheduling is null
                    ? ReadFileAsync(exactPath, relative, readLease)
                    : Task.Run(() => ReadFileAsync(exactPath, relative, readLease));
                pendingResults.Enqueue((exactPath, relative, read, true));
                if (pendingResults.Count >= maximumPending) await CommitNextAsync();
            }
        }

        await DrainAsync();
        try { if (onInventory is not null) await onInventory(lastProgressPath, entriesFound); }
        catch (Exception) { /* Reporting does not change the completed scan. */ }
        rootLease.VerifyBinding();
        stopwatch.Stop();
        return new PilotLocalScan(entries, decisions, totalBytes, filesHashed,
            Elapsed: stopwatch.Elapsed, PathsInspected: entriesFound, Incremental: refreshPaths is not null,
            Complete: complete);
        }
        finally
        {
            // The caller may release the root only AFTER every reader and its resource
            // lease have closed, including cancellation, callback failure and root drift.
            inspectionCancellation.Cancel();
            try { await Task.WhenAll(pendingResults.Select(item => item.Read)).ConfigureAwait(false); }
            catch { /* Preserve the original failure; no incomplete scan is returned. */ }
        }
    }

    internal static PilotLocalScan MergeIncremental(PilotLocalScan retained, PilotLocalScan refreshed,
        IReadOnlyList<string> paths)
    {
        return MergeIncremental(retained, refreshed, paths, CancellationToken.None, out _);
    }

    // The supplied changed-path scope is matched against ONE case-insensitive set of the exact strings the
    // caller supplied, and each retained entry probes only itself and its slash-terminated ancestors. That
    // reproduces the SyncDirectoryStoragePolicy.Contains decision the previous per-entry scan of the whole
    // supplied list produced, with O(path depth) probes instead of O(supplied paths) per retained entry. No
    // supplied or retained path is normalized, trimmed or re-cased here. The retained-then-refreshed
    // publication order, AddEntry collision handling, refreshed decisions and total-byte projection are
    // unchanged, and a cancelled token aborts the merge instead of returning a partial inventory.
    internal static PilotLocalScan MergeIncremental(PilotLocalScan retained, PilotLocalScan refreshed,
        IReadOnlyList<string> paths, CancellationToken cancellationToken, out long scopeLookups)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, PilotLocalEntry>(StringComparer.OrdinalIgnoreCase);
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<PilotDecision>(refreshed.Decisions);
        var lookups = 0L;
        var processed = 0;
        foreach (var entry in retained.Entries.Values)
        {
            if ((++processed & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!RefreshedByScope(scope, entry.RelativePath, out var probes)) AddEntry(entries, canonical, blocked, decisions, entry);
            lookups += probes;
        }
        foreach (var entry in refreshed.Entries.Values)
        {
            if ((++processed & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            AddEntry(entries, canonical, blocked, decisions, entry);
        }
        scopeLookups = lookups;
        return refreshed with { Entries = entries, Decisions = decisions,
            TotalBytes = entries.Values.Where(entry => entry.Kind == PilotEntryKind.File).Sum(entry => entry.Size) };
    }

    // True when the exact supplied scope refreshes this retained relative path. The probes are the path
    // itself and then each slash-terminated ancestor, which is exactly the set of folder strings for which
    // SyncDirectoryStoragePolicy.Contains(folder, path) is true. An empty ancestor is reached only for an
    // empty path or a path that begins with '/', matching Contains("", path) exactly.
    private static bool RefreshedByScope(IReadOnlySet<string> scope, string relativePath, out int probes)
    {
        probes = 0;
        if (scope.Count == 0) return false;
        var candidate = relativePath;
        while (true)
        {
            probes++;
            if (scope.Contains(candidate)) return true;
            var separator = candidate.LastIndexOf('/');
            if (separator < 0) return false;
            candidate = candidate[..separator];
        }
    }

    public static async Task<FileStream> OpenValidatedForUploadAsync(PilotLocalEntry expected,
        PilotDirectoryLease rootLease, CancellationToken cancellationToken,
        string? contentProvider = null, string? expectedPairId = null)
    {
        rootLease.VerifyBinding();
        var stream = OpenExclusiveVerified(expected.FullPath, rootLease, expectedPairId, contentProvider);
        try
        {
            if (!PilotDirectoryLease.GetFileInformationByHandle(stream.SafeFileHandle, out var information) ||
                expected.VolumeSerialNumber is { } expectedVolume && information.VolumeSerialNumber != expectedVolume ||
                expected.FileId is { } expectedFileId &&
                    (((ulong)information.FileIndexHigh << 32) | information.FileIndexLow) != expectedFileId)
                throw new IOException("The upload source is no longer the exact inventoried local file identity.");
            if (stream.Length != expected.Size || File.GetLastWriteTimeUtc(expected.FullPath) != expected.LastWriteUtc)
                throw new IOException("Local file changed after inventory.");
            var hash = await SyncContentIdentity.ComputeAsync(contentProvider, stream, cancellationToken);
            if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Local file bytes changed after inventory.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public static async Task VerifyHeldSourceAfterUploadAsync(FileStream stream, PilotLocalEntry expected, PilotDirectoryLease rootLease, CancellationToken cancellationToken, string? contentProvider = null)
    {
        rootLease.VerifyBinding();
        if (stream.Length != expected.Size) throw new IOException("The held source length changed during upload.");
        stream.Position = 0;
        var hash = await SyncContentIdentity.ComputeAsync(contentProvider, stream, cancellationToken);
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The held source bytes changed during upload.");
        VerifyHandlePath(stream.SafeFileHandle, expected.FullPath, rootLease);
        rootLease.VerifyBinding();
    }

    private static void AddEntry(IDictionary<string, PilotLocalEntry> entries,
        IDictionary<string, string> canonicalPaths, ISet<string> blockedCanonicalPaths,
        ICollection<PilotDecision> decisions, PilotLocalEntry entry)
    {
        var key = PilotPath.CanonicalComparisonKey(entry.RelativePath);
        if (blockedCanonicalPaths.Contains(key))
        {
            decisions.Add(new PilotDecision(entry.RelativePath, PilotDecisionKind.Blocked,
                "A case- or Unicode-normalization-colliding local name is not accepted."));
            return;
        }
        if (canonicalPaths.TryGetValue(key, out var existingPath) &&
            !string.Equals(existingPath, entry.RelativePath, StringComparison.Ordinal))
        {
            entries.Remove(existingPath);
            decisions.Add(new PilotDecision(existingPath, PilotDecisionKind.Blocked,
                "A case- or Unicode-normalization-colliding local name is not accepted."));
            decisions.Add(new PilotDecision(entry.RelativePath, PilotDecisionKind.Blocked,
                "A case- or Unicode-normalization-colliding local name is not accepted."));
            blockedCanonicalPaths.Add(key);
            return;
        }
        canonicalPaths[key] = entry.RelativePath;
        entries[entry.RelativePath] = entry;
    }

    private static FileStream OpenExclusiveVerified(string fullPath, PilotDirectoryLease rootLease,
        string? expectedPairId = null, string? contentProvider = null)
        => OpenExclusiveVerified(fullPath, rootLease, out _, expectedPairId, contentProvider);

    private static FileStream OpenExclusiveVerified(string fullPath, PilotDirectoryLease rootLease,
        out WindowsCloudPlaceholderInfo? ownedCloud, string? expectedPairId = null, string? contentProvider = null)
    {
        ownedCloud = null;
        if (PilotPath.IsUnsafeAttributes(File.GetAttributes(fullPath)))
            return expectedPairId is null && !PilotPath.IsUnsafePath(fullPath)
                ? OpenLocalCloudFile(fullPath, rootLease)
                : OpenOwnedOfflinePlaceholder(fullPath, rootLease, expectedPairId, contentProvider, out ownedCloud);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None, ReadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (PilotPath.IsUnsafePath(fullPath)) throw new IOException("The local file became a reparse point or placeholder.");
            VerifyHandlePath(stream.SafeFileHandle, fullPath, rootLease);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // Another provider's fully local cloud-files placeholder . The reparse object itself is held
    // with no-recall and no sharing, and the held handle must still report no OFFLINE / RECALL_* attribute, so a
    // dehydration in between makes the read fail instead of fetching anything.
    private static FileStream OpenLocalCloudFile(string fullPath, PilotDirectoryLease rootLease)
    {
        rootLease.VerifyBinding();
        const uint genericRead = 0x80000000;
        const uint noRecall = 0x00100000;
        const uint overlapped = 0x40000000;
        const uint sequential = 0x08000000;
        var handle = PilotDirectoryLease.CreateFile(Path.GetFullPath(fullPath), genericRead, FileShare.None,
            IntPtr.Zero, PilotDirectoryLease.OpenExisting,
            PilotDirectoryLease.FileFlagOpenReparsePoint | noRecall | overlapped | sequential, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid)
                throw new IOException("The local cloud-files file could not be held safely.",
                    unchecked((int)(0x80070000u | (uint)Marshal.GetLastWin32Error())));
            VerifyHandlePath(handle, fullPath, rootLease);
            var (attributes, tag) = PilotPath.AttributeTag(handle);
            if ((attributes & FileAttributes.Directory) != 0 || PilotPath.IsOnlineOnlyAttributes(attributes) ||
                PilotPath.IsUnsafeObject(attributes, tag))
                throw new IOException("The local cloud-files file is not fully on this device; it was not read.");
            rootLease.VerifyBinding();
            return new FileStream(handle, FileAccess.Read, ReadBufferSize, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static FileStream OpenOwnedOfflinePlaceholder(string fullPath, PilotDirectoryLease rootLease,
        string? expectedPairId, string? contentProvider, out WindowsCloudPlaceholderInfo? ownedCloud)
    {
        ownedCloud = null;
        rootLease.VerifyBinding();
        if (expectedPairId is null)
            throw new IOException("A Cloud Files byte source requires the selected pair identity.");
        // Open the reparse object itself with no-recall and no sharing, then inspect that held object
        // before reading anything. A path swap, foreign link or offline range cannot cause a fetch.
        const uint genericRead = 0x80000000;
        const uint noRecall = 0x00100000;
        const uint overlapped = 0x40000000;
        const uint sequential = 0x08000000;
        var handle = PilotDirectoryLease.CreateFile(Path.GetFullPath(fullPath), genericRead,
            FileShare.None, IntPtr.Zero, PilotDirectoryLease.OpenExisting,
            PilotDirectoryLease.FileFlagOpenReparsePoint | noRecall | overlapped | sequential, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException("The offline Cloud Files source could not be held safely.",
                    unchecked((int)(0x80070000u | (uint)error)));
            }
            VerifyHandlePath(handle, fullPath, rootLease);
            if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var information) ||
                (information.FileAttributes & FileAttributes.Directory) != 0 ||
                !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(handle, directory: false, out var cloud) ||
                cloud is null || cloud.IsDirectory || cloud.Identity.ObjectKind != "file" ||
                !cloud.Identity.IsBoundTo(expectedPairId, contentProvider) ||
                !string.Equals(cloud.Identity.RelativePath,
                    PilotPath.NormalizeRelative(Path.GetRelativePath(rootLease.Path, fullPath)),
                    StringComparison.OrdinalIgnoreCase) ||
                cloud.SyncRootFileId != rootLease.Identity.FileId ||
                cloud.FileId != (((ulong)information.FileIndexHigh << 32) | information.FileIndexLow) ||
                cloud.State.Availability != SyncCloudFileAvailability.AvailableOffline)
                throw new IOException("Only the selected pair's fully offline Cloud Files object can be read without downloading.");
            rootLease.VerifyBinding();
            // Capture CFAPI metadata on this exact exclusive handle BEFORE FileStream binds
            // it to the managed I/O completion port. CFAPI's own native completions are not
            // managed OVERLAPPED records and must never reach that port. Keep asynchronous
            // byte reads, the same native object, no-recall and every pair/root/residency check.
            ownedCloud = cloud;
            return new FileStream(handle, FileAccess.Read, ReadBufferSize, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void VerifyHandlePath(SafeFileHandle handle, string expectedPath, PilotDirectoryLease rootLease)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var observed = PilotDirectoryLease.FinalPath(handle);
        if (!string.Equals(observed, Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase) ||
            !PilotPath.IsPathInside(rootLease.Path, observed))
            throw new IOException("The opened source is not a verified descendant of the selected local folder identity.");
    }

    private static PilotDirectoryIdentity ReadDirectoryIdentity(string fullPath, PilotDirectoryLease rootLease)
    {
        using var handle = PilotDirectoryLease.CreateFile(fullPath, PilotDirectoryLease.FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, PilotDirectoryLease.OpenExisting,
            PilotDirectoryLease.FileFlagBackupSemantics | PilotDirectoryLease.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid || !PilotDirectoryLease.GetFileInformationByHandle(handle, out var information) ||
            (information.FileAttributes & FileAttributes.Directory) == 0 ||
            PilotPath.IsUnsafeAttributes(information.FileAttributes) &&
            PilotPath.IsUnsafeObject(PilotPath.AttributeTag(handle).Attributes, PilotPath.AttributeTag(handle).ReparseTag) ||
            !string.Equals(PilotDirectoryLease.FinalPath(handle), Path.GetFullPath(fullPath),
                StringComparison.OrdinalIgnoreCase) || !PilotPath.IsPathInside(rootLease.Path, fullPath))
            throw new IOException("The local folder identity could not be verified.");
        rootLease.VerifyBinding();
        return new PilotDirectoryIdentity(information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static bool IsProductOwnedPath(string fullPath, IReadOnlySet<string>? exactProductOwnedPaths)
    {
        if (exactProductOwnedPaths is null) return false;
        foreach (var owned in exactProductOwnedPaths)
        {
            var exactOwned = Path.GetFullPath(owned).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, exactOwned, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(exactOwned + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
