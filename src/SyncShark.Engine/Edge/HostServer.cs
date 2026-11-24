using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>Owns the one local host address through shutdown and the last admitted command.</summary>
public sealed class HostServer : IDisposable
{
    /// <summary>Signal a waiting CLI launcher without making readiness depend on its continued presence.</summary>
    public static void Ready(string[] args) {
        if (args is not [ResourceNames.HostReadyArgument]) return;
        try { Console.Out.WriteLine(1); Console.Out.Flush(); }
        catch (IOException) { /* The initiating CLI exited. */ }
    }

    internal static string PipeName {
        get {
            // The per-user store is shared across logon sessions.
            using var identity = WindowsIdentity.GetCurrent();
            return $"{nameof(HostServer)}.{ResourceNames.HostExecutable}.{identity.User!.Value}";
        }
    }
    private NamedPipeServerStream? waiting;
    // Predecessor concurrent-command limit (0.3.95 EngineHostEntry.MaximumConnections).
    private const int MaximumConcurrentCommands = 32;
    private NamedPipeServerStream Listen(bool first) => new(PipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    /// <summary>Claim before opening product state; authenticate any already-running instance.</summary>
    public async Task<bool> ClaimAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { waiting = Listen(true); return true; }
        catch (Exception e) when (e is UnauthorizedAccessException || e is IOException && (e.HResult & 0xffff) is 5 or 231) {
            using var probe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await probe.ConnectAsync(300, ct);
            using var existing = AuthenticatedPeer(probe);
            if (existing is null) throw new UnauthorizedAccessException();
            return false;
        }
    }
    /// <summary>Serve the claimed pipe; stop intake and drain before releasing its name.</summary>
    public async Task ServeAsync(Run.Host host, CancellationToken ct)
    {
        var commands = new Commands();
        using var intake = CancellationTokenSource.CreateLinkedTokenSource(ct, host.Stopping);
        await ServeAsync((args, output, error, token) => commands.ExecuteAsync(args, host, output, error, token),
            token => host.Stopping.IsCancellationRequested ? Task.CompletedTask : host.StopAsync(token), intake.Token);
    }
    /// <summary>Serve one authenticated command per connection; the dispatcher owns command semantics.</summary>
    private async Task ServeAsync(Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>> execute, Func<CancellationToken,Task> shutdown, CancellationToken ct)
    {
        if (waiting == null) throw new InvalidOperationException(nameof(ClaimAsync));
        var active = new List<Task>(); using var intake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try {
            while (!intake.IsCancellationRequested) {
                var connected = waiting;
                try { await connected.WaitForConnectionAsync(intake.Token); }
                catch (IOException e) when ((e.HResult & 0xffff) is 109 or 232 or 233) { waiting = Listen(false); connected.Dispose(); continue; }
                waiting = Listen(false);
                foreach (var task in active.Where(t => t.IsCompleted).ToArray()) { await task; active.Remove(task); }
                if (active.Count >= MaximumConcurrentCommands) { connected.Dispose(); continue; }
                active.Add(HandleAsync(connected));
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally {
            try { await intake.CancelAsync(); await shutdown(CancellationToken.None); }
            finally { try { await Task.WhenAll(active); } finally { Dispose(); } }
        }
        async Task HandleAsync(NamedPipeServerStream pipe)
        {
            using (pipe) try {
                using var peer = AuthenticatedPeer(pipe);
                if (peer is null) return;
                await DispatchAsync(pipe, execute, intake.Token);
            } catch (Exception e) when (e is JsonException or InvalidDataException or IOException || e is OperationCanceledException && intake.IsCancellationRequested) { /* End this connection without replay. */ }
            catch (Exception e) when (e is not OutOfMemoryException) {
                Console.Error.WriteLine(Text.Get(TextKey.FailureDetail,
                    (TextArg.Name, e.GetType().FullName ?? e.GetType().Name), (TextArg.Message, e.Message)));
            }
        }
    }
    /// <summary>Read one command from an authenticated stream and send its output and terminal status.</summary>
    private static async Task DispatchAsync(Stream pipe, Func<string[],TextWriter,TextWriter,CancellationToken,Task<int>> execute, CancellationToken ct)
    {
        var request = await PipeFrame.ReadAsync(pipe, ct);
        if (request[0] != PipeFrame.Arguments) return;
        var args = JsonSerializer.Deserialize<string[]>(request.AsSpan(1));
        if (args == null || args.Any(a => a == null)) return;
        await PipeFrame.WriteAsync(pipe, new byte[] {PipeFrame.Accepted}, default);
        using var gate = new SemaphoreSlim(1);
        using var output = new Writer(pipe, PipeFrame.Output, gate); using var error = new Writer(pipe, PipeFrame.Error, gate);
        int code;
        try { code = await execute(args, output, error, CancellationToken.None); }
        catch (Exception failure) when (failure is not OutOfMemoryException) {
            await error.WriteLineAsync(failure.Message);
            code = 1;
        }
        var exit = new byte[1 + sizeof(int)]; exit[0] = PipeFrame.Exit;
        BinaryPrimitives.WriteInt32LittleEndian(exit.AsSpan(1), code); await PipeFrame.WriteAsync(pipe, exit, default);
    }
    /// <summary>Authenticate a local peer by its token SID and the Authenticode signer of its process image.</summary>
    internal static Process? AuthenticatedPeer(PipeStream pipe)
    {
        if (!pipe.IsConnected) return null;
        Process? peer = null;
        try {
            uint pid;
            if (pipe is NamedPipeServerStream) {
                var machine = new StringBuilder(256);
                if (GetNamedPipeClientComputerName(pipe.SafePipeHandle, machine, machine.Capacity) || Marshal.GetLastWin32Error() != 229 || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)) return null;
            } else if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid)) return null;
            peer = Process.GetProcessById(checked((int)pid)); using var own = Process.GetCurrentProcess();
            if (pid == own.Id || !OpenProcessToken(peer.SafeHandle, 8, out var token)) return null;
            using (token) using (var other = new WindowsIdentity(token.DangerousGetHandle())) using (var me = WindowsIdentity.GetCurrent()) {
                if (me.User is not { } selfSid || other.User is not { } peerSid || !selfSid.Equals(peerSid)) return null;
            }
            var actual = Image(peer);
            if (actual == null || !(string.Equals(Path.GetFileName(actual), ResourceNames.HostExecutable, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(actual), ResourceNames.CliExecutable, StringComparison.OrdinalIgnoreCase)) ||
                !SignedProduct(@"\\?\GLOBALROOT" + actual) || peer.HasExited || !pipe.IsConnected) return null;
            var authenticated = peer; peer = null; return authenticated;
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or IOException or CryptographicException) { return null; }
        finally { peer?.Dispose(); }
    }
    // Windows verifies the PE digest. Read the signer from that very trust state (not a separate certificate lookup).
    private static bool SignedProduct(string image)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = image };
        var ptr = Marshal.AllocHGlobal((int)file.Size);
        Marshal.StructureToPtr(file, ptr, false);
        var trust = new TrustData { Size=(uint)Marshal.SizeOf<TrustData>(), Ui=WtdUiNone, Choice=WtdChoiceFile, File=ptr, Action=WtdStateActionVerify, Flags=WtdOfflineUrlRetrieval };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try {
            var status = WinVerifyTrust(-1, ref action, ref trust);
            if (status is not (0 or 0x800B0109) || trust.State == 0) return false;
            var provider = WTHelperProvDataFromStateData(trust.State);
            var signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var leaf = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            if (leaf == 0) return false;
            using var cert = new X509Certificate2(Marshal.ReadIntPtr(leaf, IntPtr.Size));
            using var resource = typeof(HostServer).Assembly.GetManifestResourceStream("SyncShark.SignerIdentity.json");
            if (resource == null) return false;
            using var pins = JsonDocument.Parse(resource);
            var pin = pins.RootElement.GetProperty("roles").GetProperty("publisher");
            var spki = Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
            return cert.Subject == pin.GetProperty("subject").GetString() && cert.Issuer == cert.Subject &&
                pin.GetProperty("thumbprints").EnumerateArray().Any(x => string.Equals(x.GetString(),cert.Thumbprint,StringComparison.OrdinalIgnoreCase)) &&
                pin.GetProperty("spki").EnumerateArray().Any(x => string.Equals(x.GetString(),spki,StringComparison.OrdinalIgnoreCase));
        } finally {
            trust.Action=2; WinVerifyTrust(-1,ref action,ref trust);
            Marshal.DestroyStructure<TrustFile>(ptr); Marshal.FreeHGlobal(ptr);
        }
    }
    // wintrust.h WINTRUST_DATA flags and choices, each on its own declaration so the ratchet tests every name.
    private const uint WtdUiNone = 2;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdOfflineUrlRetrieval = 0x1000; // wintrust.h WTD_CACHE_ONLY_URL_RETRIEVAL
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct TrustFile { public uint Size; public string Path; public nint File,Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData {
        public uint Size; public nint Policy,Sip; public uint Ui,Revocation,Choice; public nint File;
        public uint Action; public nint State,Url; public uint Flags,Context; public nint Settings;
    }
    [DllImport("wintrust.dll",ExactSpelling=true)] private static extern uint WinVerifyTrust(nint window,ref Guid action,ref TrustData trust);
    [DllImport("wintrust.dll")] private static extern nint WTHelperProvDataFromStateData(nint state);
    [DllImport("wintrust.dll")] private static extern nint WTHelperGetProvSignerFromChain(nint provider,uint index,bool counter,uint counterIndex);
    [DllImport("wintrust.dll")] private static extern nint WTHelperGetProvCertFromChain(nint signer,uint index);
    // PROCESS_NAME_NATIVE identifies the loaded image without following a mutable active junction.
    private static string? Image(Process process) {
        int length = 32768; var path = new StringBuilder(length);
        return QueryFullProcessImageName(process.SafeHandle, 1, path, ref length) ? path.ToString() : null;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool GetNamedPipeClientComputerName(SafePipeHandle pipe, StringBuilder name, int length);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(SafeProcessHandle process, int rights, out SafeAccessTokenHandle token);
    public void Dispose() { waiting?.Dispose(); waiting = null; }
    private sealed class Writer(Stream pipe, byte kind, SemaphoreSlim gate) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        // UTF-8 needs at most 3 bytes per UTF-16 code unit (RFC 3629).
        private const int Utf8BytesPerUtf16Unit = 3;
        public override void Write(char value) => Write(value.ToString());
        public override void Write(string? value) => WriteAsync(value).GetAwaiter().GetResult();
        public override Task WriteLineAsync(string? value) => WriteAsync(value + NewLine);
        public override async Task WriteAsync(string? value) {
            if (string.IsNullOrEmpty(value)) return;
            await gate.WaitAsync();
            try {
                int offset = 0;
                while (offset < value.Length) {
                    int chars = Math.Min(value.Length - offset, PipeFrame.MaximumPayload / Utf8BytesPerUtf16Unit);
                    if (offset + chars < value.Length && char.IsHighSurrogate(value[offset + chars - 1])) chars--;
                    var message = new byte[1 + Encoding.GetByteCount(value.AsSpan(offset,chars))]; message[0] = kind;
                    Encoding.GetBytes(value.AsSpan(offset,chars), message.AsSpan(1));
                    await PipeFrame.WriteAsync(pipe, message, default); offset += chars;
                }
            } finally { gate.Release(); }
        }
    }
}
