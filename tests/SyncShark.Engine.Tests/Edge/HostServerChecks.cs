using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Edge;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Win32.SafeHandles;
using Execute = System.Func<string[], System.IO.TextWriter, System.IO.TextWriter, System.Threading.CancellationToken, System.Threading.Tasks.Task<int>>;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class HostServerChecks
{
    public static async Task RunAsync()
    {
        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory;
        var source = File.ReadAllText(Path.Combine(root, "src", "SyncShark.Engine", "Edge", "HostServer.cs"));
        var client = File.ReadAllText(Path.Combine(root, "src", "SyncShark.Engine", "Edge", "HostClient.cs"));
        foreach (var unit in new[] {source,client})
        {
            var connect = CSharpSyntaxTree.ParseText(unit).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConnectAsync" });
            Check.Require(connect.ArgumentList.Arguments[0].Expression.ToString() == "0" &&
                connect.ArgumentList.Arguments[1].Expression.ToString() == "ct", "Both connect paths must use the pipe's own default wait and observe cancellation");
        }
        var handler = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == nameof(HostServer.ServeAsync))
            .SelectMany(method => method.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            .Single(local => local.Identifier.ValueText == "HandleAsync");
        // Count only the dispatch try's own catches, so the finally's Disconnect guard is outside the count.
        var dispatch = handler.DescendantNodes().OfType<TryStatementSyntax>().Single(attempt => attempt.Block.DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Any(call => call.Expression.ToString() == "trust"));
        Check.Require(dispatch.Catches is [{ Declaration.Type: var type, Filter.FilterExpression: var filter, Block.Statements.Count: 0 }] &&
            type.ToString() == nameof(Exception) && filter.ToString() == "e is not OutOfMemoryException",
            "The connection handler must end a failed exchange silently without stopping the listener");
        Check.Require(!source.Contains("Console.Error"), "The host server writes to stderr");
        var frameWrites=CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call=>call.Expression.ToString()=="PipeFrame.WriteAsync").ToArray();
        Check.Require(frameWrites.Length!=0 && frameWrites.All(call=>call.ArgumentList.Arguments.Last().Expression.ToString()!="default"),
            "A server frame write still ignores shutdown");
        var hostPipe=PipeName;
        using (var owner = new HostServer()) {
            Check.Require(await owner.ClaimAsync(default), "First server failed to claim the pipe");
            // Use SMB loopback, not the local pipe namespace. A broken SMB route must not count as rejection.
            var controlName=Guid.NewGuid().ToString("N");
            using var control=new NamedPipeServerStream(controlName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.CurrentUserOnly);
            using var allowed=new NamedPipeClientStream("127.0.0.1",controlName,PipeDirection.InOut);
            allowed.Connect(0);
            using var remote=new NamedPipeClientStream("127.0.0.1",hostPipe,PipeDirection.InOut);
            try {remote.Connect(0);throw new InvalidDataException("Host accepted a network pipe connection");}
            catch(UnauthorizedAccessException) { /* The available host listener refused the remote client. */ }
            using var contender = new HostServer();
            Check.Require(await Refused(contender), "Second claim succeeded before the owner released the pipe");
        }
        using (var next = new HostServer()) {
            Check.Require(await next.ClaimAsync(default), "Released pipe could not be claimed");
            using var local=new NamedPipeClientStream(".",hostPipe,PipeDirection.InOut,PipeOptions.CurrentUserOnly);
            local.Connect(0);
        }
        await RenewalSinkAsync(blocking: true);
        await RenewalSinkAsync(blocking: false);
        await InlineDispatchAsync();
        await BrokenAfterTerminalRenewalAsync();
        await RegistrationBeforeReapAsync();
        await BrokenDuringOutageAsync();
        await BrokenAcceptDuringOutageAsync();
        await UnreadReplyRetainedAsync();
        await ShutdownDuringRenewalAsync();
        await RenewalFailsThenRecoversAsync();
    }

    internal const byte Arguments = 0, Accepted = 1, Exit = 4;
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    internal static readonly Func<NamedPipeServerStream> Outage = () => throw new IOException("listen outage");
    internal static readonly Execute OneLine = (args, output, error, token) => { output.WriteLine("served"); return Task.FromResult(0); };

    internal static string PipeName {
        get { using var identity = WindowsIdentity.GetCurrent(); return $"{nameof(HostServer)}.{ResourceNames.HostExecutable}.{identity.User!.Value}"; }
    }

    /// <summary>The private ServeAsync with its listen, retry-wait and trust seams.</summary>
    internal static Task Serve(HostServer server, Execute execute, Func<NamedPipeServerStream> listen, Func<int, CancellationToken, Task> retryWait,
        Func<PipeStream, IDisposable?> trust, CancellationToken ct, Func<CancellationToken, Task>? shutdown = null) =>
        (Task)typeof(HostServer).GetMethod("ServeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(server, BindingFlags.DoNotWrapExceptions, null,
            [execute, shutdown ?? (_ => Task.CompletedTask), listen, retryWait, trust, ct], null)!;

    /// <summary>The server's own non-first listener, so a recovered renewal serves real clients.</summary>
    internal static Func<NamedPipeServerStream> Listener(HostServer server)
    {
        var listen = typeof(HostServer).GetMethod("Listen", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return () => (NamedPipeServerStream)listen.Invoke(server, BindingFlags.DoNotWrapExceptions, null, [false], null)!;
    }

    internal static async Task<NamedPipeClientStream> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync((int)Bound.TotalMilliseconds); return pipe; } catch { pipe.Dispose(); throw; }
    }

    internal static Task SendAsync(Stream pipe, params string[] args)
    {
        byte[] request = [Arguments, ..JsonSerializer.SerializeToUtf8Bytes(args)];
        return PipeFrame.WriteAsync(pipe, request, default);
    }

    /// <summary>One whole argv exchange on a raw client: Accepted, the output text, then the exit code.</summary>
    internal static async Task<(int Code, string Output)> ExchangeAsync(Stream pipe, params string[] args)
    {
        await SendAsync(pipe, args);
        return await ReplyAsync(pipe);
    }

    internal static async Task<(int Code, string Output)> ReplyAsync(Stream pipe)
    {
        Check.Require((await PipeFrame.ReadAsync(pipe, default))[0] == Accepted, "The server did not accept a valid request");
        var text = new StringBuilder();
        while (true) {
            var frame = await PipeFrame.ReadAsync(pipe, default);
            if (frame[0] == Exit) return (BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(1)), text.ToString());
            text.Append(Encoding.UTF8.GetString(frame, 1, frame.Length - 1));
        }
    }

    /// <summary>True when the client's end was closed: EOF or a broken pipe, never data.</summary>
    internal static async Task<bool> EndedAsync(Stream pipe)
    {
        try { return await pipe.ReadAsync(new byte[1]) == 0; } catch (IOException) { return true; }
    }

    internal static async Task<T> Within<T>(Task<T> task, string failure)
    {
        Check.Require(await Task.WhenAny(task, Task.Delay(Bound)) == task, failure);
        return await task;
    }

    internal static async Task Within(Task task, string failure)
    {
        Check.Require(await Task.WhenAny(task, Task.Delay(Bound)) == task, failure);
        await task;
    }

    internal static async Task<bool> Refused(HostServer contender)
    {
        try { return !await contender.ClaimAsync(default); }
        catch (Exception e) when (e is UnauthorizedAccessException or TimeoutException) { return true; }
    }

    internal static bool Busy()
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try { pipe.Connect(0); return false; } catch (TimeoutException) { return true; }
    }

    internal static async Task FreshClaimAsync(string failure)
    {
        using var fresh = new HostServer();
        Check.Require(await fresh.ClaimAsync(default), failure);
    }

    // Renewal and handler faults write no diagnostic, so a blocked or closed stderr cannot stall intake or shutdown.
    private static async Task RenewalSinkAsync(bool blocking)
    {
        var original = Console.Error;
        var sink = new Sink(blocking);
        Console.SetError(sink);
        using var stop = new CancellationTokenSource();
        Task? serve = null;
        try {
            using var server = new HostServer();
            Check.Require(await server.ClaimAsync(default), "Sink fixture could not claim the pipe");
            var real = Listener(server);
            var outage = false;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var faults = 0;
            serve = Serve(server, OneLine, () => outage ? Outage() : real(),
                (failures, token) => { entered.TrySetResult(); return gate.Task.WaitAsync(token); },
                pipe => { if (faults > 0) { faults--; throw new InvalidOperationException("trust fault"); } return new MemoryStream(); }, stop.Token);
            for (var episode = 0; episode < 2; episode++) {
                outage = true; entered = new(TaskCreationOptions.RunContinuationsAsynchronously); gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var during = await ConnectAsync())
                    Check.Require((await Within(ExchangeAsync(during, "x"), "A client was not served during an outage")).Code == 0, "Outage reply");
                await Within(entered.Task, "The listener outage was not retried");
                outage = false; gate.SetResult();
                using var after = await ConnectAsync();
                Check.Require((await Within(ExchangeAsync(after, "x"), "A recovered listener did not serve")).Code == 0, "Recovered reply");
            }
            Check.Require(sink.Entries == 0, "Listener renewal wrote to stderr");
            faults = 3;
            for (var fault = 0; fault < 3; fault++) {
                using var faulted = await ConnectAsync();
                // The server may close a faulted client before reading it, so a broken send also means the exchange ended.
                var ended = true;
                try { await SendAsync(faulted, "x"); ended = await Within(EndedAsync(faulted), "A faulted handler left its client open"); }
                catch (IOException) { /* Closed before the request was read. */ }
                Check.Require(ended, "A faulted handler replied");
            }
            Check.Require(sink.Entries == 0, "A handler fault wrote to stderr");
            await stop.CancelAsync();
            await Within(serve, "Serving did not stop after cancellation");
        } finally {
            Console.SetError(original);
            sink.Release();
            await stop.CancelAsync();
            if (serve != null) await Task.WhenAny(serve, Task.Delay(Bound));
        }
        await FreshClaimAsync("The pipe stayed claimed after a sink fixture");
    }

    // A handler that blocks before its first await (here in trust) must not hold the accept loop.
    private static async Task InlineDispatchAsync()
    {
        using var hold = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var stop = new CancellationTokenSource();
        Task? serve = null;
        try {
            using var server = new HostServer();
            Check.Require(await server.ClaimAsync(default), "Inline fixture could not claim the pipe");
            serve = Serve(server, OneLine, Listener(server), (f, t) => Task.Delay(Timeout.Infinite, t),
                pipe => { if (Interlocked.Increment(ref calls) == 1) { held.TrySetResult(); hold.Wait(); } return new MemoryStream(); }, stop.Token);
            using var first = await ConnectAsync();
            var firstReply = ExchangeAsync(first, "held");
            await Within(held.Task, "The first handler never started");
            using (var second = await ConnectAsync())
                Check.Require((await Within(ExchangeAsync(second, "small"), "A blocked handler held the accept loop")).Code == 0, "Second reply");
            hold.Set();
            Check.Require((await Within(firstReply, "The held client was not served after release")).Code == 0, "First reply");
            await stop.CancelAsync();
            await Within(serve, "Serving did not stop after cancellation");
        } finally {
            hold.Set();
            await stop.CancelAsync();
            if (serve != null) await Task.WhenAny(serve, Task.Delay(Bound));
        }
        await FreshClaimAsync("The pipe stayed claimed after the inline fixture");
    }

    // A terminal renewal failure leaves no successor: a broken exchange is disconnected at once, while the name stays held.
    private static async Task BrokenAfterTerminalRenewalAsync()
    {
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? serve = null;
        try {
            using var server = new HostServer();
            Check.Require(await server.ClaimAsync(default), "Terminal fixture could not claim the pipe");
            using var broken = await ConnectAsync();
            await OversizedHeaderAsync(broken);
            serve = Serve(server, OneLine, () => throw new UnauthorizedAccessException("listen denied"), (f, t) => Task.CompletedTask,
                pipe => new MemoryStream(), default, async _ => { draining.TrySetResult(); await drained.Task; });
            await Within(draining.Task, "Shutdown did not start after the terminal renewal failure");
            Check.Require(await Within(EndedAsync(broken), "A broken exchange stayed open through the drain"), "A broken exchange received data");
            using (var contender = new HostServer()) Check.Require(await Refused(contender), "A contender claimed during the drain");
            Check.Require(Busy(), "A new client reached the draining host");
            drained.SetResult();
            try { await Within(serve, "Serving did not end after the drain"); throw new InvalidDataException("Serving hid the renewal failure"); }
            catch (UnauthorizedAccessException) { /* The terminal renewal failure is the outcome. */ }
        } finally {
            drained.TrySetResult();
            if (serve != null) await Task.WhenAny(serve, Task.Delay(Bound));
        }
        await FreshClaimAsync("The pipe stayed claimed after a terminal renewal failure");
    }

    // A reap that rethrows must leave every instance with exactly one closer.
    private static async Task RegistrationBeforeReapAsync()
    {
        Task? serve = null;
        try {
            using var server = new HostServer();
            Check.Require(await server.ClaimAsync(default), "Reap fixture could not claim the pipe");
            var calls = 0;
            serve = Serve(server, OneLine, Listener(server), (f, t) => Task.Delay(Timeout.Infinite, t),
                pipe => Interlocked.Increment(ref calls) == 1 ? throw new OutOfMemoryException("fault marker") : new MemoryStream(), default);
            using (var first = await ConnectAsync()) await Within(EndedAsync(first), "The faulted handler left its client open");
            var deadline = DateTime.UtcNow + Bound;
            while (!serve.IsCompleted && DateTime.UtcNow < deadline) {
                try { using var next = await ConnectAsync(); await Task.WhenAny(ExchangeAsync(next, "x"), Task.Delay(Bound)); }
                catch (Exception e) when (e is IOException or TimeoutException or InvalidDataException) { /* Serving ended under this client. */ }
            }
            try { await Within(serve, "The reap never observed the faulted handler"); throw new InvalidDataException("Serving hid the handler fault"); }
            catch (OutOfMemoryException) { /* The marker fault is the outcome. */ }
        } finally {
            if (serve != null) await Task.WhenAny(serve, Task.Delay(Bound));
        }
        await FreshClaimAsync("A reap fault orphaned a pipe instance");
        using (var stop = new CancellationTokenSource()) {
            using (var server = new HostServer()) {
                Check.Require(await server.ClaimAsync(default), "Reap fixture could not reclaim the pipe");
                var normal = Serve(server, OneLine, Listener(server), (f, t) => Task.Delay(Timeout.Infinite, t), pipe => new MemoryStream(), stop.Token);
                using (var client = await ConnectAsync())
                    Check.Require((await Within(ExchangeAsync(client, "x"), "A normal client was not served")).Code == 0, "Normal reply");
                await stop.CancelAsync();
                await Within(normal, "Serving did not stop after cancellation");
            }
        }
        await FreshClaimAsync("A normal shutdown left the pipe claimed");
    }

    private static async Task OversizedHeaderAsync(Stream pipe)
    {
        var header = new byte[5]; // kind Arguments, then a length one past the one MiB payload cap
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), 1024 * 1024 + 1u);
        await pipe.WriteAsync(header);
        await pipe.FlushAsync();
    }

    /// <summary>Claim, run the body's serving, cancel it and require that a fresh claim succeeds afterwards.</summary>
    private static async Task FixtureAsync(string name, Func<HostServer, CancellationToken, List<Task>, Task> body)
    {
        using var stop = new CancellationTokenSource();
        var serving = new List<Task>();
        try {
            using var server = new HostServer();
            Check.Require(await server.ClaimAsync(default), $"{name}: could not claim the pipe");
            await body(server, stop.Token, serving);
            await stop.CancelAsync();
            foreach (var serve in serving) await Within(serve, $"{name}: serving did not stop after cancellation");
        } finally {
            await stop.CancelAsync();
            foreach (var serve in serving) await Task.WhenAny(serve, Task.Delay(Bound));
        }
        await FreshClaimAsync($"{name}: the pipe stayed claimed");
    }

    // During an outage a broken exchange is disconnected at once; the name stays held until a successor exists.
    private static async Task BrokenDuringOutageAsync()
    {
        foreach (var variant in new[] { "kind", "untrusted", "oversized" })
            await FixtureAsync($"broken during outage ({variant})", async (server, ct, serving) => {
                var renewal = new Renewal(server);
                var trusted = 0;
                using var broken = await ConnectAsync();
                if (variant == "kind") await PipeFrame.WriteAsync(broken, new byte[] { Accepted }, default);
                else if (variant == "untrusted") await SendAsync(broken, "x");
                else await OversizedHeaderAsync(broken);
                serving.Add(Serve(server, OneLine, renewal.Listen, renewal.WaitAsync,
                    pipe => variant == "untrusted" && Interlocked.Increment(ref trusted) == 1 ? null : new MemoryStream(), ct));
                await Within(renewal.Waiting.Task, "The listener outage did not start");
                Check.Require(await Within(EndedAsync(broken), "A broken exchange stayed open during the outage"), "A broken exchange received data");
                using (var contender = new HostServer()) Check.Require(await Refused(contender), "A contender claimed during the outage");
                Check.Require(Busy(), "A new client reached the host during the outage");
                renewal.Recover();
                using var next = await ConnectAsync();
                Check.Require((await Within(ExchangeAsync(next, "x"), "The recovered listener did not serve")).Code == 0, "Recovered reply");
            });
    }

    // A client that left before the accept takes the broken-accept branch, which renews while the name stays held.
    private static Task BrokenAcceptDuringOutageAsync() => FixtureAsync("broken accept during outage", async (server, ct, serving) => {
        var renewal = new Renewal(server);
        using (var gone = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly)) gone.Connect(0);
        serving.Add(Serve(server, OneLine, renewal.Listen, renewal.WaitAsync, pipe => new MemoryStream(), ct));
        await Within(renewal.Waiting.Task, "A broken accept did not renew the listener");
        using (var contender = new HostServer()) Check.Require(await Refused(contender), "A contender claimed during the outage");
        renewal.Recover();
        using var next = await ConnectAsync();
        Check.Require((await Within(ExchangeAsync(next, "x"), "The recovered listener did not serve")).Code == 0, "Recovered reply");
    });

    // A completed reply is never disconnected, even while no successor holds the name.
    private static Task UnreadReplyRetainedAsync() => FixtureAsync("unread reply retained", async (server, ct, serving) => {
        var renewal = new Renewal(server);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        serving.Add(Serve(server, async (args, output, error, token) => { await output.WriteAsync("served"); return 0; },
            renewal.Listen, renewal.WaitAsync, pipe => new Ended(done), ct));
        using var client = await ConnectAsync();
        await SendAsync(client, "x");
        await Within(renewal.Waiting.Task, "The listener outage did not start");
        await Within(done.Task, "The exchange never completed");
        // Accepted (5), Output "served" (5 + 6), Exit (5 + 4).
        Check.Require(PeekNamedPipe(client.SafePipeHandle, 0, 0, 0, out var available, 0) && available == 25,
            "The whole reply was not waiting unread in the pipe");
        using (var contender = new HostServer()) Check.Require(await Refused(contender), "A contender claimed during the outage");
        Check.Require(await Within(ReplyAsync(client), "The unread reply was not readable") is (0, "served"), "A completed reply was disconnected");
        renewal.Recover();
    });

    // Shutdown during a renewal outage drains with the name held, then returns normally.
    private static Task ShutdownDuringRenewalAsync() => FixtureAsync("shutdown during renewal", async (server, ct, serving) => {
        var renewal = new Renewal(server);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serve = Serve(server, OneLine, renewal.Listen, renewal.WaitAsync, pipe => new MemoryStream(), stop.Token,
            async _ => { draining.TrySetResult(); await drained.Task; });
        serving.Add(serve);
        try {
            using (var client = await ConnectAsync())
                Check.Require((await Within(ExchangeAsync(client, "x"), "A client was not served during the outage")).Code == 0, "Outage reply");
            await Within(renewal.Waiting.Task, "The listener outage did not start");
            await stop.CancelAsync();
            await Within(draining.Task, "Shutdown did not start during the renewal outage");
            using (var contender = new HostServer()) Check.Require(await Refused(contender), "A contender claimed during the drain");
            drained.SetResult();
            await Within(serve, "Serving did not return after shutdown during renewal");
        } finally { drained.TrySetResult(); }
    });

    // Each failed listen waits on the backoff with its failure count, then the recovered listener serves.
    private static Task RenewalFailsThenRecoversAsync() => FixtureAsync("renewal fails then recovers", async (server, ct, serving) => {
        var real = Listener(server);
        var left = 3;
        var failures = new List<int>();
        serving.Add(Serve(server, OneLine, () => left-- > 0 ? Outage() : real(),
            (failure, token) => { failures.Add(failure); return Task.CompletedTask; }, pipe => new MemoryStream(), ct));
        for (var client = 0; client < 2; client++) {
            using var pipe = await ConnectAsync();
            Check.Require((await Within(ExchangeAsync(pipe, "x"), "A client was not served around the renewal failures")).Code == 0, "Reply");
        }
        Check.Require(failures is [0, 1, 2], "Renewal did not retry each failure with its count");
    });

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, nint buffer, uint size, nint read, out uint available, nint left);

    /// <summary>A listener that fails while down; its retry wait parks on a gate until Recover.</summary>
    private sealed class Renewal(HostServer server)
    {
        private readonly Func<NamedPipeServerStream> real = Listener(server);
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool down = true;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NamedPipeServerStream Listen() => down ? Outage() : real();
        public Task WaitAsync(int failures, CancellationToken ct) { Waiting.TrySetResult(); return gate.Task.WaitAsync(ct); }
        public void Recover() { down = false; gate.TrySetResult(); }
    }

    /// <summary>A trust result whose disposal marks the end of its dispatch.</summary>
    private sealed class Ended(TaskCompletionSource done) : IDisposable
    {
        public void Dispose() => done.TrySetResult();
    }

    /// <summary>A stderr replacement whose writes either block on a gate or throw; every entry is counted.</summary>
    private sealed class Sink(bool blocking) : TextWriter
    {
        private readonly ManualResetEventSlim gate = new();
        private int entries;
        public int Entries => Volatile.Read(ref entries);
        public override Encoding Encoding => Encoding.UTF8;
        public void Release() => gate.Set();
        public override void Write(char value) => Enter();
        public override void Write(string? value) => Enter();
        public override void Write(char[] buffer, int index, int count) => Enter();
        private void Enter()
        {
            Interlocked.Increment(ref entries);
            if (blocking) gate.Wait(); else throw new IOException("closed stderr");
        }
    }
}
