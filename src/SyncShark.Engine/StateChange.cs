namespace Massdex.SyncShark.Engine;

/// <summary>Everything one committed effect changes, applied atomically by Storage (section 3.5).</summary>
public sealed record StateChange(
    IReadOnlyList<SyncedEntry> Record,
    IReadOnlyList<(Side Side, string Key)> ForgetSelf,
    IReadOnlyList<(Side Side, string Key)> ForgetSubtrees,
    IReadOnlyList<RenameApplied> Renames,
    IReadOnlyList<(IssueKind Kind, TextKey Cause, string Path, IssueSubject Subject)> Raise,
    IReadOnlyList<(string Path, IReadOnlyList<IssueKind> Kinds)> Close,
    IReadOnlyList<IssueItem> FirstRunAcknowledged)
{
    /// <summary>Exact old bindings whose differing provider paths were both observed absent before Record.</summary>
    public IReadOnlyList<SyncedEntry> StaleBindings { get; init; } = [];

    /// <summary>Absent bilateral roots: forget only bindings inside both corresponding subtrees.</summary>
    public IReadOnlyList<(string LocalKey, string DropboxKey)> ForgetBothSubtrees { get; init; } = [];

    /// <summary>A change that records, forgets and closes nothing.</summary>
    public static StateChange Empty { get; } = new([], [], [], [], [], [], []);
}
