using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

internal static partial class SyncDiscoveryClient
{
    internal const string WorkerPath = @"C:\Program Files\Massdex\SyncShark\SyncSharkDiscovery.exe";
    internal const string ManifestPath = @"C:\Program Files\Massdex\SyncShark\discovery-manifest.json";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _retryAfter;
    internal static string Status { get; private set; } = "Set Up; Checked Automatically When Needed";
    internal static bool IsInstalled => OperatingSystem.IsWindows() && File.Exists(WorkerPath) && File.Exists(ManifestPath);

    internal static async Task ResetAfterSetupAsync()
    {
        await Gate.WaitAsync();
        try { _journalRetryAfter = DateTimeOffset.MinValue; _retryAfter = DateTimeOffset.MinValue; Status = IsInstalled ? "Set Up; Checked Automatically When Needed" : "Not Set Up; Ordinary Scanning Is Active"; }
        finally { Gate.Release(); }
    }

    internal static async Task<SyncDiscoveryResult?> TryReadAsync(IReadOnlyList<SyncDiscoveryRoot> roots, CancellationToken cancellationToken,
        Func<SyncDiscoveryProgress, Task>? onProgress = null)
    {
        if (!OperatingSystem.IsWindows() || !IsInstalled || DateTimeOffset.UtcNow < _retryAfter) return null;
        // Maintenance must not delay a sync: use ordinary scanning while the fixed worker is busy.
        if (!await Gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            if (DateTimeOffset.UtcNow < _retryAfter) return null;
            Status = "Starting File Discovery";
            var result = await ReadWithDeadlineAsync((token, activity) => Task.Run(() => OperatingSystem.IsWindows()
                ? ReadWindowsAsync(roots, token, activity, onProgress)
                : throw new PlatformNotSupportedException(), token), cancellationToken);
            Status = "Fast Discovery Is Ready"; return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { Status = "Discovery Timed Out; Ordinary Scanning Is Active"; _retryAfter = DateTimeOffset.UtcNow.AddMinutes(5); return null; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { Status = "Discovery Cancelled; Ordinary Scanning Is Available"; throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or COMException or
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException or ArgumentException or FormatException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { Status = "Discovery Is Unavailable; Ordinary Scanning Is Active"; _retryAfter = DateTimeOffset.UtcNow.AddMinutes(5); return null; }
        finally { Gate.Release(); }
    }

    // The live client and deterministic clock checks use the same timer wiring.
    // Only tests supply a clock; no provider/user input can change these limits.
    internal static async Task<SyncDiscoveryResult> ReadWithDeadlineAsync(
        Func<CancellationToken, Action, Task<SyncDiscoveryResult>> read,
        CancellationToken cancellationToken, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        using var hardLimit = new CancellationTokenSource(SyncDiscoveryProtocol.MaximumDuration + TimeSpan.FromSeconds(15), clock);
        using var idleLimit = new CancellationTokenSource(SyncDiscoveryProtocol.IdleTimeout, clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hardLimit.Token, idleLimit.Token);
        return await read(timeout.Token, () => idleLimit.CancelAfter(SyncDiscoveryProtocol.IdleTimeout));
    }

    [SupportedOSPlatform("windows")]
    private static async Task<SyncDiscoveryResult> ReadWindowsAsync(IReadOnlyList<SyncDiscoveryRoot> roots, CancellationToken token,
        Action onActivity, Func<SyncDiscoveryProgress, Task>? onProgress)
    {
        // Validate before asking the fixed task to run. This never registers,
        // elevates, edits or repairs a task and never passes task parameters.
        var nonce = SyncDiscoveryProtocol.NewNonce(); var request = SyncDiscoveryProtocol.Request(roots, nonce);
        var preflight = Native.Prepare(out var prepared);
        if (preflight != 0) throw new Win32Exception((int)preflight);
        IntPtr pipe = IntPtr.Zero, lease = IntPtr.Zero;
        try
        {
            token.ThrowIfCancellationRequested(); RunExistingTask(); var started = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested(); var error = Native.Open(out pipe, out lease);
                if (error == 0) break;
                if (error is not (2 or 231) || started.Elapsed > TimeSpan.FromSeconds(15)) throw new Win32Exception((int)error);
                await Task.Delay(100, token);
            }
            using var handle = new SafeFileHandle(pipe, ownsHandle: false);
            await using var stream = new FileStream(handle, FileAccess.ReadWrite, 65536, isAsync: true);
            await stream.WriteAsync(request, token); await stream.FlushAsync(token);
            return await SyncDiscoveryProtocol.ReadAsync(stream, roots, nonce, token, acknowledgePipe: true,
                onProgress: progress => Status = progress.Description + $"\nIndex Records Read: {progress.RecordsRead:N0}",
                onActivity: onActivity, onProgressAsync: onProgress);
        }
        finally { if (lease != IntPtr.Zero) Native.Close(lease); Native.Close(prepared); }
    }

    [SupportedOSPlatform("windows")]
    private static void RunExistingTask()
    {
        object? service = null, folder = null, task = null, definition = null, actions = null, action = null,
            principal = null, settings = null, triggers = null, running = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Windows Task Scheduler is unavailable.");
            service = Activator.CreateInstance(type) ?? throw new IOException("Windows Task Scheduler is unavailable.");
            ((dynamic)service).Connect(); folder = ((dynamic)service).GetFolder(@"\"); task = ((dynamic)folder).GetTask("SyncSharkDiscovery");
            definition = ((dynamic)task).Definition; actions = ((dynamic)definition).Actions;
            principal = ((dynamic)definition).Principal; settings = ((dynamic)definition).Settings; triggers = ((dynamic)definition).Triggers;
            var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("The interactive user is unavailable.");
            ValidateTaskSecurity((string)((dynamic)task).GetSecurityDescriptor(7), user);
            ValidateTaskUser((string?)((dynamic)principal).UserId, user);
            if (!(bool)((dynamic)task).Enabled || (int)((dynamic)actions).Count != 1 || (int)((dynamic)triggers).Count != 0 ||
                (int)((dynamic)principal).RunLevel != 1 || (int)((dynamic)principal).LogonType != 3 ||
                !(bool)((dynamic)settings).AllowDemandStart || (int)((dynamic)settings).MultipleInstances != 2 ||
                (bool)((dynamic)settings).DisallowStartIfOnBatteries || (bool)((dynamic)settings).StopIfGoingOnBatteries ||
                (bool)((dynamic)settings).RunOnlyIfIdle || (bool)((dynamic)settings).RunOnlyIfNetworkAvailable ||
                System.Xml.XmlConvert.ToTimeSpan((string)((dynamic)settings).ExecutionTimeLimit) != SyncDiscoveryProtocol.TaskDuration)
                throw new IOException("Discovery task identity or execution limits changed.");
            action = ((dynamic)actions).Item(1);
            if ((int)((dynamic)action).Type != 0 || !string.Equals((string)((dynamic)action).Path, WorkerPath, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty((string)((dynamic)action).Arguments) ||
                !string.Equals((string)((dynamic)action).WorkingDirectory, Path.GetDirectoryName(WorkerPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Discovery task action changed.");
            running = ((dynamic)task).Run(null);
        }
        finally
        {
            foreach (var item in new[] { running, triggers, settings, principal, action, actions, definition, task, folder, service })
                if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidateTaskUser(string? taskUser, string expectedSid)
    {
        if (string.IsNullOrWhiteSpace(taskUser)) throw new IOException("Discovery task has no Windows account.");
        // Task Scheduler returns an account name even when registration supplied
        // its SID. Resolve through Windows; never accept a display-name match.
        SecurityIdentifier actual;
        try
        {
            actual = taskUser.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(taskUser)
                : (SecurityIdentifier)new NTAccount(taskUser).Translate(typeof(SecurityIdentifier));
        }
        catch (Exception error) when (error is IdentityNotMappedException or ArgumentException)
        { throw new IOException("Discovery task Windows account cannot be verified.", error); }
        if (actual.Value != expectedSid) throw new IOException("Discovery task belongs to a different Windows account.");
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidateTaskSecurity(string sddl, string user)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        if (descriptor.Owner?.Value != "S-1-5-32-544" || descriptor.Group?.Value != "S-1-5-32-544" ||
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 || descriptor.DiscretionaryAcl?.Count != 3)
            throw new IOException("Discovery task permissions changed.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in descriptor.DiscretionaryAcl)
        {
            if (entry is not CommonAce { AceQualifier: AceQualifier.AccessAllowed, AceFlags: AceFlags.None } ace || ace.IsCallback ||
                !seen.Add(ace.SecurityIdentifier.Value)) throw new IOException("Discovery task has unexpected permissions.");
            var sid = ace.SecurityIdentifier.Value; var mask = unchecked((uint)ace.AccessMask);
            if (sid is "S-1-5-18" or "S-1-5-32-544")
            { if (mask is not (0x10000000 or 0x001f01ff)) throw new IOException("Discovery task administrator permissions changed."); }
            else if (sid != user || mask is not (0xa0000000 or 0x001200a9)) throw new IOException("Discovery task is writable or belongs to another user.");
        }
    }
    private static class Native
    {
        [DllImport("SyncShark.Explorer.dll", EntryPoint = "SyncSharkPrepareDiscovery", ExactSpelling = true)]
        internal static extern uint Prepare(out IntPtr lease);
        [DllImport("SyncShark.Explorer.dll", EntryPoint = "SyncSharkOpenDiscoveryPipe", ExactSpelling = true)]
        internal static extern uint Open(out IntPtr pipe, out IntPtr lease);
        [DllImport("SyncShark.Explorer.dll", EntryPoint = "SyncSharkCloseDiscoveryPipe", ExactSpelling = true)]
        internal static extern void Close(IntPtr lease);
    }
}
