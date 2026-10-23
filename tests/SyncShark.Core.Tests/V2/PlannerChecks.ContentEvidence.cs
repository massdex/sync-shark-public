namespace Massdex.SyncShark.V2;

internal static partial class PlannerChecks
{
    private static void ContentEvidence_OneOwner()
    {
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Parent/Child" };
        Require(PairPath.CoveredBy("parent/child/file", prefixes) &&
            PairPath.CoveredBy("PARENT/CHILD", prefixes) &&
            !PairPath.CoveredBy("Parent/Children", prefixes), "coverage is case-insensitive and segment bounded");
        Require(!PairPath.Observed("Parent/Child/file", prefixes) &&
            PairPath.Observed("Elsewhere", prefixes) &&
            !PairPath.Observed("Elsewhere", prefixes, _ => false), "observed coverage respects incomplete and excluded paths");
        Require(PairPath.CoveredBy("Elsewhere", new[] { "" }) &&
            !PairPath.CoveredBy("Elsewhere", Array.Empty<string>()), "root prefix covers all and empty set covers none");
        foreach (IReadOnlyCollection<string> rootPrefixes in new IReadOnlyCollection<string>[]
            { new[] { "" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" } })
        {
            Require(PairPath.CoveredBy("Child", rootPrefixes, rootPrefixCovers: true) &&
                !PairPath.CoveredBy("Child", rootPrefixes, rootPrefixCovers: false) &&
                !PairPath.CoveredBy("", rootPrefixes, rootPrefixCovers: false),
                "pending-root coverage and inventory link/stub root exceptions are explicit and preserved");
        }
        var pendingRoot = LinkExclusions.None.WithPending(new[] { "" });
        Require(!pendingRoot.NarrowInventory(_ => true)("Child"), "pending whole-root hold narrows inventory");
        var known = new ItemState("item", ItemKind.File, "a", null, "ab12", 4, T0, ItemFlags.None);
        Require(Content.SameContent(known, known with { Hash = "AB12" }) == true,
            "hex hash spelling does not change content equality");
        Require(Content.SameContent(known, known with { Size = 5 }) == false,
            "different sizes are different content even with equal hashes");
        Require(Content.SameVersion(known, known with { Size = 5 }) == false,
            "same-side content evidence cannot override a size mismatch");
        Require(Content.SameContent(known, known with { Hash = null }) is null &&
            Content.SameContent(known with { Hash = null }, known with { Hash = null }) is null,
            "missing hashes never establish content equality");
        Require(Content.SameContent(known, known with { Hash = null, Size = 5 }) == false,
            "size mismatch is definite without hashes");
        Require(Content.SameHash(null, null) is null && Content.SameHash("ab12", "AB12") == true,
            "hash-only revision evidence uses the same known-hash comparison");
        var folder = known with { Kind = ItemKind.Folder, Hash = null };
        Require(Content.SameContent(folder, folder) == true && Content.SameContent(known, folder) == false,
            "folder equality needs no file hash while kind mismatches are unequal");
    }
}
