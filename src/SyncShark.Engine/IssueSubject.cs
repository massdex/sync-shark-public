using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

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

    // Persisted by number in issue rows; values are a stored format.
    /// <summary>Unfinished first-run provenance retained on the subject. None is the ordinary case and is omitted
    /// from stored JSON so ordinary subjects keep their shape (P4-05 S8).</summary>
    public enum FirstRunDeletionKind
    {
        None = 0,
        Item = 1,
        UnscannedSubtree = 2,
        PreserveWithHistoryNotice = 3
    }

    /// <summary>KeepBoth records the two original items first, then the labelled copies, so a kept-both subject's
    /// label items start after this many.</summary>
    public const int KeptBothOriginals = 2;

    /// <summary>The intended operation of a Dropbox move whose effect is unknown: the expected identity from the
    /// pre-dispatch observation, the original source, the final destination, the case-rename intermediate when the
    /// move has two hops, and the item kind. Operation data, never an observed item; null for every other subject
    /// and omitted from stored JSON.</summary>
    public sealed record UnknownMove(string ExpectedId, string Source, string Destination, string? Intermediate,
        bool IsFolder);

    /// <summary>The unknown move this Failure retains; null for every other subject.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UnknownMove? Move { get; init; }

    /// <summary>The Dropbox version this issue pins for a retry from the pre-dispatch observation: namespace, rooted
    /// original path and the original entry with its id, revision, size/hash and metadata. Null for every other
    /// subject; omitted from stored JSON.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentityLocation? DropboxVersion { get; init; }

    /// <summary>True when the pinned Dropbox version came from an attributed deletion rather than an observed-only
    /// check; false is the ordinary case and omitted from stored JSON.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DropboxVersionAttributed { get; init; }

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
