namespace Massdex.SyncShark.Engine;

/// <summary>One entry on a pair's issue list (D28, section 2.4).</summary>
public sealed record Issue(long Id, IssueKind Kind, TextKey Cause, string Path, IssueSubject Subject,
    DateTimeOffset At, bool Dismissed, bool Ignored = false)
{
    /// <summary>The exact reviewed-version token, populated only by the public notice read projection.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Reviewed { get; init; }

    /// <summary>Exact resolution identity, excluding visibility and read-projection metadata.</summary>
    public bool Matches(Issue expected) =>
        this with { Subject = expected.Subject, Ignored = expected.Ignored, Reviewed = expected.Reviewed } == expected &&
        Subject.Incoming == expected.Subject.Incoming && Subject.HasSameItems(expected.Subject) &&
        Subject.LocalHash == expected.Subject.LocalHash && Subject.FirstRunDeletion == expected.Subject.FirstRunDeletion;

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
