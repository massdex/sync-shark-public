using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class HostClientChecks
{
    public static async Task RunAsync()
    {
        var result = await new HostClient().SendAsync(["status"], TextWriter.Null, TextWriter.Null, default);
        Check.Require(result.ExitCode is null && !result.ReceivedExit, "An absent host did not return definite pre-send absence");
    }
}
