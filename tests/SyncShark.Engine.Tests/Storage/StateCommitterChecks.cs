using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Mapping preservation and independent atomic outcome checks for StateCommitter.</summary>
public static class StateCommitterChecks
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-state-committer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var connection = new SqliteConnection("Data Source=" + Path.Combine(folder, "state.db"));
            StateStore.OpenConnection(connection);
            Exec(connection, StateSchema.Create);
            Exec(connection, StateSchema.SchemaAdditions);
            Exec(connection, StateSchema.DeletedHistorySchema + StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames + StateSchema.SpentDeletedHistory + StateSchema.LocalPublishedColumns);
            Exec(connection, StateSchema.AttachScan, (StateSchema.ParamPath, StateSchema.EmptyScan));
            Exec(connection, StateSchema.ScanPragmas);
            Exec(connection, StateSchema.CreateScan);
            FileRename(connection);
            FolderRename(connection);
            FolderRebaseMatrix(connection);
            DisjointFolderRanges(connection);
            NestedFolderMoves(connection);
            FolderCollisionRollback(connection);
            InvalidLocalKeyDiagnostic(connection);
            CaseOnlyRename(connection);
            RenameWithoutRecord(connection);
            FolderRenameWithoutRecord(connection);
            MissingFolderCounterpartMatrix(connection);
            Forgets(connection);
            BilateralForgetScale(connection);
            Records(connection);
            RetireStaleRecord(connection);
            RecordConflicts(connection);
            ReleasedBindings(connection);
            EqualKeyReplacement(connection);
            PartialCounterpart(connection);
            AcknowledgesBeforeClose(connection);
            SameKeyUpdatePreservation(connection);
            Atomicity(connection);
            PendingCustodyAndCommitCallbacks();
            PendingEncodingMatrix();
            MappedUpgradeAndRestart();
            QueuedRecordIsolationAsync().GetAwaiter().GetResult();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    private static void BilateralForgetScale(SqliteConnection connection)
    {
        int? baseline = null;
        foreach (var children in new[] { 1, 10_000 })
        {
            var pair = "bilateral-" + children;
            Pair(connection, pair);
            using (var transaction = connection.BeginTransaction())
            {
                for (var i = 0; i < children; i++)
                    StateCommitter.Insert(connection, transaction, pair, new SyncedEntry(
                        PathNames.Key("Old/" + i), "Old/" + i, PathNames.DropboxKey("Remote/" + i), "Remote/" + i,
                        false, "l" + i, 0, 0, "d" + i, ""), null);
                StateCommitter.Insert(connection, transaction, pair, new SyncedEntry(
                    PathNames.Key("Old/Live"), "Old/Live", PathNames.DropboxKey("Elsewhere/Live"), "Elsewhere/Live",
                    false, "live-local", 0, 0, "live-cloud", ""), null);
                transaction.Commit();
            }
            // One set-based spend plus one delete, independent of descendant count.
            Check.Require(StateSchema.DeleteSyncedBothSubtrees.Split(';', StringSplitOptions.RemoveEmptyEntries).Length == 2,
                "Bilateral forgetting spends and deletes in two set-based statements at either size.");
            var statements = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
            try
            {
                Commit(connection, pair, StateChange.Empty with
                    { ForgetBothSubtrees = [(PathNames.Key("Old"), PathNames.DropboxKey("Remote"))] });
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            baseline ??= statements;
            Check.Require(statements > 0 && statements == baseline,
                "Observed SQLite statement count does not grow with the descendant count.");
            Console.WriteLine($"bilateral-forget children={children}; sqlite-statements={statements}");
            Check.Require(Rows(connection, pair) is [var kept] && kept.LocalPath == "Old/Live",
                "Set-based bilateral forgetting removes ordinary children but preserves the cross-moved child at scale.");
        }
    }

    private static void FileRename(SqliteConnection connection)
    {
        const string pair = "file";
        Pair(connection, pair);
        var before = new SyncedEntry(PathNames.Key("old.txt"), "old.txt", PathNames.DropboxKey("old.txt"), "old.txt",
            false, "L1", 10, 100, "D1", "H1");
        Synced(connection, pair, before);
        var after = before with
        {
            LocalKey = PathNames.Key("new.txt"),
            LocalPath = "new.txt",
            DropboxKey = PathNames.DropboxKey("New.txt"),
            DropboxPath = "New.txt",
            LocalId = "L2",
            DropboxId = "D2",
        };

        Commit(connection, pair, StateChange.Empty with { Renames = [new RenameApplied(before, after, null, null, null)] });

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 1, "file rename keeps one row");
        var row = rows[0];
        Check.Require(row.LocalKey == PathNames.Key("new.txt") && row.LocalPath == "new.txt",
            "file rename rewrites the local path and key");
        Check.Require(row.DropboxKey == PathNames.DropboxKey("New.txt") && row.DropboxPath == "New.txt",
            "file rename rewrites the dropbox path and key");
        Check.Require(row.LocalId == "L2" && row.DropboxId == "D2", "file rename takes the ids of After");
        Check.Require(row.LocalSize == 10 && row.LocalModifiedTicks == 100 && row.DropboxContentHash == "H1",
            "file rename keeps the saved content stamps");
    }

    private static void FolderRename(SqliteConnection connection)
    {
        const string pair = "folder";
        Pair(connection, pair);
        var before = new SyncedEntry(PathNames.Key("Docs"), "Docs", PathNames.DropboxKey("Docs"), "Docs",
            true, "F1", 0, 0, "FD1", "");
        var child = new SyncedEntry(PathNames.Key("Docs/a.txt"), "Docs/a.txt",
            PathNames.DropboxKey("Docs/a.txt"), "Docs/a.txt", false, "L1", 5, 50, "D1", "H1");
        var sibling = new SyncedEntry(PathNames.Key("Docs2/b.txt"), "Docs2/b.txt",
            PathNames.DropboxKey("Docs2/b.txt"), "Docs2/b.txt", false, "L2", 6, 60, "D2", "H2");
        Synced(connection, pair, before);
        Synced(connection, pair, child);
        Synced(connection, pair, sibling);
        Scan(connection, pair, Side.Dropbox, "Docs", true);
        Scan(connection, pair, Side.Dropbox, "Docs/a.txt", false);
        Scan(connection, pair, Side.Dropbox, "Docs2/b.txt", false);

        var after = before with
        {
            LocalKey = PathNames.Key("Papers"),
            LocalPath = "Papers",
            DropboxKey = PathNames.DropboxKey("Papers"),
            DropboxPath = "Papers",
            LocalId = "F2",
            DropboxId = "FD2",
        };
        Commit(connection, pair, StateChange.Empty with
        {
            Renames = [new RenameApplied(before, after, Side.Dropbox, "Docs", "Papers")],
        });

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 3, "folder rename keeps every row");
        var moved = rows.Single(row => row.IsFolder);
        Check.Require(moved.LocalKey == PathNames.Key("Papers") && moved.DropboxKey == PathNames.DropboxKey("Papers"),
            "folder rename rewrites the folder row");
        var movedChild = rows.Single(row => row.LocalPath != "Papers" && row.LocalPath != "Docs2/b.txt");
        Check.Require(movedChild.LocalKey == PathNames.Key("Papers/a.txt") && movedChild.LocalPath == "Papers/a.txt",
            "folder rename rebases the descendant local path and key");
        Check.Require(movedChild.DropboxKey == PathNames.DropboxKey("Papers/a.txt") &&
            movedChild.DropboxPath == "Papers/a.txt", "folder rename rebases the descendant dropbox path and key");
        Check.Require(rows.Any(row => row.LocalPath == "Docs2/b.txt"), "folder rename leaves a similar prefix alone");

        var scans = Scans(connection, pair);
        Check.Require(scans.Count == 3, "counterpart scan move keeps every row");
        Check.Require(scans.Any(row => row.OwnKey == PathNames.Key("Papers") && row.Path == "Papers" &&
            row.Key == PathNames.DropboxKey("Papers")), "counterpart scan move moves the folder row");
        Check.Require(scans.Any(row => row.OwnKey == PathNames.Key("Papers/a.txt") && row.Path == "Papers/a.txt" &&
            row.Key == PathNames.DropboxKey("Papers/a.txt")), "counterpart scan move moves the descendant");
        Check.Require(scans.Any(row => row.OwnKey == PathNames.Key("Docs2/b.txt")),
            "counterpart scan move leaves a similar prefix alone");
    }

    private static void FolderRebaseMatrix(SqliteConnection connection)
    {
        foreach (var (name, oldDropbox, newLocal, newDropbox) in new[]
        {
            ("unchanged", "Docs", "Docs", "Docs"),
            ("local", "Docs", "Papers", "Docs"),
            ("dropbox", "Docs", "Docs", "Papers"),
            ("both", "Docs", "Papers", "Papers"),
            ("local-case", "Docs", "docs", "Docs"),
            ("dropbox-case", "Docs", "Docs", "docs"),
            ("both-case", "Docs", "docs", "docs"),
            ("mapped-root", "R:oot", "Papers", "Papers"),
        })
        {
            var pair = "rebase-" + name;
            Pair(connection, pair);
            var before = Saved("Docs", oldDropbox, "root", true);
            var after = Repath(before, newLocal, newDropbox);
            var children = new[]
            {
                (Entry: Saved("dOcS/Shared.txt", oldDropbox.ToLowerInvariant() + "/Shared.txt", "shared"),
                    LocalSuffix: "/Shared.txt", DropboxSuffix: "/Shared.txt"),
                (Entry: Saved("DOCS/Local.txt", "Away/Local.txt", "local"),
                    LocalSuffix: "/Local.txt", DropboxSuffix: (string?)null),
                (Entry: Saved("Elsewhere/Remote.txt", oldDropbox + "/Remote.txt", "remote"),
                    LocalSuffix: (string?)null, DropboxSuffix: "/Remote.txt"),
                (Entry: Saved("Docs/Mapped_Name.txt", oldDropbox + "/Mapped:Name.txt", "mapped"),
                    LocalSuffix: "/Mapped_Name.txt", DropboxSuffix: "/Mapped:Name.txt"),
                (Entry: Saved("Docs/Linked.txt", oldDropbox + "/Different.txt", "linked"),
                    LocalSuffix: "/Linked.txt", DropboxSuffix: "/Different.txt"),
                (Entry: Saved("Docs2/Keep.txt", oldDropbox + "2/Keep.txt", "sibling"),
                    LocalSuffix: (string?)null, DropboxSuffix: (string?)null),
            };
            Synced(connection, pair, before);
            foreach (var child in children) Synced(connection, pair, child.Entry);
            var expected = new List<SyncedEntry> { after };
            var changedDescendants = 0;
            foreach (var child in children)
            {
                var localChanged = newLocal != before.LocalPath && child.LocalSuffix is not null;
                var dropboxChanged = newDropbox != before.DropboxPath && child.DropboxSuffix is not null;
                if (localChanged || dropboxChanged) changedDescendants++;
                expected.Add(Repath(child.Entry,
                    localChanged ? newLocal + child.LocalSuffix : child.Entry.LocalPath,
                    dropboxChanged ? newDropbox + child.DropboxSuffix : child.Entry.DropboxPath));
            }
            var changes = Count(connection, "SELECT total_changes()");

            Commit(connection, pair, StateChange.Empty with
            {
                Renames = [new RenameApplied(before, after, null, null, null)],
            });

            Check.Require(Count(connection, "SELECT total_changes()") - changes == 1 + changedDescendants,
                name + ": update the root and each member of the old-range union exactly once");
            RequireRows(connection, pair, expected.ToArray());
        }
    }

    private static void DisjointFolderRanges(SqliteConnection connection)
    {
        const string pair = "disjoint-old-ranges";
        Pair(connection, pair);
        var root = Saved("Docs", "Remote", "root", true);
        var local = Saved("Docs/Local", "Away/Local", "local");
        var remote = Saved("Elsewhere/Remote", "Remote/Remote", "remote");
        var inherited = Saved("Remote/Inherited", "Remote/Inherited", "inherited");
        var sibling = Saved("Docs2/Keep", "Remote2/Keep", "sibling");
        foreach (var entry in new[] { root, local, remote, inherited, sibling }) Synced(connection, pair, entry);
        var after = Repath(root, "Remote", "Final");
        var changes = Count(connection, "SELECT total_changes()");

        Commit(connection, pair, StateChange.Empty with
        {
            Renames = [new RenameApplied(root, after, null, null, null)],
        });

        RequireRows(connection, pair, after, Repath(local, "Remote/Local", "Away/Local"),
            Repath(remote, "Elsewhere/Remote", "Final/Remote"),
            Repath(inherited, "Remote/Inherited", "Final/Inherited"), sibling);
        Check.Require(Count(connection, "SELECT total_changes()") - changes == 4,
            "the root and three disjoint old-range descendants update once despite overlapping new/old roots");
    }

    private static void NestedFolderMoves(SqliteConnection connection)
    {
        const string pair = "nested-moves";
        Pair(connection, pair);
        var root = Saved("Docs", "Remote", "root", true);
        var branch = Saved("Docs/Branch", "Remote/Branch", "branch", true);
        var child = Saved("Docs/Branch/File", "Remote/Branch/File", "child");
        var movedAway = Saved("Elsewhere/File", "Remote/Branch/Away", "away");
        foreach (var entry in new[] { root, branch, child, movedAway }) Synced(connection, pair, entry);
        var movedRoot = Repath(root, "Papers", "Cloud");
        var rebasedBranch = Repath(branch, "Papers/Branch", "Cloud/Branch");
        var movedBranch = Repath(branch, "Papers/Renamed", "Cloud/Renamed");
        var changes = Count(connection, "SELECT total_changes()");

        Commit(connection, pair, StateChange.Empty with
        {
            Renames =
            [
                new RenameApplied(root, movedRoot, null, null, null),
                new RenameApplied(rebasedBranch, movedBranch, null, null, null),
            ],
        });

        RequireRows(connection, pair, movedRoot, movedBranch,
            Repath(child, "Papers/Renamed/File", "Cloud/Renamed/File"),
            Repath(movedAway, "Elsewhere/File", "Cloud/Renamed/Away"));
        Check.Require(Count(connection, "SELECT total_changes()") - changes == 7,
            "nested moves update the root and three descendants, then the branch and two descendants");
    }

    private static void FolderCollisionRollback(SqliteConnection connection)
    {
        foreach (var kind in new[] { "local", "dropbox", "inherited", "override", "scan" })
        {
            var pair = "folder-collision-" + kind;
            Pair(connection, pair);
            var root = Saved("Old", "Remote", "root", true);
            var child = Saved("Old/Child", "Remote/Child", "child");
            var first = Saved("Before.txt", "Before.txt", "first");
            var after = Repath(root, "New", "New");
            var occupied = kind switch
            {
                "local" => Saved("New/Child", "Away/Child", "occupied"),
                "dropbox" => Saved("Elsewhere/Child", "Target/Child", "occupied"),
                "inherited" => Saved("New/Child", "New/Child", "occupied"),
                _ => Saved("Elsewhere/Child", "New/Child", "occupied"),
            };
            if (kind == "dropbox") after = Repath(root, "New", "Target");
            if (kind == "inherited") after = Repath(root, "Other", "New");
            var original = kind == "scan" ? new[] { root, child, first } : new[] { root, child, first, occupied };
            foreach (var entry in original) Synced(connection, pair, entry);
            Scan(connection, pair, Side.Dropbox, "Before.txt", false);
            Scan(connection, pair, Side.Dropbox, "Remote", true);
            Scan(connection, pair, Side.Dropbox, "Remote/Child", false);
            if (kind == "scan") Scan(connection, pair, Side.Dropbox, "New/Child", false);
            var scans = Scans(connection, pair);

            using (var transaction = connection.BeginTransaction())
            {
                Exception? failure = null;
                try
                {
                    StateCommitter.Commit(connection, transaction, new ChangeTable(connection, writable: false), pair, StateChange.Empty with
                    {
                        Renames =
                        [
                            new RenameApplied(first, Repath(first, "After.txt", "After.txt"),
                                Side.Dropbox, "Before.txt", "After.txt"),
                            new RenameApplied(root, after, Side.Dropbox, "Remote", after.DropboxPath),
                        ],
                    }, At, narrowRecordUpdates: true, raised: null, scans: [Guid.Empty.ToByteArray()]);
                }
                catch (SqliteException error) { failure = error; }
                catch (InvalidOperationException error) { failure = error; }
                transaction.Rollback();
                if (kind is "inherited" or "override")
                    Check.Require(failure is InvalidOperationException &&
                        failure.Message == Text.Get(TextKey.ClashNameTaken, (TextArg.Path, after.DropboxPath)),
                        kind + ": the final clash probe rejects inherited/override collisions");
                else
                    Check.Require(failure is SqliteException { SqliteExtendedErrorCode: 2067 },
                        kind + ": the unique constraint rejects the destination collision");
            }

            RequireRows(connection, pair, original);
            Check.Require(Scans(connection, pair).SequenceEqual(scans),
                kind + ": rollback restores every scan row, including the earlier successful move");
        }
    }

    private static void InvalidLocalKeyDiagnostic(SqliteConnection connection)
    {
        const string pair = "invalid-key-diagnostic";
        Pair(connection, pair);
        var keep = Saved("Keep.txt", "Keep.txt", "keep");
        var incoming = Saved("Incoming.txt", "Incoming.txt", "incoming");
        var invalid = incoming with { LocalKey = "WRONG" };
        Synced(connection, pair, keep);
        foreach (var change in new[]
        {
            StateChange.Empty with { Record = [invalid] },
            StateChange.Empty with { Renames = [new RenameApplied(null, invalid, null, null, null)] },
            StateChange.Empty with { Renames = [new RenameApplied(keep, invalid, null, null, null)] },
            StateChange.Empty with { Renames = [new RenameApplied(invalid, incoming, null, null, null)] },
        })
        {
            var changes = Count(connection, "SELECT total_changes()");
            using var transaction = connection.BeginTransaction();
            ArgumentException? failure = null;
            try
            {
                StateCommitter.Commit(connection, transaction, new ChangeTable(connection, writable: false), pair,
                    change with { ForgetSelf = [(Side.Local, keep.LocalKey)] }, At,
                    narrowRecordUpdates: true, raised: null, scans: [Guid.Empty.ToByteArray()]);
            }
            catch (ArgumentException error) { failure = error; }
            transaction.Rollback();
            var diagnostic = Text.Get(TextKey.StateLocalKeyInvalid, (TextArg.Path, invalid.LocalKey));
            Check.Require(failure is not null && failure.ParamName == "entry" &&
                failure.Message == new ArgumentException(diagnostic, "entry").Message,
                "invalid local keys retain ArgumentException/entry and render the storage-specific path diagnostic");
            Check.Require(Count(connection, "SELECT total_changes()") == changes,
                "invalid keys are refused before any mutation, not merely undone by rollback");
            RequireRows(connection, pair, keep);
        }
    }

    private static SyncedEntry Saved(string localPath, string dropboxPath, string id, bool folder = false) =>
        new(PathNames.Key(localPath), localPath, PathNames.DropboxKey(dropboxPath), dropboxPath,
            folder, "local-" + id, 7, 11, "dropbox-" + id, "hash-" + id);

    private static SyncedEntry Repath(SyncedEntry entry, string localPath, string dropboxPath) =>
        entry with
        {
            LocalKey = PathNames.Key(localPath), LocalPath = localPath,
            DropboxKey = PathNames.DropboxKey(dropboxPath), DropboxPath = dropboxPath,
        };

    private static void RequireRows(SqliteConnection connection, string pair, params SyncedEntry[] expected) =>
        Check.Require(Rows(connection, pair).SequenceEqual(expected.OrderBy(row => row.LocalKey, StringComparer.Ordinal)),
            pair + ": every saved path, key, identity and content stamp matches exactly");

    private static void CaseOnlyRename(SqliteConnection connection)
    {
        const string pair = "case";
        Pair(connection, pair);
        var before = new SyncedEntry(PathNames.Key("Photo.JPG"), "Photo.JPG",
            PathNames.DropboxKey("Photo.JPG"), "Photo.JPG", false, "L1", 10, 100, "D1", "H1");
        Synced(connection, pair, before);
        var after = before with { LocalPath = "photo.jpg", DropboxPath = "photo.jpg", LocalId = "L2", DropboxId = "D2" };

        Commit(connection, pair, StateChange.Empty with { Renames = [new RenameApplied(before, after, null, null, null)] });

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 1, "case-only rename keeps one row");
        Check.Require(rows[0].LocalPath == "photo.jpg" && rows[0].DropboxPath == "photo.jpg",
            "case-only rename updates the paths");
        Check.Require(rows[0].LocalKey == PathNames.Key("Photo.JPG") &&
            rows[0].DropboxKey == PathNames.DropboxKey("Photo.JPG"), "case-only rename keeps the keys");
        Check.Require(rows[0].LocalId == "L2" && rows[0].DropboxId == "D2", "case-only rename takes the ids of After");
    }

    private static void RenameWithoutRecord(SqliteConnection connection)
    {
        const string pair = "norecord";
        Pair(connection, pair);
        var after = new SyncedEntry(PathNames.Key("spelled.txt"), "spelled.txt",
            PathNames.DropboxKey("spelled.txt"), "spelled.txt", false, "L1", 10, 100, "D1", "H1");

        Commit(connection, pair,
            StateChange.Empty with { Renames = [new RenameApplied(null, after, null, null, null)] });

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 1 && rows[0] == after, "a rename without a prior row records After");
    }

    private static void FolderRenameWithoutRecord(SqliteConnection connection)
    {
        const string pair = "norecord-folder";
        Pair(connection, pair);
        var child = new SyncedEntry(PathNames.Key("Docs/a.txt"), "Docs/a.txt",
            PathNames.DropboxKey("Docs/a.txt"), "Docs/a.txt", false, "L1", 5, 50, "D1", "H1");
        Synced(connection, pair, child);
        var after = new SyncedEntry(PathNames.Key("Papers"), "Papers", PathNames.DropboxKey("Docs"), "Docs",
            true, "F1", 0, 0, "FD1", "");

        Commit(connection, pair, StateChange.Empty with
        {
            Renames = [new RenameApplied(null, after, Side.Local, "Docs", "Papers")],
        });

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 2, "a folder move with no record keeps the saved child");
        var movedChild = rows.Single(row => !row.IsFolder);
        Check.Require(movedChild.LocalKey == PathNames.Key("Papers/a.txt") &&
            movedChild.LocalPath == "Papers/a.txt", "the moved folder rebases the saved child's local path");
        Check.Require(movedChild.DropboxPath == "Docs/a.txt",
            "the moved folder leaves the unmoved side's saved child alone");
    }

    private static void MissingFolderCounterpartMatrix(SqliteConnection connection)
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var target in new[] { "Docs", "Papers", "docs" })
        {
            var pair = "missing-folder-" + side + "-" + target;
            Pair(connection, pair);
            var child = Saved("dOcS/Child.txt", "dOCS/Child.txt", "child");
            Synced(connection, pair, child);
            var after = Saved(side == Side.Local ? target : "Docs",
                side == Side.Dropbox ? target : "Docs", "root", true);
            var expectedChild = Repath(child,
                side == Side.Local && target != "Docs" ? target + "/Child.txt" : child.LocalPath,
                side == Side.Dropbox && target != "Docs" ? target + "/Child.txt" : child.DropboxPath);
            var changes = Count(connection, "SELECT total_changes()");

            Commit(connection, pair, StateChange.Empty with
            {
                Renames = [new RenameApplied(null, after, side, "Docs", target)],
            });

            Check.Require(Count(connection, "SELECT total_changes()") - changes == (target == "Docs" ? 1 : 2),
                "an absent folder inserts its root but never updates an unchanged counterpart's descendant");
            RequireRows(connection, pair, after, expectedChild);
        }
    }

    private static void Forgets(SqliteConnection connection)
    {
        const string pair = "forget";
        Pair(connection, pair);
        Synced(connection, pair, new SyncedEntry(PathNames.Key("A"), "A", PathNames.DropboxKey("A"), "A",
            true, "F1", 0, 0, "FD1", ""));
        Synced(connection, pair, new SyncedEntry(PathNames.Key("A/x.txt"), "A/x.txt",
            PathNames.DropboxKey("A/x.txt"), "A/x.txt", false, "L1", 1, 10, "D1", "H1"));
        Synced(connection, pair, new SyncedEntry(PathNames.Key("B"), "B", PathNames.DropboxKey("B"), "B",
            true, "F2", 0, 0, "FD2", ""));
        Synced(connection, pair, new SyncedEntry(PathNames.Key("B/x.txt"), "B/x.txt",
            PathNames.DropboxKey("B/x.txt"), "B/x.txt", false, "L2", 2, 20, "D2", "H2"));
        Synced(connection, pair, new SyncedEntry(PathNames.Key("AB"), "AB", PathNames.DropboxKey("AB"), "AB",
            false, "L3", 3, 30, "D3", "H3"));

        Commit(connection, pair, StateChange.Empty with
        {
            ForgetSelf = [(Side.Local, PathNames.Key("A"))],
            ForgetSubtrees = [(Side.Dropbox, PathNames.DropboxKey("B"))],
        });

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 2, "forgets leave only the untouched rows");
        Check.Require(rows.Any(row => row.LocalPath == "A/x.txt"), "ForgetSelf deletes only its own row");
        Check.Require(rows.Any(row => row.LocalPath == "AB"), "ForgetSubtrees respects the segment boundary");
    }

    private static void Records(SqliteConnection connection)
    {
        const string pair = "record";
        Pair(connection, pair);
        Synced(connection, pair, new SyncedEntry(PathNames.Key("a.txt"), "a.txt", PathNames.DropboxKey("a.txt"),
            "a.txt", false, "L1", 1, 10, "D1", "H1"));
        var replacement = new SyncedEntry(PathNames.Key("b.txt"), "b.txt", PathNames.DropboxKey("a.txt"), "a.txt",
            false, "L2", 2, 20, "D2", "H2");

        Commit(connection, pair, StateChange.Empty with { Record = [replacement] });

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 1 && rows[0] == replacement,
            "a record replaces the row holding either of its keys");
    }

    private static void RetireStaleRecord(SqliteConnection connection)
    {
        foreach (var changed in new[] { false, true })
        {
            var pair = changed ? "retire-changed" : "retire-exact";
            Pair(connection, pair);
            var before = new SyncedEntry("OLD", "Old", "OLD", "Old", false, "local-id", 1, 10, "remote-id", "hash");
            var current = changed ? before with { LocalModifiedTicks = 20 } : before;
            Synced(connection, pair, current);
            var after = before with { LocalKey = "ARCHIVE/ITEM", LocalPath = "Archive/Item",
                DropboxKey = "ARCHIVE/ITEM", DropboxPath = "Archive/Item" };
            Commit(connection, pair, StateChange.Empty with { Record = [after], StaleBindings = [before] });
            var rows = Rows(connection, pair);
            Check.Require(rows.Contains(after) && rows.Contains(current) == changed && rows.Count == (changed ? 2 : 1),
                "Stale retirement compares the entire saved preimage before removing it in the Record transaction.");
        }
    }

    private static void RecordConflicts(SqliteConnection connection)
    {
        const string pair = "record-two-conflicts";
        Pair(connection, pair);
        var local = new SyncedEntry("A", "a", "B", "b", false, "l1", 1, 1, "d1", "h");
        var dropbox = local with { LocalKey = "C", LocalPath = "c", DropboxKey = "D", DropboxPath = "d" };
        var child = local with { LocalKey = "A/CHILD", LocalPath = "a/child", DropboxKey = "D/CHILD", DropboxPath = "d/child" };
        Synced(connection, pair, local);
        Synced(connection, pair, dropbox);
        Synced(connection, pair, child);
        var replacement = local with { DropboxKey = "D", DropboxPath = "d" };
        using (var transaction = connection.BeginTransaction())
        {
            try
            {
                StateCommitter.Commit(connection, transaction, new ChangeTable(connection, writable: false), pair, StateChange.Empty with
                {
                    Record = [replacement with { LocalId = null! }],
                }, At, narrowRecordUpdates: true, raised: null, scans: [Guid.Empty.ToByteArray()]);
                Check.Require(false, "a failed insert after both key deletions must fail");
            }
            catch (SqliteException)
            {
                transaction.Rollback();
            }
        }
        Check.Require(Rows(connection, pair).OrderBy(row => row.LocalKey).SequenceEqual(
                new[] { local, dropbox, child }.OrderBy(row => row.LocalKey)),
            "an insert failure restores both conflicting records and leaves descendants untouched");
        Commit(connection, pair, StateChange.Empty with { Record = [replacement] });
        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 2 && rows.Contains(replacement) && rows.Contains(child),
            "replacement deletes both conflicting self rows, never their descendants");
    }

    private static void ReleasedBindings(SqliteConnection connection)
    {
        const string pair = "returning-original";
        Pair(connection, pair);
        var a = Saved("Original", "RemotePartner", "near");
        var b = Saved("LocalPartner", "Original", "far");
        Synced(connection, pair, a);
        Synced(connection, pair, b);
        var after = a with { DropboxKey = b.DropboxKey, DropboxPath = b.DropboxPath, DropboxId = b.DropboxId };
        var change = StateChange.Empty with { ForgetSelf = [(Side.Dropbox, a.DropboxKey)], Record = [after] };
        var watermark = Count(connection, StateSchema.WorkWatermark);
        Exec(connection, "CREATE TEMP TRIGGER reject_release_notice BEFORE INSERT ON issue BEGIN SELECT RAISE(ABORT,'notice-failure'); END");
        try
        {
            Reject<SqliteException>(() => Commit(connection, pair, change with
            {
                Raise = [(IssueKind.Info, TextKey.InfoKeptBoth, "Original", new IssueSubject([], null))]
            }));
        }
        finally { Exec(connection, "DROP TRIGGER reject_release_notice"); }
        RequireRows(connection, pair, a, b);
        Check.Require(Count(connection, StateSchema.WorkWatermark) == watermark,
            "A failure after release registration rolls back bindings and both acquired work rows.");
        Commit(connection, pair, change);
        RequireRows(connection, pair, after);
        Check.Require(Count(connection, StateSchema.WorkWatermark) == watermark + 2,
            "ForgetSelf plus both-key deletion consumes every RETURNING result once.");
        Commit(connection, pair, change);
        Check.Require(Count(connection, StateSchema.WorkWatermark) == watermark + 2,
            "A replay cannot release endpoints whose crossed binding has already retired.");
        var folder = Saved("Folder", "OtherFolder", "folder", true);
        Synced(connection, pair, folder);
        Commit(connection, pair, StateChange.Empty with { Record = [folder with
            { DropboxKey = folder.LocalKey, DropboxPath = folder.LocalPath }] });
        Check.Require(Count(connection, StateSchema.WorkWatermark) == watermark + 2,
            "Folder bindings never create file-release work.");
    }

    private static void EqualKeyReplacement(SqliteConnection connection)
    {
        const string pair = "equal-keys";
        Pair(connection, pair);
        var inherited = new SyncedEntry(PathNames.Key("A"), "A", PathNames.DropboxKey("A"), "A",
            false, "L1", 1, 10, "D1", "H1");
        var overrideHolder = new SyncedEntry(PathNames.Key("B"), "B", PathNames.DropboxKey("A"), "A",
            false, "L2", 2, 20, "D2", "H2");
        Synced(connection, pair, inherited);
        Synced(connection, pair, overrideHolder);

        // Equal incoming keys skip the already-deleted inherited arm; the override arm must remain.
        Commit(connection, pair, StateChange.Empty with { Record = [inherited with { LocalSize = 3 }] });

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 1 && rows[0].LocalPath == "A" && rows[0].LocalSize == 3,
            "an equal-key replacement still removes an override holder of the Dropbox key");
    }

    private static void PartialCounterpart(SqliteConnection connection)
    {
        const string pair = "partial-counterpart";
        Pair(connection, pair);
        var child = new SyncedEntry(PathNames.Key("Docs/a.txt"), "Docs/a.txt",
            PathNames.DropboxKey("Docs/a.txt"), "Docs/a.txt", false, "L1", 1, 10, "D1", "H1");
        Synced(connection, pair, child);
        Scan(connection, pair, Side.Local, "Docs/a.txt", false);
        // Each incomplete counterpart must move neither the saved child nor its scan row.
        SyncedEntry Folder(string path) =>
            new(PathNames.Key(path), path, PathNames.DropboxKey(path), path, true, "F1", 0, 0, "FD1", "");
        foreach (var incomplete in new[]
        {
            new RenameApplied(null, Folder("PapersA"), null, "Docs", "PapersA"),
            new RenameApplied(null, Folder("PapersB"), Side.Local, null, "PapersB"),
            new RenameApplied(null, Folder("PapersC"), Side.Local, "Docs", null),
        })
        {
            Commit(connection, pair, StateChange.Empty with { Renames = [incomplete] });
            Check.Require(Rows(connection, pair).Any(row => row.LocalPath == "Docs/a.txt") &&
                Scans(connection, pair).Any(row => row.Side == (long)Side.Local && row.Path == "Docs/a.txt"),
                "a counterpart missing its side or a path moves neither saved nor scanned rows");
        }

        var folder = Folder("Papers");
        Commit(connection, pair, StateChange.Empty with
        {
            Renames = [new RenameApplied(null, folder, Side.Local, "Docs", "Papers")],
        });

        Check.Require(Rows(connection, pair).Any(row => row.LocalPath == "Papers/a.txt") &&
            !Rows(connection, pair).Any(row => row.LocalPath == "Docs/a.txt") &&
            Scans(connection, pair).Any(row => row.Side == (long)Side.Local &&
                row.OwnKey == PathNames.Key("Papers/a.txt") && row.Path == "Papers/a.txt"),
            "a complete counterpart moves the intended saved and scanned rows");
    }

    // Distinct neutral values exercise subject identity, alias spelling and content-stamp preservation.
    private const string OutcomePair = "outcome-order";
    private const string OutcomePath = "Parent/Item";
    private const string OutcomeOther = "Sibling/Item";
    private const string OutcomeId = "item-id";
    private const string OutcomeOtherId = "other-id";
    private const string OutcomeCheck = "atomic-outcome-contract";
    private const string MappedLocal = "Parent/A_B";
    private const string MappedRemote = "Parent/A:B";

    private static void AcknowledgesBeforeClose(SqliteConnection connection)
    {
        foreach (var marker in Enum.GetValues<IssueSubject.FirstRunDeletionKind>()
            .Where(value => value != IssueSubject.FirstRunDeletionKind.None))
        {
            var pair = OutcomePair + marker;
            Pair(connection, pair);
            var item = new IssueItem(Side.Local, OutcomePath, OutcomeId);
            var subject = new IssueSubject([item], null) { FirstRunDeletion = marker };
            Commit(connection, pair, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailChanged, OutcomePath, subject)]
            });
            var original = IssueTable.List(connection, pair).Single();
            Commit(connection, pair, StateChange.Empty with { Close = [(OutcomePath, [IssueKind.Failure])] });
            Check.Require(IssueTable.ById(connection, pair, original.Id) is not null, OutcomeCheck);
            foreach (var wrong in new[] { item with { Id = OutcomeOtherId }, item with { Path = OutcomeOther } })
            {
                Commit(connection, pair, StateChange.Empty with
                {
                    FirstRunAcknowledged = [wrong], Close = [(OutcomePath, [IssueKind.Failure])]
                });
                Check.Require(IssueTable.ById(connection, pair, original.Id)!.Subject.FirstRunDeletion == marker,
                    OutcomeCheck);
            }
            using (var transaction = connection.BeginTransaction())
            {
                StateCommitter.Commit(connection, transaction, new ChangeTable(connection, writable: false), pair, StateChange.Empty with
                {
                    FirstRunAcknowledged = [item], Close = [(OutcomePath, [IssueKind.Failure])],
                    Raise = [(IssueKind.Info, TextKey.InfoDropboxHistoryUnchecked, OutcomePath, subject)]
                }, At, narrowRecordUpdates: true, raised: null, scans: [Guid.Empty.ToByteArray()]);
                transaction.Rollback();
            }
            Check.Require(IssueTable.List(connection, pair).Single().Subject.FirstRunDeletion == marker, OutcomeCheck);
            Commit(connection, pair, StateChange.Empty with { FirstRunAcknowledged = [item] });
            Check.Require(IssueTable.ById(connection, pair, original.Id) is { } acknowledged &&
                acknowledged.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None,
                OutcomeCheck);
            Commit(connection, pair, StateChange.Empty with
            {
                Close = [(OutcomePath, [IssueKind.Failure])],
                Raise = [(IssueKind.Info, TextKey.InfoDropboxHistoryUnchecked, OutcomePath, subject)]
            });
            Check.Require(IssueTable.List(connection, pair).Single().Kind == IssueKind.Info, OutcomeCheck);
            Commit(connection, pair, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailChanged, OutcomePath, subject)]
            });
            Commit(connection, pair, StateChange.Empty with
            {
                FirstRunAcknowledged = [item], Close = [(OutcomePath, [IssueKind.Failure])]
            });
            Check.Require(IssueTable.List(connection, pair).All(issue => issue.Kind != IssueKind.Failure), OutcomeCheck);
        }
    }

    private static void SameKeyUpdatePreservation(SqliteConnection connection)
    {
        foreach (var narrow in new[] { false, true })
        foreach (var remote in new[] { MappedLocal, MappedRemote })
        {
            var pair = OutcomePair + narrow + remote;
            Pair(connection, pair);
            var before = Saved(MappedLocal, remote, OutcomeId);
            Synced(connection, pair, before);
            var after = before with { LocalSize = before.LocalSize + 1, DropboxId = OutcomeOtherId };
            var changes = Count(connection, "SELECT total_changes()");
            using (var transaction = connection.BeginTransaction())
            {
                StateCommitter.Commit(connection, transaction, new ChangeTable(connection, writable: false), pair,
                    StateChange.Empty with { Record = [after] }, At, narrow, raised: null);
                transaction.Commit();
            }
            Check.Require(Count(connection, "SELECT total_changes()") - changes == (narrow ? 1 : 2),
                OutcomeCheck);
            RequireRows(connection, pair, after);
            using (var transaction = connection.BeginTransaction())
            {
                StateCommitter.Commit(connection, transaction, new ChangeTable(connection, writable: false), pair,
                    StateChange.Empty with { Record = [before] }, At, narrow, raised: null);
                transaction.Rollback();
            }
            RequireRows(connection, pair, after);
        }
    }

    private const string CustodyDirectory = "syncshark-custody-contract-";
    private const string CustodyDatabase = "state.db";
    private const string CustodyGuidFormat = "N";
    private const string CustodyRoot = "C:\\Synthetic";
    private const string CustodyRemote = "/Synthetic";
    private const string CustodyConnection = "connection";
    private const string CustodyVolume = "volume";
    private const string CustodyOperation = "operation";
    // PARITY 3.1 defines the first current pending encoding as version one.
    private const int PendingVersion = 1;
    private const char HashDigit = 'a';

    private static void PendingCustodyAndCommitCallbacks()
    {
        var directory = Path.Combine(Path.GetTempPath(), CustodyDirectory + Guid.NewGuid().ToString(CustodyGuidFormat));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, CustodyDatabase);
            var hash = new string(HashDigit, StateSchema.HashHexLength);
            var entry = new Entry(OutcomePath, false, 0, At, OutcomeId, string.Empty, hash, null);
            var pending = new PendingEffect(PendingVersion, CustodyOperation, 0, PendingKind.LocalDelete,
                PendingPhase.Armed, OutcomePath, new RemovalProof(entry, CustodyVolume, hash),
                null, null, null, null, null, false, null);
            using (var store = StateStore.Open(database))
            {
                store.SavePair(new PairConfig(OutcomePair, OutcomePair, CustodyRoot, CustodyRemote,
                    CustodyConnection, [], [], FirstRunPolicy.NewestWins, true, null));
                store.ArmPending(OutcomePair, pending);
                Check.Require(store.Pending(OutcomePair) is CurrentPending current && current.Value == pending,
                    OutcomeCheck);
                var raised = new List<Issue>();
                var subject = new IssueSubject([new IssueItem(Side.Local, OutcomePath, OutcomeId)], null);
                store.Commit(OutcomePair, StateChange.Empty with
                {
                    Raise = [(IssueKind.Info, TextKey.InfoDropboxHistoryUnchecked, OutcomePath, subject),
                        (IssueKind.Failure, TextKey.FailChanged, OutcomeOther, subject)]
                }, issue =>
                {
                    Check.Require(store.IssueById(OutcomePair, issue.Id)?.Cause == issue.Cause, OutcomeCheck);
                    raised.Add(issue);
                });
                Check.Require(raised.Count == store.Issues(OutcomePair).Count &&
                    store.Pending(OutcomePair) is CurrentPending unchanged && unchanged.Value == pending,
                    OutcomeCheck);
                var next = pending with { Generation = pending.Generation + 1, Phase = PendingPhase.Unknown };
                store.AdvancePending(OutcomePair, pending, next);
                try
                {
                    store.AdvancePending(OutcomePair, pending, next);
                    throw new ArgumentException(OutcomeCheck);
                }
                catch (InvalidOperationException) { }
                Check.Require(store.Pending(OutcomePair) is CurrentPending advanced && advanced.Value == next,
                    OutcomeCheck);
                pending = next;
                var notificationError = new IOException();
                try
                {
                    store.Commit(OutcomePair, StateChange.Empty with
                    {
                        Raise = [(IssueKind.Info, TextKey.InfoNewestWins, OutcomePath, subject)]
                    }, _ => throw notificationError);
                    throw new ArgumentException(OutcomeCheck);
                }
                catch (IOException error) when (ReferenceEquals(error, notificationError)) { }
                Check.Require(store.Issues(OutcomePair).Single(issue => issue.Kind == IssueKind.Info).Cause ==
                    TextKey.InfoNewestWins, OutcomeCheck);
            }
            using var reopened = StateStore.Open(database);
            Check.Require(reopened.Pending(OutcomePair) is CurrentPending retained && retained.Value == pending,
                OutcomeCheck);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WithDatabase(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), CustodyDirectory + Guid.NewGuid().ToString(CustodyGuidFormat));
        Directory.CreateDirectory(directory);
        try { action(Path.Combine(directory, CustodyDatabase)); }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PairConfig CustodyPair() => new(OutcomePair, OutcomePair, CustodyRoot, CustodyRemote,
        CustodyConnection, [], [], FirstRunPolicy.NewestWins, true, null);

    private static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        StateStore.OpenConnection(connection);
        return connection;
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(OutcomeCheck);
    }

    private static void PendingEncodingMatrix()
    {
        foreach (var phase in Enum.GetValues<PendingPhase>())
        WithDatabase(path =>
        {
            var hash = new string(HashDigit, StateSchema.HashHexLength);
            var entry = new Entry(OutcomePath, false, 0, At, OutcomeId, string.Empty, hash, null);
            var stage = new StageProof(OutcomeOther, CustodyVolume, OutcomeOtherId, 0, hash);
            var armed = new PendingEffect(PendingVersion, CustodyOperation, 0, PendingKind.LocalReplace,
                PendingPhase.Armed, OutcomePath, new RemovalProof(entry, CustodyVolume, hash), null,
                entry, stage, null, null, true, null);
            using (var store = StateStore.Open(path))
            {
                store.SavePair(CustodyPair());
                foreach (var malformed in new[]
                {
                    armed with { Version = PendingVersion + 1 },
                    armed with { Stage = null },
                    armed with { Source = null },
                    armed with { OperationId = string.Empty },
                    armed with { Phase = PendingPhase.Recycled, BinLocator = null },
                    armed with { Phase = PendingPhase.RestoreArmed, Victim = null },
                    armed with { Phase = PendingPhase.PermanentArmed, PermanentAllowed = false }
                })
                {
                    Reject<InvalidDataException>(() => store.ArmPending(OutcomePair, malformed));
                    Check.Require(store.Pending(OutcomePair) is NoPending, OutcomeCheck);
                }
                store.ArmPending(OutcomePair, armed);
                using (var inspection = OpenDatabase(path))
                {
                    using var command = inspection.CreateCommand();
                    command.CommandText = "SELECT typeof(pending_json) FROM pair WHERE pair_id=$p";
                    command.Parameters.AddWithValue(StateSchema.ParamPair, OutcomePair);
                    Check.Require((string)command.ExecuteScalar()! == "text", OutcomeCheck);
                }
                var next = armed with { Generation = armed.Generation + 1, Phase = phase,
                    BinLocator = OutcomeId, Victim = stage };
                store.AdvancePending(OutcomePair, armed, next);
                Check.Require(store.Pending(OutcomePair) is CurrentPending current && current.Value == next, OutcomeCheck);
                Reject<InvalidOperationException>(() => store.AdvancePending(OutcomePair, armed, next));
                Reject<InvalidOperationException>(() => store.AdvancePending(OutcomePair,
                    next with { Cause = TextKey.FailChanged }, next with { Generation = next.Generation + 1 }));
                Reject<InvalidOperationException>(() => store.ArmPending(OutcomePair, armed));
                using var inspectionAfter = OpenDatabase(path);
                using var type = inspectionAfter.CreateCommand();
                type.CommandText = "SELECT typeof(pending_json) FROM pair WHERE pair_id=$p";
                type.Parameters.AddWithValue(StateSchema.ParamPair, OutcomePair);
                Check.Require((string)type.ExecuteScalar()! == "text", OutcomeCheck);
            }
            using var reopened = StateStore.Open(path);
            Check.Require(reopened.Pending(OutcomePair) is CurrentPending restored && restored.Value.Phase == phase,
                OutcomeCheck);
        });
        WithDatabase(path =>
        {
            var source = new Entry(OutcomePath, false, 0, At, OutcomeId, string.Empty, string.Empty, null);
            var raw = " {\"Source\":" + System.Text.Json.JsonSerializer.Serialize(source) +
                ",\"StagePath\":\"stage\",\"Path\":\"Parent/Item\"} ";
            using (var connection = OpenDatabase(path))
            {
                Exec(connection, StateSchema.Create);
                Exec(connection, "PRAGMA user_version=4");
                Exec(connection, StateSchema.UpsertPair, (StateSchema.ParamPair, OutcomePair),
                    (StateSchema.ParamDef, System.Text.Json.JsonSerializer.Serialize(CustodyPair())));
                Exec(connection, "UPDATE pair SET pending_json=$next WHERE pair_id=$p",
                    (StateSchema.ParamPair, OutcomePair), (StateSchema.ParamNext, raw));
            }
            using var store = StateStore.Open(path);
            var legacy = (LegacyPending)store.Pending(OutcomePair);
            Check.Require(legacy.RawJson == raw && legacy.Source == source, OutcomeCheck);
            Reject<InvalidOperationException>(() => store.SettleLegacyPending(OutcomePair,
                legacy.RawToken + OutcomeId, StateChange.Empty));
            Reject<ArgumentException>(() => store.SettleLegacyPending(OutcomePair, legacy.RawToken,
                StateChange.Empty with { Record = [Saved(OutcomePath, OutcomePath, OutcomeId)] }));
            Check.Require(((LegacyPending)store.Pending(OutcomePair)).RawJson == raw, OutcomeCheck);
            store.SettleLegacyPending(OutcomePair, legacy.RawToken, StateChange.Empty);
            Check.Require(store.Pending(OutcomePair) is NoPending, OutcomeCheck);
        });
    }

    private static void MappedUpgradeAndRestart() => WithDatabase(path =>
    {
        var original = Saved(MappedLocal, MappedRemote, OutcomeId) with
        {
            // The schema-4 mapping policy stored the substituted join key, not the raw Dropbox key.
            DropboxKey = PathNames.Key(MappedLocal)
        };
        using (var connection = OpenDatabase(path))
        {
            Exec(connection, StateSchema.Create);
            Exec(connection, "PRAGMA user_version=4");
            Exec(connection, StateSchema.UpsertPair, (StateSchema.ParamPair, OutcomePair),
                (StateSchema.ParamDef, System.Text.Json.JsonSerializer.Serialize(CustodyPair())));
            Synced(connection, OutcomePair, original);
        }
        var expected = original with { DropboxKey = PathNames.DropboxKey(MappedRemote) };
        for (var reopen = 0; reopen <= 1; reopen++)
        {
            using var store = StateStore.Open(path);
            Check.Require(store.SyncedByKey(OutcomePair, Side.Local, expected.LocalKey) == expected &&
                store.SyncedByKey(OutcomePair, Side.Dropbox, expected.DropboxKey) == expected &&
                store.SyncedById(OutcomePair, Side.Local, expected.LocalId) == expected, OutcomeCheck);
            store.Commit(OutcomePair, StateChange.Empty with
            {
                Raise = [(IssueKind.Skipped, TextKey.SkipReservedName, MappedRemote,
                    new IssueSubject([new IssueItem(Side.Dropbox, MappedRemote, expected.DropboxId)], null))]
            });
            Check.Require(store.Issues(OutcomePair).Single().Cause == TextKey.SkipReservedName &&
                store.SyncedByKey(OutcomePair, Side.Local, expected.LocalKey) == expected, OutcomeCheck);
        }
    });

    private static async Task QueuedRecordIsolationAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), CustodyDirectory + Guid.NewGuid().ToString(CustodyGuidFormat));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, CustodyDatabase);
            using var store = StateStore.Open(database);
            store.SavePair(CustodyPair());
            var original = Saved(OutcomePath, OutcomePath, OutcomeId);
            var survivor = Saved(OutcomeOther, OutcomeOther, OutcomeOtherId);
            var bad = Saved(MappedLocal, MappedRemote, OutcomeOtherId) with { DropboxId = null! };
            Task? goodTask = null;
            Task? badTask = null;
            Task? lastTask = null;
            var starter = new OnApplyingRecord(original, () =>
            {
                // Reentrant submissions at the existing input-enumeration boundary queue behind the active drain.
                goodTask = store.CommitRecordAsync(OutcomePair, StateChange.Empty with { Record = [survivor] }, null);
                badTask = store.CommitRecordAsync(OutcomePair, StateChange.Empty with { Record = [bad] }, null);
                lastTask = store.CommitRecordAsync(OutcomePair,
                    StateChange.Empty with { Record = [survivor with { LocalSize = survivor.LocalSize + 1 }] }, null);
            });
            await store.CommitRecordAsync(OutcomePair, StateChange.Empty with { Record = starter }, null);
            Check.Require(goodTask is not null && badTask is not null && lastTask is not null, OutcomeCheck);
            // A synchronous write is the public ordering barrier, not proof that every drained member succeeded.
            store.Commit(OutcomePair, StateChange.Empty with
            {
                Raise = [(IssueKind.Info, TextKey.InfoNewestWins, OutcomePath, new IssueSubject([], null))]
            });
            await goodTask!;
            try { await badTask!; throw new InvalidOperationException(OutcomeCheck); }
            catch (SqliteException) { }
            await lastTask!;
            using var durable = StateStore.OpenReadOnly(database)!;
            Check.Require(durable.SyncedByKey(OutcomePair, Side.Local, original.LocalKey) == original &&
                durable.SyncedByKey(OutcomePair, Side.Local, survivor.LocalKey) ==
                    (survivor with { LocalSize = survivor.LocalSize + 1 }) &&
                durable.SyncedByKey(OutcomePair, Side.Local, bad.LocalKey) is null &&
                durable.Issues(OutcomePair).Single().Kind == IssueKind.Info, OutcomeCheck);
            var callbacks = new List<Issue>();
            Reject<SqliteException>(() => store.Commit(OutcomePair, StateChange.Empty with
            {
                Record = [bad], Raise = [(IssueKind.Info, TextKey.InfoNewestWins, OutcomeOther, new IssueSubject([], null))]
            }, callbacks.Add));
            Check.Require(callbacks.Count == 0 && store.Issues(OutcomePair).Count == 1, OutcomeCheck);
            using (var fault = OpenDatabase(database))
                Exec(fault, "CREATE TRIGGER reject_second_notice BEFORE INSERT ON issue WHEN NEW.path='Sibling/Item' BEGIN SELECT RAISE(ABORT,'notice-failure'); END");
            try
            {
                Reject<SqliteException>(() => store.Commit(OutcomePair, StateChange.Empty with
                {
                    Raise = [(IssueKind.Info, TextKey.InfoNewestWins, MappedLocal, new IssueSubject([], null)),
                        (IssueKind.Info, TextKey.InfoNewestWins, OutcomeOther, new IssueSubject([], null))]
                }, callbacks.Add));
            }
            finally
            {
                using var fault = OpenDatabase(database);
                Exec(fault, "DROP TRIGGER reject_second_notice");
            }
            Check.Require(callbacks.Count == 0 && store.Issues(OutcomePair).Count == 1, OutcomeCheck);
            // rev3h G05: a raising Record now takes the synchronous commit and reports its issue.
            await store.CommitRecordAsync(OutcomePair, StateChange.Empty with
            {
                Record = [original], Raise = [(IssueKind.Info, TextKey.InfoNewestWins, OutcomeOther, new IssueSubject([], null))]
            }, null);
            Check.Require(store.Issues(OutcomePair).Any(issue => issue.Path == OutcomeOther), OutcomeCheck);
            await store.CommitAsync(OutcomePair, StateChange.Empty with { Record = [original] });
            Task[] successful = [];
            await store.CommitRecordAsync(OutcomePair, StateChange.Empty with
            {
                Record = new OnApplyingRecord(original, () => successful =
                [
                    store.CommitRecordAsync(OutcomePair, StateChange.Empty with { Record = [original] }, null),
                    store.CommitRecordAsync(OutcomePair, StateChange.Empty with { Record = [survivor] }, null)
                ])
            }, null);
            store.Commit(OutcomePair, StateChange.Empty);
            Check.Require(successful.Length == 2, OutcomeCheck);
            await Task.WhenAll(successful);
            Check.Require(store.SyncedByKey(OutcomePair, Side.Local, survivor.LocalKey) == survivor, OutcomeCheck);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class OnApplyingRecord(SyncedEntry entry, Action applying) : IReadOnlyList<SyncedEntry>
    {
        private bool validated;
        private Action? pending = applying;
        public int Count => 1;
        public SyncedEntry this[int index] => index == 0 ? entry : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<SyncedEntry> GetEnumerator()
        {
            // The queue's public eligibility enumeration precedes transaction application.
            if (validated) Interlocked.Exchange(ref pending, null)?.Invoke();
            validated = true;
            yield return entry;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void Atomicity(SqliteConnection connection)
    {
        const string pair = "atomic";
        Pair(connection, pair);
        var first = new SyncedEntry(PathNames.Key("a.txt"), "a.txt", PathNames.DropboxKey("a.txt"), "a.txt",
            false, "L1", 1, 10, "D1", "H1");
        var second = new SyncedEntry(PathNames.Key("b.txt"), "b.txt", PathNames.DropboxKey("b.txt"), "b.txt",
            false, "L2", 2, 20, "D2", "H2");
        Synced(connection, pair, first);
        Synced(connection, pair, second);
        var movedFirst = first with { LocalKey = PathNames.Key("c.txt"), LocalPath = "c.txt" };
        var movedSecond = second with { LocalKey = PathNames.Key("c.txt"), LocalPath = "c.txt" };

        using (var transaction = connection.BeginTransaction())
        {
            try
            {
                StateCommitter.Commit(connection, transaction, new ChangeTable(connection, writable: false), pair, StateChange.Empty with
                {
                    Renames =
                    [
                        new RenameApplied(first, movedFirst, null, null, null),
                        new RenameApplied(second, movedSecond, null, null, null),
                    ],
                }, At, narrowRecordUpdates: true, raised: null, scans: [Guid.Empty.ToByteArray()]);
                Check.Require(false, "atomicity: the conflicting second rename must fail");
            }
            catch (PathOccupiedException)
            {
                transaction.Rollback();
            }
        }

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 2 && rows.Any(row => row.LocalPath == "a.txt") &&
            rows.Any(row => row.LocalPath == "b.txt"), "a failure mid-commit leaves no change");
    }

    private static void Pair(SqliteConnection connection, string pairId)
    {
        var pair = SourceFixture.Bound(new PairConfig(pairId, "Pair A", @"C:\Synthetic", "/Synthetic",
            "connection", [], [], FirstRunPolicy.NewestWins, true, null));
        Exec(connection, StateSchema.UpsertPair, (StateSchema.ParamPair, pairId),
            (StateSchema.ParamDef, System.Text.Json.JsonSerializer.Serialize(pair)));
        using var transaction = connection.BeginTransaction();
        new ChangeTable(connection, writable: false).SeedPairSources(transaction, pairId);
        transaction.Commit();
    }

    private static void Synced(SqliteConnection connection, string pairId, SyncedEntry entry) =>
        Exec(connection, StateSchema.InsertSynced,
            (StateSchema.ParamPair, pairId),
            (StateSchema.ParamLocalKey, entry.LocalKey),
            (StateSchema.ParamLocalPath, entry.LocalPath),
            (StateSchema.ParamDropboxKey, entry.DropboxKey),
            (StateSchema.ParamDropboxPath, entry.DropboxPath),
            (StateSchema.ParamIsFolder, entry.IsFolder),
            (StateSchema.ParamLocalId, entry.LocalId),
            (StateSchema.ParamLocalSize, entry.LocalSize),
            (StateSchema.ParamLocalMtime, entry.LocalModifiedTicks),
            (StateSchema.ParamDropboxId, entry.DropboxId),
            (StateSchema.ParamDropboxHash, entry.DropboxContentHash), (StateSchema.ParamPublishedJournal, null), (StateSchema.ParamPublishedUsn, null));

    private static void Scan(SqliteConnection connection, string pairId, Side side, string path,
        bool isFolder) =>
        Exec(connection, StateSchema.ScanUpsert,
            (StateSchema.ParamScan, Guid.Empty.ToByteArray()),
            (StateSchema.ParamPair, pairId), (StateSchema.ParamSide, (int)side),
            (StateSchema.ParamJoinKey, Key(side, path)), (StateSchema.ParamOwnKey, PathNames.Key(path)),
            (StateSchema.ParamPath, path), (StateSchema.ParamIsFolder, isFolder), (StateSchema.ParamSize, 0L),
            (StateSchema.ParamMtime, 0L), (StateSchema.ParamId, ""), (StateSchema.ParamRev, ""),
            (StateSchema.ParamHash, ""), (StateSchema.ParamSkip, null));

    private static string Key(Side side, string path) =>
        side == Side.Local ? PathNames.Key(path) : PathNames.DropboxKey(path);

    private static void Commit(SqliteConnection connection, string pairId, StateChange change)
    {
        using var transaction = connection.BeginTransaction();
        StateCommitter.Commit(connection, transaction, new ChangeTable(connection, writable: false), pairId, change, At,
            narrowRecordUpdates: true, raised: null, scans: [Guid.Empty.ToByteArray()]);
        transaction.Commit();
    }

    private static IReadOnlyList<SyncedEntry> Rows(SqliteConnection connection, string pairId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pathkey(local_path), local_path, dropbox_key, dropbox_path, is_folder, local_id, " +
            "local_size, local_mtime, dropbox_id, dropbox_hash FROM synced_entry WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) ORDER BY pathkey(local_path)";
        command.Parameters.AddWithValue(StateSchema.ParamPair, pairId);
        using var reader = command.ExecuteReader();
        var rows = new List<SyncedEntry>();
        while (reader.Read())
            rows.Add(new SyncedEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4) != 0, reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7),
                reader.GetString(8), StateEncoding.ReadHash(reader, 9)));
        return rows;
    }

    private static IReadOnlyList<ScanRow> Scans(SqliteConnection connection, string pairId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT side, own_key, key, path FROM scan WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) ORDER BY side, own_key";
        command.Parameters.AddWithValue(StateSchema.ParamPair, pairId);
        using var reader = command.ExecuteReader();
        var rows = new List<ScanRow>();
        while (reader.Read())
            rows.Add(new ScanRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    private static IReadOnlyList<(long Kind, string Cause, string Path)> Issues(SqliteConnection connection,
        string pairId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, cause, path FROM issue WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) ORDER BY issue_id";
        command.Parameters.AddWithValue(StateSchema.ParamPair, pairId);
        using var reader = command.ExecuteReader();
        var rows = new List<(long Kind, string Cause, string Path)>();
        while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private static void Exec(SqliteConnection connection, string sql, params (string Name, object? Value)[] arguments)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection connection, string sql, params (string Name, object? Value)[] arguments)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed record ScanRow(long Side, string OwnKey, string Key, string Path);
}
