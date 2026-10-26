namespace Massdex.SyncShark.V2;

/// <summary>One path seen on both sides at a prepare, referencing each side's observed state without allocating eager stamps.</summary>
public sealed record MatchedPair(string Path, ItemState A, ItemState B);
