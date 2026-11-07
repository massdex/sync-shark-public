using System.Threading.Channels;
using Massdex.SyncShark.Engine.Storage;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks for ScanWriter (`--engine-checks store`), written by ScanWriter's writer.</summary>
public static class ScanWriterChecks
{
    private static readonly DateTimeOffset Modified = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly object WriteLock = new();

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            var database = Path.Combine(folder, "state.db");
            using var connection = Open(database);
            RowCases(connection);
            DropboxKeyCases(connection);
            FileReplacesFolderCases(connection);
            DeleteCases(connection);
            TombstoneRangeCases(connection);
            UpsertCases(connection);
            ConflictCases(connection);
            AbortCases(connection);
            AddAfterCompleteCases(connection);
            WriteFailureCases(connection);
            CachedBatchCases(connection);
            DrainBatchCases(connection);
            FullQueueCancelCases(connection);
            FullQueueAbortCases(connection);
            FullQueueFailureCases(connection);
            FreshDescendantCases(connection);
            FreshStatementCases(connection);
            OrderedEventCases(connection);
            DeletedNameCases(connection);
            MixedBoundaryCases(connection);
            DeletedNameOrderCases(connection);
            TombstoneRunOrderCases(connection);
            TombstoneRunEqualityCases(connection);
            DeletedNameFailureCases(connection);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void RowCases(SqliteConnection connection)
    {
        const string pair = "scan-row";
        AddPair(connection, pair);
        Write(connection, pair, Side.Local, begunEmpty: false,
            new Listed("a.txt", FileEntry("a.txt", 10, "id-a", "", "")),
            new Listed("dir", FolderEntry("dir", "id-d")),
            new Listed("dir/b.txt", FileEntry("dir/b.txt", 3, "id-b", "rev-b", "", TextKey.SkipLink.ToString())));

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 3, "each listed item becomes one row");

        var file = rows.Single(row => row.Path == "a.txt");
        Check.Require(file.Side == (int)Side.Local && file.OwnKey == PathNames.Key("a.txt") &&
            file.Key == PathNames.Key("a.txt"), "a local row keys on its own spelling");
        Check.Require(!file.IsFolder && file.Size == 10 && file.Mtime == Modified.UtcTicks && file.Id == "id-a" &&
            file.Rev == "" && file.Hash == "" && file.Skip is null, "a file row stores the entry's facts");

        var folder = rows.Single(row => row.Path == "dir");
        Check.Require(folder.IsFolder && folder.Size == 0 && folder.Mtime == 0, "a folder row stores folder facts");

        var skipped = rows.Single(row => row.Path == "dir/b.txt");
        Check.Require(skipped.Skip == TextKey.SkipLink.ToString() && skipped.Rev == "rev-b",
            "a skipped row keeps its cause and revision");
    }

    private static void DropboxKeyCases(SqliteConnection connection)
    {
        const string pair = "scan-dropbox";
        AddPair(connection, pair);
        Write(connection, pair, Side.Local, begunEmpty: false,
            new Listed("f:x.txt", FileEntry("f:x.txt", 1, "id-l", "", "")));
        Write(connection, pair, Side.Dropbox, begunEmpty: false,
            new Listed("f:x.txt", FileEntry("f:x.txt", 2, "id-r", "rev-r", "hash-r")));

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 2, "the two sides get separate rows");
        var local = rows.Single(row => row.Side == (int)Side.Local);
        var dropbox = rows.Single(row => row.Side == (int)Side.Dropbox);
        Check.Require(local.OwnKey == PathNames.Key("f:x.txt") && local.Key == local.OwnKey,
            "a local row's join key is its own key");
        Check.Require(dropbox.OwnKey == PathNames.Key("f:x.txt") &&
            dropbox.Key == PathNames.DropboxKey("f:x.txt") && dropbox.Key == dropbox.OwnKey,
            "a Dropbox row's join key is its own frozen key");
        Check.Require(dropbox.Path == "f:x.txt" && dropbox.Rev == "rev-r" && dropbox.Hash == "hash-r",
            "a Dropbox row keeps its listed spelling, revision and hash");
    }

    private static void FileReplacesFolderCases(SqliteConnection connection)
    {
        const string pair = "scan-replace";
        AddPair(connection, pair);
        Write(connection, pair, Side.Local, begunEmpty: false,
            new Listed("d", FolderEntry("d", "id-d")),
            new Listed("d/x.txt", FileEntry("d/x.txt", 1, "id-x", "", "")),
            new Listed("d/y/z.txt", FileEntry("d/y/z.txt", 2, "id-z", "", "")));
        Check.Require(Rows(connection, pair).Count == 3, "a folder and its descendants are listed");

        Write(connection, pair, Side.Local, begunEmpty: false,
            new Listed("d", FileEntry("d", 7, "id-replacement", "", "")));

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 1 && rows[0].Path == "d" && !rows[0].IsFolder && rows[0].Size == 7,
            "a file replaces the folder and deletes its descendants");
    }

    private static void DeleteCases(SqliteConnection connection)
    {
        const string pair = "scan-delete";
        AddPair(connection, pair);
        Write(connection, pair, Side.Local, begunEmpty: false,
            new Listed("d", FolderEntry("d", "id-d")),
            new Listed("d/x.txt", FileEntry("d/x.txt", 1, "id-x", "", "")),
            new Listed("dx", FolderEntry("dx", "id-dx")),
            new Listed("dx/y.txt", FileEntry("dx/y.txt", 2, "id-y", "", "")),
            new Listed("keep.txt", FileEntry("keep.txt", 3, "id-k", "", "")));

        Write(connection, pair, Side.Local, begunEmpty: false, new Listed("d", null));

        var rows = Rows(connection, pair);
        Check.Require(rows.Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal)
            .SequenceEqual(["dx", "dx/y.txt", "keep.txt"]),
            "an absence deletes the path and its descendants but not a shared prefix");

        Write(connection, pair, Side.Local, begunEmpty: false, new Listed("keep.txt", null));
        Check.Require(Rows(connection, pair).Count == 2, "an absence of a file deletes just that row");
    }

    private static void TombstoneRangeCases(SqliteConnection connection)
    {
        foreach (var hasSelf in new[] { false, true })
        {
            var pair = "tombstone-range-" + hasSelf;
            AddPair(connection, pair);
            if (hasSelf)
                Write(connection, pair, Side.Dropbox, begunEmpty: false, new Listed("a%_é", FileEntry("a%_é", 1, "self", "", "")));
            // A listing stream may already contain children when its parent tombstone arrives.
            Write(connection, pair, Side.Dropbox, begunEmpty: false,
                new Listed("a%_é/雪", FileEntry("a%_é/雪", 1, "child", "", "")),
                new Listed("a%_é0", FileEntry("a%_é0", 1, "adjacent", "", "")));
            Write(connection, pair, Side.Dropbox, begunEmpty: false, new Listed("a%_é", null));
            Check.Require(Rows(connection, pair).Select(row => row.Path).SequenceEqual(["a%_é0"]),
                "tombstones remove descendants even without a folder self, preserving the upper range boundary");
        }
    }

    private static void UpsertCases(SqliteConnection connection)
    {
        const string pair = "scan-upsert";
        AddPair(connection, pair);
        Write(connection, pair, Side.Local, begunEmpty: false, new Listed("A.txt", FileEntry("A.txt", 1, "id-old", "", "")));
        Write(connection, pair, Side.Local, begunEmpty: false, new Listed("a.txt", FileEntry("a.txt", 2, "id-new", "", "")));

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 1 && rows[0].Path == "a.txt" && rows[0].Size == 2 && rows[0].Id == "id-new",
            "entries that share an own_key upsert one row");

        var changes = Number(connection, "SELECT total_changes();");
        Write(connection, pair, Side.Local, begunEmpty: false, new Listed("a.txt", FileEntry("a.txt", 2, "id-new", "", "")));
        Check.Require(Number(connection, "SELECT total_changes();") == changes,
            "an identical scan event changes no row");
        Write(connection, pair, Side.Local, begunEmpty: false, new Listed("A.txt", FileEntry("A.txt", 2, "id-new", "", "")));
        Check.Require(Number(connection, "SELECT total_changes();") == changes + 1 &&
            Rows(connection, pair).Single().Path == "A.txt",
            "a case-only spelling change still updates the row");
    }

    // Every persisted field must update the row when it alone differs, and nothing must change on an exact repeat.
    private static void ConflictCases(SqliteConnection connection)
    {
        const string pair = "scan-conflict";
        AddPair(connection, pair);
        var key = PathNames.Key("conflict.txt");

        long Upsert(string path, long size, bool folder, long mtime, string id, string rev, string hash, string? skip)
        {
            var before = Number(connection, "SELECT total_changes();");
            Execute(connection, StateSchema.ScanUpsert,
            (StateSchema.ParamScan, Guid.Empty.ToByteArray()),
                (StateSchema.ParamPair, pair), (StateSchema.ParamSide, (int)Side.Local),
                (StateSchema.ParamJoinKey, key), (StateSchema.ParamOwnKey, key),
                (StateSchema.ParamPath, path), (StateSchema.ParamIsFolder, folder),
                (StateSchema.ParamSize, size), (StateSchema.ParamMtime, mtime),
                (StateSchema.ParamId, id), (StateSchema.ParamRev, rev),
                (StateSchema.ParamHash, hash), (StateSchema.ParamSkip, skip));
            return Number(connection, "SELECT total_changes();") - before;
        }

        StoredRow Stored() => Rows(connection, pair).Single(row => row.Side == (int)Side.Local && row.OwnKey == key);

        Check.Require(Upsert("conflict.txt", 1, false, 100, "id", "rev", "hash", null) == 1,
            "a fresh scan row inserts once");
        Check.Require(Upsert("conflict.txt", 1, false, 100, "id", "rev", "hash", null) == 0,
            "an identical scan event changes no row");
        Check.Require(Upsert("CONFLICT.TXT", 1, false, 100, "id", "rev", "hash", null) == 1 &&
            Stored().Path == "CONFLICT.TXT", "a path-spelling-only change updates the row");
        const string path = "CONFLICT.TXT";
        Check.Require(Upsert(path, 1, true, 100, "id", "rev", "hash", null) == 1 && Stored().IsFolder,
            "a folder-flag-only change updates the row");
        Check.Require(Upsert(path, 2, true, 100, "id", "rev", "hash", null) == 1 && Stored().Size == 2,
            "a size-only change updates the row");
        Check.Require(Upsert(path, 2, true, 101, "id", "rev", "hash", null) == 1 && Stored().Mtime == 101,
            "an mtime-only change updates the row");
        Check.Require(Upsert(path, 2, true, 101, "id2", "rev", "hash", null) == 1 && Stored().Id == "id2",
            "an id-only change updates the row");
        Check.Require(Upsert(path, 2, true, 101, "id2", "rev2", "hash", null) == 1 && Stored().Rev == "rev2",
            "a revision-only change updates the row");
        Check.Require(Upsert(path, 2, true, 101, "id2", "rev2", "hash2", null) == 1 && Stored().Hash == "hash2",
            "a hash-only change updates the row");
        Check.Require(Upsert(path, 2, true, 101, "id2", "rev2", "hash2", "skipped") == 1 &&
            Stored().Skip == "skipped", "a null-to-value skip change updates the row");
        Check.Require(Upsert(path, 2, true, 101, "id2", "rev2", "hash2", null) == 1 && Stored().Skip is null,
            "a value-to-null skip change updates the row");

        var ownKey = PathNames.Key("delta.txt");
        long DropboxUpsert(string joinKey)
        {
            var before = Number(connection, "SELECT total_changes();");
            Execute(connection, StateSchema.ScanUpsert,
            (StateSchema.ParamScan, Guid.Empty.ToByteArray()),
                (StateSchema.ParamPair, pair), (StateSchema.ParamSide, (int)Side.Dropbox),
                (StateSchema.ParamJoinKey, joinKey), (StateSchema.ParamOwnKey, ownKey),
                (StateSchema.ParamPath, "delta.txt"), (StateSchema.ParamIsFolder, false),
                (StateSchema.ParamSize, 1L), (StateSchema.ParamMtime, 100L),
                (StateSchema.ParamId, "d"), (StateSchema.ParamRev, ""), (StateSchema.ParamHash, ""),
                (StateSchema.ParamSkip, null));
            return Number(connection, "SELECT total_changes();") - before;
        }

        StoredRow Dropbox() =>
            Rows(connection, pair).Single(row => row.Side == (int)Side.Dropbox && row.OwnKey == ownKey);

        Check.Require(DropboxUpsert("KEY_ONE") == 1 && Dropbox().Key == "KEY_ONE",
            "a new Dropbox key delta inserts once");
        Check.Require(DropboxUpsert("KEY_ONE") == 0, "an identical Dropbox event changes no row");
        Check.Require(DropboxUpsert("KEY_TWO") == 1 && Dropbox().Key == "KEY_TWO",
            "a key-delta-only change updates the row");
    }

    private static void AbortCases(SqliteConnection connection)
    {
        const string pair = "scan-abort";
        AddPair(connection, pair);
        Write(connection, pair, Side.Local, begunEmpty: false, new Listed("kept.txt", FileEntry("kept.txt", 1, "id-k", "", "")));

        var writer = new ScanWriter(connection, WriteLock, pair, Side.Local, static () => { }, ScanTestSettings.BatchRows);
        Task aborting;
        lock (WriteLock)
        {
            writer.AddAsync(new Listed("dropped.txt", FileEntry("dropped.txt", 1, "id-d", "", "")), default).GetAwaiter().GetResult();
            aborting = writer.AbortAsync();
        }
        aborting.GetAwaiter().GetResult();

        var rows = Rows(connection, pair);
        Check.Require(rows.Count == 1 && rows[0].Path == "kept.txt", "abort discards the remainder");
    }

    // A producer that adds after the writer finished is refused, so a short scan can never be reported complete.
    private static void AddAfterCompleteCases(SqliteConnection connection)
    {
        const string pair = "scan-add-after-complete";
        AddPair(connection, pair);
        var writer = new ScanWriter(connection, WriteLock, pair, Side.Local, static () => { }, ScanTestSettings.BatchRows);
        writer.AddAsync(new Listed("first.txt", FileEntry("first.txt", 1, "id-first", "", "")), default).GetAwaiter().GetResult();
        writer.CompleteAsync().GetAwaiter().GetResult();
        var refused = false;
        try { writer.AddAsync(new Listed("late.txt", FileEntry("late.txt", 2, "id-late", "", "")), default).GetAwaiter().GetResult(); }
        catch (ChannelClosedException) { refused = true; }
        Check.Require(refused, "an Add after the writer finished is refused instead of silently dropped");
        Check.Require(Rows(connection, pair).Select(row => row.Path).SequenceEqual(["first.txt"]),
            "the refused late step is not written");
    }

    // A save that cannot write fails the writer's loop, so CompleteAsync surfaces it (the caller abandons the run).
    private static void WriteFailureCases(SqliteConnection connection)
    {
        const string pair = "scan-failure";
        AddPair(connection, pair);
        Execute(connection, "PRAGMA query_only=ON;");
        try
        {
            var writer = new ScanWriter(connection, WriteLock, pair, Side.Local, static () => { }, ScanTestSettings.BatchRows);
            writer.AddAsync(new Listed("fail.txt", FileEntry("fail.txt", 1, "id-f", "", "")), default).GetAwaiter().GetResult();
            try
            {
                writer.CompleteAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException("a write failure must surface to the caller");
            }
            catch (SqliteException error)
            {
                Check.Require(error.SqliteErrorCode == 8, "the existing scan rejects writes rather than failing schema lookup");
            }
            Check.Require(Rows(connection, pair).Count == 0, "a refused write leaves the scan unchanged");
        }
        finally
        {
            Execute(connection, "PRAGMA query_only=OFF;");
        }
    }

    private static void CachedBatchCases(SqliteConnection connection)
    {
        foreach (var fail in new[] { false, true })
        {
            var pair = "cached-batch-" + fail;
            AddPair(connection, pair);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var first = true;
            var writer = new ScanWriter(connection, WriteLock, pair, Side.Local, static () => { }, ScanTestSettings.BatchRows, attach: () =>
            {
                if (!first) return;
                first = false;
                entered.Set();
                release.Wait();
            });
            writer.AddAsync(new Listed("seed", FileEntry("seed", 1, "seed", "", "")), default).GetAwaiter().GetResult();
            entered.Wait();
            Task completed;
            try
            {
                writer.AddAsync(new Listed("a", FileEntry("a", 1, "a", "", "", TextKey.SkipLink.ToString())), default).GetAwaiter().GetResult();
                writer.AddAsync(new Listed("b", FileEntry("b", 2, fail ? null! : "b", "", "")), default).GetAwaiter().GetResult();
                writer.AddAsync(new Listed("c", FileEntry("c", 3, "c", "", "", TextKey.SkipSpecial.ToString())), default).GetAwaiter().GetResult();
                completed = writer.CompleteAsync();
            }
            finally
            {
                release.Set();
            }
            if (!fail)
            {
                completed.GetAwaiter().GetResult();
                var rows = Rows(connection, pair);
                Check.Require(rows.Single(row => row.Path == "a").Skip == TextKey.SkipLink.ToString() &&
                    rows.Single(row => row.Path == "b").Skip is null &&
                    rows.Single(row => row.Path == "c").Skip == TextKey.SkipSpecial.ToString(),
                    "cached commands rebind non-null, null and non-null values within a batch");
                continue;
            }
            SqliteException failure;
            try
            {
                completed.GetAwaiter().GetResult();
                throw new InvalidOperationException("the malformed second row must fail its batch");
            }
            catch (SqliteException error)
            {
                failure = error;
            }
            Check.Require(Rows(connection, pair).Select(row => row.Path).SequenceEqual(["seed"]),
                "failed cached statements are disposed before the production MEMORY-journalled transaction rolls back");
            try
            {
                writer.AbortAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException("abort must retain the original save failure");
            }
            catch (SqliteException error)
            {
                Check.Require(ReferenceEquals(error, failure), "completion and abort report the same final save failure");
            }
        }
        const string failedPair = "failed-consumer";
        AddPair(connection, failedPair);
        var failed = new ScanWriter(connection, WriteLock, failedPair, Side.Local, static () => { }, ScanTestSettings.BatchRows);
        failed.AddAsync(new Listed("bad", FileEntry("bad", 1, null!, "", "")), default).GetAwaiter().GetResult();
        var loop = (Task)typeof(ScanWriter).GetField("_loop", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.GetValue(failed)!;
        Check.Require(Throws(() => loop.GetAwaiter().GetResult()), "the consumer fails before producer completion");
        var queued = (System.Threading.Channels.Channel<Listed>)typeof(ScanWriter).GetField("_queued",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(failed)!;
        Check.Require(!queued.Writer.TryWrite(new Listed("late", FileEntry("late", 1, "late", "", ""))),
            "a failed consumer closes the producer side instead of retaining later entries");
        Check.Require(Throws(() => failed.CompleteAsync().GetAwaiter().GetResult()),
            "producer completion still receives the save failure");
    }

    // The queue holds one write batch: a producer waits while it is full and resumes as the consumer drains, so a
    // listing larger than the batch is committed in chunks with the same final rows and order.
    private static void DrainBatchCases(SqliteConnection connection)
    {
        const string pair = "scan-drain-batch";
        const int batchRows = 4;
        AddPair(connection, pair);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = true;
        var drains = 0;
        var writer = new ScanWriter(connection, WriteLock, pair, Side.Local, static () => { }, batchRows, attach: () =>
        {
            drains++;
            if (!first) return;
            first = false;
            entered.Set();
            release.Wait();
        });
        // Hold the seed's drain so the tested events queue while the consumer is paused.
        writer.AddAsync(new Listed("seed.txt", FileEntry("seed.txt", 1, "seed", "", "")), default).GetAwaiter().GetResult();
        entered.Wait();
        Task extra;
        Task completed;
        try
        {
            for (var index = 0; index < batchRows; index++)
                writer.AddAsync(new Listed($"n{index:D5}", FileEntry($"n{index:D5}", index, $"id-{index:D5}", "", "")), default)
                    .GetAwaiter().GetResult();
            extra = writer.AddAsync(
                new Listed($"n{batchRows:D5}", FileEntry($"n{batchRows:D5}", batchRows, $"id-{batchRows:D5}", "", "")), default);
            Check.Require(!extra.IsCompleted, "a submission to the full queue waits instead of growing it");
            release.Set();
            extra.GetAwaiter().GetResult();
            completed = writer.CompleteAsync();
        }
        finally
        {
            release.Set();
        }
        completed.GetAwaiter().GetResult();

        Check.Require(drains == 3, "a queue larger than the write batch is committed in chunks");
        var expected = Enumerable.Range(0, batchRows + 1)
            .Select(index => new StoredRow((int)Side.Local, PathNames.Key($"n{index:D5}"),
                PathNames.KeyFor(Side.Local, $"n{index:D5}"), $"n{index:D5}", false, index, Modified.UtcTicks,
                $"id-{index:D5}", "", "", null))
            .Append(new StoredRow((int)Side.Local, PathNames.Key("seed.txt"),
                PathNames.KeyFor(Side.Local, "seed.txt"), "seed.txt", false, 1, Modified.UtcTicks, "seed", "", "", null))
            .OrderBy(row => row.OwnKey, StringComparer.Ordinal).ToArray();
        Check.Require(Rows(connection, pair).SequenceEqual(expected),
            "a chunked drain writes the same final rows in the same order");
    }

    // A producer waiting on the full queue leaves on cancellation without writing; the rest still commits.
    private static void FullQueueCancelCases(SqliteConnection connection)
    {
        const string pair = "scan-full-cancel";
        const int batchRows = 2;
        AddPair(connection, pair);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = true;
        var writer = new ScanWriter(connection, WriteLock, pair, Side.Local, static () => { }, batchRows, attach: () =>
        {
            if (!first) return;
            first = false;
            entered.Set();
            release.Wait();
        });
        writer.AddAsync(new Listed("seed.txt", FileEntry("seed.txt", 1, "seed", "", "")), default).GetAwaiter().GetResult();
        entered.Wait();
        using var cancellation = new CancellationTokenSource();
        try
        {
            writer.AddAsync(new Listed("a.txt", FileEntry("a.txt", 1, "id-a", "", "")), default).GetAwaiter().GetResult();
            writer.AddAsync(new Listed("b.txt", FileEntry("b.txt", 1, "id-b", "", "")), default).GetAwaiter().GetResult();
            var waiting = writer.AddAsync(new Listed("c.txt", FileEntry("c.txt", 1, "id-c", "", "")), cancellation.Token);
            Check.Require(!waiting.IsCompleted, "the cancelled submission waits on the full queue");
            cancellation.Cancel();
            var cancelled = false;
            try { waiting.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check.Require(cancelled, "a cancelled submission leaves the queue instead of being written");
        }
        finally
        {
            release.Set();
        }
        writer.CompleteAsync().GetAwaiter().GetResult();
        Check.Require(Rows(connection, pair).Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal)
                .SequenceEqual(["a.txt", "b.txt", "seed.txt"]),
            "cancelling an admission writes neither it nor anything after it");
    }

    // The attach callback runs after the consumer has filled its active batch, inside the write lock. Gating there
    // freezes the drain (holding the lock alone does not: the drain precedes it), so the queue cannot change between
    // the snapshot and the abort; aborting reentrantly on that thread closes the writer side before the gate opens.
    private static void FullQueueAbortCases(SqliteConnection connection)
    {
        const string pair = "scan-full-abort";
        const int batchRows = 2;
        AddPair(connection, pair);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var aborted = new ManualResetEventSlim();
        ScanWriter writer = null!;
        Task aborting = null!;
        var first = true;
        writer = new ScanWriter(connection, WriteLock, pair, Side.Local, static () => { }, batchRows, attach: () =>
        {
            if (!first) return;
            first = false;
            entered.Set();
            release.Wait();
            aborting = writer.AbortAsync();
            aborted.Set();
        });
        // The seed fills the active batch, so the gated consumer holds no queued item and the empty queue takes the
        // next batchRows admissions; the one after that must wait.
        writer.AddAsync(new Listed("seed.txt", FileEntry("seed.txt", 1, "seed", "", "")), default).GetAwaiter().GetResult();
        entered.Wait();
        var admissions = new List<Task>();
        for (var index = 0; index < batchRows; index++)
            admissions.Add(writer.AddAsync(
                new Listed($"n{index:D2}", FileEntry($"n{index:D2}", index, $"id-{index:D2}", "", "")), default));
        foreach (var admission in admissions) admission.GetAwaiter().GetResult();
        admissions.Add(writer.AddAsync(
            new Listed($"n{batchRows:D2}", FileEntry($"n{batchRows:D2}", batchRows, $"id-{batchRows:D2}", "", "")), default));
        var waiting = admissions.Where(admission => !admission.IsCompleted).ToArray();
        Check.Require(waiting.Length == 1, "exactly the admission beyond the full queue waits on the gated consumer");
        release.Set();
        aborted.Wait();
        aborting.GetAwaiter().GetResult();
        var rejected = 0;
        foreach (var admission in admissions)
        {
            try { admission.GetAwaiter().GetResult(); }
            catch (ChannelClosedException) { rejected++; }
        }
        Check.Require(rejected == waiting.Length, "abort rejects exactly the admissions that were waiting");
        Check.Require(Rows(connection, pair).Select(row => row.Path).SequenceEqual(["seed.txt"]),
            "abort discards everything the aborted writer held and keeps only the seed it had applied");
    }

    // A failed consumer faults the queue with its own save error, so a waiting producer wakes with that failure.
    private static void FullQueueFailureCases(SqliteConnection connection)
    {
        const string pair = "scan-full-failure";
        const int batchRows = 2;
        AddPair(connection, pair);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = true;
        var writer = new ScanWriter(connection, WriteLock, pair, Side.Local, static () => { }, batchRows, attach: () =>
        {
            if (!first) return;
            first = false;
            entered.Set();
            release.Wait();
        });
        writer.AddAsync(new Listed("bad", FileEntry("bad", 1, null!, "", "")), default).GetAwaiter().GetResult();
        entered.Wait();
        Task waiting;
        try
        {
            writer.AddAsync(new Listed("a.txt", FileEntry("a.txt", 1, "id-a", "", "")), default).GetAwaiter().GetResult();
            writer.AddAsync(new Listed("b.txt", FileEntry("b.txt", 1, "id-b", "", "")), default).GetAwaiter().GetResult();
            waiting = writer.AddAsync(new Listed("c.txt", FileEntry("c.txt", 1, "id-c", "", "")), default);
            Check.Require(!waiting.IsCompleted, "the failure case waits on the full queue");
        }
        finally
        {
            release.Set();
        }
        SqliteException? woken = null;
        try { waiting.GetAwaiter().GetResult(); }
        catch (SqliteException error) { woken = error; }
        SqliteException? failure = null;
        try { writer.CompleteAsync().GetAwaiter().GetResult(); }
        catch (SqliteException error) { failure = error; }
        Check.Require(failure is not null, "the save failure reaches completion");
        Check.Require(woken is not null && ReferenceEquals(woken, failure),
            "the waiting producer wakes with the original save failure");
        Check.Require(Rows(connection, pair).Count == 0, "the failed batch leaves no row");
    }

    // Events of different kinds cross chunk boundaries; a later chunk's failure rolls back only that chunk.
    private static void MixedBoundaryCases(SqliteConnection connection)
    {
        const string pair = "scan-mixed-boundary";
        const int batchRows = 3;
        AddPair(connection, pair);
        var pairRow = PairRowId(connection, pair);
        Listed[][] chunks =
        [
            [new Listed("d", FolderEntry("d", "id-d")), new Listed("d/child", FileEntry("d/child", 1, "id-child", "", "")),
                new Listed("d", null)],
            [new Listed("d", null), new Listed("d", FolderEntry("d", "id-d2")),
                new Listed("a", FileEntry("a", 2, "id-a", "", ""))],
            [new Listed("ab", FileEntry("ab", 3, "id-ab", "", "")), new Listed("a", null),
                new Listed("b", FileEntry("b", 4, "id-b", "", ""))],
            [new Listed("ab", null), new Listed("d/child", FileEntry("d/child", 5, "id-child2", "", "")),
                new Listed("bad", FileEntry("bad", 1, null!, "", ""))]
        ];
        using var gate = new SemaphoreSlim(0);
        using var entered = new ManualResetEventSlim();
        var writer = new ScanWriter(connection, WriteLock, pair, Side.Dropbox, static () => { }, batchRows, attach: () =>
        {
            entered.Set();
            gate.Wait();
        }, deletedNamePairRow: pairRow);
        void Queue(IEnumerable<Listed> items)
        {
            foreach (var item in items)
                writer.AddAsync(item, default).GetAwaiter().GetResult();
        }

        // A one-event seed lets every later chunk be queued whole while the consumer is paused, so each chunk holds
        // exactly the events written between two gates.
        Queue([new Listed("seed", FileEntry("seed", 0, "id-seed", "", ""))]);
        entered.Wait();
        entered.Reset();
        foreach (var chunk in chunks)
        {
            Queue(chunk);
            gate.Release();
            entered.Wait();
            entered.Reset();
        }
        gate.Release();
        try
        {
            writer.CompleteAsync().GetAwaiter().GetResult();
            throw new InvalidOperationException("the later malformed row must fail its chunk");
        }
        catch (SqliteException)
        {
        }

        Check.Require(Rows(connection, pair).Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal)
                .SequenceEqual(["ab", "b", "d", "seed"]),
            "committed chunks apply parent and child tombstones, recreation and the surviving prefix neighbour");
        Check.Require(Markers(connection, pair).SequenceEqual(
                new[] { "a", "d" }.Select(PathNames.Key).OrderBy(key => key, StringComparer.Ordinal)),
            "a duplicate marker is kept once, and a later failing chunk rolls back its own marker");
    }

    // #129: a scan that began empty is not provably childless once its own events have inserted descendants.
    private static void FreshDescendantCases(SqliteConnection connection)
    {
        var replacement = FileEntry("a", 7, "id-a-new", "rev-new", "hash-new");

        // A child listed before its parent file, with no parent folder event of any kind.
        const string childFirst = "fresh-child-first";
        AddPair(connection, childFirst);
        Write(connection, childFirst, Side.Local, begunEmpty: true,
            new Listed("a/b", FileEntry("a/b", 1, "id-ab", "", "")), new Listed("a", replacement));
        Check.Require(Rows(connection, childFirst).Select(row => row.Path).SequenceEqual(["a"]),
            "a fresh scan's later file removes a child listed before it");

        // A folder and a deeper descendant, then the file.
        const string folderFirst = "fresh-folder-first";
        AddPair(connection, folderFirst);
        Write(connection, folderFirst, Side.Local, begunEmpty: true,
            new Listed("a", FolderEntry("a", "id-a")),
            new Listed("a/b", FileEntry("a/b", 1, "id-ab", "", "")),
            new Listed("a/b/c", FileEntry("a/b/c", 2, "id-abc", "", "")),
            new Listed("a", replacement));
        Check.Require(Rows(connection, folderFirst).Select(row => row.Path).SequenceEqual(["a"]),
            "a fresh scan's later file removes a folder's remaining descendants");

        // The same sequence across real batches of one writer: batchRows 1 commits one event per transaction.
        const string acrossBatches = "fresh-across-batches";
        AddPair(connection, acrossBatches);
        var writer = new ScanWriter(connection, WriteLock, acrossBatches, Side.Local, static () => { },
            batchRows: 1, begunEmpty: true);
        writer.AddAsync(new Listed("a/b", FileEntry("a/b", 1, "id-ab", "", "")), default).GetAwaiter().GetResult();
        writer.AddAsync(new Listed("a", replacement), default).GetAwaiter().GetResult();
        writer.CompleteAsync().GetAwaiter().GetResult();
        Check.Require(Rows(connection, acrossBatches).Select(row => row.Path).SequenceEqual(["a"]),
            "the probe sees a descendant committed by an earlier batch of the same fresh writer");

        // An identical re-upsert of the file changes no row but must still remove the child; a RETURNING-only
        // probe would miss it.
        const string identicalAgain = "fresh-identical-again";
        AddPair(connection, identicalAgain);
        var same = FileEntry("a", 7, "id-a-new", "rev-new", "hash-new");
        Write(connection, identicalAgain, Side.Local, begunEmpty: true,
            new Listed("a", same), new Listed("a/b", FileEntry("a/b", 1, "id-ab", "", "")), new Listed("a", same));
        Check.Require(Rows(connection, identicalAgain).Select(row => row.Path).SequenceEqual(["a"]),
            "an identical file event still removes a descendant added after it");

        // Deep removal leaves prefix neighbours on the same side and other sides and pairs untouched.
        const string ranges = "fresh-ranges";
        AddPair(connection, ranges);
        AddPair(connection, "fresh-ranges-other");
        Write(connection, ranges, Side.Dropbox, begunEmpty: false, new Listed("a/b", FileEntry("a/b", 1, "id-dbox-child", "", "")));
        Write(connection, "fresh-ranges-other", Side.Local, begunEmpty: false,
            new Listed("a/b", FileEntry("a/b", 2, "id-other-child", "", "")));
        Write(connection, ranges, Side.Local, begunEmpty: false, new Listed("keep", FileEntry("keep", 1, "id-keep", "", "")));
        Write(connection, ranges, Side.Local, begunEmpty: true,
            new Listed("a", FolderEntry("a", "id-a")),
            new Listed("a/b", FileEntry("a/b", 1, "id-ab", "", "")),
            new Listed("a/b/c", FileEntry("a/b/c", 2, "id-abc", "", "")),
            new Listed("ab", FileEntry("ab", 3, "id-ab-neighbour", "", "")),
            new Listed("a0", FileEntry("a0", 4, "id-a0", "", "")),
            new Listed("a", replacement));
        Check.Require(Rows(connection, ranges).Where(row => row.Side == (int)Side.Local).Select(row => row.Path)
                .OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(["a", "a0", "ab", "keep"]),
            "a fresh file removes every descendant but no prefix neighbour");
        Check.Require(Rows(connection, ranges).Where(row => row.Side == (int)Side.Dropbox)
                .Select(row => (row.Path, row.Id)).SequenceEqual([("a/b", "id-dbox-child")]),
            "the opposite side's descendant is untouched");
        Check.Require(Rows(connection, "fresh-ranges-other")
                .Select(row => (row.Path, row.Id)).SequenceEqual([("a/b", "id-other-child")]),
            "another pair's descendant is untouched");

        // Case variants, pattern characters and supplementary characters use the native-key range, never LIKE.
        const string natives = "fresh-natives";
        AddPair(connection, natives);
        Write(connection, natives, Side.Local, begunEmpty: true,
            new Listed("A%_é", FolderEntry("A%_é", "id-native")),
            new Listed("A%_é/𝄞", FileEntry("A%_é/𝄞", 1, "id-music", "", "")),
            new Listed("A%_é/雪", FileEntry("A%_é/雪", 2, "id-snow", "", "")),
            new Listed("A%_é0", FileEntry("A%_é0", 3, "id-adjacent", "", "")),
            new Listed("a%_é", FileEntry("a%_é", 4, "id-replacement", "", "")));
        Check.Require(Rows(connection, natives).Select(row => row.Path)
                .OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(["A%_é0", "a%_é"]),
            "a case-variant file removes native-key descendants and keeps the upper boundary neighbour");

        // A fresh leaf writes only its own row, and an identical repeat changes none. The traced fixture below
        // proves the leaf's statement count.
        const string freshLeaf = "fresh-leaf";
        AddPair(connection, freshLeaf);
        var before = Number(connection, "SELECT total_changes();");
        Write(connection, freshLeaf, Side.Local, begunEmpty: true, new Listed("leaf", FileEntry("leaf", 1, "id-leaf", "", "")));
        Check.Require(Number(connection, "SELECT total_changes();") == before + 1 &&
            Rows(connection, freshLeaf).Select(row => row.Path).SequenceEqual(["leaf"]),
            "a fresh leaf insert changes only its own row");
        Write(connection, freshLeaf, Side.Local, begunEmpty: true, new Listed("leaf", FileEntry("leaf", 1, "id-leaf", "", "")));
        Check.Require(Number(connection, "SELECT total_changes();") == before + 1,
            "an identical fresh leaf event changes no row");
    }

    // #129 statement counts, traced the same way StoredFolderPlanChecks counts frozen reads. Row counts cannot
    // distinguish an executed empty DELETE from a skipped one; the trace reports the statement itself.
    private static void FreshStatementCases(SqliteConnection connection)
    {
        var traced = new List<string>();
        // Trace expands bound parameters, so identify each statement before its first parameter.
        int Probes() => traced.Count(sql =>
            sql.StartsWith(StateSchema.ScanHasDescendants.Split(StateSchema.ParamPair)[0], StringComparison.Ordinal));
        int DescendantDeletes() => traced.Count(sql =>
            sql.StartsWith(StateSchema.ScanDeleteDescendants.Split(StateSchema.ParamPair)[0], StringComparison.Ordinal));
        SQLitePCL.raw.sqlite3_trace(connection.Handle,
            (SQLitePCL.delegate_trace)((_, sql) => traced.Add(sql.utf8_to_string())), null);
        try
        {
            const string leaf = "traced-fresh-leaf";
            AddPair(connection, leaf);
            traced.Clear();
            Write(connection, leaf, Side.Local, begunEmpty: true, new Listed("leaf", FileEntry("leaf", 1, "id-leaf", "", "")));
            Check.Require(Probes() == 1 && DescendantDeletes() == 0,
                "a fresh leaf runs one existence probe and no descendant delete");

            const string replaced = "traced-fresh-replacement";
            AddPair(connection, replaced);
            traced.Clear();
            Write(connection, replaced, Side.Local, begunEmpty: true,
                new Listed("a/b", FileEntry("a/b", 1, "id-ab", "", "")),
                new Listed("a", FileEntry("a", 2, "id-a", "", "")));
            Check.Require(Probes() == 2 && DescendantDeletes() == 1,
                "each fresh file probes once and only the replacement deletes a descendant");

            const string seeded = "traced-nonempty";
            AddPair(connection, seeded);
            Write(connection, seeded, Side.Local, begunEmpty: false, new Listed("a/b", FileEntry("a/b", 1, "id-ab", "", "")));
            traced.Clear();
            Write(connection, seeded, Side.Local, begunEmpty: false, new Listed("a", FileEntry("a", 2, "id-a", "", "")));
            Check.Require(Probes() == 0 && DescendantDeletes() == 1,
                "a non-empty scan file keeps the unconditional delete and adds no probe");
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null);
        }
    }

    private static void OrderedEventCases(SqliteConnection connection)
    {
        var folder = FolderEntry("d", "folder");
        var child = FileEntry("d/child", 3, "child", "rev-child", "hash-child");
        var neighbor = FileEntry("dx/child", 4, "neighbor", "", "");
        var replacement = FileEntry("d", 7, "replacement", "rev-new", "hash-new");
        var unreadable = folder with { Skip = TextKey.SkipUnreadable.ToString() };
        var earlier = FileEntry("A.txt", 1, "old", "rev-old", "hash-old", TextKey.SkipLink.ToString());
        var later = FileEntry("a.txt", 2, "new", "rev-new", "hash-new") with
        {
            Modified = Modified.AddTicks(1)
        };
        var invalidFolder = FolderEntry("f:x", "invalid-folder");
        var invalidChild = FileEntry("f:x/child", 5, "invalid-child", "rev-invalid", "hash-invalid");
        var underscoreFolder = FolderEntry("f_x", "underscore-folder");
        var underscoreChild = FileEntry("f_x/child", 6, "underscore-child", "", "");
        (string Name, Listed[] Events, Entry[] Expected)[] cases =
        [
            ("parent-tombstone", [new(folder.Path, folder), new(child.Path, child),
                new(neighbor.Path, neighbor), new(folder.Path, null)], [neighbor]),
            ("absent-parent-tombstone", [new(child.Path, child), new(neighbor.Path, neighbor),
                new(folder.Path, null)], [neighbor]),
            ("tombstone-then-child", [new(child.Path, child), new(folder.Path, null),
                new(child.Path, child)], [child]),
            ("child-before-file-parent", [new(child.Path, child), new(replacement.Path, replacement)],
                [replacement]),
            ("file-replaces-folder", [new(folder.Path, folder), new(child.Path, child),
                new(replacement.Path, replacement)], [replacement]),
            ("folder-replaces-file", [new(replacement.Path, replacement), new(folder.Path, folder),
                new(child.Path, child)], [folder, child]),
            ("equal-key-history", [new(earlier.Path, earlier), new(later.Path, later),
                new(later.Path, later)], [later]),
            ("unreadable-before-child", [new(folder.Path, folder), new(folder.Path, unreadable),
                new(child.Path, child)], [unreadable, child]),
            ("unreadable-after-child", [new(folder.Path, folder), new(child.Path, child),
                new(folder.Path, unreadable), new(folder.Path, unreadable)], [unreadable, child]),
            ("readable-again", [new(folder.Path, unreadable), new(child.Path, child),
                new(folder.Path, folder)], [folder, child]),
            ("invalid-beside-underscore", [new(invalidFolder.Path, invalidFolder), new(underscoreFolder.Path, underscoreFolder),
                new(invalidChild.Path, invalidChild), new(underscoreChild.Path, underscoreChild)],
                [invalidFolder, underscoreFolder, invalidChild, underscoreChild]),
            ("invalid-tombstone-beside-underscore", [new(invalidFolder.Path, invalidFolder), new(underscoreFolder.Path, underscoreFolder),
                new(invalidChild.Path, invalidChild), new(underscoreChild.Path, underscoreChild),
                new(invalidFolder.Path, null)], [underscoreFolder, underscoreChild])
        ];

        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var begunEmpty in new[] { false, true })
        foreach (var test in cases)
        {
            var expected = test.Expected
                .Select(entry => new StoredRow((int)side, PathNames.Key(entry.Path),
                    PathNames.KeyFor(side, entry.Path), entry.Path, entry.IsFolder, entry.Size,
                    entry.Modified.UtcTicks, entry.Id, entry.Revision, entry.ContentHash, entry.Skip))
                .OrderBy(row => row.OwnKey, StringComparer.Ordinal).ToArray();
            foreach (var together in new[] { false, true })
            {
                var pair = $"ordered-{test.Name}-{side}-{begunEmpty}-{together}";
                AddPair(connection, pair);
                WriteOrdered(connection, pair, side, begunEmpty, together, test.Events);
                var rows = Rows(connection, pair);
                Check.Require(rows.SequenceEqual(expected),
                    $"{pair}: ordered ingestion preserves every expected row and field");
            }
        }
    }

    private static void WriteOrdered(SqliteConnection connection, string pair, Side side,
        bool begunEmpty, bool together, Listed[] items, long? deletedNamePairRow = null)
    {
        if (!together)
        {
            foreach (var item in items)
            {
                var single = new ScanWriter(connection, WriteLock, pair, side, static () => { },
                    ScanTestSettings.BatchRows, begunEmpty, deletedNamePairRow: deletedNamePairRow);
                single.AddAsync(item, default).GetAwaiter().GetResult();
                single.CompleteAsync().GetAwaiter().GetResult();
            }
            return;
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = true;
        var writer = new ScanWriter(connection, WriteLock, pair, side, static () => { }, ScanTestSettings.BatchRows, begunEmpty, attach: () =>
        {
            if (!first) return;
            first = false;
            entered.Set();
            release.Wait();
        }, deletedNamePairRow: deletedNamePairRow);
        // Hold a one-tombstone warm-up batch (absent path) so every tested event lands in the next batch.
        writer.AddAsync(new Listed("batch-boundary", null), default).GetAwaiter().GetResult();
        entered.Wait();
        Task completed;
        try
        {
            foreach (var item in items) writer.AddAsync(item, default).GetAwaiter().GetResult();
            completed = writer.CompleteAsync();
        }
        finally
        {
            release.Set();
        }
        completed.GetAwaiter().GetResult();
    }

    // Deleted-name markers and their live-row effects commit atomically, regardless of insertion order.
    private static void DeletedNameCases(SqliteConnection connection)
    {
        // An ordinary writer never receives the numeric discovery row, so it must not touch the relation's table.
        const string plain = "deleted-plain";
        AddPair(connection, plain);
        Write(connection, plain, Side.Dropbox, begunEmpty: false,
            new Listed("gone", null),
            new Listed("kept", FileEntry("kept", 1, "id-k", "", "")));
        Check.Require(Rows(connection, plain).Select(row => row.Path).SequenceEqual(["kept"]),
            "a null carrier still tombstones when the deleted-name relation does not exist");

        // CREATE TABLE is not idempotent: this is the one creation, after the no-table case above.
        Execute(connection, StateSchema.CreateDeletedName);

        const string nonDiscovery = "deleted-non-discovery";
        AddPair(connection, nonDiscovery);
        Write(connection, nonDiscovery, Side.Dropbox, begunEmpty: false,
            new Listed("live", FileEntry("live", 1, "id-l", "", "")),
            new Listed("live", null));
        Check.Require(!Rows(connection, nonDiscovery).Any() && Markers(connection, nonDiscovery).Count == 0,
            "a Dropbox writer without a carrier tombstones and records nothing");

        (string Name, Listed[] Events, string[] Live, string[] Markers)[] cases =
        [
            ("absent-self", [new Listed("gone", null)], [], ["gone"]),
            ("duplicate", [new Listed("gone", null), new Listed("gone", null)], [], ["gone"]),
            ("parent-tombstone", [new Listed("d", FolderEntry("d", "id-d")),
                new Listed("d/child", FileEntry("d/child", 1, "id-c", "", "")), new Listed("d", null)], [], ["d"]),
            ("child-before-parent", [new Listed("d/child", FileEntry("d/child", 1, "id-c", "", "")),
                new Listed("d", null)], [], ["d"]),
            ("tombstone-then-child", [new Listed("d", null),
                new Listed("d/child", FileEntry("d/child", 1, "id-c", "", ""))], ["d/child"], ["d"]),
            ("recreation", [new Listed("d", FolderEntry("d", "id-d")), new Listed("d", null),
                new Listed("d", FolderEntry("d", "id-d2"))], ["d"], ["d"]),
            ("prefix-neighbor", [new Listed("a", FileEntry("a", 1, "id-a", "", "")),
                new Listed("ab", FileEntry("ab", 2, "id-ab", "", "")), new Listed("a", null)], ["ab"], ["a"]),
            ("mixed-batch", [new Listed("x", null), new Listed("y", null),
                new Listed("z", FileEntry("z", 3, "id-z", "", ""))], ["z"], ["x", "y"])
        ];

        foreach (var together in new[] { false, true })
        foreach (var test in cases)
        {
            var pair = $"deleted-{test.Name}-{together}";
            AddPair(connection, pair);
            var row = PairRowId(connection, pair);
            if (together)
                WriteMarked(connection, pair, row, test.Events);
            else
                foreach (var item in test.Events)
                    WriteMarked(connection, pair, row, item);
            var expectedLive = test.Live.OrderBy(path => PathNames.Key(path), StringComparer.Ordinal).ToArray();
            var expectedMarkers = test.Markers.Select(PathNames.Key)
                .OrderBy(key => key, StringComparer.Ordinal).ToArray();
            Check.Require(Rows(connection, pair).Select(stored => stored.Path).SequenceEqual(expectedLive) &&
                Markers(connection, pair).SequenceEqual(expectedMarkers),
                $"{pair}: tombstones keep their deleted names and remove exactly the live rows they cover");
        }

        const string firstPair = "deleted-first";
        const string secondPair = "deleted-second";
        AddPair(connection, firstPair);
        AddPair(connection, secondPair);
        WriteMarked(connection, firstPair, PairRowId(connection, firstPair), new Listed("shared", null));
        WriteMarked(connection, secondPair, PairRowId(connection, secondPair),
            new Listed("shared", null), new Listed("only-second", null));
        Check.Require(Markers(connection, firstPair).SequenceEqual([PathNames.Key("shared")]) &&
            Markers(connection, secondPair).SequenceEqual([PathNames.Key("only-second"), PathNames.Key("shared")]),
            "each pair records only its own deleted names under its numeric row");
    }

    private static void DeletedNameOrderCases(SqliteConnection connection)
    {
        const string pair = "deleted-sorted";
        AddPair(connection, pair);
        var row = PairRowId(connection, pair);
        var child = FileEntry("a/child", 1, "child", "", "");
        Listed[] events = [new("z", null), new(child.Path, child), new("a", null),
            new(child.Path, child), new("b", null), new("z", null), new("\U00010000", null), new("", null)];
        WriteOrdered(connection, pair, Side.Dropbox, false, true, events, row);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path_key FROM scan_state.deleted_name WHERE pair_id=$pair AND path_key<>$warmup ORDER BY rowid;";
            command.Parameters.AddWithValue("$pair", row);
            command.Parameters.AddWithValue("$warmup", PathNames.Key("batch-boundary"));
            using var reader = command.ExecuteReader();
            var inserted = new List<string>();
            while (reader.Read()) inserted.Add(reader.GetString(0));
            Check.Require(inserted.SequenceEqual(events.Where(item => item.Entry is null)
                    .Select(item => PathNames.Key(item.Path)).Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal)),
                "deleted-name set writes are sorted within the batch without dropping Unicode or duplicate markers");
        }
        Check.Require(Rows(connection, pair).Select(item => item.Path).SequenceEqual([child.Path]),
            "sorting markers preserves parent tombstone and later child recreation event order");
        var before = Markers(connection, pair).ToArray();
        Execute(connection, $"CREATE TRIGGER scan_state.reject_sorted_marker BEFORE INSERT ON deleted_name WHEN NEW.path_key='{PathNames.Key("marker-b")}' BEGIN SELECT RAISE(ABORT,'marker failure'); END;");
        try
        {
            Check.Require(Throws(() => WriteOrdered(connection, pair, Side.Dropbox, false, true,
                [new("marker-c", null), new("marker-a", null), new("marker-b", null),
                    new("uncommitted", FileEntry("uncommitted", 1, "new", "", ""))], row)),
                "a failure during sorted marker insertion is surfaced");
            Check.Require(Markers(connection, pair).SequenceEqual(before) &&
                Rows(connection, pair).Select(item => item.Path).SequenceEqual([child.Path]),
                "a marker insertion failure rolls back earlier markers and live changes in the same batch");
        }
        finally { Execute(connection, "DROP TRIGGER scan_state.reject_sorted_marker;"); }
    }

    private static void TombstoneRunOrderCases(SqliteConnection connection)
    {
        const string pair = "tombstone-run-order";
        AddPair(connection, pair);
        string[] paths = ["z", "\U00010000", "", "a"];
        Write(connection, pair, Side.Dropbox, begunEmpty: false,
            paths.Select(path => new Listed(path, FileEntry(path, 1, path, "", ""))).ToArray());
        Execute(connection, "CREATE TABLE scan_state.delete_order(path TEXT);");
        Execute(connection, "CREATE TRIGGER scan_state.trace_delete BEFORE DELETE ON scan BEGIN INSERT INTO delete_order VALUES(OLD.path); END;");
        try
        {
            WriteOrdered(connection, pair, Side.Dropbox, false, true,
                paths.Select(path => new Listed(path, null)).ToArray(), PairRowId(connection, pair));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path FROM scan_state.delete_order ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            var actual = new List<string>();
            while (reader.Read()) actual.Add(reader.GetString(0));
            Check.Require(actual.SequenceEqual(["a", "z", "\U00010000", ""]),
                "contiguous tombstones follow ordinal key order, including supplementary characters");
        }
        finally
        {
            Execute(connection, "DROP TRIGGER scan_state.trace_delete; DROP TABLE scan_state.delete_order;");
        }
    }

    private static void TombstoneRunEqualityCases(SqliteConnection connection)
    {
        const string pair = "tombstone-run-equality";
        AddPair(connection, pair);
        var pairRow = PairRowId(connection, pair);
        var mainBefore = TableBytes(connection, "main");
        byte[]? expected = null;
        foreach (var together in new[] { false, true })
        foreach (var reverse in new[] { false, true })
        {
            Execute(connection, "DELETE FROM scan_state.scan WHERE pair_id=$pair; DELETE FROM scan_state.deleted_name WHERE pair_id=$pair;", ("$pair", pairRow));
            var child = FileEntry("p/child", 5, "child", "revision", "");
            Write(connection, pair, Side.Dropbox, begunEmpty: false, new("p", FolderEntry("p", "parent")),
                new(child.Path, child), new("p/other", FileEntry("p/other", 3, "other", "", "")),
                new("prefix", FileEntry("prefix", 2, "neighbor", "", "")));
            Listed[] deletes = reverse ? [new(child.Path, null), new("p", null)] :
                [new("p", null), new(child.Path, null)];
            Listed[] events = [new("batch-boundary", null), .. deletes,
                new(child.Path, child), new("absent", null), new("absent", null)];
            WriteOrdered(connection, pair, Side.Dropbox, false, together, events, pairRow);
            var actual = TableBytes(connection, "scan_state");
            expected ??= actual;
            Check.Require(actual.SequenceEqual(expected),
                "both parent/child arrival orders and batch boundaries produce byte-identical scan and history table contents");
            Check.Require(TableBytes(connection, "main").SequenceEqual(mainBefore),
                "tombstone ordering leaves every main table unchanged, including issue and change_work");
            Check.Require(Rows(connection, pair).Select(row => row.Path).SequenceEqual([child.Path, "prefix"]),
                "a live recreation splits tombstone runs and retains the unrelated prefix neighbor");
        }
    }

    private static byte[] TableBytes(SqliteConnection connection, string schema)
    {
        using var tables = connection.CreateCommand();
        tables.CommandText = $"SELECT name FROM {schema}.sqlite_master WHERE type='table' ORDER BY name;";
        var names = new List<string>();
        using (var reader = tables.ExecuteReader())
            while (reader.Read()) names.Add(reader.GetString(0));
        var contents = new List<string>();
        foreach (var name in names)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {schema}.\"{name.Replace("\"", "\"\"")}\";";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(System.Text.Json.JsonSerializer.Serialize(values));
            }
            rows.Sort(StringComparer.Ordinal);
            contents.Add(System.Text.Json.JsonSerializer.Serialize(new { name, rows }));
        }
        return System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(contents));
    }

    // A discovery batch remains one transaction: a refused save rolls back its markers and keeps committed ones.
    private static void DeletedNameFailureCases(SqliteConnection connection)
    {
        const string failed = "deleted-failed";
        AddPair(connection, failed);
        var failedRow = PairRowId(connection, failed);
        WriteMarked(connection, failed, failedRow, new Listed("committed", null));
        Check.Require(Markers(connection, failed).SequenceEqual([PathNames.Key("committed")]),
            "a committed discovery batch keeps its deleted name");

        var writer = new ScanWriter(connection, WriteLock, failed, Side.Dropbox, static () => { }, ScanTestSettings.BatchRows, deletedNamePairRow: failedRow);
        writer.AddAsync(new Listed("pending", null), default).GetAwaiter().GetResult();
        writer.AddAsync(new Listed("bad", FileEntry("bad", 1, null!, "", "")), default).GetAwaiter().GetResult();
        SqliteException failure;
        try
        {
            writer.CompleteAsync().GetAwaiter().GetResult();
            throw new InvalidOperationException("a malformed discovery entry must fail its batch");
        }
        catch (SqliteException error)
        {
            failure = error;
        }
        Check.Require(Markers(connection, failed).SequenceEqual([PathNames.Key("committed")]) &&
            !Rows(connection, failed).Any(),
            "a failed discovery batch rolls back its markers and leaves earlier committed markers");
        try
        {
            writer.AbortAsync().GetAwaiter().GetResult();
            throw new InvalidOperationException("abort must retain the original save failure");
        }
        catch (SqliteException error)
        {
            Check.Require(ReferenceEquals(error, failure), "a failed discovery scan reports the same failure on abort");
        }

        const string aborted = "deleted-aborted";
        AddPair(connection, aborted);
        var aborting = new ScanWriter(connection, WriteLock, aborted, Side.Dropbox, static () => { },
            ScanTestSettings.BatchRows, deletedNamePairRow: PairRowId(connection, aborted));
        Task abortingTask;
        lock (WriteLock)
        {
            aborting.AddAsync(new Listed("aborted", null), default).GetAwaiter().GetResult();
            abortingTask = aborting.AbortAsync();
        }
        abortingTask.GetAwaiter().GetResult();
        Check.Require(Markers(connection, aborted).Count == 0 && !Rows(connection, aborted).Any(),
            "abort discards a pending discovery batch and its deleted name");
    }

    private static void WriteMarked(SqliteConnection connection, string pairId, long pairRow, params Listed[] items)
    {
        var writer = new ScanWriter(connection, WriteLock, pairId, Side.Dropbox, static () => { }, ScanTestSettings.BatchRows, deletedNamePairRow: pairRow);
        foreach (var item in items) writer.AddAsync(item, default).GetAwaiter().GetResult();
        writer.CompleteAsync().GetAwaiter().GetResult();
    }

    private static long PairRowId(SqliteConnection connection, string pairId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM pair WHERE pair_id = $p;";
        command.Parameters.AddWithValue(StateSchema.ParamPair, pairId);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> Markers(SqliteConnection connection, string pairId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path_key FROM scan_state.deleted_name
            WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) ORDER BY path_key;
            """;
        command.Parameters.AddWithValue(StateSchema.ParamPair, pairId);
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (SqliteException)
        {
            return true;
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        StateStore.OpenConnection(connection);
        Execute(connection, StateSchema.Pragmas);
        Execute(connection, StateSchema.Create + StateSchema.SchemaAdditions + StateSchema.DeletedHistorySchema +
            StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames + StateSchema.SpentDeletedHistory + StateSchema.LocalPublishedColumns + StateSchema.DiscoveryRequiredColumn);
        Execute(connection, StateSchema.AttachScan, (StateSchema.ParamPath, StateSchema.EmptyScan));
        Execute(connection, StateSchema.ScanPragmas);
        Execute(connection, StateSchema.CreateScan);
        return connection;
    }

    private static void AddPair(SqliteConnection connection, string pairId) =>
        Execute(connection, StateSchema.UpsertPair,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamDef, "{}"));

    private static void Write(SqliteConnection connection, string pairId, Side side, bool begunEmpty,
        params Listed[] items)
    {
        var writer = new ScanWriter(connection, WriteLock, pairId, side, static () => { },
            ScanTestSettings.BatchRows, begunEmpty: begunEmpty);
        foreach (var item in items) writer.AddAsync(item, default).GetAwaiter().GetResult();
        writer.CompleteAsync().GetAwaiter().GetResult();
    }

    private static long Number(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<StoredRow> Rows(SqliteConnection connection, string pairId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT side, own_key, key, path, is_folder, size, mtime, id, rev, hash, skip
            FROM scan WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) ORDER BY side, own_key;
            """;
        command.Parameters.AddWithValue(StateSchema.ParamPair, pairId);
        using var reader = command.ExecuteReader();
        var rows = new List<StoredRow>();
        while (reader.Read())
            rows.Add(new StoredRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4) != 0, reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7),
                reader.GetString(8), StateEncoding.ReadHash(reader, 9), reader.IsDBNull(10) ? null : reader.GetString(10)));
        return rows;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] arguments)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static Entry FileEntry(string path, long size, string id, string rev, string hash, string? skip = null) =>
        new(path, false, size, Modified, id, rev, hash, skip);

    private static Entry FolderEntry(string path, string id) => new(path, true, 0, default, id, "", "", null);

    private sealed record StoredRow(int Side, string OwnKey, string Key, string Path, bool IsFolder, long Size,
        long Mtime, string Id, string Rev, string Hash, string? Skip);
}
