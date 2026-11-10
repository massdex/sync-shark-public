using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark.Engine;

/// <summary>What an issue is about: every candidate involved. Incoming = the side whose item was refused
/// (null: no history, D20).</summary>
public sealed record IssueSubject(
    IReadOnlyList<IssueItem> Items, Side? Incoming,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    LocalHashProof? LocalHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    IssueSubject.FirstRunDeletionKind FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None)
{
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

    /// <summary>Returns the conflict proof only while its exact local identity and file metadata still match.</summary>
    public string? ReusableLocalHash(Entry? local) =>
        local is { IsFolder: false, Skip: null, Path: not null, Id: not null } &&
        Items is not null && LocalHash is { } proof && !string.IsNullOrEmpty(proof.Hash) &&
        proof.Size >= 0 && proof.Size == local.Size && proof.ModifiedTicks == local.Modified.UtcTicks &&
        Items.Any(item => item is { Side: Side.Local } &&
            string.Equals(item.Path, local.Path, StringComparison.Ordinal) &&
            string.Equals(item.Id, local.Id, StringComparison.Ordinal))
                ? proof.Hash : null;

    /// <summary>True when both subjects name exactly the same items and the same unknown move; order and repetition
    /// are ignored, Incoming is not compared (D20 continuation). Two unknown moves of the same items to different
    /// destinations are different responsibilities.</summary>
    public bool HasSameItems(IssueSubject other) =>
        Equals(Move, other.Move) && new HashSet<IssueItem>(Items).SetEquals(other.Items);

    /// <summary>Every item of this subject, by side, path and identity, is in other, with the same move.</summary>
    public bool HasItemsWithin(IssueSubject other) =>
        Equals(Move, other.Move) && new HashSet<IssueItem>(other.Items).IsSupersetOf(Items);

    /// <summary>Indexes subjects by their item sets without retaining a second copy of the items.</summary>
    public static IEqualityComparer<IssueSubject> ItemSetComparer { get; } = new ItemComparer();

    private sealed class ItemComparer : IEqualityComparer<IssueSubject>
    {
        public bool Equals(IssueSubject? left, IssueSubject? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.HasSameItems(right);

        public int GetHashCode(IssueSubject subject)
        {
            if (subject.Items.Count == 1) return subject.Items[0]?.GetHashCode() ?? 0;
            var hash = 0;
            foreach (var item in new HashSet<IssueItem>(subject.Items)) hash ^= item?.GetHashCode() ?? 0;
            return hash;
        }
    }

    /// <summary>The distinct items of a supplied sequence, in first-occurrence order; the one distinct-array rule.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IssueItem[] DistinctItems(IEnumerable<IssueItem> items) => items.Distinct().ToArray();

    /// <summary>Builds one subject from the distinct items of a supplied sequence, keeping first-occurrence order.
    /// Only existing normalization sites call this; ordinary raises construct their items directly.</summary>
    public static IssueSubject FromDistinct(IEnumerable<IssueItem> items, Side? incoming) =>
        new(DistinctItems(items), incoming);
}
