namespace Massdex.SyncShark.Engine;

/// <summary>Selected identity and path footprint for moves/deletes; creates carry a name only.</summary>
public sealed record JobMember(string? Id, string Path, string? Destination)
{
    /// <summary>Request a new name rather than adopting an existing folder.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExclusiveCreate { get; init; }
    /// <summary>Original action for this launch member; absent on legacy jobs and never inferred from the route.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ActionKind? Origin { get; init; }
}
