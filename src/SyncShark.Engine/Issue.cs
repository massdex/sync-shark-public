namespace Massdex.SyncShark.Engine;

/// <summary>One entry on a pair's issue list (D28, section 2.4).</summary>
public sealed record Issue(long Id, IssueKind Kind, TextKey Cause, string Path, IssueSubject Subject,
    DateTimeOffset At, bool Dismissed, bool Ignored = false)
{
    /// <summary>Whether any subject key is self-or-under this scope; item-less operational notices use their path.
    /// Empty scope includes every issue, including dismissed ones, independently of holding policy.</summary>
    public bool IsUnder(string scopeKey)
    {
        if (scopeKey.Length == 0) return true;
        if (Subject.Items.Count == 0)
            return Kind is IssueKind.Failure or IssueKind.Tie or IssueKind.NameClash &&
                PathNames.IsSelfOrUnderKey(PathNames.Key(Path), scopeKey);
        foreach (var item in Subject.Items)
            if (PathNames.IsSelfOrUnderKey(PathNames.KeyFor(item.Side, item.Path), scopeKey)) return true;
        return false;
    }
}
