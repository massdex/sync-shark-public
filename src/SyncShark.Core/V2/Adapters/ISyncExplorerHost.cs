namespace Massdex.SyncShark;

/// <summary>The Explorer-status surface the window calls.</summary>
public interface ISyncExplorerHost
{
    /// <summary>Checks fast discovery for a pair.</summary>
    Task<SyncDiscoveryCheckResult> CheckFastDiscoveryAsync(string pairId, CancellationToken cancellationToken = default);

    /// <summary>Loads a page of Explorer status items for a pair.</summary>
    Task<IReadOnlyList<SyncExplorerItemStatus>> LoadExplorerStatusPageAsync(string pairId, string? afterPath = null, CancellationToken cancellationToken = default);
}
