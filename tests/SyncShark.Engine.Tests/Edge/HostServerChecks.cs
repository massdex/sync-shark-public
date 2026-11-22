using Massdex.SyncShark.Engine.Edge;
using System.IO.Pipes;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class HostServerChecks
{
    public static async Task RunAsync()
    {
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
