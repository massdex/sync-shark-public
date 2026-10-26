namespace Massdex.SyncShark.V2;

/// <summary>A configured pair and its rules.</summary>
public sealed record PairDef(string PairId, string Name, RootDef A, RootDef B, IReadOnlyList<string> Excluded,
    IReadOnlyList<string> IgnoreRules, bool Enabled);
