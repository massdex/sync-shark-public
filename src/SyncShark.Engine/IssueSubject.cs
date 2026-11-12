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

    /// <summary>The side of the endpoint a turn at path retains: the incoming side, else the item at that path.</summary>
    internal Side? EndpointSide(string path) => Incoming ?? Items.FirstOrDefault(item => item.Path == path)?.Side;

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

    /// <summary>Retry capture only (row 43a): fresh names the same items after a case-only rename. Exact duplicates
    /// aside, items pair one to one under <see cref="IsRespelling"/>; the move must be equal. A wait recorded before
    /// rename subjects named the saved Dropbox counterpart holds only its Local item, and pairs with a fresh subject
    /// that adds exactly one Dropbox item only when that item is the counterpart (identity and key) of the binding
    /// <paramref name="saved"/> held now for the same Local identity; any other added endpoint, including one beside
    /// a new file's wait, stays a miss. Never an item-set equality: tokens, registration and completion stay exact.</summary>
    internal bool HasCaseRenamedItems(IssueSubject fresh, SyncedEntry? saved)
    {
        var recorded = DistinctItems(Items);
        var unmatched = DistinctItems(fresh.Items).ToList();
        if (recorded is [{ Side: Side.Local } legacy] && saved is { IsFolder: false } &&
            unmatched.Where(item => item.Side == Side.Dropbox).ToList() is [var added] &&
            added.Id is { Length: > 0 } && added.Id == saved.DropboxId && PathNames.DropboxKey(added.Path) == saved.DropboxKey)
            unmatched.Remove(added);
        if (!Equals(Move, fresh.Move) || recorded.Length != unmatched.Count) return false;
        foreach (var item in recorded)
        {
            // Every candidate with this side and known id shares the key, so a first match never strands a later one.
            var index = unmatched.FindIndex(other => IsRespelling(item, other));
            if (index < 0) return false;
            unmatched.RemoveAt(index);
        }
        return true;
    }

    /// <summary>Row 43a: fresh is recorded itself, or the same side and known identity respelled only in case.</summary>
    internal static bool IsRespelling(IssueItem recorded, IssueItem fresh) =>
        fresh.Side == recorded.Side && fresh.Id == recorded.Id && (fresh.Path == recorded.Path ||
            recorded.Id is { Length: > 0 } && PathNames.Comparer.Equals(
                PathNames.KeyFor(recorded.Side, recorded.Path), PathNames.KeyFor(fresh.Side, fresh.Path)));

    /// <summary>The one distinct Local item at localKey (a <see cref="PathNames.KeyFor"/> Local key), or null when the
    /// subject names none or more than one Local item, or its Local item is elsewhere: the Local retry capture's
    /// subject contract.</summary>
    public IssueItem? LocalItemAt(string localKey) =>
        DistinctItems(Items.Where(item => item.Side == Side.Local)) is [var local] &&
        PathNames.Comparer.Equals(PathNames.KeyFor(Side.Local, local.Path), localKey) ? local : null;

    /// <summary>The distinct Local items at path's key that name another non-empty file id than the one read there
    /// (B-195). Empty when the subject names only that file.</summary>
    public IssueItem[] OtherLocalAt(string path, string id) => DistinctItems(Items.Where(item => item.Side == Side.Local &&
        PathNames.Comparer.Equals(PathNames.KeyFor(Side.Local, item.Path), PathNames.KeyFor(Side.Local, path)) &&
        item.Id is { Length: > 0 } && item.Id != id));

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
