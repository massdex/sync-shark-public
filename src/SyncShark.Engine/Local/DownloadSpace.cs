using System.Collections.Concurrent;
using System.ComponentModel;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>The one process-wide owner of local download space accounting: it admits a transfer against fresh available bytes on the destination's own volume and retires that grant exactly once.</summary>
public sealed class DownloadSpace
{
    // Retained for the process lifetime: a serial's aggregate must outlive every lease so later admissions
    // see the same pool; media churn can leave empty cells, which cost nothing beyond the cell.
    private readonly ConcurrentDictionary<uint, VolumeState> _volumes = new();
    private readonly Func<string, uint> _volumeSerial;
    private readonly Func<string, long> _availableBytes;

    /// <summary>Binds the native volume-serial and caller-available-space observations this owner uses, so a test can supply synthetic volumes.</summary>
    public DownloadSpace(Func<string, uint> volumeSerial, Func<string, long> availableBytes)
    {
        _volumeSerial = volumeSerial;
        _availableBytes = availableBytes;
    }

    /// <summary>The process-wide owner bound to the real native volume operations.</summary>
    public static DownloadSpace Shared { get; } = new(VolumeSpace.Serial, VolumeSpace.Available);

    // One reservation: the volume gate, fresh free-space read and lease creation are one atomic admission.
    /// <summary>Grants the declared bytes in the target's parent-volume pool when fresh free space admits them and returns the lease that retires the grant once; a refusal throws the mapped failure cause.</summary>
    public async ValueTask<IAsyncDisposable> ReserveAsync(string localRoot, string target, long bytes, CancellationToken ct)
    {
        if (bytes < 0) throw new ItemFailedException(target, TextKey.FailDownloadSizeInvalid, null);
        var parent = Path.GetDirectoryName(LocalPaths.Full(localRoot, target))!;

        uint serial;
        try
        {
            serial = _volumeSerial(parent);
        }
        catch (Exception error) when (IsObservationFailure(error))
        {
            throw Observe(target, error);
        }

        var state = _volumes.GetOrAdd(serial, static _ => new VolumeState());
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            long available;
            try
            {
                available = _availableBytes(parent);
            }
            catch (Exception error) when (IsObservationFailure(error))
            {
                throw Observe(target, error);
            }
            if (available < 0) throw new ItemFailedException(target, TextKey.FailLocalSpaceUnknown, null);
            ct.ThrowIfCancellationRequested();
            // R <= F and S <= F - R, written by subtraction so long.MaxValue cannot overflow.
            if (state.ReservedBytes > available || bytes > available - state.ReservedBytes)
                throw new ItemFailedException(target, TextKey.FailLocalNoSpace, null);

            var lease = new Lease(state, bytes);
            state.ReservedBytes += bytes;
            return lease;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static bool IsObservationFailure(Exception error) =>
        LocalErrors.IsFileFailure(error) || error is OverflowException;

    private static ItemFailedException Observe(string target, Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => new ItemFailedException(target, TextKey.ParentMissing, null),
        Win32Exception { NativeErrorCode: ErrorFileNotFound or ErrorPathNotFound } => new ItemFailedException(target, TextKey.ParentMissing, null),
        _ => new ItemFailedException(target, TextKey.FailLocalSpaceUnknown, error),
    };

    // OS facts: Windows ERROR_FILE_NOT_FOUND and ERROR_PATH_NOT_FOUND mean the destination parent is absent.
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int SingleHolder = 1;

    // One retained pool per observed volume serial; only admission/registration and retirement synchronize on it.
    private sealed class VolumeState
    {
        public readonly SemaphoreSlim Gate = new(SingleHolder, SingleHolder);
        public long ReservedBytes;
    }

    // Retirement is uncancellable and subtracts the immutable grant exactly once.
    private sealed class Lease(VolumeState state, long bytes) : IAsyncDisposable
    {
        private bool _retired;

        public async ValueTask DisposeAsync()
        {
            await state.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_retired) return;
                _retired = true;
                state.ReservedBytes -= bytes;
            }
            finally
            {
                state.Gate.Release();
            }
        }
    }
}
