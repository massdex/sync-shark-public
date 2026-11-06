using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Massdex.SyncShark;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.V2;

namespace SyncShark.UiFixture;

/// <summary>Exercises the real CLI launcher with this fixture as its controlled supervisor child.</summary>
internal static class EngineStartupReadinessChecks
{
    internal const string RendezvousVariable = "SYNCSHARK_UI_STARTUP_RENDEZVOUS";

    internal static async Task<int> ChildAsync(string root, string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var rendezvous = new NamedPipeClientStream(".", Path.GetFileName(root), PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await rendezvous.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(rendezvous, leaveOpen: true);
        using var writer = new StreamWriter(rendezvous, leaveOpen: true) { AutoFlush = true };
        using var readiness = AutoSyncProcess.TakeReadinessPipe();
        await writer.WriteLineAsync(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await writer.WriteLineAsync(mode);
        var release = await reader.ReadLineAsync(deadline.Token);
        if (release != "ready") return 0;
        using var isolated = SyncSharkRuntimePaths.OverrideProductRootForTest(root);
        using var host = new NamedPipeServerStream(
            AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile) + ".engine",
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        EngineHostEntry.WriteReady(readiness);
        await host.WaitForConnectionAsync(deadline.Token);
        await new EngineHostServer((arguments, _, _, _) =>
        {
            Require(arguments.SequenceEqual(new[] { "--cli", "engine", "host", "start", "--yes" }),
                "The ready host receives the exact registered start command");
            return Task.FromResult((int)SyncCliExitCode.Success);
        }).ServeAsync(host, deadline.Token);
        return 0;
    }

    internal static async Task<int> RunAsync()
    {
        var previous = Environment.GetEnvironmentVariable(RendezvousVariable);
        try
        {
            foreach (var ready in new[] { true, false })
            {
                var root = Path.Combine(Path.GetTempPath(), "SyncShark.StartupProbe." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                using var isolated = SyncSharkRuntimePaths.OverrideProductRootForTest(root);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var rendezvous = new NamedPipeServerStream(Path.GetFileName(root), PipeDirection.InOut,
                    1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                Process? child = null;
                Task<int?>? starting = null;
                try
                {
                    Environment.SetEnvironmentVariable(RendezvousVariable, root);
                    starting = EngineHostEntry.TryCliAsync(
                        ["--cli", "engine", "host", "start", "--yes"], TextWriter.Null, TextWriter.Null, deadline.Token);
                    await rendezvous.WaitForConnectionAsync(deadline.Token);
                    using var reader = new StreamReader(rendezvous, leaveOpen: true);
                    using var writer = new StreamWriter(rendezvous, leaveOpen: true) { AutoFlush = true };
                    var pid = int.Parse((await reader.ReadLineAsync(deadline.Token))!, System.Globalization.CultureInfo.InvariantCulture);
                    child = Process.GetProcessById(pid);
                    // A process obtained by id owns no handle, so ExitCode below needs the watch enabled first.
                    child.EnableRaisingEvents = true;
                    Require(child.MainModule?.FileName == Environment.ProcessPath,
                        "The launcher starts only the UiFixture executable");
                    Require(await reader.ReadLineAsync(deadline.Token) == "--engine-supervisor",
                        "The real CLI start launches a supervisor, never a bare host");
                    Require(!starting.IsCompleted, "CLI start remains pending before the child publishes readiness");
                    await writer.WriteLineAsync(ready ? "ready" : "exit");
                    var result = await starting.WaitAsync(deadline.Token);
                    Require(ready ? result == (int)SyncCliExitCode.Success : result == (int)SyncCliExitCode.Failed,
                        ready ? "CLI start succeeds only after readiness and the host reply" :
                            "A child exiting zero without readiness is a failed start");
                    await child.WaitForExitAsync(deadline.Token);
                    Require(child.ExitCode == 0, "The controlled child exits cleanly");
                }
                finally
                {
                    await deadline.CancelAsync();
                    if (child is not null)
                    {
                        if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                        child.Dispose();
                    }
                    if (starting is not null)
                    {
                        try { await starting; }
                        catch (OperationCanceledException) { }
                    }
                    Directory.Delete(root, recursive: true);
                }
            }
        }
        finally { Environment.SetEnvironmentVariable(RendezvousVariable, previous); }
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        Console.WriteLine("PASS " + message);
    }
}
