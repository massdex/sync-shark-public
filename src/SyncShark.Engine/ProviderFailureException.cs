namespace Massdex.SyncShark.Engine;

/// <summary>Allowlisted provider failure facts retained across item-error mapping; never carries a response body.</summary>
public sealed class ProviderFailureException(string route, int? httpStatus, string tag) : Exception(tag)
{
    public string Route { get; } = route;
    public int? HttpStatus { get; internal set; } = httpStatus;
    public string Tag { get; } = tag;
}
