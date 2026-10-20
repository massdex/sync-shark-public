using System.Buffers.Binary;
using System.ComponentModel;
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

    internal static async Task<bool> TryGrowJournalAsync(SyncDiscoveryRoot root, long maximum)
    {
        if (!OperatingSystem.IsWindows() || !IsInstalled) return false;
        // Maintenance never queues behind inventory. The next run can try again or offer UAC.
        if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            if (DateTimeOffset.UtcNow < _journalRetryAfter) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var success = await GrowJournalWindowsAsync(root, maximum, timeout.Token).ConfigureAwait(false);
            if (!success) _journalRetryAfter = DateTimeOffset.UtcNow.AddHours(1);
            return success;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
            System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or
            OperationCanceledException or System.Runtime.InteropServices.ExternalException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { _journalRetryAfter = DateTimeOffset.UtcNow.AddHours(1); return false; }
        finally { Gate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<bool> GrowJournalWindowsAsync(SyncDiscoveryRoot root, long maximum, CancellationToken ct)
    {
        var request = JournalRequest(root, maximum, SyncDiscoveryProtocol.NewNonce());
        var preflight = Native.Prepare(out var prepared);
        if (preflight != 0) return false;
        IntPtr pipe = IntPtr.Zero, lease = IntPtr.Zero;
        try
        {
            RunExistingTask(); var started = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var error = Native.Open(out pipe, out lease);
                if (error == 0) break;
                if (error is not (2 or 231) || started.Elapsed > TimeSpan.FromSeconds(15)) return false;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            using var handle = new SafeFileHandle(pipe, ownsHandle: false);
            await using var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);
            await stream.WriteAsync(request, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            var reply = new byte[16];
            await stream.ReadExactlyAsync(reply, ct).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(reply) != 12 || BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(4)) != 5)
                return false;
            await stream.WriteAsync(new byte[] { 0xa5 }, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(8)) == 0;
        }
        finally { if (lease != IntPtr.Zero) Native.Close(lease); Native.Close(prepared); }
    }
}
