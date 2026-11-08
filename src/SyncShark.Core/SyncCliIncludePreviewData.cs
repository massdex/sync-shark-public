namespace Massdex.SyncShark;

/// <summary>Ephemeral hypothetical review; not a plan, baseline, deletion authority or policy mutation.</summary>
public sealed record SyncCliIncludePreviewData(
    string PairId, string RelativePath, bool Complete, int ObservedA, int ObservedB,
    SyncCliIncludePreviewSummary Summary, IReadOnlyList<SyncCliIncludePreviewExample> Examples,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Rules);

public sealed record SyncCliIncludePreviewSummary(
    int CopyAToB, int CopyBToA, int KeepBoth,
    int MatchingFolders, int IdenticalFiles, int HeldUnknown,
    int Deletes = 0, int NewestWins = 0, int HeldTies = 0);

public sealed record SyncCliIncludePreviewExample(string RelativePath, string Recommendation, string Reason);
