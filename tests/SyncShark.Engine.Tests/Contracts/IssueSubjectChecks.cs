using System.Text.Json;

namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Checks for IssueSubject (`--engine-checks issues`), written by the issue-policy writer.</summary>
public static class IssueSubjectChecks
{
    private static readonly IssueItem LocalA = new(Side.Local, "a.txt", "id-a");
    private static readonly IssueItem LocalB = new(Side.Local, "b.txt", "id-b");
    private static readonly IssueItem DropboxC = new(Side.Dropbox, "C.txt", "id-c");
    private static readonly DateTimeOffset Modified = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static void Run()
    {
        SameItems();
        DistinctConstruction();
        ReusableProofIdentity();
        ProofJson();
        FirstRunDeletionJson();
    }

    private static void SameItems()
    {
        Same([LocalA, LocalB], [LocalA, LocalB], true, "the same order");
        Same([LocalA, LocalB], [LocalB, LocalA], true, "another order");
        Same([LocalA], [LocalA, LocalA], true, "repetition is ignored");
        Same([], [], true, "two empty subjects");
        Same([LocalA], [LocalA, LocalB], false, "an extra item");
        Same([LocalA, LocalB], [LocalA], false, "a missing item");
        Same([LocalA], [new IssueItem(Side.Local, "a.txt", "id-z")], false, "a different id");
        Same([LocalA], [new IssueItem(Side.Dropbox, "a.txt", "id-a")], false, "a different side");
        Same([LocalA], [new IssueItem(Side.Local, "z.txt", "id-a")], false, "a different path");
        Same([LocalA, null!], [null!, LocalA], true, "a null item compares by equality");
        Same([null!, null!], [null!], true, "a repeated null item is ignored");
        Same([LocalA, null!], [LocalA], false, "a missing null item");
        Same([null!], [], false, "a null item is not an empty set");
        Check.Require(new IssueSubject([LocalA, LocalB], Side.Local).HasSameItems(
                new IssueSubject([LocalB, LocalA], Side.Dropbox)),
            "HasSameItems ignores Incoming");
        var left = new IssueSubject([LocalA, LocalB], null);
        left.HasSameItems(new IssueSubject([LocalB, LocalA], Side.Dropbox));
        Check.Require(left.Items.Count == 2, "HasSameItems leaves its own items unchanged");
    }

    private static void Same(IReadOnlyList<IssueItem> left, IReadOnlyList<IssueItem> right, bool expected,
        string what) =>
        Check.Require(new IssueSubject(left, null).HasSameItems(new IssueSubject(right, null)) == expected,
            "HasSameItems: " + what);

    private static void DistinctConstruction()
    {
        var source = new List<IssueItem> { LocalA, LocalB, LocalA, DropboxC, LocalB };
        var subject = IssueSubject.FromDistinct(source, Side.Dropbox);
        Check.Require(subject.Items.SequenceEqual([LocalA, LocalB, DropboxC]),
            "FromDistinct keeps first-occurrence order and removes repetition");
        Check.Require(subject.Incoming == Side.Dropbox, "FromDistinct keeps the incoming side");
        Check.Require(source.Count == 5, "FromDistinct leaves the supplied sequence unchanged");
        Check.Require(subject.Items is not List<IssueItem>, "FromDistinct materializes its own array");

        var empty = IssueSubject.FromDistinct([], null);
        Check.Require(empty.Items.Count == 0 && empty.Incoming is null, "FromDistinct of nothing is an empty subject");

        var one = IssueSubject.FromDistinct(new[] { LocalA }, Side.Local);
        Check.Require(one.Items.Count == 1 && one.Items[0] == LocalA, "FromDistinct keeps a single item");
    }

    private static void ReusableProofIdentity()
    {
        var proof = new LocalHashProof(10, Modified.UtcTicks, "hash-ab");
        var subject = new IssueSubject([LocalA], null, proof);

        Check.Require(subject.ReusableLocalHash(LocalEntry("a.txt", "id-a", 10)) == "hash-ab",
            "ReusableLocalHash reuses a proof for the exact local path, id, size and UTC ticks");
        Check.Require(subject.ReusableLocalHash(LocalEntry("a.txt", "id-a", 11)) is null,
            "ReusableLocalHash refuses a changed size");
        Check.Require(subject.ReusableLocalHash(LocalEntry("a.txt", "id-a", 10, Modified.AddTicks(1))) is null,
            "ReusableLocalHash refuses changed UTC ticks");
        Check.Require(subject.ReusableLocalHash(LocalEntry("a.txt", "id-z", 10)) is null,
            "ReusableLocalHash refuses a different id");
        Check.Require(subject.ReusableLocalHash(LocalEntry("A.txt", "id-a", 10)) is null,
            "ReusableLocalHash refuses a different path spelling");
        Check.Require(subject.ReusableLocalHash(LocalEntry("a.txt", "id-a", 10, folder: true)) is null,
            "ReusableLocalHash refuses a folder");
        Check.Require(subject.ReusableLocalHash(LocalEntry("a.txt", "id-a", 10, skip: "link")) is null,
            "ReusableLocalHash refuses a skipped entry");
        Check.Require(subject.ReusableLocalHash(null) is null, "ReusableLocalHash refuses a missing entry");

        var dropboxOnly = new IssueSubject([DropboxC], null, proof);
        Check.Require(dropboxOnly.ReusableLocalHash(LocalEntry("C.txt", "id-c", 10)) is null,
            "ReusableLocalHash requires a Local subject item with the entry's path and id");

        Check.Require(new IssueSubject([LocalA], null).ReusableLocalHash(LocalEntry("a.txt", "id-a", 10)) is null,
            "ReusableLocalHash has nothing to reuse without a proof");
        Check.Require(new IssueSubject([LocalA], null, new LocalHashProof(10, Modified.UtcTicks, ""))
                .ReusableLocalHash(LocalEntry("a.txt", "id-a", 10)) is null,
            "ReusableLocalHash refuses an empty hash");
        Check.Require(new IssueSubject([LocalA], null, new LocalHashProof(10, Modified.UtcTicks, null!))
                .ReusableLocalHash(LocalEntry("a.txt", "id-a", 10)) is null,
            "ReusableLocalHash refuses a null hash");
        Check.Require(new IssueSubject([LocalA], null, new LocalHashProof(-1, Modified.UtcTicks, "hash-ab"))
                .ReusableLocalHash(LocalEntry("a.txt", "id-a", -1)) is null,
            "ReusableLocalHash refuses a negative proof size even when the entry matches it");

        Check.Require(new IssueSubject([LocalA], Side.Dropbox, proof).HasSameItems(
                new IssueSubject([LocalA], null, new LocalHashProof(10, Modified.UtcTicks, "hash-other"))),
            "HasSameItems ignores the proof");
    }

    private static void ProofJson()
    {
        Check.Require(!JsonSerializer.Serialize(new IssueSubject([LocalA], null))
                .Contains("LocalHash", StringComparison.Ordinal),
            "JSON omits an absent proof");

        var proof = new LocalHashProof(10, Modified.UtcTicks, "hash-ab");
        var json = JsonSerializer.Serialize(new IssueSubject([LocalA], null, proof));
        Check.Require(json.Contains("\"LocalHash\"", StringComparison.Ordinal), "JSON writes a supplied proof");
        var read = JsonSerializer.Deserialize<IssueSubject>(json)!;
        Check.Require(read.LocalHash == proof, "a proof round-trips through JSON");
        Check.Require(read.ReusableLocalHash(LocalEntry("a.txt", "id-a", 10)) == "hash-ab",
            "a round-tripped proof still authorizes the exact entry");

        var absent = JsonSerializer.Deserialize<IssueSubject>("{\"Items\":[],\"Incoming\":null}")!;
        Check.Require(absent.LocalHash is null, "stored JSON without a proof decodes with no proof");
        Check.Require(absent.ReusableLocalHash(LocalEntry("a.txt", "id-a", 10)) is null,
            "stored JSON without a proof authorizes nothing");

        const string items = "\"Items\":[{\"Side\":0,\"Path\":\"a.txt\",\"Id\":\"id-a\"}],\"Incoming\":null";
        var nullHash = JsonSerializer.Deserialize<IssueSubject>("{" + items + ",\"LocalHash\":{\"Size\":10," +
            "\"ModifiedTicks\":" + Modified.UtcTicks + ",\"Hash\":null}}")!;
        Check.Require(nullHash.ReusableLocalHash(LocalEntry("a.txt", "id-a", 10)) is null,
            "a stored null hash is refused, not trusted");

        var emptyHash = JsonSerializer.Deserialize<IssueSubject>("{" + items + ",\"LocalHash\":{\"Size\":10," +
            "\"ModifiedTicks\":" + Modified.UtcTicks + ",\"Hash\":\"\"}}")!;
        Check.Require(emptyHash.ReusableLocalHash(LocalEntry("a.txt", "id-a", 10)) is null,
            "a stored empty hash is refused, not trusted");
    }

    private static void FirstRunDeletionJson()
    {
        Check.Require(!JsonSerializer.Serialize(new IssueSubject([LocalA], null))
                .Contains("FirstRunDeletion", StringComparison.Ordinal),
            "JSON omits a None first-run deletion");

        var older = JsonSerializer.Deserialize<IssueSubject>(
            "{\"Items\":[{\"Side\":0,\"Path\":\"a.txt\",\"Id\":\"id-a\"}],\"Incoming\":null}")!;
        Check.Require(older.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None,
            "stored JSON without a first-run deletion decodes as None");

        foreach (var kind in new[]
                 {
                     IssueSubject.FirstRunDeletionKind.Item,
                     IssueSubject.FirstRunDeletionKind.UnscannedSubtree,
                     IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice
                 })
        {
            var json = JsonSerializer.Serialize(new IssueSubject([LocalA], null, FirstRunDeletion: kind));
            Check.Require(json.Contains("\"FirstRunDeletion\"", StringComparison.Ordinal),
                "JSON writes first-run deletion " + kind);
            Check.Require(JsonSerializer.Deserialize<IssueSubject>(json)!.FirstRunDeletion == kind,
                "first-run deletion " + kind + " round-trips through JSON");
        }
    }

    private static Entry LocalEntry(string path, string id, long size, DateTimeOffset? modified = null,
        bool folder = false, string? skip = null) =>
        new(path, folder, size, modified ?? Modified, id, "", "", skip);
}
