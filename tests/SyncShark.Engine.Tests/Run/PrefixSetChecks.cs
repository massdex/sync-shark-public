using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Checks for PrefixSet (`--engine-checks repair`), written by PrefixSet's writer.</summary>
public static class PrefixSetChecks
{
    public static void Run()
    {
        Check.Require(!new PrefixSet().Covers("") && !new PrefixSet().Covers("a/b"),
            "an empty PrefixSet covers nothing");
        Check.Require(!new PrefixSet().Overlaps("") && !new PrefixSet().Overlaps("a/b"),
            "an empty PrefixSet overlaps nothing");

        EmptySetAllocatesNothing();

        (string Added, string Key, bool Covered)[] cases =
        [
            ("Docs", "Docs", true),
            ("Docs", "docs/Report.txt", true),
            ("Docs", "docs/a/b/c.txt", true),
            ("Docs", "Documents", false),
            ("Docs", "Docs2", false),
            ("Docs", "Doc", false),
            ("Docs", "Other/Docs", false),
            ("Docs", "", false),
            ("app/node_modules", "app/node_modules/pkg/index.js", true),
            ("app/node_modules", "app", false),
            ("app/node_modules", "app/node_modules2", false),
            ("a/b/c", "a/b/c", true),
            ("a/b/c", "a/b/c/d/e", true),
            ("a/b/c", "a/b", false),
            ("a/b/c", "a/b/cd", false),
            ("Docs", "/Docs/Child", false),
            ("/Docs", "/Docs/Child", false),
            ("/Docs", "Docs/Child", false),
            ("App/Folder", "app/folder/child", true),
        ];
        foreach (var (added, key, covered) in cases)
        {
            var set = new PrefixSet();
            set.Add(added);
            Check.Require(set.Covers(key) == covered, "PrefixSet Add(" + added + ") Covers(" + key + ")");
        }

        var union = new PrefixSet();
        union.Add("one");
        union.Add("two/three");
        Check.Require(union.Covers("one") && union.Covers("two/three/four") &&
            !union.Covers("two") && !union.Covers("three"), "a PrefixSet covers every added key's own subtree");

        // Overlaps is the two-way test: self, ancestor or descendant at the same segment boundaries (2.4).
        (string Added, string Key, bool Overlaps)[] overlapCases =
        [
            ("Docs", "Docs", true),
            ("Docs", "docs/Report.txt", true),
            ("app/node_modules", "app", true),
            ("App/Folder", "app", true),
            ("a/b/c", "a/b", true),
            ("Docs", "Documents", false),
            ("Docs", "Docs2", false),
            ("Docs", "Other/Docs", false),
            ("app/node_modules", "app/node_modules2", false),
            ("a/b/c", "a/b/cd", false),
        ];
        foreach (var (added, key, overlaps) in overlapCases)
        {
            var set = new PrefixSet();
            set.Add(added);
            Check.Require(set.Overlaps(key) == overlaps, "PrefixSet Add(" + added + ") Overlaps(" + key + ")");
        }

        Check.Require(union.Overlaps("two") && union.Overlaps("one/child") && !union.Overlaps("three"),
            "a PrefixSet overlaps every added key's ancestors and own subtree");

        // Adding after a lookup must invalidate the lazily built strict-ancestor set.
        var late = new PrefixSet();
        late.Add("other");
        Check.Require(!late.Overlaps("top/mid/leaf"), "a key unrelated to every added key does not overlap");
        late.Add("top/mid/leaf");
        Check.Require(late.Overlaps("top/mid"), "an Add after a lookup is visible to the ancestor lookup");
        late.Add("top");
        Check.Require(late.Overlaps("top/readme") && late.Overlaps("top/mid/leaf/child"),
            "an Add after a lookup is visible to the descendant lookup");

        var root = new PrefixSet();
        root.Add("");
        Check.Require(root.Overlaps(""), "an added empty key overlaps itself");
        Check.Require(!root.Overlaps("Child") && !root.Overlaps("/Child"),
            "an empty stored key overlaps no other key, as Covers does");

        var keys = new PrefixSet();
        keys.Add("one");
        keys.Add("Two/Three");
        keys.Add("ONE");
        Check.Require(keys.Keys.Count() == 2 && keys.Keys.Contains("one") && keys.Keys.Contains("Two/Three"),
            "Keys enumerates each added key once");
        Check.Require(keys.Keys is not ISet<string>, "Keys exposes no mutable set");

        // Unreadable prefixes are Local own-key space: mapped join keys are not substituted (2.4).
        var mapped = new PrefixSet();
        mapped.Add("Docs/Name_");
        Check.Require(mapped.Overlaps("Docs/Name_/Child"), "a mapped local spelling overlaps its subtree");
        Check.Require(!mapped.Overlaps("Docs/Name."),
            "keys are compared as spelled, so the provider spelling of a mapped name does not overlap");
    }

    // Healthy runs build the set empty, so its lookups and Keys gate must cost nothing per row (2.4).
    private static void EmptySetAllocatesNothing()
    {
        var empty = new PrefixSet();
        Consume(empty.Covers("a/b"));
        Consume(empty.Overlaps("a/b"));
        Consume(empty.Keys.Any());
        var covers = AllocProbe.Measure(_ => Consume(empty.Covers("a/b")), 1_000);
        var overlaps = AllocProbe.Measure(_ => Consume(empty.Overlaps("a/b")), 1_000);
        var keys = AllocProbe.Measure(_ => Consume(empty.Keys.Any()), 1_000);
        Check.Require(covers == 0 && overlaps == 0 && keys == 0,
            "an empty PrefixSet allocates nothing (Covers=" + covers + " B, Overlaps=" + overlaps + " B, Keys.Any=" +
            keys + " B per 1,000 calls)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(bool value) => _ = value;
}
