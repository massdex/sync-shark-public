using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using Massdex.SyncShark;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.V2;

namespace SyncShark.UiFixture;

/// <summary>Bug #115 route-level checks: the native Dropbox group reaches the verified running Engine host
/// through the real <see cref="CliEntry"/> routing, and only an absent host before anything was sent permits the
/// standalone engine runtime. The trusted peer must run in another process of this same executable, so the child
/// role (selected by <see cref="ChildFlag"/>) performs the real <see cref="CliEntry.Execute"/> call over an
/// overridden product root and reports its exit code through the process exit and standard output.</summary>
internal static class CliDropboxRouteChecks
{
    internal const string ChildFlag = "--cli-route-child";
    private const string HostMarker = "host-served-dropbox-route";
    private const string CatalogMarker = "files/list_folder";
    private static readonly string[] RouteArguments = ["--cli", "dropbox", "routes"];

    internal static int Run(string root)
    {
        // The product-root override admits only operating-system temporary roots, so each case owns one.
        Directory.CreateDirectory(root);
        try
        {
            WithCaseRoot(RunningHostServesTheRoute);
            WithCaseRoot(AbsentHostPermitsTheStandaloneRuntime);
            WithCaseRoot(AcceptedThenLostNeverAcquiresStandaloneAuthority);
            Console.WriteLine("PASS Dropbox route checks");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WithCaseRoot(Action<string> body)
    {
        var caseRoot = Directory.CreateTempSubdirectory("sync-shark-cli-route-").FullName;
        try { body(caseRoot); }
        finally
        {
            try { Directory.Delete(caseRoot, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // A verified running host answers first: it executes once and its output is the route result.
    private static void RunningHostServesTheRoute(string root)
    {
        var caseRoot = root;
        var pipeName = PipeName(caseRoot);
        var executions = 0;
        using var stop = new CancellationTokenSource();
        StartServer(pipeName, async (_, stdout, _, _) =>
        {
            Interlocked.Increment(ref executions);
            await stdout.WriteAsync(HostMarker).ConfigureAwait(false);
            return (int)SyncCliExitCode.Success;
        }, stop.Token);
        var run = RunChild(caseRoot, RouteArguments);
        Require(run.ExitCode == (int)SyncCliExitCode.Success,
            "a running host answers the Dropbox route with success: " + run.Output + run.Error);
        Require(run.Output.Contains(HostMarker, StringComparison.Ordinal),
            "the Dropbox route is executed on the running host, not locally");
        Require(executions == 1, "the running host executes the route exactly once");
    }

    // No host answered before anything was sent: the explicit standalone runtime renders the route table.
    private static void AbsentHostPermitsTheStandaloneRuntime(string root)
    {
        var caseRoot = root;
        var run = RunChild(caseRoot, RouteArguments);
        Require(run.ExitCode == (int)SyncCliExitCode.Success,
            "an absent host still succeeds through the standalone runtime: " + run.Output + run.Error);
        Require(run.Output.Contains(CatalogMarker, StringComparison.Ordinal),
            "the standalone runtime renders the route table after an absent host");
        Require(!run.Output.Contains(HostMarker, StringComparison.Ordinal),
            "an absent host produces no host output");
    }

    // Accepted then lost is the existing failure result: one execution, and never a second standalone acquisition.
    // The parent holds the product lock, so a fallback would surface as Busy instead of the failure result.
    private static void AcceptedThenLostNeverAcquiresStandaloneAuthority(string root)
    {
        var caseRoot = root;
        var pipeName = PipeName(caseRoot);
        var executions = 0;
        using var stop = new CancellationTokenSource();
        StartServer(pipeName, (_, _, _, _) =>
        {
            Interlocked.Increment(ref executions);
            // The host closed after its acceptance frame: the client keeps the failure result and never replays.
            throw new IOException("fixture reply lost after acceptance");
        }, stop.Token);
        using var held = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var run = RunChild(caseRoot, RouteArguments);
        Require(run.ExitCode == (int)SyncCliExitCode.Failed,
            "a reply lost after acceptance is the existing failure result: " + run.Output + run.Error);
        Require(run.ExitCode != (int)SyncCliExitCode.Busy,
            "a lost reply never falls back to a second standalone acquisition");
        Require(executions == 1, "a lost reply is executed exactly once and never replayed");
    }

    // The listener owns the pipe and ListenAsync's finally disposes it; the server itself holds no state to release.
    private static void StartServer(string pipeName, Func<string[], TextWriter, TextWriter,
        CancellationToken, Task<int>> execute, CancellationToken stop)
    {
        var server = new EngineHostServer(execute);
        _ = server.ListenAsync(CreateListener(pipeName, first: true),
            () => CreateListener(pipeName, first: false), 4, stop);
    }

    private static NamedPipeServerStream CreateListener(string pipeName, bool first) =>
        new(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly |
            (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    // The client derives its host pipe from the product root it overrides in-process; the child override is what
    // makes this an isolated route check instead of the machine's real product state.
    private static string PipeName(string productRoot) =>
        AutoSyncHostProtocol.PipeName(Path.Combine(productRoot, "state", "instance.lock")) + ".engine";

    private static ChildRun RunChild(string productRoot, string[] arguments)
    {
        var executable = Environment.ProcessPath ??
                         throw new InvalidOperationException("The fixture process identity is absent.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = productRoot,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location ??
                           throw new InvalidOperationException("The fixture entry assembly identity is absent.");
            start.ArgumentList.Add(assembly);
        }
        start.ArgumentList.Add(ChildFlag);
        start.ArgumentList.Add(productRoot);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
                            throw new InvalidOperationException("The route fixture child could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("The route fixture child did not finish in time.");
        }
        return new ChildRun(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }

    private sealed record ChildRun(int ExitCode, string Output, string Error);
}
