namespace Massdex.SyncShark.V2;

/// <summary>Rebuildable-folder checks (docs/design/rebuildables.md): the detector's pure per-category rules, the
/// selection reduction and the rebuildables entry in the first-sync choices document. Every case is a pure function
/// of items plus a text reader.</summary>
internal static class RebuildableChecks
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public static Task RunAsync()
    {
        Detect_JsPackages();
        Detect_NestedNodeModulesCovered();
        Detect_PythonCache();
        Detect_PythonVenv();
        Detect_PythonVenvMarkerMustBeAFile();
        Detect_DotnetBuild();
        Detect_CacheTagged();
        Detect_CacheTaggedTagMustBeAFile();
        Detect_JsBuild();
        Detect_Negatives();
        Chosen_CollapsesNestedAcrossCategories();
        Chosen_DropsPathWhoseCloudIsNotAFolder();
        Retained_RevalidatesMarkers();
        DeletionProbe_DropsRebuildableAndExcludedPaths();
        Choices_RebuildablesParsedAndRefused();
        Choices_RebuildablesNeedsOneLocalAndOneDropboxSide();
        Console.WriteLine("v2 rebuildable checks passed.");
        return Task.CompletedTask;
    }

    private static void Detect_JsPackages()
    {
        var items = new[] { D("app"), D("app/node_modules"), F("app/package.json"), F("app/node_modules/p/x.js", 10) };
        var found = RebuildableDetector.Detect(items, Texts());
        Require(found.Count == 1 && found[0] == new RebuildableFolder("js-packages", "app/node_modules", 1, 10),
            "a node_modules beside package.json is detected with its file count and bytes: " + Show(found));
    }

    private static void Detect_NestedNodeModulesCovered()
    {
        var items = new[]
        {
            D("app"), D("app/node_modules"), D("app/node_modules/p"), D("app/node_modules/p/node_modules"),
            F("app/package.json"), F("app/node_modules/p/package.json"), F("app/node_modules/p/node_modules/q/y.js", 4)
        };
        var found = RebuildableDetector.Detect(items, Texts());
        Require(found.Count == 1 && found[0].Path == "app/node_modules",
            "a nested node_modules is covered by the outer one, not listed again: " + Show(found));
    }

    private static void Detect_PythonCache()
    {
        var hit = new[] { D("pkg"), D("pkg/__pycache__"), F("pkg/__pycache__/m.cpython-312.pyc", 20) };
        var found = RebuildableDetector.Detect(hit, Texts());
        Require(found.Count == 1 && found[0] == new RebuildableFolder("python-cache", "pkg/__pycache__", 1, 20),
            "a __pycache__ holding a .pyc is detected: " + Show(found));
        var miss = new[] { D("pkg/__pycache__"), F("pkg/__pycache__/readme.txt", 1) };
        Require(RebuildableDetector.Detect(miss, Texts()).Count == 0, "a __pycache__ without a .pyc is not detected");
    }

    private static void Detect_PythonVenv()
    {
        var dotVenv = new[] { D(".venv"), F(".venv/pyvenv.cfg", 3) };
        var venv = new[] { D("venv"), F("venv/pyvenv.cfg", 3) };
        Require(RebuildableDetector.Detect(dotVenv, Texts()) is [{ Category: "python-venv", Path: ".venv" }],
            "a .venv holding pyvenv.cfg is detected");
        Require(RebuildableDetector.Detect(venv, Texts()) is [{ Category: "python-venv", Path: "venv" }],
            "a venv holding pyvenv.cfg is detected");
        var miss = new[] { D(".venv"), F(".venv/readme.txt", 1) };
        Require(RebuildableDetector.Detect(miss, Texts()).Count == 0, "a .venv without pyvenv.cfg is not detected");
    }

    private static void Detect_PythonVenvMarkerMustBeAFile()
    {
        var items = new[] { D(".venv"), D(".venv/pyvenv.cfg") };
        Require(RebuildableDetector.Detect(items, Texts()).Count == 0,
            "a pyvenv.cfg that is not a file does not qualify a virtual environment");
    }

    private static void Detect_DotnetBuild()
    {
        var items = new[]
        {
            D("src"), D("src/bin"), D("src/obj"), F("src/app.vcxproj", 5), F("src/bin/a.dll", 100)
        };
        var found = RebuildableDetector.Detect(items, Texts());
        Require(found.IsAll("dotnet-build", "src/bin", "src/obj"),
            "bin and obj beside a file whose extension ends with proj are both detected: " + Show(found));
        Require(found.Single(folder => folder.Path == "src/bin") is { Files: 1, Bytes: 100 },
            "the bin folder counts the file under it");
        var miss = new[] { D("src/bin"), F("src/bin/a.dll", 1) };
        Require(RebuildableDetector.Detect(miss, Texts()).Count == 0, "a bin without a project file is not detected");
        var notAFile = new[] { D("src/bin"), D("src/app.csproj") };
        Require(RebuildableDetector.Detect(notAFile, Texts()).Count == 0,
            "a project marker that is not a file does not qualify bin");
    }

    private static void Detect_CacheTagged()
    {
        var items = new[] { D("cache"), F("cache/CACHEDIR.TAG", 43) };
        var hit = RebuildableDetector.Detect(items,
            Texts(("cache/CACHEDIR.TAG", "Signature: 8a477f597d28d172789f06886806bc55\nother line\n")));
        Require(hit is [{ Category: "cache-tagged", Path: "cache" }], "a CACHEDIR.TAG with the standard signature is detected");
        var wrong = RebuildableDetector.Detect(items, Texts(("cache/CACHEDIR.TAG", "just some text\n")));
        Require(wrong.Count == 0, "a CACHEDIR.TAG without the signature is not detected");
    }

    private static void Detect_CacheTaggedTagMustBeAFile()
    {
        var items = new[] { D("cache"), D("cache/CACHEDIR.TAG") };
        var read = false;
        RebuildableDetector.Detect(items, _ => { read = true; return "Signature: 8a477f597d28d172789f06886806bc55"; });
        Require(!read, "a CACHEDIR.TAG that is not a file is never read");
    }

    private static void Detect_JsBuild()
    {
        var items = new[] { D("web"), D("web/.next"), F("web/package.json", 2), F("web/next.config.js", 30), F("web/.next/b.js", 7) };
        var found = RebuildableDetector.Detect(items, Texts());
        Require(found is [{ Category: "js-build", Path: "web/.next", Files: 1, Bytes: 7 }],
            "a .next beside package.json and a next.config.* file is detected: " + Show(found));
        var noConfig = new[] { D("web/.next"), F("web/package.json", 2), F("web/.next/b.js", 7) };
        Require(RebuildableDetector.Detect(noConfig, Texts()).Count == 0,
            "a .next without a next.config.* file is not detected");
        var noPackageJson = new[] { D("web/.next"), F("web/next.config.js", 30), F("web/.next/b.js", 7) };
        Require(RebuildableDetector.Detect(noPackageJson, Texts()).Count == 0,
            "a .next without package.json is not detected");
        // dist and build are ordinary folder names and are never detected, whatever sits beside them.
        var others = new[]
        {
            D("web/dist"), D("web/build"), D("web"), F("web/package.json", 2), F("web/next.config.js", 30),
            F("web/dist/a.js", 1), F("web/build/b.js", 1)
        };
        Require(RebuildableDetector.Detect(others, Texts()).Count == 0,
            "dist and build are no longer detected at all");
    }

    private static void Detect_Negatives()
    {
        var items = new[] { D(".git"), F(".git/config", 5), D("ordinary"), F("ordinary/x.txt", 1) };
        Require(RebuildableDetector.Detect(items, Texts()).Count == 0,
            "a .git folder and an ordinary folder are never detected");
    }

    // The chosen set is reduced to its outermost roots across every category, not only node_modules.
    private static void Chosen_CollapsesNestedAcrossCategories()
    {
        var detected = new[]
        {
            new RebuildableFolder("js-packages", "app/node_modules", 1, 1),
            new RebuildableFolder("python-venv", "app/node_modules/.venv", 1, 1)
        };
        var chosen = RebuildableDetector.Chosen(detected, [], ["js-packages", "python-venv"]);
        Require(chosen is [{ Path: "app/node_modules" }],
            "a nested selection is covered by its outermost root: " + Show(chosen));
    }

    // A path whose cloud counterpart is not a folder is left to ordinary planning, never selected.
    private static void Chosen_DropsPathWhoseCloudIsNotAFolder()
    {
        var detected = new[] { new RebuildableFolder("js-packages", "app/node_modules", 1, 1) };
        var asFile = new[] { F("app/node_modules", 4) };
        Require(RebuildableDetector.Chosen(detected, asFile, ["js-packages"]).Count == 0,
            "a path whose cloud counterpart is a file is not selected");
        var asFolder = new[] { D("app/node_modules") };
        Require(RebuildableDetector.Chosen(detected, asFolder, ["js-packages"]) is [{ Path: "app/node_modules" }],
            "a path whose cloud counterpart is a folder is selected");
        Require(RebuildableDetector.Chosen(detected, [], ["js-packages"]) is [{ Path: "app/node_modules" }],
            "a path the cloud does not hold at all is selected");
    }

    // Deletion-evidence probes are filtered before any provider read: a path at or under a chosen rebuildable
    // folder, or at or under a pair exclusion, is never requested (it is never uploaded or deleted).
    private static void DeletionProbe_DropsRebuildableAndExcludedPaths()
    {
        var pair = Pair() with { Excluded = ["cache"] };
        var requested = new[]
        {
            "a.txt", "cache/x.txt", "app/node_modules/p/x.js", "app/node_modules", "app/other/y.js"
        };
        Require(SyncRun.DeletionProbePaths(pair, requested, ["app/node_modules"])
                .SequenceEqual(new[] { "a.txt", "app/other/y.js" }),
            "deletion probes drop excluded and rebuildable paths, keeping everything else");
        Require(SyncRun.DeletionProbePaths(pair, requested, []).SequenceEqual(
                new[] { "a.txt", "app/node_modules/p/x.js", "app/node_modules", "app/other/y.js" }),
            "with no rebuildable selection only the exclusion is dropped");
        Require(SyncRun.DeletionProbePaths(pair, new[] { "cache", "cache2/x.txt" }, [])
                .SequenceEqual(new[] { "cache2/x.txt" }),
            "an exclusion drops its own path and its descendants, never a sibling with the same prefix");
    }

    // At apply a frozen path is revalidated with the same marker rules detection uses: a path that no longer
    // qualifies (markers gone, category changed, local folder gone, cloud counterpart not a folder) is dropped.
    private static void Retained_RevalidatesMarkers()
    {
        var frozen = new[] { new RebuildableFolder("js-packages", "app/node_modules", 0, 0) };
        var qualifying = new[] { D("app"), D("app/node_modules"), F("app/package.json") };
        var withoutMarker = new[] { D("app"), D("app/node_modules") };
        Require(RebuildableDetector.Retained(frozen, qualifying, "C:\\a", []) is [{ Path: "app/node_modules" }],
            "a frozen path whose markers still hold is kept");
        Require(RebuildableDetector.Retained(frozen, withoutMarker, "C:\\a", []).Count == 0,
            "a frozen path whose marker is gone is dropped");
        Require(RebuildableDetector.Retained([new RebuildableFolder("python-venv", "app/node_modules", 0, 0)],
            qualifying, "C:\\a", []).Count == 0, "a frozen path whose category no longer matches is dropped");
        Require(RebuildableDetector.Retained(frozen, [D("app")], "C:\\a", []).Count == 0,
            "a frozen path whose local folder is gone is dropped");
        Require(RebuildableDetector.Retained(frozen, qualifying, "C:\\a", [F("app/node_modules", 3)]).Count == 0,
            "a frozen path whose cloud counterpart is a file is dropped");
    }

    private static void Choices_RebuildablesParsedAndRefused()
    {
        var pair = Pair();
        var parsed = FirstSyncChoices.Parse("""{"rebuildables":["js-packages","cache-tagged"]}""", pair);
        Require(parsed.Rebuildables.SequenceEqual([new RebuildableChoice("js-packages"), new RebuildableChoice("cache-tagged")]),
            "the rebuildables list parses in order");
        var smaller = FirstSyncChoices.Parse("""{"rebuildables":["js-packages"]}""", pair);
        Require(FirstSyncChoices.Digest(parsed, []) != FirstSyncChoices.Digest(smaller, []),
            "the digest binds the rebuildables list");
        var scoped = FirstSyncChoices.Parse("""{"rebuildables":[{"id":"js-packages","prefix":"a/"}]}""", pair);
        var whole = FirstSyncChoices.Parse("""{"rebuildables":[{"id":"js-packages","prefix":""}]}""", pair);
        Require(FirstSyncChoices.Digest(scoped, []) != FirstSyncChoices.Digest(smaller, []),
            "scope changes the digest even with no frozen folders");
        Require(FirstSyncChoices.Digest(whole, []) == FirstSyncChoices.Digest(smaller, []),
            "empty prefix has the same digest as the legacy string");
        var candidates = new[] { new RebuildableFolder("js-packages", "a/node_modules", 0, 0),
            new RebuildableFolder("js-packages", "ab/node_modules", 0, 0),
            new RebuildableFolder("js-packages", "node_modules", 0, 0) };
        Require(RebuildableDetector.ChosenScoped(candidates, [], scoped.Rebuildables)
                .Select(folder => folder.Path).SequenceEqual(["a/node_modules"]),
            "scoped choice keeps only folders below its prefix, not siblings or ancestors");
        Require(new RebuildableChoice("js-packages", "A/NODE_MODULES").Matches(candidates[0]),
            "scope includes the exact folder and ignores case");
        Require(RebuildableDetector.ChosenScoped(candidates, [], smaller.Rebuildables).Count == 3,
            "legacy string still selects the entire pair");
        // The digest also covers the canonical frozen (category, path) tuples, so a changed set needs a new prepare.
        var first = FirstSyncChoices.Digest(parsed, [new RebuildableFolder("js-packages", "app/node_modules", 0, 0)]);
        var second = FirstSyncChoices.Digest(parsed, [new RebuildableFolder("js-packages", "app/other", 0, 0)]);
        var recategorised = FirstSyncChoices.Digest(parsed, [new RebuildableFolder("python-venv", "app/node_modules", 0, 0)]);
        Require(first != second && first != recategorised, "the digest binds the frozen (category, path) set");
        Require(first == FirstSyncChoices.Digest(parsed, [new RebuildableFolder("js-packages", "app/node_modules", 7, 9)]),
            "the digest covers only the category and path, not the counts");
        Require(FirstSyncChoices.Parse("""{"x.txt":"upload"}""", pair) is { Rebuildables.Count: 0 },
            "a legacy path map carries no rebuildables");
        string[] refused =
        [
            """{"rebuildables":["nope"]}""",
            """{"rebuildables":[{"id":"js-packages","prefix":"/a"}]}""",
            """{"rebuildables":[{"id":"js-packages","prefix":"a//b"}]}""",
            """{"rebuildables":[{"id":"js-packages","prefix":"a/../b"}]}""",
            """{"rebuildables":[{"id":"js-packages","prefix":"a/./b"}]}""",
            """{"rebuildables":[{"id":"js-packages","prefix":"a\\b"}]}""",
            """{"rebuildables":[{"id":"js-packages"}]}""",
            """{"rebuildables":[{"id":"js-packages","prefix":null}]}""",
            """{"rebuildables":[{"id":"js-packages","prefix":"a","extra":true}]}""",
            """{"rebuildables":[{"id":"js-packages","prefix":"a","prefix":"b"}]}""",
            """{"rebuildables":["js-packages",{"id":"js-packages","prefix":""}]}""",
            """{"rebuildables":"js-packages"}""",
            """{"rebuildables":[1]}""",
            """{"rebuildables":["js-packages","JS-PACKAGES"]}""",
            """{"rebuildables":["js-packages"],"bogus":1}"""
        ];
        foreach (var json in refused)
        {
            var refusedIt = false;
            try { FirstSyncChoices.Parse(json, pair); }
            catch (SyncCliUsageException) { refusedIt = true; }
            Require(refusedIt, "an invalid rebuildables document is refused: " + json);
        }
    }

    // The choice needs a cloud side to remove the copy from and a local side to keep; any other pair is refused.
    private static void Choices_RebuildablesNeedsOneLocalAndOneDropboxSide()
    {
        var bothLocal = new PairDef("p", "p",
            new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
            new RootDef("rb", SyncProviders.Local, null, "C:\\b", "id:rb"), [], [], true);
        var google = new PairDef("p", "p",
            new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
            new RootDef("rb", SyncProviders.GoogleDrive, "acc", "/b", "id:rb"), [], [], true);
        foreach (var pair in new[] { bothLocal, google })
        {
            Require(RebuildableSelection.Sides(pair) is null, "this pair has no single local/cloud side pair");
            var refused = false;
            try { FirstSyncChoices.Parse("""{"rebuildables":["js-packages"]}""", pair); }
            catch (SyncCliUsageException error)
            {
                refused = error.Message.Contains("exactly one local side and one Dropbox side", StringComparison.Ordinal);
            }
            Require(refused, "the rebuildables choice is refused for a pair with no single local/cloud side pair");
            Require(FirstSyncChoices.Parse("""{"rebuildables":[]}""", pair).Rebuildables.Count == 0,
                "an empty rebuildables list is not refused for such a pair");
        }
    }

    // ---- fixtures ---------------------------------------------------------------

    private static ItemState F(string path, long size = 0) =>
        new(path, ItemKind.File, "id:" + path, null, "h", size, T0, ItemFlags.None);

    private static ItemState D(string path) =>
        new(path, ItemKind.Folder, "id:" + path, null, null, 0, T0, ItemFlags.None);

    private static Func<string, string?> Texts(params (string Path, string Text)[] files)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in files) map[path] = text;
        return path => map.GetValueOrDefault(path);
    }

    private static PairDef Pair() => new("p1", "p1",
        new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
        new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"), [], [], true);

    private static bool IsAll(this IReadOnlyList<RebuildableFolder> found, string category, params string[] paths) =>
        found.Count == paths.Length && found.All(folder => folder.Category == category && paths.Contains(folder.Path));

    private static string Show(IReadOnlyList<RebuildableFolder> found) =>
        found.Count == 0 ? "(none)" : string.Join(", ", found.Select(folder => folder.Category + " " + folder.Path));

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
