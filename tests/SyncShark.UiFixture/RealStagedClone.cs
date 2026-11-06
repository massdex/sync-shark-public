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
// The retained pair and run identities belong to the operator's environment, never to source.
// Each accessor fails closed rather than admitting an arbitrary pair.
internal static class RetainedCloneIdentities
{
    internal static string ParentPair => Read("SYNCSHARK_REAL_CLONE_PARENT_PAIR");
    internal static string ChildPair => Read("SYNCSHARK_REAL_CLONE_CHILD_PAIR");
    internal static string ParentRun => Read("SYNCSHARK_REAL_CLONE_PARENT_RUN");
    internal static string ChildRun => Read("SYNCSHARK_REAL_CLONE_CHILD_RUN");

    internal static string Read(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                "Set " + variable + " to the exact retained identity before running this mode.");
}