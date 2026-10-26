namespace Massdex.SyncShark.V2;

/// <summary>A rebuildable category to stop syncing at or below a pair-relative folder; empty means the whole pair.</summary>
public sealed record RebuildableChoice(string Id, string Prefix = "")
{
    public bool Matches(RebuildableFolder folder) =>
        string.Equals(Id, folder.Category, StringComparison.OrdinalIgnoreCase) &&
        (Prefix.Length == 0 || PairPath.IsSelfOrUnder(folder.Path, Prefix));
}
