namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for Dispatcher.</summary>
public static class DispatcherChecks
{
    public static Task RelocatedDeleteAsync() => throw new NotImplementedException();

    public static Task DeferredSettlementAsync() => throw new NotImplementedException();
    public static Task SelectedDigestAsync() => throw new NotImplementedException();
    public static Task BodyWaitAsync() => throw new NotImplementedException();
    public static Task FinishCursorAsync() => throw new NotImplementedException();

    /// <summary>Check upload receipt validation across the provider modules.</summary>
    public static Task UploadReceiptAsync() => throw new NotImplementedException();
    /// <summary>Check throttled write at integration.</summary>
    public static Task ThrottledWriteAsync() => throw new NotImplementedException();
    /// <summary>Check deferred create at integration.</summary>
    public static Task DeferredCreateAsync() => throw new NotImplementedException();
    /// <summary>Verifies the Dispatcher contract through the module surface.</summary>
    public static Task RunAsync() => throw new NotImplementedException();
}
