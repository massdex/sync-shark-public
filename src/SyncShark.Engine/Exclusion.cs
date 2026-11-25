namespace Massdex.SyncShark.Engine;

/// <summary>An excluded path on one side (D26). Keep: exclude without removing anything.
/// KeptBy is set only on a KEEP leg derived from a bound row's keep column; it never reaches def_json.</summary>
public sealed record Exclusion(Side Side, string Path, bool Keep)
{
    // A detached leg keeps the rule selectable even with no surviving owner on its side.
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RuleId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Side? Origin { get; init; }
    internal string? Token => KeptBy?.Id ?? RuleId;
    internal Side ConfiguredSide => KeptBy?.Origin ?? Origin ?? Side;
    /// <summary>The bound row and relative tail this leg is derived from; null for a configured path rule.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (string Id, Side Origin, string Tail, long Row, int Index)? KeptBy { get; init; }
}
