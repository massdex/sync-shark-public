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
// Identities come only from the operator's environment, so the accessor is public-safe.
internal static class RetainedCloneIdentities
{
    internal static string ParentPair => Read("SYNCSHARK_REAL_CLONE_PARENT_PAIR");
    internal static string ChildPair => Read("SYNCSHARK_REAL_CLONE_CHILD_PAIR");
    internal static string Read(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value
            : throw new InvalidOperationException("Set " + variable + " to the exact retained identity before running this mode.");
}