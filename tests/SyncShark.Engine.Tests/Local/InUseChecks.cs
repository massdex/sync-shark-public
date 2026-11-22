using Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

public static class InUseChecks
{
    public static Task RunAsync()
    {
        Check.Require(InUse.Processes(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Count == 0,
            "absent item must not invent a locking process");
        return Task.CompletedTask;
    }
}
