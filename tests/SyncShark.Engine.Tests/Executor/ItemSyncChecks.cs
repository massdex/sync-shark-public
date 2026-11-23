namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Contract checks for ItemSync.</summary>
public static class ItemSyncChecks
{
    public static Task ReplayPutBackAsync() => throw new NotImplementedException();
    public static Task PlannedFailureAsync() => throw new NotImplementedException();
    public static Task RetainedMovePrefixAsync() => throw new NotImplementedException();
    public static Task EditedPeerAsync() => throw new NotImplementedException();

    /// <summary>Check tombstone recovery at integration.</summary>
    public static Task TombstoneRecoveryAsync() => throw new NotImplementedException();
    /// <summary>Check empty folder history at integration.</summary>
    public static Task EmptyFolderHistoryAsync() => throw new NotImplementedException();
    /// <summary>Check feed move at integration.</summary>
    public static Task FeedMoveAsync() => throw new NotImplementedException();
    /// <summary>Check replacement held child at integration.</summary>
    public static Task ReplacementHeldChildAsync() => throw new NotImplementedException();
    /// <summary>Verifies the ItemSync contract through the module surface.</summary>
    public static Task RunAsync() => throw new NotImplementedException();
}
