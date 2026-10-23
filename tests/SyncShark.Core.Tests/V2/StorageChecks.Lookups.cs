using System.Text.RegularExpressions;

namespace Massdex.SyncShark.V2;

/// <summary>Round-3 checks: case-insensitive path lookups across roots that spell a folder differently.</summary>
internal static partial class StorageChecks
{
    // The folder table is shared by all roots: "Docs" (root ra, interned first) and "docs" (root rb) are two rows,
    // and a lookup must reach the right one for each root .
    private static void PathLookups_TwoRootsDifferentCase(IStateStore store)
    {
        var genA = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, genA, "c", [F("Docs/a.txt", "id:a", "11")], []));
        var genB = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, genB, "c", [F("docs/b.txt", "id:b", "22")], []));
        var b = store.Live("rb", ["DOCS/B.TXT"]);
        Require(b.Count == 1 && b[0].Path == "docs/b.txt", "Live finds root rb's 'docs/b.txt' although ra interned 'Docs'");
        Require(store.Live("ra", ["DOCS/A.TXT"]).Count == 1, "Live finds root ra's 'Docs/a.txt'");
        Require(store.LiveUnder("rb", ["DOCS/B.TXT"]).Select(i => i.Path).SequenceEqual(["docs/b.txt"]),
            "LiveUnder finds the prefix item itself in root rb's spelling of the folder");
        Require(store.LiveUnder("rb", ["DOCS"]).Select(i => i.Path).SequenceEqual(["docs/b.txt"]),
            "LiveUnder of the folder returns only root rb's rows");
        store.SavePair(Pair("p1"));
        SeedBaseline(store, "p1", "k1", F("docs/b.txt", "id:x", "22"), F("docs/b.txt", "id:b", "22"));
        Require(store.BaselinesTouching("p1", Side.A, [], ["DOCS/B.TXT"]).Count == 1,
            "BaselinesTouching finds the prefix item in the second spelling of the folder");
    }

    // The hot lookups (Live by paths, LiveUnder, BaselinesTouching) must be indexed for case-insensitive matching.
    private static void HotLookups_UseIndexes(string root)
    {
        using var store = OpenSqlite(root, "query-plans", new TestClock());
        var plans = store.HotQueryPlans();
        foreach (var line in plans) Console.WriteLine("plan " + line);
        Require(plans.Count > 0 && !plans.Any(line => Regex.IsMatch(line, @": SCAN (f|sp|file_state|sync_point)\b")),
            "no hot lookup scans file_state or sync_point");
        Require(!plans.Any(line => Regex.IsMatch(line, @"SEARCH (f|sp) USING PRIMARY KEY \((root_no|pair_no)=\?\)$")),
            "no hot lookup walks a whole root or pair through the leading key column");
        const string leafKey = @"SEARCH f USING PRIMARY KEY \(root_no=\? AND dir_no=\? AND name_key=\?\)";
        Require(plans.Any(line => line.StartsWith("Live:", StringComparison.Ordinal) && Regex.IsMatch(line, leafKey)),
            "Live's leaf lookup uses the stored leaf key");
        Require(plans.Any(line => line.StartsWith("Live:", StringComparison.Ordinal) && line.Contains("dir_key (path_key=?)")),
            "Live's folder lookup uses the stored folder key");
        Require(plans.Any(line => line.StartsWith("LiveBatch:", StringComparison.Ordinal) && Regex.IsMatch(line, leafKey)),
            "LiveBatch's leaf lookup uses the stored leaf key");
        Require(plans.Any(line => line.StartsWith("LiveBatch:", StringComparison.Ordinal) && line.Contains("dir_key (path_key=?)")),
            "LiveBatch's folder lookup uses the stored folder key");
        Require(plans.Any(line => line.StartsWith("LiveUnder:", StringComparison.Ordinal) && Regex.IsMatch(line, leafKey)),
            "LiveUnder's prefix item uses the stored leaf key");
        Require(plans.Count(line => line.StartsWith("BaselinesTouching", StringComparison.Ordinal) &&
                Regex.IsMatch(line, @"sp_[ab]_dir \(pair_no=\? AND [ab]_dir=\? AND [ab]_name_key=\?\)")) == 3,
            "BaselinesTouching's prefix items use the side indexes down to the leaf key (A once, B twice)");
    }

    // .NET ordinal-ignore-case, not SQLite NOCASE (ASCII-only), so non-ASCII names match.
    private static void PathLookups_NonAscii(IStateStore store)
    {
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", [F("Äpfel/Über.txt", "id:u", "11")], []));
        Require(store.Live("ra", ["äpfel/über.txt"]).Select(i => i.Path).SequenceEqual(["Äpfel/Über.txt"]),
            "Live matches 'äpfel/über.txt' to the stored 'Äpfel/Über.txt'");
        Require(store.LiveUnder("ra", ["äpfel"]).Count == 1 && store.LiveUnder("ra", ["ÄPFEL/ÜBER.TXT"]).Count == 1,
            "LiveUnder matches the non-ASCII folder and the prefix item ignoring case");
        store.SavePair(Pair("p1"));
        SeedBaseline(store, "p1", "k1", F("Äpfel/Über.txt", "id:u", "11"), F("Äpfel/Über.txt", "id:v", "11"));
        Require(store.BaselinesTouching("p1", Side.B, [], ["äpfel"]).Count == 1 &&
            store.BaselinesTouching("p1", Side.A, [], ["äpfel/über.txt"]).Count == 1,
            "BaselinesTouching matches non-ASCII folders and items ignoring case");
    }

    // A different object arriving at a path that differs only in case replaces the live one: never both live.
    private static void Upsert_CaseInsensitiveDestination(IStateStore store)
    {
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", [F("Docs/file.txt", "id:x", "11")], []));
        var delta = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, delta, "c", "d",
            [new Change(ChangeKind.Upsert, "id:y", "docs/File.txt", F("docs/File.txt", "id:y", "22"), null)], false, []));
        RequireLive(store, "ra", F("docs/File.txt", "id:y", "22"));
        var rename = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, rename, "d", "e",
            [new Change(ChangeKind.Upsert, "id:y", "DOCS/FILE.TXT", F("DOCS/FILE.TXT", "id:y", "22"), null)], false, []));
        RequireLive(store, "ra", F("DOCS/FILE.TXT", "id:y", "22"));
        var unicode = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, unicode, "e", "f", [
            new Change(ChangeKind.Upsert, "id:u", "Äpfel/Über.txt", F("Äpfel/Über.txt", "id:u", "33"), null),
            new Change(ChangeKind.Upsert, "id:w", "äpfel/über.txt", F("äpfel/über.txt", "id:w", "44"), null),
        ], false, []));
        RequireLive(store, "ra", F("DOCS/FILE.TXT", "id:y", "22"), F("äpfel/über.txt", "id:w", "44"));
        // B-126: a path-only Delete acts on its exact spelling; another spelling needs evidence from the inventory.
        var other = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, other, "f", "g",
            [new Change(ChangeKind.Delete, "", "ÄPFEL", null, T0)], false, []));
        RequireLive(store, "ra", F("DOCS/FILE.TXT", "id:y", "22"), F("äpfel/über.txt", "id:w", "44"));
        var delete = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, delete, "g", "h",
            [new Change(ChangeKind.Delete, "", "äpfel", null, T0)], false, []));
        RequireLive(store, "ra", F("DOCS/FILE.TXT", "id:y", "22"));
    }

    /// <summary>The all-Unicode path-key proof (~2 s), run on its own: <c>--v2-storage-unicode-checks</c>.</summary>
    public static void RunUnicode()
    {
        PathKey_MatchesOrdinalIgnoreCase();
        Console.WriteLine("v2 storage unicode checks passed.");
    }

    // , exactly: the stored key must agree with OrdinalIgnoreCase on every Unicode scalar (Astra
    // found "ſ" vs "S", which upper-invariant merges and OrdinalIgnoreCase keeps apart).
    private static void PathKey_MatchesOrdinalIgnoreCase()
    {
        var keyOfClass = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var classOfKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var checkedScalars = 0;
        var mismatches = new List<string>();
        for (var scalar = 0; scalar <= 0x10FFFF; scalar++)
        {
            if (scalar is >= 0xD800 and <= 0xDFFF) continue;
            var text = char.ConvertFromUtf32(scalar);
            var key = PathKey.Of(text);
            if (keyOfClass.TryGetValue(text, out var classKey) && classKey != key)
                mismatches.Add($"U+{scalar:X4} OrdinalIgnoreCase-equal to a scalar keyed U+{char.ConvertToUtf32(classKey, 0):X4}");
            keyOfClass.TryAdd(text, key);
            if (classOfKey.TryGetValue(key, out var member) && !string.Equals(member, text, StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"U+{scalar:X4} shares its key with distinct U+{char.ConvertToUtf32(member, 0):X4}");
            classOfKey.TryAdd(key, text);
            checkedScalars++;
        }
        Require(mismatches.Count == 0, "no key/OrdinalIgnoreCase mismatch: " + string.Join("; ", mismatches.Take(40)));
        Require(PathKey.Of("ſ.txt") != PathKey.Of("S.txt") && PathKey.Of("Docs/Äpfel") == PathKey.Of("docs/äpfel") &&
            PathKey.Of("a\uD800b") != PathKey.Of("a\uDC00b"),
            $"path keys equal exactly when OrdinalIgnoreCase does ({checkedScalars} scalars; ſ/S distinct; lone surrogates kept)");
    }

    // ſ (U+017F) and S are distinct under OrdinalIgnoreCase: leaf names, parent folders, deletes and notices keep them apart.
    private static void PathLookups_LongS(IStateStore store)
    {
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", [
            F("ſ.txt", "id:1", "11"), F("S.txt", "id:2", "22"), F("ſdir/a.txt", "id:3", "33"), F("Sdir/a.txt", "id:4", "44"),
        ], []));
        Require(store.Live("ra", ["s.TXT"]).Select(i => i.Path).SequenceEqual(["S.txt"]) &&
            store.Live("ra", ["ſ.txt"]).Select(i => i.Path).SequenceEqual(["ſ.txt"]), "ſ.txt and S.txt are distinct leaves");
        Require(store.LiveUnder("ra", ["sdir"]).Select(i => i.Path).SequenceEqual(["Sdir/a.txt"]) &&
            store.LiveUnder("ra", ["ſdir"]).Select(i => i.Path).SequenceEqual(["ſdir/a.txt"]), "ſdir and Sdir are distinct folders");
        var delta = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, delta, "c", "d", [
            new Change(ChangeKind.Upsert, "id:5", "s.txt", F("s.txt", "id:5", "55"), null),
            new Change(ChangeKind.Delete, "", "Sdir", null, T0),
        ], false, []));
        RequireLive(store, "ra", F("ſ.txt", "id:1", "11"), F("s.txt", "id:5", "55"), F("ſdir/a.txt", "id:3", "33"));
        store.SavePair(Pair("p1"));
        store.AddNotices([new Notice("p1", 4, NoticeKind.AutoConflict, "ſ.txt", "d", "conflict"),
            new Notice("p1", 4, NoticeKind.AutoConflict, "S.txt", "d", "conflict")]);
        Require(store.OpenNotices("p1").Count == 2, "notices for ſ.txt and S.txt are two problems");
    }

    // RQ-039: a case clash a full scan recorded (Docs/a.txt X and Docs/A.txt Y) is kept: updating or re-hashing one
    // at its exact path never removes the other or invents its deletion; only evidence for that spelling removes it.
    private static void CaseClash_KeptUntilEvidence(IStateStore store)
    {
        Track(store, "ra", "id:x", "id:y");
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c",
            [F("Docs/a.txt", "id:x", "11"), F("Docs/A.txt", "id:y", "22")], []));
        var update = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, update, "c", "d", [
            new Change(ChangeKind.Upsert, "id:x", "Docs/a.txt", F("Docs/a.txt", "id:x", "33"), null),
            new Change(ChangeKind.Upsert, "id:y", "Docs/A.txt", F("Docs/A.txt", "id:y", "22"), null),
        ], false, []));
        RequireLive(store, "ra", F("Docs/a.txt", "id:x", "33"), F("Docs/A.txt", "id:y", "22"));
        if (store is SqliteStateStore sqlite)
            Require(sqlite.Tombstones("ra").Count == 0, "updating one side of a case clash records no deletion");
        var delete = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, delete, "d", "e",
            [new Change(ChangeKind.Delete, "id:x", "Docs/a.txt", null, T0)], false, []));
        RequireLive(store, "ra", F("Docs/A.txt", "id:y", "22"));
        if (store is SqliteStateStore after)
            RequireTombstones(after, "ra", "deleting one spelling leaves evidence for it only", ("Docs/a.txt", "id:x"));
    }

    private static ItemState[] ClashSet =>
    [
        F("Docs/a.txt", "id:x", "11"), F("Docs/A.txt", "id:y", "22"),
        D("Sub", "id:f"), F("Sub/x.txt", "id:sx", "33"), D("sub", "id:g"), F("sub/y.txt", "id:sy", "44"),
    ];

    // A Delete naming its object never reaches another spelling: replaying it (the object is gone) changes nothing.
    private static void CaseClash_DeleteReplayKeepsOthers(IStateStore store)
    {
        Track(store, "ra", "id:x", "id:y", "id:f", "id:sx", "id:g", "id:sy");
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", ClashSet, []));
        Change[] deletes =
        [
            new(ChangeKind.Delete, "id:x", "Docs/a.txt", null, T0), new(ChangeKind.Delete, "id:f", "Sub", null, T0),
        ];
        foreach (var cursor in new[] { "d", "e" })
        {
            var delta = store.BeginGeneration("ra", "delta");
            store.WriteDelta(new Delta("ra", Side.A, delta, "c", cursor, deletes, false, []));
        }
        RequireLive(store, "ra", ClashSet[1], ClashSet[4], ClashSet[5]);
        if (store is SqliteStateStore sqlite)
            RequireTombstones(sqlite, "ra", "a replayed delete leaves evidence only for the deleted objects",
                ("Docs/a.txt", "id:x"), ("Sub", "id:f"), ("Sub/x.txt", "id:sx"));
    }

    // A move written as Delete(X) + Upsert(X elsewhere) (file and folder) never deletes the other spelling.
    private static void CaseClash_DeleteMoveKeepsOthers(IStateStore store)
    {
        Track(store, "ra", "id:x", "id:y", "id:f", "id:sx", "id:g", "id:sy");
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", ClashSet, []));
        var delta = store.BeginGeneration("ra", "delta");
        store.WriteDelta(new Delta("ra", Side.A, delta, "c", "d", [
            new Change(ChangeKind.Delete, "id:x", "Docs/a.txt", null, T0),
            new Change(ChangeKind.Upsert, "id:x", "Elsewhere/a.txt", F("Elsewhere/a.txt", "id:x", "11"), null),
            new Change(ChangeKind.Delete, "id:f", "Sub", null, T0),
            new Change(ChangeKind.Upsert, "id:f", "Moved", D("Moved", "id:f"), null),
            new Change(ChangeKind.Upsert, "id:sx", "Moved/x.txt", F("Moved/x.txt", "id:sx", "33"), null),
        ], false, []));
        RequireLive(store, "ra", ClashSet[1], ClashSet[4], ClashSet[5], F("Elsewhere/a.txt", "id:x", "11"),
            D("Moved", "id:f"), F("Moved/x.txt", "id:sx", "33"));
        if (store is SqliteStateStore sqlite)
            Require(sqlite.Tombstones("ra").Count == 0, "a move of one spelling records no deletion and keeps the other");
    }}
