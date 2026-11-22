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
            await HostServer.DispatchAsync(stream, (_,_,_,_) => fail ? throw new IOException("request failed") : Task.FromResult(0), default);
            stream.Position = replies;
            Check.Require((await PipeFrame.ReadAsync(stream, default))[0] == 1, "Request not accepted");
            var frame = await PipeFrame.ReadAsync(stream, default);
            if (fail) {
                Check.Require(frame[0] == 3 && System.Text.Encoding.UTF8.GetString(frame,1,frame.Length-1).Contains("request failed"), "Missing request error");
                frame = await PipeFrame.ReadAsync(stream, default);
            }
            Check.Require(frame[0] == 4 && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(1)) == (fail ? 1 : 0), "Wrong terminal status");
        }
        string address = Guid.NewGuid().ToString("N");
        using var owner = new HostServer(address);
        Check.Require(await owner.ClaimAsync(default), "First server failed to claim the pipe");
        using var stop = new CancellationTokenSource();
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = owner.ServeAsync((_,_,_,_) => throw new Exception("Untrusted command executed"), _ => { draining.SetResult(); return release.Task; }, stop.Token);
        stop.Cancel(); await draining.Task;
        try {
            using var contender = new HostServer(address);
            bool refused = false;
            try { refused = !await contender.ClaimAsync(default); } catch (UnauthorizedAccessException) { refused = true; }
            Check.Require(refused, "Second claim succeeded during drain");
        } finally { release.SetResult(); await serve; }
        using var next = new HostServer(address);
        Check.Require(await next.ClaimAsync(default), "Drained pipe did not release ownership");
    }
}
