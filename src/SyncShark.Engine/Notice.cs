using System.Text.Json.Serialization;

namespace Massdex.SyncShark.Engine;

/// <summary>One entry on a pair's issue list. A PutBack notice keeps its original failure cause and owns the identified edit’s outstanding put-back, even when ignored.</summary>
public sealed record Notice(long Id, NoticeKind Kind, TextKey Cause, string Path, IssueSubject Subject,
    DateTimeOffset At, bool Dismissed, bool Ignored = false)
{
    /// <summary>The exact reviewed-version token, populated only by the public notice read projection.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Reviewed { get; init; }

    /// <summary>Allowed resolutions projected for this read, not persisted with the notice.</summary>
    [JsonIgnore]
    public IReadOnlyList<ResolutionChoice>? Choices { get; init; }

    /// <summary>Number of links in the notice display projection.</summary>
    [JsonIgnore]
    public int? LinkCount { get; init; }
    /// <summary>Top folders in the notice display projection.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? LinkFolders { get; init; }

    /// <summary>Exact resolution identity, excluding visibility and read-projection metadata.</summary>
    public bool Matches(Notice expected) =>
        this with { Subject = expected.Subject, Ignored = expected.Ignored, Reviewed = expected.Reviewed, Choices = expected.Choices,
            LinkCount = expected.LinkCount, LinkFolders = expected.LinkFolders } == expected &&
        Subject.Incoming == expected.Subject.Incoming && Subject.HasSameItems(expected.Subject) &&
        Subject.FirstRunDeletion == expected.Subject.FirstRunDeletion &&
        Subject.DropboxVersion == expected.Subject.DropboxVersion &&
        Subject.DropboxVersionAttributed == expected.Subject.DropboxVersionAttributed;

    /// <summary>Whether any subject key is self-or-under this scope; item-less operational notices use their path.
    /// Empty scope includes every issue, including dismissed ones, independently of holding policy.</summary>
    public bool IsUnder(string scopeKey)
    {
        if (scopeKey.Length == 0) return true;
        if (Subject.Items.Count == 0)
            return Kind is NoticeKind.Failure or NoticeKind.PutBack or NoticeKind.Tie or NoticeKind.NameClash &&
                PathNames.IsSelfOrUnderKey(PathNames.Key(Path), scopeKey);
        foreach (var item in Subject.Items)
            if (PathNames.IsSelfOrUnderKey(PathNames.KeyFor(item.Side, item.Path), scopeKey)) return true;
        return false;
    }
}
