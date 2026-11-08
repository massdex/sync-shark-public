using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark;

// Process-scoped window-message transport for the running-application mode. It reuses the existing
// same-product activation boundary: a hidden window in the owning process, a bounded WM_COPYDATA
// request, and a bounded WM_COPYDATA reply addressed only to the verified caller's own window. There
// is no new pipe, service, credential or durable namespace.
public static class SyncCliBridgeTransport
{
    internal const uint CopyDataMessage = 0x004A;
    internal static readonly IntPtr CopyDataKind = (IntPtr)(0x8000 + 114);
    // Unpublished process-local message variants, not durable OS registrations. A supplied HWND is
    // only an address: ask its verified owning window to attest the exact bytes before trusting them.
    internal static readonly IntPtr VerifyRequestKind = (IntPtr)(0x8000 + 115);
    internal static readonly IntPtr VerifyReplyKind = (IntPtr)(0x8000 + 116);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyData
    {
        public IntPtr Kind;
        public int Bytes;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string? ClassName;
        // WNDCLASSEXW ends with hIconSm; omitting it made cbSize too small, so RegisterClassEx failed
        // with ERROR_INVALID_PARAMETER and the bridge window was never created.
        public IntPtr IconSmall;
    }

    internal delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string windowName);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, ref CopyData data,
        uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")]
    internal static extern bool PostQuitMessage(int exitCode);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information,
        int length, out int returned);
    [DllImport("advapi32.dll")]
    private static extern bool EqualSid(IntPtr first, IntPtr second);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // OpenProcessToken takes TOKEN_* access rights, not PROCESS_* rights: TOKEN_QUERY (0x0008) is the
    // minimum access needed for GetTokenInformation(TokenUser). The previous constant was the process
    // access value PROCESS_QUERY_LIMITED_INFORMATION, which made every token open fail and therefore
    // made the same-user check refuse every peer.
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const uint SendMessageAbortIfHung = 0x0002;
    private const uint SendMessageBlock = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public IntPtr Window;
        public uint Value;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    // ---- identity -----------------------------------------------------------------------------

    // Discovery is not authentication. Another build (or an unrelated window) can have the same title;
    // do not let its position in the desktop's Z order hide the actual eligible receiver. Every external
    // candidate still passes the unchanged executable/session/token check and request attestation.
    internal static IntPtr FindReceiver(bool includeCurrentProcess, out string? refusal)
    {
        refusal = null;
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var window = IntPtr.Zero;
        var visited = new HashSet<IntPtr>();
        while (visited.Count < 256)
        {
            window = FindWindowEx(IntPtr.Zero, window, null, SyncCliBridgeProtocol.ReceiverWindowTitle);
            if (window == IntPtr.Zero || !visited.Add(window)) return IntPtr.Zero;
            GetWindowThreadProcessId(window, out var processId);
            if ((includeCurrentProcess && processId == (uint)Environment.ProcessId) ||
                IsTrustedPeer(processId, out _))
            {
                refusal = null;
                return window;
            }
            refusal ??= "A window named '" + SyncCliBridgeProtocol.ReceiverWindowTitle +
                        "' exists but is not a trusted Sync Shark runtime.";
        }
        refusal = "Sync Shark receiver discovery reached its safety bound; no request was sent.";
        return IntPtr.Zero;
    }

    // The peer must be the same product executable, in the same Windows session, under the same user
    // token as this process. A window title, a claimed pid or a process name alone is never enough.
    internal static bool IsTrustedPeer(uint processId, out string reason)
    {
        reason = string.Empty;
        if (processId == 0 || processId == (uint)Environment.ProcessId)
        {
            reason = "the peer is missing or is this process";
            return false;
        }
        try
        {
            using var peer = Process.GetProcessById((int)processId);
            if (peer.SessionId != Process.GetCurrentProcess().SessionId)
            {
                reason = "the peer is in a different Windows session";
                return false;
            }
            var peerPath = peer.MainModule?.FileName;
            var ownPath = Environment.ProcessPath;
            if (peerPath is null || ownPath is null ||
                !string.Equals(ResolveExecutable(peerPath), ResolveExecutable(ownPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "the peer is not the same product executable";
                return false;
            }
            if (!SameUser(peer, Process.GetCurrentProcess()))
            {
                reason = "the peer is not running under the same user token";
                return false;
            }
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or NotSupportedException or IOException or
                                          UnauthorizedAccessException)
        {
            reason = "the peer identity could not be observed: " + SyncCli.DescribeException(error);
            return false;
        }
    }

    private static string ResolveExecutable(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return PilotDirectoryLease.FinalPath(handle);
    }

    private static bool SameUser(Process first, Process second)
    {
        if (!OpenProcessToken(first.Handle, TokenQuery, out var firstToken)) return false;
        try
        {
            if (!OpenProcessToken(second.Handle, TokenQuery, out var secondToken)) return false;
            try
            {
                return EqualTokenUser(firstToken, secondToken);
            }
            finally { CloseHandle(secondToken); }
        }
        finally { CloseHandle(firstToken); }
    }

    private static bool EqualTokenUser(IntPtr first, IntPtr second)
    {
        GetTokenInformation(first, TokenUser, IntPtr.Zero, 0, out var firstLength);
        GetTokenInformation(second, TokenUser, IntPtr.Zero, 0, out var secondLength);
        if (firstLength <= 0 || secondLength <= 0) return false;
        var firstBuffer = Marshal.AllocHGlobal(firstLength);
        var secondBuffer = Marshal.AllocHGlobal(secondLength);
        try
        {
            if (!GetTokenInformation(first, TokenUser, firstBuffer, firstLength, out _)) return false;
            if (!GetTokenInformation(second, TokenUser, secondBuffer, secondLength, out _)) return false;
            var firstSid = Marshal.ReadIntPtr(firstBuffer);
            var secondSid = Marshal.ReadIntPtr(secondBuffer);
            return firstSid != IntPtr.Zero && secondSid != IntPtr.Zero && EqualSid(firstSid, secondSid);
        }
        finally
        {
            Marshal.FreeHGlobal(firstBuffer);
            Marshal.FreeHGlobal(secondBuffer);
        }
    }

    internal static bool TryReadAddress(string payload, out IntPtr address)
    {
        address = IntPtr.Zero;
        if (payload.Length is 0 or > 16) return false;
        return long.TryParse(payload, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value) &&
               (address = new IntPtr(value)) != IntPtr.Zero;
    }

    // The caller's own window handle travels in wParam, so the receiver can verify the sending process
    // and its reply window before any product work starts.
    internal static bool TrySendCopyData(IntPtr window, IntPtr fromWindow, string payload, uint timeoutMilliseconds,
        out string failure, IntPtr? kind = null)
    {
        failure = string.Empty;
        var bytes = Encoding.Unicode.GetBytes(payload + "\0");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var data = new CopyData { Kind = kind ?? CopyDataKind, Bytes = bytes.Length, Data = pointer };
            if (SendMessageTimeout(window, CopyDataMessage, fromWindow, ref data,
                    SendMessageAbortIfHung | SendMessageBlock, timeoutMilliseconds, out var result) == IntPtr.Zero ||
                result == IntPtr.Zero)
            {
                failure = "the peer did not accept the message within its bound";
                return false;
            }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    internal static string? TryReadCopyData(IntPtr data, IntPtr? expectedKind = null)
    {
        if (data == IntPtr.Zero) return null;
        try
        {
            var copy = Marshal.PtrToStructure<CopyData>(data);
            if (copy.Kind != (expectedKind ?? CopyDataKind) || copy.Bytes is < 2 or > SyncCliBridgeProtocol.MaximumPayloadBytes * 2 ||
                copy.Bytes % 2 != 0 || copy.Data == IntPtr.Zero) return null;
            var value = Marshal.PtrToStringUni(copy.Data, copy.Bytes / 2) ?? string.Empty;
            return value.EndsWith('\0') && !value[..^1].Contains('\0') ? value[..^1] : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}

// Hosts the running-application bridge in the process that owns the product runtime.
public sealed class SyncCliBridgeServer : IDisposable
{
    private readonly ISyncCliOperations _operations;
    private readonly SyncCliBridgeReplayCache _replay = new();
    private readonly Thread _thread;
    private readonly SyncCliBridgeTransport.WindowProcedure _procedure;
    private IntPtr _window;
    private volatile bool _stopping;
    private Exception? _failure;
    private uint _threadId;
    private readonly ConcurrentDictionary<(IntPtr Window, string Hash), byte> _outgoing = new();
    private readonly Action? _afterSuccessfulExit;
    private readonly Func<bool>? _updateExitPrepared;
    private readonly Func<Task>? _refreshPresentation;

    public SyncCliBridgeServer(ISyncCliOperations operations, Action? afterSuccessfulExit = null,
        Func<bool>? updateExitPrepared = null, Func<Task>? refreshPresentation = null)
    {
        _operations = operations;
        _afterSuccessfulExit = afterSuccessfulExit;
        _updateExitPrepared = updateExitPrepared;
        _refreshPresentation = refreshPresentation;
        _procedure = HandleMessage;
        _thread = new Thread(Run) { IsBackground = true, Name = "Sync Shark CLI bridge" };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        _thread.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_window == IntPtr.Zero && DateTime.UtcNow < deadline)
        {
            if (_failure is not null) throw _failure;
            Thread.Sleep(20);
        }
        if (_window == IntPtr.Zero) throw new InvalidOperationException(
            "The CLI bridge window could not be created in this process.");
    }

    private void Run()
    {
        var registered = false;
        var instance = SyncCliBridgeTransport.GetModuleHandle(null);
        try
        {
            var windowClass = new SyncCliBridgeTransport.WindowClass();
            SyncCliBridgeTransport.WindowProcedure procedure = _procedure;
            windowClass.Size = (uint)Marshal.SizeOf<SyncCliBridgeTransport.WindowClass>();
            windowClass.Procedure = Marshal.GetFunctionPointerForDelegate(procedure);
            windowClass.Instance = instance;
            windowClass.ClassName = "SyncSharkCliBridgeWindow";
            if (SyncCliBridgeTransport.RegisterClassEx(ref windowClass) == 0)
                throw new InvalidOperationException("The CLI bridge window class could not be registered.");
            registered = true;
            _window = SyncCliBridgeTransport.CreateWindowEx(0, windowClass.ClassName!,
                SyncCliBridgeProtocol.ReceiverWindowTitle, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero,
                windowClass.Instance, IntPtr.Zero);
            if (_window == IntPtr.Zero)
                throw new InvalidOperationException("The CLI bridge window could not be created.");
            _threadId = SyncCliBridgeTransport.GetCurrentThreadId();
            while (!_stopping && SyncCliBridgeTransport.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                SyncCliBridgeTransport.TranslateMessage(ref message);
                SyncCliBridgeTransport.DispatchMessage(ref message);
            }
        }
        catch (Exception error)
        {
            _failure = error;
        }
        finally
        {
            if (_window != IntPtr.Zero) SyncCliBridgeTransport.DestroyWindow(_window);
            _window = IntPtr.Zero;
            // Release only the class this receiver actually registered, on its
            // owning pump thread after destroying its window. Otherwise a normal
            // dispose/recreate in this process retains a stale delegate/class.
            if (registered && !SyncCliBridgeTransport.UnregisterClass("SyncSharkCliBridgeWindow", instance))
                _failure ??= new InvalidOperationException("The closed CLI bridge window class could not be released.");
        }
    }

    private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message != SyncCliBridgeTransport.CopyDataMessage)
            return SyncCliBridgeTransport.DefWindowProc(window, message, wParam, lParam);
        var proof = SyncCliBridgeTransport.TryReadCopyData(lParam, SyncCliBridgeTransport.VerifyReplyKind);
        if (proof is not null)
            return _outgoing.ContainsKey((wParam, PayloadHash(proof))) ? new IntPtr(1) : IntPtr.Zero;
        if (_stopping) return IntPtr.Zero;
        var payload = SyncCliBridgeTransport.TryReadCopyData(lParam);
        if (payload is null) return new IntPtr(1);
        var sender = wParam;
        ThreadPool.QueueUserWorkItem(_ => HandleRequest(sender, payload));
        return new IntPtr(1);
    }

    private async void HandleRequest(IntPtr senderWindow, string payload)
    {
        SyncCliBridgeRequest request;
        var callerConfirmed = false;
        try
        {
            SyncCliBridgeTransport.GetWindowThreadProcessId(senderWindow, out var senderProcess);
            if (!SyncCliBridgeTransport.IsTrustedPeer(senderProcess, out var reason))
                throw new SyncCliBridgeException("untrusted", "The bridge request was refused: " + reason + ".");
            request = SyncCliBridgeProtocol.ParseRequest(payload, DateTimeOffset.UtcNow);
            if (!SyncCliBridgeTransport.TryReadAddress(request.ReplyWindow, out var replyWindow))
                throw new SyncCliBridgeException("malformed", "The reply window handle is not valid.");
            SyncCliBridgeTransport.GetWindowThreadProcessId(replyWindow, out var replyProcess);
            if (replyProcess != senderProcess)
                throw new SyncCliBridgeException("untrusted",
                    "The reply window does not belong to the requesting process.");
            // wParam is caller-controlled. Owning a genuine client HWND is not proof that this
            // payload came from it. Its procedure must confirm the exact outstanding invocation.
            if (!SyncCliBridgeTransport.TrySendCopyData(replyWindow, _window, payload, 5_000, out _,
                    SyncCliBridgeTransport.VerifyRequestKind))
                throw new SyncCliBridgeException("untrusted", "The client did not confirm this exact request.");
            callerConfirmed = true;
            if (!_replay.TryAccept(request.Nonce, DateTimeOffset.UtcNow))
                throw new SyncCliBridgeException("duplicate",
                    "This request correlation value was already accepted; the request was not run again.");
            Reply(replyWindow, request.Nonce, "ack", true, 0, string.Empty, null);
        }
        catch (SyncCliBridgeException error)
        {
            // A rejected request never runs product work. A reply is returned only to the verified caller.
            if (callerConfirmed) TryReplyRejected(senderWindow, payload, error);
            return;
        }
        catch (Exception error)
        {
            if (callerConfirmed) TryReplyRejected(senderWindow, payload,
                new SyncCliBridgeException("malformed", SyncCli.DescribeException(error)));
            return;
        }

        if (!SyncCliBridgeTransport.TryReadAddress(request.ReplyWindow, out var target)) return;
        try
        {
            var output = new StringWriter();
            using var progress = new SyncCliBridgeProgressWriter(text =>
                Reply(target, request.Nonce, "progress", true, 0, text, null));
            var exitCode = await SyncCli.ExecuteAsync(request.Arguments.ToArray(), output, progress,
                () => _operations, CancellationToken.None);
            try { await progress.CompleteAsync(); }
            catch (Exception error)
            {
                // Optional live delivery cannot retract an already retained
                // command result or make its completed effects look retryable.
                Console.Error.WriteLine("Sync Shark: CLI live-log delivery failed; retained Activity is still available: " +
                    SyncCli.DescribeException(error));
            }
            var command = CommandForPresentationRefresh(request.Arguments);
            var ownerExitPrepared = exitCode == 0 && (command == "app exit" ||
                (command is "updates apply" or "app uninstall") && _updateExitPrepared?.Invoke() == true);
            // UI5: only a genuine mutation refreshes the window presentation. The registry's own Mutating mark
            // is that one classification, resolved through the existing parser, so a read-only State command -
            // status, jobs status, activity list, runs list, queue list or validation/help - executes and
            // returns without touching the display. A committed mutation still refreshes even when it reported
            // a partial failure, and presentation must never turn a committed command into a failed/retryable
            // CLI effect.
            // A completed exit checkpoint has already closed/disposed the window.
            // Reloading its potentially large history delays the result and owner
            // shutdown for no visible benefit. Failed exits still refresh normally.
            // The authoritative command and its durable outcome are already complete.
            // Return them before read-only presentation work: history/review reloads
            // must not extend a command deadline or make a safe failure look hung.
            // This does not move the application's pre-exit checkpoint: ExecuteAsync
            // still awaited it, and shutdown still happens only after this reply.
            Reply(target, request.Nonce, "result", exitCode == 0, exitCode, output.ToString(), null);
            if (ownerExitPrepared)
            {
                _afterSuccessfulExit?.Invoke();
                return;
            }
            if (!ownerExitPrepared && _refreshPresentation is not null && MutatesProductState(request.Arguments))
            {
                try { await _refreshPresentation(); }
                catch (Exception error)
                { Console.Error.WriteLine("Sync Shark: Display refresh pending after the command completed: " +
                    SyncCli.DescribeException(error)); }
            }
        }
        catch (Exception error)
        {
            Reply(target, request.Nonce, "result", false, (int)SyncCliExitCode.Failed, string.Empty,
                SyncCli.DescribeException(error));
        }
    }

    private void TryReplyRejected(IntPtr senderWindow, string payload, SyncCliBridgeException error)
    {
        try
        {
            var request = SyncCliBridgeProtocol.ParseRequest(payload, DateTimeOffset.UtcNow);
            if (!SyncCliBridgeTransport.TryReadAddress(request.ReplyWindow, out var reply)) return;
            SyncCliBridgeTransport.GetWindowThreadProcessId(reply, out var replyProcess);
            SyncCliBridgeTransport.GetWindowThreadProcessId(senderWindow, out var senderProcess);
            if (replyProcess != senderProcess) return;
            Reply(reply, request.Nonce, "result", false, (int)SyncCliExitCode.Usage, string.Empty,
                error.Code + ": " + error.Message);
        }
        catch (Exception)
        {
            // A rejected or unparseable request that has no safe reply target is simply dropped.
        }
    }

    private void Reply(IntPtr window, string nonce, string kind, bool ok, int exitCode, string text,
        string? error)
    {
        var reply = new SyncCliBridgeReply(kind, nonce, ok, exitCode, text, error);
        var payload = kind == "progress" ? SyncCliBridgeProtocol.EncodeProgressReply(reply)
            : SyncCliBridgeProtocol.EncodeReply(reply);
        if (payload.Length >= SyncCliBridgeProtocol.MaximumPayloadBytes)
            payload = SyncCliBridgeProtocol.EncodeReply(new SyncCliBridgeReply("result", nonce, false,
                (int)SyncCliExitCode.Failed, string.Empty,
                "The result exceeds the running-app response limit. Use a smaller --limit or narrower target."));
        // The client verifies this payload by querying this server's window, not by trusting the
        // alleged sender HWND or correlation alone. Retain only during the synchronous delivery.
        var key = (window, PayloadHash(payload));
        _outgoing.TryAdd(key, 0);
        try { SyncCliBridgeTransport.TrySendCopyData(window, _window, payload, 5_000, out _); }
        finally { _outgoing.TryRemove(key, out _); }
    }

    private static string PayloadHash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    // UI5: the registry's own Mutating mark is the one classification of a genuine mutation, so no second
    // command list is created and a read-only command can never be reclassified by this file.
    private static bool MutatesProductState(IReadOnlyList<string> arguments)
    {
        try { return SyncCliRegistry.MutatesProductState(SyncCliParser.Parse(arguments.ToArray())); }
        catch (SyncCliException) { return false; }
    }

    // The exact registered command of this request, read through the existing parser. A command the registry
    // cannot parse here has already reported its own usage failure and performed no effect, so it is classified
    // from its candidate name and therefore never refreshes the presentation.
    private static string CommandForPresentationRefresh(IReadOnlyList<string> arguments)
    {
        try { return SyncCliParser.Parse(arguments.ToArray()).Command; }
        catch (SyncCliException) { return SyncCliParser.CommandCandidate(arguments.ToArray()); }
    }

    public void Dispose()
    {
        _stopping = true;
        // WM_QUIT must be posted to the pump thread, not the disposing thread: PostQuitMessage posts to
        // the caller's own queue, which left the pump blocked and (once the loop exited for any reason)
        // destroyed the receiver window while callers still expected it.
        if (_threadId != 0) SyncCliBridgeTransport.PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        // _window can already be zero while class cleanup is still finishing.
        // Wait for that ordinary cleanup, never terminate the receiver thread.
        if (_thread.IsAlive && _thread.ManagedThreadId != Environment.CurrentManagedThreadId)
            _thread.Join(TimeSpan.FromSeconds(5));
    }

}

// Preserve every producer-stamped line, but amortize the unchanged per-frame peer
// identity and exact-payload challenge. A bounded queue supplies backpressure;
// logging cannot grow without limit or silently drop individual file outcomes.
internal sealed class SyncCliBridgeProgressWriter : TextWriter
{
    // Even an entirely JSON-escaped batch stays below the transport's UTF-16
    // byte limit, including the reply envelope and its correlation value.
    internal const int MaximumBatchCharacters = 4_000;
    private readonly object _gate = new();
    private readonly StringBuilder _pending = new();
    private readonly System.Threading.Channels.Channel<string> _lines =
        System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(256)
        { SingleReader = true, FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
    private readonly Task _delivery;
    private bool _completed;
    public override Encoding Encoding => Encoding.Unicode;

    internal SyncCliBridgeProgressWriter(Action<string> send) => _delivery = Task.Run(async () =>
    {
        var batch = new StringBuilder();
        try
        {
            while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // Coalesce sparse output for at most 100ms, but keep draining a
                // full batch immediately. Sleeping before every channel drain
                // makes a busy producer pay the sparse-output delay repeatedly.
                using var coalescing = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                while (true)
                {
                    while (_lines.Reader.TryRead(out var line))
                    {
                        if (batch.Length > 0 && batch.Length + line.Length > MaximumBatchCharacters)
                        {
                            send(batch.ToString()); batch.Clear();
                            coalescing.CancelAfter(TimeSpan.FromMilliseconds(100));
                        }
                        batch.Append(line);
                        if (batch.Length == MaximumBatchCharacters)
                        {
                            send(batch.ToString()); batch.Clear();
                            coalescing.CancelAfter(TimeSpan.FromMilliseconds(100));
                        }
                    }
                    try
                    {
                        if (!await _lines.Reader.WaitToReadAsync(coalescing.Token).ConfigureAwait(false)) break;
                    }
                    catch (OperationCanceledException) when (coalescing.IsCancellationRequested) { break; }
                }
                if (batch.Length > 0) { send(batch.ToString()); batch.Clear(); }
            }
        }
        catch (Exception error) { _lines.Writer.TryComplete(error); throw; }
    });

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_gate)
        {
            if (_completed) throw new ObjectDisposedException(nameof(SyncCliBridgeProgressWriter));
            foreach (var character in value)
            {
                _pending.Append(character);
                if (character == '\n' || _pending.Length == MaximumBatchCharacters) EnqueuePending();
            }
        }
    }

    // Keep concurrent producers' complete lines together. TextWriter's default
    // implementation writes the text and terminator in separate calls.
    public override void WriteLine(string? value)
    {
        lock (_gate) { Write(value); Write('\n'); }
    }

    public override void Flush()
    {
        lock (_gate) { if (!_completed) EnqueuePending(); }
    }

    private void EnqueuePending()
    {
        if (_pending.Length == 0) return;
        _lines.Writer.WriteAsync(_pending.ToString()).AsTask().GetAwaiter().GetResult();
        _pending.Clear();
    }

    internal Task CompleteAsync()
    {
        lock (_gate)
        {
            if (!_completed)
            {
                try { EnqueuePending(); }
                catch (System.Threading.Channels.ChannelClosedException)
                { /* CompleteAsync returns the pump's original delivery failure below. */ }
                finally { _completed = true; _lines.Writer.TryComplete(); }
            }
        }
        return _delivery;
    }

    protected override void Dispose(bool disposing)
    {
        bool needsCompletion;
        lock (_gate) needsCompletion = !_completed;
        if (disposing && needsCompletion) CompleteAsync().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
}

public sealed class SyncCliBridgeClient : IDisposable
{
    private readonly SyncCliBridgeTransport.WindowProcedure _procedure;
    private readonly ManualResetEventSlim _completed = new(false);
    private readonly ConcurrentQueue<string> _progress = new();
    private readonly Thread _thread;
    private string? _result;
    private string? _error;
    private int _exitCode;
    private bool _acknowledged;
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    private IntPtr _window;
    private uint _threadId;
    private IntPtr _receiver;
    private string? _requestPayload;
    private int _disposed;

    public SyncCliBridgeClient()
    {
        _procedure = HandleMessage;
        _thread = new Thread(Run) { IsBackground = true, Name = "Sync Shark CLI reply window" };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
    }

    public string Nonce => _nonce;

    internal static TimeSpan ResolveReplyWaitTimeout(SyncCliInvocation invocation, TimeSpan timeout)
    {
        // The authority owns the command deadline and its safe cancellation. A client must not
        // disappear at the generic 30-minute wait while that longer, explicitly bounded job runs.
        // Leave a finite interval for the authority's cancellation checkpoint and terminal reply.
        if (invocation.Value("--timeout-seconds") is { } seconds)
        {
            var commandWait = TimeSpan.FromSeconds(double.Parse(seconds,
                System.Globalization.CultureInfo.InvariantCulture)) + TimeSpan.FromMinutes(2);
            if (commandWait > timeout) timeout = commandWait;
        }
        if (invocation.Command == "app exit" && timeout < TimeSpan.FromMinutes(2))
            timeout = TimeSpan.FromMinutes(2);
        return timeout;
    }

    // Returns false when no running application owns the product runtime, so the caller can fall back to
    // the direct path. Throws only when a running owner refused or did not answer an accepted request.
    public bool TryInvoke(IReadOnlyList<string> arguments, TextWriter standardError,
        TimeSpan timeout, out int exitCode, out string text, out string? error) =>
        TryInvoke(arguments, standardError, timeout, CancellationToken.None, out exitCode, out text, out error);

    // A waiting client can stop immediately when the operator presses Ctrl+C. The request keeps running inside
    // the owning application - this only stops waiting, so nothing is cancelled or replayed.
    public bool TryInvoke(IReadOnlyList<string> arguments, TextWriter standardError,
        TimeSpan timeout, CancellationToken cancellationToken,
        out int exitCode, out string text, out string? error)
    {
        exitCode = (int)SyncCliExitCode.Failed;
        text = string.Empty;
        error = null;
        var invocation = SyncCliParser.Parse(arguments.ToArray());
        timeout = ResolveReplyWaitTimeout(invocation, timeout);
        var target = FindReceiver(out var refusal);
        if (target == IntPtr.Zero)
        {
            if (refusal is not null) throw new SyncCliOperationException(refusal);
            return false;
        }
        _receiver = target;
        _thread.Start();
        if (!SpinWait.SpinUntil(() => _window != IntPtr.Zero, TimeSpan.FromSeconds(10)))
            throw new SyncCliOperationException("The CLI reply window could not be created in this process.");
        var request = SyncCliBridgeProtocol.EncodeRequest(new SyncCliBridgeRequest("invoke", _nonce,
            DateTimeOffset.UtcNow, arguments.ToArray(), _window.ToInt64().ToString("x",
                System.Globalization.CultureInfo.InvariantCulture)));
        _requestPayload = request;
        if (!SyncCliBridgeTransport.TrySendCopyData(target, _window, request, 5_000, out var failure))
        {
            // The request may have been accepted and be running, so this is reported as unknown rather
            // than retried or replayed.
            throw new SyncCliOperationException("The running Sync Shark application did not accept the request: " +
                failure + ". The request state is unknown; check 'status' before retrying a mutation.");
        }
        var exitRequested = invocation.Command == "app exit";
        // Graceful exit may wait for the bounded in-flight checkpoint. It is not
        // a normal 15-second read request, and timing out must not say it failed.
        var deadline = DateTime.UtcNow + timeout;
        var nextExitNotice = DateTime.UtcNow;
        int wait;
        do
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) { wait = WaitHandle.WaitTimeout; break; }
            wait = WaitHandle.WaitAny([_completed.WaitHandle, cancellationToken.WaitHandle],
                remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100));
            while (_progress.TryDequeue(out var progress)) standardError.WriteLine(progress);
            if (exitRequested && _acknowledged && DateTime.UtcNow >= nextExitNotice)
            {
                standardError.WriteLine("Sync Shark: Exit accepted; waiting for the active operation's safe checkpoint and retained state to close. No process is being forcibly terminated.");
                nextExitNotice = DateTime.UtcNow.AddSeconds(5);
            }
            if (wait == 1) throw new OperationCanceledException(cancellationToken);
        } while (wait == WaitHandle.WaitTimeout && DateTime.UtcNow < deadline);
        if (wait != 0)
        {
            if (exitRequested && _acknowledged)
                throw new SyncCliBusyException("Exit is still pending its safe checkpoint. This is not a failed or cancelled exit; the application retains the request. Check status before issuing another command. Request: " + _nonce);
            if (_acknowledged)
                throw new SyncCliOperationException("The running Sync Shark application accepted this request " +
                    "(" + _nonce + ") but did not finish within " + timeout.TotalSeconds +
                    " second(s). The job may still be running; check 'status' or 'runs list' before retrying.");
            throw new SyncCliOperationException("The running Sync Shark application did not acknowledge this " +
                "request within " + timeout.TotalSeconds + " second(s). Its state is unknown; retrying a mutation " +
                "could repeat an effect, so verify state first.");
        }
        while (_progress.TryDequeue(out var line)) standardError.WriteLine(line);
        exitCode = _exitCode;
        text = _result ?? string.Empty;
        error = _error;
        if (exitCode == 0 && SyncCliParser.Parse(arguments.ToArray()).Command is "app exit" or "updates apply" or "app uninstall")
        {
            SyncCliBridgeTransport.GetWindowThreadProcessId(target, out var ownerId);
            if (ownerId != 0)
            {
                try
                {
                    using var owner = Process.GetProcessById((int)ownerId);
                    if (!owner.WaitForExit(10_000))
                        throw new SyncCliBusyException("The exit checkpoint completed; process shutdown is still pending. No process was terminated and the exit request must not be replayed; check status.");
                }
                catch (ArgumentException) { /* The observed owner already exited. */ }
            }
        }
        return true;
    }

    private static IntPtr FindReceiver(out string? refusal)
    {
        return SyncCliBridgeTransport.FindReceiver(includeCurrentProcess: false, out refusal);
    }

    private void Run()
    {
        var windowClass = new SyncCliBridgeTransport.WindowClass();
        SyncCliBridgeTransport.WindowProcedure procedure = _procedure;
        windowClass.Size = (uint)Marshal.SizeOf<SyncCliBridgeTransport.WindowClass>();
        windowClass.Procedure = Marshal.GetFunctionPointerForDelegate(procedure);
        windowClass.Instance = SyncCliBridgeTransport.GetModuleHandle(null);
        windowClass.ClassName = "SyncSharkCliReplyWindow" + _nonce;
        if (SyncCliBridgeTransport.RegisterClassEx(ref windowClass) == 0) return;
            _window = SyncCliBridgeTransport.CreateWindowEx(0, windowClass.ClassName!, "Sync Shark CLI reply " + _nonce,
                0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) return;
            _threadId = SyncCliBridgeTransport.GetCurrentThreadId();
        while (SyncCliBridgeTransport.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            SyncCliBridgeTransport.TranslateMessage(ref message);
            SyncCliBridgeTransport.DispatchMessage(ref message);
        }
        SyncCliBridgeTransport.DestroyWindow(_window);
        _window = IntPtr.Zero;
    }

    private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message != SyncCliBridgeTransport.CopyDataMessage)
            return SyncCliBridgeTransport.DefWindowProc(window, message, wParam, lParam);
        if (Volatile.Read(ref _disposed) != 0) return IntPtr.Zero;
        var proof = SyncCliBridgeTransport.TryReadCopyData(lParam, SyncCliBridgeTransport.VerifyRequestKind);
        if (proof is not null)
            return wParam == _receiver && string.Equals(proof, _requestPayload, StringComparison.Ordinal)
                ? new IntPtr(1) : IntPtr.Zero;
        var payload = SyncCliBridgeTransport.TryReadCopyData(lParam);
        if (payload is null) return new IntPtr(1);
        if (wParam != _receiver) return IntPtr.Zero;
        SyncCliBridgeTransport.GetWindowThreadProcessId(_receiver, out var receiverProcess);
        if (!SyncCliBridgeTransport.IsTrustedPeer(receiverProcess, out _) ||
            !SyncCliBridgeTransport.TrySendCopyData(_receiver, _window, payload, 5_000, out _,
                SyncCliBridgeTransport.VerifyReplyKind)) return IntPtr.Zero;
        try
        {
            var reply = SyncCliBridgeProtocol.ParseReply(payload, _nonce);
            switch (reply.Kind)
            {
                case "ack":
                    _acknowledged = true;
                    break;
                case "progress":
                    // Registry progress already carries its producer timestamp.
                    // Do not replace it with arrival time or repeat the prefix.
                    _progress.Enqueue(reply.Text ?? string.Empty);
                    break;
                default:
                    _acknowledged = true;
                    _exitCode = reply.ExitCode;
                    _result = reply.Text;
                    _error = reply.Error;
                    _completed.Set();
                    SyncCliBridgeTransport.PostQuitMessage(0);
                    break;
            }
        }
        catch (SyncCliBridgeException)
        {
            // A reply that does not match this correlation value is ignored, never acted on.
        }
        return new IntPtr(1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_threadId != 0) SyncCliBridgeTransport.PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        // Never dispose the event while the callback might still signal it. If the bounded join
        // cannot finish, leave this small object for process cleanup rather than crash that pump.
        if ((_thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0 ||
            _thread.Join(TimeSpan.FromSeconds(6))) _completed.Dispose();
    }
}
