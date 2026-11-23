namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Contract checks for AutoSync.</summary>
public static class AutoSyncChecks
{
    public static Task UnprovedFolderStaysAsync() => throw new NotImplementedException();
    public static Task ManualRetryAsync() => throw new NotImplementedException();
    public static Task ChangedItemReplayAsync() => throw new NotImplementedException();

    /// <summary>Check reset behind held page at integration.</summary>
    public static Task ResetBehindHeldPageAsync() => throw new NotImplementedException();
    /// <summary>A failed read waits the attempt schedule, then only the remaining positive provider wait.</summary>
    public static Task SourceRetryAsync() => throw new NotImplementedException();
    /// <summary>Verifies the AutoSync contract through the module surface.</summary>
    public static Task RunAsync() => throw new NotImplementedException();
}
