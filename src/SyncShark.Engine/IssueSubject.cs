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

    /// <summary>Returns the conflict proof only while its exact local identity and file metadata still match.</summary>
    public string? ReusableLocalHash(Entry? local) =>
        local is { IsFolder: false, Skip: null, Path: not null, Id: not null } &&
        Items is not null && LocalHash is { } proof && !string.IsNullOrEmpty(proof.Hash) &&
        proof.Size >= 0 && proof.Size == local.Size && proof.ModifiedTicks == local.Modified.UtcTicks &&
        Items.Any(item => item is { Side: Side.Local } &&
            string.Equals(item.Path, local.Path, StringComparison.Ordinal) &&
            string.Equals(item.Id, local.Id, StringComparison.Ordinal))
                ? proof.Hash : null;

    /// <summary>True when both subjects name exactly the same items; order and repetition are ignored, Incoming is
    /// not compared (D20 continuation).</summary>
    public bool HasSameItems(IssueSubject other) => new HashSet<IssueItem>(Items).SetEquals(other.Items);

    /// <summary>The distinct items of a supplied sequence, in first-occurrence order; the one distinct-array rule.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IssueItem[] DistinctItems(IEnumerable<IssueItem> items) => items.Distinct().ToArray();

    /// <summary>Builds one subject from the distinct items of a supplied sequence, keeping first-occurrence order.
    /// Only existing normalization sites call this; ordinary raises construct their items directly.</summary>
    public static IssueSubject FromDistinct(IEnumerable<IssueItem> items, Side? incoming) =>
        new(DistinctItems(items), incoming);
}
