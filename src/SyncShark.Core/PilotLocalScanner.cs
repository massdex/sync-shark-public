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
    bool Complete = true)
{
    // Evidence for this scan only; ordinary deletion still honors explicit entity holds.
    internal IReadOnlySet<string>? DefenderHistoryClearFolders { get; init; }
}

public sealed record PilotLocalFileFingerprint(
    string RelativePath,
    uint VolumeSerialNumber,
    ulong FileId,
    long Size,
    DateTime LastWriteUtc);

public sealed class PilotLocalScanner
{
    internal V2.LocalWalkCheckpoint? WalkCheckpoint { get; init; }
    // A bounded page from a directory handle whose identity the caller verified. Reuses all
    // scanner classification without relisting a million-name parent for every 256 children.
    internal IReadOnlyDictionary<string, DirectoryEntryInfo>? ListedMetadata { get; init; }
    internal Action? BetweenDirectories { get; init; }
    internal Action<string>? DirectoryCompletedForTest { get; init; }
    internal static Action<int>? ParentListingRetainedForTest { get; set; }
    // Observes an owned-placeholder probe attempt (the extra native attribute/CfGetPlaceholderInfo open).
    // The caller passes expectedPairId only when storage is opted in, so this stays silent otherwise.
    internal Action<string>? OwnedPlaceholderProbeForTest { get; init; }
    internal Func<string, WindowsCloudPlaceholderInfo?>? ReadOwnedPlaceholderForTest { get; init; }
    // V2 supplies durable entity/root ownership. No database or path-based cache lives in the scanner.
    internal Func<string, WindowsCloudPlaceholderInfo, long, V2.OwnedPointerObservation>? ClassifyOwnedPointer { get; init; }
    internal Func<ulong, string?>? KnownOwnedPointerPath { get; init; }

    private bool TryReadOwnedPlaceholder(string path, out WindowsCloudPlaceholderInfo? info)
    {
        if (ReadOwnedPlaceholderForTest is not { } read)
            return WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out info);
        info = read(path);
        return info is not null;
    }
    // FileStream buffering is an I/O choice, not the provider's four-MiB hash-block boundary.
    // Hashing reads larger chunks and bypasses this small buffer instead of allocating four MiB
    // for every tiny file. The held handle, exclusive sharing and fresh byte digest are unchanged.
    private const int ReadBufferSize = 64 * 1024;
    // One owner for the refresh-spelling trace text. The relative path format matches the scan's paths.
    private const string RefreshCaseResolvedTrace = "refresh path case resolved: ";
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
        if (WalkCheckpoint is not null && (!MetadataOnly || refreshPaths is not null || maxEntries is not null))
            throw new InvalidOperationException("Only an unbounded metadata full walk can be checkpointed.");
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
        var entries = new Dictionary<string, PilotLocalEntry>(StringComparer.Ordinal);
        var blockedCanonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<PilotDecision>();
        long totalBytes = 0;
        var filesHashed = 0;
        var entriesFound = 0;
        // Only one directory's raw observations are retained in addition to the existing scan state.
        List<PilotLocalEntry>? pageEntries = WalkCheckpoint is null ? null : [];
        List<(string Path, PilotDirectoryIdentity Identity)>? pageDirectories = WalkCheckpoint is null ? null : [];
        List<V2.LocalWalkSelection>? pageSelections = WalkCheckpoint is null ? null : [];
        void RecordEntry(PilotLocalEntry entry)
        {
            pageEntries?.Add(entry);
            LocalScanEntries.AddEntry(entries, blockedCanonicalPaths, decisions, entry);
        }
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
                RecordEntry(entry);
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
                    "Local file could not be read: " + SyncActivityText.Failure(exception))
                    { QuarantineCode = V2.QuarantineFailure.NativeCode(exception), QuarantineFile = true }, length,
                    exception is IOException && (exception.HResult & 0xffff) == 32);
            }
            finally { lease?.Dispose(); } // Inspection admission never counts as a completed transfer.
        }

        var pendingDirectories = new Stack<(string Path, PilotDirectoryIdentity? Identity)>();
        void PushDirectory(string path, PilotDirectoryIdentity identity)
        {
            pendingDirectories.Push((path, identity));
            pageDirectories?.Add((path, identity));
        }
        // A refresh request carries the spelling the store or the journal resolved the path to, and a
        // case-only rename can leave that spelling behind the directory. Resolve every component against
        // the parent listings before the explicit children and traversal prefixes are built, so a stale
        // requested spelling and the enumerated on-disk name describe one entry instead of colliding.
        // An absent component, or more than one case-insensitive match in a case-sensitive directory,
        // keeps the requested spelling: absence and genuine collisions are never silently re-spelled.
        var requestedRelatives = refreshPaths?.Select(PilotPath.NormalizeRelative).ToArray();
        // One canonical key can only be reached twice when the request carries several paths, because a
        // single path is enumerated once and a folder's own children never repeat its key. A lone scoped
        // metadata read (content classification) therefore keeps its one parent listing, while a per-subtree
        // retry or journal refresh resolves every component. A bounded preview keeps its lazy single-parent
        // read too, so neither hot path gains a whole-tree ancestor walk.
        var resolveOnDisk = MetadataOnly && maxEntries is null && requestedRelatives is { Length: > 1 };
        var wantedByParent = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void WantParent(string parent, string name)
        {
            if (!wantedByParent.TryGetValue(parent, out var wanted))
                wantedByParent[parent] = wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            wanted.Add(name);
        }
        if (requestedRelatives is not null)
            foreach (var relative in requestedRelatives)
            {
                // Resolution needs every component's parent retained; the ordinary refresh needs only
                // each exact requested path's own parent and leaf, exactly as before.
                if (!resolveOnDisk)
                {
                    var exact = Path.Combine(rootLease.Path, relative.Replace('/', Path.DirectorySeparatorChar));
                    WantParent(Path.GetDirectoryName(exact) ?? rootLease.Path, Path.GetFileName(exact));
                    continue;
                }
                var prefix = string.Empty;
                foreach (var component in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    WantParent(prefix.Length == 0 ? rootLease.Path
                        : Path.Combine(rootLease.Path, prefix.Replace('/', Path.DirectorySeparatorChar)), component);
                    prefix = prefix.Length == 0 ? component : prefix + "/" + component;
                }
            }
        // A journal-bound refresh resolves each exact path from its own containing directory listing,
        // cached per parent, and admits a vanished entry as absent (never opening the entry itself).
        Dictionary<string, DirectoryEntryInfo[]>? parentListings = null;
        // One owner for a refresh parent listing: the bounded set retained for a requested parent, or a
        // direct read for a bounded preview or an unrequested ancestor. A vanished parent lists nothing.
        IEnumerable<DirectoryEntryInfo> ListParent(string parent)
        {
            if (maxEntries is null && wantedByParent.TryGetValue(parent, out var wanted))
            {
                parentListings ??= new(StringComparer.OrdinalIgnoreCase);
                if (parentListings.TryGetValue(parent, out var cached)) return cached;
                DirectoryEntryInfo[] listing;
                try
                {
                    // Stream the directory once, retaining only the bounded refresh set, not every sibling.
                    listing = WindowsDirectoryListing.List(parent).Where(entry =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return wanted.Contains(entry.Name);
                    }).ToArray();
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return []; }
                parentListings[parent] = listing;
                ParentListingRetainedForTest?.Invoke(listing.Length);
                return listing;
            }
            try
            {
                if (!string.Equals(parent, rootLease.Path, StringComparison.OrdinalIgnoreCase))
                    ReadDirectoryIdentity(parent, rootLease);
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return []; }
            return WindowsDirectoryListing.List(parent);
        }
        // The on-disk spelling of every requested component, or the requested path when a component is
        // absent or ambiguous. The cache is keyed case-insensitively, so a descendant resolves through
        // the same listing its already-resolved ancestor produced.
        string ResolveOnDiskSpelling(string relative)
        {
            var resolved = new List<string>();
            foreach (var component in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var parent = resolved.Count == 0 ? rootLease.Path
                    : Path.Combine(rootLease.Path, string.Join('/', resolved).Replace('/', Path.DirectorySeparatorChar));
                DirectoryEntryInfo? match = null;
                try
                {
                    foreach (var entry in ListParent(parent))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!string.Equals(entry.Name, component, StringComparison.OrdinalIgnoreCase)) continue;
                        // Two ordinal names in one case-sensitive directory: never silently choose one.
                        if (match is not null) return relative;
                        match = entry;
                    }
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return relative; }
                if (match is null) return relative;
                resolved.Add(match.Name);
            }
            return string.Join('/', resolved);
        }
        string[]? explicitChildren = null;
        if (requestedRelatives is not null)
        {
            explicitChildren = new string[requestedRelatives.Length];
            for (var index = 0; index < requestedRelatives.Length; index++)
            {
                var requested = requestedRelatives[index];
                var onDisk = resolveOnDisk ? ResolveOnDiskSpelling(requested) : requested;
                if (!string.Equals(onDisk, requested, StringComparison.Ordinal))
                    V2.RunTrace.Emit(RefreshCaseResolvedTrace + requested + " -> " + onDisk);
                explicitChildren[index] = Path.Combine(rootLease.Path,
                    onDisk.Replace('/', Path.DirectorySeparatorChar));
            }
        }
        if (refreshPaths is null) pendingDirectories.Push((rootLease.Path, rootLease.Identity));
        DirectoryEntryInfo? ResolveFromParent(string exactPath)
        {
            if (ListedMetadata is not null) return ListedMetadata.GetValueOrDefault(exactPath);
            var parent = Path.GetDirectoryName(exactPath) ?? rootLease.Path;
            try
            {
                return ListParent(parent).FirstOrDefault(entry =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return string.Equals(entry.Name, Path.GetFileName(exactPath), StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
        }
        try
        {
        while (pendingDirectories.Count > 0 || explicitChildren is not null)
        {
            BetweenDirectories?.Invoke(); // The finally block flushes completed pages even on pause.
            cancellationToken.ThrowIfCancellationRequested();
            var queuedDirectory = explicitChildren is null ? pendingDirectories.Pop() : (Path: rootLease.Path, Identity: (PilotDirectoryIdentity?)null);
            var currentDirectory = queuedDirectory.Path;
            if (WalkCheckpoint?.Read() is { } saved)
            {
                if (!string.Equals(saved.Directory, currentDirectory, StringComparison.Ordinal) ||
                    saved.DirectoryIdentity is not null && queuedDirectory.Identity is not null &&
                    saved.DirectoryIdentity != queuedDirectory.Identity)
                    throw new V2.LocalWalkReplayInvalidException("Local walk frontier does not match its checkpoint.");
                // A changed opaque inclusion predicate must not turn old pruned coverage into absence.
                foreach (var selection in saved.Selections)
                    if (selection.Include != (includePath?.Invoke(selection.Path) ?? true) ||
                        selection.Traverse != (traverseDirectory?.Invoke(selection.Path) ?? true))
                        throw new V2.LocalWalkPolicyChangedException();
                var unusual = saved.Selections.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
                foreach (var path in saved.Entries.Select(e => e.RelativePath)
                    .Concat(saved.Decisions.Select(d => d.RelativePath))
                    .Concat(saved.AddedDirectories.Select(d => PilotPath.NormalizeRelative(Path.GetRelativePath(rootLease.Path, d)))))
                    if (!unusual.Contains(path) && (!(includePath?.Invoke(path) ?? true) ||
                        !(traverseDirectory?.Invoke(path) ?? true)))
                        throw new V2.LocalWalkPolicyChangedException();
                // Blocked paths retain their incomplete coverage (no deletion authority).
                // Retry them on the next full walk, without discarding unrelated saved progress.
                var replayDecisionStart = decisions.Count;
                foreach (var entry in saved.Entries)
                    LocalScanEntries.AddEntry(entries, blockedCanonicalPaths, decisions, entry);
                // AddEntry reconstructs collision state, but the page already contains its exact decisions.
                decisions.RemoveRange(replayDecisionStart, decisions.Count - replayDecisionStart);
                decisions.AddRange(saved.Decisions);
                for (var i = 0; i < saved.AddedDirectories.Length; i++)
                    pendingDirectories.Push((saved.AddedDirectories[i], saved.AddedDirectoryIdentities[i]));
                totalBytes += saved.Bytes;
                entriesFound += saved.Found;
                await ReportProgressAsync(Path.GetRelativePath(rootLease.Path, currentDirectory), entriesFound);
                continue;
            }
            pageEntries?.Clear();
            pageDirectories?.Clear();
            pageSelections?.Clear();
            var decisionStart = decisions.Count;
            var bytesStart = totalBytes;
            var foundStart = entriesFound;
            void SaveDirectory()
            {
                WalkCheckpoint?.Save(new V2.LocalWalkPage(currentDirectory, pageDirectories!.Select(d => d.Path).ToArray(),
                    pageEntries!.ToArray(), decisions.GetRange(decisionStart, decisions.Count - decisionStart).ToArray(), pageSelections!.ToArray(),
                    totalBytes - bytesStart, entriesFound - foundStart)
                    { DirectoryIdentity = queuedDirectory.Identity,
                        AddedDirectoryIdentities = pageDirectories!.Select(d => (PilotDirectoryIdentity?)d.Identity).ToArray() });
                DirectoryCompletedForTest?.Invoke(currentDirectory);
            }
            await ReportProgressAsync(Path.GetRelativePath(rootLease.Path, currentDirectory), entriesFound);
            var requested = explicitChildren;
            ChildEntry[] children;
            try
            {
                rootLease.VerifyBinding();
                if ((maxEntries is not null || WalkCheckpoint is not null) && requested is null &&
                    !string.Equals(currentDirectory, rootLease.Path, StringComparison.OrdinalIgnoreCase))
                    ReadDirectoryIdentity(currentDirectory, rootLease);
                explicitChildren = null;
                if (requested is not null)
                    children = requested.Select(path => new ChildEntry(path, null)).ToArray();
                else if (MetadataOnly || queuedDirectory.Identity is not null)
                    children = WindowsDirectoryListing.List(currentDirectory, queuedDirectory.Identity)
                        .Take(maxEntries is { } metadataLimit ? metadataLimit - collected + 1 : int.MaxValue)
                        .Select(entry => new ChildEntry(Path.Combine(currentDirectory, entry.Name), MetadataOnly ? entry : null))
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
                decisions.Add(exception is WindowsDirectoryListing.DirectoryIdentityMismatchException
                    ? new PilotDecision(blocked, PilotDecisionKind.Blocked, "replaced during scan")
                    : new PilotDecision(blocked, PilotDecisionKind.Blocked,
                        "A local folder could not be enumerated safely.") { QuarantineCode = V2.QuarantineFailure.NativeCode(exception) });
                SaveDirectory();
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
                pageSelections?.Add(new V2.LocalWalkSelection(relative, include, traverseDirectory?.Invoke(relative) ?? true));
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
                        decisions.Add(PilotDecision.MetadataFailure(relative, exception));
                    continue;
                }

                // Identity-only consumers never need file contents. Keep the same
                // verified directory/reparse traversal, but do not open/hash files.
                if (directoriesOnly && (attributes & FileAttributes.Directory) == 0) continue;

                WindowsCloudPlaceholderInfo? ownedPlaceholder = null;
                var probeOwned = expectedPairId is not null && PilotPath.IsUnsafeAttributes(attributes);
                if (probeOwned) OwnedPlaceholderProbeForTest?.Invoke(relative);
                // The repeated null test lets flow analysis see a non-null pair id across the chain.
                var nativeObserved = !probeOwned || TryReadOwnedPlaceholder(exactPath, out ownedPlaceholder);
                if (probeOwned && !nativeObserved && KnownOwnedPointerPath is not null)
                {
                    try { metadata ??= ResolveFromParent(exactPath); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
                    if (metadata is null)
                    {
                        decisions.Add(new PilotDecision(relative, PilotDecisionKind.Blocked, V2.OwnedPointerProof.ObservationMessage));
                        continue;
                    }
                }
                if (probeOwned && !nativeObserved && metadata is { } unobserved &&
                    KnownOwnedPointerPath?.Invoke((ulong)unobserved.FileId) is { } previousPath)
                {
                    // An exclusive settlement/hydration handle is not evidence of a local edit.
                    // Hold both names when a rename races the probe; publish no replacement row.
                    decisions.Add(new PilotDecision(relative, PilotDecisionKind.Blocked, V2.OwnedPointerProof.ObservationMessage));
                    if (!string.Equals(previousPath, relative, StringComparison.OrdinalIgnoreCase))
                        decisions.Add(new PilotDecision(previousPath, PilotDecisionKind.Blocked, V2.OwnedPointerProof.ObservationMessage));
                    continue;
                }
                var owned = probeOwned && expectedPairId is not null && nativeObserved &&
                    ownedPlaceholder is not null && ownedPlaceholder.Identity.ObjectKind != "root" &&
                    ownedPlaceholder.Identity.IsBoundTo(expectedPairId, contentProvider) &&
                    ownedPlaceholder.SyncRootFileId == rootLease.Identity.FileId && ownedPlaceholder.FileId != 0;
                // Ownership belongs to the item, not its serialized old path. A local rename or
                // enclosing folder move keeps its native file/root identity and exact cloud revision.
                // Demoting such an item to a foreign stub would silently prevent move propagation.
                if (CloudFilesAware && !owned && PilotPath.IsUnsafeAttributes(attributes))
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
                            RecordEntry(
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
                            RecordEntry(
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
                    if (owned && ownedPlaceholder is { } cloud)
                    {
                        var expectedDirectory = cloud.Identity.ObjectKind == "directory";
                        if (expectedDirectory != ((attributes & FileAttributes.Directory) != 0))
                        {
                            decisions.Add(new PilotDecision(relative, PilotDecisionKind.Blocked,
                                "The Cloud Files placeholder kind differs from its signed Sync Shark identity."));
                            continue;
                        }
                        var physicalSize = expectedDirectory ? 0 : new FileInfo(exactPath).Length;
                        var proof = !expectedDirectory
                            ? ClassifyOwnedPointer?.Invoke(relative, cloud, physicalSize) : null;
                        if (proof == V2.OwnedPointerObservation.NotObserved)
                        {
                            decisions.Add(new PilotDecision(relative, PilotDecisionKind.Blocked, V2.OwnedPointerProof.ObservationMessage));
                            if (KnownOwnedPointerPath?.Invoke(cloud.FileId) is { } oldPath &&
                                !string.Equals(oldPath, relative, StringComparison.OrdinalIgnoreCase))
                                decisions.Add(new PilotDecision(oldPath, PilotDecisionKind.Blocked, V2.OwnedPointerProof.ObservationMessage));
                            continue;
                        }
                        if (proof is V2.OwnedPointerObservation.Foreign or V2.OwnedPointerObservation.NeedsVerification)
                        {
                            // An explicit observation preserves the entity and its new path. A blocked
                            // decision alone would omit a renamed pointer and could lose its old-name hold.
                            if (include)
                                RecordEntry(new PilotLocalEntry(relative, PilotEntryKind.File, exactPath,
                                    physicalSize, File.GetLastWriteTimeUtc(exactPath), null,
                                    rootLease.Identity.VolumeSerialNumber, cloud.FileId,
                                    cloud.State.Availability, ContentUnreadable: true,
                                    NeedsRehash: proof == V2.OwnedPointerObservation.NeedsVerification));
                            continue;
                        }
                        // Resident bytes are always ordinary hashable data, never an old receipt's hash.
                        if (!expectedDirectory && cloud.State.Availability == SyncCloudFileAvailability.AvailableOffline)
                        {
                            if (!include) continue;
                            if (MetadataOnly)
                                RecordEntry(new PilotLocalEntry(relative, PilotEntryKind.File, exactPath,
                                    physicalSize, File.GetLastWriteTimeUtc(exactPath), null,
                                    rootLease.Identity.VolumeSerialNumber, cloud.FileId));
                            else
                            {
                                var residentLease = await AcquireAsync();
                                pendingResults.Enqueue((exactPath, relative,
                                    ReadFileAsync(exactPath, relative, residentLease), true));
                                if (pendingResults.Count >= maximumPending) await CommitNextAsync();
                            }
                            continue;
                        }
                        // A placeholder's identity describes its last cloud version, not necessarily
                        // its current local bytes. Only durable proof can admit a dirty renamed pointer.
                        var physicalMetadataChanged = !expectedDirectory &&
                            (physicalSize != cloud.Identity.LogicalSize ||
                             cloud.Identity.ModifiedUtc is { } modified &&
                             File.GetLastWriteTimeUtc(exactPath) != modified.UtcDateTime);
                        if (!expectedDirectory && proof != V2.OwnedPointerObservation.Pointer &&
                            (!cloud.State.IsInSync || physicalMetadataChanged ||
                             cloud.State.Availability == SyncCloudFileAvailability.PartiallyHydrated))
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
                            RecordEntry(
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
                            PushDirectory(exactPath, new(rootLease.Identity.VolumeSerialNumber, cloud.FileId));
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
                            PushDirectory(exactPath, new(directoryEntry.VolumeSerialNumber!.Value, directoryEntry.FileId!.Value));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        directoryDecision = new PilotDecision(relative, PilotDecisionKind.Blocked,
                            "A local folder moved, changed identity, or became inaccessible during inventory.")
                            { QuarantineCode = V2.QuarantineFailure.NativeCode(exception) };
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
            if (WalkCheckpoint is not null) await DrainAsync();
            SaveDirectory();
        }

        WalkCheckpoint?.Complete();
        await DrainAsync();
        try { if (onInventory is not null) await onInventory(lastProgressPath, entriesFound); }
        catch (Exception) { /* Reporting does not change the completed scan. */ }
        rootLease.VerifyBinding();
        stopwatch.Stop();
        return new PilotLocalScan(LocalScanEntries.PublishEntries(entries), decisions, totalBytes, filesHashed,
            Elapsed: stopwatch.Elapsed, PathsInspected: entriesFound, Incremental: refreshPaths is not null,
            Complete: complete);
        }
        finally
        {
            // Managed pause/cancel/failure preserves completed pages only. Abrupt process loss
            // may discard the bounded batch; it can never publish an unfinished directory.
            // The caller may release the root only AFTER every reader and its resource
            // lease have closed, including cancellation, callback failure and root drift.
            inspectionCancellation.Cancel();
            try { await Task.WhenAll(pendingResults.Select(item => item.Read)).ConfigureAwait(false); }
            catch { /* Preserve the original failure; no incomplete scan is returned. */ }
            try { WalkCheckpoint?.Flush(); }
            catch (Exception error)
            {
                // Best-effort durability must not replace a pause or the original scan failure.
                // Complete() still propagates flush failures on the successful scan path.
                Trace.TraceWarning($"Local walk checkpoint flush failed: {error}");
            }
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
        var entries = new Dictionary<string, PilotLocalEntry>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<PilotDecision>(refreshed.Decisions);
        var lookups = 0L;
        var processed = 0;
        foreach (var entry in retained.Entries.Values)
        {
            if ((++processed & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!RefreshedByScope(scope, entry.RelativePath, out var probes)) LocalScanEntries.AddEntry(entries, blocked, decisions, entry);
            lookups += probes;
        }
        foreach (var entry in refreshed.Entries.Values)
        {
            if ((++processed & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            LocalScanEntries.AddEntry(entries, blocked, decisions, entry);
        }
        scopeLookups = lookups;
        return refreshed with { Entries = LocalScanEntries.PublishEntries(entries), Decisions = decisions,
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
                // A rename changes the name, not the held NTFS entity or its resident bytes.
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
        if (handle.IsInvalid || !PilotDirectoryLease.GetFileInformationByHandle(handle, out var information))
            throw new IOException("The local folder identity could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((information.FileAttributes & FileAttributes.Directory) == 0 ||
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
