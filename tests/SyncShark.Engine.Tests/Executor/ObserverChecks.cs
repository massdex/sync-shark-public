using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for Observer (`--engine-checks syncer`), written by Observer's writer.</summary>
public static class ObserverChecks
{
    private static readonly DateTimeOffset ComparisonTime = DateTimeOffset.UnixEpoch;

    // The mapped substitutions the raw policy no longer performs, plus trailing dot/space and reserved device names;
    // each one must be recognized without an invalid Windows open or a sibling listing.
    private static readonly string[] InvalidNames =
    [
        "name:", "name*", "name?", "name\"", "name<", "name>", "name|", "name.", "name ",
        "CON", "CON.txt", "NUL", "AUX", "PRN", "COM1", "LPT9"
    ];

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await FreshDropboxObservationAsync();
        await FreshLocalObservationAsync();
        await UnboundObservationAsync();
        await SavedBindingAsync();
        await CounterpartOwnershipAsync();
        await ReturningOriginalAsync();
        await CurrentKindAsync();
        await AbsenceAsync();
        await UnicodeKeysAsync();
        await LinkedParentsAsync();
        await NestedRootsAsync();
        await CancellationAsync();
        await ReadFailuresAsync();
        await SkippedEntriesAsync();
        await InvalidNamesAsync();
        await InvalidAncestorAsync();
        await SavedInvalidBindingAsync();
        await SkipFollowsCurrentEntryAsync();
        await IndependentUnderscoreAsync();
        await RenamedToValidAsync();
        await ZeroSiblingAsync();
    }

    private static async Task FreshDropboxObservationAsync()
    {
        foreach (var mode in new[] { "present", "absent", "skip", "mismatch" })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var path = mode == "skip" ? "name:" : "item";
            var current = FileAt(path);
            Seed(Side.Dropbox, context, current);
            var dropbox = new CountingSide(context.Dropbox);
            var local = new CountingSide(context.Local);
            context = context with { Local = local, Dropbox = dropbox };
            var observer = new Observer(context, new Parents(context));
            var supplied = mode == "absent" ? null : mode == "skip" ? Skipped(path) : current with { Size = 9 };
            var row = await observer.ObserveDropboxAsync(Side.Dropbox, path,
                (PathNames.DropboxKey(path), null, null), default,
                (mode == "mismatch" ? "other" : path, supplied));
            Check.Require(row.Dropbox == (mode == "mismatch" ? current : supplied) &&
                row.Context.Skipped == (mode == "skip"), "121 supplied Dropbox observation preserves its result: " + mode);
            Check.Require(local.Reads == 0 && dropbox.Reads == (mode == "mismatch" ? 1 : 0),
                "121 only an exact primary path avoids Dropbox metadata: " + mode);
        }
    }

    private static async Task FreshLocalObservationAsync()
    {
        foreach (var mode in new[] { "present", "absent", "invalid", "mismatch", "case", "dropbox", "unsupplied" })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var path = mode == "invalid" ? "CON" : "item";
            var current = FileAt(path);
            Seed(Side.Local, context, current);
            var local = new CountingSide(context.Local);
            context = context with { Local = local };
            var observer = new Observer(context, new Parents(context));
            var supplied = mode == "absent" ? null : current with { Size = 9 };
            (string Path, Entry? Entry)? fresh = mode == "unsupplied" ? null :
                (mode == "mismatch" ? "other" : mode == "case" ? "ITEM" : path, supplied);
            var observed = await observer.ObserveLocalAsync(mode == "dropbox" ? Side.Dropbox : Side.Local,
                path, default, fresh);
            var reused = mode is "present" or "absent";
            Check.Require(observed.Local == (mode == "invalid" ? null : reused ? supplied : current),
                "106 supplied Local observation respects absence, transferability, exact path and origin: " + mode);
            Check.Require(local.Reads == (reused || mode == "invalid" ? 0 : 1),
                "106 only a matching transferable Local observation removes a read: " + mode);
        }
    }

    private static async Task UnboundObservationAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var entry = FileAt("item");
            Seed(Side.Local, context, entry);
            Seed(Side.Dropbox, context, entry);
            var row = await Observe(context, side, entry.Path);
            Check.Require(row.Local == entry && row.Dropbox == entry, "Observation reads both resolved paths");
            Check.Require(row.Synced is null, "An unrecorded item has no saved binding");
            Check.Require(row.Key == PathNames.KeyFor(side, entry.Path),
                "Observation computes the requested side's join key");
            Check.Require(row.Context == RowContext.Neutral, "A complete readable row carries no observed fact");
            Check.Require(!row.Ambiguous && row.Extra.Count == 0 && row.LocalHash is null,
                "A point observation has no sibling extras and does not hash");
        }
    }

    private static async Task SavedBindingAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            foreach (var (localPath, dropboxPath) in new[]
            {
                (Local: "same", Dropbox: "same"),
                (Local: "left", Dropbox: "right"),
                (Local: "mapped_", Dropbox: "mapped:")
            })
            {
                using var store = new InMemoryStateStore();
                var context = Context(store);
                var local = FileAt(localPath);
                var dropbox = FileAt(dropboxPath);
                Seed(Side.Local, context, local);
                Seed(Side.Dropbox, context, dropbox);
                var saved = Save(context, local, dropbox);
                var path = side == Side.Local ? localPath : dropboxPath;
                var row = await Observe(context, side, path);
                Check.Require(row.Local == local && row.Dropbox == dropbox,
                    "A saved binding resolves both stored paths");
                Check.Require(row.Synced == saved, "Observation uses the requested side's saved key");
                Check.Require(row.Key == PathNames.KeyFor(side, path),
                    "Observation computes the requested side's join key");
                Check.Require(row.Extra.Count == 0, "A saved binding adds no sibling candidates");
            }

            using (var store = new InMemoryStateStore())
            {
                var context = Context(store);
                var local = FileAt("left");
                var dropbox = FileAt("right");
                var surviving = side == Side.Local ? dropbox : local;
                Seed(PathNames.Other(side), context, surviving);
                var saved = Save(context, local, dropbox);
                var path = side == Side.Local ? local.Path : dropbox.Path;
                var row = await Observe(context, side, path);
                Check.Require(row.Synced == saved && (side == Side.Local ? row.Local : row.Dropbox) is null &&
                    row.EntryOn(PathNames.Other(side)) == surviving,
                    "An absent observed entry still locates its surviving partner through the saved mapping");
                Check.Require(row.PathOn(side) == path,
                    "An absent observed entry keeps its own path as the destination");
            }
        }
    }

    private static async Task CounterpartOwnershipAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        foreach (var bound in new[] { false, true })
        foreach (var replaced in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var other = PathNames.Other(side);
            var counterpart = FileAt("item");
            var partner = FileAt("saved-copy");
            SyncedEntry? saved = bound
                ? side == Side.Local ? Save(context, partner, counterpart) : Save(context, counterpart, partner)
                : null;
            Seed(other, context, replaced ? counterpart with { Id = "replacement" } : counterpart);
            var row = await Observe(context, side, "item");
            var suppressed = bound && !replaced;
            Check.Require(row.Synced is null && row.EntryOn(side) is null &&
                (row.EntryOn(other) is null) == suppressed,
                "Only the matching identity owned by another requested-side key is excluded");
            var action = Decide.For(row, FirstRunPolicy.NewestWins, ComparisonTime, localContentDirty: false);
            Check.Require(suppressed
                ? action.Kind == ActionKind.None && action.None == NoneReason.InSync
                : action.Kind == (other == Side.Local ? ActionKind.Upload : ActionKind.Download),
                "An owned counterpart retires as in-sync; unbound or reused paths still transfer");
            if (saved is not null)
                Check.Require(store.SyncedByKey(context.Pair.PairId, other, saved.KeyOn(other)) == saved,
                    "Excluding a counterpart leaves its actual binding unchanged");
        }
    }

    private static async Task ReturningOriginalAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        foreach (var present in new[] { false, true })
        foreach (var relocated in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var near = FileAt("Original") with { Id = "near" };
            var partner = FileAt("Partner") with { Id = "partner" };
            var returned = FileAt("Original") with { Id = relocated ? partner.Id : "returned" };
            var saved = side == Side.Local ? Save(context, partner, near) : Save(context, near, partner);
            Seed(PathNames.Other(side), context, near);
            if (present) Seed(side, context, returned);
            var row = await Observe(context, side, "Original");
            Check.Require(row.EntryOn(side) == (present ? returned : null), "The requested entry is never invented.");
            Check.Require(present && !relocated
                    ? row.EntryOn(PathNames.Other(side)) == near && row.Synced == saved
                    : row.EntryOn(PathNames.Other(side)) is null && row.Synced is null,
                "Only a present returning original retains the crossed counterpart; absent requests and moves keep #112 behavior.");
        }
    }

    private static async Task CurrentKindAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var local = FileAt("item");
            var dropbox = FileAt("item");
            Seed(Side.Local, context, local);
            Seed(Side.Dropbox, context, dropbox);
            var saved = Save(context, local, dropbox);
            var folder = FileAt("item", folder: true);
            Seed(side, context, folder);
            var row = await Observe(context, side, "item");
            Check.Require(row.EntryOn(side) == folder && row.Synced == saved && saved.IsFolder == false,
                "Observation reads the current kind rather than the saved snapshot");
        }
    }

    private static async Task AbsenceAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            foreach (var presence in new[] { (Local: true, Dropbox: false), (Local: false, Dropbox: true),
                (Local: false, Dropbox: false) })
            {
                using var store = new InMemoryStateStore();
                var context = Context(store);
                if (presence.Local) Seed(Side.Local, context, FileAt("entry"));
                if (presence.Dropbox) Seed(Side.Dropbox, context, FileAt("entry"));
                var row = await Observe(context, side, "entry");
                Check.Require((row.Local is not null) == presence.Local &&
                    (row.Dropbox is not null) == presence.Dropbox,
                    "Observation preserves absent entries");
                Check.Require(row.Context.Destination == (presence.Local || presence.Dropbox ? "entry" : null),
                    "Only a one-sided row receives the opposite path as its destination");
                Check.Require(row.Extra.Count == 0 && row.Synced is null,
                    "Absent sides invent no candidates or history");
            }
        }
    }

    private static async Task UnicodeKeysAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        Seed(Side.Dropbox, context, FileAt("S_"));
        Seed(Side.Dropbox, context, Skipped("S?"));
        var row = await Observe(context, Side.Local, "s_");
        Check.Require(row.Dropbox?.Path == "S_" && row.Extra.Count == 0 && !row.Ambiguous,
            "The frozen case-insensitive key finds the point entry without enumerating siblings");
    }

    private static async Task LinkedParentsAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var localParent = FileAt("mapped_", folder: true);
            var dropboxParent = FileAt("mapped:", folder: true);
            Seed(Side.Local, context, localParent);
            Seed(Side.Dropbox, context, dropboxParent);
            Save(context, localParent, dropboxParent);
            var source = FileAt(side == Side.Local ? "mapped_/child" : "mapped:/child");
            var expected = side == Side.Local ? "mapped:/child" : "mapped_/child";
            Seed(side, context, source);
            var row = await Observe(context, side, source.Path);
            Check.Require(row.Context.Destination == expected && row.Synced is null,
                "An unlinked child uses the saved parent binding for its missing-side destination");
            var other = PathNames.Other(side);
            var target = FileAt(expected);
            Seed(other, context, target);
            row = await Observe(context, side, source.Path);
            Check.Require(row.EntryOn(side) == source && row.EntryOn(other) == target &&
                row.Context.Destination is null,
                "Parent binding locates both existing children without a child record");
        }
    }

    private static async Task NestedRootsAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            foreach (var path in new[] { "Tree", "Tree/Nested", "TreeSibling" })
            {
                using var store = new InMemoryStateStore();
                var pair = Pair();
                var nested = pair with
                {
                    PairId = "nested", LocalRoot = @"C:\ObserverRoot\Tree\Nested",
                    DropboxRoot = "/ObserverRoot/Tree/Nested"
                };
                var context = Context(store, new PathScope(pair, new[] { pair, nested }));
                Seed(side, context, FileAt(path, folder: true));
                var row = await Observe(context, side, path);
                Check.Require(context.Scope.HoldsNestedRoot(side, path) == (path != "TreeSibling"),
                    "Nested-root protection uses the deleting side and segment boundaries");
                Check.Require(!row.Context.HoldsNestedRoot,
                    "Observation alone carries no nested-root fact without a folder delete");
            }
        }
    }

    private static async Task CancellationAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await new Observer(context, new Parents(context))
                .ObserveAsync(Side.Local, "item", cancellation.Token);
            Check.Require(false, "Observation must propagate cancellation");
        }
        catch (OperationCanceledException) { }
    }

    private static async Task ReadFailuresAsync()
    {
        foreach (var observed in Enum.GetValues<Side>())
        {
            foreach (var failing in Enum.GetValues<Side>())
            {
                foreach (var error in new Exception[]
                {
                    new ItemFailedException("item", failing == Side.Local
                        ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed, null),
                    new ItemFailedException("item", TextKey.FailLocalInUseOrAccessDenied, null),
                    new ChangedUnderneathException("left"),
                    new OperationCanceledException(), new InvalidOperationException()
                })
                {
                    using var store = new InMemoryStateStore();
                    var context = Context(store);
                    var local = FileAt("left");
                    var dropbox = FileAt("right");
                    Seed(Side.Local, context, local);
                    Seed(Side.Dropbox, context, dropbox);
                    var saved = Save(context, local, dropbox);
                    ((FakeSide)context.For(failing)).FailNext(
                        failing == Side.Local ? local.Path : dropbox.Path, error);
                    var propagated = false;
                    try
                    {
                        await Observe(context, observed,
                            observed == Side.Local ? local.Path : dropbox.Path);
                    }
                    catch (Exception actual) when (ReferenceEquals(actual, error)) { propagated = true; }
                    Check.Require(propagated && store.Issues(context.Pair.PairId).Count == 0 &&
                        store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved,
                        "Observation propagates the original read error without changing state or inventing absence");
                    var row = await Observe(context, observed,
                        observed == Side.Local ? local.Path : dropbox.Path);
                    Check.Require(row.Local == local && row.Dropbox == dropbox && row.Synced == saved,
                        "A later observation still sees both linked entries after a failed read");
                }
            }
        }

        using (var store = new InMemoryStateStore())
        {
            var context = Context(store);
            var nativeDropbox = (FakeSide)context.Dropbox;
            var local = new CountingSide(context.Local);
            var dropbox = new CountingSide(nativeDropbox);
            context = context with { Local = local, Dropbox = dropbox };
            var error = new ItemFailedException("name:", TextKey.ReadDropboxFailed, null);
            nativeDropbox.FailNext("name:", error);
            var caught = false;
            try { await Observe(context, Side.Local, "name:"); }
            catch (Exception actual) when (ReferenceEquals(actual, error)) { caught = true; }
            Check.Require(caught && local.Reads == 0 && store.Issues(context.Pair.PairId).Count == 0,
                "A failed provider read for an invalid name stays a failure, never a local open, skip or absence");
        }
    }

    private static async Task SkippedEntriesAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            foreach (var folder in new[] { false, true })
            {
                using var store = new InMemoryStateStore();
                var context = Context(store);
                var skipped = Skipped(nameof(SkippedEntriesAsync), folder);
                Seed(side, context, skipped);
                var row = await Observe(context, side, skipped.Path);
                Check.Require(row.EntryOn(side) == skipped && row.EntryOn(PathNames.Other(side)) is null &&
                    row.LocalHash is null,
                    "A skipped entry remains present with its reason and is not opened or hashed");
                Check.Require(row.Context.Destination == skipped.Path &&
                    store.Issues(context.Pair.PairId).Count == 0,
                    "Observation leaves the skip notice to its caller");
                Check.Require(Decide.For(row, FirstRunPolicy.NewestWins, ComparisonTime, localContentDirty: false) is
                    { Kind: ActionKind.None, None: NoneReason.Skipped },
                    "A newly observed skip becomes the skipped planner outcome, not hash or transfer work");
            }
        }
    }

    private static async Task InvalidNamesAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            foreach (var path in InvalidNames)
            {
                foreach (var folder in new[] { false, true })
                {
                    using var store = new InMemoryStateStore();
                    var context = Context(store);
                    var nativeDropbox = (FakeSide)context.Dropbox;
                    var parent = PathNames.Parent(path);
                    if (parent.Length > 0)
                    {
                        nativeDropbox.Seed(FileAt(parent, folder: true) with
                            { Skip = nameof(TextKey.SkipReservedName) }, null);
                    }
                    var skipped = Skipped(path, folder);
                    nativeDropbox.Seed(skipped, null);
                    var local = new CountingSide(context.Local);
                    var dropbox = new CountingSide(nativeDropbox);
                    context = context with { Local = local, Dropbox = dropbox };
                    var row = await Observe(context, side, path);
                    Check.Require(row.Synced is null && row.Local is null && row.Dropbox == skipped,
                        $"An invalid name is resolved to its skipped provider entry ({path})");
                    Check.Require(row.Extra.Count == 0 && !row.Ambiguous && row.LocalHash is null,
                        "An invalid name never becomes a sibling candidate");
                    Check.Require(row.Context.Skipped && row.Context.Destination == path,
                        "An invalid direct target is a skip with its own destination, never absence");
                    Check.Require(local.Reads == 0 && local.Listings == 0 && dropbox.Listings == 0,
                        $"An invalid name performs no invalid local open and no sibling listing ({path})");
                    Check.Require(dropbox.Reads == 1, "The point provider read is the only read");
                    Check.Require(local.Work == 0 && dropbox.Work == 0,
                        "A skipped target performs no transfer, create, delete or move");
                    Check.Require(
                        store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(path)) is null &&
                        store.SyncedByKey(context.Pair.PairId, Side.Dropbox, PathNames.DropboxKey(path)) is null &&
                        store.Issues(context.Pair.PairId).Count == 0,
                        "Observation writes no record or notice for a skipped target");
                }
            }
        }
    }

    private static async Task InvalidAncestorAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var ancestor = FileAt("invalid:", folder: true) with { Skip = nameof(TextKey.SkipReservedName) };
        var child = FileAt("invalid:/child") with { Skip = nameof(TextKey.SkipReservedName) };
        var nativeDropbox = (FakeSide)context.Dropbox;
        nativeDropbox.Seed(ancestor, null);
        nativeDropbox.Seed(child, null);
        var local = new CountingSide(context.Local);
        var dropbox = new CountingSide(nativeDropbox);
        context = context with { Local = local, Dropbox = dropbox };

        // The child is observed first; direct observation never needs the parent's listing order.
        var childRow = await Observe(context, Side.Local, child.Path);
        Check.Require(childRow.Dropbox == child && childRow.Context.Skipped,
            "A child of an invalid ancestor is skipped without waiting for the parent's page");
        var ancestorRow = await Observe(context, Side.Local, ancestor.Path);
        Check.Require(ancestorRow.Dropbox == ancestor && ancestorRow.Context.Skipped,
            "The invalid ancestor itself is the same skipped observation");
        Check.Require(local.Reads == 0 && local.Listings == 0 && dropbox.Listings == 0 &&
            store.SyncedByKey(context.Pair.PairId, Side.Dropbox, PathNames.DropboxKey(child.Path)) is null,
            "Invalid ancestors require no local open, sibling listing or record");
    }

    private static async Task SavedInvalidBindingAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        {
            var local = FileAt("Local Copy");
            var dropbox = Skipped("b:");
            var saved = SyncedEntries.From(local, dropbox);
            Check.Require(saved.DropboxKey == PathNames.DropboxKey("b:"),
                "The saved binding joins the raw provider spelling");

            // A restart reopens only the persisted binding; no observation state survives.
            using var reopened = new InMemoryStateStore();
            var context = Context(reopened);
            reopened.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
            Seed(Side.Local, context, local);
            Seed(Side.Dropbox, context, dropbox);
            var localCount = new CountingSide(context.Local);
            var dropboxCount = new CountingSide(context.Dropbox);
            context = context with { Local = localCount, Dropbox = dropboxCount };
            var row = await Observe(context, side, side == Side.Local ? local.Path : dropbox.Path);
            Check.Require(row.Synced == saved && row.Local == local && row.Dropbox == dropbox,
                "A restart finds the joined raw-key binding and keeps the saved local copy");
            Check.Require(row.Context.Skipped && row.Extra.Count == 0 && !row.Ambiguous,
                "A skipped saved binding is never treated as absence or deletion");
            Check.Require(localCount.Reads == 1 && dropboxCount.Reads == 1 && dropboxCount.Listings == 0 &&
                localCount.Work == 0 && dropboxCount.Work == 0,
                "The saved valid local counterpart is still read, with no sibling listing or effect");
            Check.Require(reopened.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved,
                "Observation leaves the joined binding unchanged");
        }
    }

    private static async Task SkipFollowsCurrentEntryAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var kept = FileAt("kept");
        var historical = "b:";
        Seed(Side.Local, context, kept);
        Seed(Side.Dropbox, context, FileAt(historical));
        var saved = new SyncedEntry(PathNames.Key(kept.Path), kept.Path,
            PathNames.DropboxKey(historical), historical, false, kept.Id, kept.Size, kept.Modified.UtcTicks,
            "historical", "");
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var row = await Observe(context, Side.Dropbox, historical);
        Check.Require(row.Synced == saved && row.Local == kept && !row.Context.Skipped,
            "Skip is taken from the current observed entry, not the saved historical path");
        Seed(Side.Dropbox, context, Skipped(historical));
        row = await Observe(context, Side.Dropbox, historical);
        Check.Require(row.Context.Skipped && row.Dropbox?.Skip == nameof(TextKey.SkipReservedName),
            "A current skip entry becomes the skipped observation");
    }

    private static async Task IndependentUnderscoreAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var under = FileAt("b_");
        var colon = Skipped("b:");
        Seed(Side.Dropbox, context, under, colon);
        var valid = await Observe(context, Side.Local, "b_");
        Check.Require(valid.Dropbox == under && valid.Dropbox!.Skip is null && !valid.Context.Skipped &&
            valid.Extra.Count == 0,
            "A legitimate underscore is literal and does not resolve to the invalid spelling");
        var invalid = await Observe(context, Side.Local, "b:");
        Check.Require(invalid.Dropbox == colon && invalid.Synced is null && invalid.Extra.Count == 0,
            "An invalid spelling is skipped without inferring a valid underscore name");
        valid = await Observe(context, Side.Local, "b_");
        Check.Require(valid.Dropbox == under && !valid.Context.Skipped,
            "Skipping one spelling leaves the unrelated valid item progressing");

        var first = Skipped("name:");
        var second = Skipped("name?");
        Seed(Side.Dropbox, context, first, second);
        foreach (var (path, entry) in new[] { ("name:", first), ("name?", second) })
        {
            var row = await Observe(context, Side.Local, path);
            Check.Require(row.Dropbox == entry && !row.Ambiguous && row.Extra.Count == 0,
                "Two formerly colliding remote spellings stay distinct, without a NameClash candidate");
        }
        Check.Require(store.Issues(context.Pair.PairId).Count == 0,
            "Observation never manufactures a collision notice");
    }

    private static async Task RenamedToValidAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        Seed(Side.Dropbox, context, Skipped("name:"));
        var old = await Observe(context, Side.Dropbox, "name:");
        Check.Require(old.Context.Skipped, "The old invalid spelling is skipped");

        var local = FileAt("name_");
        var renamed = FileAt("name-");
        Seed(Side.Local, context, local);
        Seed(Side.Dropbox, context, renamed);
        var saved = Save(context, local, renamed);
        var row = await Observe(context, Side.Local, local.Path);
        Check.Require(row.Dropbox == renamed && row.Synced == saved && !row.Context.Skipped &&
            row.Extra.Count == 0,
            "A positively observed valid rename is not skipped and carries no stale skip fact");
        Check.Require(store.Issues(context.Pair.PairId).Count == 0,
            "Observation leaves notice closure and the following sync to its existing owner");
    }

    private static async Task ZeroSiblingAsync()
    {
        // MEASURE section 2.1 preserves these focused fixture sizes; the representative anchors are the budget key's.
        const int smallSiblings = 5_110;
        const int largeSiblings = 20_440;
        foreach (var siblings in new[] { smallSiblings, largeSiblings })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var native = (FakeSide)context.Dropbox;
            for (var index = 0; index < siblings; index++)
                native.Seed(FileAt($"sibling-{index:D5}"), null);
            native.Seed(FileAt("involved"), null);
            var local = new CountingSide(context.Local);
            var dropbox = new CountingSide(native);
            context = context with { Local = local, Dropbox = dropbox };
            var observer = new Observer(context, new Parents(context));
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var row = await observer.ObserveAsync(Side.Local, "involved", CancellationToken.None);
                Check.Require(row.Dropbox?.Path == "involved" && row.Extra.Count == 0,
                    "A flat-root observation returns only its own entry at every retry");
            }
            Check.Require(local.Listings == 0 && dropbox.Listings == 0 && dropbox.SiblingListings == 0 &&
                dropbox.Yielded == 0,
                "Name-skip observation performs zero sibling ReadAll and retains no sibling snapshot");
            Check.Require(local.Reads == 3 && dropbox.Reads == 3,
                "Each retry is one point read per side, with no repeated listing work");
            Console.WriteLine($"observer-name-skip siblings={siblings}; involved=1; sibling-readall=0; " +
                $"sibling-yielded=0; retries=3; point-reads-per-side=3; retained-cache=none");
        }
    }

    private static Task<Row> Observe(ExecutorContext context, Side side, string path) =>
        new Observer(context, new Parents(context)).ObserveAsync(side, path, CancellationToken.None);

    private static PairConfig Pair() => new("pair", "Pair A", @"C:\ObserverRoot", "/ObserverRoot", "connection",
        [], [], FirstRunPolicy.NewestWins, true, null);

    private static ExecutorContext Context(InMemoryStateStore store, PathScope? scope = null)
    {
        var pair = Pair();
        store.SavePair(pair);
        var settings = new EngineSettings("", "", "", "", "", RecycleFallback.Fail, 1,
            FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows);
        return new ExecutorContext(Local: new FakeSide(Side.Local), Dropbox: new FakeSide(Side.Dropbox),
            Recovery: new FakeRecovery(), Store: store, Pair: pair, Scope: scope ?? new PathScope(pair, [pair]),
            Settings: settings, Runtime: new ReplaceTurn(), Admission: new FakeAdmission(), Priority: false,
            Events: _ => { },
            ReserveDownloadAsync: (_, _, _, _) => throw new InvalidOperationException(),
            Time: TimeProvider.System,
            ReadDeletionEvidenceAsync: (_, _, _) => throw new InvalidOperationException(),
            BeginDownloadAttempt: _ => throw new InvalidOperationException(),
            CompleteDownloadAttemptAsync: (_, _) => throw new InvalidOperationException(),
            UploadAdmissionIdentity: ("account", "route", "source"),
            ItemAttempts: new NoAttempts(), Opportunity: new object(), ExpectedGeneration: 1);
    }

    private static Entry FileAt(string path, bool folder = false) =>
        new(path, folder, folder ? 0 : 1, DateTimeOffset.UnixEpoch, path, "", "", null);

    private static Entry Skipped(string path, bool folder = false) =>
        FileAt(path, folder) with { Skip = nameof(TextKey.SkipReservedName) };

    private static void Seed(Side side, ExecutorContext context, params Entry[] entries)
    {
        var target = (FakeSide)context.For(side);
        foreach (var entry in entries) target.Seed(entry, null);
    }

    private static SyncedEntry Save(ExecutorContext context, Entry local, Entry dropbox)
    {
        var saved = SyncedEntries.From(local, dropbox);
        context.Store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        return saved;
    }

    /// <summary>Observer never records item attempts; any call is a check failure.</summary>
    private sealed class NoAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            throw new InvalidOperationException();

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
            IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            PendingEffect? expectedPending, ItemRetryToken? expectedEpisode) =>
            throw new InvalidOperationException();

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
            throw new InvalidOperationException();
    }
}
