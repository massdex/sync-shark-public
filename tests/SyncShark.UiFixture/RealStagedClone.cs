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
// Published checks in ClonedStateAdapterChecks.cs and OperationLogUiChecks.cs read the operator's
// retained identities through this type, so the stub must carry it. It holds environment-variable
// names and no values. Keep it in step with the excluded RealStagedClone.cs declaration.
internal static class RetainedCloneIdentities
{
    internal const string ParentPairVariable = "SYNCSHARK_REAL_CLONE_PARENT_PAIR";
    internal const string ChildPairVariable = "SYNCSHARK_REAL_CLONE_CHILD_PAIR";
    internal const string ParentRunVariable = "SYNCSHARK_REAL_CLONE_PARENT_RUN";
    internal const string ChildRunVariable = "SYNCSHARK_REAL_CLONE_CHILD_RUN";

    internal static string ParentPair => Read(ParentPairVariable);
    internal static string ChildPair => Read(ChildPairVariable);
    internal static string ParentRun => Read(ParentRunVariable);
    internal static string ChildRun => Read(ChildRunVariable);

    internal static string Read(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                "Set " + variable + " to the exact retained identity before running this mode.");

}