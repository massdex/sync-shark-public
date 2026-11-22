namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Notices operations for the executor module.</summary>
public sealed class Notices
{
    /// <summary>Update only an observed link member; the existing member rows determine when the one pair-wide notice closes.</summary>
    public Task RaiseAsync(IStore store, string pairId, Notice notice, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Close operation on the supplied facts.</summary>
    public Task CloseAsync(IStore store, string pairId, Notice expected, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>PutBackSettled for the selected Notices operation.</summary>
    public static bool PutBackSettled(bool ownRetryOrLaterChange) => throw new NotImplementedException();
    /// <summary>Notice kinds settled by AfterEffect.</summary>
    public static IReadOnlyList<NoticeKind> AfterEffect => throw new NotImplementedException();
    /// <summary>Notice kinds settled by AfterInSync.</summary>
    public static IReadOnlyList<NoticeKind> AfterInSync => throw new NotImplementedException();
    /// <summary>Notice kinds settled by AfterHeldInSync.</summary>
    public static IReadOnlyList<NoticeKind> AfterHeldInSync => throw new NotImplementedException();
    /// <summary>Notice kinds settled by AfterKeepBoth.</summary>
    public static IReadOnlyList<NoticeKind> AfterKeepBoth => throw new NotImplementedException();
}
