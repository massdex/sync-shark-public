using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class HostServerChecks
{
    public static async Task RunAsync()
    {
        using (var owner = new HostServer()) {
            Check.Require(await owner.ClaimAsync(default), "First server failed to claim the pipe");
            using var contender = new HostServer();
            bool refused;
            try { refused = !await contender.ClaimAsync(default); } catch (UnauthorizedAccessException) { refused = true; }
            Check.Require(refused, "Second claim succeeded before the owner released the pipe");
        }
        using var next = new HostServer();
        Check.Require(await next.ClaimAsync(default), "Released pipe could not be claimed");
    }
}
