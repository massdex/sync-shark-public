namespace Massdex.SyncShark.Engine;

/// <summary>An excluded path on one side (D26). Keep: exclude without removing anything.
/// KeptBy is set only on a KEEP leg derived from a bound row's keep column; it never reaches def_json.
/// Token and DropboxId are set only on an explicit never-synced KEEP rule so the object can be found again after it moves.</summary>
public sealed record Exclusion(Side Side, string Path, bool Keep)
{
    /// <summary>The bound row and relative tail this leg is derived from; null for a configured path rule.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (long Row, string Tail)? KeptBy { get; init; }

    /// <summary>Opaque rule identity for a never-synced KEEP: written into the object's Sync Shark stream on the PC (or only as the rule key on Dropbox). Null for a plain path rule.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; init; }

    /// <summary>Stable Dropbox entry id of a never-synced Dropbox KEEP rule; survives a move. Null for a PC-only rule.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DropboxId { get; init; }
}
