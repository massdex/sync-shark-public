using System.Text.Json.Serialization;

namespace Massdex.SyncShark.V2;

/// <summary>
/// The retained prepare: the choices digest, per-choice effects, the reviewed deletes' victims, the frozen
/// rebuildable set and the both-sides matched set. The rebuildable set is the only source the apply may draw
/// rebuildable paths from; RebuildableChoices records the reviewed scope for inspection (the digest enforces it).
/// The matched set is the reviewed apply's baseline for post-prepare deletion propagation.
/// </summary>
internal sealed record Prepared(string Digest, IReadOnlyDictionary<string, string> Paths,
    IReadOnlyList<StoredChoice> OneSided, IReadOnlyList<string> DeleteVictims, int DeleteVictimCount,
    IReadOnlyList<StoredRebuildable> Rebuildables, IReadOnlyList<HeldDeletion> HeldDeletions,
    IReadOnlyList<StoredMatch> Matched, IReadOnlyList<RebuildableChoice>? RebuildableChoices = null,
    IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<SyncCliCarriedIgnoredData> CarriedIgnoredDeletes { get; init; } = [];
    [JsonIgnore] internal IPrepareDatabase? Database { get; init; }
    public IReadOnlyList<StoredInventoryCursor> InventoryCursors { get; init; } = [];
    public string? InventoryScope { get; init; }
    // Narrowing changes the review digest, never the approved choices. This independent binding
    // excludes large held sets, whose authorization remains in the filtered SQLite rows.
    public string? NarrowedChoicesDigest { get; init; }
    public IReadOnlyList<string> NarrowedIgnoreRules { get; init; } = [];
    /// <summary>
    /// Set when a review action rewrote the choices without re-detecting the folders they cover (Choose folder).
    /// The retained review no longer describes what the new rule applies to, so an apply must wait for a prepare
    /// that detects folders under the new prefix.
    /// </summary>
    public bool NeedsReprepare { get; init; }
}
