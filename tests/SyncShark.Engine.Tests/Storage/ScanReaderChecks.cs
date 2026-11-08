using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.V2;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks for ScanReader (`--engine-checks store`), written by ScanReader's writer.</summary>
public static class ScanReaderChecks
{
    private static readonly RowContext EmptyContext = new(false, false, false, null, false, false, false, false);

    public static void Run()
    {
        QueryPlanChecks.Run();
        var folder = Path.Combine(Path.GetTempPath(), "scan-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var statePath = Path.Combine(folder, "state.db");
            using var connection = Open(statePath);
            ScopedCases(connection);
            DecoderCases(connection);
            PagingVisibilityCases(connection, statePath);
            ContextCases(connection);
            RenameCases(connection);
            DropboxRenameCases(connection);
            DropboxMoveCases(connection);
            RenameOrderCases(connection);
            RenameDepthEquivalenceCases();
            DeletedNameCases(connection);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void ScopedCases(SqliteConnection connection)
    {
        RowsCase(connection, "scoped-batch", "subtree containment", pair =>
        {
            AddSynced(connection, pair, Synced("OUTSIDE/MISSING", "Outside/Missing", "OUTSIDE/MISSING", "Outside/Missing", "outside-local", "outside-remote"));
            AddSynced(connection, pair, Synced("PARENT/OLD", "Parent/Old", "PARENT/OLD", "Parent/Old", "inside-local", "inside-remote"));
            AddSynced(connection, pair, Synced("OUTSIDE/OLD", "Outside/Old", "OUTSIDE/OLD", "Outside/Old", "moved-local", "moved-remote"));
            AddScan(connection, pair, Side.Local, "PARENT", "Parent", id: "root-local");
            AddScan(connection, pair, Side.Local, "PARENT/NEW", "Parent/New", id: "inside-local");
            AddScan(connection, pair, Side.Local, "PARENT/INCOMING", "Parent/Incoming", id: "moved-local");
            AddScan(connection, pair, Side.Local, "PARENTS/OTHER", "Parents/Other", id: "sibling-local");
            var rows = ScanReader.Rows(connection, pair, _ => EmptyContext, "Parent").ToArray();
            Check.Require(rows.Length == 4 && rows.All(row => PathNames.IsSelfOrUnderKey(row.Key, "PARENT")),
                "scoped scans include their root and inside baseline-only rows, never outside missing or prefix siblings");
            var incoming = rows.Single(row => row.Key == "PARENT/INCOMING");
            Check.Require(incoming.Synced is null && incoming.Local?.Id == "moved-local",
                "cross-boundary move-in is a new in-scope row, not an outside baseline");
        });
    }

    private static void DecoderCases(SqliteConnection connection)
    {
        RowsCase(connection, "dec-local", "local-only", pair =>
        {
            AddScan(connection, pair, Side.Local, "L.TXT", "l.txt", size: 5, mtime: 123, id: "lid");
            var row = Single(connection, pair);
            Check.Require(row.Key == "L.TXT" && row.Synced is null && row.Dropbox is null,
                "a local-only item is one free row");
            var local = row.Local!;
            Check.Require(local.Path == "l.txt" && !local.IsFolder && local.Size == 5 &&
                local.Modified.UtcTicks == 123 && local.Id == "lid" && local.Revision == "" &&
                local.ContentHash == "" && local.Skip is null, "the local entry comes from the local columns");
            Check.Require(!row.Ambiguous, "an unambiguous row has no Extra entries");
        });

        RowsCase(connection, "dec-dropbox", "Dropbox-only", pair =>
        {
            AddScan(connection, pair, Side.Dropbox, "D.TXT", "d.txt", size: 7, mtime: 456,
                id: "did", rev: "rev1", hash: "hash1", skip: "SkipLink");
            var row = Single(connection, pair);
            Check.Require(row.Key == "D.TXT" && row.Local is null && row.Synced is null,
                "a Dropbox-only item is one free row");
            var dropbox = row.Dropbox!;
            Check.Require(dropbox.Path == "d.txt" && dropbox.Size == 7 && dropbox.Modified.UtcTicks == 456 &&
                dropbox.Id == "did" && dropbox.Revision == "rev1" && dropbox.ContentHash == "hash1" &&
                dropbox.Skip == "SkipLink", "the Dropbox entry comes from the Dropbox columns");
        });

        RowsCase(connection, "dec-both", "both sides", pair =>
        {
            AddScan(connection, pair, Side.Local, "B.TXT", "b.txt", id: "lid");
            AddScan(connection, pair, Side.Dropbox, "B.TXT", "b.txt", id: "did", rev: "r", hash: "h");
            var row = Single(connection, pair);
            Check.Require(row.Local?.Path == "b.txt" && row.Dropbox?.Path == "b.txt" && row.Synced is null,
                "a free key present on both sides is one row with both entries");
            Check.Require(!row.Ambiguous, "the same key on both sides is not ambiguity");
        });

        RowsCase(connection, "dec-synced", "synced-only", pair =>
        {
            AddSynced(connection, pair, Synced("A.TXT", "a.txt", "A.TXT", "a.txt", "lid", "did"));
            var row = Single(connection, pair);
            Check.Require(row.Synced is not null && row.Local is null && row.Dropbox is null && row.Key == "A.TXT",
                "a synced row nobody observed is a linked row with no entries");
            Check.Require(row.Synced!.LocalPath == "a.txt" && row.Synced.DropboxId == "did",
                "the linked entry comes from the synced columns");
            Check.Require(row.Synced.DropboxKey == "A.TXT" && row.Synced.LocalId == "lid",
                "an inherited saved Dropbox key falls back to the supplied local key");
        });

        RowsCase(connection, "dec-inherited-held", "inherited held key", pair =>
        {
            AddSynced(connection, pair, Synced("A.TXT", "a.txt", "A.TXT", "a.txt", "lid", "did"));
            AddScan(connection, pair, Side.Dropbox, PathNames.DropboxKey("a.txt"), "a.txt", id: "did", hash: "h");
            var row = Single(connection, pair);
            Check.Require(row.Synced is not null && row.Dropbox?.Path == "a.txt",
                "an inherited saved key holds its Dropbox scan row, so no free duplicate appears");
        });

        RowsCase(connection, "dec-keepboth", "cross-mapped keep-both", pair =>
        {
            AddSynced(connection, pair, Synced("A.TXT", "a.txt", "B.TXT", "b.txt", "lid", "did"));
            AddScan(connection, pair, Side.Local, "A.TXT", "a.txt", id: "lid");
            AddScan(connection, pair, Side.Dropbox, "B.TXT", "b.txt", id: "did", rev: "r", hash: "h");
            var row = Single(connection, pair);
            Check.Require(row.Synced is not null && row.Local?.Path == "a.txt" && row.Dropbox?.Path == "b.txt",
                "a keep-both copy joins its own local and Dropbox keys");
            Check.Require(!row.Ambiguous, "an observed keep-both copy is not ambiguity");
        });

        RowsCase(connection, "dec-mapped", "mapped name", pair =>
        {
            AddSynced(connection, pair, Synced("X_Y.TXT", "x_y.txt", "X_Y.TXT", "x:y.txt", "lid", "did"));
            AddScan(connection, pair, Side.Local, "X_Y.TXT", "x_y.txt", id: "lid");
            // A historical mapped binding is the saved join key, which the scan row carries as its key delta.
            AddScan(connection, pair, Side.Dropbox, "X_Y.TXT", "x:y.txt",
                id: "did", rev: "r", hash: "h");
            var row = Single(connection, pair);
            Check.Require(row.Dropbox?.Path == "x:y.txt",
                "the Dropbox entry keeps the provider spelling, not the join key");
            Check.Require(!row.Ambiguous, "one mapped name is not ambiguity");
        });

        RowsCase(connection, "dec-ambiguous", "ambiguous mapped pair", pair =>
        {
            AddScan(connection, pair, Side.Dropbox, "A_B", "a:b", id: "d1", hash: "h1");
            AddScan(connection, pair, Side.Dropbox, "A_B", "a?b", id: "d2", hash: "h2");
            var row = Single(connection, pair);
            Check.Require(row.Ambiguous && row.Extra.Count == 1,
                "a second Dropbox entry with the same key goes into Extra");
            Check.Require(row.Dropbox!.Id == "d1" && row.Extra[0].Id == "d2",
                "the first distinct own key is the Dropbox entry, the rest are Extra");
        });

        RowsCase(connection, "dec-mapped-ambiguous", "mapped ambiguity", pair =>
        {
            AddScan(connection, pair, Side.Dropbox, "A_B", "a:b", id: "d1", hash: "h1");
            AddScan(connection, pair, Side.Dropbox, "A_B", "a?b", id: "d2", hash: "h2");
            var row = Single(connection, pair);
            Check.Require(row.Ambiguous && row.Extra.Count == 1,
                "two provider names that map to one local name are one ambiguous row");
            Check.Require(row.Dropbox?.Path == "a:b" && row.Extra[0].Path == "a?b",
                "the mapping ambiguity keeps both provider spellings");
        });

        RowsCase(connection, "dec-repeated-local", "repeated local join", pair =>
        {
            AddScan(connection, pair, Side.Local, "A_B", "a_b", id: "local");
            foreach (var path in new[] { "a_b", "a?b", "a:b" })
                AddScan(connection, pair, Side.Dropbox, "A_B", path, id: path);
            var row = Single(connection, pair);
            Check.Require(row.Local?.Id == "local" && row.Dropbox?.Path == "a:b" &&
                row.Extra.Select(entry => entry.Path).SequenceEqual(["a?b", "a_b"]),
                "a repeated local join decodes once and every Dropbox candidate keeps SQL order");
        });

        RowsCase(connection, "dec-r3m02", "R3-M02 fixture", pair =>
        {
            AddSynced(connection, pair, Synced("A.TXT", "a.txt", "B.TXT", "b.txt", "lid", "did"));
            AddScan(connection, pair, Side.Local, "A.TXT", "a.txt", id: "lid");
            AddScan(connection, pair, Side.Dropbox, "B.TXT", "b.txt", id: "did", rev: "r", hash: "h");
            AddScan(connection, pair, Side.Dropbox, "A.TXT", "a.txt", id: "new", rev: "r2", hash: "h2");
            var rows = ScanReader.Rows(connection, pair, _ => EmptyContext).ToList();
            Check.Require(rows.Count == 2, "a new Dropbox item at a linked key keeps its own row");
            Check.Require(rows[0].Synced is not null && rows[0].Local?.Path == "a.txt" &&
                rows[0].Dropbox?.Path == "b.txt", "the linked row comes first");
            Check.Require(rows[1].Synced is null && rows[1].Local is null && rows[1].Dropbox?.Path == "a.txt",
                "the free row is Dropbox-only");
        });
    }

    private static void PagingVisibilityCases(SqliteConnection reader, string statePath)
    {
        // Documented contract (engine design, scan reading): each key page is its own snapshot and the
        // reader holds none across caller work, so a commit for a key above the cursor is seen at that key.
        const string pair = "page-visibility";
        AddPair(reader, pair);
        AddScan(reader, pair, Side.Local, "A.TXT", "a.txt", id: "la");
        AddScan(reader, pair, Side.Local, "C.TXT", "c.txt", id: "lc");

        var seen = new List<Row>();
        using (var rows = ScanReader.Rows(reader, pair, _ => EmptyContext).GetEnumerator())
        {
            Check.Require(rows.MoveNext() && rows.Current.Key == "A.TXT" && rows.Current.Synced is null,
                "the pass reads the first key before the mid-pass commit");
            seen.Add(rows.Current);
            using (var writer = new SqliteConnection("Data Source=" + statePath + ";Pooling=False"))
            {
                StateStore.OpenConnection(writer);
                Execute(writer, StateSchema.Pragmas);
                AddSynced(writer, pair, Synced("B.TXT", "b.txt", "B.TXT", "b.txt", "lb", "db"));
            }
            while (rows.MoveNext()) seen.Add(rows.Current);
        }

        Check.Require(seen.Select(row => row.Key).SequenceEqual(["A.TXT", "B.TXT", "C.TXT"]),
            "an above-cursor commit is seen at its own key and no key is read twice or skipped");
        Check.Require(seen[1].Synced is not null && seen[1].Synced!.LocalPath == "b.txt" &&
            seen[1].Local is null && seen[1].Dropbox is null,
            "the pass discovers the committed saved key above the cursor with no observed sides");
        Check.Require(seen[0].Local?.Path == "a.txt" && seen[2].Local?.Path == "c.txt" &&
            seen[2].Synced is null, "keys already read and later keys keep their own states");
    }

    private static void ContextCases(SqliteConnection connection)
    {
        const string pair = "dec-context";
        AddPair(connection, pair);
        AddScan(connection, pair, Side.Local, "B.TXT", "b.txt");
        AddScan(connection, pair, Side.Local, "A.TXT", "a.txt");
        var context = EmptyContext with { FirstRun = true };
        var seen = new List<string>();
        var rows = ScanReader.Rows(connection, pair, row =>
        {
            seen.Add(row.Key);
            return context;
        }).ToList();
        Check.Require(rows.Count == 2 && seen.Count == 2, "context is called once per row");
        Check.Require(seen.SequenceEqual(["A.TXT", "B.TXT"]), "context is called in result order");
        Check.Require(rows.All(row => ReferenceEquals(row.Context, context)),
            "each row carries the context built for it");
    }

    private static void RenameCases(SqliteConnection connection)
    {
        const string pair = "ren-local";
        AddPair(connection, pair);
        AddSynced(connection, pair, Synced("OLD.TXT", "old.txt", "OLD.TXT", "old.txt", "lid", "did"));
        AddScan(connection, pair, Side.Local, "OLD.TXT", "old.txt", id: "lid");
        AddScan(connection, pair, Side.Local, "NEW.TXT", "new.txt", id: "lid");
        AddScan(connection, pair, Side.Local, "OTHER.TXT", "other.txt", id: "nope");
        var candidates = ScanReader.Renames(connection, pair);
        Check.Require(candidates.Count == 1, "only a differing local path with the same id is a candidate");
        Check.Require(candidates[0].Side == Side.Local && candidates[0].Moved.Path == "new.txt" &&
            candidates[0].Synced.LocalPath == "old.txt", "the local path is compared exactly");
    }

    private static void DropboxRenameCases(SqliteConnection connection)
    {
        const string pair = "ren-dropbox";
        AddPair(connection, pair);
        AddSynced(connection, pair, Synced("A.TXT", "a.txt", "FOLDER/OLD.TXT", "folder/old.txt", "lid", "did"));
        AddSynced(connection, pair, Synced("S.TXT", "s.txt", "S.TXT", "same.txt", "l2", "dsame"));
        AddScan(connection, pair, Side.Dropbox, PathNames.DropboxKey("folder/new.txt"),
            "folder/new.txt", id: "did", hash: "h");
        AddScan(connection, pair, Side.Dropbox, PathNames.DropboxKey("Folder/old.txt"),
            "Folder/old.txt", id: "did", hash: "h");
        AddScan(connection, pair, Side.Dropbox, PathNames.DropboxKey("Folder/moved.txt"),
            "Folder/moved.txt", id: "did", hash: "h");
        AddScan(connection, pair, Side.Dropbox, PathNames.DropboxKey("folder/A.txt"),
            "folder/A.txt", id: "did", hash: "h");
        AddScan(connection, pair, Side.Dropbox, PathNames.DropboxKey("same.txt"), "same.txt",
            id: "dsame", hash: "h");
        var paths = ScanReader.Renames(connection, pair).Select(candidate => candidate.Moved.Path).ToList();
        Check.Require(paths.Contains("Folder/old.txt"),
            "an ancestor-only case difference is a rename candidate, same as local");
        Check.Require(!paths.Contains("same.txt"), "an unchanged Dropbox path is no candidate");
        Check.Require(paths.Contains("folder/new.txt") && paths.Contains("Folder/moved.txt"),
            "a last segment that differs exactly is a rename whatever the ancestor casing");
        Check.Require(paths.Contains("folder/A.txt"), "a case-only last segment is a rename");
        Check.Require(paths.SequenceEqual(
                ["Folder/moved.txt", "Folder/old.txt", "folder/A.txt", "folder/new.txt"]),
            "Dropbox candidates are ordered by depth then path");
    }

    private static void DropboxMoveCases(SqliteConnection connection)
    {
        const string pair = "ren-moves";
        AddPair(connection, pair);
        AddSynced(connection, pair, Synced("A.TXT", "a.txt", "DOCS/A.TXT", "Docs/a.txt", "l1", "d1"));
        AddSynced(connection, pair, Synced("F", "f", "OLD/F", "Old/f", "l2", "d2"));
        AddSynced(connection, pair, Synced("G.TXT", "g.txt", "G.TXT", "g.txt", "l3", "d3"));
        AddSynced(connection, pair, Synced("H.TXT", "h.txt", "CASE/H.TXT", "case/h.txt", "l4", "d4"));
        AddScan(connection, pair, Side.Dropbox, PathNames.DropboxKey("Other/a.txt"),
            "Other/a.txt", id: "d1", hash: "h");
        AddScan(connection, pair, Side.Dropbox, PathNames.DropboxKey("New/Old/f"),
            "New/Old/f", isFolder: true, id: "d2");
        AddScan(connection, pair, Side.Dropbox, PathNames.DropboxKey("Root/g.txt"),
            "Root/g.txt", id: "d3", hash: "h");
        AddScan(connection, pair, Side.Dropbox, PathNames.DropboxKey("Case/h.txt"),
            "Case/h.txt", id: "d4", hash: "h");

        var paths = ScanReader.Renames(connection, pair).Select(candidate => candidate.Moved.Path).ToList();
        Check.Require(paths.Contains("Other/a.txt"), "a file moved to another parent is a rename candidate");
        Check.Require(paths.Contains("New/Old/f"), "a folder moved to another depth is a rename candidate");
        Check.Require(paths.Contains("Root/g.txt"), "a file moved to another depth is a rename candidate");
        Check.Require(paths.Contains("Case/h.txt"),
            "an ancestor-only case difference is a rename candidate, same as local");
        Check.Require(paths.Count == 4, "every differing Dropbox path is a candidate");
    }

    private static void RenameOrderCases(SqliteConnection connection)
    {
        const string pair = "ren-order";
        AddPair(connection, pair);
        AddSynced(connection, pair, Synced("S1.TXT", "s1.txt", "S1.TXT", "s1.txt", "i1", "d1"));
        AddSynced(connection, pair, Synced("S2.TXT", "s2.txt", "S2.TXT", "s2.txt", "i2", "d2"));
        AddSynced(connection, pair, Synced("S3.TXT", "s3.txt", "S3.TXT", "s3.txt", "i3", "d3"));
        AddSynced(connection, pair, Synced("S4.TXT", "s4.txt", "S4.TXT", "s4.txt", "i4", "d4"));
        AddScan(connection, pair, Side.Local, "B.TXT", "b.txt", id: "i1");
        AddScan(connection, pair, Side.Local, "A.TXT", "a.txt", id: "i2");
        AddScan(connection, pair, Side.Local, "D/C.TXT", "d/c.txt", id: "i3");
        AddScan(connection, pair, Side.Local, "D/A.TXT", "d/a.txt", id: "i4");
        AddScan(connection, pair, Side.Dropbox, "A.TXT", "a.txt", id: "d2");
        var candidates = ScanReader.Renames(connection, pair);
        Check.Require(candidates.Select(candidate => candidate.Moved.Path).SequenceEqual(
                ["a.txt", "a.txt", "b.txt", "d/a.txt", "d/c.txt"]),
            "candidates are ordered by path depth, then path");
        Check.Require(candidates.Take(2).Select(candidate => candidate.Side).SequenceEqual([Side.Local, Side.Dropbox]),
            "equal depth and path preserve the SQL side order");
    }

    // Rename ordering used Core PairPath.Depth; the owner PathNames.Depth must agree on every reachable spelling.
    private static void RenameDepthEquivalenceCases()
    {
        var alphabet = new[] { 'a', '/', 'B' };
        for (var length = 0; length <= 6; length++)
        {
            var count = 1;
            for (var index = 0; index < length; index++) count *= alphabet.Length;
            for (var code = 0; code < count; code++)
            {
                var chars = new char[length];
                var value = code;
                for (var index = 0; index < length; index++)
                {
                    chars[index] = alphabet[value % alphabet.Length];
                    value /= alphabet.Length;
                }
                var path = new string(chars);
                Check.Require(PathNames.Depth(path) == PairPath.Depth(path),
                    "PathNames.Depth equals the old Core depth for every empty, root, leading, trailing, repeated or "
                    + "mixed-case separator spelling: '" + path + "'");
            }
        }
        Check.Require(ThrowsNullReference(() => _ = PathNames.Depth(null!)) &&
            ThrowsNullReference(() => _ = PairPath.Depth(null!)),
            "both depth implementations reject a null path with NullReferenceException");
    }

    // Discovery membership: exact tombstones only, once each, and never another pair's row.
    private static void DeletedNameCases(SqliteConnection connection)
    {
        const string pair = "deleted-names";
        const string other = "deleted-other";
        AddPair(connection, pair);
        AddPair(connection, other);
        var row = PairRow(connection, pair);
        var otherRow = PairRow(connection, other);
        Execute(connection, StateSchema.CreateDeletedName);

        Check.Require(!ScanReader.HasDeletedNames(connection, row) &&
            !ScanReader.DeletedNameExact(connection, row, PathNames.Key("gone.txt")),
            "an empty discovery relation has no deleted names");

        var key = PathNames.Key("gone.txt");
        Execute(connection, StateSchema.DeletedNameInsert,
            (StateSchema.ParamPairRow, row), (StateSchema.ParamPath, key));
        Check.Require(ScanReader.HasDeletedNames(connection, row) && ScanReader.DeletedNameExact(connection, row, key),
            "one tombstone is a hit for its own pair and exact key");
        Check.Require(!ScanReader.DeletedNameExact(connection, row, PathNames.Key("gone.txt.bak")) &&
            !ScanReader.DeletedNameExact(connection, row, PathNames.Key("gone")),
            "a sibling sharing a prefix is not an exact deleted name");
        Check.Require(!ScanReader.HasDeletedNames(connection, otherRow) &&
            !ScanReader.DeletedNameExact(connection, otherRow, key),
            "another pair's tombstone is invisible");

        // A duplicate page marker is idempotent in the disposable relation.
        Execute(connection, StateSchema.DeletedNameInsert,
            (StateSchema.ParamPairRow, row), (StateSchema.ParamPath, key));
        Check.Require(Count(connection, row) == 1, "a duplicate tombstone stays one marker");

        foreach (var name in new[] { "a", "b", "Parent/C", "Parent/D", "Z" })
            Execute(connection, StateSchema.DeletedNameInsert,
                (StateSchema.ParamPairRow, row), (StateSchema.ParamPath, PathNames.Key(name)));
        Check.Require(ScanReader.HasDeletedNames(connection, row) &&
            ScanReader.DeletedNameExact(connection, row, PathNames.Key("Parent/C")) &&
            ScanReader.DeletedNameExact(connection, row, PathNames.Key("Parent/D")) &&
            !ScanReader.DeletedNameExact(connection, row, PathNames.Key("Parent/E")),
            "many markers keep exact matches and reject an absent key");
        Check.Require(Count(connection, row) == 6, "each distinct tombstone is stored once");
    }

    private static long PairRow(SqliteConnection connection, string pairId)
    {
        using var command = StorageCommand.Create(connection, null, StateSchema.PairFence,
            (StateSchema.ParamPair, pairId));
        using var reader = command.ExecuteReader();
        Check.Require(reader.Read(), "the fixture has the pair row");
        return reader.GetInt64(0);
    }

    private static long Count(SqliteConnection connection, long pairRow)
    {
        using var command = StorageCommand.Create(connection, null,
            "SELECT count(*) FROM scan_state.deleted_name WHERE pair_id=$pair;", (StateSchema.ParamPairRow, pairRow));
        return (long)command.ExecuteScalar()!;
    }

    private static bool ThrowsNullReference(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (NullReferenceException)
        {
            return true;
        }
    }

    private static void RowsCase(SqliteConnection connection, string pair, string what, Action<string> check)
    {
        AddPair(connection, pair);
        try
        {
            check(pair);
        }
        catch (InvalidOperationException failure)
        {
            throw new InvalidOperationException(what + ": " + failure.Message);
        }
    }

    private static Row Single(SqliteConnection connection, string pair)
    {
        var rows = ScanReader.Rows(connection, pair, _ => EmptyContext).ToList();
        Check.Require(rows.Count == 1, "the fixture decodes to one row");
        return rows[0];
    }

    private static SyncedEntry Synced(string localKey, string localPath, string dropboxKey, string dropboxPath,
        string localId, string dropboxId) =>
        new(localKey, localPath, dropboxKey, dropboxPath, false, localId, 10, 100, dropboxId, "hash");

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        StateStore.OpenConnection(connection);
        Execute(connection, StateSchema.Pragmas);
        Execute(connection, StateSchema.Create);
        // Fresh creation order in StateStore.InitializeSchema: base schema, additions, version.
        Execute(connection, StateSchema.SchemaAdditions);
        Execute(connection, StateSchema.DeletedHistorySchema + StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames + StateSchema.SpentDeletedHistory + StateSchema.LocalPublishedColumns + StateSchema.DiscoveryRequiredColumn + StateSchema.IssuePathKeyIndex);
        Execute(connection, StateSchema.PublishVersion);
        Execute(connection, StateSchema.AttachScan, (StateSchema.ParamPath, StateSchema.EmptyScan));
        Execute(connection, StateSchema.ScanPragmas);
        Execute(connection, StateSchema.CreateScan);
        return connection;
    }

    private static void AddPair(SqliteConnection connection, string pairId) =>
        Execute(connection, StateSchema.UpsertPair,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamDef, "{}"));

    private static void AddSynced(SqliteConnection connection, string pairId, SyncedEntry entry) =>
        Execute(connection, StateSchema.InsertSynced,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamLocalKey, entry.LocalKey),
            (StateSchema.ParamLocalPath, entry.LocalPath), (StateSchema.ParamDropboxKey, entry.DropboxKey),
            (StateSchema.ParamDropboxPath, entry.DropboxPath), (StateSchema.ParamIsFolder, entry.IsFolder),
            (StateSchema.ParamLocalId, entry.LocalId), (StateSchema.ParamLocalSize, entry.LocalSize),
            (StateSchema.ParamLocalMtime, entry.LocalModifiedTicks), (StateSchema.ParamDropboxId, entry.DropboxId),
            (StateSchema.ParamDropboxHash, entry.DropboxContentHash), (StateSchema.ParamPublishedJournal, null), (StateSchema.ParamPublishedUsn, null));

    private static void AddScan(SqliteConnection connection, string pairId, Side side, string key,
        string path, bool isFolder = false, long size = 1, long mtime = 100, string id = "id", string rev = "",
        string hash = "", string? skip = null) =>
        Execute(connection, StateSchema.ScanUpsert,
            (StateSchema.ParamScan, Guid.Empty.ToByteArray()),
            (StateSchema.ParamPair, pairId), (StateSchema.ParamSide, (int)side),
            (StateSchema.ParamJoinKey, key), (StateSchema.ParamOwnKey, PathNames.Key(path)),
            (StateSchema.ParamPath, path), (StateSchema.ParamIsFolder, isFolder),
            (StateSchema.ParamSize, size), (StateSchema.ParamMtime, mtime), (StateSchema.ParamId, id),
            (StateSchema.ParamRev, rev), (StateSchema.ParamHash, hash), (StateSchema.ParamSkip, skip));

    private static void Execute(SqliteConnection connection, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
