namespace Massdex.SyncShark.V2;

/// <summary>Bounded fresh metadata observations for restore receipt verification.</summary>
public interface IEndpointMetadataPages
{
    Task<IReadOnlyDictionary<string, ItemState?>> ObservePageAsync(IReadOnlyList<string> paths, CancellationToken ct);
}
