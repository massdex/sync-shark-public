namespace Massdex.SyncShark.V2;

/// <summary>
/// One instance owns the scheduler and state for one host lifetime. Dispose must release the store before
/// the separately supplied product-lock lease is released. Stop must reach a durable safe point.
/// </summary>
public interface IAutoSyncHostEngine : IAsyncDisposable
{
    Task<object> ExecuteAsync(AutoSyncHostRequest request, CancellationToken cancellationToken);
    Task StopAsync();
}
