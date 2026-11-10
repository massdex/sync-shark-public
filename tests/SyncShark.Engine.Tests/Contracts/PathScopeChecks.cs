namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Checks for PathScope (`--engine-checks scope`), written by PathScope's writer.</summary>
public static class PathScopeChecks
{
    public static void Run()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), nameof(PathScopeChecks));
        var dropboxRoot = PathNames.PathSeparator + nameof(PathScopeChecks);
        var relatives = "Relatives";
        var pair = new PairConfig("pair", "pair", localRoot, dropboxRoot, "pair",
            ["FileExtension:.tmp;FileName:Thumbs.db;FolderName:node_modules;FileNamePrefix:~$;FileNamePrefix:.~"],
            [new Exclusion(Side.Local, "Docs/Secret", true), new Exclusion(Side.Dropbox, "Hidden", false),
                new Exclusion(Side.Local, "a/b", true), new Exclusion(Side.Dropbox, "Bad:Name", true),
                new Exclusion(Side.Local, "Long/s", true), new Exclusion(Side.Local, "Long/σ", true)],
            FirstRunPolicy.NewestWins, true, null);
        var inner = pair with
        {
            PairId = "inner", Name = "inner",
            LocalRoot = Path.Combine(localRoot, "inner"),
            DropboxRoot = PathNames.Child(dropboxRoot, "inner")
        };
        var deep = pair with
        {
            PairId = "deep", Name = "deep",
            LocalRoot = Path.Combine(localRoot, "deep", relatives),
            DropboxRoot = PathNames.Child(PathNames.Child(dropboxRoot, "deep"), relatives)
        };
        var equal = pair with { PairId = "equal", Name = "equal" };
        var otherAccount = pair with
        {
            PairId = "otherAccount", Name = "otherAccount", ConnectionId = "otherAccount",
            LocalRoot = Path.Combine(localRoot, "otherAccount"),
            DropboxRoot = PathNames.Child(dropboxRoot, "otherAccount")
        };
        var disabled = pair with
        {
            PairId = "disabled", Name = "disabled",
            LocalRoot = Path.Combine(localRoot, "disabled"),
            DropboxRoot = PathNames.Child(dropboxRoot, "disabled"), Enabled = false
        };
        var dropboxOnly = pair with
        {
            PairId = "dropboxOnly", Name = "dropboxOnly",
            LocalRoot = Path.Combine(Path.GetTempPath(), nameof(PathScopeChecks) + relatives),
            DropboxRoot = PathNames.Child(dropboxRoot, "dropboxOnly")
        };
        var mappedNested = pair with
        {
            PairId = "mappedNested", Name = "mappedNested",
            LocalRoot = Path.Combine(Path.GetTempPath(), nameof(PathScopeChecks) + "Mapped"),
            DropboxRoot = PathNames.Child(dropboxRoot, "Nested:Mapped")
        };
        var scope = new PathScope(pair,
            [pair, inner, deep, equal, otherAccount, disabled, dropboxOnly, mappedNested]);

        RequireRoots(scope.NestedRoots(Side.Local), nameof(inner), nameof(otherAccount),
            PathNames.Child(nameof(deep), relatives));
        RequireRoots(scope.NestedRoots(Side.Dropbox), nameof(inner), nameof(dropboxOnly),
            PathNames.Child(nameof(deep), relatives), "Nested:Mapped");

        Excluded(scope, "Docs/Secret", null, false, true, "exclusion on the local side");
        Excluded(scope, "Docs/Secret/file.txt", null, false, true, "under a local exclusion");
        Excluded(scope, null, "Docs/Secret/file.txt", false, true, "a local exclusion seen from the Dropbox path");
        Excluded(scope, "Hidden", null, false, true, "a Dropbox exclusion kept the local row out of sync");
        Excluded(scope, "Hidden/child", "Hidden/child", false, true, "under a Dropbox exclusion");
        Excluded(scope, "Docs/SecretSibling", null, false, false, "an exclusion ends at a segment boundary");
        Excluded(scope, "a/b/c/d/e", null, false, true, "deep under an exclusion");
        Excluded(scope, "a/bc", null, false, false, "a shared name prefix is not under the exclusion");
        Excluded(scope, "A/B/C", null, false, true, "an exclusion key ignores case at depth");
        Excluded(scope, null, "Hidden/child/grandchild", false, true, "deep under a Dropbox exclusion");
        Excluded(scope, "docs/secret", null, false, true, "keys ignore case");
        Excluded(scope, "a/b.tmp", null, false, true, "an ignored file extension");
        Excluded(scope, null, "a/b.TMP", false, true, "an ignored file extension on the Dropbox path");
        Excluded(scope, "node_modules/x", null, true, true, "an ignored folder name");
        Excluded(scope, "Thumbs.db", null, false, true, "an ignored file name");
        Excluded(scope, "Thumbs.db.bak", null, false, false, "an ignored name must match exactly");
        Excluded(scope, "~$Book.xlsx", null, false, true, "a configured file-name prefix");
        Excluded(scope, null, "Parent/~$Report.docx", false, true, "a configured prefix on the Dropbox path");
        Excluded(scope, "Parent/Child/.~lock.Book.ods#", null, false, true, "the second configured prefix at depth");
        Excluded(scope, "~$", null, false, true, "the prefix alone matches a file");
        Excluded(scope, "Book~$copy.xlsx", null, false, false, "a prefix must start the final name");
        Excluded(scope, "~$Folder", null, true, false, "a file-name prefix does not ignore a same-named directory");
        Excluded(scope, "~$Folder/ordinary.txt", null, false, false, "a prefix-named directory does not ignore its children");
        var genericPrefix = pair with { IgnoreRules = ["FileNamePrefix:temp"] };
        var genericPrefixScope = new PathScope(genericPrefix, [genericPrefix]);
        Excluded(genericPrefixScope, "temp.log", null, false, true, "a generic configured prefix matches");
        Excluded(genericPrefixScope, "Parent/TEMP", null, false, true, "a generic prefix ignores case at depth");
        Excluded(genericPrefixScope, "attemptemp.log", null, false, false, "a generic prefix must start the final name");
        Excluded(scope, "Inner/x", null, false, true, "under a local nested root");
        Excluded(scope, "Inner/child/deeper", null, false, true, "deep under a local nested root");
        Excluded(scope, "InnerSibling", null, false, false, "a nested root ends at a segment boundary");
        Excluded(scope, null, "Inner/child/deeper", false, true, "deep under a Dropbox nested root");
        Excluded(scope, null, "Inner", false, true, "at a Dropbox nested root");
        Excluded(scope, null, "Inner/x", false, true, "under a Dropbox nested root");
        Excluded(scope, "Deep", null, true, false, "a parent of a nested root is itself in scope");
        Excluded(scope, PathNames.Child(PathNames.Child(nameof(deep), relatives), nameof(pair)), null, false,
            true, "under a deeper nested root");
        Excluded(scope, "Deep/Relatives/x/y", null, false, true, "deep under a deeper nested root");
        Excluded(scope, PathNames.Child(nameof(otherAccount), nameof(pair)), null, false, true,
            "another connection's local root is still local-nested");
        Excluded(scope, null, PathNames.Child(nameof(otherAccount), nameof(pair)), false, true,
            "a local nested root also keeps the Dropbox-only row out of sync (bilateral)");
        Excluded(scope, null, "dropboxOnly/x", false, true, "a Dropbox nested root with a foreign local root");
        Excluded(scope, "dropboxOnly/x", null, false, true,
            "a Dropbox-only nested root also keeps the local row out of sync (bilateral)");
        Excluded(scope, "disabled/x", null, false, false, "a switched-off pair is not nested");
        Excluded(scope, "Other/x", null, false, false, "an unrelated path");
        Excluded(scope, null, "Bad:Name/x", false, true, "a Dropbox exclusion matches the raw spelling");
        Excluded(scope, "Bad:Name/x", null, false, true, "a Dropbox exclusion is bilateral at the raw spelling");
        Excluded(scope, "Long/Σ", null, false, true, "an exclusion key folds Greek sigma case");
        Excluded(scope, "Long/ς", null, false, true, "a final sigma is the same key");
        Excluded(scope, "Long/ſ", null, false, false, "long s is not the same key as s");

        Check.Require(scope.HoldsNestedRoot(Side.Local, nameof(deep)), nameof(PathScope.HoldsNestedRoot));
        Check.Require(scope.HoldsNestedRoot(Side.Local, PathNames.Child(nameof(deep), relatives)),
            nameof(PathScope.HoldsNestedRoot));
        Check.Require(!scope.HoldsNestedRoot(Side.Local, PathNames.Child(
            PathNames.Child(nameof(deep), relatives), nameof(pair))), "a root above the path is not held");
        Check.Require(!scope.HoldsNestedRoot(Side.Local, "Deep/Relatives/x/y"),
            "a nested root above a deep path is not held");
        Check.Require(!scope.HoldsNestedRoot(Side.Local, nameof(deep)[..1]), "a path boundary is respected");
        Check.Require(!scope.HoldsNestedRoot(Side.Local, nameof(inner) + PathNames.PathSeparator + nameof(pair)),
            "a nested root above the path is not held");
        Check.Require(scope.HoldsNestedRoot(Side.Local, nameof(otherAccount)), "the local root of another account");
        Check.Require(!scope.HoldsNestedRoot(Side.Dropbox, nameof(otherAccount)),
            "Dropbox roots compare only within the same connection");
        Check.Require(scope.HoldsNestedRoot(Side.Dropbox, nameof(dropboxOnly)), nameof(dropboxOnly));
        Check.Require(!scope.HoldsNestedRoot(Side.Local, nameof(dropboxOnly)), "a Dropbox-only nested root");
        Check.Require(!scope.HoldsNestedRoot(Side.Local, nameof(Exclusion)), "an unrelated path");

        Check.Require(scope.OverlapsNestedRoot(Side.Local, nameof(deep), PathNames.KeyFor(Side.Local, nameof(deep))),
            "OverlapsNestedRoot: a nested root under the path");
        Check.Require(scope.OverlapsNestedRoot(Side.Local, "Inner/child/x", PathNames.KeyFor(Side.Local, "Inner/child/x")),
            "OverlapsNestedRoot: the key lies under a nested root");
        Check.Require(!scope.HoldsNestedRoot(Side.Local, "Inner/child/x"),
            "HoldsNestedRoot: a path under a nested root is not held");
        Check.Require(scope.OverlapsNestedRoot(Side.Local, "Other", PathNames.KeyFor(Side.Local, nameof(inner))),
            "OverlapsNestedRoot: the supplied key is used");
        Check.Require(!scope.OverlapsNestedRoot(Side.Local, "Other", PathNames.KeyFor(Side.Local, "Other")),
            "OverlapsNestedRoot: an unrelated path and key");
        Check.Require(scope.OverlapsNestedRoot(Side.Dropbox, "dropboxOnly/child",
                PathNames.KeyFor(Side.Dropbox, "dropboxOnly/child")),
            "OverlapsNestedRoot: a Dropbox nested root above the supplied key");
        Check.Require(scope.OverlapsNestedRoot(Side.Dropbox, nameof(deep), PathNames.DropboxKey("Other")),
            "OverlapsNestedRoot derives the root-under-path key from the path");

        // The path's own key still protects a descendant whose stored key has drifted away from the root.
        var savedDescendant = PathNames.Child(nameof(inner), "saved");
        Check.Require(scope.OverlapsNestedRoot(Side.Local, savedDescendant,
                PathNames.KeyFor(Side.Local, "Elsewhere")),
            "OverlapsNestedRoot: a saved-only local descendant with a drifted stored key is protected by its path");
        Check.Require(scope.OverlapsNestedRoot(Side.Dropbox, savedDescendant,
                PathNames.KeyFor(Side.Dropbox, "Elsewhere")),
            "OverlapsNestedRoot: a saved-only Dropbox descendant with a drifted stored key is protected by its path");
        Check.Require(!scope.OverlapsNestedRoot(Side.Local, "InnerSibling", PathNames.KeyFor(Side.Local, "InnerSibling")),
            "OverlapsNestedRoot: an adjacent name prefix is not protected");
        var mappedDescendant = PathNames.Child("Nested:Mapped", "saved");
        Check.Require(scope.OverlapsNestedRoot(Side.Dropbox, mappedDescendant,
                PathNames.DropboxKey("Elsewhere")),
            "OverlapsNestedRoot: a mapped nested root protects a descendant with a drifted stored key");
        Check.Require(!scope.OverlapsNestedRoot(Side.Local, PathNames.Child("disabled", "x"),
                PathNames.KeyFor(Side.Local, PathNames.Child("disabled", "x"))),
            "OverlapsNestedRoot: a switched-off nested pair is not protected");
        Excluded(scope, null, "Bad:Name/mapped", false, true,
            "a Dropbox descendant is excluded at its raw spelling");

        var narrowed = scope.Without(new Exclusion(Side.Local, "Docs/Secret", true));
        Excluded(narrowed, "Docs/Secret", null, false, false, "Without removes that one exclusion");
        Excluded(narrowed, "Hidden", null, false, true, "Without keeps the other exclusions");
        Excluded(narrowed, "Inner/x", null, false, true, "Without keeps nested roots");
        Excluded(narrowed, "a/b.tmp", null, false, true, "Without keeps ignore rules");
        Excluded(narrowed, "~$Book.xlsx", null, false, true, "Without keeps configured prefixes");
        var keptDropbox = scope.Without(new Exclusion(Side.Dropbox, "Hidden", false));
        Excluded(keptDropbox, "Hidden", null, false, false, "Without removes a Dropbox exclusion");
        Excluded(keptDropbox, "Docs/Secret", null, false, true, "Without keeps a local exclusion");
        var untouched = scope.Without(new Exclusion(Side.Local, "missing", false));
        Excluded(untouched, "Docs/Secret", null, false, true, "Without of an excluding record changes nothing");

        var protection = scope.ProtectionWithout(new Exclusion(Side.Local, "Docs/Secret", true));
        Excluded(protection, "Docs/Secret", null, false, false, "ProtectionWithout removes that exclusion");
        Excluded(protection, "Hidden", null, false, true, "ProtectionWithout keeps the other exclusions");
        Excluded(protection, "a/b.tmp", null, false, false, "ProtectionWithout drops ignore rules");
        Excluded(protection, "Inner/x", null, false, true, "ProtectionWithout keeps nested roots");

        // The engine's default ignore names apply through the same rule matching, even with no pair rules.
        var bare = pair with { IgnoreRules = [] };
        var withDefaults = new PathScope(bare, [bare], "FileName:desktop.ini;FileName:thumbs.db");
        Excluded(withDefaults, "Desktop.ini", null, false, true, "a default ignore name ignores case");
        Excluded(withDefaults, null, "sub/DESKTOP.INI", false, true, "a default ignore name anywhere");
        Excluded(withDefaults, "Desktop.ini.bak", null, false, false, "a default ignore name matches exactly");
        Excluded(withDefaults, "ordinary.txt", null, false, false, "an ordinary file is unaffected");
        Excluded(withDefaults.ProtectionWithout(new Exclusion(Side.Local, "missing", false)), "Desktop.ini", null,
            false, false, "ProtectionWithout drops the default ignore rules too");

        // The shipped settings - not a local list - drive the defaults through production scope composition.
        var shippedJson = ShippedSettingsJson();
        var shipped = SettingsParser.Parse(shippedJson, null);
        var composed = new PathScope(bare, [bare], shipped.DefaultIgnoreRules);
        Excluded(composed, "desktop.ini", null, false, true, "the shipped defaults ignore desktop.ini");
        Excluded(composed, "Desktop.INI", null, false, true, "the shipped default name ignores case");
        Excluded(composed, "ordinary.txt", null, false, false, "the shipped defaults leave ordinary files in scope");
        var pairRule = bare with { IgnoreRules = ["FileExtension:.tmp"] };
        var composedWithPairRule = new PathScope(pairRule, [pairRule], shipped.DefaultIgnoreRules);
        Excluded(composedWithPairRule, "a.tmp", null, false, true, "a pair rule applies beside the shipped defaults");
        Excluded(composedWithPairRule, "desktop.ini", null, false, true, "the shipped defaults apply beside a pair rule");
        Excluded(composedWithPairRule.Without(new Exclusion(Side.Local, "missing", false)), "desktop.ini", null,
            false, true, "Without preserves the shipped defaults");
        var overridden = SettingsParser.Parse(shippedJson, """{ "DefaultIgnoreRules": "" }""");
        var overriddenScope = new PathScope(bare, [bare], overridden.DefaultIgnoreRules);
        Excluded(overriddenScope, "desktop.ini", null, false, false, "an empty user override removes the shipped defaults");
        Excluded(composed, "a.tmp", null, false, false, "an empty pair list inherits only the defaults");
        Excluded(composed.ProtectionWithout(new Exclusion(Side.Local, "missing", false)), "~$Book.xlsx", null,
            false, false, "ProtectionWithout drops the shipped prefixes too");
        var duplicatePrefix = bare with { IgnoreRules = ["FileNamePrefix:~$"] };
        var composedDuplicate = new PathScope(duplicatePrefix, [duplicatePrefix], shipped.DefaultIgnoreRules);
        Excluded(composedDuplicate, "~$Book.xlsx", null, false, true,
            "a prefix repeated in pair and defaults does not change the result");
        var withoutPrefix = SettingsParser.Parse(shippedJson, """{ "DefaultIgnoreRules": "FileName:desktop.ini" }""");
        var withoutPrefixScope = new PathScope(bare, [bare], withoutPrefix.DefaultIgnoreRules);
        Excluded(withoutPrefixScope, "desktop.ini", null, false, true, "an override keeps its own rule");
        var emptyDefaultsWithPairRule = new PathScope(pairRule, [pairRule], overridden.DefaultIgnoreRules);
        Excluded(emptyDefaultsWithPairRule, "a.tmp", null, false, true, "pair rules apply beside empty defaults");
        Excluded(emptyDefaultsWithPairRule, "desktop.ini", null, false, false, "empty defaults remove the shipped rules");
        foreach (var (prefix, file) in new[] { ("~$", "~$Book.xlsx"), (".~", ".~lock.Book.ods#") })
        {
            foreach (var (configuredScope, ignored, label) in new[]
            {
                (composed, true, "shipped defaults with an empty pair list"),
                (withoutPrefixScope, false, "custom defaults omitting prefixes"),
                (overriddenScope, false, "empty defaults with an empty pair list"),
                (emptyDefaultsWithPairRule, false, "empty defaults with a pair rule")
            })
            {
                Excluded(configuredScope, file, null, false, ignored, $"{label}: {prefix} file");
                Excluded(configuredScope, null, "Parent/Child/" + file, false, ignored,
                    $"{label}: {prefix} Dropbox file at depth");
                Excluded(configuredScope, "Parent/Book" + prefix + "copy.xlsx", null, false, false,
                    $"{label}: {prefix} must start the final name");
                Excluded(configuredScope, "ordinary.txt", null, false, false, $"{label}: ordinary sibling");
                Excluded(configuredScope, prefix + "Folder", null, true, false, $"{label}: {prefix} directory");
                Excluded(configuredScope, prefix + "Folder/ordinary.txt", null, false, false,
                    $"{label}: ordinary child of a {prefix} directory");
            }
        }

        // CV-D09: the saved keep selectors cover a group only when every retained entry is still ignored by
        // the pair's rules and matched by one of those selectors; an excluded-only entry is not ignored.
        var keepPair = pair with
        {
            IgnoreRules = ["FileExtension:.tmp;FolderName:node_modules;FileNamePrefix:~$"],
            IgnoredFolderKeepRules = ["FileExtension:.tmp;FolderName:node_modules"]
        };
        var keepScope = new PathScope(keepPair, [keepPair]);
        Covers(keepScope, true, "an all-ignored file and folder set is covered",
            EntryOf("a.tmp", false), EntryOf("PARENT/A.TMP", false), EntryOf("node_modules", true),
            EntryOf("Parent/node_modules/child.txt", false));
        Covers(keepScope, false, "a mixed group is refused when one ignored entry has no keep selector",
            EntryOf("a.tmp", false), EntryOf("~$Book.xlsx", false));
        Covers(keepScope, false, "an excluded-only entry is not an ignored entry",
            EntryOf("Docs/Secret/file.txt", false));
        Covers(keepScope, false, "a same-named directory is not covered by a file-extension selector",
            EntryOf("folder.tmp", true));
        var prefixKeep = pair with
        {
            IgnoreRules = ["FileNamePrefix:~$"],
            IgnoredFolderKeepRules = ["FileNamePrefix:~$"]
        };
        var prefixKeepScope = new PathScope(prefixKeep, [prefixKeep]);
        Covers(prefixKeepScope, true, "a saved prefix selector covers a matching file",
            EntryOf("~$Book.xlsx", false), EntryOf("Parent/~$Report.docx", false));
        Covers(prefixKeepScope, false, "a file-name prefix does not cover a same-named directory",
            EntryOf("~$Folder", true));
        var noKeepPair = keepPair with { IgnoredFolderKeepRules = null };
        var noKeepScope = new PathScope(noKeepPair, [noKeepPair]);
        Covers(noKeepScope, false, "empty save rules cover nothing", EntryOf("a.tmp", false));
        Covers(noKeepScope, true, "an empty retained set needs no coverage");

        // R2-27: the one-constructor ProtectionWithout state answers exactly like the former two-step build.
        RequireProtectionEquivalence(pair, 32);
        RequireProtectionEquivalence(pair, 256);

        // Only control characters short-circuit to no match; every other malformed spelling still throws.
        Check.Require(!withDefaults.Excludes("bad\u0001name", null, false),
            "a control-character path matches no ignore rule");
        ThrowsArgument(() => withDefaults.Excludes("/name", null, false), "a leading slash still throws");
        ThrowsArgument(() => withDefaults.Excludes("a//b", null, false), "an empty segment still throws");
        ThrowsArgument(() => withDefaults.Excludes("../name", null, false), "a parent segment still throws");
    }

    private static void ThrowsArgument(Action action, string what)
    {
        try
        {
            action();
            Check.Require(false, what);
        }
        catch (ArgumentException)
        {
        }
    }

    private static string ShippedSettingsJson()
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        return document.RootElement.GetRawText();
    }

    private static void RequireRoots(IReadOnlyList<string> actual, params string[] expected)
    {
        var sorted = actual.OrderBy(root => root, StringComparer.Ordinal).ToArray();
        var wanted = expected.OrderBy(root => root, StringComparer.Ordinal).ToArray();
        Check.Require(sorted.SequenceEqual(wanted), "NestedRoots: " + string.Join(", ", sorted));
    }

    private static void Excluded(PathScope scope, string? localPath, string? dropboxPath, bool isFolder,
        bool expected, string what) =>
        Check.Require(scope.Excludes(localPath, dropboxPath, isFolder) == expected, what);

    private static void Covers(PathScope scope, bool expected, string what, params Entry[] retained) =>
        Check.Require(scope.CoversIgnoredContents(retained) == expected, what);

    private static Entry EntryOf(string path, bool isFolder) =>
        new(path, isFolder, 0, DateTimeOffset.UnixEpoch, "id", "revision", "", null);

    private static void RequireProtectionEquivalence(PairConfig pair, int exclusionCount)
    {
        var retained = new Exclusion(Side.Local, "Retained", true);
        var exclusions = Enumerable.Range(0, exclusionCount)
            .Select(index => new Exclusion(index % 2 == 0 ? Side.Local : Side.Dropbox, "Ex" + index, true))
            .Append(retained).ToArray();
        var many = pair with { IgnoreRules = ["FileExtension:.tmp"], Exclusions = exclusions };
        Check.Require(many.Exclusions.Count == exclusionCount + 1, "the equivalence fixture holds its stated exclusions");
        var nested = many with
        {
            PairId = "equivalenceNested", Name = "equivalenceNested",
            LocalRoot = Path.Combine(many.LocalRoot, "equivalenceNested"),
            DropboxRoot = PathNames.Child(many.DropboxRoot, "equivalenceNested")
        };
        PairConfig[] all = [many, nested];
        var expected = new PathScope(many with { IgnoreRules = [] }, all).Without(retained);
        var actual = new PathScope(many, all, "FileName:desktop.ini").ProtectionWithout(retained);
        foreach (var (localPath, dropboxPath, isFolder) in new (string?, string?, bool)[]
        {
            ("Retained", "Retained", false), ("Retained/child", null, false), ("Retained", null, true),
            ("Ex0", null, false), ("Ex0", null, true), (null, "Ex1", false), (null, "Ex1", true),
            ("Ex2/child", null, false),
            ("equivalenceNested", "equivalenceNested", true), ("equivalenceNested/x", null, true),
            (null, "equivalenceNested/x", false),
            ("desktop.ini", null, false), ("desktop.ini", null, true),
            ("a.tmp", null, false), ("ordinary.txt", null, false), ("Ordinary", null, true)
        })
            Check.Require(actual.Excludes(localPath, dropboxPath, isFolder) ==
                expected.Excludes(localPath, dropboxPath, isFolder),
                $"ProtectionWithout equivalence at {exclusionCount} exclusions: {localPath ?? dropboxPath} ({isFolder})");
    }
}
