using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks for DropboxListing (`--engine-checks dropbox`), written by DropboxListing's writer.</summary>
public static class DropboxListingChecks
{
    // The adopted schedule without jitter, so a fallback gap is exact; its real numbers are its owner's checks.
    private static readonly FailureRetryOwner.BackoffSettings Adopted = new(1, 1.6, 0, 120);
    private static readonly TimeSpan ClockStep = TimeSpan.FromMilliseconds(100);

    // One ordinary request-attempt context per listing read, as the pair session composes it.
    private static ListingReadContext BeginListing() => new(
        () => new RequestAttempt(new object(), AttemptOwner.Request),
        (_, _) => Task.FromResult(false), static () => ValueTask.CompletedTask);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        AllocationProbe();
        await OrderedPagesAsync();
        await RetainedCursorAsync();
        await CursorFallbackAsync();
        await RequestedRootAsync();
        await KindChangesAsync();
        await ScopeAsync();
        await PrefixIgnoresAsync();
        await StaleAncestorsAsync();
        await RepeatedListingRenameAsync();
        await MetadataAsync();
        await ReservedNamesAsync();
        await InvalidNeighboursAsync();
        await ShippedDefaultsReservedNameAsync();
        await ReservedDescendantsAsync();
        await MalformedMetadataAsync();
        await PageShapesAsync();
        await TransportAndProgrammingErrorsAsync();
        await RepeatedLatePageFailureAsync();
        await PageWaitsAsync();
        await LocalSpoolFailuresAsync();
        await SealedPageAsync();
        await SparseReadsAsync();
        await FailuresAsync();
        await CancellationAsync();
    }

    private static void AllocationProbe()
    {
        const int calls = 1000;
        const string path = "Docs/A/Child.txt";
        const string under = "Docs";
        const int parentEnd = 6;
        var listed = new Listed(path, null);
        Action<int> baselineSame = index => Sink(string.Equals(listed.Path, under, StringComparison.OrdinalIgnoreCase));
        Action<int> candidateSame = index => Sink(PathNames.Same(listed.Path, under));
        Action<int> baselineSeparator = index => Sink(listed.Path.LastIndexOf(PathNames.PathSeparator, StringComparison.Ordinal));
        Action<int> candidateSeparator = index => Sink(PathNames.LastSeparator(listed.Path));
        Action<int> baselineName = index =>
            Sink(listed.Path[(listed.Path.LastIndexOf(PathNames.PathSeparator, StringComparison.Ordinal) + 1)..]);
        Action<int> candidateName = index => Sink(PathNames.Name(listed.Path));
        Action<int> baselineTransferable = index =>
            Sink(PathRules.IsWindowsTransferPath(PathNames.LocalSpelling(listed.Path[..parentEnd])));
        Action<int> candidateTransferable = index => Sink(PathNames.IsTransferable(listed.Path[..parentEnd]));

        var same = (AllocProbe.Measure(baselineSame, calls), AllocProbe.Measure(candidateSame, calls));
        var separator = (AllocProbe.Measure(baselineSeparator, calls), AllocProbe.Measure(candidateSeparator, calls));
        var name = (AllocProbe.Measure(baselineName, calls), AllocProbe.Measure(candidateName, calls));
        var transferable = (AllocProbe.Measure(baselineTransferable, calls), AllocProbe.Measure(candidateTransferable, calls));
        Check.Require(same.Item1 == same.Item2 && separator.Item1 == separator.Item2 &&
            name.Item1 == name.Item2 && transferable.Item1 == transferable.Item2,
            "Same, LastSeparator, Name and IsTransferable allocate the same bytes per call as the listing's inline expressions.");
    }

    // Typed, non-inlined sinks keep each probed result live without boxing.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(bool value) => _boolean = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(int value) => _number = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(string? value) => _string = value;

    private static bool _boolean;
    private static int _number;
    private static string? _string;

    private static async Task OrderedPagesAsync()
    {
        foreach (var root in new[] { "/", "/Parent" })
        {
            var pair = Pair(root);
            using var fixture = new Fixture(pair,
                _ => Page(true, "cursor-one", Folder(root, ""), Folder(root, "Folder"),
                    File(root, "Folder/Child"), File(root, "Replace", "old")),
                _ => Page(false, "cursor-two", Deleted(root, "Folder"), Deleted(root, "Replace"),
                    File(root, "Replace", "new"), File(root, "Cloud", downloadable: false)));
            var entries = await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
            Check.Require(entries.Select(item => item.Path).SequenceEqual(new[]
                { "Folder", "Folder/Child", "Replace", "Folder", "Replace", "Replace", "Cloud" }),
                "Listing preserves provider order, replacements and descendant-removing tombstones.");
            Check.Require(entries[0].Entry?.IsFolder == true && entries[1].Entry?.IsFolder == false,
                "Folders and downloadable files are yielded.");
            Check.Require(entries[3].Entry is null && entries[4].Entry is null,
                "Deleted metadata is yielded rather than discarded.");
            Check.Require(entries[2].Entry?.Revision == "old" && entries[5].Entry?.Revision == "new",
                "A replacement at the same path is not deduplicated.");
            Check.Require(!string.IsNullOrEmpty(entries[^1].Entry?.Skip), "Non-downloadable files retain Skip.");
            Check.Require(fixture.Handler.Requests.Count == 2, "Every continuation page is requested.");
            var first = fixture.Handler.Requests[0];
            using var args = JsonDocument.Parse(first.Body);
            var value = args.RootElement;
            Check.Require(first.Path.EndsWith(DropboxProtocol.ListFolder, StringComparison.Ordinal) &&
                value.GetProperty("path").GetString() == (root == "/" ? "" : root) &&
                value.GetProperty("recursive").GetBoolean() && !value.GetProperty("include_deleted").GetBoolean() &&
                value.GetProperty("include_mounted_folders").GetBoolean() &&
                value.GetProperty("include_non_downloadable_files").GetBoolean() &&
                value.GetProperty("limit").GetInt32() == DropboxProtocol.ListPageMaximum,
                "Initial listing uses the designed flags and root argument.");
            var next = fixture.Handler.Requests[1];
            using var continuation = JsonDocument.Parse(next.Body);
            Check.Require(next.Path.EndsWith(DropboxProtocol.ListFolderContinue, StringComparison.Ordinal) &&
                continuation.RootElement.EnumerateObject().Count() == 1 &&
                continuation.RootElement.GetProperty("cursor").GetString() == "cursor-one",
                "Continuation sends only the preceding cursor.");
        }
        using var empty = new Fixture(Pair(), _ => Page(true, "empty"), _ => Page(false, "end"));
        Check.Require((await CollectAsync(empty.Listing, "", new ListOptions(true, false))).Count == 0 &&
            empty.Handler.Requests.Count == 2, "An empty page with has_more still continues.");

        // Only discovery Repairs ask for deleted entries; the listing alone translates the option into the request.
        using var discovery = new Fixture(Pair(), _ => Page(false, "end",
            Folder("/Parent", "Kept"), Deleted("/Parent", "Gone")));
        var discovered = await CollectAsync(discovery.Listing, "", new ListOptions(true, false, IncludeDeleted: true));
        Check.Require(discovered.Select(item => item.Path).SequenceEqual(new[] { "Kept", "Gone" }) &&
            discovered[1].Entry is null,
            "With discovery enabled a deleted entry stays a Listed tombstone, never a live entry.");
        using var discoveryRequest = JsonDocument.Parse(discovery.Handler.Requests[0].Body);
        Check.Require(discoveryRequest.RootElement.GetProperty("include_deleted").GetBoolean(),
            "The explicit IncludeDeleted option alone sets include_deleted=true on the root list_folder request.");
    }

    private static async Task RetainedCursorAsync()
    {
        using var fixture = new Fixture(Pair(),
            _ => Page(true, "next", Deleted("/Parent", "Gone")),
            _ => Page(false, "terminal", File("/Parent", "Folder/Child")));
        string? terminal = null;
        var entries = await CollectAsync(fixture.Listing, "", new ListOptions(true, false, true)
        {
            Cursor = "saved", CursorAcquired = value => terminal = value
        });
        using var request = JsonDocument.Parse(fixture.Handler.Requests[0].Body);
        Check.Require(fixture.Handler.Requests[0].Path.EndsWith(DropboxProtocol.ListFolderContinue, StringComparison.Ordinal) &&
            request.RootElement.GetProperty("cursor").GetString() == "saved" &&
            entries.Select(item => item.Path).SequenceEqual(new[] { "Gone", "Folder/Child" }) && terminal == "terminal",
            "Inventory continuation uses its own saved cursor and publishes only after deferred spelling flush.");
        using var empty = new Fixture(Pair(), _ => Page(false, "empty-terminal"));
        terminal = null;
        await CollectAsync(empty.Listing, "", new ListOptions(true, false, true)
        {
            Cursor = "saved", CursorAcquired = value => terminal = value
        });
        Check.Require(terminal == "empty-terminal", "An empty terminal continuation advances the history boundary.");
    }

    private static async Task CursorFallbackAsync()
    {
        // Only a listing started from a saved cursor turns an unusable continuation into reset-and-relist.
        using var fresh = new Fixture(Pair(),
            _ => Page(true, "cursor-one", File("/Parent", "First")),
            _ => Reset());
        await ExpectAsync(fresh, new ListOptions(true, false), typeof(InventoryIncompleteException),
            "A reset on a fresh listing's second page stays an incomplete inventory.");

        using var resumed = new Fixture(Pair(), _ => Reset());
        await ExpectAsync(resumed, new ListOptions(true, false) { Cursor = "saved" }, typeof(ListingCursorResetException),
            "A reset on a saved cursor falls back to the fresh listing.");

        using var malformed = new Fixture(Pair(), _ => Error(HttpStatusCode.BadRequest, "invalid_cursor/.."));
        await ExpectAsync(malformed, new ListOptions(true, false) { Cursor = "saved" }, typeof(ListingCursorResetException),
            "A permanent non-reset refusal of a saved cursor reuses the same fallback.");

        using var freshMalformed = new Fixture(Pair(), _ => Error(HttpStatusCode.BadRequest, "invalid_cursor/.."));
        await ExpectAsync(freshMalformed, new ListOptions(true, false), typeof(InventoryIncompleteException),
            "A permanent refusal on a fresh listing stays an incomplete inventory.");

        static async Task ExpectAsync(Fixture fixture, ListOptions options, Type expected, string rule)
        {
            Type? actual = null;
            try { await CollectAsync(fixture.Listing, "", options); }
            catch (Exception error) { actual = error.GetType(); }
            Check.Require(actual == expected, rule);
        }
    }

    private static async Task RequestedRootAsync()
    {
        // Static requested-root fixture across page and order variants: the root is omitted, every descendant is
        // emitted exactly once, and a missing root entry never becomes inferred deletion.
        var variants = new[]
        {
            new Func<CancellationToken, HttpResponseMessage>[]
            {
                _ => Page(false, "end", Folder("/Parent", ""), Folder("/Parent", "Folder"),
                    File("/Parent", "Folder/Child"))
            },
            new Func<CancellationToken, HttpResponseMessage>[]
            {
                _ => Page(true, "next", File("/Parent", "Folder/Child")),
                _ => Page(false, "end", Folder("/Parent", "Folder"), Folder("/Parent", ""))
            },
            new Func<CancellationToken, HttpResponseMessage>[]
            {
                _ => Page(false, "end", Folder("/Parent", "Folder"), File("/Parent", "Folder/Child"))
            }
        };
        foreach (var replies in variants)
        {
            using var fixture = new Fixture(Pair(), replies);
            var entries = await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
            Check.Require(entries.Select(item => item.Path).SequenceEqual(new[] { "Folder", "Folder/Child" }),
                "The requested root is omitted and every descendant emitted once across page and order variants.");
        }
        using (var fixture = new Fixture(Pair(), _ => Page(false, "end",
            Folder("/Parent", "Doc"), File("/Parent", "Doc/Child"))))
        {
            Check.Require((await CollectAsync(fixture.Listing, "Doc", new ListOptions(true, false)))
                .Select(item => item.Path).SequenceEqual(new[] { "Doc/Child" }),
                "The exact requested root is skipped, never a prefix that also removes its descendants.");
        }
        using (var fixture = new Fixture(Pair(), _ => Page(false, "end",
            Folder("/Parent", "Docs"), File("/Parent", "DOCS/Child"))))
        {
            Check.Require((await CollectAsync(fixture.Listing, "Docs", new ListOptions(true, false)))
                .Select(item => item.Path).SequenceEqual(new[] { "Docs/Child" }),
                "The root skip applies to the corrected spelling, after parsing and spelling correction.");
        }
        using (var fixture = new Fixture(Pair(), _ => Page(false, "end",
            File("/Parent", "Item", "one"), File("/Parent", "Item", "two"))))
        {
            Check.Require((await CollectAsync(fixture.Listing, "", new ListOptions(true, false)))
                .Select(item => item.Entry?.Revision).SequenceEqual(new[] { "one", "two" }),
                "Repeated ordered events remain legal; no deduplication set removes them.");
        }
    }

    private static async Task KindChangesAsync()
    {
        using var fixture = new Fixture(Pair(), _ => Page(false, "end",
            Folder("/Parent", "Node"), File("/Parent", "Node"),
            File("/Parent", "Other"), Folder("/Parent", "Other"),
            Deleted("/Parent", "Gone"), File("/Parent", "Gone", "new")));
        var entries = await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
        Check.Require(entries.Select(item => item.Path + (item.Entry is null ? ":deleted" :
                item.Entry.IsFolder ? ":folder" : ":file")).SequenceEqual(new[]
                { "Node:folder", "Node:file", "Other:file", "Other:folder", "Gone:deleted", "Gone:file" }),
            "A kind change or delete-then-recreate at one path is yielded in provider order, never deduplicated.");
    }

    private static async Task ScopeAsync()
    {
        var pair = Pair() with
        {
            IgnoreRules = new[] { "FileExtension:tmp;FolderName:Ignored" },
            Exclusions = new[] { new Exclusion(Side.Local, "Excluded", true) }
        };
        var nested = pair with { PairId = "nested", LocalRoot = @"C:\Sibling", DropboxRoot = "/Parent/Nested" };
        var shared = pair with { PairId = "shared", IgnoreRules = Array.Empty<string>(), Exclusions = Array.Empty<Exclusion>() };
        foreach (var include in new[] { false, true })
        {
            using var fixture = new Fixture(pair, new[] { pair, nested, shared }, _ => Page(false, "end",
                Folder(pair.DropboxRoot, ""), Folder(pair.DropboxRoot, "Excluded"),
                File(pair.DropboxRoot, "Excluded/Child"), Deleted(pair.DropboxRoot, "Excluded/Old"),
                Folder(pair.DropboxRoot, "Nested"), File(pair.DropboxRoot, "Nested/Child"), Deleted(pair.DropboxRoot, "Nested/Old"),
                File(pair.DropboxRoot, "scratch.tmp"), Deleted(pair.DropboxRoot, "scratch.tmp"),
                Folder(pair.DropboxRoot, "Ignored"), Deleted(pair.DropboxRoot, "Ignored"),
                File(pair.DropboxRoot, "Ignored/Child"), File(pair.DropboxRoot, "Visible"),
                File(pair.DropboxRoot, "ExcludedElsewhere"), File(pair.DropboxRoot, "NestedElsewhere")));
            var entries = await CollectAsync(fixture.Listing, "", new ListOptions(true, include));
            Check.Require(include ? entries.Count == 14 : entries.Select(item => item.Path).SequenceEqual(
                new[] { "Excluded/Old", "Nested/Old", "scratch.tmp", "Ignored", "Visible", "ExcludedElsewhere", "NestedElsewhere" }),
                "Scope filters live entries but never tombstones, including kind-specific ignores, exclusions and nested roots.");
            Check.Require(entries.Where(item => item.Entry is null).Select(item => item.Path).SequenceEqual(
                new[] { "Excluded/Old", "Nested/Old", "scratch.tmp", "Ignored" }),
                "All tombstones survive with or without the scope bypass.");
        }
        using var child = new Fixture(Pair(), _ => Page(false, "end", Folder("/Parent", "Child"), File("/Parent", "Child/File")));
        var children = await CollectAsync(child.Listing, "child", new ListOptions(false, true));
        Check.Require(children.Count == 1 && children[0].Path == "child/File",
            "The under folder is not yielded, ignoring case; its children take the caller's true spelling.");
        using var request = JsonDocument.Parse(child.Handler.Requests[0].Body);
        Check.Require(request.RootElement.GetProperty("path").GetString() == "/Parent/child" &&
            !request.RootElement.GetProperty("recursive").GetBoolean(), "Subfolder and nonrecursive options reach Dropbox.");
    }

    private static async Task PrefixIgnoresAsync()
    {
        foreach (var (prefix, other) in new[] { ("~$", ".~"), (".~", "~$") })
        {
            var pair = Pair() with { IgnoreRules = new[] { nameof(SyncIgnoreKind.FileNamePrefix) + ":" + prefix } };
            var omitted = new[] { prefix + "Book.docx", prefix, "Docs/" + prefix + "Note.txt" };
            var all = new[]
            {
                "Book.xlsx", omitted[0], omitted[1], prefix + "Folder", prefix + "Folder/ordinary.txt", "Docs", omitted[2],
                "Docs/Book" + prefix + "copy.xlsx", other + "Book.docx", prefix + "Gone.docx", "desktop.ini"
            };
            foreach (var include in new[] { false, true })
            {
                using var fixture = new Fixture(pair, _ => Page(false, "end",
                    File("/Parent", all[0]), File("/Parent", all[1]), File("/Parent", all[2]),
                    Folder("/Parent", all[3]), File("/Parent", all[4]), Folder("/Parent", all[5]), File("/Parent", all[6]),
                    File("/Parent", all[7]), File("/Parent", all[8]), Deleted("/Parent", all[9]), File("/Parent", all[10])));
                var entries = await CollectAsync(fixture.Listing, "", new ListOptions(true, include));
                Check.Require(entries.Select(item => item.Path).SequenceEqual(include ? all : all.Except(omitted)),
                    "A configured prefix omits matching files only: directories, their ordinary children, mid-name matches, " +
                    "the other prefix and provider-refused names stay, and IncludeOutOfScope reveals the omitted files.");
                Check.Require(entries.Single(item => item.Path == all[9]).Entry is null,
                    "A prefixed tombstone stays unfiltered: its kind is unknown, so the prefix implies no file.");
            }
        }

        // BlockerNames.DropboxRefuses is a provider-refusal predicate, not an ignore rule the scope may inherit.
        using var unconfigured = new Fixture(Pair(), _ => Page(false, "end", File("/Parent", "~$Book.docx"),
            File("/Parent", ".~lock.Book.ods#"), File("/Parent", "desktop.ini"), File("/Parent", "thumbs.db")));
        Check.Require((await CollectAsync(unconfigured.Listing, "", new ListOptions(true, false))).Count == 4,
            "With no configured rule the listing filters no temporary or provider-refused name.");

        var shippedPair = Pair();
        using var shipped = new Fixture(shippedPair, new[] { shippedPair }, ShippedDefaultIgnoreRules(),
            _ => Page(false, "end", File("/Parent", "~$Book.docx"), File("/Parent", ".~lock.Book.ods#"),
                Folder("/Parent", "~$Folder"), File("/Parent", "Book.xlsx")));
        Check.Require((await CollectAsync(shipped.Listing, "", new ListOptions(true, false)))
            .Select(item => item.Path).SequenceEqual(new[] { "~$Folder", "Book.xlsx" }),
            "The production default scope omits files of both shipped prefixes and keeps a prefixed folder.");
    }

    private static async Task StaleAncestorsAsync()
    {
        using (var fixture = new Fixture(Pair(), _ => Page(false, "end",
            Folder("/Parent", "Docs/A"), File("/Parent", "docs/A/b.txt"))))
        {
            var entries = await CollectAsync(fixture.Listing, "Docs", new ListOptions(true, false));
            Check.Require(entries.Select(item => item.Path).SequenceEqual(new[] { "Docs/A", "Docs/A/b.txt" }) &&
                entries[1].Entry?.Path == "Docs/A/b.txt",
                "A stale ancestor in a displayed path is corrected from the listed folder's own spelling.");
        }
        using (var fixture = new Fixture(Pair(),
            _ => Page(true, "next", File("/Parent", "docs/A/b.txt")),
            _ => Page(false, "end", Folder("/Parent", "Docs"), Folder("/Parent", "Docs/A"))))
        {
            var entries = await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
            Check.Require(entries.Select(item => item.Path).SequenceEqual(new[] { "Docs", "Docs/A", "Docs/A/b.txt" }),
                "A child listed before its parent waits and is yielded after the parent, with the corrected path.");
        }
        using (var fixture = new Fixture(Pair(), _ => Page(false, "end",
            Folder("/Parent", "Docs/a"), File("/Parent", "DOCS/a/c.txt"))))
        {
            Check.Require((await CollectAsync(fixture.Listing, "Docs", new ListOptions(true, false)))
                .Single(item => item.Path == "Docs/a/c.txt").Entry?.Path == "Docs/a/c.txt",
                "A case-only folder rename corrects the children's stale ancestor.");
        }
        using (var fixture = new Fixture(Pair(), _ => Json(File("/Parent", "docs/a/b.txt"))))
        {
            var entry = await fixture.Listing.ReadOneAsync("Docs/A/b.txt", CancellationToken.None);
            Check.Require(entry?.Path == "Docs/A/b.txt",
                "ReadOne uses the requested true parent and the metadata's own name segment.");
        }
        var scoped = Pair() with { Exclusions = new[] { new Exclusion(Side.Dropbox, "Docs/a/Excluded", true) } };
        using (var fixture = new Fixture(scoped, _ => Page(false, "end", Folder("/Parent", "Docs/a"),
            File("/Parent", "DOCS/a/Excluded"), File("/Parent", "DOCS/a/CON"))))
        {
            var entries = await CollectAsync(fixture.Listing, "Docs", new ListOptions(true, false));
            Check.Require(entries.Select(item => item.Path).SequenceEqual(new[] { "Docs/a", "Docs/a/CON" }) &&
                entries[1].Entry?.Skip == nameof(TextKey.SkipReservedName),
                "Exclusions and reserved-name skips apply to the corrected paths.");
        }
        using (var fixture = new Fixture(Pair(),
            _ => Page(true, "next", File("/Parent", "docs/a/b/c.txt"), Folder("/Parent", "docs/a/B")),
            _ => Page(false, "end", Folder("/Parent", "Docs/A"), Folder("/Parent", "Docs"))))
        {
            var entries = await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
            Check.Require(entries.Select(item => item.Path).SequenceEqual(
                new[] { "Docs", "Docs/A", "Docs/A/B", "Docs/A/B/c.txt" }),
                "A reverse-ordered chain across pages takes each parent's true spelling, each item after its parent.");
        }
        using (var fixture = new Fixture(Pair(), _ => Page(false, "end",
            Folder("/Parent", "Kept"), File("/Parent", "lost/First"), File("/Parent", "LOST/Second"))))
        {
            string? finalPath = null;
            var entries = new List<Listed>();
            var consumedAtProgress = 0;
            var boundaries = new List<(RunEvent Event, int Consumed)>();
            var options = new ListOptions(true, false)
            {
                Progress = (path, _) => { finalPath = path; consumedAtProgress = entries.Count; },
                Events = item => boundaries.Add((item, entries.Count))
            };
            await foreach (var item in fixture.Listing.ReadAll("", options, BeginListing(), default)) entries.Add(item);
            Check.Require(boundaries.Where(value => !value.Event.InProgress)
                .Select(value => (value.Event.StageKey, value.Event.Ended, value.Event.Count, value.Consumed))
                .SequenceEqual(new (TextKey?, bool, long?, int)[]
                {
                    (TextKey.StageDropboxPages, false, 0, 0),
                    (TextKey.StageDropboxPages, true, 1, 0),
                    (TextKey.StageDropboxFlush, false, 0, 1),
                    (TextKey.StageDropboxFlush, true, 2, 3)
                }), "last-page receipt precedes replay, and retained flush reports its exact entry count after consumption");
            Check.Require(finalPath == "LOST/Second" && consumedAtProgress == entries.Count,
                "final progress follows consumption of the spelling buffer drain");
            Check.Require(entries.Select(item => item.Path).SequenceEqual(new[] { "Kept", "lost/First", "LOST/Second" }),
                "Leftovers whose parent folder was never listed are yielded last, in arrival order, with their displayed path.");
        }
        var reply = new Entry("docs/a/b.txt", false, 0, default, "id", "rev", "", null);
        Check.Require(DropboxListing.Respelled(reply, "Docs/A/b.txt").Path == "Docs/A/b.txt",
            "Respelled takes the requested true parent and the reply's own name.");
        Check.Require(DropboxListing.Respelled(reply with { Path = "item" }, "Item").Path == "item",
            "A root-level requested path contributes no parent, so the reply's own name stands alone.");
    }

    private static async Task RepeatedListingRenameAsync()
    {
        // Two independent listings stand in for consecutive Repairs: each starts a fresh root request, and a
        // casing-only rename seen only through that fresh listing is corrected without inferring deletion.
        var first = new Fixture(Pair(), _ => Page(false, "end",
            Folder("/Parent", "docs", "id:folder"), File("/Parent", "docs/a.txt", id: "id:file-a")));
        using (first)
        {
            var entries = await CollectAsync(first.Listing, "", new ListOptions(true, false));
            Check.Require(entries.Select(item => item.Path).SequenceEqual(new[] { "docs", "docs/a.txt" }) &&
                entries[1].Entry?.Id == "id:file-a",
                "A first listing yields the saved spelling with its stable identity.");
        }
        using var second = new Fixture(Pair(), _ => Page(false, "end",
            Folder("/Parent", "Docs", "id:folder"), File("/Parent", "DOCS/a.txt", id: "id:file-a")));
        var renamed = await CollectAsync(second.Listing, "", new ListOptions(true, false));
        Check.Require(renamed.Select(item => item.Path).SequenceEqual(new[] { "Docs", "Docs/a.txt" }) &&
            renamed[1].Entry?.Id == "id:file-a",
            "A later fresh listing corrects a casing-only rename from stale ancestors, keeps the identity and deletes nothing.");
        Check.Require(first.Handler.Requests.Count == 1 && second.Handler.Requests.Count == 1 &&
            second.Handler.Requests[0].Path.EndsWith(DropboxProtocol.ListFolder, StringComparison.Ordinal) &&
            !second.Handler.Requests[0].Path.EndsWith(DropboxProtocol.ListFolderContinue, StringComparison.Ordinal),
            "Consecutive listings each begin with a fresh root request, never a saved continuation cursor.");
    }

    private static async Task MetadataAsync()
    {
        foreach (var metadata in new[] { File("/Parent", "Item"), Folder("/Parent", "Item"), File("/Parent", "Item", downloadable: false) })
        {
            var pair = Pair() with { Exclusions = new[] { new Exclusion(Side.Dropbox, "Item", true) } };
            using var fixture = new Fixture(pair, _ => Json(metadata));
            var entry = await fixture.Listing.ReadOneAsync("Item", CancellationToken.None);
            Check.Require(entry is not null && entry.Path == "Item" &&
                entry.IsFolder == Equals(metadata[DropboxProtocol.Tag], DropboxProtocol.TagFolder) &&
                (entry.Skip is not null) == (metadata.TryGetValue(DropboxProtocol.IsDownloadable, out var downloadable) &&
                    Equals(downloadable, false)), "ReadOne parses metadata without applying listing scope.");
            var request = fixture.Handler.Requests.Single();
            using var arg = JsonDocument.Parse(request.Body);
            Check.Require(request.Path.EndsWith(DropboxProtocol.GetMetadata, StringComparison.Ordinal) &&
                arg.RootElement.GetProperty("path").GetString() == "/Parent/Item",
                "ReadOne requests the pair-root-relative metadata path.");
        }
        foreach (var summary in new[] { "path/not_found", "path/not_found/.." })
        {
            using var fixture = new Fixture(Pair(), _ => Error(HttpStatusCode.Conflict, summary));
            Check.Require(await fixture.Listing.ReadOneAsync("Missing", CancellationToken.None) is null,
                "Metadata path/not_found is null.");
        }
        using var refused = new Fixture(Pair(), _ => Error(HttpStatusCode.Conflict, "path/not_file/.."));
        try
        {
            await refused.Listing.ReadOneAsync("Item", CancellationToken.None);
            Check.Require(false, "Other metadata errors must not become absence.");
        }
        catch (ItemFailedException) { }
        // Bug #95: the structured lookup union proves not_found even when the error summary is absent.
        using var structured = new Fixture(Pair(), _ => Json(new Dictionary<string, object?>
        {
            [DropboxProtocol.ErrorSummary] = "",
            [DropboxProtocol.Error] = new Dictionary<string, object?>
            {
                [DropboxProtocol.Tag] = DropboxProtocol.Path,
                [DropboxProtocol.Path] = new Dictionary<string, object?>
                {
                    [DropboxProtocol.Tag] = DropboxProtocol.PathNotFound[(DropboxProtocol.Path.Length + 1)..]
                }
            }
        }, HttpStatusCode.Conflict));
        Check.Require(await structured.Listing.ReadOneAsync("Deleted", CancellationToken.None) is null,
            "The structured path/not_found union is absence, so a both-deleted item reaches the planner");
    }

    private static async Task ReservedNamesAsync()
    {
        var names = new[] { "CON", "AUX", "NUL", "PRN" }
            .Concat(Enumerable.Range(1, 9).SelectMany(number => new[] { "COM" + number, "LPT" + number }))
            .Concat(":*?\"<>|".Select(character => "a" + character + "b"));
        var paths = names.SelectMany(name => new[] { name, name.ToLowerInvariant() + ".txt", name + "/Child", "Folder/" + name })
            .Concat(new[] { "Trailing.", "Trailing ", "Folder/Trailing.", "Folder/Trailing " });
        foreach (var path in paths)
        foreach (var folder in new[] { false, true })
        {
            var metadata = folder ? Folder("/Parent", path) : File("/Parent", path);
            using var fixture = new Fixture(Pair(), _ => Page(false, "end", metadata), _ => Json(metadata));
            var listed = await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
            var single = await fixture.Listing.ReadOneAsync(path, default);
            Check.Require(single?.Skip == nameof(TextKey.SkipReservedName) &&
                (path.EndsWith("/Child", StringComparison.Ordinal) ? listed.Count == 0 : listed.Single().Entry == single),
                "Reserved items retain Skip in ReadOne; listing yields only the topmost skipped item.");
            Check.Require(fixture.Handler.Requests.Count == 2 &&
                fixture.Handler.Requests[1].Path.EndsWith(DropboxProtocol.GetMetadata, StringComparison.Ordinal),
                "A targeted read of an invalid name is one get_metadata, never a sibling listing.");
        }
        foreach (var path in new[] { "a_b.txt", "COM10", "LPT10", "Console.txt" })
        {
            using var fixture = new Fixture(Pair(), _ => Page(false, "end", File("/Parent", path)),
                _ => Json(File("/Parent", path)));
            Check.Require((await CollectAsync(fixture.Listing, "", new ListOptions(true, false))).Single().Entry?.Skip is null &&
                (await fixture.Listing.ReadOneAsync(path, default))?.Skip is null,
                "Literal underscores and reserved prefixes alone are allowed.");
        }
    }

    private static async Task InvalidNeighboursAsync()
    {
        var reserved = nameof(TextKey.SkipReservedName);
        using (var fixture = new Fixture(Pair(), _ => Page(false, "end", File("/Parent", "a:b", id: "id:colon"),
            File("/Parent", "a?b", id: "id:question"), File("/Parent", "a_b", id: "id:underscore"))))
        {
            var entries = await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
            Check.Require(entries.Select(item => (item.Path, item.Entry?.Id, item.Entry?.Skip)).SequenceEqual(
                new (string, string?, string?)[]
                {
                    ("a:b", "id:colon", reserved), ("a?b", "id:question", reserved), ("a_b", "id:underscore", null)
                }), "Formerly colliding spellings stay separate raw skips; a literal underscore syncs and is never remapped.");
        }
        using var retried = new Fixture(Pair(),
            _ => throw new HttpRequestException(),
            _ => Json(File("/Parent", "a:b")));
        Check.Require((await retried.Listing.ReadOneAsync("a:b", default))?.Skip == reserved &&
            retried.Handler.Requests.Count == 2 && retried.Handler.Requests.All(request => request.Path.EndsWith(
                DropboxProtocol.GetMetadata, StringComparison.Ordinal)),
            "A retried targeted read of an invalid name still sends no sibling listing.");
    }

    private static async Task ShippedDefaultsReservedNameAsync()
    {
        // The shipped defaults make the ignore matcher non-empty; a reserved name with a control character must
        // still be yielded as skipped instead of being handed to a matcher that refuses its path.
        var pair = Pair();
        using var fixture = new Fixture(pair, new[] { pair }, ShippedDefaultIgnoreRules(),
            _ => Page(false, "end", File("/Parent", "bad\u0001name")));
        var listed = await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
        Check.Require(listed.Count == 1 && listed[0].Entry?.Skip == nameof(TextKey.SkipReservedName),
            "A reserved name with a control character is yielded as skipped under the shipped defaults.");
    }

    private static string ShippedDefaultIgnoreRules()
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        return SettingsParser.Parse(document.RootElement.GetRawText(), null).DefaultIgnoreRules;
    }

    private static async Task ReservedDescendantsAsync()
    {
        foreach (var includeOutOfScope in new[] { false, true })
        foreach (var root in new[] { "/Parent", "/" })
        {
            using var fixture = new Fixture(Pair(root),
                _ => Page(true, "next", File(root, "CON/Early"), Folder(root, "CON"),
                    File(root, "CON/Child"), Folder(root, "CON/AUX"), File(root, "a:b/Early"), Folder(root, "a:b")),
                _ => Page(false, "end", File(root, "con/Folder/Deep"), Deleted(root, "CON/Old"),
                    File(root, "CONifer/Visible"), File(root, "a:b/Late"), File(root, "a_b/Visible")));
            var listed = await CollectAsync(fixture.Listing, "", new ListOptions(true, includeOutOfScope));
            Check.Require(listed.Select(item => item.Path).SequenceEqual(new[] { "CON", "a:b", "CONifer/Visible", "a_b/Visible" }) &&
                listed.Count(item => item.Entry?.Skip == nameof(TextKey.SkipReservedName)) == 2,
                "Only the topmost invalid item is yielded, even across pages or before its parent; siblings and literal underscores survive.");
        }
        using var under = new Fixture(Pair(), _ => Page(false, "end", File("/Parent", "CON/Child")));
        Check.Require((await CollectAsync(under.Listing, "CON", new ListOptions(false, true))).Count == 0,
            "Listing directly beneath a reserved folder also suppresses descendants without a parent metadata row.");
    }

    private static async Task MalformedMetadataAsync()
    {
        var missingId = File("/Parent", "Item");
        missingId.Remove(DropboxProtocol.Id);
        var invalidSize = File("/Parent", "Item");
        invalidSize[DropboxProtocol.Size] = "not-a-number";
        var invalidTime = File("/Parent", "Item");
        invalidTime[DropboxProtocol.ClientModified] = "not-a-timestamp";
        var nullId = File("/Parent", "Item");
        nullId[DropboxProtocol.Id] = null!;
        foreach (var (metadata, extraction) in new (object Metadata, Type? Extraction)[]
        {
            (missingId, typeof(KeyNotFoundException)), (invalidSize, typeof(InvalidOperationException)),
            (invalidTime, typeof(FormatException)), (nullId, null), (Array.Empty<object>(), typeof(InvalidOperationException))
        })
        {
            using var fixture = new Fixture(Pair(), _ => Json(metadata));
            await ExpectIncompleteAsync(fixture.Listing, extraction);
        }
        using var malformed = new Fixture(Pair(), _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{")
        });
        await ExpectIncompleteAsync(malformed.Listing, null);

        static async Task ExpectIncompleteAsync(DropboxListing listing, Type? extraction)
        {
            try
            {
                await listing.ReadOneAsync("Item", default);
                Check.Require(false, "Malformed metadata must not become absence or a usable entry.");
            }
            catch (ItemFailedException error)
            {
                var cause = error.InnerException;
                Check.Require(error.Path == "Item" && error.Cause == TextKey.ReadDropboxFailed &&
                    (extraction is null ? cause is JsonException : cause?.GetType() == typeof(JsonException)),
                    "A malformed single-item reply is this endpoint's item failure with one JsonException wrapper.");
                if (extraction is null)
                {
                    Check.Require(cause!.InnerException is null,
                        "The parser's own JsonException carries no shape wrapper.");
                }
                else
                {
                    Check.Require(cause!.Message == new JsonException().Message &&
                        cause.InnerException?.GetType() == extraction &&
                        cause.InnerException!.InnerException is null,
                        "A shape failure is exactly JsonException(null, the extraction exception).");
                }
                Check.Require(error.Message == Text.Get(TextKey.ReadDropboxFailed,
                    (TextArg.Path, "Item"), (TextArg.Message, cause!.Message)),
                    "Single-item malformed metadata keeps the Dropbox read cause, path and message.");
            }
        }
    }

    private static async Task PageShapesAsync()
    {
        await ExpectPageFailureAsync(new Dictionary<string, object?>
        {
            [DropboxProtocol.Entries] = Array.Empty<object>(), [DropboxProtocol.HasMore] = false
        }, typeof(KeyNotFoundException), wrapped: true);
        await ExpectPageFailureAsync(new Dictionary<string, object?>
        {
            [DropboxProtocol.Entries] = Array.Empty<object>(), [DropboxProtocol.Cursor] = null,
            [DropboxProtocol.HasMore] = false
        }, null, wrapped: false);
        await ExpectPageFailureAsync(new Dictionary<string, object?>
        {
            [DropboxProtocol.Entries] = Array.Empty<object>(), [DropboxProtocol.Cursor] = 1,
            [DropboxProtocol.HasMore] = false
        }, typeof(InvalidOperationException), wrapped: true);
        using (var fixture = new Fixture(Pair(), _ => Json(new { entries = Array.Empty<object>(), cursor = "", has_more = false })))
            Check.Require((await CollectAsync(fixture.Listing, "", new ListOptions(true, false))).Count == 0,
                "An empty cursor is accepted.");

        static async Task ExpectPageFailureAsync(object page, Type? extraction, bool wrapped)
        {
            using var fixture = new Fixture(Pair(), _ => Json(page));
            try
            {
                await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
                Check.Require(false, "A malformed listing page must make the inventory incomplete.");
            }
            catch (InventoryIncompleteException error)
            {
                var cause = error.InnerException;
                Check.Require(cause?.GetType() == typeof(JsonException),
                    "A malformed page is wrapped in exactly one JsonException.");
                if (!wrapped)
                {
                    Check.Require(cause!.InnerException is null,
                        "A null cursor is the engine's own JsonException, not a shape wrapper.");
                    return;
                }
                Check.Require(cause!.Message == new JsonException().Message &&
                    cause.InnerException?.GetType() == extraction &&
                    cause.InnerException!.InnerException is null,
                    "A page shape failure is exactly JsonException(null, the extraction exception).");
            }
        }
    }

    private static async Task TransportAndProgrammingErrorsAsync()
    {
        foreach (var error in new Exception[] { new InvalidOperationException(), new KeyNotFoundException(),
            new OperationCanceledException() })
        foreach (var listing in new[] { false, true })
        {
            using var fixture = new Fixture(Pair(), _ => throw error);
            try
            {
                if (listing) await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
                else await fixture.Listing.ReadOneAsync("Item", default);
                Check.Require(false, "The injected error must propagate or be mapped.");
            }
            catch (Exception actual)
            {
                Check.Require(ReferenceEquals(actual, error) && fixture.Handler.Requests.Count == 1,
                    "Programming errors and non-cancellation cancellation are not relabelled or retried.");
            }
        }
        foreach (var listing in new[] { false, true })
        {
            using var fixture = new Fixture(Pair(),
                _ => throw new HttpRequestException(),
                _ => listing ? Page(false, "end", File("/Parent", "Item")) : Json(File("/Parent", "Item")));
            if (listing)
                Check.Require((await CollectAsync(fixture.Listing, "", new ListOptions(true, false)))
                    .Single().Path == "Item", "A transient listing failure retries the same page and completes.");
            else
                Check.Require((await fixture.Listing.ReadOneAsync("Item", default))?.Path == "Item",
                    "A transient single-item failure retries the same request and completes.");
            Check.Require(fixture.Handler.Requests.Count == 2 &&
                fixture.Handler.Requests[1].Path.EndsWith(
                    listing ? DropboxProtocol.ListFolder : DropboxProtocol.GetMetadata, StringComparison.Ordinal),
                "One transient failure reissues the same input request once; the transport boundary itself does not resend.");
        }
    }

    private static async Task RepeatedLatePageFailureAsync()
    {
        // The child must remain in Spelling's waiting state across failures until its parent supplies the casing.
        using var fixture = new Fixture(Pair(),
            _ => Page(true, "cursor-one", File("/Parent", "docs/a.txt")),
            _ => BrokenBody(),
            _ => BrokenBody(),
            _ => Page(false, "end", Folder("/Parent", "Docs")));
        var entries = await CollectAsync(fixture.Listing, "", new ListOptions(true, false));
        Check.Require(entries.Select(item => item.Path).SequenceEqual(new[] { "Docs", "Docs/a.txt" }) &&
            entries[1].Entry?.Path == "Docs/a.txt",
            "Repeated late-page failures preserve the waiting child and correct its spelling once, without loss or duplication.");
        Check.Require(fixture.Handler.Requests.Count == 4 &&
            fixture.Handler.Requests.Skip(1).All(request => request.Path.EndsWith(
                DropboxProtocol.ListFolderContinue, StringComparison.Ordinal)) &&
            fixture.Handler.Requests.Skip(1).All(request => Cursor(request) == "cursor-one"),
            "Every retry keeps the unchanged input cursor and no extra root list_folder is sent.");
        Check.Require(fixture.Spools == 4, "Each page attempt seals a fresh spool.");
    }

    private static string? Cursor((string Path, string Body) request)
    {
        using var document = JsonDocument.Parse(request.Body);
        return document.RootElement.GetProperty("cursor").GetString();
    }

    private static async Task PageWaitsAsync()
    {
        foreach (var floor in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(3) })
        {
            using var fixture = new Fixture(Pair(), Adopted, () => new MemoryStream(),
                _ => Page(true, "cursor-one", File("/Parent", "docs/a.txt")),
                _ => Throttled(floor),
                _ => Page(false, "end", Folder("/Parent", "Docs")));
            var entries = await DriveAsync(fixture);
            Check.Require(entries.Select(item => item.Path).SequenceEqual(new[] { "Docs", "Docs/a.txt" }) &&
                fixture.Handler.Requests.Count == 3 && fixture.Spools == 2 &&
                fixture.Handler.Requests.Skip(1).All(request => Cursor(request) == "cursor-one"),
                "A provider wait keeps the waiting child and retries only the same cursor.");
            RequireGap(fixture, 1, floor);
        }

        // Ghost reaches the failed attempt's spool, so publishing before EOF would surface it.
        using var fallback = new Fixture(Pair(), Adopted, () => new MemoryStream(),
            _ => Page(true, "cursor-one", File("/Parent", "First")),
            _ => BrokenBody("{\"entries\":[" + JsonSerializer.Serialize(File("/Parent", "Ghost")) + ","),
            _ => BrokenBody(),
            _ => Page(true, "cursor-two", File("/Parent", "Second")),
            _ => BrokenBody(),
            _ => Page(false, "end", File("/Parent", "Third")));
        var listed = await DriveAsync(fallback);
        Check.Require(listed.Select(item => item.Path).SequenceEqual(new[] { "First", "Second", "Third" }) &&
            fallback.Spools == 6 &&
            fallback.Handler.Requests.Skip(1).Select(Cursor).SequenceEqual(new[]
                { "cursor-one", "cursor-one", "cursor-one", "cursor-two", "cursor-two" }),
            "Failed attempts publish nothing and each retries only its own page.");
        RequireGap(fallback, 1, TimeSpan.FromSeconds(1));
        RequireGap(fallback, 2, TimeSpan.FromSeconds(1.6));
        // A successful page ends only its own read episode, so the next page's failure restarts the schedule.
        RequireGap(fallback, 4, TimeSpan.FromSeconds(1));

        using var stopped = new CancellationTokenSource();
        using var waiting = new Fixture(Pair(), Adopted, () => new MemoryStream(),
            _ => Page(true, "next", File("/Parent", "First")), _ => BrokenBody());
        var reading = CollectAsync(waiting.Listing, "", new ListOptions(true, false), stopped.Token);
        stopped.Cancel();
        var cancelled = false;
        try { await reading; }
        catch (OperationCanceledException) { cancelled = true; }
        Check.Require(cancelled && waiting.Handler.Requests.Count == 2, "Cancellation ends a page wait without another request.");
    }

    // Only Task.Delay suspends these fixtures, so the fake clock moves solely while the listing really waits.
    private static async Task<List<Listed>> DriveAsync(Fixture fixture)
    {
        var listing = CollectAsync(fixture.Listing, "", new ListOptions(true, false));
        while (!listing.IsCompleted)
        {
            await Task.Yield();
            // Advance only while a timer is armed: after it fires, let the waiting continuation run before moving time on.
            if (fixture.Time.HasArmedTimer) fixture.Time.Advance(ClockStep);
        }
        return await listing;
    }

    private static void RequireGap(Fixture fixture, int failedRequest, TimeSpan wait)
    {
        var gap = fixture.Handler.Sent[failedRequest + 1] - fixture.Handler.Sent[failedRequest];
        Check.Require(gap >= wait && gap <= wait + ClockStep,
            "The retry follows exactly one selected wait: a provider wait, or else the adopted fallback, never both.");
    }

    private static async Task LocalSpoolFailuresAsync()
    {
        var spools = 0;
        using var replay = new Fixture(Pair(), Adopted, () => ++spools == 2 ? new UnreadableSpool() : new MemoryStream(),
            _ => Page(true, "next", File("/Parent", "First")),
            _ => Page(false, "end", File("/Parent", "Second")));
        var observed = new List<Listed>();
        var incomplete = false;
        try
        {
            await foreach (var item in replay.Listing.ReadAll("", new ListOptions(true, false), BeginListing(), default)) observed.Add(item);
        }
        catch (InventoryIncompleteException error)
        {
            incomplete = error.InnerException is IOException;
        }
        Check.Require(incomplete && observed.Single().Path == "First" && replay.Handler.Requests.Count == 2,
            "A local replay failure leaves an incomplete inventory and never re-reads the page from Dropbox.");

        using var cleanup = new Fixture(Pair(), Adopted, () => new UndisposableSpool(), _ => BrokenBody());
        var stopped = false;
        try { await CollectAsync(cleanup.Listing, "", new ListOptions(true, false)); }
        catch (AggregateException error) { stopped = error.InnerExceptions.Count == 2; }
        Check.Require(stopped && cleanup.Handler.Requests.Count == 1,
            "A spool that cannot be disposed stops the attempt instead of accumulating retries.");
    }

    private static async Task SealedPageAsync()
    {
        using var fixture = new Fixture(Pair(), _ => Json(new
        {
            entries = new object[] { File("/Parent", "First"), File("/Parent", "Second") },
            cursor = "next-cursor",
            has_more = true
        }));
        var attempt = new RequestAttempt(new object(), AttemptOwner.Prerequisite);
        await using var page = await fixture.Api.ReadPageAsync(DropboxProtocol.ListFolder, new
        {
            path = "",
            recursive = true,
            include_deleted = false,
            include_mounted_folders = true,
            include_non_downloadable_files = true,
            limit = DropboxProtocol.ListPageMaximum
        }, fixture.Pair, attempt, CancellationToken.None);
        var completion = attempt.Completion;
        Check.Require(completion is { } completed && completed.Result == RequestResult.Success &&
            page.Cursor == "next-cursor" && page.HasMore,
            "Only a completely validated response completes the attempt and exposes typed page metadata.");
        Check.Require(page.ReadEntries().Select(item => item.Path).SequenceEqual(new[] { "First", "Second" }),
            "The sealed page replays its entries once in provider order.");
        var replayed = true;
        try { page.ReadEntries().ToList(); }
        catch (InvalidOperationException) { replayed = false; }
        Check.Require(!replayed, "A sealed page permits exactly one ordered replay.");
        await page.DisposeAsync();
        var disposedReplay = true;
        try { page.ReadEntries().ToList(); }
        catch (ObjectDisposedException) { disposedReplay = false; }
        Check.Require(!disposedReplay, "A disposed page cannot be replayed.");
    }

    private static async Task SparseReadsAsync()
    {
        foreach (var includeDeleted in new[] { false, true })
        {
            using var capture = new Fixture(Pair(), _ => Json(new { cursor = "c0" }));
            var attempt = new RequestAttempt(new object(), AttemptOwner.Listener);
            var cursor = await capture.Listing.CaptureCursorAsync(
                new ListOptions(true, false, IncludeDeleted: includeDeleted), attempt, default);
            var request = capture.Handler.Requests.Single();
            using var arg = JsonDocument.Parse(request.Body);
            var value = arg.RootElement;
            Check.Require(cursor == "c0" && attempt.Completion?.Result == RequestResult.Success &&
                request.Path.EndsWith(DropboxProtocol.ListFolderGetLatestCursor, StringComparison.Ordinal) &&
                value.GetProperty("path").GetString() == "/Parent" && value.GetProperty("recursive").GetBoolean() &&
                value.GetProperty("include_deleted").GetBoolean() == includeDeleted &&
                value.GetProperty("include_mounted_folders").GetBoolean() &&
                value.GetProperty("include_non_downloadable_files").GetBoolean(),
                "Cursor capture carries the listing's provenance flags and completes its explicit attempt.");
        }

        using (var feed = new Fixture(Pair(), _ => Page(false, "c1",
            File("/Parent", "docs/a.txt"), Deleted("/Parent", "Gone"), Folder("/Parent", "Docs"))))
        {
            var attempt = new RequestAttempt(new object(), AttemptOwner.Listener);
            await using var page = await feed.Listing.ReadChangePageAsync("c0", attempt, default);
            var entries = page.ReadEntries().ToList();
            Check.Require(entries.Select(item => item.Path).SequenceEqual(new[] { "docs/a.txt", "Gone", "Docs" }) &&
                entries[1].Entry is null && page.Cursor == "c1" && !page.HasMore &&
                attempt.Completion?.Result == RequestResult.Success && Cursor(feed.Handler.Requests.Single()) == "c0" &&
                feed.Handler.Requests[0].Path.EndsWith(DropboxProtocol.ListFolderContinue, StringComparison.Ordinal),
                "A sparse page keeps provider order, each entry's own spelling and native tombstone paths; no parent wait.");
        }

        var bound = Pair() with
        {
            Roots = new RootBinding(@"C:\Local", "/Parent", "connection", new DirectoryIdentity(1, 2, null),
                new DropboxRootIdentity("account", "namespace", "home", "folder"))
        };
        foreach (var (root, expected) in new[] { ("/Parent", "Item"), ("/Elsewhere", "/Elsewhere/Item") })
        {
            using var fixture = new Fixture(bound, _ => Json(File(root, "Item", id: "id:item")));
            var found = await fixture.Listing.ReadByIdAsync("id:item", ByIdAttempt(), default);
            using var arg = JsonDocument.Parse(fixture.Handler.Requests.Single().Body);
            Check.Require(found is { Domain: "namespace" } && found.NativePath == root + "/Item" &&
                found.Entry.Path == expected && arg.RootElement.GetProperty("path").GetString() == "id:item",
                "A by-ID read returns the native location, intact even outside this pair.");
        }
        using (var gone = new Fixture(bound, _ => Error(HttpStatusCode.Conflict, "path/not_found/..")))
        {
            Check.Require(await gone.Listing.ReadByIdAsync("id:item", ByIdAttempt(), default) is null,
                "Only path/not_found is proven identity absence.");
        }
        await ExpectByIdFailureAsync<EngineRequestException>(Pair(), "id:item");
        await ExpectByIdFailureAsync<ItemFailedException>(bound, " ");
        await ExpectByIdFailureAsync<ItemFailedException>(bound, "id:item", _ => Json(File("/Parent", "Item", id: "id:other")));
        await ExpectByIdFailureAsync<ItemFailedException>(bound, "id:item", _ => Error(HttpStatusCode.Conflict, "path/not_file/.."));
        await ExpectByIdFailureAsync<JsonException>(bound, "id:item",
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") });

        static RequestAttempt ByIdAttempt() => new(new object(), AttemptOwner.Request);

        static async Task ExpectByIdFailureAsync<T>(PairConfig pair, string id,
            params Func<CancellationToken, HttpResponseMessage>[] replies) where T : Exception
        {
            using var fixture = new Fixture(pair, replies);
            var failed = false;
            try { await fixture.Listing.ReadByIdAsync(id, ByIdAttempt(), default); }
            catch (T) { failed = true; }
            Check.Require(failed, "An unbound, mismatched, refused or malformed by-ID read is not identity absence.");
        }
    }

    private static async Task FailuresAsync()
    {
        Func<CancellationToken, HttpResponseMessage>[] failures =
        [
            _ => Error(HttpStatusCode.Conflict, "path/not_found/.."),
            _ => Error(HttpStatusCode.Conflict, "reset/.."),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    entries = new object[] { File("/Parent", "Late") },
                    cursor = "end",
                    has_more = false
                }) + "}")
            },
            _ => Json(new { cursor = "missing-entries", has_more = false }),
            _ => Page(false, "end", new { invalid = true })
        ];
        foreach (var failure in failures)
        foreach (var afterPage in new[] { false, true })
        {
            using var fixture = afterPage
                ? new Fixture(Pair(), _ => Page(true, "next", File("/Parent", "First")), failure)
                : new Fixture(Pair(), failure);
            var observed = new List<Listed>();
            try
            {
                await foreach (var item in fixture.Listing.ReadAll("", new ListOptions(true, false), BeginListing(), CancellationToken.None))
                    observed.Add(item);
                Check.Require(false, "A failed page must make the inventory incomplete.");
            }
            catch (InventoryIncompleteException error)
            {
                Check.Require(error.InnerException is not null && observed.Count == (afterPage ? 1 : 0),
                    "Page failure retains its cause and does not pretend the scan completed.");
                Check.Require(error.Message == Text.Get(TextKey.ReadDropboxFailed,
                    (TextArg.Path, ""), (TextArg.Message, error.InnerException!.Message)),
                    "Page failure uses ReadDropboxFailed.");
            }
        }
        using var next = new Fixture(Pair(), _ => Page(false, "end",
            File("/Parent", "First"), File("/Parent", "Second")));
        Check.Require((await CollectAsync(next.Listing, "", new ListOptions(true, false))).Count == 2 &&
            next.Handler.Requests.Count == 1 &&
            next.Handler.Requests[0].Path.EndsWith(DropboxProtocol.ListFolder, StringComparison.Ordinal) &&
            !next.Handler.Requests[0].Path.EndsWith(DropboxProtocol.ListFolderContinue, StringComparison.Ordinal),
            "A listing after an interrupted attempt starts from a fresh root request, never a saved continuation cursor.");
    }

    private static async Task CancellationAsync()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var fixture = new Fixture(Pair());
        try
        {
            await CollectAsync(fixture.Listing, "", new ListOptions(true, false), cancelled.Token);
            Check.Require(false, "Cancelled listing must stop.");
        }
        catch (OperationCanceledException) { }
        Check.Require(fixture.Handler.Requests.Count == 0, "Pre-cancelled listing sends nothing.");

        using var during = new CancellationTokenSource();
        using var pages = new Fixture(Pair(), _ => Page(true, "next", File("/Parent", "First")), _ =>
        {
            during.Cancel();
            throw new OperationCanceledException(during.Token);
        });
        try
        {
            await CollectAsync(pages.Listing, "", new ListOptions(true, false), during.Token);
            Check.Require(false, "Cancellation while reading a page must stop.");
        }
        catch (OperationCanceledException) { }

        using var batchCancelled = new CancellationTokenSource();
        using var batch = new Fixture(Pair(), _ => Page(false, "end",
            File("/Parent", "docs/First"), File("/Parent", "docs/Second"), Folder("/Parent", "Docs")));
        var yielded = new List<Listed>();
        try
        {
            await foreach (var item in batch.Listing.ReadAll("", new ListOptions(true, false), BeginListing(), batchCancelled.Token))
            {
                yielded.Add(item);
                batchCancelled.Cancel();
            }
            Check.Require(false, "Cancellation after a released item must stop the batch.");
        }
        catch (OperationCanceledException) { }
        Check.Require(yielded.Select(item => item.Path).SequenceEqual(new[] { "Docs" }),
            "Cancellation is checked before every yielded item, not once per released batch.");
    }

    private static async Task<List<Listed>> CollectAsync(DropboxListing listing, string under, ListOptions options,
        CancellationToken ct = default)
    {
        var result = new List<Listed>();
        await foreach (var item in listing.ReadAll(under, options, BeginListing(), ct)) result.Add(item);
        return result;
    }

    private static PairConfig Pair(string root = "/Parent") => new("pair", "Pair A", @"C:\Local", root,
        "connection", Array.Empty<string>(), Array.Empty<Exclusion>(), FirstRunPolicy.NewestWins, true, null);

    private static Dictionary<string, object> Folder(string root, string path, string? id = null) => new()
    {
        [DropboxProtocol.Tag] = DropboxProtocol.TagFolder,
        [DropboxProtocol.PathDisplay] = root.TrimEnd('/') + (path.Length == 0 ? "" : "/" + path),
        [DropboxProtocol.Id] = id ?? "id:" + path
    };

    private static Dictionary<string, object> File(string root, string path, string revision = "rev",
        bool downloadable = true, string? id = null)
    {
        var metadata = Folder(root, path, id);
        metadata[DropboxProtocol.Tag] = DropboxProtocol.TagFile;
        metadata[DropboxProtocol.Rev] = revision;
        metadata[DropboxProtocol.Size] = 1L;
        metadata[DropboxProtocol.ClientModified] = "2026-01-01T00:00:00Z";
        metadata[DropboxProtocol.ContentHash] = new string('a', 64);
        metadata[DropboxProtocol.IsDownloadable] = downloadable;
        return metadata;
    }

    private static Dictionary<string, object> Deleted(string root, string path) => new()
    {
        [DropboxProtocol.Tag] = DropboxProtocol.TagDeleted,
        [DropboxProtocol.PathDisplay] = root.TrimEnd('/') + "/" + path
    };

    private static HttpResponseMessage Page(bool more, string cursor, params object[] entries) =>
        Json(new { entries, cursor, has_more = more });

    // A body that fails mid-read, unlike a transport failure, spends an unpublished attempt spool the retry replaces.
    private static HttpResponseMessage BrokenBody(string served = "") =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new FailingReadStream(Encoding.UTF8.GetBytes(served))) };

    private static HttpResponseMessage Error(HttpStatusCode status, string summary) => Json(new { error_summary = summary }, status);

    // The provider's structured reset union: the tag proves it, never the summary text.
    private static HttpResponseMessage Reset() => Json(new Dictionary<string, object>
    {
        ["error_summary"] = "reset/..",
        ["error"] = new Dictionary<string, object> { [DropboxProtocol.Tag] = DropboxProtocol.TagReset }
    }, HttpStatusCode.Conflict);

    private static HttpResponseMessage Throttled(TimeSpan wait)
    {
        var response = Error(HttpStatusCode.TooManyRequests, "too_many_requests/..");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }

    private static HttpResponseMessage Json(object data, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };

    private sealed class Fixture : IDisposable
    {
        // Zero delays keep the retry path immediate here; the adopted schedule's numbers are its owner's checks.
        private static readonly FailureRetryOwner.BackoffSettings NoDelay = new(0, 1, 0, 0);
        private readonly HttpClient _http;
        private int _spools;
        public Handler Handler { get; }
        public DropboxListing Listing { get; }
        public DropboxApi Api { get; }
        public PairConfig Pair { get; }
        public int Spools => _spools;
        public ListingClock Time { get; } = new();

        public Fixture(PairConfig pair, params Func<CancellationToken, HttpResponseMessage>[] replies)
            : this(pair, new[] { pair }, replies) { }

        public Fixture(PairConfig pair, IReadOnlyList<PairConfig> pairs,
            params Func<CancellationToken, HttpResponseMessage>[] replies)
            : this(pair, pairs, null, replies) { }

        public Fixture(PairConfig pair, IReadOnlyList<PairConfig> pairs, string? defaultIgnoreRules,
            params Func<CancellationToken, HttpResponseMessage>[] replies)
            : this(pair, pairs, defaultIgnoreRules, NoDelay, () => new MemoryStream(), replies) { }

        public Fixture(PairConfig pair, FailureRetryOwner.BackoffSettings backoff, Func<Stream> spool,
            params Func<CancellationToken, HttpResponseMessage>[] replies)
            : this(pair, new[] { pair }, null, backoff, spool, replies) { }

        private Fixture(PairConfig pair, IReadOnlyList<PairConfig> pairs, string? defaultIgnoreRules,
            FailureRetryOwner.BackoffSettings backoff, Func<Stream> spool,
            Func<CancellationToken, HttpResponseMessage>[] replies)
        {
            Pair = pair;
            Handler = new Handler(replies, Time);
            _http = new HttpClient(Handler);
            var network = new FakeNetworkSignal();
            network.SetAvailable(true);
            var limiter = new AdaptiveLimiter(1, 1, Time, network, _ => { }, backoff);
            Api = new DropboxApi(_http, new Tokens(), "namespace", limiter, _ =>
            {
                _spools++;
                return new ValueTask<Stream>(spool());
            }, backoff, Time, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
            Listing = new DropboxListing(Api, pair, new PathScope(pair, pairs, defaultIgnoreRules));
        }

        public void Dispose() => _http.Dispose();
    }

    // The hand-driven double, with monotonic stamps on the same clock so provider floors expire when the driver advances.
    private sealed class ListingClock : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        public bool HasArmedTimer => _inner.HasArmedTimer;
        public void Advance(TimeSpan by) => _inner.Advance(by);
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => GetUtcNow().UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _inner.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class FailingReadStream(byte[] served) : MemoryStream(served)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            Position < Length ? base.Read(buffer, offset, count) : throw new IOException("body read failed");
        public override int Read(Span<byte> buffer) =>
            Position < Length ? base.Read(buffer) : throw new IOException("body read failed");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position < Length ? base.ReadAsync(buffer, cancellationToken) : throw new IOException("body read failed");
    }

    private sealed class UnreadableSpool : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("spool read failed");
        public override int Read(Span<byte> buffer) => throw new IOException("spool read failed");
        public override int ReadByte() => throw new IOException("spool read failed");
    }

    private sealed class UndisposableSpool : MemoryStream
    {
        protected override void Dispose(bool disposing) => throw new IOException("spool cleanup failed");
    }

    private sealed class Tokens : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("test-token");
    }

    private sealed class Handler(IEnumerable<Func<CancellationToken, HttpResponseMessage>> replies, TimeProvider time)
        : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, HttpResponseMessage>> _replies = new(replies);
        public List<(string Path, string Body)> Requests { get; } = new();
        public List<DateTimeOffset> Sent { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            Sent.Add(time.GetUtcNow());
            Check.Require(_replies.Count != 0, "Unexpected listing request; fake handler never contacts the network.");
            return _replies.Dequeue()(cancellationToken);
        }
    }
}
