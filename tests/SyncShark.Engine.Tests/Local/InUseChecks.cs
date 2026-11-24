using Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

public static class InUseChecks
{
    public static Task RunAsync()
    {
        Check.Require(InUse.Processes(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Count == 0,
            "absent item must not invent a locking process");
        using var file = new FileStream(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        Check.Require(InUse.Processes(file.Name).Any(s => !string.IsNullOrWhiteSpace(s)),
            "Restart Manager omitted a process holding the test file");
        return Task.CompletedTask;
    }
}
