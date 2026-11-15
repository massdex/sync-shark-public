using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for LockKeys (`--engine-checks syncer`), written by LockKeys's writer.</summary>
public static class LockKeysChecks
{
    public static void Run()
    {
        var pair = new PairConfig("pair-a", "Pair A", @"C:\SyncRoot", "/RemoteRoot", "connection-a",
            Array.Empty<string>(), Array.Empty<Exclusion>(), FirstRunPolicy.NewestWins, true, null);
        var row = ShapeRow("Local/Original", "Remote/Original", "Local/Original", "Remote/Original");
        var originals = new[] { (Side.Local, row.Local!.Path), (Side.Dropbox, row.Dropbox!.Path) };

        BothSides(pair, row, originals);
        Targets(pair, row, originals);
        MissingSides(pair, row, originals);
        KeepBoth(pair, row, originals);
        FootprintShapes(pair);
        NewFolderFootprints(pair);
        DirectFootprints(pair);
        PairAndAccount(pair, row);
        var operation = new FolderRemovalState("operation-a", 1, FolderRemovalPhase.Retained,
            Side.Dropbox, EntryAt("Folder") with { IsFolder = true }, null, null, row.Context,
            null, true, null, null, FolderRemovalAuthority.Inferred);
        var scopes = LockKeys.ForRecovery(pair, operation, new NoPending(),
            @"D:\RecoveryStage\owned.tmp", @"D:\RecoveryStage\allocated.tmp");
        Check.Require(scopes.Any(scope => scope.Key == PathLocks.LocalNativeKey(@"D:\RecoveryStage\owned.tmp")) &&
            scopes.Any(scope => scope.Key == PathLocks.LocalNativeKey(@"D:\RecoveryStage\allocated.tmp")),
            "recovery footprint includes selected member stage and allocation outside pair root");
    }

    private static void NewFolderFootprints(PairConfig pair)
    {
        var folder = EntryAt("Folder") with { IsFolder = true };
        var row = ShapeRow("Folder", "Folder", "Folder", "Folder") with { Local = folder, Dropbox = null, Synced = null };
        foreach (var side in Enum.GetValues<Side>())
        {
            var current = side == Side.Dropbox ? row : row with { Local = null, Dropbox = folder };
            var create = new SyncAction(side == Side.Dropbox ? ActionKind.EnsureDropboxFolder : ActionKind.EnsureLocalFolder,
                current, Target: folder.Path);
            Check.Require(LockKeys.ForLiveAction(create, pair, null).All(scope => !scope.IncludesDescendants) &&
                LockKeys.ForScopes(pair, LockKeys.ForLiveAction(create, pair, null)).All(scope => !scope.IncludesDescendants),
                "F02: creating an absent folder admits exact paths only");
        }
        var rename = LockKeys.ForRenamePaths(pair, [(Side.Local, "Folder"), (Side.Dropbox, "Moved")], true);
        Check.Require(rename.All(scope => scope.ExactEvidence && scope.IncludesDescendants) &&
            LockKeys.ForScopes(pair, rename).All(scope => scope.IncludesDescendants),
            "folder rename uses exact evidence but keeps subtree locks");
        var delete = new SyncAction(ActionKind.DeleteLocal, row);
        Check.Require(LockKeys.ForLiveAction(delete, pair, null).All(scope => scope.IncludesDescendants && !scope.ExactEvidence) &&
            LockKeys.ForScopes(pair, LockKeys.ForLiveAction(delete, pair, null)).All(scope => scope.IncludesDescendants),
            "F02: deleting a folder retains descendant evidence checks");
    }

    private static void BothSides(PairConfig pair, Row row, (Side, string)[] originals)
    {
        foreach (var isFolder in new[] { false, true })
        {
            var current = row with
            {
                Local = row.Local! with { IsFolder = isFolder },
                Dropbox = row.Dropbox! with { IsFolder = isFolder },
                Synced = row.Synced! with { IsFolder = isFolder }
            };
            foreach (var kind in new[] { ActionKind.Upload, ActionKind.Download, ActionKind.DeleteLocal,
                ActionKind.DeleteDropbox, ActionKind.Record, ActionKind.Notice, ActionKind.None,
                ActionKind.ForgetOnly, ActionKind.DeferFolder })
            {
                RequireKeys(LockKeys.For(new SyncAction(kind, current), pair, null), pair, originals,
                    $"{kind} locks both file/folder locations and deduplicates saved paths");
            }
        }

        var mapped = row with
        {
            Local = EntryAt("Folder/Name_"),
            Dropbox = EntryAt("Folder/Name:"),
            Synced = null,
            Extra = new[] { EntryAt("folder/name_"), EntryAt("folder/name:") }
        };
        RequireKeys(LockKeys.For(new SyncAction(ActionKind.Notice, mapped), pair, null), pair,
            new[] { (Side.Local, "Folder/Name_"), (Side.Dropbox, "Folder/Name:") },
            "Extra candidates share the row's primary key and add no lock of their own");
    }

    private static void Targets(PairConfig pair, Row row, (Side, string)[] originals)
    {
        var targets = new (ActionKind Kind, Side Side)[]
        {
            (ActionKind.Upload, Side.Dropbox), (ActionKind.Download, Side.Local),
            (ActionKind.DeleteDropbox, Side.Dropbox), (ActionKind.DeleteLocal, Side.Local),
            (ActionKind.EnsureDropboxFolder, Side.Dropbox), (ActionKind.EnsureLocalFolder, Side.Local),
            (ActionKind.MoveDropbox, Side.Dropbox), (ActionKind.MoveLocal, Side.Local),
            (ActionKind.MoveUnlinked, Side.Local), (ActionKind.MoveUnlinked, Side.Dropbox),
            (ActionKind.ReplaceKind, Side.Local), (ActionKind.ReplaceKind, Side.Dropbox)
        };
        foreach (var (kind, side) in targets)
        {
            var action = new SyncAction(kind, row, Target: "Destination/Name",
                Side: kind is ActionKind.MoveUnlinked or ActionKind.ReplaceKind ? side : null);
            Check.Require(action.WrittenSide() == side, $"{kind} has one shared written-side mapping");
            RequireKeys(LockKeys.For(action, pair, null), pair,
                originals.Append((side, action.Target!)), $"{kind} locks Target on {side}");
        }

        foreach (var kind in new[] { ActionKind.MoveUnlinked, ActionKind.ReplaceKind })
            RequireThrows<ArgumentException>(() => new SyncAction(kind, row).WrittenSide(),
                "An explicit-side action cannot guess its written side.");
        RequireThrows<ArgumentOutOfRangeException>(() => new SyncAction(ActionKind.None, row).WrittenSide(),
            "A state-only action has no written side.");
    }

    private static void MissingSides(PairConfig pair, Row row, (Side, string)[] originals)
    {
        foreach (var reason in Enum.GetValues<NoneReason>())
        {
            RequireKeys(LockKeys.For(new SyncAction(ActionKind.None, row, None: reason), pair, null),
                pair, originals, $"None({reason}) retains the row paths");
        }

        foreach (var side in Enum.GetValues<Side>())
        {
            var missing = side == Side.Local ? row with { Local = null } : row with { Dropbox = null };
            var kind = side == Side.Local ? ActionKind.DeleteDropbox : ActionKind.DeleteLocal;
            RequireKeys(LockKeys.For(new SyncAction(kind, missing), pair, null), pair, originals,
                "Delete locks the absent saved location as well as the surviving entry");

            var free = missing with
            {
                Synced = null,
                Context = missing.Context with { Destination = "MappedParent/NewItem" }
            };
            var present = side == Side.Local ? (Side.Dropbox, row.Dropbox!.Path) : (Side.Local, row.Local!.Path);
            RequireKeys(LockKeys.For(new SyncAction(ActionKind.None, free), pair, null), pair,
                new[] { present, (side, free.Context.Destination!) },
                "The missing side's observation path comes from Context.Destination");
        }

        RequireKeys(LockKeys.For(new SyncAction(ActionKind.ForgetOnly,
                row with { Local = null, Dropbox = null }), pair, null), pair, originals,
            "A saved-only row locks both saved locations");
        RequireKeys(LockKeys.For(new SyncAction(ActionKind.Record,
                row with { Local = row.Local! with { Path = "Moved/Local" } }), pair, null), pair,
            originals.Append((Side.Local, "Moved/Local")),
            "Record/refresh locks observed and saved paths when they differ");
    }

    private static void KeepBoth(PairConfig pair, Row row, (Side, string)[] originals)
    {
        foreach (var reuseLocal in new[] { false, true })
        foreach (var reuseDropbox in new[] { false, true })
        {
            var labels = new KeepBothLabels("Remote/LocalCopy", reuseDropbox, "Local/RemoteCopy", reuseLocal);
            RequireKeys(LockKeys.For(new SyncAction(ActionKind.KeepBoth, row), pair, labels), pair,
                originals.Append((Side.Local, labels.LocalLabel!)).Append((Side.Dropbox, labels.DropboxLabel!)),
                "KeepBoth locks both labels even when a finished copy is reused");
        }
        RequireThrows<InvalidOperationException>(
            () => LockKeys.For(new SyncAction(ActionKind.KeepBoth, row), pair, null),
            "KeepBoth cannot return incomplete keys before labels have been chosen");
        var oneLegs = new (string Name, KeepBothLabels Labels, (Side Side, string Path) Label)[]
        {
            ("Dropbox", new KeepBothLabels("Remote/LocalCopy", false, null, false),
                (Side.Dropbox, "Remote/LocalCopy")),
            ("Local", new KeepBothLabels(null, false, "Local/RemoteCopy", false),
                (Side.Local, "Local/RemoteCopy"))
        };
        foreach (var (name, labels, label) in oneLegs)
            RequireKeys(LockKeys.For(new SyncAction(ActionKind.KeepBoth, row), pair, labels), pair,
                originals.Append(label), $"KeepBoth with only the {name} leg locks only its label");
    }

    // The sixteen measured M29 footprint shapes, including the unchanged-string, case-variant, all-distinct,
    // keep-both, delete and notice cases. Each must keep exactly its real-location key set.
    private static void FootprintShapes(PairConfig pair)
    {
        static SyncAction Make(ActionKind kind, Row row, string? target = null, Side? side = null) =>
            new(kind, row, Target: target, Side: side);
        var originals = new[] { (Side.Local, "Top/Local"), (Side.Dropbox, "Top/Remote") };
        var shapes = new (string Name, SyncAction Action, KeepBothLabels? Labels, (Side, string)[] Paths)[]
        {
            ("observed both", Make(ActionKind.Upload, ShapeRow("Top/Local", "Top/Remote", null, null)),
                null, originals),
            // A decoded row and a saved record never share text; the skip is by string value, not reference.
            ("equal-content saved strings", Make(ActionKind.Upload,
                    ShapeRow(Copy("Top/Local"), Copy("Top/Remote"), "Top/Local", "Top/Remote")),
                null, originals),
            ("target already saved", Make(ActionKind.Upload,
                    ShapeRow("Top/Local", "Top/Remote", "Top/Local", "Top/Remote"), "Top/Remote"),
                null, originals),
            ("Download target", Make(ActionKind.Download,
                    ShapeRow("Top/Local", null, "Top/Local", "Top/Remote"), "Top/Target"),
                null, new[] { (Side.Local, "Top/Local"), (Side.Dropbox, "Top/Remote"),
                    (Side.Local, "Top/Target") }),
            ("delete destination", Make(ActionKind.DeleteDropbox,
                    ShapeRow("Top/Local", null, null, null, "Top/Local")),
                null, new[] { (Side.Local, "Top/Local"), (Side.Dropbox, "Top/Local") }),
            ("none destination", Make(ActionKind.None, ShapeRow(null, "Top/Remote", null, null, "Top/Remote")),
                null, new[] { (Side.Local, "Top/Remote"), (Side.Dropbox, "Top/Remote") }),
            ("record moved local", Make(ActionKind.Record,
                    ShapeRow("Moved/Local", "Top/Remote", "Top/Local", "Top/Remote")),
                null, new[] { (Side.Local, "Moved/Local"), (Side.Dropbox, "Top/Remote"),
                    (Side.Local, "Top/Local") }),
            ("keep both", Make(ActionKind.KeepBoth,
                    ShapeRow("Top/Local", "Top/Remote", "Top/Local", "Top/Remote")),
                new KeepBothLabels("Top/RemoteCopy", false, "Top/LocalCopy", false),
                new[] { (Side.Local, "Top/Local"), (Side.Dropbox, "Top/Remote"),
                    (Side.Local, "Top/LocalCopy"), (Side.Dropbox, "Top/RemoteCopy") }),
            ("keep both one leg", Make(ActionKind.KeepBoth,
                    ShapeRow("Top/Local", "Top/Remote", "Top/Local", "Top/Remote")),
                new KeepBothLabels(null, false, "Top/LocalCopy", true),
                new[] { (Side.Local, "Top/Local"), (Side.Dropbox, "Top/Remote"),
                    (Side.Local, "Top/LocalCopy") }),
            ("move local", Make(ActionKind.MoveLocal,
                    ShapeRow("Old/Local", "Old/Remote", "Old/Local", "Old/Remote"), "New/Local", Side.Local),
                null, new[] { (Side.Local, "Old/Local"), (Side.Dropbox, "Old/Remote"),
                    (Side.Local, "New/Local") }),
            ("move dropbox", Make(ActionKind.MoveDropbox,
                    ShapeRow("Old/Local", "Old/Remote", "Old/Local", "Old/Remote"), "New/Remote", Side.Dropbox),
                null, new[] { (Side.Local, "Old/Local"), (Side.Dropbox, "Old/Remote"),
                    (Side.Dropbox, "New/Remote") }),
            ("replace target already observed", Make(ActionKind.ReplaceKind,
                    ShapeRow("A/B", "A/C", null, null), "A/B", Side.Local),
                null, new[] { (Side.Local, "A/B"), (Side.Dropbox, "A/C") }),
            ("ensure local folder", Make(ActionKind.EnsureLocalFolder,
                    ShapeRow("D/E", null, null, null, "D/E")),
                null, new[] { (Side.Local, "D/E"), (Side.Dropbox, "D/E") }),
            ("notice", Make(ActionKind.Notice, ShapeRow("F/G", "F/H", null, null)),
                null, new[] { (Side.Local, "F/G"), (Side.Dropbox, "F/H") }),
            ("defer folder", Make(ActionKind.DeferFolder, ShapeRow("I/J", "I/K", null, null, "I/J")),
                null, new[] { (Side.Local, "I/J"), (Side.Dropbox, "I/K") }),
            ("forget only", Make(ActionKind.ForgetOnly, ShapeRow("L/M", "L/N", "L/M", "L/N")),
                null, new[] { (Side.Local, "L/M"), (Side.Dropbox, "L/N") })
        };
        Check.Require(shapes.Length == 16, "the M29 footprint shape table keeps all sixteen measured shapes");
        foreach (var (name, action, labels, paths) in shapes)
            RequireKeys(LockKeys.For(action, pair, labels), pair, paths,
                $"LockKeys.For keeps the real key set for {name}");

        RequireKeys(LockKeys.For(Make(ActionKind.Upload,
                ShapeRow("Top/Local", "Top/Remote", "top/local", "top/remote")), pair, null), pair, originals,
            "A case-variant saved spelling normalizes to the same real keys");
        RequireKeys(LockKeys.For(Make(ActionKind.Record,
                ShapeRow("Moved/Local", "Moved/Remote", "Old/Local", "Old/Remote")), pair, null), pair,
            new[] { (Side.Local, "Moved/Local"), (Side.Dropbox, "Moved/Remote"),
                (Side.Local, "Old/Local"), (Side.Dropbox, "Old/Remote") },
            "All-distinct observed and saved spellings keep every real key");
    }

    private static void DirectFootprints(PairConfig pair)
    {
        var unchanged = new[]
        {
            (Side.Local, "Top/OldLocal"), (Side.Dropbox, "Top/OldRemote"),
            (Side.Local, "Top/NewLocal"), (Side.Dropbox, "Top/NewRemote"),
            (Side.Local, "Top/OldLocal"), (Side.Dropbox, "Top/OldRemote")
        };
        var unchangedKeys = LockKeys.ForPaths(pair, unchanged);
        RequireKeys(unchangedKeys, pair, unchanged, "ForPaths keeps the unchanged direct footprint");
        Check.Require(unchangedKeys.Count == 4, "ForPaths deduplicates repeated direct paths");

        var cases = new (string Name, (Side Side, string Path)[] Paths)[]
        {
            ("rename", new[] { (Side.Local, "Old/Local"), (Side.Local, "New/Local"),
                (Side.Dropbox, "Old/Remote"), (Side.Dropbox, "New/Remote") }),
            ("folder plan", new[] { (Side.Local, "Folder"), (Side.Dropbox, "Folder") }),
            ("exclusion", new[] { (Side.Local, "Excluded"), (Side.Dropbox, "Excluded") }),
            ("resolution", new[] { (Side.Local, "Subject/Local"), (Side.Dropbox, "Subject/Remote"),
                (Side.Local, "Renamed/Incoming"), (Side.Local, "Label/Local"), (Side.Dropbox, "Label/Remote") }),
            ("duplicate and mapped", new[] { (Side.Local, "Folder/Name_"), (Side.Local, "folder/name_"),
                (Side.Dropbox, "Folder/Name:"), (Side.Dropbox, "folder/name_") }),
            ("roots", new[] { (Side.Local, ""), (Side.Dropbox, "") }),
            ("empty", Array.Empty<(Side, string)>())
        };
        foreach (var (name, paths) in cases)
        {
            RequireKeys(LockKeys.ForPaths(pair, paths), pair, paths,
                $"ForPaths uses real-location keys for {name}");
        }
    }

    private static void PairAndAccount(PairConfig pair, Row row)
    {
        var originals = new[] { (Side.Local, row.Local!.Path), (Side.Dropbox, row.Dropbox!.Path) };
        var shared = pair with { PairId = "pair-b", Name = "Pair B" };
        Check.Require(LockKeys.ForPaths(pair, originals).ToHashSet(StringComparer.Ordinal)
            .SetEquals(LockKeys.ForPaths(shared, originals)),
            "Different pairs sharing roots produce the same lock keys");
        var otherConnection = pair with { ConnectionId = "connection-b" };
        var remotePaths = new[] { (Side.Dropbox, row.Dropbox!.Path) };
        Check.Require(!LockKeys.ForPaths(pair, remotePaths).Intersect(LockKeys.ForPaths(otherConnection,
            remotePaths), StringComparer.Ordinal).Any(), "Different Dropbox accounts do not share keys");
    }

    private static Row ShapeRow(string? localPath, string? dropboxPath, string? syncedLocal,
        string? syncedDropbox, string? destination = null) =>
        new("row-key", localPath is null ? null : EntryAt(localPath),
            dropboxPath is null ? null : EntryAt(dropboxPath),
            syncedLocal is null || syncedDropbox is null ? null : Synced(syncedLocal, syncedDropbox),
            Array.Empty<Entry>(), null,
            new RowContext(false, false, false, null, false, false, false, false, destination));

    private static SyncedEntry Synced(string localPath, string dropboxPath) =>
        new("local-key", localPath, "dropbox-key", dropboxPath, false, "local-id", 0, 0,
            "dropbox-id", "content-hash");

    private static Entry EntryAt(string path) =>
        new(path, false, 0, DateTimeOffset.UnixEpoch, "id", "revision", "hash", null);

    private static string Copy(string value) => new(value.ToCharArray());

    private static void RequireKeys(IReadOnlyCollection<string> actual, PairConfig pair,
        IEnumerable<(Side Side, string Path)> paths, string message)
    {
        var expected = paths.Select(path => path.Side == Side.Local
            ? PathLocks.LocalKey(pair, path.Path)
            : PathLocks.DropboxKey(pair, path.Path)).ToHashSet(StringComparer.Ordinal);
        Check.Require(actual.Count == expected.Count && expected.SetEquals(actual), message);
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        Check.Require(false, message);
    }
}
