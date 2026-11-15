namespace Massdex.SyncShark;

/// <summary>The one exclusive product-state lock every CLI runtime takes. Contention is the shared Busy refusal;
/// any other failure is a real state problem, never reported as "busy".</summary>
internal static class CliProductLock
{
    /// <summary>Takes the exclusive product-state lock, translating contention and real failures.</summary>
    internal static PilotInstanceLock Acquire(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        try
        {
            return PilotInstanceLock.Acquire(lockPath);
        }
        catch (IOException error) when (IsContention(error))
        {
            throw new SyncCliBusyException(
                "Sync Shark already holds its exclusive product state lock for this Windows user, so this command " +
                "cannot read or change product state here. Use the running application: start Sync Shark and run " +
                "the command again, so it executes inside that process. The CLI never forces the other process, " +
                "bypasses the lock or starts a second runtime.", error);
        }
        catch (Exception error)
        {
            throw new SyncCliOperationException(
                "The Sync Shark product state could not be opened: " + SyncCli.DescribeException(error), error);
        }
    }

    /// <summary>Only sharing violations and lock conflicts mean "another process owns the product"; a missing
    /// volume, a denied path or a damaged state directory must surface as a real failure.</summary>
    internal static bool IsContention(IOException error) =>
        error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 } ||
        error.Message.Contains("already open in this Windows user account", StringComparison.Ordinal);
}
