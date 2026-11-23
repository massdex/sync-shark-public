using Massdex.SyncShark.Engine.Edge;
using System.IO.Pipes;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class HostServerChecks
{
    public static async Task RunAsync()
    {
        foreach (bool fail in new[] { true, false }) {
            using var stream = new MemoryStream();
            await PipeFrame.WriteAsync(stream, (byte[])[0, ..System.Text.Encoding.UTF8.GetBytes("[\"status\"]")], default);
            long replies = stream.Position; stream.Position = 0;
            int calls = 0;
            try {
                await HostServer.DispatchAsync(stream, async (args, output, _, _) => {
                    calls++;
                    Check.Require(args is ["status"], "Dispatcher did not pass the expected command");
                    if (fail) throw new IOException("request failed");
                    await output.WriteAsync("status observed");
                    return 0;
                }, default);
            } catch (IOException ex) when (ex.Message == "request failed") {
                throw new InvalidOperationException("Request IOException escaped dispatch instead of reaching its caller as stderr and exit 1", ex);
            }
            Check.Require(calls == 1, "Dispatcher did not invoke the callback exactly once");
            stream.Position = replies;
            Check.Require((await PipeFrame.ReadAsync(stream, default))[0] == 1, "Request not accepted");
            var frame = await PipeFrame.ReadAsync(stream, default);
            if (fail) {
                Check.Require(frame[0] == 3 && System.Text.Encoding.UTF8.GetString(frame,1,frame.Length-1).Contains("request failed"), "Missing request error");
            } else {
                Check.Require(frame[0] == 2 && System.Text.Encoding.UTF8.GetString(frame,1,frame.Length-1) == "status observed", "Successful command output was not delivered");
            }
            frame = await PipeFrame.ReadAsync(stream, default);
            Check.Require(frame[0] == 4 && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(1)) == (fail ? 1 : 0), "Wrong terminal status");
        }
        using var owner = new HostServer();
        Check.Require(await owner.ClaimAsync(default), "First server failed to claim the pipe");
        using var stop = new CancellationTokenSource();
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = owner.ServeAsync((_,_,_,_) => throw new Exception("Untrusted command executed"), _ => { draining.SetResult(); return release.Task; }, stop.Token);
        stop.Cancel(); await draining.Task;
        try {
            using var contender = new HostServer();
            bool refused = false;
            try { refused = !await contender.ClaimAsync(default); } catch (UnauthorizedAccessException) { refused = true; }
            Check.Require(refused, "Second claim succeeded during drain");
        } finally { release.SetResult(); await serve; }
        using var next = new HostServer();
        Check.Require(await next.ClaimAsync(default), "Drained pipe did not release ownership");
    }
}
