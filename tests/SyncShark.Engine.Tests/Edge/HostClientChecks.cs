using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Massdex.SyncShark.Engine.Edge;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Massdex.SyncShark.Engine.Tests.Edge.HostServerChecks;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class HostClientChecks
{
    public static async Task RunAsync()
    {
        var result = await new HostClient().SendAsync(["status"], TextWriter.Null, TextWriter.Null, default);
        Check.Require(result.ExitCode is null && !result.ReceivedExit, "An absent host did not return definite pre-send absence");

        // A long-lived host must not keep the caller's pipe open after the CLI exits.
        using var caller = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var probe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Check.Require(await HostClient.StartAsync(probe, "/d /c \"echo 1&ping -n 30 127.0.0.1 >nul\"", default) == 0,
            "The probe's readiness line was not read");
        caller.DisposeLocalCopyOfClientHandle();
        var eof = Task.Run(() => caller.Read(new byte[1], 0, 1));
        Check.Require(await Task.WhenAny(eof, Task.Delay(TimeSpan.FromSeconds(10))) == eof && await eof == 0,
            "The launched host inherited the caller's pipe");
        // Readiness EOF maps to 1 whatever the child's own exit code.
        Check.Require(await HostClient.StartAsync(probe, "/d /c exit 7", default) == 1, "Readiness EOF did not map to 1");

        await StartEofReturnsAtOnceAsync();
        await BusyMutationNotSentOrStartedAsync();
        await DisconnectAfterMutationAsync();
    }

    private const string EofChild = "--readiness-eof-child";

    // The test runner doubles as a child that closes its stdout and keeps running until released.
    [ModuleInitializer]
    internal static void RunEofChild()
    {
        if (Environment.GetCommandLineArgs() is not [.., EofChild, var pidFile, var release]) return;
        File.WriteAllText(pidFile, Environment.ProcessId.ToString());
        using (var started = EventWaitHandle.OpenExisting(release + ".started")) started.Set();
        CloseHandle(GetStdHandle(-11)); // winbase.h STD_OUTPUT_HANDLE
        using (var signal = EventWaitHandle.OpenExisting(release)) signal.WaitOne(TimeSpan.FromMinutes(1));
        Environment.Exit(0);
    }
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    private static async Task StartEofReturnsAtOnceAsync()
    {
        var release = $"sync-shark-eof-{Guid.NewGuid():N}";
        var pidFile = Path.Combine(Path.GetTempPath(), release + ".pid");
        var exe = Environment.ProcessPath!;
        // Under `dotnet <dll>` the runner's own assembly is the first argument.
        var self = Path.GetFileNameWithoutExtension(exe) == "dotnet" ? $"\"{typeof(HostClientChecks).Assembly.Location}\" " : "";
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, release);
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, release + ".started");
        Process? child = null;
        Exception? fault = null;
        try {
            var start = HostClient.StartAsync(exe, $"{self}{EofChild} \"{pidFile}\" {release}", default);
            // Hold the child's handle before waiting on StartAsync, so the finally reaps it on every path.
            Check.Require(await Task.Run(() => started.WaitOne(Bound)), "The readiness child never started");
            child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
            Check.Require(await Within(start, "StartAsync waited for the child after readiness EOF") == 1, "Readiness EOF did not return 1");
            Check.Require(!child.HasExited, "The child exited before StartAsync returned, so EOF was not proven independent of exit");
        } catch (Exception e) { fault = e; throw; }
        finally {
            // L13 R5 0.3: "the test then sets the event and reaps the child (also on failure)".
            signal.Set();
            // A child that published its PID before a failed wait is still reaped; the image name guards against a reused PID.
            try {
                if (child == null && int.TryParse(File.Exists(pidFile) ? File.ReadAllText(pidFile) : "", out var pid)) {
                    var published = Process.GetProcessById(pid);
                    if (published.ProcessName == Path.GetFileNameWithoutExtension(exe)) child = published; else published.Dispose();
                }
            } catch (Exception e) when (e is ArgumentException or IOException) { /* It already exited, or never finished publishing. */ }
            var reaped = child is not { } running || running.WaitForExit(Bound) || Kill(running);
            child?.Dispose();
            File.Delete(pidFile);
            // Report a leaked child, keeping the primary failure as the first cause.
            if (!reaped) throw fault is null ? new InvalidDataException("The readiness child was not reaped")
                : new AggregateException("The readiness child was not reaped after a failure", fault);
        }
        static bool Kill(Process running) { running.Kill(); return running.WaitForExit(Bound); }
    }

    private static async Task BusyMutationNotSentOrStartedAsync()
    {
        Check.Require(!File.Exists(Path.Combine(AppContext.BaseDirectory, ResourceNames.HostExecutable)),
            "A host image beside the test runner would let a start launch it");
        var busy = Text.Get(TextKey.HostBusy);
        var absent = Text.Get(TextKey.HostNotRunning);
        using (var owner = new HostServer()) {
            Check.Require(await owner.ClaimAsync(default), "Busy fixture could not claim the pipe");
            using var occupier = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            occupier.Connect(0);
            foreach (var args in new[] { new[] { "host", "start" }, ["status"] }) {
                var output = new StringWriter(); var error = new StringWriter();
                Check.Require(await Cli.RunAsync(args, null!, output, error, default) == 1, "A busy host did not return exit 1");
                Check.Require(error.ToString().Contains(busy) && !(output.ToString() + error).Contains(absent),
                    "A busy host was not reported as busy with nothing sent");
            }
        }
        var repair = Cli.Parse(["repair", "--pair", "pair-a"])!;
        async Task<(int Code, int Starts, int Sends, string Output)> Resolve(int start, (int?, bool) second)
        {
            int starts = 0, sends = 0; var output = new StringWriter();
            var code = await Cli.ResolveSend(repair, (null, false), output, c => { sends++; return Task.FromResult(second); },
                c => { starts++; return Task.FromResult(start); }, default);
            return (code, starts, sends, output.ToString());
        }
        Check.Require(await Resolve(1, (7, true)) is (7, 1, 1, _), "A raced start did not send once and keep the host's reply");
        Check.Require(await Resolve(1, (null, false)) is (1, 1, 1, var failed) && failed.Contains(absent), "A failed start did not report absence");
        Check.Require(await Resolve(0, (null, false)) is (1, 1, 1, var gone) && gone.Contains(absent), "A vanished host did not report absence");

        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory;
        CatchClauseSyntax[] Timeouts(string file) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src", "SyncShark.Engine", "Edge", file)))
            .GetRoot().DescendantNodes().OfType<CatchClauseSyntax>().Where(c => c.Declaration?.Type.ToString() == nameof(TimeoutException)).ToArray();
        Check.Require(Timeouts("HostClient.cs") is [{ Parent: TryStatementSyntax { Block.Statements: [ExpressionStatementSyntax only] } }] &&
            only.ToString().Contains("ConnectAsync"), "Busy must be classified only around the connect");
        Check.Require(Timeouts("Cli.cs").Length == 0, "Cli must not classify timeouts");
    }

    // A disconnect after an accepted mutation makes the client report exit 1 (unknown), never "nothing was sent", and no retry.
    private static async Task DisconnectAfterMutationAsync()
    {
        var renewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new Gated();
        var mutations = 0; var returned = false;
        Task? serve = null;
        try {
            using var server = new HostServer();
            Check.Require(await server.ClaimAsync(default), "Mutation fixture could not claim the pipe");
            serve = Serve(server, (args, output, error, token) => {
                    Interlocked.Increment(ref mutations);
                    output.Write(new string('x', 8 * 1024 * 1024));
                    returned = true;
                    return Task.FromResult(0);
                }, Outage, (f, t) => renewal.Task.WaitAsync(t), pipe => new MemoryStream(), default,
                async _ => { draining.TrySetResult(); await drained.Task; });
            using var pipe = await ConnectAsync();
            var exchange = typeof(HostClient).GetMethod("ExchangeAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var reply = (Task<(int? ExitCode, bool ReceivedExit)>)exchange.Invoke(null, BindingFlags.DoNotWrapExceptions, null,
                [pipe, new[] { "mutate" }, reading, TextWriter.Null, CancellationToken.None], null)!;
            await Within(reading.Entered.Task, "The client never read the first output frame");
            Check.Require(!Volatile.Read(ref returned) && Volatile.Read(ref mutations) == 1, "The mutation's output write was not pending");
            // The renewal ends without a successor before shutdown cancels the pending write.
            renewal.SetCanceled();
            await Within(draining.Task, "Shutdown did not start after the renewal ended");
            reading.Release();
            var result = await Within(reply, "The client result waited for the final close, not the disconnect");
            Check.Require(result == (1, false), "A disconnected mutation was not reported as an unknown exit 1");
            int starts = 0, sends = 0; var error = new StringWriter(); var output = new StringWriter();
            var code = await Cli.ResolveSend(Cli.Parse(["repair", "--pair", "pair-a"])!, result, output,
                c => { sends++; return Task.FromResult<(int?, bool)>((0, true)); }, c => { starts++; return Task.FromResult(0); }, default);
            var text = output.ToString() + error;
            Check.Require(code == 1 && starts == 0 && sends == 0 && mutations == 1, "A disconnected mutation was started or sent again");
            Check.Require(!text.Contains(Text.Get(TextKey.HostBusy)) && !text.Contains(Text.Get(TextKey.HostNotRunning)),
                "A disconnected mutation was reported as nothing sent");
            drained.SetResult();
            await Task.WhenAny(serve, Task.Delay(Bound));
            Check.Require(serve.IsCompleted, "Serving did not end after the drain");
        } finally {
            reading.Release();
            drained.TrySetResult();
            if (serve != null) await Task.WhenAny(serve, Task.Delay(Bound));
        }
        await FreshClaimAsync("The pipe stayed claimed after the mutation fixture");
    }

    /// <summary>A client output writer that stops reading at its first write until released.</summary>
    private sealed class Gated : TextWriter
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Encoding Encoding => Encoding.UTF8;
        public void Release() => release.TrySetResult();
        public override Task WriteAsync(string? value) { Entered.TrySetResult(); return release.Task; }
    }
}
