using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Checks for PathNames (`--engine-checks paths`), written by PathNames's writer.</summary>
public static class PathNamesChecks
{
    public static void Run()
    {
        Identity();
        CaseOnlyAndComparer();
        FrozenIdentity();
        FrozenStrings();
        Boundaries();
        NamesAndParents();
        KeyAncestry();
        Depth();
        Keys();
        Mapping();
        NameShape();
        DropboxRoots();
        LocalRoots();
        Evidence();
    }

    private static void Identity()
    {
        Check.Require(PathNames.Same("Parent/Child", "parent/child"), "Same ignores case");
        Check.Require(PathNames.Same("Parent/Child", "Parent/Child"), "Same accepts the exact spelling");
        Check.Require(!PathNames.Same("Parent/Child", "Parent/Child2"), "Same rejects a different path");
        Check.Require(!PathNames.Same("Parentish/Child", "Parent/Child"), "Same is not a prefix rule");
        Check.Require(!PathNames.Same("Parent", "Parent/Child"), "Same is not a descendant rule");
        Check.Require(PathNames.Same(null, null), "Same(null, null)");
        Check.Require(!PathNames.Same(null, "") && !PathNames.Same("", null), "Same with one null");
        Check.Require(PathNames.Same("", ""), "Same(\"\", \"\")");
        Check.Require(PathNames.Same("Σ", "σ"), "Same folds sigma case");
        Check.Require(!PathNames.Same("ſ", "S"), "Same is ordinal, not culture-aware");
    }

    private static void CaseOnlyAndComparer()
    {
        Check.Require(PathNames.CaseOnly("Parent/Child", "parent/child"), "CaseOnly accepts a case-only change");
        Check.Require(!PathNames.CaseOnly("a", "a"), "CaseOnly needs a changed spelling");
        Check.Require(!PathNames.CaseOnly("a", "b"), "CaseOnly needs the same identity");
        Check.Require(PathNames.CaseOnly("a", "A"), "CaseOnly accepts one changed character");
        Check.Require(!PathNames.CaseOnly("Parent/Child", "Parent/Child/"), "CaseOnly rejects a changed path");
        Check.Require(!PathNames.CaseOnly("Parent", "Parent/Child"), "CaseOnly rejects a different length");
        Check.Require(PathNames.Same("Parent", "parent") && PathNames.CaseOnly("Parent", "parent") &&
            !string.Equals("Parent", "parent", StringComparison.Ordinal),
            "identity, case-only and exact spelling are distinct");

        (string Left, string Right)[] shapes =
        [
            ("Parent/Child/Alpha.txt", "Parent/Child/Betaa.txt"),
            ("Parent/Child/Alpha.txt", "Parent/Child/Alpha-longer.txt"),
            ("Parent/Child/Alpha.txt", "Parent/Child/Alpha.txt"),
            ("Parent/Child/Alpha.txt", "parent/child/alpha.txt"),
        ];
        foreach (var (left, right) in shapes)
            Check.Require(PathNames.CaseOnly(left, right) == (left != right && PathNames.Same(left, right)),
                "CaseOnly matches its definition for " + left + " and " + right);

        Check.Require(ReferenceEquals(PathNames.Comparer, PathNames.Comparer), "Comparer is cached");
        Check.Require(PathNames.Comparer.Equals(null, null) &&
            !PathNames.Comparer.Equals(null, "") && !PathNames.Comparer.Equals("", null),
            "Comparer preserves null identity");
        var set = new HashSet<string>(PathNames.Comparer) { "Long/S" };
        Check.Require(set.Contains("long/s"), "Comparer ignores case");
        Check.Require(!set.Contains("Long/ſ"), "Comparer is ordinal, not culture-aware");
        Check.Require(!set.Contains("Long/S/Child"), "Comparer is not a prefix rule");
    }

    private static Dictionary<int, int> FrozenMappings()
    {
        using var document = CliCatalogResource.Load(typeof(PathNames).Assembly, ResourceNames.PathCase);
        var root = document.RootElement;
        var mappings = root.GetProperty("mappings");
        var provenance = root.GetProperty("provenance");
        const string digest = "99dd873ebcb35816c7adcd679295114e4ecc7597d71854cb3e64c5bf061441a2";
        Check.Require(root.GetProperty("formatVersion").GetInt32() == 1 &&
            provenance.GetProperty("mappingPayloadSha256").GetString() == digest &&
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(mappings)))
                .Equals(digest, StringComparison.OrdinalIgnoreCase),
            "the oracle is the exact approved frozen export, not the current runtime's casing");
        var result = new Dictionary<int, int>();
        var previous = -1;
        foreach (var mapping in mappings.EnumerateArray())
        {
            Check.Require(mapping.GetArrayLength() == 2, "a frozen mapping has exactly two scalars");
            var source = int.Parse(mapping[0].GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var target = int.Parse(mapping[1].GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            Check.Require(Rune.IsValid(source) && Rune.IsValid(target) && source > previous && source != target,
                "frozen mappings are valid, non-identity, unique and sorted");
            Check.Require((source <= 0x7f) == (target <= 0x7f) &&
                new Rune(source).Utf16SequenceLength == new Rune(target).Utf16SequenceLength,
                "frozen mappings preserve the ASCII partition and UTF-16 length");
            result.Add(source, target);
            previous = source;
        }
        Check.Require(result.Count == provenance.GetProperty("mappingCount").GetInt32(),
            "all exported mappings were read");
        foreach (var target in result.Values)
            Check.Require(!result.ContainsKey(target), "every exported mapping is idempotent");
        return result;
    }

    private static void FrozenIdentity()
    {
        var mappings = FrozenMappings();
        var scalars = 0;
        for (var value = 0; value <= 0x10ffff; value++)
        {
            // BMP scalar cases also cover their code unit; isolated surrogates must not become replacement runes.
            var input = value <= char.MaxValue ? ((char)value).ToString() : char.ConvertFromUtf32(value);
            var expected = mappings.TryGetValue(value, out var target) ? char.ConvertFromUtf32(target) : input;
            RequireFrozenIdentity(input, expected);
            RequireFrozenIdentity("a/" + input + "/z", "A/" + expected + "/Z");
            if (Rune.IsValid(value)) scalars++;
            if (value <= 0x7f)
                Check.Require(expected == char.ToUpperInvariant((char)value).ToString(),
                    "every ASCII scalar retains baseline uppercasing");
        }
        FrozenSql(mappings, scalars - 1);
    }

    private static void RequireFrozenIdentity(string input, string expected)
    {
        Check.Require(PathNames.Key(input) == expected && PathNames.Key(expected) == expected,
            "Key and idempotence agree with the frozen export");
        Check.Require(PathNames.Same(input, expected) && PathNames.Same(expected, input) &&
            PathNames.Comparer.Equals(input, expected) && PathNames.Comparer.Equals(expected, input),
            "Same and comparer equality agree with the frozen key in both directions");
        Check.Require(PathNames.Comparer.GetHashCode(input) == PathNames.Comparer.GetHashCode(expected),
            "equal frozen keys have equal comparer hashes");
        Check.Require(PathNames.CaseOnly(input, expected) == (input != expected),
            "CaseOnly distinguishes spelling from frozen identity");
        var other = expected == "!" ? "?" : "!";
        Check.Require(!PathNames.Same(input, other) && !PathNames.Comparer.Equals(input, other),
            "different frozen keys remain distinct");
    }

    private static void FrozenSql(Dictionary<int, int> mappings, int scalarCount)
    {
        using var probe = new CostProbe(":memory:");
        probe.Execute("CREATE TEMP TABLE frozen_mapping (source INTEGER PRIMARY KEY, target INTEGER NOT NULL);" +
            "CREATE TEMP TABLE frozen_mismatch (scalar INTEGER NOT NULL);");
        var inserted = probe.Execute(
            "INSERT INTO frozen_mapping SELECT CAST(key AS INTEGER), value FROM json_each($mappings);",
            ("$mappings", JsonSerializer.Serialize(mappings)));
        Check.Require(inserted.ChangedRows == mappings.Count, "SQL received every exported mapping");
        // An indexed map and a streaming scalar CTE avoid a million prepares or a retained million-item fixture.
        // SQLite text results truncate U+0000, which cannot occur in paths; the managed loop covers it.
        var checkedScalars = probe.Execute(
            "WITH RECURSIVE scalars(value) AS (VALUES(1) UNION ALL " +
            "SELECT value + 1 FROM scalars WHERE value < $last) " +
            "INSERT INTO frozen_mismatch SELECT value FROM scalars " +
            "LEFT JOIN frozen_mapping ON source = value " +
            "WHERE value NOT BETWEEN $surrogateStart AND $surrogateEnd AND " +
            "CAST(" + StateSchema.PathKeyFunction + "(char(value)) AS BLOB) IS NOT " +
            "CAST(char(COALESCE(target, value)) AS BLOB);",
            ("$last", 0x10ffff), ("$surrogateStart", 0xd800), ("$surrogateEnd", 0xdfff));
        Check.Require(checkedScalars.PathKeyCalls == scalarCount && checkedScalars.ChangedRows == 0,
            "SQL checked every non-NUL Unicode scalar against the exact frozen export: " +
            checkedScalars.PathKeyCalls + " calls, " + checkedScalars.ChangedRows + " mismatches");

        using var connection = StateStore.Connect(StateSchema.ReadRoot, SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + StateSchema.PathKeyFunction + "($path);";
        command.Parameters.AddWithValue("$path", "Parent/σ");
        Check.Require(command.ExecuteScalar() is string key && key == "PARENT/Σ",
            "the production SQL registration uses frozen Unicode path identity");
    }

    private static void FrozenStrings()
    {
        (string Input, string Key)[] cases =
        [
            ("", ""), ("I", "I"), ("i", "I"), ("İ", "İ"), ("ı", "ı"),
            ("ß", "ß"), ("ẞ", "ẞ"), ("ss", "SS"),
            ("Σ", "Σ"), ("σ", "Σ"), ("ς", "Σ"), ("ſ", "ſ"), ("s", "S"),
            ("a/\U00010428/z", "A/\U00010400/Z"), ("a/\U0001e943/z", "A/\U0001e921/Z"),
            ("a/\U0010ffff/z", "A/\U0010ffff/Z"),
            ("\ud800a\udbff", "\ud800A\udbff"), ("\udc00a\udfff", "\udc00A\udfff"),
            ("\udc00\ud800", "\udc00\ud800"), ("\ud800\ud800", "\ud800\ud800"),
            ("\udc00\udc00", "\udc00\udc00"), ("\ud800/\udc00", "\ud800/\udc00"),
            ("\ud800\U00010428\udfff", "\ud800\U00010400\udfff"), ("�", "�"),
        ];
        foreach (var (input, key) in cases)
        {
            RequireFrozenIdentity(input, key);
            var set = new HashSet<string>(PathNames.Comparer) { input };
            foreach (var (other, otherKey) in cases)
            {
                var equal = key == otherKey;
                Check.Require(PathNames.Same(input, other) == equal &&
                    PathNames.Comparer.Equals(input, other) == equal && set.Contains(other) == equal &&
                    PathNames.CaseOnly(input, other) == (equal && input != other),
                    "all named Unicode and malformed-string pairs agree with their frozen keys");
            }
            var prefix = "Parent/" + input;
            var foldedPrefix = "PARENT/" + key;
            Check.Require(PathNames.IsSelfOrUnder(prefix, foldedPrefix) &&
                PathNames.IsSelfOrUnder(prefix + "/Child", foldedPrefix) &&
                !PathNames.IsSelfOrUnder(prefix + "Suffix/Child", foldedPrefix),
                "frozen ancestry preserves self and segment boundaries");
            Check.Require(PathNames.RelativeUnderDropboxRoot("/" + foldedPrefix, "/" + prefix + "/Child") == "Child" &&
                PathNames.RelativeUnderDropboxRoot("/" + foldedPrefix, "/" + prefix) is null &&
                PathNames.RelativeUnderDropboxRoot("/" + foldedPrefix, "/" + prefix + "Suffix/Child") is null,
                "frozen Dropbox roots preserve strict ancestry and the unchanged suffix");
        }
    }

    private static void Boundaries()
    {
        Check.Require(PathNames.LastSeparator("") == -1, "LastSeparator of an empty span");
        Check.Require(PathNames.LastSeparator("name") == -1, "LastSeparator without a separator");
        Check.Require(PathNames.LastSeparator("/x") == 0, "a leading separator is index zero");
        Check.Require(PathNames.LastSeparator("a/b/c") == 3, "LastSeparator at depth");
        Check.Require(PathNames.LastSeparator("a/b/") == 3, "LastSeparator of a trailing separator");
        Check.Require(PathNames.LastSeparator("/") == 0, "LastSeparator of the separator itself");

        Check.Require(PathNames.NextSeparator("a/b/c", 0) == 1, "NextSeparator from the start");
        Check.Require(PathNames.NextSeparator("a/b/c", 2) == 3, "NextSeparator from a later index");
        Check.Require(PathNames.NextSeparator("a/", 1) == 1, "NextSeparator finds a separator at the start index");
        Check.Require(PathNames.NextSeparator("abc", 0) == -1, "NextSeparator without a separator");
        Check.Require(PathNames.NextSeparator("a/b", 2) == -1, "NextSeparator past the last separator");
        Check.Require(PathNames.NextSeparator("a", 1) == -1, "NextSeparator at the end is a valid search");
        RequireThrows<ArgumentOutOfRangeException>(() => PathNames.NextSeparator("a", -1),
            "NextSeparator rejects a negative start");
        RequireThrows<ArgumentOutOfRangeException>(() => PathNames.NextSeparator("a", 2),
            "NextSeparator rejects a start past the end");
    }

    private static void NamesAndParents()
    {
        Check.Require(PathNames.Name("") == "", "Name of an empty path");
        Check.Require(PathNames.Name("name") == "name", "Name of a top-level path");
        Check.Require(PathNames.Name("a/b") == "b", "Name at depth");
        Check.Require(PathNames.Name("a/b/") == "", "Name of a trailing separator");
        Check.Require(PathNames.Name("/b") == "b", "Name after a leading separator");
        Check.Require(PathNames.Name("/") == "", "Name of the separator itself");

        Check.Require(PathNames.Parent("") == "", "Parent of an empty path");
        Check.Require(PathNames.Parent("name") == "", "Parent of a top-level path");
        Check.Require(PathNames.Parent("a/b") == "a", "Parent at depth");
        Check.Require(PathNames.Parent("a/b/c") == "a/b", "Parent of a deep path");
        Check.Require(PathNames.Parent("a/b/") == "a/b", "Parent of a trailing separator");
        Check.Require(PathNames.Parent("/b") == "", "Parent after a leading separator");
        Check.Require(PathNames.Parent("/") == "", "Parent of the separator itself");

        RequireThrows<NullReferenceException>(() => PathNames.Name(null!), "Name(null)");
        RequireThrows<NullReferenceException>(() => PathNames.Parent(null!), "Parent(null)");
    }

    private static void KeyAncestry()
    {
        Check.Require(PathNames.IsUnderKey("Parent/Child", "Parent"), "a child key is under its parent");
        Check.Require(PathNames.IsUnderKey("Parent/Child/Leaf", "Parent"), "a deep key is under its root");
        Check.Require(PathNames.IsUnderKey("Parent/", "Parent"), "a trailing separator is a boundary");
        Check.Require(!PathNames.IsUnderKey("Parent", "Parent"), "a key is not under itself");
        Check.Require(!PathNames.IsUnderKey("Parentish/Child", "Parent"), "a shared name prefix is not under");
        Check.Require(!PathNames.IsUnderKey("parent/child", "Parent"), "key ancestry is ordinal");
        Check.Require(PathNames.IsUnderKey("/Child", ""), "an empty prefix matches a leading-slash descendant");
        Check.Require(!PathNames.IsUnderKey("Child", ""), "an empty prefix rejects a bare child");
        Check.Require(!PathNames.IsUnderKey("", ""), "an empty key has no descendant");

        Check.Require(PathNames.IsSelfOrUnderKey("Parent", "Parent"), "a key is self-or-under itself");
        Check.Require(PathNames.IsSelfOrUnderKey("Parent/Child", "Parent"), "a key is self-or-under its parent");
        Check.Require(!PathNames.IsSelfOrUnderKey("Parentish/Child", "Parent"), "a shared name prefix is not under");
        Check.Require(!PathNames.IsSelfOrUnderKey("parent", "Parent"), "key self-or-under is ordinal");
        Check.Require(PathNames.IsSelfOrUnderKey("", ""), "an empty key is self-or-under an empty prefix");
        Check.Require(PathNames.IsSelfOrUnderKey("/Child", ""), "an empty prefix matches a leading-slash descendant");
        Check.Require(!PathNames.IsSelfOrUnderKey("Child", ""), "an empty prefix rejects a bare child");

        Check.Require(PathNames.IsSelfOrUnder("Parent", "Parent"), "a path is self-or-under itself");
        Check.Require(PathNames.IsSelfOrUnder("parent/child", "Parent"), "path self-or-under ignores case");
        Check.Require(!PathNames.IsSelfOrUnder("Parentish/Child", "Parent"), "a shared name prefix is not under");
        Check.Require(PathNames.IsSelfOrUnder("", ""), "an empty path is self-or-under an empty prefix");
        Check.Require(!PathNames.IsSelfOrUnder("Child", ""), "an empty prefix matches only itself, unlike keys");
        Check.Require(!PathNames.IsSelfOrUnder("/Child", ""), "an empty prefix does not match a leading-slash child");
    }

    private static void Depth()
    {
        Check.Require(PathNames.Depth("") == 1, "an empty path has depth one");
        Check.Require(PathNames.Depth("a") == 1, "a top-level path has depth one");
        Check.Require(PathNames.Depth("a/b") == 2, "depth at one separator");
        Check.Require(PathNames.Depth("a/b/c") == 3, "depth at two separators");
        Check.Require(PathNames.Depth("/a") == 2, "a leading separator adds a segment");
        Check.Require(PathNames.Depth("/") == 2, "the separator alone has depth two");
        Check.Require(PathNames.Depth("a//b") == 3, "every separator counts");
        RequireThrows<NullReferenceException>(() => PathNames.Depth(null!), "Depth(null)");
    }

    private static void Keys()
    {
        var native = "native-key";
        Check.Require(ReferenceEquals(PathNames.KeyFor(Side.Local, "a/b", native), native),
            "the Local branch reuses the supplied native key");
        Check.Require(PathNames.KeyFor(Side.Dropbox, "Bad:Name", native) == PathNames.DropboxKey("Bad:Name"),
            "every non-Local side uses the raw Dropbox key");
        Check.Require(PathNames.DropboxKey("Bad:Name") == PathNames.Key("Bad:Name") &&
            PathNames.DropboxKey("Bad:Name") != PathNames.Key("Bad_Name"),
            "the raw Dropbox key folds case and never substitutes a character");
        Check.Require(PathNames.DropboxKey("a:b") != PathNames.DropboxKey("a_b"),
            "formerly colliding raw spellings keep distinct keys");
        Check.Require(PathNames.KeyFor(Side.Dropbox, "Bad:Name", native) != native,
            "the Dropbox branch ignores the native key");
        Check.Require(PathNames.KeyFor(Side.Local, "A/B") == PathNames.Key("A/B"), "two-argument Local key");
        Check.Require(PathNames.KeyFor(Side.Dropbox, "A/B") == PathNames.DropboxKey("A/B"),
            "two-argument Dropbox key");
        // The reuse selector returns exactly what the inline ternary it replaced returned, including empty and null.
        foreach (var side in new[] { Side.Local, Side.Dropbox })
            foreach (var path in new[] { "", "plain.txt", "Bad:Name", "Parent/Child" })
                Check.Require(PathNames.KeyFor(side, path, native) ==
                    (side == Side.Local ? native : PathNames.DropboxKey(path)),
                    "KeyFor matches the inline ternary: " + side + " " + path);
        Check.Require(ReferenceEquals(PathNames.KeyFor(Side.Local, null!, native), native),
            "KeyFor Local reuses the key for a null path");
        RequireThrows<NullReferenceException>(() => PathNames.KeyFor(Side.Dropbox, null!, native),
            "KeyFor Dropbox null path");
    }

    private static void Mapping()
    {
        // NAME-SKIP: the Engine facade keeps the raw Dropbox spelling; Core character mapping is dormant.
        Check.Require(!PathNames.IsMapped("plain/name"), "a plain name is not mapped");
        Check.Require(!PathNames.IsMapped("Bad:Name"), "a forbidden character is not substituted");
        Check.Require(!PathNames.IsMapped("name."), "a trailing dot is not substituted");
        Check.Require(!PathNames.IsMapped("Bad:Name", "Bad:Name"), "the raw spelling is the supplied spelling");
        Check.Require(PathNames.IsMapped("Bad:Name", "Bad_Name"), "a supplied legacy spelling can differ");
        Check.Require(PathNames.IsMapped("plain", "other"), "a supplied spelling is used as given");
        Check.Require(!PathNames.IsMapped("Bad:Name", PathNames.LocalSpelling("Bad:Name")),
            "the raw spelling composes with LocalSpelling");
        Check.Require(PathNames.LocalSpelling("Bad:Name") == "Bad:Name", "LocalSpelling keeps the raw spelling");

        foreach (var name in new[] { "a:b", "a*b", "a?b", "a\"b", "a<b", "a>b", "a|b", "a.", "a " })
        {
            Check.Require(PathNames.LocalSpelling("Parent/" + name) == "Parent/" + name,
                "no mapped character is substituted: " + name);
            Check.Require(!PathNames.IsTransferable("Parent/" + name),
                "a name Windows rejects must not transfer: " + name);
        }

        Check.Require(PathNames.IsTransferable("plain/name"), "a plain path is transferable");
        Check.Require(!PathNames.IsTransferable("Bad:Name"), "a forbidden character must not transfer");
        Check.Require(!PathNames.IsTransferable("name."), "a trailing dot must not transfer");
        Check.Require(!PathNames.IsTransferable("name "), "a trailing space must not transfer");
        Check.Require(!PathNames.IsTransferable("CON"), "a reserved Windows name must not transfer");
        Check.Require(!PathNames.IsTransferable("Bad:Name/child"), "an invalid ancestor must not transfer");
        Check.Require(PathNames.IsTransferable("Bad_Name"), "a legitimate underscore name is transferable");
        Check.Require(PathNames.IsTransferable("Bad_Name/child"), "a legitimate underscore ancestor is transferable");
        Check.Require(!PathNames.IsTransferable("/a"), "a rooted Dropbox spelling is not a relative transfer path");
        Check.Require(!PathNames.IsTransferable("a//b"), "a repeated separator is not transferable");
        Check.Require(!PathNames.IsTransferable(""), "an empty path is not transferable");

        Check.Require(PathNames.CaseRenamePath("a/b", ".case") == "a/b.case", "CaseRenamePath appends the suffix");
        Check.Require(PathNames.SpellingOn(Side.Local, "Bad:Name") == "Bad:Name",
            "Local keeps the raw spelling without substitution");
        Check.Require(ReferenceEquals(PathNames.SpellingOn(Side.Dropbox, "Bad:Name"), "Bad:Name"),
            "Dropbox keeps the supplied spelling");
    }

    private static void NameShape()
    {
        Check.Require(!PathNames.IsSingleSegment(null), "null is not one segment");
        Check.Require(!PathNames.IsSingleSegment(""), "an empty name is not one segment");
        Check.Require(!PathNames.IsSingleSegment("."), "a dot is not one segment");
        Check.Require(!PathNames.IsSingleSegment(".."), "two dots are not one segment");
        Check.Require(!PathNames.IsSingleSegment("..."), "dots-only is not one segment");
        Check.Require(PathNames.IsSingleSegment("name"), "a plain name is one segment");
        Check.Require(PathNames.IsSingleSegment("name.ext"), "a name with a dot is one segment");
        Check.Require(!PathNames.IsSingleSegment("a/b"), "a relative separator is rejected");
        Check.Require(!PathNames.IsSingleSegment("a" + Path.DirectorySeparatorChar + "b"),
            "the platform separator is rejected");
        Check.Require(!PathNames.IsSingleSegment("a" + Path.AltDirectorySeparatorChar + "b"),
            "the alternate separator is rejected");
        Check.Require(PathNames.IsSingleSegment("CON"), "reserved Windows names are not rejected here");
        Check.Require(PathNames.IsSingleSegment("name "), "a trailing space is not rejected here");
        Check.Require(PathNames.IsSingleSegment(" ."), "a space before a dot is not dots-only");
    }

    private static void DropboxRoots()
    {
        Check.Require(PathNames.IsDropboxRoot("/"), "the separator alone is a Dropbox root");
        Check.Require(PathNames.IsDropboxRoot("/root"), "an absolute path is a Dropbox root");
        Check.Require(PathNames.IsDropboxRoot("//"), "repeated separators are a Dropbox root");
        Check.Require(PathNames.IsDropboxRoot("//a//b"), "repeated separators at depth are a Dropbox root");
        Check.Require(PathNames.IsDropboxRoot("/a/b:c"), "a colon after the first segment is allowed");
        Check.Require(!PathNames.IsDropboxRoot("/a:b"), "a colon in the first segment is a drive prefix");
        Check.Require(!PathNames.IsDropboxRoot("/C:/x"), "a drive prefix in the first segment is rejected");
        Check.Require(!PathNames.IsDropboxRoot("root"), "a relative path is not a Dropbox root");
        Check.Require(!PathNames.IsDropboxRoot(""), "an empty path is not a Dropbox root");
        Check.Require(!PathNames.IsDropboxRoot("/a\\b"), "a backslash is rejected");
        Check.Require(!PathNames.IsDropboxRoot("\\a"), "a leading backslash is rejected");

        Check.Require(PathNames.TrimDropboxRoot("/") == "", "a separator-only root trims to empty");
        Check.Require(PathNames.TrimDropboxRoot("///") == "", "an all-slash root trims to empty");
        Check.Require(PathNames.TrimDropboxRoot("") == "", "an empty root stays empty");
        Check.Require(PathNames.TrimDropboxRoot("/root/") == "/root", "one trailing separator is trimmed");
        Check.Require(PathNames.TrimDropboxRoot("/root//") == "/root", "repeated trailing separators are trimmed");
        Check.Require(PathNames.TrimDropboxRoot("/root/sub") == "/root/sub", "a root without a trailing separator");
        Check.Require(PathNames.TrimDropboxRoot("root/") == "root", "no leading slash is added");

        Check.Require(PathNames.RootedDropboxPath("", "") == "", "the provider account root stays empty");
        Check.Require(PathNames.RootedDropboxPath("/", "") == "", "an all-slash root is the empty account root");
        Check.Require(PathNames.RootedDropboxPath("/root", "") == "/root", "an empty relative path is the root");
        Check.Require(PathNames.RootedDropboxPath("/root/", "child") == "/root/child",
            "a trimmed root composes with the relative path");
        Check.Require(PathNames.RootedDropboxPath("", "child") == "/child", "the account root composes at one slash");
        Check.Require(PathNames.RootedDropboxPath("/root", "a/b") == "/root/a/b", "a deep relative path");
        Check.Require(PathNames.RootedDropboxPath("///", "x") == "/x", "all-slash roots trim before composition");

        Check.Require(PathNames.RelativeUnderDropboxRoot("/root", "/root/child") == "child",
            "a child strips the root and one separator");
        Check.Require(PathNames.RelativeUnderDropboxRoot("/Root", "/root/child") == "child",
            "the root prefix ignores case");
        Check.Require(PathNames.RelativeUnderDropboxRoot("/root", "/root") is null,
            "equality is not handled here");
        Check.Require(PathNames.RelativeUnderDropboxRoot("/root", "/ROOT") is null,
            "equality in another case is not handled here");
        Check.Require(PathNames.RelativeUnderDropboxRoot("/root", "/rootish/child") is null,
            "the boundary must be a separator");
        Check.Require(PathNames.RelativeUnderDropboxRoot("/root", "/other/child") is null,
            "an outside path is not under the root");
        Check.Require(PathNames.RelativeUnderDropboxRoot("/root", "/roo") is null,
            "a shorter path is not under the root");
        Check.Require(PathNames.RelativeUnderDropboxRoot("", "/child") == "child",
            "an empty root works for account-root paths");
        Check.Require(PathNames.RelativeUnderDropboxRoot("", "") is null, "an empty path is not under an empty root");
        Check.Require(PathNames.RelativeUnderDropboxRoot("/root", "/root/") == "",
            "a terminal boundary separator leaves an empty relative path");
        Check.Require(PathNames.RelativeUnderDropboxRoot("/root", "/root//child") == "/child",
            "only one boundary separator is stripped");
    }

    private static void LocalRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(PathNamesChecks), "Outer");
        var nested = Path.Combine(root, "a", "b");
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(root))!;

        Check.Require(PathNames.NormalizeLocalPath(root + Path.DirectorySeparatorChar) ==
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), "NormalizeLocalPath trims a separator");
        Check.Require(PathNames.NormalizeLocalPath(volumeRoot) == volumeRoot,
            "NormalizeLocalPath preserves the volume root");
        RequireThrows<ArgumentException>(() => PathNames.NormalizeLocalPath(""),
            "an empty path is rejected by the framework");

        Check.Require(PathNames.RelativeUnderLocalRoot(root, nested) == "a/b",
            "a nested path becomes a '/'-relative path");
        Check.Require(PathNames.RelativeUnderLocalRoot(root, root) is null,
            "the root itself is not strictly inside");
        Check.Require(PathNames.RelativeUnderLocalRoot(root, root + "Sibling") is null,
            "a shared name prefix is not inside");
        Check.Require(PathNames.RelativeUnderLocalRoot(root,
            root.ToUpperInvariant() + Path.DirectorySeparatorChar + "Name") == "Name",
            "the root prefix ignores case");
        Check.Require(PathNames.RelativeUnderLocalRoot(volumeRoot, nested) ==
            Path.GetRelativePath(volumeRoot, nested).Replace(Path.DirectorySeparatorChar, '/'),
            "a path below the volume root is relative");
        Check.Require(PathNames.RelativeUnderLocalRoot(volumeRoot, volumeRoot) == "",
            "a separator-terminated normalized root can leave an empty suffix");
    }

    private static void Evidence()
    {
        RequireInline(nameof(PathNames.Same), typeof(string), typeof(string));
        RequireInline(nameof(PathNames.CaseOnly), typeof(string), typeof(string));
        RequireInline(nameof(PathNames.LastSeparator), typeof(ReadOnlySpan<char>));
        RequireInline(nameof(PathNames.NextSeparator), typeof(string), typeof(int));
        RequireInline(nameof(PathNames.Name), typeof(string));
        RequireInline(nameof(PathNames.Parent), typeof(string));
        RequireInline(nameof(PathNames.IsUnderKey), typeof(string), typeof(string));
        RequireInline(nameof(PathNames.IsSelfOrUnderKey), typeof(string), typeof(string));
        RequireInline(nameof(PathNames.IsSelfOrUnder), typeof(string), typeof(string));
        RequireInline(nameof(PathNames.Depth), typeof(string));
        RequireInline(nameof(PathNames.KeyFor), typeof(Side), typeof(string), typeof(string));
        RequireInline(nameof(PathNames.SpellingOn), typeof(Side), typeof(string));
        RequireInline(nameof(PathNames.IsMapped), typeof(string));
        RequireInline(nameof(PathNames.IsMapped), typeof(string), typeof(string));
        RequireInline(nameof(PathNames.IsTransferable), typeof(string));
        RequireInline(nameof(PathNames.CaseRenamePath), typeof(string), typeof(string));
        RequireInline(nameof(PathNames.IsSingleSegment), typeof(string));
        RequireInline(nameof(PathNames.TrimDropboxRoot), typeof(string));
        RequireInline(nameof(PathNames.RootedDropboxPath), typeof(string), typeof(string));
        RequireInline(nameof(PathNames.RelativeUnderDropboxRoot), typeof(string), typeof(string));

        AllocProbe.RequireAddsNothing();
        RequireAllocationFree(index => PathNames.Same("Parent/Child", "parent/child"), nameof(PathNames.Same));
        RequireAllocationFree(index => PathNames.CaseOnly("a", "A"), nameof(PathNames.CaseOnly));
        RequireAllocationFree(index => PathNames.LastSeparator("a/b/c"), nameof(PathNames.LastSeparator));
        RequireAllocationFree(index => PathNames.NextSeparator("a/b/c", 0), nameof(PathNames.NextSeparator));
        RequireAllocationFree(index => PathNames.IsUnderKey("Parent/Child", "Parent"), nameof(PathNames.IsUnderKey));
        RequireAllocationFree(index => PathNames.IsSelfOrUnderKey("Parent/Child", "Parent"),
            nameof(PathNames.IsSelfOrUnderKey));
        RequireAllocationFree(index => PathNames.IsSelfOrUnder("Parent/Child", "Parent"),
            nameof(PathNames.IsSelfOrUnder));
        RequireAllocationFree(index => PathNames.Depth("a/b/c"), nameof(PathNames.Depth));
        var native = "native";
        RequireAllocationFree(index => PathNames.KeyFor(Side.Local, "a/b", native), "KeyFor Local reuse");
        // X2-1: the reuse selector allocates exactly what the inline ternary it replaced allocated.
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var owner = AllocProbe.Measure(index => SinkString(PathNames.KeyFor(side, "Bad:Name", native)), 1_000, 100);
            var inline = AllocProbe.Measure(index =>
                SinkString(side == Side.Local ? native : PathNames.DropboxKey("Bad:Name")), 1_000, 100);
            Check.Require(owner == inline,
                "KeyFor " + side + " allocates the inline expression's bytes: " + owner + " vs " + inline);
        }
        RequireAllocationFree(index => PathNames.SpellingOn(Side.Dropbox, "a/b"), "SpellingOn Dropbox reuse");
        RequireAllocationFree(index => PathNames.IsMapped("plain/name"), nameof(PathNames.IsMapped));
        RequireAllocationFree(index => PathNames.IsMapped("Bad:Name", "Bad_Name"), "IsMapped supplied spelling");
        RequireAllocationFree(index => PathNames.IsSingleSegment("name.ext"), nameof(PathNames.IsSingleSegment));
        RequireAllocationFree(index => PathNames.TrimDropboxRoot("/root"), nameof(PathNames.TrimDropboxRoot));
        FrozenAllocations();
    }

    private static void FrozenAllocations()
    {
        foreach (var path in new[] { "Parent/Child", new string('a', 128), new string('a', 512),
            "Parent/σ/\U00010428", new string('σ', 128), new string('σ', 512), "Parent/\ud800a\udfff" })
        {
            var key = PathNames.Key(path);
            var descendant = path + "/Child";
            RequireAllocationFree(index => PathNames.Same(path, key), "frozen Same");
            RequireAllocationFree(index => PathNames.CaseOnly(path, key), "frozen CaseOnly");
            RequireAllocationFree(index => PathNames.Comparer.Equals(path, key), "frozen comparer equality");
            RequireAllocationFree(index => PathNames.Comparer.GetHashCode(path), "frozen comparer hash");
            RequireAllocationFree(index => PathNames.IsSelfOrUnder(descendant, key), "frozen prefix");
            var allocated = AllocProbe.Measure(index => SinkString(PathNames.Key(path)), 1_000, 100);
            var oneString = AllocProbe.Measure(index => SinkString(new string(path.AsSpan())), 1_000, 100);
            Check.Require(allocated <= oneString, "frozen Key allocates at most its one output string");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkString(string? value) => StringSink = value;

    private static string? StringSink;

    private static void RequireInline(string name, params Type[] parameters)
    {
        var method = typeof(PathNames).GetMethod(name, parameters) ??
            throw new InvalidOperationException("PathNames." + name + " is missing");
        Check.Require((method.MethodImplementationFlags & MethodImplAttributes.AggressiveInlining) != 0,
            "AggressiveInlining: PathNames." + name);
    }

    private static void RequireAllocationFree(Action<int> action, string what)
    {
        var allocated = AllocProbe.Measure(action, 10_000, 2_000);
        Check.Require(allocated == 0, what + " allocated " + allocated + " bytes");
    }

    private static void RequireThrows<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            Check.Require(error.GetType() == typeof(TException), what + ": " + error.GetType().Name);
            return;
        }
        throw new InvalidOperationException(what + ": " + typeof(TException).Name + " was not thrown");
    }
}
