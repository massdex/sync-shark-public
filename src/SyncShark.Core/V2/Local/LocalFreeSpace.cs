using System.Diagnostics;
using System.Globalization;

namespace Massdex.SyncShark.V2;

/// <summary>Observes a path's volume free space; null means it cannot be read.</summary>
public interface IVolumeSpaceProbe
{
    long? FreeBytes(string path);
}

/// <summary>An endpoint whose local writes are admitted against free space before any effect.</summary>
/// <remarks>
/// The verdict may wait: when every demand fits the observed free space alone and only other writers' reservations
/// block it, admission waits (bounded and cancellable) for a release instead of refusing a soon-satisfiable item.
/// </remarks>
public interface ILocalFreeSpaceAdmission
{
    Task<FreeSpaceAdmission> AdmitFreeSpaceAsync(SyncAction action, CancellationToken ct);
}

/// <summary>The verdict: a reservation to release when the effect ends, or the pending refusal.</summary>
public sealed record FreeSpaceAdmission(IDisposable? Reservation, FreeSpaceRefusal? Refusal)
{
    public static FreeSpaceAdmission Granted { get; } = new(null, null);
}

/// <summary>A pending item's stored notice text and stable reason key.</summary>
/// <param name="VolumeRoot">
/// The normalized path root the refusal concerns (for example <c>C:\</c>), stable for per-volume aggregation; null
/// only when the path's root cannot be parsed.
/// </param>
/// <param name="RequiredBytes">The demand held against that volume (the copy's source size, or the victim's size).</param>
/// <param name="FreeBytes">The observed free bytes on that volume, or null when they could not be read.</param>
/// <param name="ReserveBytes">The reserve that must remain on that volume, or null when it could not be established.</param>
public sealed record FreeSpaceRefusal(string Detail, string ReasonKey, string? VolumeRoot = null,
    long RequiredBytes = 0, long? FreeBytes = null, long? ReserveBytes = null)
{
    internal const string InsufficientReasonKey = "insufficient-free-space";
    internal const string UnknownSpaceReasonKey = "free-space-unknown";
    internal const string UnknownReserveReasonKey = "free-space-reserve-unknown";

    internal static FreeSpaceRefusal Shortage(string volume, string? volumeRoot, long needed, long free,
        long reserve) => new(
        string.Format(CultureInfo.InvariantCulture,
            "Not enough free space on {0}: needs {1}, free {2}, reserve {3}. " +
            "Options: free space and retry; exclude this folder",
            volume, Bytes(needed), Bytes(free), Bytes(reserve)), InsufficientReasonKey, volumeRoot, needed, free,
        reserve);

    internal static FreeSpaceRefusal UnknownSpace(string path, string? volumeRoot, long needed) => new(
        "Could not read the target volume's free space for '" + path +
        "'; this item is pending until it can be checked. Options: free space and retry; exclude this folder",
        UnknownSpaceReasonKey, volumeRoot, needed);

    internal static FreeSpaceRefusal UnknownReserve(string volume, string? volumeRoot, long needed,
        long free) => new(
        "Could not read the Sync Shark state database size on " + volume +
        "; this item is pending until the data folder is available. " +
        "Options: free space and retry; exclude this folder", UnknownReserveReasonKey, volumeRoot, needed, free);

    private static string Bytes(long bytes) => bytes >= 1L << 30
        ? (bytes / 1073741824d).ToString("0.0", CultureInfo.InvariantCulture) + " GiB"
        : bytes >= 1L << 20
            ? (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
            : bytes.ToString(CultureInfo.InvariantCulture) + " B";
}

/// <summary>
/// The writer's own byte admission refused after the durable intent was recorded (the up-front admission could
/// reserve metadata only). The executor maps it to the same pending free-space notice as an up-front refusal.
/// </summary>
public sealed class FreeSpaceRefusedException(FreeSpaceRefusal refusal) : IOException(refusal.Detail)
{
    public FreeSpaceRefusal Refusal { get; } = refusal;
}

/// <summary>The production probe over DriveInfo.</summary>
internal sealed class DriveVolumeSpaceProbe : IVolumeSpaceProbe
{
    public long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// One process-wide free-space gate: observes the volumes, reserves bytes per volume root so concurrent writers
/// (including different pairs) see each other, and releases them when the action ends. The reserve is 512 MiB
/// everywhere; on the configured state database's volume it is the larger of that floor and state.db + WAL bytes.
/// An unreadable volume, database or data path leaves the item pending instead of granting it.
/// </summary>
/// <remarks>
/// A Dropbox source is downloaded straight into the target's own temporary file, so a download demands one copy
/// plus the reserve on the target volume; there is no spool copy to reserve. The verified partial and the Recovery
/// archive coexist until the swap, so they sum on one shared volume and are checked separately on two. A demand that fits
/// the observed free space alone but fails only because of other writers' reservations waits (bounded, cancellable,
/// woken by any release) and rechecks instead of refusing immediately.
/// </remarks>
internal sealed class LocalFreeSpaceGuard
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Reserved = new(StringComparer.OrdinalIgnoreCase);
    private static TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const int AdmissionWaitSeconds = 120;

    private readonly IVolumeSpaceProbe _probe;
    private readonly V2Settings _settings;
    private readonly TimeProvider _time;
    private readonly string? _databasePath;
    private readonly string? _dataRoot;
    private readonly TimeSpan _admissionWait;
    private long? _dataBytes;
    private DateTimeOffset _dataReadAt;
    private bool _dataRead;

    internal LocalFreeSpaceGuard(IVolumeSpaceProbe probe, V2Settings settings, string? databasePath = null,
        TimeProvider? time = null, TimeSpan? admissionWait = null)
    {
        _probe = probe;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _databasePath = databasePath;
        _dataRoot = databasePath is null ? null : RootOf(databasePath);
        _admissionWait = admissionWait ?? TimeSpan.FromSeconds(AdmissionWaitSeconds);
    }

    internal async Task<FreeSpaceAdmission> AdmitAsync(SyncAction action, string targetPath, string recoveryPath,
        CancellationToken ct)
    {
        var writes = action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth;
        var writeBytes = writes ? Math.Max(0, action.Source?.Size ?? 0) : 0;
        var recoveryBytes = action.RecoveryFirst && action.ExpectedTarget is { Kind: ItemKind.File } victim
            ? Math.Max(0, victim.Size)
            : 0;
        return await AdmitAsync(writeBytes, targetPath, writes, recoveryBytes, recoveryPath, ct)
            .ConfigureAwait(false);
    }

    internal async Task<FreeSpaceAdmission> AdmitAsync(long writeBytes, string targetPath, bool writes,
        long recoveryBytes, string recoveryPath, CancellationToken ct)
    {
        if (!writes && recoveryBytes <= 0) return FreeSpaceAdmission.Granted;
        // Observation and reservation share one gate: a reading taken outside it could go stale while another writer
        // reserves, and both writes would be admitted against the same free space.
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var waitable = Stopwatch.GetElapsedTime(started) < _admissionWait;
            Task release;
            lock (Gate)
            {
                var demands = BuildDemands(writeBytes, targetPath, writes, recoveryBytes, recoveryPath);
                if (demands.Refusal is { } refusal) return Pending(refusal);

                var wait = false;
                foreach (var (root, demand) in demands.Volumes)
                {
                    var reserve = ReserveFor(root);
                    if (reserve is not { } required)
                        return Pending(FreeSpaceRefusal.UnknownReserve(demand.Volume, root, demand.Bytes,
                            demand.Free));
                    if (demand.Free - Reserved.GetValueOrDefault(root) - demand.Bytes >= required) continue;
                    // The observed free space itself is enough (free already shows in-flight bytes); only another
                    // writer's reservation stands in the way, so a release may admit this demand soon.
                    if (waitable && demand.Free - demand.Bytes >= required)
                    {
                        wait = true;
                        continue;
                    }
                    return Pending(FreeSpaceRefusal.Shortage(demand.Volume, root, demand.Bytes, demand.Free,
                        required));
                }
                if (!wait)
                {
                    foreach (var (root, demand) in demands.Volumes)
                        Reserved[root] = Reserved.GetValueOrDefault(root) + demand.Bytes;
                    return new FreeSpaceAdmission(
                        new Reservation(demands.Volumes.Select(d => (d.Key, d.Value.Bytes)).ToArray()), null);
                }
                release = _release.Task;
            }

            var remaining = _admissionWait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) continue; // recheck once more; waitable is now false
            try
            {
                // Woken by any reservation release (a fresh signal per release generation); otherwise bounded.
                await release.WaitAsync(remaining, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The bound elapsed; the next pass evaluates with waitable false and refuses if still blocked.
            }
        }
    }

    // The per-volume demands for one admission. A download writes its one verified copy into the target directory
    // before the original is archived, and the Recovery copy exists at the same moment, so on one shared volume the
    // two demands SUM. On separate volumes each is checked on its own. No spool is reserved.
    private Demands BuildDemands(long writeBytes, string targetPath, bool writes, long recoveryBytes,
        string recoveryPath)
    {
        var volumes = new Dictionary<string, Demand>(StringComparer.OrdinalIgnoreCase);
        if (writes)
        {
            var target = _probe.FreeBytes(targetPath);
            if (target is null)
                return new Demands(volumes, FreeSpaceRefusal.UnknownSpace(targetPath, RootOf(targetPath), writeBytes));
            var targetRoot = RootOf(targetPath);
            if (targetRoot is null)
                return new Demands(volumes, FreeSpaceRefusal.UnknownSpace(targetPath, null, writeBytes));
            volumes[targetRoot] = new Demand(VolumeName(targetPath), writeBytes, target.Value);
        }
        if (recoveryBytes > 0)
        {
            var recovery = _probe.FreeBytes(recoveryPath);
            if (recovery is null)
                return new Demands(volumes,
                    FreeSpaceRefusal.UnknownSpace(recoveryPath, RootOf(recoveryPath), recoveryBytes));
            var root = RootOf(recoveryPath);
            if (root is null)
                return new Demands(volumes, FreeSpaceRefusal.UnknownSpace(recoveryPath, null, recoveryBytes));
            if (volumes.TryGetValue(root, out var existing))
            {
                // The verified partial and the Recovery archive coexist until the swap, so a shared volume must
                // hold both at once.
                volumes[root] = existing with { Bytes = checked(existing.Bytes + recoveryBytes) };
            }
            else volumes[root] = new Demand(VolumeName(recoveryPath), recoveryBytes, recovery.Value);
        }
        return new Demands(volumes, null);
    }

    private readonly record struct Demand(string Volume, long Bytes, long Free);

    private readonly record struct Demands(Dictionary<string, Demand> Volumes, FreeSpaceRefusal? Refusal);

    // The floor everywhere; on the state database's volume, max(floor, database + WAL). Null = the reserve cannot
    // be established, so the item stays pending rather than being admitted against an assumed small database.
    private long? ReserveFor(string root)
    {
        var floor = Math.Max(0, _settings.FreeSpaceFloorBytes);
        if (_databasePath is null) return floor;
        if (_dataRoot is null) return null;
        if (!string.Equals(root, _dataRoot, StringComparison.OrdinalIgnoreCase)) return floor;
        var data = DataBytes();
        return data is null ? (long?)null : Math.Max(floor, data.Value);
    }

    // Cached for FreeSpaceSizeCache; a missing/unreadable database is unknown, never zero. A missing WAL is zero.
    private long? DataBytes()
    {
        var now = _time.GetUtcNow();
        if (_dataRead && now - _dataReadAt < _settings.FreeSpaceSizeCache) return _dataBytes;
        var database = Length(_databasePath!, required: true);
        var wal = database is null ? null : Length(_databasePath! + "-wal", required: false);
        _dataBytes = database is null || wal is null ? null : database.Value + wal.Value;
        _dataRead = true;
        _dataReadAt = now;
        return _dataBytes;
    }

    // A genuinely missing file is the only "absent" case: an unreadable one is unknown (null), never treated as zero.
    private static long? Length(string path, bool required)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (FileNotFoundException)
        {
            return required ? (long?)null : 0L;
        }
        catch (DirectoryNotFoundException)
        {
            return required ? (long?)null : 0L;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    private static FreeSpaceAdmission Pending(FreeSpaceRefusal refusal) => new(null, refusal);

    private static string? RootOf(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    private static string VolumeName(string path) =>
        (RootOf(path) ?? path).TrimEnd('\\', '/') is { Length: > 0 } name ? name : path;

    private sealed class Reservation(IReadOnlyList<(string Root, long Bytes)> held) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            lock (Gate)
            {
                foreach (var (root, bytes) in held)
                {
                    var left = Reserved.GetValueOrDefault(root) - bytes;
                    if (left > 0) Reserved[root] = left;
                    else Reserved.Remove(root);
                }
                // Wake every admission waiting only on other writers' reservations so it rechecks the new state.
                var released = _release;
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                released.TrySetResult();
            }
        }
    }
}
