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
        UnknownMoveIdentity();
        CaseRenamedItems();
        PendingMembership();
    }

    // Row 43a: the capture-only pairing accepts exactly a case-only respelling of the same items and nothing wider.
    private static void CaseRenamedItems()
    {
        var local = new IssueItem(Side.Local, "Parent/file.txt", "local-id");
        var dropbox = new IssueItem(Side.Dropbox, "Parent/file.txt", "dropbox-id");
        var recorded = new IssueSubject([local, dropbox], null);
        var binding = new SyncedEntry(PathNames.Key(local.Path), local.Path, PathNames.DropboxKey(dropbox.Path), dropbox.Path,
            false, local.Id, 1, 0, dropbox.Id, "hash");
        var move = new IssueSubject.UnknownMove("dropbox-id", "Parent/file.txt", "Parent/File.txt", null, false);
        IssueItem Spelled(IssueItem item, string path) => item with { Path = path };
        foreach (var (fresh, expected, what) in new (IssueSubject Fresh, bool Expected, string What)[]
        {
            (recorded, true, "the same items"),
            (new IssueSubject([Spelled(local, "Parent/File.txt"), dropbox], null), true, "a Local case-only respelling"),
            (new IssueSubject([local, Spelled(dropbox, "PARENT/file.TXT")], null), true, "a Dropbox case-only respelling"),
            (new IssueSubject([Spelled(dropbox, "Parent/FILE.txt"), Spelled(local, "Parent/File.txt"), Spelled(dropbox, "Parent/FILE.txt")],
                Side.Local),
                true, "both respelled, reordered, with an exact duplicate and another Incoming"),
            (new IssueSubject([Spelled(local, "Parent/File.txt")], null), false, "a missing endpoint"),
            (new IssueSubject([Spelled(local, "Parent/File.txt"), dropbox, new IssueItem(Side.Dropbox, "Parent/other.txt", "other")],
                null), false, "an extra endpoint"),
            (new IssueSubject([Spelled(local, "Parent/File.txt"), Spelled(dropbox, "Parent/File.txt"), dropbox], null), false,
                "two spellings of one Dropbox item for one recorded"),
            (new IssueSubject([local with { Id = "replaced-id" }, dropbox], null), false, "a changed identity at the same key"),
            (new IssueSubject([Spelled(local, "Parent/renamed.txt"), dropbox], null), false, "a move that is not case-only"),
            (new IssueSubject([local, dropbox], null) { Move = move }, false, "another unknown-move responsibility"),
        })
            Check.Require(recorded.HasCaseRenamedItems(fresh, binding) == expected, "43a case-rename pairing: " + what);
        var unknown = new IssueSubject([local, dropbox with { Id = "" }], null);
        Check.Require(!unknown.HasCaseRenamedItems(new IssueSubject([local, new IssueItem(Side.Dropbox, "Parent/File.txt", "")],
                null), binding) && unknown.HasCaseRenamedItems(unknown with { Items = [dropbox with { Id = "" }, local] }, binding),
            "43a: an unknown identity pairs only at its exact spelling, never by case");
        // A wait recorded before rename subjects named the saved Dropbox counterpart holds only its Local item.
        var legacy = new IssueSubject([local], null);
        foreach (var (fresh, expected, what) in new (IssueSubject Fresh, bool Expected, string What)[]
        {
            (new IssueSubject([Spelled(local, "Parent/File.txt"), dropbox], null), true, "respelled, with its counterpart"),
            (new IssueSubject([local, dropbox], null), true, "same spelling, with its counterpart"),
            (new IssueSubject([Spelled(local, "Parent/File.txt")], null), true, "respelled alone"),
            (new IssueSubject([Spelled(local, "Parent/File.txt"), dropbox, new IssueItem(Side.Dropbox, "Parent/other.txt",
                "other")], null), false, "two Dropbox items"),
            (new IssueSubject([local with { Id = "replaced-id" }, dropbox], null), false, "another Local identity"),
            (new IssueSubject([Spelled(local, "Parent/renamed.txt"), dropbox], null), false, "a move that is not case-only"),
            (new IssueSubject([Spelled(local, "Parent/File.txt"), dropbox], null) { Move = move }, false, "another move"),
            (new IssueSubject([Spelled(local, "Parent/File.txt"), dropbox with { Id = "other" }], null), false,
                "a Dropbox endpoint that is not the held counterpart"),
            (new IssueSubject([Spelled(local, "Parent/File.txt"), Spelled(dropbox, "Parent/other.txt")], null), false,
                "the counterpart's identity at another key"),
        })
            Check.Require(legacy.HasCaseRenamedItems(fresh, binding) == expected, "43a legacy Local-only wait: " + what);
        // A new file's Local-only wait has no binding: a newly appeared Dropbox item keeps today's exact miss.
        Check.Require(!legacy.HasCaseRenamedItems(new IssueSubject([local, dropbox], null), null) &&
            !legacy.HasCaseRenamedItems(new IssueSubject([local, dropbox], null), binding with { LocalId = "replaced-id" }) &&
            legacy.HasCaseRenamedItems(new IssueSubject([Spelled(local, "Parent/File.txt")], null), null),
            "43a: the legacy endpoint is accepted only as the counterpart of the binding held for that Local identity");
        Check.Require(!new IssueSubject([dropbox], null).HasCaseRenamedItems(new IssueSubject([dropbox, local], null), binding),
            "43a: only a Local-only wait takes the legacy shape");
        Check.Require(!recorded.HasSameItems(new IssueSubject([Spelled(local, "Parent/File.txt"), dropbox], null)),
            "43a: item-set equality stays exact; only the retry capture relaxes spelling");
    }

    // Row 43a (47e): a completed pending's losing Local item belongs to the wait when it is a recorded item itself or
    // that item respelled only in case; every other identity, key, side or id-less respelling stays outside.
    private static void PendingMembership()
    {
        var local = new IssueItem(Side.Local, "Parent/file.txt", "local-id");
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, "Parent/file.txt", "dropbox-id"), local], null);
        var unknown = new IssueSubject([local with { Id = "" }], null);
        var respelled = local with { Path = "Parent/File.txt" };
        foreach (var (owner, item, expected, what) in new (IssueSubject Owner, IssueItem Item, bool Expected, string What)[]
        {
            (subject, local, true, "the recorded item itself"),
            (subject, respelled, true, "its case-only respelling"),
            (subject, respelled with { Id = "other-id" }, false, "another identity at the same case-insensitive spelling"),
            (subject, local with { Path = "Parent/renamed.txt" }, false, "the same identity at another key"),
            (subject, new IssueItem(Side.Local, "Parent/file.txt", "dropbox-id"), false, "the Dropbox item's identity on Local"),
            (unknown, local with { Id = "" }, true, "an id-less item at its exact spelling"),
            (unknown, respelled with { Id = "" }, false, "an id-less respelling"),
        })
            Check.Require(owner.HoldsRespelling(item) == expected, "43a pending membership: " + what);
        // Counted work: the exact path allocates nothing; a respelling allocates exactly its two key reads.
        static long Allocated(Action work)
        {
            work();
            var before = GC.GetAllocatedBytesForCurrentThread();
            work();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Action exact = () => subject.HoldsRespelling(local);
        Action respelling = () => subject.HoldsRespelling(respelled);
        Action keys = () =>
        {
            PathNames.KeyFor(Side.Local, local.Path);
            PathNames.KeyFor(Side.Local, respelled.Path);
        };
        Check.Require(Allocated(exact) == 0 && Allocated(respelling) == Allocated(keys),
            "43a pending membership allocates nothing beyond the respelling's two key reads");
    }

    // DESIGN-B70-TRANSIENT-10 section 11: two unknown moves of the same items to different destinations are different
    // responsibilities; ordinary subjects keep their equality and stored shape.
    private static void UnknownMoveIdentity()
    {
        var move = new IssueSubject.UnknownMove("id-a", "a.txt", "b.txt", null, false);
        var subject = new IssueSubject([LocalA], null) { Move = move };
        Same(subject, subject with { Move = move with { } }, true, "the same intended move");
        Same(subject, subject with { Move = move with { Destination = "c.txt" } }, false, "another destination");
        Same(subject, subject with { Move = move with { Intermediate = "b.txt.case" } }, false, "another intermediate");
        Same(subject, subject with { Move = null }, false, "an ordinary subject of the same items");
        Check.Require(!JsonSerializer.Serialize(new IssueSubject([LocalA], null)).Contains("Move", StringComparison.Ordinal),
            "JSON omits an absent move, so ordinary subjects keep their stored shape");
        var read = JsonSerializer.Deserialize<IssueSubject>(JsonSerializer.Serialize(subject))!;
        Check.Require(read.Move == move && read.HasSameItems(subject), "an unknown move round-trips through JSON");
        var original = new Issue(1, IssueKind.Failure, TextKey.DropboxRefused, "a.txt", subject, Modified, false);
        Identity(original, original with { Subject = original.Subject with { Move = move with { Source = "z.txt" } } },
            false);
        Identity(original, JsonSerializer.Deserialize<Issue>(JsonSerializer.Serialize(original))!, true);

        static void Same(IssueSubject left, IssueSubject right, bool expected, string what)
        {
            Check.Require(left.HasSameItems(right) == expected && right.HasSameItems(left) == expected,
                "unknown move equivalence: " + what);
            Check.Require(new HashSet<IssueSubject>(IssueSubject.ItemSetComparer) { left }.Contains(right) == expected,
                "indexed unknown move equivalence: " + what);
        }
    }

    public static void ReviewedIdentity()
    {
        var original = new Issue(1, IssueKind.Failure, TextKey.FailChanged, "a.txt",
            new IssueSubject([LocalA, DropboxC], Side.Local, new LocalHashProof(10, Modified.UtcTicks, "hash")),
            Modified, false);
        foreach (var equivalent in new[]
        {
            original with { Reviewed = "read-metadata", Ignored = true },
            original with { At = Modified.ToOffset(TimeSpan.FromHours(2)) },
            original with { Subject = original.Subject with { Items = [DropboxC, LocalA, DropboxC] } },
            JsonSerializer.Deserialize<Issue>(JsonSerializer.Serialize(original))!
        }) Identity(original, equivalent, true);
        foreach (var changed in new[]
        {
            original with { Id = 2 }, original with { Kind = IssueKind.Info },
            original with { Cause = TextKey.FailLocalInUse }, original with { Path = "A.txt" },
            original with { At = Modified.AddTicks(1) }, original with { Dismissed = true },
            original with { Subject = original.Subject with { Incoming = Side.Dropbox } },
            original with { Subject = original.Subject with { Items = [LocalA] } },
            original with { Subject = original.Subject with { Items = [LocalA, DropboxC, LocalB] } },
            original with { Subject = original.Subject with { Items = [LocalA with { Side = Side.Dropbox }, DropboxC] } },
            original with { Subject = original.Subject with { Items = [LocalA with { Path = "A.txt" }, DropboxC] } },
            original with { Subject = original.Subject with { Items = [LocalA with { Id = "replacement" }, DropboxC] } },
            original with { Subject = original.Subject with { LocalHash = null } },
            original with { Subject = original.Subject with { LocalHash = original.Subject.LocalHash! with { Size = 11 } } },
            original with { Subject = original.Subject with { LocalHash = original.Subject.LocalHash! with { ModifiedTicks = 1 } } },
            original with { Subject = original.Subject with { LocalHash = original.Subject.LocalHash! with { Hash = "other" } } },
            original with { Subject = original.Subject with { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.Item } }
        }) Identity(original, changed, false);
    }

    public static void ReviewedNullMembers()
    {
        var withNull = new Issue(1, IssueKind.Failure, TextKey.FailChanged, "a.txt",
            new IssueSubject([LocalA, null!], null), Modified, false);
        string? token = null;
        Exception? failure = null;
        try { token = Massdex.SyncShark.Engine.Run.PairSafety.ForReview(withNull).Reviewed; }
        catch (Exception error) { failure = error; }
        Check.Require(failure is null && token is { Length: 64 },
            "a supported null subject member must produce a reviewed token rather than throw");
        Identity(withNull, withNull with { Subject = withNull.Subject with { Items = [null!, LocalA, null!] } }, true);
        Identity(withNull, withNull with { Subject = withNull.Subject with { Items = [LocalA] } }, false);
    }

    private static void Identity(Issue left, Issue right, bool same)
    {
        Check.Require(left.Matches(right) == same && right.Matches(left) == same,
            "exact issue identity includes every resolution field and ignores projection metadata");
        Check.Require((Massdex.SyncShark.Engine.Run.PairSafety.ForReview(left).Reviewed ==
            Massdex.SyncShark.Engine.Run.PairSafety.ForReview(right).Reviewed) == same,
            "review fingerprints have the same field and canonicalization semantics as exact identity");
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
        string what)
    {
        var a = new IssueSubject(left, null);
        var b = new IssueSubject(right, Side.Dropbox);
        Check.Require(a.HasSameItems(b) == expected, "HasSameItems: " + what);
        Check.Require(new HashSet<IssueSubject>(IssueSubject.ItemSetComparer) { a }.Contains(b) == expected,
            "indexed item equality: " + what);
    }

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
