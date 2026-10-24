using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

/// <summary>Full-sharing, low-priority streaming IO. Metadata probes never open file contents.</summary>
internal sealed class LocalGapIo(IStateStore known, V2Settings settings, IReadOnlySet<string> owned,
    Func<bool>? pauseOverride = null) : ILocalGapIo
{
    private const int ContentBufferBytes = 64 * 1024;
    private readonly object _rateGate = new();
    private double _nextRead;

    // Isolated native sharing fixtures can supply journal evidence on a drive with no active journal.
    internal Func<string, NtfsUsnCheckpoint?>? CheckpointForCheck { get; init; }
    internal Func<SafeFileHandle, NtfsFileUsn?>? FileUsnForCheck { get; init; }
    internal Action<long>? AfterContentReadForCheck { get; init; }

    private NtfsUsnCheckpoint? Checkpoint(string path) => CheckpointForCheck is { } read ? read(path) :
        NtfsUsnJournal.TryReadCheckpoint(path, out var proof) ? proof : null;

    private NtfsFileUsn? FileProof(SafeFileHandle file) => FileUsnForCheck is { } read ? read(file) :
        NtfsUsnJournal.TryReadFileUsn(file, out var proof) ? proof : null;

    public bool Paused
    {
        get
        {
            if (pauseOverride is not null) return pauseOverride();
            if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var power) || power.Ac != 1) return true;
            var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            return !GetLastInputInfo(ref input) ||
                unchecked((uint)Environment.TickCount - input.Time) < Math.Max(0, settings.LocalGapIdleTime.TotalMilliseconds);
        }
    }

    public string PauseReason => Paused ? "content verification paused: user active, on battery, or power/input status unavailable; metadata continues" : "content verification running";

    public bool Excluded(RootDef root, ItemState item) =>
        LocalItems.IsExcluded(item.Path, LocalItems.ProductOwnedRelative(root.RootPath, owned));

    public ILocalGapProbe OpenProbes(RootDef root) => new FileIdProbes(root, Checkpoint);

    public LocalGapProof? Probe(RootDef root, ItemState item)
    {
        using var page = OpenProbes(root);
        return page.Probe(item);
    }

    private sealed class FileIdProbes : ILocalGapProbe
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _volume;
        private readonly NtfsUsnCheckpoint? _checkpoint;

        internal FileIdProbes(RootDef root, Func<string, NtfsUsnCheckpoint?> checkpoint)
        {
            _checkpoint = checkpoint(root.RootPath);
            _volume = NtfsUsnJournal.OpenGapVolume(root.RootPath);
        }

        public string? UnavailableReason => _checkpoint is null
            ? "journal unavailable or unsupported: verification needs a readable NTFS journal; ask the Windows administrator to check/enable it, then Sync Now"
            : _volume.IsInvalid ? "volume identity handle unavailable; retry when the volume is accessible" : null;

        public string? LastFailureReason { get; private set; }
        public int? LastOpenError { get; private set; }

        public LocalGapProof? Probe(ItemState item)
        {
            LastFailureReason = null;
            LastOpenError = null;
            if (UnavailableReason is not null) { LastFailureReason = "journal-unavailable"; return null; }
            var identity = LocalItems.ParseObjectId(item.ObjectId);
            using var file = NtfsUsnJournal.OpenGapMetadata(_volume, identity.FileId);
            if (file.IsInvalid)
            {
                LastOpenError = Marshal.GetLastWin32Error();
                LastFailureReason = "open-failed(" + LastOpenError + ")";
                return null;
            }
            if (!NtfsUsnJournal.TryReadFileUsn(file, out var proof, out var failure) || proof is null)
            {
                LastFailureReason = failure ?? "proof-unavailable";
                return null;
            }
            if (proof.Volume != identity.Volume || proof.FileId != identity.FileId)
            {
                LastFailureReason = "metadata-mismatch";
                return null;
            }
            return new(_checkpoint!.Cursor, proof);
        }

        public void Dispose() => _volume.Dispose();
    }

    private LocalGapProof? ProbePath(RootDef root, ItemState item)
    {
        if (LocalItems.IsExcluded(item.Path, LocalItems.ProductOwnedRelative(root.RootPath, owned))) return null;
        var identity = LocalItems.ParseObjectId(root.RootObjectId);
        using var lease = PilotDirectoryLease.Open(root.RootPath, new PilotDirectoryIdentity(identity.Volume, identity.FileId));
        using var guard = LocalItems.GuardRefreshPaths(root.RootPath, root.RootId, known, [item.Path]);
        if (guard.Safe.Count == 0 || Checkpoint(root.RootPath) is not { } checkpoint) return null;
        using var file = NtfsUsnJournal.OpenGapMetadata(Path.Combine(root.RootPath, item.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (file.IsInvalid || FileProof(file) is not { } proof) return null;
        return new(checkpoint.Cursor, proof);
    }

    public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) => HashAsync(root, item, false, ct);

    public async Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, bool urgent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!urgent && Paused) return new(LocalGapHashOutcome.Paused);
        if (settings.LocalGapBytesPerSecond <= 0) return new(LocalGapHashOutcome.Disabled);
        if (LocalItems.IsExcluded(item.Path, LocalItems.ProductOwnedRelative(root.RootPath, owned)))
            return new(LocalGapHashOutcome.Unverifiable);
        var identity = LocalItems.ParseObjectId(root.RootObjectId);
        using var lease = PilotDirectoryLease.Open(root.RootPath, new PilotDirectoryIdentity(identity.Volume, identity.FileId));
        using var guard = LocalItems.GuardRefreshPaths(root.RootPath, root.RootId, known, [item.Path]);
        if (guard.Safe.Count == 0 || Checkpoint(root.RootPath) is not { } checkpoint) return new(LocalGapHashOutcome.Unverifiable);
        var fileIdentity = LocalItems.ParseObjectId(item.ObjectId);
        using var volume = NtfsUsnJournal.OpenGapVolume(root.RootPath);
        if (volume.IsInvalid) return new(LocalGapHashOutcome.Unverifiable);
        // A path-open file handle can pin its parent even with DELETE sharing. Open only by ID.
        using var handle = NtfsUsnJournal.OpenGapContent(volume, fileIdentity.FileId);
        if (handle.IsInvalid)
        {
            return FailedOpen(root, item, Marshal.GetLastWin32Error(), guard, lease);
        }
        if (FileProof(handle) is not { } before || !LocalGapWorker.Matches(item, before))
            return new(LocalGapHashOutcome.Unverifiable, Reason: "file identity or journal proof unavailable");
        // The file alone is pinned with READ|WRITE|DELETE sharing. Never deny parent renames
        // during a large hash or its rate-limit waits. Next-run probing validates the path again.
        guard.Dispose();
        lease.Dispose();
        using var stream = new FileStream(handle, FileAccess.Read, ContentBufferBytes, isAsync: false);
        var content = await ReadHashAsync(stream, urgent, ct).ConfigureAwait(false);
        if (content is null) return new(LocalGapHashOutcome.Paused);
        var after = FileProof(handle);
        if (after is null || before != after || Checkpoint(root.RootPath) is not { } through ||
            through.JournalId != checkpoint.JournalId || through.NextUsn < checkpoint.NextUsn || content.Value.Bytes != item.Size)
            return new(LocalGapHashOutcome.Unverifiable);
        if (ProbePath(root, item) is not { } pathProof || pathProof.File != after) return new(LocalGapHashOutcome.Unverifiable);
        // Promotion checks this file's USN and metadata in the same journal on a later run.
        return new(LocalGapHashOutcome.Verified, content.Value.Hash, new LocalGapProof(checkpoint.Cursor, after!));
    }

    private async Task<(string Hash, long Bytes)?> ReadHashAsync(FileStream stream, bool urgent, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ContentBufferBytes);
        var hasher = new DropboxContentHash.BlockHasher();
        try
        {
            while (true)
            {
                if (!await Budget(buffer.Length, ct, urgent).ConfigureAwait(false)) return null;
                var count = await stream.ReadAsync(buffer.AsMemory(0, ContentBufferBytes), ct).ConfigureAwait(false);
                if (!urgent && Paused) return null;
                if (count == 0) break;
                hasher.Append(buffer.AsSpan(0, count));
                AfterContentReadForCheck?.Invoke(hasher.TotalBytes);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return (hasher.Finish(), hasher.TotalBytes);
    }

    private LocalGapHashResult FailedOpen(RootDef root, ItemState item, int error, IDisposable guard, IDisposable lease)
    {
        guard.Dispose();
        lease.Dispose();
        return CachedIdentityRefresh.OpenFailure(known, root, item, error);
    }

    internal async Task<bool> Budget(int bytes, CancellationToken ct, bool urgent = false)
    {
        double due;
        lock (_rateGate)
        {
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            // Shared across all roots, bounded to one chunk reservation per active reader.
            due = Math.Max(now, _nextRead);
            _nextRead = due + bytes / (double)Math.Max(1, settings.LocalGapBytesPerSecond);
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!urgent && Paused) return false;
            var remaining = due - Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (remaining <= 0) return true;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(remaining, 0.2)), ct).ConfigureAwait(false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Time; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte Ac; public byte Flags; public byte Percent; public byte Reserved; public uint Life; public uint FullLife; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
