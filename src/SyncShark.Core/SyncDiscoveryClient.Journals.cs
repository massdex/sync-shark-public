using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

internal static partial class SyncDiscoveryClient
{
    private static DateTimeOffset _journalRetryAfter;

    internal static byte[] JournalRequest(SyncDiscoveryRoot root, long maximum, byte[] nonce)
    {
        if (maximum <= 0 || maximum > V2.JournalSizing.Maximum) throw new ArgumentOutOfRangeException(nameof(maximum));
        var request = SyncDiscoveryProtocol.Request([root], nonce);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), 2); // old worker rejects, without effects
        Array.Resize(ref request, request.Length + 8);
        BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(request.Length - 8), maximum);
        return request;
    }

    internal static async Task<uint?> TryGrowJournalAsync(SyncDiscoveryRoot root, long maximum)
    {
        if (!OperatingSystem.IsWindows() || !IsInstalled) return 1;
        // The analyzer does not carry the IsWindows guard above into the lambda.
#pragma warning disable CA1416
        return await TryGrowJournalCoreAsync(ct => GrowJournalWindowsAsync(root, maximum, ct), TimeProvider.System).ConfigureAwait(false);
#pragma warning restore CA1416
    }

    // null means no attempt (busy/cooldown); non-null is a Win32 outcome, including transport failure.
    internal static async Task<uint?> TryGrowJournalCoreAsync(Func<CancellationToken, Task<uint?>> grow, TimeProvider clock)
    {
        // Maintenance never queues behind inventory. The next run can try again or offer UAC.
        if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return null;
        try
        {
            if (clock.GetUtcNow() < _journalRetryAfter) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await grow(timeout.Token).ConfigureAwait(false);
            if (result is not (null or 0 or 183)) _journalRetryAfter = clock.GetUtcNow().AddHours(1);
            return result;
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Automatic journal enlargement failed: {0}", error.GetType().Name);
            _journalRetryAfter = clock.GetUtcNow().AddHours(1); return 1;
        }
        finally { Gate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<uint?> GrowJournalWindowsAsync(SyncDiscoveryRoot root, long maximum, CancellationToken ct)
    {
        var request = JournalRequest(root, maximum, SyncDiscoveryProtocol.NewNonce());
        var preflight = Native.Prepare(out var prepared);
        if (preflight != 0) return 1; // Transport setup is not a journal outcome.
        IntPtr pipe = IntPtr.Zero, lease = IntPtr.Zero;
        try
        {
            RunExistingTask(); var started = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var error = Native.Open(out pipe, out lease);
                if (error == 0) break;
                if (error is not (2 or 231) || started.Elapsed > TimeSpan.FromSeconds(15)) return 1;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            using var handle = new SafeFileHandle(pipe, ownsHandle: false);
            await using var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);
            await stream.WriteAsync(request, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return await ReadJournalReplyAsync(stream, ct).ConfigureAwait(false);
        }
        finally { if (lease != IntPtr.Zero) Native.Close(lease); Native.Close(prepared); }
    }
    internal static async Task<uint?> ReadJournalReplyAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            var reply = new byte[16];
            await stream.ReadExactlyAsync(reply, ct).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(reply) != 12 || BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(4)) != 5)
                return 1;
            await stream.WriteAsync(new byte[] { 0xa5 }, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            var error = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(8));
            if (error is not (0 or 183)) Trace.TraceWarning("Journal worker returned Windows error {0}; the enlargement notice remains available.", error);
            return error;
        }
        catch (EndOfStreamException) { return 1; } // Previous workers reject operation 2 by closing the pipe.
    }

}
