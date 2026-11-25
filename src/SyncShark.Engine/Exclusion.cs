namespace Massdex.SyncShark.Engine;

/// <summary>An excluded path on one side (D26). Keep: exclude without removing anything.
/// KeptBy is set only on a KEEP leg derived from a bound row's keep column; it never reaches def_json.</summary>
public sealed record Exclusion(Side Side, string Path, bool Keep)
{
    /// <summary>The bound row and relative tail this leg is derived from; null for a configured path rule.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (long Row, string Tail)? KeptBy { get; init; }
}
