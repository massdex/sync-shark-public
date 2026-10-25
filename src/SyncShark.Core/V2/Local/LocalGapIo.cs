using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ProofStage = Massdex.SyncShark.V2.ContentProofFailure.StageKind;

namespace Massdex.SyncShark.V2;

/// <summary>Full-sharing, low-priority streaming IO. Metadata probes never open file contents.</summary>
internal sealed partial class LocalGapIo(IStateStore known, V2Settings settings, IReadOnlySet<string> owned,
    Func<bool>? pauseOverride = null, TimeProvider? time = null) : ILocalGapIo
{
    public TimeProvider Time => time ?? TimeProvider.System;
    private const int ContentBufferBytes = 64 * 1024;
    private readonly object _rateGate = new();
    private double _nextRead;

    // Isolated native sharing fixtures can supply journal evidence on a drive with no active journal.
    internal Func<string, NtfsUsnCheckpoint?>? CheckpointForCheck { get; init; }
    internal Func<SafeFileHandle, NtfsFileUsn?>? FileUsnForCheck { get; init; }
    internal Action<long>? AfterContentReadForCheck { get; init; }

    private NtfsUsnCheckpoint? Checkpoint(string path) => CheckpointForCheck is { } read ? read(path) :
        NtfsUsnJournal.TryReadCheckpoint(path, out var proof) ? proof : null;

    private NtfsFileUsn? FileProof(SafeFileHandle file, out string? reason)
    {
        reason = null;
        if (FileUsnForCheck is { } read) return read(file);
        return NtfsUsnJournal.TryReadFileUsn(file, out var proof, out reason) ? proof : null;
    }

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

    internal Func<PairDef, Side, ItemState, ItemEligibility.Purpose, string?>? EligibilityHeld { get; set; }

    public bool Excluded(RootDef root, ItemState item) => Eligibility(root, item, checkHolds: false).NotPairWork;

    private ItemEligibility.Result Eligibility(RootDef root, ItemState item, bool checkHolds)
    {
        var pass = _proofPass.Value;
        var eligibility = pass?.RootId == root.RootId ? (checkHolds ? pass.Proof : pass.Selection) :
            new ItemEligibility(known.Pairs(), LocalItems.ProductOwnedRelative(root.RootPath, owned).ToArray(),
                checkHolds ? EligibilityHeld : null);
        // Only the removal owner supplies this pair and enclosing-folder scope.
        if (pass?.RootId == root.RootId && pass.ReviewedRemoval(item) is { } pair)
        {
            var side = pair.A.RootId == root.RootId ? Side.A : Side.B;
            var boundary = pass.Selection.Evaluate(pair, side == Side.A ? Side.B : Side.A,
                item, ItemEligibility.Purpose.ReviewedRemoval);
            return boundary.Eligible ? eligibility.Evaluate(pair, side, item, ItemEligibility.Purpose.ReviewedRemoval) : boundary;
        }
        return eligibility.EvaluateRoot(root.RootId, item, ItemEligibility.Purpose.ContentProof);
    }

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

    private LocalGapProof? ProbePath(RootDef root, ItemState item, out ContentProofFailure? failure)
    {
        failure = new(ProofStage.PathIdentity, MessageCatalog.Default.Message("proof-path-unavailable"));
        try
        {
            if (Eligibility(root, item, checkHolds: true) is { Eligible: false } eligibility)
            { failure = new(ProofStage.Eligibility, eligibility.Reason!); return null; }
            var identity = LocalItems.ParseObjectId(root.RootObjectId);
            using var lease = PilotDirectoryLease.Open(root.RootPath, new PilotDirectoryIdentity(identity.Volume, identity.FileId));
            using var guard = LocalItems.GuardRefreshPaths(root.RootPath, root.RootId, known, [item.Path]);
            if (guard.Safe.Count == 0) { failure = guard.Failure ?? failure; return null; }
            if (Checkpoint(root.RootPath) is not { } checkpoint)
            { failure = new(ProofStage.Journal, MessageCatalog.Default.Message("proof-journal-unavailable")); return null; }
            using var file = NtfsUsnJournal.OpenGapMetadata(LocalItems.FullPath(root.RootPath, item.Path));
            if (file.IsInvalid) { failure = failure with { NativeError = Marshal.GetLastWin32Error() }; return null; }
            if (FileProof(file, out var reason) is not { } proof)
            { failure = ContentProofFailure.FromDiagnostic(ProofStage.PathIdentity, reason ?? failure.Cause); return null; }
            failure = null;
            return new(checkpoint.Cursor, proof);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        { failure = ContentProofFailure.FromException(ProofStage.PathIdentity, error); return null; }
    }

    public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) => HashAsync(root, item, false, ct);

    public async Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, bool urgent, CancellationToken ct)
    {
        var stage = ProofStage.Eligibility;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!urgent && Paused) return new(LocalGapHashOutcome.Paused);
            if (settings.LocalGapBytesPerSecond <= 0) return new(LocalGapHashOutcome.Disabled);
            if (Eligibility(root, item, checkHolds: true) is { Eligible: false } eligibility)
                return EligibilityRejection(eligibility);
            stage = ProofStage.RootOpen;
            var identity = LocalItems.ParseObjectId(root.RootObjectId);
            using var lease = PilotDirectoryLease.Open(root.RootPath, new PilotDirectoryIdentity(identity.Volume, identity.FileId));
            stage = ProofStage.Classification;
            if (await RefreshClassificationAsync(root, item, lease, ct).ConfigureAwait(false) is { } rejection)
                return rejection.Outcome == LocalGapHashOutcome.Unverifiable && rejection.Failure is null ? rejection.At(stage) : rejection;
            return await HashResidentAsync(root, item, urgent, lease, ct, next => stage = next).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            NativeIoDiagnostic.LogOnce("content proof " + stage, error);
            return new(LocalGapHashOutcome.Unverifiable, Reason: error.Message, QuarantineCode: QuarantineFailure.NativeCode(error))
            { Failure = ContentProofFailure.FromException(stage, error) };
        }
    }

    private async Task<LocalGapHashResult> HashResidentAsync(RootDef root, ItemState item, bool urgent,
        PilotDirectoryLease lease, CancellationToken ct, Action<ProofStage> setStage)
    {
        ProofStage stage;
        setStage(stage = ProofStage.ParentIdentity);
        using var guard = LocalItems.GuardRefreshPaths(root.RootPath, root.RootId, known, [item.Path]);
        if (guard.Safe.Count == 0) return new(LocalGapHashOutcome.Unverifiable, Reason: MessageCatalog.Default.Message("proof-parent-guard"))
        { Failure = guard.Failure ?? new(stage, MessageCatalog.Default.Message("proof-parent-guard")) };
        setStage(stage = ProofStage.Journal);
        if (Checkpoint(root.RootPath) is not { } checkpoint)
            return LocalGapHashResult.Failed(stage, MessageCatalog.Default.Message("proof-journal-unavailable"));
        setStage(stage = ProofStage.VolumeOpen);
        var fileIdentity = LocalItems.ParseObjectId(item.ObjectId);
        using var volume = NtfsUsnJournal.OpenGapVolume(root.RootPath);
        if (volume.IsInvalid) return LocalGapHashResult.Failed(stage, MessageCatalog.Default.Message("proof-volume-open"), Marshal.GetLastWin32Error());
        setStage(stage = ProofStage.ContentOpen);
        // The content handle is no-recall and opened by identity, never through a cached path.
        using var handle = NtfsUsnJournal.OpenGapContent(volume, fileIdentity.FileId);
        if (handle.IsInvalid) return FailedOpen(root, item, Marshal.GetLastWin32Error(), guard, lease);
        setStage(stage = ProofStage.BeforeRead);
        if (FileProof(handle, out var beforeFailure) is not { } before || !LocalGapWorker.Matches(item, before))
            return LocalGapHashResult.Failed(stage, beforeFailure ?? MessageCatalog.Default.Message("proof-before-read"));
        guard.Dispose();
        lease.Dispose();
        setStage(stage = ProofStage.Read);
        using var stream = new FileStream(handle, FileAccess.Read, ContentBufferBytes, isAsync: false);
        var content = await ReadHashAsync(stream, urgent, ct).ConfigureAwait(false);
        if (content is null) return new(LocalGapHashOutcome.Paused);
        setStage(stage = ProofStage.AfterRead);
        var after = FileProof(handle, out var afterFailure);
        if (after is null) return LocalGapHashResult.Failed(stage, afterFailure ?? MessageCatalog.Default.Message("proof-after-unavailable"));
        return ValidateHash(root, item, urgent, checkpoint, before, after, content.Value);
    }

    private LocalGapHashResult ValidateHash(RootDef root, ItemState item, bool urgent, NtfsUsnCheckpoint checkpoint,
        NtfsFileUsn before, NtfsFileUsn? after, (string Hash, long Bytes) content)
    {
        if (after is null || Checkpoint(root.RootPath) is not { } through ||
            through.JournalId != checkpoint.JournalId || through.NextUsn < checkpoint.NextUsn)
            return LocalGapHashResult.Failed(ProofStage.AfterRead, MessageCatalog.Default.Message("proof-journal-changed"));
        if (before != after || content.Bytes != item.Size)
            return LocalGapHashResult.Failed(ProofStage.AfterRead, MessageCatalog.Default.Message("proof-content-changed"),
                outcome: urgent ? LocalGapHashOutcome.ChangedDuringRead : LocalGapHashOutcome.Unverifiable);
        if (ProbePath(root, item, out var failure) is not { } pathProof)
            return failure is { Stage: ProofStage.Eligibility }
                ? EligibilityRejection(new ItemEligibility.Result(failure.Cause))
                : new(LocalGapHashOutcome.Unverifiable, Reason: failure?.Cause) { Failure = failure };
        if (pathProof.File != after) return LocalGapHashResult.Failed(ProofStage.PathIdentity, MessageCatalog.Default.Message("proof-path-changed"),
            outcome: urgent ? LocalGapHashOutcome.ChangedDuringRead : LocalGapHashOutcome.Unverifiable);
        // Promotion checks this file's USN and metadata in the same journal on a later run.
        return new(LocalGapHashOutcome.Verified, content.Hash, new LocalGapProof(checkpoint.Cursor, after!));
    }

    private static LocalGapHashResult EligibilityRejection(ItemEligibility.Result eligibility) =>
        new(eligibility.NotPairWork ? LocalGapHashOutcome.NotPairWork : LocalGapHashOutcome.Held,
            Reason: eligibility.Reason);

    private async Task<(string Hash, long Bytes)?> ReadHashAsync(FileStream stream, bool urgent, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ContentBufferBytes);
        var hasher = new ContentBlockHasher();
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
        return CachedIdentityRefresh.OpenFailure(known, root, item, error, Time.GetUtcNow());
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
