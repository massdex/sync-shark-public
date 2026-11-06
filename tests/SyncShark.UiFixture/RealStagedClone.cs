namespace SyncShark.UiFixture;
internal static partial class Program
{
    private static Task<int> RunRealStagedCloneAsync(string root, string[] args) =>
        throw new NotSupportedException("Live-data operator tooling is not distributed in this snapshot.");
    private static int RunRealStagedWindow(string root) =>
        throw new NotSupportedException("Live-data operator tooling is not distributed in this snapshot.");
    private static int RunRealStagedGuardChecks() =>
        throw new NotSupportedException("Live-data operator tooling is not distributed in this snapshot.");
}