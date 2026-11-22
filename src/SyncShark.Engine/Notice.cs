using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

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
        Subject.FirstRunDeletion == expected.Subject.FirstRunDeletion;

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

/// <summary>What an issue is about: every candidate involved. Incoming = the side whose item was refused
/// (null: no history, D20).</summary>
public sealed record IssueSubject(
    IReadOnlyList<NoticeItem> Items, Side? Incoming,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    IssueSubject.FirstRunDeletionKind FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None)
{
    /// <summary>The observed program holding the file, if known; display metadata in subject JSON, not part of the reviewed identity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Holder { get; init; }

    /// <summary>Unfinished first-run provenance retained on the subject. None is the ordinary case and is omitted
    /// from stored JSON so ordinary subjects keep their shape (P4-05 S8).</summary>
    public enum FirstRunDeletionKind
    {
        None,
        Item,
        UnscannedSubtree,
        PreserveWithHistoryNotice
    }

    /// <summary>The intended operation of a Dropbox move whose effect is unknown: the expected identity from the
    /// pre-dispatch observation, the original source, the final destination, the case-rename intermediate when the
    /// move has two hops, and the item kind. Operation data, never an observed item; null for every other subject
    /// and omitted from stored JSON.</summary>
    public sealed record UnknownMove(string ExpectedId, string Source, string Destination, string? Intermediate,
        bool IsFolder);

    /// <summary>The unknown move this Failure retains; null for every other subject.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UnknownMove? Move { get; init; }

    /// <summary>The side of the endpoint a turn at path retains: the incoming side, else the item at that path.</summary>
    internal Side? EndpointSide(string path) => Incoming ?? Items.FirstOrDefault(item => item.Path == path)?.Side;

    /// <summary>True when both subjects name exactly the same items and the same unknown move; order and repetition
    /// are ignored, Incoming is not compared (D20 continuation). Two unknown moves of the same items to different
    /// destinations are different responsibilities.</summary>
    public bool HasSameItems(IssueSubject other) =>
        Equals(Move, other.Move) && new HashSet<NoticeItem>(Items).SetEquals(other.Items);

    /// <summary>Every item of this subject, by side, path and identity, is in other, with the same move.</summary>
    public bool HasItemsWithin(IssueSubject other) =>
        Equals(Move, other.Move) && new HashSet<NoticeItem>(other.Items).IsSupersetOf(Items);

    /// <summary>The one distinct Local item at localKey (a <see cref="PathNames.KeyFor"/> Local key), or null when the
    /// subject names none or more than one Local item, or its Local item is elsewhere: the Local retry capture's
    /// subject contract.</summary>
    public NoticeItem? LocalItemAt(string localKey) =>
        DistinctItems(Items.Where(item => item.Side == Side.Local)) is [var local] &&
        PathNames.Comparer.Equals(PathNames.KeyFor(Side.Local, local.Path), localKey) ? local : null;

    /// <summary>The distinct items of a supplied sequence, in first-occurrence order; the one distinct-array rule.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NoticeItem[] DistinctItems(IEnumerable<NoticeItem> items) => items.Distinct().ToArray();

    /// <summary>Builds one subject from the distinct items of a supplied sequence, keeping first-occurrence order.
    /// Only existing normalization sites call this; ordinary raises construct their items directly.</summary>
    public static IssueSubject FromDistinct(IEnumerable<NoticeItem> items, Side? incoming) =>
        new(DistinctItems(items), incoming);
}

/// <summary>One item an issue is about.</summary>
public sealed record NoticeItem(Side Side, string Path, string Id);
