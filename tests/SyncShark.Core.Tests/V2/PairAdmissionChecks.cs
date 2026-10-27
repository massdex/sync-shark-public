namespace Massdex.SyncShark.V2;

/// <summary>Table checks for the pair admission rule: which pair layouts may coexist with a saved pair.</summary>
/// <remarks>Every candidate has its own root ids, so the path rows decide on paths alone.</remarks>
internal static class PairAdmissionChecks
{
    private const string Account = "acc";

    // The saved pair every row is checked against: local C:\work\a with Dropbox /work/a in account "acc".
    private static readonly PairDef Saved = Pair("saved", @"C:\work\a", Account, "/work/a");

    private static readonly (string Label, PairDef Candidate, NoticeReason? Expected)[] Rows =
    [
        ("equal local root is refused", Pair("c", @"C:\work\a", Account, "/elsewhere"), PairAdmissionReasons.SameRoot),
        ("equal Dropbox root is refused", Pair("c", @"C:\elsewhere", Account, "/work/a"), PairAdmissionReasons.SameRoot),
        ("equal roots differing only in case are refused", Pair("c", @"c:\WORK\A", Account, "/x"),
            PairAdmissionReasons.SameRoot),
        ("equal Dropbox roots differing only in case are refused", Pair("c", @"C:\x", Account, "/Work/A"),
            PairAdmissionReasons.SameRoot),
        ("a trailing separator does not hide an equal root", Pair("c", @"C:\work\a\", Account, "/x/"),
            PairAdmissionReasons.SameRoot),
        ("a forward-slash local path is the same root", Pair("c", "C:/work/a", Account, "/x"),
            PairAdmissionReasons.SameRoot),
        ("a '..' alias of a local root is the same root", Pair("c", @"C:\work\other\..\a", Account, "/x"),
            PairAdmissionReasons.SameRoot),
        ("a '.' alias of a local root is the same root", Pair("c", @"C:\work\.\a", Account, "/x"),
            PairAdmissionReasons.SameRoot),
        ("repeated Dropbox separators are the same root", Pair("c", @"C:\x", Account, "//work//a"),
            PairAdmissionReasons.SameRoot),
        ("a Dropbox dot segment cannot be compared and is refused", Pair("c", @"C:\x", Account, "/work/x/../a"),
            PairAdmissionReasons.Unresolved),
        ("a relative local root cannot be compared and is refused", Pair("c", @"work\a", Account, "/x"),
            PairAdmissionReasons.Unresolved),
        ("both roots nested inside the saved pair is admitted", Pair("c", @"C:\work\a\b", Account, "/work/a/b"), null),
        ("both roots enclosing the saved pair is admitted", Pair("c", @"C:\work", Account, "/work"), null),
        ("nested on one side only is admitted", Pair("c", @"C:\work\a\b", Account, "/other"), null),
        ("nested in case variants is admitted the same way", Pair("c", @"C:\WORK\A\b", Account, "/WORK/a/b"), null),
        ("the Dropbox root encloses every Dropbox folder", Pair("c", @"D:\x", Account, "/"), null),
        ("a local drive root encloses every folder on it", Pair("c", @"C:\", Account, "/other"), null),
        ("nesting in opposite directions is refused", Pair("c", @"C:\work\a\b", Account, "/work"),
            PairAdmissionReasons.CrossedNesting),
        ("opposite directions via the Dropbox root are refused", Pair("c", @"C:\work\a\b", Account, "/"),
            PairAdmissionReasons.CrossedNesting),
        ("a sibling with a shared name prefix is distinct", Pair("c", @"C:\work\ab", Account, "/work/ab"), null),
        ("the same Dropbox path in another account is distinct", Pair("c", @"C:\x", "other", "/work/a"), null),
        ("opposite nesting across accounts is not compared", Pair("c", @"C:\work\a\b", "other", "/work"), null),
    ];

    private static readonly (string Provider, string Path, string? Expected)[] CanonicalRows =
    [
        (SyncProviders.Local, @"C:\work\x\..\a\", @"C:\work\a"),
        (SyncProviders.Local, "C:/work/./a", @"C:\work\a"),
        (SyncProviders.Local, @"C:\", @"C:\"),
        (SyncProviders.Local, @"work\a", null),
        (SyncProviders.Local, "", null),
        (SyncProviders.Dropbox, "work//a/", "/work/a"),
        (SyncProviders.Dropbox, @"\work\a", "/work/a"),
        (SyncProviders.Dropbox, "/", "/"),
        (SyncProviders.Dropbox, "/a/../b", null),
        (SyncProviders.Dropbox, "/a/./b", null),
    ];

    public static void Run()
    {
        foreach (var (label, candidate, expected) in Rows)
            Require(PairAdmission.Decide(candidate, [Saved]) == expected, label);
        Require(PairAdmission.Decide(Saved, []) is null, "no other pair admits any layout");
        foreach (var (provider, path, expected) in CanonicalRows)
            Require(CanonicalOrNull(provider, path) == expected,
                $"the canonical {provider} form of '{path}' is {expected ?? "a refusal"}");
        IdentityRows();
        Require(PairAdmission.NestedPath(Saved.A, Pair("c", @"C:\WORK\a\b\c", Account, "/x").A) == "b/c",
            "the nested path is the '/' remainder below the enclosing root");
        Require(PairAdmission.NestedPath(Saved.A, Pair("c", "C:/work/a/b", Account, "/x").A) == "b",
            "a forward-slash local root nests the same as its backslash form");
        Require(PairAdmission.NestedPath(Saved.A, Saved.A) is null, "an equal root is not nested");
        Console.WriteLine("v2 pair admission checks passed.");
    }

    // Observed identities decide equality independently of path and account; a missing identity leaves the paths.
    private static void IdentityRows()
    {
        var alias = WithIds(Pair("c", @"D:\alias", Account, "/x"), localId: Saved.A.RootObjectId);
        Require(PairAdmission.Decide(alias, [Saved]) == PairAdmissionReasons.SameRoot,
            "a local root with another pair's volume and file id is the same root behind another path");
        var shared = WithIds(Pair("c", @"D:\x", "other", "/mounted/elsewhere"), cloudId: Saved.B.RootObjectId);
        Require(PairAdmission.Decide(shared, [Saved]) == PairAdmissionReasons.SameRoot,
            "a Dropbox folder id seen through another account is the same root");
        var unobserved = WithIds(Pair("c", @"D:\x", "other", "/y"), localId: string.Empty, cloudId: string.Empty);
        Require(PairAdmission.Decide(unobserved, [Saved]) is null,
            "a root without an observed identity next to a disjoint pair is admitted on its paths");
        Require(PairAdmission.Decide(Saved, [unobserved]) is null,
            "a saved pair is not blocked by a disjoint neighbour without identities");
        Require(PairAdmission.Decide(WithIds(Pair("c", @"C:\work\a", Account, "/y"), localId: ""), [Saved]) ==
            PairAdmissionReasons.SameRoot, "a root without an identity is still refused on an equal path");
        Require(PairAdmission.Decide(WithIds(alias, localId: ""), [Saved]) is null,
            "before observation an alias is not visible; the check after observation sees it");
    }

    private static string? CanonicalOrNull(string provider, string path)
    {
        try
        {
            return PairAdmission.Canonical(provider, path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static PairDef Pair(string id, string localRoot, string account, string remotePath) =>
        new(id, id, new RootDef(id + "-a", SyncProviders.Local, null, localRoot, "id:" + id + "-a"),
            new RootDef(id + "-b", SyncProviders.Dropbox, account, remotePath, "id:" + id + "-b"), [], [], true);

    private static PairDef WithIds(PairDef pair, string? localId = null, string? cloudId = null) => pair with
    {
        A = pair.A with { RootObjectId = localId ?? pair.A.RootObjectId },
        B = pair.B with { RootObjectId = cloudId ?? pair.B.RootObjectId }
    };

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
