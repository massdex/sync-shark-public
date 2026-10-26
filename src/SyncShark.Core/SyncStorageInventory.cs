using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

public sealed record SyncStorageInventoryRow(string? PairId, string? ConnectionId, string Provider,
    string PairName, string LocalPath, string RelativePath, string Availability, string DesiredPolicy,
    string Participation, string Ownership, string Evidence, DateTimeOffset? ObservedUtc,
    string Completeness, long? LogicalBytes = null, long? ResidentBytes = null, long? OnlineOnlyFiles = null);
public sealed record SyncDeviceSpace(string Drive, long? CapacityBytes, long? AvailableBytes, DateTimeOffset CheckedUtc, string? Error = null);
public sealed record SyncStorageInventoryPage(IReadOnlyList<SyncStorageInventoryRow> Rows, bool HasMore,
    int NextOffset, DateTimeOffset ReadUtc, string Scope, string Notice);

public static class SyncStorageInventory
{
    public static bool IsOnlineOnlyMapping(SyncStorageInventoryRow row) => row.Participation != "Ignored" &&
        (row.DesiredPolicy == "Online only" || row.Availability.StartsWith("Online", StringComparison.OrdinalIgnoreCase) || row.OnlineOnlyFiles > 0);
    public static bool IsSelectiveSyncMapping(SyncStorageInventoryRow row) => row.Participation == "Excluded from sync";

    public static IReadOnlyList<SyncDeviceSpace> ReadDeviceSpace() => ReadDeviceSpace(
        DriveInfo.GetDrives().Select(drive => drive.Name),
        root => new DriveInfo(root).DriveType, ResolveVolumeIdentity,
        root => { var drive = new DriveInfo(root); return (drive.TotalSize, drive.AvailableFreeSpace); });

    // Metadata suppliers are injectable for deterministic checks; filtering/dedup/error decisions
    // stay here, shared by both the GUI and CLI capacity surfaces.
    internal static IReadOnlyList<SyncDeviceSpace> ReadDeviceSpace(IEnumerable<string> roots,
        Func<string, DriveType> driveType, Func<string, string?> volumeIdentity,
        Func<string, (long Total, long Available)> capacity)
    {
        var rows = new List<SyncDeviceSpace>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            string? identity;
            try
            {
                if (driveType(root) != DriveType.Fixed) continue;
                identity = volumeIdentity(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { continue; } // A failed identity query is not proof of a local volume.
            if (string.IsNullOrWhiteSpace(identity) || !seen.Add(identity)) continue;
            try
            {
                var bytes = capacity(root);
                rows.Add(new(root, bytes.Total, bytes.Available, DateTimeOffset.UtcNow));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { rows.Add(new(root, null, null, DateTimeOffset.UtcNow, "Device capacity unavailable")); }
        }
        return rows;
    }

    private static string? ResolveVolumeIdentity(string root)
    {
        if (!OperatingSystem.IsWindows()) return root;
        var name = new StringBuilder(50);
        if (!GetVolumeNameForVolumeMountPointW(root, name, (uint)name.Capacity) || name.Length == 0) return null;
        // Cloud filesystem drivers can advertise Fixed drives AND volume GUIDs. Require a readable
        // storage-device descriptor too; never count a filesystem-only virtual capacity as disk space.
        // Zero desired access: query metadata only, no file/device data read, writes or elevation.
        using var handle = CreateFileW(name.ToString().TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var descriptor = new byte[4096];
        return DeviceIoControl(handle, 0x002d1400, new byte[12], 12, descriptor,
            (uint)descriptor.Length, out var returned, IntPtr.Zero) && IsDiskDescriptor(descriptor, returned)
            ? name.ToString() : null;
    }

    internal static bool IsDiskDescriptor(byte[] descriptor, uint returned) => returned >= 36 &&
        returned <= descriptor.Length && BitConverter.ToUInt32(descriptor, 0) >= 36 &&
        BitConverter.ToUInt32(descriptor, 4) >= 36 && BitConverter.ToUInt32(descriptor, 4) <= returned &&
        descriptor[8] == 0 && BitConverter.ToUInt32(descriptor, 28) != 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
        byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder name, uint capacity);
    internal static SyncStorageInventoryRow Saved(SyncPair pair, string path, string availability,
        DateTimeOffset? indexed, string completeness) => new(pair.Id, pair.ConnectionId, pair.Provider,
            pair.Name, path == "." ? pair.LocalRoot : Path.Combine(pair.LocalRoot, path.Replace('/', Path.DirectorySeparatorChar)),
            path, availability,
            (path == "." ? SyncDirectoryStoragePolicy.Default(pair) : SyncDirectoryStoragePolicy.Resolve(pair with { FileStorageRules = null }, path)) == SyncDirectoryStorage.CloudOnly
                    ? "Online only" : "Available offline",
            path == "." ? "Pair root" : SyncIgnorePolicy.IsIgnored(path, true, pair.IgnoreRules ?? []) ? "Ignored"
                : SyncSelectionPolicy.IsIncluded(path, pair, true) ? "Selected for sync" : "Excluded from sync",
            "Not inspected", "Saved pair policy and retained folder index; not a fresh filesystem observation",
            indexed, completeness);

    public static SyncStorageInventoryPage Inspect(string root, SyncPair? pair, int limit,
        CancellationToken cancellationToken = default)
    {
        root = StorageInventoryWindows.ValidateRoot(root);
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        var registrations = StorageInventoryWindows.RegisteredRoots();
        var observedAt = DateTimeOffset.UtcNow; var rows = new List<SyncStorageInventoryRow>();
        var pending = new Queue<string>(); pending.Enqueue(root);
        var remainingEntries = Math.Min(5000, limit * 25); var complete = true;
        while (pending.Count > 0 && rows.Count < limit && remainingEntries > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue(); var errors = new List<string>();
            StorageLocalObservation? metadata = null;
            long logical = 0, resident = 0; var files = 0; var online = 0; var offline = 0; var partial = 0; var unknown = 0;
            var directoryComplete = false;
            try
            {
                metadata = StorageInventoryWindows.Inspect(directory);
                if (!metadata.Directory) throw new IOException("Select a directory for storage inspection.");
                if (metadata.MayEnumerate)
                {
                    var children = StorageInventoryWindows.OnDiskChildren(directory, remainingEntries, cancellationToken, out directoryComplete);
                    remainingEntries -= children.Count;
                    foreach (var child in children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var item = StorageInventoryWindows.Inspect(child);
                            if (item.Directory) { pending.Enqueue(child); continue; }
                            files++;
                            if (item.LogicalBytes is { } size) logical += size;
                            if (item.ResidentBytes is { } disk) resident += disk;
                            switch (item.Availability)
                            {
                                case "Online only": online++; break;
                                case "Available offline": offline++; break;
                                case "Partly available / awaiting validation": partial++; break;
                                default: unknown++; break;
                            }
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        { unknown++; errors.Add("One child could not be inspected: " + error.Message); }
                    }
                }
                else errors.Add(metadata.Evidence);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
            { errors.Add(error.Message); }
            complete &= directoryComplete && errors.Count == 0;
            var registration = registrations.Where(r => Contains(r.Path, directory)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
            var availability = metadata is null ? "Unavailable / not inspected" : files == 0
                ? metadata.Availability : partial > 0 || unknown > 0 ? "Mixed / partly known"
                : online > 0 && offline > 0 ? "Mixed availability" : online > 0 ? "Online-only files observed" : "Offline files observed";
            var relative = pair is null ? Path.GetRelativePath(root, directory).Replace('\\', '/')
                : Path.GetRelativePath(pair.LocalRoot, directory).Replace('\\', '/');
            var saved = pair is null ? null : Saved(pair, relative, availability, observedAt, "");
            rows.Add(new(pair?.Id, pair?.ConnectionId, pair?.Provider ?? "Unpaired", pair?.Name ?? "Unpaired folder", directory, relative,
                availability, saved?.DesiredPolicy ?? "Not managed by Sync Shark", saved?.Participation ?? "Unknown; no saved selection rules",
                registration is null ? "Unknown / no registered provider" : "Registered: " + registration.Provider,
                (registration?.Evidence is { } evidence ? evidence + ". " : "") + (metadata?.Evidence ?? "No readable metadata") +
                    $". Direct files observed: {files}; online only: {online}; offline: {offline}; partial: {partial}; unknown: {unknown}." +
                    (errors.Count > 0 ? " " + errors[0] : ""), observedAt,
                directoryComplete && errors.Count == 0 ? "On-disk direct entries inspected; cloud-only children may be absent" : "Incomplete / not inspected",
                unknown == 0 && directoryComplete ? logical : null, unknown == 0 && directoryComplete ? resident : null, online));
        }
        complete &= pending.Count == 0;
        return new(rows, false, 0, observedAt, root,
            (complete ? "Bounded on-disk inspection finished." : "Partial inspection: limits or unreadable entries remain. Inspect a specific subfolder to narrow the scope.") +
            " Sizes cover directly observed files, not whole subtrees. Missing selective-sync folders cannot be inferred from the filesystem. No content was read or downloaded.");
    }

    internal static bool Contains(string root, string path) => string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

public sealed partial class SyncAuthorityRuntimeHost
{
    private async Task<SyncPairSet> LoadInventoryPairsAsync(CancellationToken cancellationToken)
    {
        // Viewing storage must not initialise/migrate authority or recover commands as LoadPairs does.
        var retained = AuthorityStateExists ? await _authority.LoadPairSetAsync(cancellationToken, readOnly: true) : new SyncPairSet([], null);
        var known = retained.Pairs.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var legacy = pairStore.Load();
        var merged = new SyncPairSet(retained.Pairs.Concat(legacy.Pairs.Where(p => !known.Contains(p.Id))
            .Select(p => p with { ControlNamespace = null, RecoveryRoot = null, PolicyRevision = 1 })).ToArray(), retained.SelectedPairId);
        return AuthorityStateExists
            ? await _authority.NormalizeRetainedReadbackAsync(merged, cancellationToken)
            : SyncPairStore.Normalize(merged);
    }

    public async Task<SyncStorageInventoryPage> LoadStorageInventoryAsync(string? accountId = null, string? pairId = null,
        int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        var set = await LoadInventoryPairsAsync(cancellationToken);
        string Account(SyncPair pair) => pair.Provider == SyncProviders.Dropbox
            ? connectionStore.Load(pair.ConnectionId)?.ConnectionId ?? pair.ConnectionId : pair.ConnectionId;
        if (pairId is not null && !set.Pairs.Any(p => p.Id == pairId && (accountId is null || Account(p) == accountId)))
            throw new InvalidOperationException("This pair is unavailable in the selected account.");
        var rows = new List<SyncStorageInventoryRow>(); var more = false;
        foreach (var pair in set.Pairs.Where(p => (pairId is null || p.Id == pairId) && (accountId is null || Account(p) == accountId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = AuthorityStateExists ? await _authority.LoadFolderSelectionPageAsync(pair.Id, limit, offset, cancellationToken: cancellationToken) : null;
            var indexed = AuthorityStateExists ? (await _authority.LoadStorageSizeSummaryAsync(pair.Id, cancellationToken)).IndexedUtc : null;
            if (offset == 0)
            {
                rows.Add(SyncStorageInventory.Saved(pair, ".", "Not inspected", indexed, page is null ? "No retained index" : "Saved index; refresh does not scan") with { ConnectionId = Account(pair) });
            }
            // Page explicit choices too: excluded/absent folders must not disappear at an arbitrary cap.
            var choices = (pair.SelectionRules ?? []).Select(r => r.FolderPath)
                .Concat((pair.DirectoryStorageRules ?? []).Select(r => r.FolderPath)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(offset).Take(limit + 1).ToArray();
            foreach (var path in choices.Take(limit))
                rows.Add(SyncStorageInventory.Saved(pair, path, "Not observed; saved policy only", null, "No presence inferred from policy") with { ConnectionId = Account(pair) });
            foreach (var folder in page?.Folders ?? [])
                rows.Add(SyncStorageInventory.Saved(pair, folder.RelativePath, folder.Presence switch
                {
                    SyncFolderPresence.CloudOnly => "Cloud present; absent locally at last index",
                    SyncFolderPresence.OnlineOnly => "Online only at last index",
                    SyncFolderPresence.Mixed => "Mixed at last index",
                    SyncFolderPresence.LocalUnloaded => "Excluded; local contents not inspected",
                    SyncFolderPresence.Excluded => "Excluded; presence not established",
                    _ => "Folder present at last index; contents may be mixed"
                }, indexed, "Retained index; not a live observation") with { ConnectionId = Account(pair) });
            more |= page?.HasMore == true || choices.Length > limit;
        }
        return new(rows.GroupBy(r => (r.PairId, Path.GetFullPath(r.LocalPath).ToUpperInvariant())).Select(g => g.Last()).ToArray(),
            more, offset + limit, DateTimeOffset.UtcNow, pairId ?? "All selected pair roots",
            "Saved index and policy only. Inspect a root for current on-disk metadata; no sync or provider listing starts here.");
    }

    public async Task<SyncStorageInventoryPage> InspectStorageRootAsync(string root, string? pairId = null,
        string? accountId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        root = StorageInventoryWindows.ValidateRoot(root);
        SyncPair? pair = null;
        if (pairId is not null)
        {
            pair = (await LoadInventoryPairsAsync(cancellationToken)).Pairs.SingleOrDefault(p => p.Id == pairId)
                ?? throw new InvalidOperationException("The selected pair no longer exists.");
            var boundAccount = pair.Provider == SyncProviders.Dropbox ? connectionStore.Load(pair.ConnectionId)?.ConnectionId ?? pair.ConnectionId : pair.ConnectionId;
            if (accountId is null || boundAccount != accountId || !SyncStorageInventory.Contains(pair.LocalRoot, root))
                throw new InvalidOperationException("The inspection path does not belong to this exact pair and account.");
        }
        else if (accountId is not null) throw new InvalidOperationException("An unpaired folder cannot be assigned to an account by inspection.");
        return await Task.Run(() => SyncStorageInventory.Inspect(root, pair, limit, cancellationToken), cancellationToken);
    }
}
