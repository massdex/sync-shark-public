using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks for the pure exclusion policy (`--engine-checks exclusions`), written by Exclusions's writer.</summary>
public static class ExclusionsChecks
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly RowContext Destination = new(false, false, false, null, false, false, false, false, "dest");

    public static void Run()
    {
        ActionCases();
        BlockedCases();
        PreviewCases();
        ListingCases();
    }

    private static void ActionCases()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var row = new Row("k", null, null, null, Array.Empty<Entry>(), null, Destination);
            var target = PathNames.Other(side);
            foreach (var folder in new[] { false, true })
            {
                var unsaved = FileEntry("a", "id", folder: folder);
                var action = Exclusions.For(side, unsaved, row, null)!;
                Check.Require(action.Kind == SyncAction.CopyOn(folder, target) && ReferenceEquals(action.Row, row)
                    && action.Target == "dest", $"an unsaved {side} exclusion copies onto {target}");
            }

            // The reverse-copy guard applies to exactly the four copy/delete kinds whose written side is the excluded one.
            foreach (var skippedKind in new[] { ActionKind.Upload, ActionKind.Download, ActionKind.DeleteLocal,
                         ActionKind.DeleteDropbox })
            {
                var writesExcluded = new SyncAction(skippedKind, row).WrittenSide() == side;
                var decided = Exclusions.For(side, FileEntry("a", "id"), row, new SyncAction(skippedKind, row));
                Check.Require(writesExcluded == (decided is null),
                    $"{skippedKind} on an exclusion of {side} is skipped only when it writes the excluded side");
            }

            // Ensure actions are not in the skip guard: the outward one is preserved, the reverse one becomes a failure.
            var away = new SyncAction(side == Side.Local ? ActionKind.EnsureDropboxFolder : ActionKind.EnsureLocalFolder,
                row);
            Check.Require(ReferenceEquals(Exclusions.For(side, FolderEntry("a", "fid"), row, away), away),
                $"an ensuring action away from the excluded {side} side is preserved");
            var back = new SyncAction(side == Side.Local ? ActionKind.EnsureLocalFolder : ActionKind.EnsureDropboxFolder,
                row);
            RequireFailure(Exclusions.For(side, FolderEntry("a", "fid"), row, back),
                $"an ensuring action back onto the excluded {side} side is a failure, not a new skip");

            // DeferFolder is skipped only when the other side has no entry.
            Check.Require(Exclusions.For(side, FolderEntry("a", "fid"), row,
                new SyncAction(ActionKind.DeferFolder, row)) is null,
                $"a {side} defer with no other-side entry is skipped");
            var presentOther = side == Side.Local
                ? new Row("k", null, FileEntry("b", "bid"), null, Array.Empty<Entry>(), null, Destination)
                : new Row("k", FileEntry("b", "bid"), null, null, Array.Empty<Entry>(), null, Destination);
            RequireFailure(Exclusions.For(side, FolderEntry("a", "fid"), presentOther,
                new SyncAction(ActionKind.DeferFolder, presentOther)),
                $"a {side} defer with an other-side entry is a failure");

            var replace = Exclusions.For(side, FileEntry("a", "id"), row,
                new SyncAction(ActionKind.ReplaceKind, row, Side: side));
            Check.Require(replace is { Kind: ActionKind.Notice, NoticeKind: IssueKind.NameClash,
                Cause: TextKey.ClashFileFolder }, $"a {side} replace-kind becomes a file/folder name clash");

            // The expected copy kind, best-case actions and a settled no-op are preserved as they are.
            var kind = SyncAction.CopyOn(false, target);
            foreach (var preserved in new[]
                     {
                         new SyncAction(kind, row), new SyncAction(ActionKind.Record, row),
                         new SyncAction(ActionKind.KeepBoth, row),
                         new SyncAction(ActionKind.Notice, row, NoticeKind: IssueKind.Info, Cause: TextKey.InfoKeptBoth),
                         new SyncAction(ActionKind.None, row, NoneReason.InSync)
                     })
                Check.Require(ReferenceEquals(Exclusions.For(side, FileEntry("a", "id"), row, preserved), preserved),
                    $"{preserved.Kind} with the expected copy kind is preserved for a {side} exclusion");

            RequireFailure(Exclusions.For(side, FileEntry("a", "id"), row,
                    new SyncAction(ActionKind.None, row, NoneReason.OutOfScope)),
                $"an {side} exclusion turns an unsettled no-op into a failure so no issue closes");
            RequireFailure(Exclusions.For(side, FileEntry("a", "id"), row,
                    new SyncAction(ActionKind.MoveDropbox, row)),
                $"an unexpected {side} action kind is a failure");
        }
    }

    private static void RequireFailure(SyncAction? action, string what) =>
        Check.Require(action is { Kind: ActionKind.Notice, NoticeKind: IssueKind.Failure, Cause: TextKey.FailChanged },
            what);

    private static void BlockedCases()
    {
        var row = new Row("k", null, null, null, Array.Empty<Entry>(), null, Destination);
        var blocked = Exclusions.Blocked(row);
        Check.Require(blocked.Kind == ActionKind.Notice && blocked.NoticeKind == IssuePolicy.ProtectedExclusion.Kind
            && blocked.Cause == IssuePolicy.ProtectedExclusion.Cause && ReferenceEquals(blocked.Row, row),
            "the blocked notice uses the protected-exclusion failure on the supplied row");
    }

    private static void PreviewCases()
    {
        var file = FileEntry("a", "id");
        Check.Require(Exclusions.MayCopyFile(file, null, Side.Local), "an unsaved item may need a copy");
        Check.Require(Exclusions.MayCopyFile(file, Synced("a", "a", true), Side.Local),
            "a saved folder may need a copy");

        var unchanged = Synced("a", "a", false, localId: "id", size: 1, hash: file.ContentHash);
        Check.Require(!Exclusions.MayCopyFile(file, unchanged, Side.Local),
            "an unchanged local file is not a potential copy");
        Check.Require(!Exclusions.MayCopyFile(file, unchanged, Side.Dropbox),
            "an unchanged Dropbox file is not a potential copy");
        Check.Require(Exclusions.MayCopyFile(file, Synced("a", "a", false, "id", 2, file.ContentHash), Side.Local),
            "a resized local file may need a copy");
        Check.Require(Exclusions.MayCopyFile(file, Synced("a", "a", false, "other", 1, file.ContentHash), Side.Local),
            "unknown local content counts as a potential copy without hashing");
        Check.Require(Exclusions.MayCopyFile(file, Synced("a", "a", false, "id", 1, "different"), Side.Dropbox),
            "a changed Dropbox file may need a copy");
    }

    private static void ListingCases()
    {
        var folder = FolderEntry("a", "id");
        Check.Require(Exclusions.ShouldList(folder), "an ordinary folder is listed");
        Check.Require(!Exclusions.ShouldList(FileEntry("a", "id")), "a file is not listed");
        Check.Require(!Exclusions.ShouldList(FileEntry("a", "id", skip: "skip")),
            "a skipped item is not listed");
        Check.Require(!Exclusions.ShouldList(FolderEntry("a", "id", skip: "skip")),
            "a skipped folder is not listed");
    }

    private static Entry FileEntry(string path, string id, bool folder = false, string? skip = null) =>
        new(path, folder, folder ? 0 : 1, T0, id, "", folder ? "" : "h1", skip);

    private static Entry FolderEntry(string path, string id, string? skip = null) =>
        new(path, true, 0, T0, id, "", "", skip);

    private static SyncedEntry Synced(string localPath, string dropboxPath, bool isFolder, string localId = "id",
        long size = 1, string hash = "h1") =>
        new(PathNames.Key(localPath), localPath, PathNames.DropboxKey(dropboxPath), dropboxPath, isFolder, localId,
            size, T0.UtcTicks, localId + "d", hash);
}
