namespace Massdex.SyncShark.Engine;

/// <summary>Named issue-kind sets shared by the call sites that close issues after a completed operation
/// (section 2.4).</summary>
public static class IssueClosure
{
    /// <summary>Kinds a completed operation closes when its result is not in sync: an applied exclusion removal, a
    /// completed rename or a published pending recovery.</summary>
    public static IReadOnlyList<IssueKind> AfterEffect { get; } = Array.AsReadOnly(new[] { IssueKind.Failure });

    /// <summary>Kinds closed after an ordinary in-sync result.</summary>
    public static IReadOnlyList<IssueKind> AfterInSync { get; } =
        Array.AsReadOnly(new[] { IssueKind.Failure, IssueKind.NameClash });

    /// <summary>Kinds closed when a held row becomes in sync; successful copy, delete and move effects close these
    /// too.</summary>
    public static IReadOnlyList<IssueKind> AfterHeldInSync { get; } =
        Array.AsReadOnly(new[] { IssueKind.Failure, IssueKind.Tie, IssueKind.NameClash });

    /// <summary>Kinds closed after a KeepBoth resolution.</summary>
    public static IReadOnlyList<IssueKind> AfterKeepBoth { get; } =
        Array.AsReadOnly(new[] { IssueKind.Failure, IssueKind.Tie });
}
