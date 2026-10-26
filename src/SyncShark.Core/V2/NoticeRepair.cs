namespace Massdex.SyncShark.V2;

/// <summary>Identity-bound repair evidence, stored in the existing notice detail payload.</summary>
/// <param name="RetainedStaging">Bounded (20) list of excluded staging folders this link's attempts created; never deleted.</param>
public sealed record NoticeRepair(Side Side, ItemState Item, string? Destination = null,
    IReadOnlyList<string>? RetainedStaging = null);
