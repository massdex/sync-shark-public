using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Requirement-derived persistence oracles through the real Store, including reopen and refusal preimages.</summary>
public static class StateStoreChecks
{
    // These are synthetic identities and boundary fixtures, not operating limits.
    private const string PairId = "pair-a";
    private const string OtherPair = "pair-b";
    private const string Account = "account";
    private const string LocalRoot = "X:/Parent";
    private const string RemoteRoot = "/Parent";
    private const string Parent = "Parent";
    private const string Child = "Parent/Child";
    private const string Sibling = "Parent0";
    private const string Moved = "Moved";
    private const string Other = "Other";
    private const string LocalId = "local-id";
    private const string RemoteId = "remote-id";
    private const string Revision = "revision";
    private const string Cursor = "cursor-a";
    private const string NextCursor = "cursor-b";
    private const string EmptyPageCursor = "cursor-c";
    private const string Namespace = "namespace";
    private const string Domain = "1";
    private const string Stage = "stage";
    private const string InvalidName = "A:B";
    private const string MappedName = "A_B";
    private const string ValidName = "AB";
    private const string AlternateKey = "OTHER-KEY";
    private const string KeepRule = "FileNamePrefix:cache";
    private const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";
    // Encoding version and one-item/page fixtures derive from the corresponding public contracts.
    private const int PendingVersion = 1;
    private const int Single = 1;
    private const int BothSides = 2;
    private const long BeforeFirstOrdinal = -1;
    private static readonly RowContext Context = new(false, false, false, null, false, false, false, false);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var failures = new List<Exception>();
        try
        {
            foreach (var test in new Func<string, Task>[]
            {
                DisposedRecordCases, IssueRollbackCase, IssueUpgradeCases, BusyReaderCases, BasicCases, MappingCases, ScanCases, DeletedHistoryCases, DeletedHistoryBatchCases, DeletedHistoryUpgradeCases, LocalPublishedUpgradeCases, ScanRecordCases, SkipCases, ProtectingMemberCases, MemberlessUpgradeCases, UnreadableCases, RecordCases, GroupedRecordCases, GroupedAdoptionCases, MarkerCases,
                PendingCases, PageCases, JournalCases, PageWaitCases, CompletionCases, ClaimedRepairCases, DropboxBookmarkCases, DropboxResetRetryCases, ConfigurationCases,
                ItemCases, FolderCases, CounterpartPagesCases, CounterpartSiblingAnchorCases, UpgradeCases, PhaseIndexUpgradeCases, CodecCases, LegacyPendingCases, KeepRulesCases,
                PendingAcknowledgmentCases, UnsavedPendingBindingRaces, CrossedPendingReplacement, SelectedPendingSettlement, RootContextCases, FolderMemberOrdinalCases, FolderSettlementCases, FolderBatchSettlementCases, InvalidJobCases, FolderRefusalTypeCases, BeginFolderRemovalCancellationCase, BeginFolderRemovalMidCancellationCase, DecoratedCases, PostCommitCases
            })
            {
                try { await test(Path.Combine(folder, test.Method.Name + StateSchema.DatabaseExtension)); }
                catch (Exception error) { failures.Add(new InvalidOperationException(test.Method.Name, error)); }
            }
            try { ForgetUnreadableChecks.Run(); }
            catch (Exception error) { failures.Add(new InvalidOperationException(nameof(ForgetUnreadableChecks), error)); }
            try { CompactStorageChecks.Run(); }
            catch (Exception error) { failures.Add(new InvalidOperationException(nameof(CompactStorageChecks), error)); }
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    // SQLitePCLRaw exposes busy_timeout but not busy_handler; the fixture needs the contention callback.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BusyHandler(IntPtr state, int retries);

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_busy_handler", CallingConvention = CallingConvention.Cdecl)]
    private static extern int InstallBusyHandler(IntPtr database, BusyHandler handler, IntPtr state);

    private static Task BusyReaderCases(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One second is a test fixture timeout; production inherits the provider's default.
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, DefaultTimeout = Single }.ToString());
        StateStore.OpenConnection(writer);
        using (var timeout = StorageCommand.Create(writer, null, "PRAGMA busy_timeout;"))
            Check.Require(Convert.ToInt64(timeout.ExecuteScalar()) == TimeSpan.FromSeconds(writer.DefaultTimeout).TotalMilliseconds,
                nameof(BusyReaderCases));
        Execute(writer, StateSchema.Pragmas);
        var scanPath = path + StateSchema.PlanFileSuffix;
        Execute(writer, StateSchema.AttachScan, (StateSchema.ParamPath, scanPath));
        Execute(writer, StateSchema.ScanPragmas);
        Execute(writer, "CREATE TABLE main.probe(value INTEGER); INSERT INTO main.probe VALUES(0); " +
            "CREATE TABLE scan_state.probe(value INTEGER); INSERT INTO scan_state.probe VALUES(0);");
        using var outside = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = scanPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        outside.Open();
        using var select = outside.CreateCommand();
        select.CommandText = "SELECT value FROM probe;";
        using (var reader = select.ExecuteReader())
        {
            Check.Require(reader.Read(), nameof(BusyReaderCases));
            var contended = false;
            // Release the other connection only once SQLite actually encounters its read lock.
            BusyHandler releaseReader = (_, _) =>
            {
                contended = true;
                reader.Dispose();
                return Single;
            };
            SqliteException.ThrowExceptionForRC(InstallBusyHandler(writer.Handle!.DangerousGetHandle(),
                releaseReader, IntPtr.Zero), writer.Handle);
            try
            {
                Execute(writer, "UPDATE scan_state.probe SET value=1;");
                Check.Require(contended, nameof(BusyReaderCases));
            }
            finally
            {
                SqliteException.ThrowExceptionForRC(SQLitePCL.raw.sqlite3_busy_timeout(writer.Handle,
                    checked((int)TimeSpan.FromSeconds(writer.DefaultTimeout).TotalMilliseconds)), writer.Handle);
                GC.KeepAlive(releaseReader);
            }
        }
        using (var reader = select.ExecuteReader())
        {
            Check.Require(reader.Read() && reader.GetInt64(0) == Single, nameof(BusyReaderCases));
            using var transaction = writer.BeginTransaction();
            using var update = StorageCommand.Create(writer, transaction,
                "UPDATE main.probe SET value=1; UPDATE scan_state.probe SET value=2;");
            try
            {
                update.ExecuteNonQuery();
                transaction.Commit();
                throw new InvalidOperationException(nameof(BusyReaderCases));
            }
            catch (SqliteException error)
            {
                Check.Require(error.SqliteErrorCode == SQLitePCL.raw.SQLITE_BUSY, nameof(BusyReaderCases));
                transaction.Rollback();
            }
        }
        Check.Require(Snapshot(writer, "SELECT value FROM main.probe;") == "[[0]]" &&
            Snapshot(writer, "SELECT value FROM scan_state.probe;") == "[[1]]" &&
            Snapshot(writer, "PRAGMA main.integrity_check;") == "[[\"ok\"]]" &&
            Snapshot(writer, "PRAGMA scan_state.integrity_check;") == "[[\"ok\"]]", nameof(BusyReaderCases));
        Execute(writer, "UPDATE scan_state.probe SET value=2;");
        Check.Require(Snapshot(writer, "SELECT value FROM scan_state.probe;") == "[[2]]", nameof(BusyReaderCases));
        return Task.CompletedTask;
    }

    private static async Task DeletedHistoryCases(string path)
    {
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) is null, "No completed history before acquisition.");
            var sink = store.OpenScan(PairId, Side.Dropbox, ScanTestSettings.BatchRows);
            await sink.AddAsync(new Listed(Parent, null), CancellationToken.None);
            await sink.AddAsync(new Listed(Other, null), CancellationToken.None);
            await sink.CompleteAsync();
            store.SaveDeletedHistory(PairId, token, Cursor);
        }
        using (var store = StateStore.Open(path))
        {
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) == Cursor &&
                store.DeletedNameExact(PairId, PathNames.Key(Parent)) &&
                store.DeletedNameExact(PairId, PathNames.Key(Other)), "Completed names and cursor survive dispose/reopen.");
            var local = store.OpenScan(PairId, Side.Local, ScanTestSettings.BatchRows);
            await local.AddAsync(new Listed(Child, Entry(Child)), CancellationToken.None);
            await local.AddAsync(new Listed("Other/Child", Entry("Other/Child")), CancellationToken.None);
            await local.CompleteAsync();
            var delta = store.OpenScan(PairId, Side.Dropbox, ScanTestSettings.BatchRows);
            await delta.AddAsync(new Listed(Parent, Entry(Parent, folder: true)), CancellationToken.None);
            await delta.AddAsync(new Listed(Moved, null), CancellationToken.None);
            await delta.CompleteAsync();
            Check.Require(!store.DeletedNameExact(PairId, PathNames.Key(Parent)),
                "A recreated ancestor in the delta cannot select local-only descendants for deletion history.");
            store.ResetDeletedHistory(PairId, token, discardNames: false);
            var live = store.OpenScan(PairId, Side.Dropbox, ScanTestSettings.BatchRows);
            await live.AddAsync(new Listed(Other, Entry(Other, folder: true)), CancellationToken.None);
            await live.CompleteAsync();
            Check.Require(!store.DeletedNameExact(PairId, PathNames.Key(Other)) &&
                store.DeletedNameExact(PairId, PathNames.Key(Moved)),
                "Fresh live ancestor overrides saved tombstone; new delta deletion remains investigation only.");
            Check.Require(store.ScanRows(PairId, _ => Context).Count(row =>
                row.Local is { IsFolder: false } && row.Dropbox is null) == BothSides,
                "Both local-only descendants remain present beneath recreated ancestors.");
            store.SaveDeletedHistory(PairId, token, NextCursor);
        }
        using (var store = StateStore.Open(path))
        {
            var token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) == NextCursor &&
                !store.DeletedNameExact(PairId, PathNames.Key(Parent)) &&
                !store.DeletedNameExact(PairId, PathNames.Key(Other)), "Every live override survives reopening.");
            store.ResetDeletedHistory(PairId, token, discardNames: true);
            Check.Require(!store.HasDeletedNames(PairId), "Reset never treats saved history as fresh absence evidence.");
            store.SaveDeletedHistory(PairId, token, EmptyPageCursor);
            store.MergeDriveEvidence(PairId, Generation(store), new DriveEvidence(1, 1, 1));
            token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) == EmptyPageCursor,
                "Routine drive evidence advancement must not erase completed remote discovery.");
            store.SavePair(Pair() with { DropboxRoot = "/Other" });
            token = store.BeginScan(PairId, Generation(store), true);
            Check.Require(store.RestoreDeletedHistory(PairId, token) is null, "Configuration change invalidates the cursor.");
        }
    }

    private static async Task DeletedHistoryBatchCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var token = store.BeginScan(PairId, Generation(store), true);
        var connection = (SqliteConnection)typeof(StateStore).GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        var batch = StateSchema.DeletedHistoryBatchSize;
        var sink = store.OpenScan(PairId, Side.Dropbox, ScanTestSettings.BatchRows);
        for (var i = 0; i <= batch; i++) await sink.AddAsync(new Listed("Name" + i.ToString("D8", CultureInfo.InvariantCulture), null), CancellationToken.None);
        await sink.CompleteAsync();
        store.SaveDeletedHistory(PairId, token, Cursor);
        token = store.BeginScan(PairId, Generation(store), true);
        Check.Require(store.RestoreDeletedHistory(PairId, token) == Cursor &&
            Snapshot(connection, "SELECT count(*) FROM scan_state.deleted_name;") == "[[" + (batch + 1) + "]]" &&
            Enumerable.Range(0, batch + 1).All(i => store.DeletedNameExact(PairId,
                PathNames.Key("Name" + i.ToString("D8", CultureInfo.InvariantCulture)))),
            "A published multi-batch copy restores exactly every saved key and row.");
        Execute(connection, "CREATE TEMP TRIGGER change_generation AFTER INSERT ON main.discovery_deleted_block " +
            "WHEN new.last_key='" + PathNames.Key("Name" + (batch - 1).ToString("D8", CultureInfo.InvariantCulture)) +
            "' BEGIN UPDATE pair SET execution_generation=execution_generation+1 WHERE id=new.pair_id; END;");
        Refuses<LiveAdmissionStaleException>(() => store.SaveDeletedHistory(PairId, token, NextCursor));
        Execute(connection, "DROP TRIGGER change_generation;");
        Check.Require(Snapshot(connection, "SELECT sum(name_count) FROM discovery_deleted_block;") == "[[" + batch + "]]" &&
            Snapshot(connection, "SELECT deleted_history_cursor IS NULL FROM pair;") == "[[1]]",
            "Generation change at the first batch boundary refuses the next batch without publication.");
        token = store.BeginScan(PairId, Generation(store), true);
        Check.Require(store.RestoreDeletedHistory(PairId, token) is null && !store.HasDeletedNames(PairId),
            "A generation-invalidated partial copy is not restorable.");
        sink = store.OpenScan(PairId, Side.Dropbox, ScanTestSettings.BatchRows);
        for (var i = 0; i <= batch; i++) await sink.AddAsync(new Listed("Name" + i.ToString("D8", CultureInfo.InvariantCulture), null), CancellationToken.None);
        await sink.CompleteAsync();
        Execute(connection, "CREATE TEMP TRIGGER interrupt_save BEFORE INSERT ON main.discovery_deleted_block " +
            "WHEN new.last_key='" + PathNames.Key("Name" + batch.ToString("D8", CultureInfo.InvariantCulture)) +
            "' BEGIN SELECT RAISE(ABORT,'synthetic interruption'); END;");
        Refuses<SqliteException>(() => store.SaveDeletedHistory(PairId, token, Cursor));
        Check.Require(Snapshot(connection, "SELECT sum(name_count) FROM discovery_deleted_block;") == "[[" + batch + "]]",
            "A completed batch survives interruption before publication.");
        Execute(connection, "DROP TRIGGER interrupt_save;");
        token = store.BeginScan(PairId, Generation(store), true);
        Check.Require(store.RestoreDeletedHistory(PairId, token) is null && !store.HasDeletedNames(PairId),
            "Unpublished partial saves never restore.");
        Execute(connection, "DELETE FROM discovery_deleted_block;");
        using (var transaction = connection.BeginTransaction())
        {
            for (var index = 0; index <= batch; index++)
            {
                var key = "Extra" + index.ToString("D8", CultureInfo.InvariantCulture);
                using var insert = StorageCommand.Create(connection, transaction, StateSchema.SaveDeletedBlock,
                    (StateSchema.ParamPairRow, 1L), (StateSchema.ParamPath, key), (StateSchema.ParamCount, 1),
                    (StateSchema.ParamNames, StateEncoding.EncodeDeletedNames([key])));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        Task? intake = null;
        var deleted = 0;
        connection.CreateFunction("reclaim_progress", () =>
        {
            if (deleted++ == 0) intake = store.CommitRecordAsync(PairId, StateChange.Empty with { Record = [Saved()] }, null);
            if (deleted > batch) Check.Require(store.SyncedByKey(PairId, Side.Local, Saved().LocalKey) == Saved(),
                "A queued store write commits before reclaim's next batch, while rows remain.");
            return 0;
        });
        Execute(connection, "CREATE TEMP TRIGGER reclaim_progress AFTER DELETE ON main.discovery_deleted_block BEGIN SELECT reclaim_progress(); END;");
        store.SaveDeletedHistory(PairId, token, EmptyPageCursor);
        Execute(connection, "DROP TRIGGER reclaim_progress;");
        await intake!;
        Check.Require(deleted == batch + 1 && Snapshot(connection, "SELECT count(*) FROM discovery_deleted_block;") == "[[0]]" &&
            store.SyncedByKey(PairId, Side.Local, Saved().LocalKey) == Saved(),
            "The next save reclaims interrupted rows and interleaves the queued writer before publishing.");
        sink = store.OpenScan(PairId, Side.Dropbox, ScanTestSettings.BatchRows);
        await sink.AddAsync(new Listed(Parent, null), CancellationToken.None);
        await sink.CompleteAsync();
        store.SaveDeletedHistory(PairId, token, Cursor);
        token = store.BeginScan(PairId, Generation(store), true);
        Execute(connection, "CREATE TEMP TRIGGER interrupt_restore BEFORE INSERT ON scan_state.deleted_name BEGIN SELECT RAISE(ABORT,'synthetic interruption'); END;");
        Refuses<SqliteException>(() => store.RestoreDeletedHistory(PairId, token));
        token = store.BeginScan(PairId, Generation(store), true);
        Check.Require(store.RestoreDeletedHistory(PairId, token) == Cursor && store.DeletedNameExact(PairId, PathNames.Key(Parent)),
            "BeginScan discards interrupted restore scratch before reusing the published acquisition.");
        store.SavePair(Pair() with { Name = "Renamed pair" });
        token = store.BeginScan(PairId, Generation(store), true);
        Check.Require(Snapshot(connection, "SELECT count(*) FROM discovery_deleted_block;") == "[[1]]" &&
            store.RestoreDeletedHistory(PairId, token) is null && !store.HasDeletedNames(PairId),
            "Ordinary invalidation keeps unpublished leftovers, never restore evidence.");
        store.SavePair(Pair() with { DropboxRoot = "/Other" });
        token = store.BeginScan(PairId, Generation(store), true);
        Check.Require(Snapshot(connection, "SELECT count(*) FROM discovery_deleted_block;") == "[[0]]" &&
            store.RestoreDeletedHistory(PairId, token) is null && !store.HasDeletedNames(PairId),
            "Identity replacement removes the old namespace, never retaining its restore evidence.");
    }

    private static void RefuseOldRead(string path)
    {
        try { using var unexpected = StateStore.OpenReadOnly(path); throw new Exception("old read accepted"); }
        catch (InvalidDataException error)
        {
            Check.Require(error.Message.Contains("upgrade with a compatible writable host", StringComparison.Ordinal),
                "old read explains writable upgrade");
        }
    }

    // Use the original schema's own SQL, preserving its exact ALTER formatting and FK destinations.
    private static void DowngradeIssueFixture(SqliteConnection connection)
    {
        Execute(connection, "ALTER TABLE change_work DROP COLUMN discovery_required;" + DropLocalPublished);
        Execute(connection, "DROP TRIGGER invalidate_deleted_history;DROP TABLE discovery_deleted_block;DROP TABLE discovery_deleted_delta;ALTER TABLE pair DROP COLUMN deleted_history_owed;ALTER TABLE pair DROP COLUMN deleted_history_cursor;" + StateSchema.DeletedHistorySchema);
        using var legacy = Inspect(":memory:");
        Execute(legacy, StateSchema.Create + StateSchema.SchemaAdditions);
        using var query = StorageCommand.Create(legacy, null, "SELECT sql FROM sqlite_master WHERE name='issue';");
        var sql = (string)query.ExecuteScalar()!;
        Execute(connection, "PRAGMA foreign_keys=OFF;CREATE TEMP TABLE saved_issue AS SELECT issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed,item_retry_episode,item_retry_revision,item_retry_work_id FROM issue;DROP TABLE issue;" +
            sql + ";INSERT INTO issue SELECT * FROM saved_issue;DROP TABLE saved_issue;CREATE UNIQUE INDEX issue_item_retry_work ON issue(item_retry_work_id) WHERE item_retry_work_id IS NOT NULL;PRAGMA foreign_keys=ON;");
    }

    private static Task IssueRollbackCase(string path)
    {
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            store.Commit(PairId, StateChange.Empty with
            { Raise = [(IssueKind.Info, TextKey.FailChanged, Other, new IssueSubject([], null))] });
        }
        string before;
        using (var connection = Inspect(path))
        {
            DowngradeIssueFixture(connection);
            Execute(connection, "PRAGMA user_version=7;PRAGMA foreign_keys=OFF;UPDATE issue SET pair_id=999;PRAGMA foreign_keys=ON;");
            before = DatabaseImage(connection);
        }
        try { using var refused = StateStore.Open(path); throw new Exception("invalid foreign key accepted"); }
        catch (InvalidDataException) { }
        using (var connection = Inspect(path))
        {
            Check.Require(Version(connection) == 7 && DatabaseImage(connection) == before &&
                Snapshot(connection, "SELECT count(*) FROM sqlite_master WHERE name='issue_new';") == "[[0]]" &&
                Snapshot(connection, "SELECT count(*) FROM sqlite_sequence WHERE name='issue';") == "[[0]]",
                "failed migration rolls back rows, version, temporary table and sequence");
            Execute(connection, "UPDATE issue SET pair_id=(SELECT id FROM pair LIMIT 1);");
        }
        using var restarted = StateStore.Open(path);
        Check.Require(restarted.Issues(PairId).Count == 1, "restart upgrades the repaired source once");
        return Task.CompletedTask;
    }

    private static Task IssueUpgradeCases(string path)
    {
        foreach (var source in new[] { 5, 6, 7 })
        foreach (var populated in new[] { false, true })
        {
            var file = path + source + populated;
            using (var store = StateStore.Open(file))
            {
                store.SavePair(Pair());
                if (populated)
                    store.Commit(PairId, StateChange.Empty with
                    {
                        Raise = [(IssueKind.Info, TextKey.FailChanged, Other, new IssueSubject([], null)),
                            (IssueKind.Skipped, TextKey.SkipOnlineOnly, Child, new IssueSubject([], null))]
                    });
            }
            string before;
            string members;
            string workSequence;
            long maximum;
            using (var connection = Inspect(file))
            {
                DowngradeIssueFixture(connection);
                if (populated)
                    Execute(connection, "UPDATE issue SET issue_id=issue_id*7,dismissed=1;INSERT INTO issue_member(pair_id,issue_id,side,path_key,identity,subject_json,skip_cause) SELECT pair_id,issue_id,0,'CHILD','identity','{\"Version\":1}','SkipOnlineOnly' FROM issue LIMIT 1;");
                if (populated)
                {
                    Execute(connection, "INSERT INTO change_work(work_origin,issue_work_key,pair_id,pair_generation,source_epoch,side,domain,path_key) SELECT 'Issue','retry-key',id,execution_generation,0,0,'domain','OTHER' FROM pair;");
                    Execute(connection, "UPDATE issue SET kind=0,cause='FailLocalInUse',item_retry_episode='episode',item_retry_revision=3,item_retry_work_id=(SELECT max(work_id) FROM change_work) WHERE path='Other';");
                    Execute(connection, "INSERT INTO issue_member(pair_id,issue_id,work_id,side,path_key,identity,subject_json) SELECT pair_id,issue_id,item_retry_work_id,0,'OTHER','retry-identity','{\"Version\":1}' FROM issue WHERE item_retry_work_id IS NOT NULL;");
                }
                if (source < 7)
                    Execute(connection, "DROP TRIGGER invalidate_deleted_history;DROP TABLE discovery_deleted_name;ALTER TABLE pair DROP COLUMN deleted_history_cursor;");
                if (source == 5) Execute(connection, StateSchema.CreateObsoletePhaseIndex);
                Execute(connection, "PRAGMA user_version=" + source + ";");
                before = Snapshot(connection, "SELECT * FROM issue ORDER BY issue_id;");
                members = Snapshot(connection, "SELECT * FROM issue_member ORDER BY member_id;");
                workSequence = Snapshot(connection, "SELECT * FROM sqlite_sequence WHERE name='change_work';");
                using var max = StorageCommand.Create(connection, null, "SELECT coalesce(max(issue_id),0) FROM issue;");
                maximum = (long)max.ExecuteScalar()!;
            }
            RefuseOldRead(file);
            using (var upgraded = StateStore.Open(file))
                Check.Require(upgraded.Issues(PairId).All(issue => !issue.Ignored), "upgraded visibility defaults active");
            using (var connection = Inspect(file))
            {
                Check.Require(Snapshot(connection, "SELECT issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed,item_retry_episode,item_retry_revision,item_retry_work_id FROM issue ORDER BY issue_id;") == before &&
                    Snapshot(connection, "SELECT * FROM issue_member ORDER BY member_id;") == members,
                    "upgrade preserves every issue value and entire member rows");
                Check.Require(Snapshot(connection, "SELECT * FROM sqlite_sequence WHERE name='change_work';") == workSequence &&
                    Snapshot(connection, "SELECT count(*) FROM sqlite_sequence WHERE name='issue_new';") == "[[0]]" &&
                    Snapshot(connection, "SELECT seq FROM sqlite_sequence WHERE name='issue';") == (populated ? "[[" + maximum + "]]" : "[]"),
                    "only issue's sequence is seeded to the old maximum");
                Check.Require(Snapshot(connection, "PRAGMA foreign_key_check;") == "[]", "all upgrade routes preserve foreign keys");
            }
            using var reopened = StateStore.Open(file);
        }
        return Task.CompletedTask;
    }

    // The v11 columns only; every older fixture starts from a current store without them.
    private const string DropLocalPublished = "ALTER TABLE synced_entry DROP COLUMN local_published_usn;" +
        "ALTER TABLE synced_entry DROP COLUMN local_published_journal;";

    // Both predecessors use their own schema and prove transactional rollback before successful upgrade.
    private static async Task LocalPublishedUpgradeCases(string path)
    {
        foreach (var version in new[] { StateSchema.LocalPublishedUpgradeVersion, StateSchema.DiscoveryRequiredUpgradeVersion })
            await DiscoveryUpgradeCase(path + version, version);
    }

    private static Task DiscoveryUpgradeCase(string path, int version)
    {
        const string Rows = "SELECT pair_id,local_path,dropbox_key_delta,dropbox_path_delta,is_folder,local_id,local_size," +
            "local_mtime,dropbox_id,dropbox_hash FROM synced_entry ORDER BY rowid;";
        string before;
        using (var connection = Inspect(path))
        {
            Execute(connection, StateSchema.Create + StateSchema.SchemaAdditions + StateSchema.DeletedHistorySchema +
                StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames +
                StateSchema.SpentDeletedHistory);
            if (version == StateSchema.DiscoveryRequiredUpgradeVersion)
                Execute(connection, StateSchema.LocalPublishedColumns);
            Execute(connection, StateSchema.UpsertPair, (StateSchema.ParamPair, PairId),
                (StateSchema.ParamDef, JsonSerializer.Serialize(Pair())));
            foreach (var saved in new[] { Saved(), Saved(Other), Saved(Child + "2") })
                Execute(connection, "INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size,local_mtime," +
                    "dropbox_id,dropbox_hash) VALUES(1,$path,0,$id,0,0,$remote,$hash);", ("$path", saved.LocalPath),
                    ("$id", StateEncoding.LocalId(saved.LocalId)), ("$remote", saved.DropboxId),
                    ("$hash", StateEncoding.Hash(saved.DropboxContentHash)));
            var changes = new ChangeTable(connection, writable: true);
            using (var transaction = connection.BeginTransaction())
            {
                changes.SeedPairSources(transaction, PairId);
                transaction.Commit();
            }
            Execute(connection, "INSERT INTO change_work(work_origin,issue_work_key,pair_id,pair_generation,source_epoch,side,domain,path_key) VALUES('Issue','pending',1,0,0,0,'domain','CHILD');");
            Execute(connection, "PRAGMA user_version=" + version +
                ";PRAGMA foreign_keys=OFF;INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size," +
                "local_mtime,dropbox_id,dropbox_hash) VALUES(999,'Orphan',0,'orphan',0,0,'orphan','');PRAGMA foreign_keys=ON;");
            before = Snapshot(connection, Rows);
        }
        RefuseOldRead(path);
        Refuses<InvalidDataException>(() => StateStore.Open(path).Dispose());
        using (var connection = Inspect(path))
        {
            Check.Require(Snapshot(connection, "PRAGMA user_version;") == "[[" + version + "]]" &&
                Snapshot(connection, "SELECT count(*) FROM pragma_table_info('synced_entry') WHERE name LIKE 'local_published%';") ==
                    (version == StateSchema.DiscoveryRequiredUpgradeVersion ? "[[2]]" : "[[0]]") &&
                Snapshot(connection, "SELECT count(*) FROM pragma_table_info('change_work') WHERE name='discovery_required';") == "[[0]]" &&
                Snapshot(connection, Rows) == before,
                "a refused predecessor upgrade rolls back its columns and keeps every row and the old version");
            Execute(connection, "DELETE FROM synced_entry WHERE pair_id=999;");
            before = Snapshot(connection, Rows);
        }
        using (var upgraded = StateStore.Open(path))
            Check.Require(upgraded.SyncedByKey(PairId, Side.Local, Saved(Other).LocalKey) == Saved(Other) with
                { LocalModifiedTicks = 0 }, "the upgraded store reads its saved rows");
        using (var connection = Inspect(path))
            Check.Require(Snapshot(connection, "PRAGMA user_version;") == "[[" + StateSchema.Version + "]]" &&
                Snapshot(connection, Rows) == before &&
                Snapshot(connection, "SELECT count(*) FROM synced_entry WHERE local_published_journal IS NOT NULL OR " +
                    "local_published_usn IS NOT NULL;") == "[[0]]" &&
                Snapshot(connection, "PRAGMA foreign_key_check;") == "[]",
                "both predecessors upgrade keeping every saved row and proving no boundary for any of them");
        using (var connection = Inspect(path))
            Check.Require(Snapshot(connection, "SELECT discovery_required FROM change_work;") == "[[0]]",
                "Old work survives migration with discovery false; migration invents no discovery obligation.");
        using var reopened = StateStore.Open(path);
        return Task.CompletedTask;
    }

    private static Task DeletedHistoryUpgradeCases(string path)
    {
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            store.Commit(PairId, StateChange.Empty with { Record = [Saved()] });
        }
        using (var inspect = Inspect(path))
        {
            DowngradeIssueFixture(inspect);
            Execute(inspect, "DROP TRIGGER invalidate_deleted_history;DROP TABLE discovery_deleted_name;ALTER TABLE pair DROP COLUMN deleted_history_cursor;");
            Execute(inspect, "PRAGMA user_version=" + StateSchema.DeletedHistoryUpgradeVersion + ";");
        }
        RefuseOldRead(path);
        using (var upgraded = StateStore.Open(path))
        {
            Check.Require(upgraded.SyncedByKey(PairId, Side.Local, Saved().LocalKey) == Saved(), "History migration preserves saved bindings.");
            var token = upgraded.BeginScan(PairId, Generation(upgraded), true);
            Check.Require(upgraded.RestoreDeletedHistory(PairId, token) is null, "Migration invents no completed acquisition.");
        }
        return Task.CompletedTask;
    }

    private static PairConfig Pair(string id = PairId) => new(id, id, LocalRoot, RemoteRoot,
        Account, [], [], FirstRunPolicy.NewestWins, true, null);

    private static PairConfig BoundPair() => Pair() with
    {
        Roots = new RootBinding(LocalRoot, RemoteRoot, Account, new DirectoryIdentity(Single, 0, null),
            new DropboxRootIdentity(Account, Namespace, Namespace, RemoteId))
    };

    private static Entry Entry(string path = Child, bool folder = false) =>
        new(path, folder, 0, DateTimeOffset.UnixEpoch, LocalId, Revision, ZeroHash, null);

    private static SyncedEntry Saved(string path = Child) => new(PathNames.Key(path), path,
        PathNames.DropboxKey(path), path, false, LocalId + path, 0, DateTimeOffset.UnixEpoch.UtcTicks,
        RemoteId + path, ZeroHash);

    private static IssueSubject Subject(string path = Child, string? id = LocalId,
        IssueSubject.FirstRunDeletionKind kind = IssueSubject.FirstRunDeletionKind.None) =>
        new([new IssueItem(Side.Local, path, id!)], null, FirstRunDeletion: kind);

    private static StateChange Raise(TextKey cause, string path = Child, IssueSubject? subject = null,
        IssueKind kind = IssueKind.Failure) => StateChange.Empty with
        { Raise = [(kind, cause, path, subject ?? Subject(path))] };

    private static long Generation(IStateStore store) => store.ReadPairExecution(PairId)!.Value.Generation;

    private static void Refuses<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(typeof(T).Name);
    }

    private static async Task RefusesAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(typeof(T).Name);
    }

    private static SqliteConnection Inspect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString());
        StateStore.OpenConnection(connection);
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql,
        params (string Name, object? Value)[] values)
    {
        using var command = StorageCommand.Create(connection, null, sql, values);
        command.ExecuteNonQuery();
    }

    private static string Snapshot(SqliteConnection connection, string sql)
    {
        using var command = StorageCommand.Create(connection, null, sql);
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return JsonSerializer.Serialize(rows);
    }

    private static string DatabaseImage(SqliteConnection connection)
    {
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var named = new List<(string Name, string Sql)>();
        using (var reader = tables.ExecuteReader())
            while (reader.Read()) named.Add((reader.GetString(0), reader.GetString(1)));
        return JsonSerializer.Serialize(named.Select(table => new
        {
            Name = table.Name,
            Rows = Snapshot(connection, "SELECT * FROM \"" + table.Name.Replace("\"", "\"\"") + "\" ORDER BY " +
                (table.Sql.Contains("WITHOUT ROWID", StringComparison.Ordinal) ? KeyOrder(connection, table.Name) : "rowid") + ";")
        }));
    }

    // A WITHOUT ROWID table has no rowid; its declared columns are the deterministic equivalent image order.
    private static string KeyOrder(SqliteConnection connection, string table)
    {
        using var command = StorageCommand.Create(connection, null,
            "SELECT name FROM pragma_table_info($t) ORDER BY cid;", ("$t", table));
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add("\"" + reader.GetString(0).Replace("\"", "\"\"") + "\"");
        return string.Join(',', names);
    }

    private static void RollbackAt(string path, string boundary, Action action)
    {
        using var connection = Inspect(path);
        var before = DatabaseImage(connection);
        Execute(connection, "CREATE TRIGGER injected_failure BEFORE " + boundary +
            " BEGIN SELECT RAISE(ABORT,'test-transaction-boundary'); END;");
        try { Refuses<SqliteException>(action); }
        finally { Execute(connection, "DROP TRIGGER injected_failure;"); }
        Check.Require(DatabaseImage(connection) == before, boundary);
    }

    private static long Version(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = StateSchema.ReadVersion;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Definition(SqliteConnection connection)
    {
        using var command = StorageCommand.Create(connection, null, StateSchema.SelectPair,
            (StateSchema.ParamPair, PairId));
        return (string)command.ExecuteScalar()!;
    }

    private static RepairSummary Summary() => new(nameof(Summary), DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch, true, true, null, false, new Dictionary<string, int>(),
        new Dictionary<string, int>(), string.Empty, false);

    private static Task BasicCases(string path)
    {
        Check.Require(StateStore.OpenReadOnly(path) is null && !File.Exists(path), nameof(BasicCases));
        var pair = Pair() with { Drive = new(uint.MaxValue, ulong.MaxValue, long.MaxValue) };
        using (var store = StateStore.Open(path))
        {
            Check.Require(store.Pairs().Count == 0 && store.Pair(PairId) is null, nameof(BasicCases));
            store.SavePair(pair);
            store.SavePair(Pair(OtherPair));
            Check.Require(store.Pairs().Count == BothSides && store.Pair(PairId)!.Drive == pair.Drive,
                nameof(BasicCases));
            var generation = Generation(store);
            var source = store.ReadSource(PairId, Side.Local);
            store.SaveLimiterLevel(Account, Single);
            Check.Require(store.LimiterLevel(Account) == Single && store.LimiterLevel(Other) is null,
                nameof(BasicCases));
            store.FinishRepair(PairId, Summary());
            Check.Require(!store.FirstRepairDone(PairId) && store.LastRepair(PairId) is { Completed: false } &&
                store.ReadSource(PairId, Side.Local) == source && Generation(store) == generation,
                nameof(BasicCases));
            store.RemovePair(OtherPair);
            Check.Require(store.Pair(OtherPair) is null && store.Pair(PairId) is not null, nameof(BasicCases));
        }
        using var read = StateStore.OpenReadOnly(path)!;
        Check.Require(read.Pairs().Count == Single && read.Pair(PairId)!.Drive == pair.Drive &&
            read.LimiterLevel(Account) == Single, nameof(BasicCases));
        Refuses<SqliteException>(() => read.SavePair(Pair(OtherPair)));
        return Task.CompletedTask;
    }

    private static Task MappingCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        store.SavePair(Pair(OtherPair));
        foreach (var side in Enum.GetValues<Side>())
        {
            var parent = Saved(Parent) with { IsFolder = true };
            var child = Saved();
            var sibling = Saved(Sibling);
            store.Commit(PairId, StateChange.Empty with { Record = [parent, child, sibling] });
            store.Commit(OtherPair, StateChange.Empty with { Record = [child] });
            foreach (var saved in new[] { parent, child, sibling })
                Check.Require(store.SyncedByKey(PairId, side, saved.KeyOn(side)) == saved &&
                    store.SyncedById(PairId, side, side == Side.Local ? saved.LocalId : saved.DropboxId) == saved,
                    nameof(MappingCases));
            var next = side == Side.Local
                ? parent with { LocalPath = Moved, LocalKey = PathNames.Key(Moved) }
                : parent with { DropboxPath = Moved, DropboxKey = PathNames.DropboxKey(Moved) };
            store.Commit(PairId, StateChange.Empty with
                { Renames = [new RenameApplied(parent, next, null, null, null)] });
            var childPath = Moved + PathNames.PathSeparator + Child[(Parent.Length + 1)..];
            var expected = side == Side.Local
                ? child with { LocalPath = childPath, LocalKey = PathNames.Key(childPath) }
                : child with { DropboxPath = childPath, DropboxKey = PathNames.DropboxKey(childPath) };
            Check.Require(store.SyncedByKey(PairId, side, expected.KeyOn(side)) == expected &&
                store.SyncedByKey(PairId, side, child.KeyOn(side)) is null, nameof(MappingCases));
            store.Commit(PairId, StateChange.Empty with { ForgetSubtrees = [(side, next.KeyOn(side))] });
            Check.Require(store.SyncedByKey(PairId, side, expected.KeyOn(side)) is null &&
                store.SyncedByKey(PairId, side, sibling.KeyOn(side)) == sibling &&
                store.SyncedByKey(OtherPair, side, child.KeyOn(side)) == child, nameof(MappingCases));
            store.ForgetAllSynced(PairId);
        }
        var linked = Saved() with { DropboxKey = PathNames.Key(Other), DropboxPath = Other };
        store.Commit(PairId, StateChange.Empty with { Record = [linked] });
        Check.Require(!store.ForgetTouchesUnreadable(PairId, [(Side.Dropbox, linked.DropboxKey)],
            CancellationToken.None), nameof(MappingCases));
        UnreadableMember.Record(store, PairId, Parent);
        Check.Require(store.ForgetTouchesUnreadable(PairId, [(Side.Dropbox, linked.DropboxKey)],
            CancellationToken.None) &&
            !store.ForgetTouchesUnreadable(PairId, [(Side.Dropbox, PathNames.Key(Sibling))],
                CancellationToken.None) &&
            !store.ForgetTouchesUnreadable(PairId, [], CancellationToken.None), nameof(MappingCases));
        return Task.CompletedTask;
    }

    private static async Task ScanCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        foreach (var discovery in new[] { false, true })
        {
            store.BeginScan(PairId, Generation(store), discovery);
            foreach (var side in Enum.GetValues<Side>())
            {
                var sink = store.OpenScan(PairId, side, ScanTestSettings.BatchRows);
                var entry = Entry();
                sink.AddAsync(new Listed(entry.Path, entry), default).GetAwaiter().GetResult();
                sink.AddAsync(new Listed(entry.Path, null), default).GetAwaiter().GetResult();
                sink.AddAsync(new Listed(entry.Path, entry), default).GetAwaiter().GetResult();
                await sink.CompleteAsync();
                var expected = side == Side.Local
                    ? entry with { Revision = string.Empty, ContentHash = string.Empty } : entry;
                Check.Require(store.ScanEntry(PairId, side, PathNames.Key(Child)) == expected,
                    nameof(ScanCases));
            }
            Check.Require(store.HasDeletedNames(PairId) == discovery &&
                store.DeletedNameExact(PairId, PathNames.Key(Child)) == discovery,
                nameof(ScanCases));
            var row = store.ScanRows(PairId, _ => Context).Single();
            Check.Require(row.Local == (Entry() with { Revision = string.Empty, ContentHash = string.Empty }) &&
                row.Dropbox == Entry(), nameof(ScanCases));
        }
        var stale = store.BeginScan(PairId, Generation(store), false);
        store.BeginScan(PairId, Generation(store), false);
        Refuses<LiveAdmissionStaleException>(() => store.ReplaceSkipped(PairId, stale, []));
        Check.Require(!store.ScanRows(PairId, _ => Context).Any(), nameof(ScanCases));
        var saved = Saved() with { LocalId = LocalId };
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var moved = Entry(Moved);
        var writer = store.OpenScan(PairId, Side.Local, ScanTestSettings.BatchRows);
        writer.AddAsync(new Listed(Moved, moved), default).GetAwaiter().GetResult();
        await writer.CompleteAsync();
        Check.Require(store.ScanRenames(PairId).Single().Synced == saved, nameof(ScanCases));
    }

    private static async Task ScanRecordCases(string path)
    {
        using var durable = StateStore.Open(path);
        using var memory = new Massdex.SyncShark.Engine.Tests.TestDoubles.InMemoryStateStore();
        foreach (IStateStore store in new IStateStore[] { durable, memory })
        {
            store.SavePair(BoundPair());
            InstallDropbox(store);
            var generation = Generation(store);
            var local = store.ReadSource(PairId, Side.Local)!.Value;
            var remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
            var position = new JournalPosition(Single, 0);
            store.AcquireJournalPage(Domain, null, position,
                [(PairId, generation, local.Epoch, local.Revision, false)], []);
            var token = store.BeginScan(PairId, generation, true);
            var captured = store.ReadScan(PairId, token);
            Check.Require(captured == new ScanRecord(token, generation, true, true, 0,
                local.Epoch, local.Revision, remote.Epoch, remote.Revision, null), nameof(ScanRecordCases));
            var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, Child, null, LocalId, Entry(), true, false);
            var later = store.AcquireJournalPage(Domain, position, position,
                [(PairId, generation, local.Epoch, local.Revision, false)], [(PairId, notice)])!.Value;
            Check.Require(later > captured.ThroughWorkId && store.ReadScan(PairId, token) == captured,
                nameof(ScanRecordCases));
            var oldSink = store.OpenScan(PairId, Side.Local, ScanTestSettings.BatchRows);
            await oldSink.AbortAsync();
            var replacement = store.BeginScan(PairId, generation, false);
            Check.Require(store.ReadScan(PairId, replacement).Token == replacement, nameof(ScanRecordCases));
            Refuses<LiveAdmissionStaleException>(() => store.ReadScan(PairId, token));
            await store.OpenScan(PairId, Side.Local, ScanTestSettings.BatchRows).AbortAsync();
            Refuses<LiveAdmissionStaleException>(() => store.ReadScan(PairId, replacement));
            var pair = store.Pair(PairId)!;
            store.SavePairDecision(pair, generation, pair with { Name = "renamed" }, null, false, null);
            InstallDropbox(store);
            generation = Generation(store);
            local = store.ReadSource(PairId, Side.Local)!.Value;
            remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
            token = store.BeginScan(PairId, generation, false);
            captured = store.ReadScan(PairId, token);
            Check.Require(captured.LocalGapRevision == local.Revision, nameof(ScanRecordCases));
            var through = store.AcquireJournalPage(Domain, position, position,
                [(PairId, generation, local.Epoch, local.Revision, true)], [])!.Value;
            Check.Require(store.ReadLocalGap(PairId, local.Epoch) == local.Revision + 1,
                nameof(ScanRecordCases));
            var localCatchUp = new LocalCatchUp(PairId, generation, local.Epoch,
                new NtfsUsnCheckpoint(Single, 0, 0, 0), through);
            var remoteCatchUp = new DropboxCatchUp(PairId, generation, remote.Epoch, Cursor, through);
            Check.Require(store.ReadScan(PairId, token) == captured, nameof(ScanRecordCases));
            Refuses<LiveAdmissionStaleException>(() =>
                store.FinishRepair(PairId, Summary(), generation, token, localCatchUp, remoteCatchUp));
            Check.Require(!store.FirstRepairDone(PairId), nameof(ScanRecordCases));
            var renamed = store.Pair(PairId)!;
            store.SavePairDecision(renamed, generation, renamed with { Enabled = false }, null, false, null);
            Refuses<LiveAdmissionStaleException>(() => store.ReadScan(PairId, token));
        }
        using var inspect = Inspect(path);
        Execute(inspect, "DELETE FROM source_state WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND side=$side;",
            (StateSchema.ParamPair, PairId), (StateSchema.ParamSide, (int)Side.Local));
        Refuses<LiveAdmissionStaleException>(() => durable.BeginScan(PairId, Generation(durable), false));
    }

    private static async Task SkipCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var generation = Generation(store);
        var local = Subject();
        var remote = new IssueSubject([new IssueItem(Side.Dropbox, Child, RemoteId)], null);
        foreach (var touched in Enum.GetValues<Side>())
        {
            var token = store.BeginScan(PairId, generation, false);
            foreach (var side in Enum.GetValues<Side>()) await store.OpenScan(PairId, side, ScanTestSettings.BatchRows).CompleteAsync();
            store.ObserveSkipped(PairId, generation, touched, Child, null);
            store.ReplaceSkipped(PairId, token,
                [(Child, TextKey.SkipLink, local), (Child, TextKey.SkipReservedName, remote)]);
            var kept = store.Issues(PairId).Single();
            Check.Require(kept.Kind == IssueKind.Skipped && kept.Subject.Items.Single().Side != touched,
                nameof(SkipCases));
            store.DismissIssue(PairId, kept.Id);
            var remaining = touched == Side.Local ? Side.Dropbox : Side.Local;
            var subject = remaining == Side.Local ? local : remote;
            var cause = remaining == Side.Local ? TextKey.SkipLink : TextKey.SkipReservedName;
            store.ObserveSkipped(PairId, generation, remaining, Child, (cause, subject));
            Check.Require(store.IssueById(PairId, kept.Id)!.Dismissed, nameof(SkipCases));
            store.ObserveSkipped(PairId, generation, remaining, Child, null);
            Check.Require(store.Issues(PairId).Count == 0, nameof(SkipCases));
        }
        Refuses<LiveAdmissionStaleException>(() =>
            store.ObserveSkipped(PairId, generation + 1, Side.Local, Child, (TextKey.SkipLink, local)));
    }

    // A100: a nonprotecting observation neither clears nor touches a protecting Local member, so Repair still can.
    private static async Task ProtectingMemberCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var generation = Generation(store);
        var token = store.BeginScan(PairId, generation, false);
        foreach (var side in Enum.GetValues<Side>()) await store.OpenScan(PairId, side, ScanTestSettings.BatchRows).CompleteAsync();
        var local = Subject();
        store.ReplaceSkipped(PairId, token, [(Child, TextKey.SkipUnreadable, local)]);
        foreach (var incoming in new (TextKey Cause, IssueSubject Subject)?[]
            { null, (TextKey.SkipLink, local), (TextKey.SkipOnlineOnly, local) })
        {
            store.ObserveSkipped(PairId, generation, Side.Local, Child, incoming);
            Check.Require(store.Issues(PairId).Single().Cause == TextKey.SkipUnreadable, nameof(ProtectingMemberCases));
        }
        store.ReplaceSkipped(PairId, token, []);
        Check.Require(store.Issues(PairId).Count == 0, nameof(ProtectingMemberCases));
    }

    // A20, A21, A36, A39, A59, A60, A98, A99: the member's own cause and identity decide, at the Store.
    private static async Task UnreadableCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var generation = Generation(store);
        var token = store.BeginScan(PairId, generation, false);
        foreach (var side in Enum.GetValues<Side>()) await store.OpenScan(PairId, side, ScanTestSettings.BatchRows).CompleteAsync();
        var key = PathNames.Key(Parent);
        var local = new IssueSubject([new IssueItem(Side.Local, Parent, LocalId)], null);
        var remote = new IssueSubject([new IssueItem(Side.Dropbox, Parent, RemoteId)], null);
        store.ObserveSkipped(PairId, generation, Side.Dropbox, Parent, (TextKey.SkipReservedName, remote));
        store.ReplaceSkipped(PairId, token, [(Parent, TextKey.SkipUnreadable, local)]);
        var issue = store.Issues(PairId).Single();
        Check.Require(issue.Cause == TextKey.SkipReservedName && store.UnreadableTouches(PairId, key, false) &&
            store.UnreadableTouches(PairId, PathNames.Key(Child), false) &&
            !store.UnreadableTouches(PairId, PathNames.Key(Sibling), true), "own cause, inside, adjacent");
        store.DismissIssue(PairId, issue.Id);
        Check.Require(store.UnreadableTouches(PairId, key, true), "a dismissed notice still protects");
        Check.Require(!store.ReleaseUnreadable(PairId, generation, key, RemoteId) &&
            store.UnreadableTouches(PairId, key, false), "a different identity writes nothing");
        Check.Require(store.ReleaseUnreadable(PairId, generation, key, LocalId) &&
            !store.UnreadableTouches(PairId, key, true), "the exact member goes, dismissed or not");
        var left = store.Issues(PairId).Single();
        Check.Require(left.Subject.Items.Single().Side == Side.Dropbox, "the Dropbox member survives");
        store.ReplaceSkipped(PairId, token, [(Parent, TextKey.SkipUnreadable, local)]);
        Check.Require(!store.UnreadableTouches(PairId, key, true), "an unobligated Repair does not restore a release");

        var nonprotecting = new IssueSubject([new IssueItem(Side.Local, Moved, LocalId)], null);
        store.ObserveSkipped(PairId, generation, Side.Local, Moved, (TextKey.SkipLink, nonprotecting));
        var unknown = new IssueSubject([new IssueItem(Side.Local, Other, "")], null);
        store.ObserveSkipped(PairId, generation, Side.Local, Other, (TextKey.SkipUnreadable, unknown));
        Check.Require(!store.UnreadableTouches(PairId, PathNames.Key(Moved), true) &&
            !store.ReleaseUnreadable(PairId, generation, PathNames.Key(Moved), LocalId) &&
            !store.ReleaseUnreadable(PairId, generation, PathNames.Key(Other), ""), "cause and null identity refuse");

        var deep = PathNames.Child(Child, Other);
        UnreadableMember.Record(store, PairId, deep);
        Check.Require(!store.UnreadableTouches(PairId, PathNames.Key(Child), false) &&
            store.UnreadableTouches(PairId, PathNames.Key(Child), true) &&
            store.UnreadableUnder(PairId, key).SequenceEqual([PathNames.Key(deep)]) &&
            store.UnreadableUnder(PairId, PathNames.Key(deep)).SequenceEqual([PathNames.Key(deep)]), "file and range");
        Refuses<LiveAdmissionStaleException>(() => store.ReleaseUnreadable(PairId, generation + 1, key, LocalId));

        // A66: a range answers from protecting members only; a protecting member last in key order is found; no match
        // is empty. The statement's visited rows are not asserted: the production connection exposes no counter.
        const string range = "Range";
        var onlyNonprotecting = new IssueSubject([new IssueItem(Side.Local, range + "/a", LocalId)], null);
        store.ObserveSkipped(PairId, generation, Side.Local, range + "/a", (TextKey.SkipLink, onlyNonprotecting));
        UnreadableMember.Record(store, PairId, range + "/z");
        Check.Require(store.UnreadableUnder(PairId, PathNames.Key(range)).SequenceEqual([PathNames.Key(range + "/z")]) &&
            store.UnreadableUnder(PairId, PathNames.Key("Absent")).Count == 0 &&
            !store.UnreadableTouches(PairId, PathNames.Key(range + "/a"), true),
            "A66 nonprotecting-only range, protecting last in range and no match all answer exactly");

        // A103: a repeated failure keeps the one member and issue; a single release removes it.
        var repeat = new IssueSubject([new IssueItem(Side.Local, Sibling, LocalId)], null);
        for (var attempt = 0; attempt < 2; attempt++)
            store.ObserveSkipped(PairId, generation, Side.Local, Sibling, (TextKey.SkipUnreadable, repeat));
        Check.Require(store.Issues(PairId).Count(issue => issue.Path == Sibling) == 1 &&
            store.UnreadableUnder(PairId, PathNames.Key(Sibling)).Count == 1,
            "A103 a repeated failure keeps one member and one issue");
        Check.Require(store.ReleaseUnreadable(PairId, generation, PathNames.Key(Sibling), LocalId) &&
            store.UnreadableUnder(PairId, PathNames.Key(Sibling)).Count == 0,
            "A103 one release clears the member");
    }

    // A36: the upgrade writes each legacy Skipped issue's own members and leaves the issue's identity and dismissal.
    private static Task MemberlessUpgradeCases(string path)
    {
        SeedLegacy(path);
        long issue;
        using (var seed = Inspect(path))
        {
            Execute(seed, StateSchema.InsertIssue, (StateSchema.ParamPair, PairId),
                (StateSchema.ParamPath, Child), (StateSchema.ParamKind, (int)IssueKind.Skipped),
                (StateSchema.ParamCause, nameof(TextKey.SkipUnreadable)),
                (StateSchema.ParamSubject, JsonSerializer.Serialize(Subject())),
                (StateSchema.ParamAt, DateTimeOffset.UnixEpoch.UtcTicks));
            using var read = seed.CreateCommand();
            read.CommandText = "SELECT issue_id FROM issue WHERE kind=$kind;";
            read.Parameters.AddWithValue("$kind", (int)IssueKind.Skipped);
            issue = (long)read.ExecuteScalar()!;
            Execute(seed, StateSchema.DismissIssue, (StateSchema.ParamPair, PairId), (StateSchema.ParamId, issue),
                (StateSchema.ParamKind, (int)IssueKind.Info));
        }
        using var store = StateStore.Open(path);
        Check.Require(store.IssueById(PairId, issue) is { Dismissed: true, Cause: TextKey.SkipUnreadable },
            nameof(MemberlessUpgradeCases));
        using var inspect = Inspect(path);
        using var members = inspect.CreateCommand();
        members.CommandText = "SELECT skip_cause FROM issue_member WHERE issue_id=$issue AND side=$side AND path_key=$key;";
        members.Parameters.AddWithValue("$issue", issue);
        members.Parameters.AddWithValue("$side", (int)Side.Local);
        members.Parameters.AddWithValue("$key", PathNames.Key(Child));
        Check.Require(members.ExecuteScalar() as string == nameof(TextKey.SkipUnreadable), nameof(MemberlessUpgradeCases));
        return Task.CompletedTask;
    }

    private static async Task DisposedRecordCases(string path)
    {
        var store = StateStore.Open(path);
        store.SavePair(Pair());
        var change = StateChange.Empty with { Record = [Saved()] };
        var queued = store.CommitRecordAsync(PairId, change, null);
        store.Dispose();
        await queued;
        Check.Require(queued.IsCompletedSuccessfully, "Dispose preserves accepted record completion");
        store.Dispose();
        foreach (var call in new Action[]
        {
            () => _ = store.CommitRecordAsync(PairId, change, null),
            () => _ = store.CommitAsync(PairId, change),
            () => store.Commit(PairId, StateChange.Empty)
        })
        {
            var refused = false;
            try { call(); }
            catch (ObjectDisposedException) { refused = true; }
            Check.Require(refused, "disposed commits are refused synchronously");
        }
        using var reopened = StateStore.Open(path);
        Check.Require(reopened.SyncedByKey(PairId, Side.Local, Saved().LocalKey) == Saved(),
            "accepted record survives disposal and reopen");
    }

    private static async Task RecordCases(string path)
    {
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            store.SavePair(Pair(OtherPair));
            var saved = Saved();
            foreach (var pair in new[] { PairId, OtherPair })
            {
                store.Commit(pair, Raise(TextKey.FailChanged));
                await store.CommitRecordAsync(pair, StateChange.Empty with
                    { Record = [saved], Close = [(Child, IssueClosure.AfterEffect)] }, null);
                using var visible = StateStore.OpenReadOnly(path)!;
                Check.Require(visible.SyncedByKey(pair, Side.Local, saved.LocalKey) == saved &&
                    visible.Issues(pair).Count == 0, nameof(RecordCases));
            }
            foreach (var invalid in new[]
            {
                StateChange.Empty,
                StateChange.Empty with { Record = [saved], ForgetSelf = [(Side.Local, saved.LocalKey)] },
                StateChange.Empty with { Record = [saved], ForgetSubtrees = [(Side.Local, saved.LocalKey)] },
                StateChange.Empty with { Record = [saved], Renames = [new RenameApplied(saved, saved, null, null, null)] },
                StateChange.Empty with { Record = [saved], Close = [(Child, new[] { IssueKind.DriveChange })] }
            })
                await RefusesAsync<ArgumentException>(() => store.CommitRecordAsync(PairId, invalid, null));
            // rev3h G05: a raising or folder first-run Record is the synchronous commit, not a rejection.
            await store.CommitRecordAsync(PairId, Raise(TextKey.FailChanged) with { Record = [saved] }, null);
            Check.Require(store.Issues(PairId).Any(issue => issue.Cause == TextKey.FailChanged), nameof(RecordCases));
            var folderFirst = Saved(Other) with { IsFolder = true };
            await store.CommitRecordAsync(PairId, StateChange.Empty with { Record = [folderFirst] }, null);
            Check.Require(store.SyncedByKey(PairId, Side.Local, PathNames.Key(Other))?.IsFolder == true,
                nameof(RecordCases));
            var delivered = new List<Issue>();
            await store.CommitRecordAsync(PairId, Raise(TextKey.FailChanged) with { Record = [saved] }, delivered.Add);
            Check.Require(delivered.Count == Single && store.IssueById(PairId, delivered[0].Id) is not null,
                "a raising first-run Record delivers its committed issue exactly once");
            var folderDelivered = new List<Issue>();
            await store.CommitRecordAsync(PairId,
                Raise(TextKey.FailChanged, Other) with { Record = [Saved(Parent + Other) with { IsFolder = true }] },
                folderDelivered.Add);
            Check.Require(folderDelivered.Count == Single,
                "a folder first-run Record delivers its committed issue exactly once");
            var first = Saved(Parent);
            var invalidRow = Saved(Other) with { DropboxId = null! };
            await RefusesAsync<SqliteException>(() => store.CommitRecordAsync(PairId,
                StateChange.Empty with { Record = [first, invalidRow] }, null));
            Check.Require(store.SyncedByKey(PairId, Side.Local, first.LocalKey) is null &&
                store.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved, nameof(RecordCases));
            await store.CommitAsync(PairId, StateChange.Empty with { Record = [first with { IsFolder = true }] });
            Check.Require(store.SyncedByKey(PairId, Side.Local, first.LocalKey)!.IsFolder, nameof(RecordCases));
            var callbacks = new List<Issue>();
            store.Commit(PairId, Raise(TextKey.FailChanged), issue =>
            {
                Check.Require(store.IssueById(PairId, issue.Id) is not null, nameof(RecordCases));
                callbacks.Add(issue);
            });
            Check.Require(callbacks.Count == Single, nameof(RecordCases));
            var notificationError = new IOException(nameof(RecordCases));
            var notified = false;
            try { store.Commit(PairId, Raise(TextKey.FailChanged, Other), _ => throw notificationError); }
            catch (IOException error)
            {
                Check.Require(ReferenceEquals(error, notificationError), nameof(RecordCases));
                notified = true;
            }
            Check.Require(notified, nameof(RecordCases));
            Check.Require(store.Issues(PairId).Any(issue => issue.Path == Other), nameof(RecordCases));
        }
        using var reopened = StateStore.OpenReadOnly(path)!;
        Check.Require(reopened.SyncedByKey(OtherPair, Side.Local, PathNames.Key(Child)) == Saved(), nameof(RecordCases));
    }

    // Test-owned input enumeration exposes queue/cache boundaries without product hooks or private reflection.
    internal sealed class ObservedList<T>(IReadOnlyList<T> values, Action beforeEnumeration) : IReadOnlyList<T>
    {
        public int Count => values.Count;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator()
        {
            beforeEnumeration();
            return values.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static async Task GroupedAdoptionCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var before = new[] { Saved(Child), Saved(Other) };
        store.Commit(PairId, StateChange.Empty with { Record = before });
        var after = before.Select(row => row with { LocalPath = row.LocalPath.ToUpperInvariant() }).ToArray();
        var queued = new List<Task>();
        var visits = 0;
        var gate = new ObservedList<SyncedEntry>([Saved(Parent)], () =>
        {
            // Enqueue while the existing writer owns its transaction; completion must wait for the next commit.
            if (Interlocked.Increment(ref visits) != 2) return;
            for (var index = 0; index < before.Length; index++)
            {
                var task = store.CommitAsync(PairId, StateChange.Empty with
                {
                    Renames = [new RenameApplied(before[index], after[index], null, null, null)]
                });
                Check.Require(!task.IsCompleted, "An adoption queued behind an active commit is not acknowledged early.");
                queued.Add(task);
            }
        });
        await store.CommitRecordAsync(PairId, StateChange.Empty with { Record = gate }, null);
        await Task.WhenAll(queued);
        Check.Require(queued.Count == before.Length, "Every independent adoption enters the existing record queue.");
        using var reopened = StateStore.OpenReadOnly(path)!;
        foreach (var row in after)
            Check.Require(reopened.SyncedByKey(PairId, Side.Local, row.LocalKey) == row,
                "Queued adoption completion means the relink is durable with its content history intact.");
    }

    private static async Task GroupedRecordCases(string path)
    {
        var patterns = new[]
        {
            new[] { false, true, false }, new[] { true, false, false },
            new[] { false, false, true }, new[] { true, false, true }
        };
        foreach (var bad in patterns)
        foreach (var firstFails in new[] { false, true })
        foreach (var failureType in new[] { typeof(ArgumentException), typeof(SqliteException), typeof(IOException) })
        {
            var database = path + Path.GetRandomFileName();
            using (var store = StateStore.Open(database))
            {
                store.SavePair(BoundPair());
                store.SavePair(BoundPair() with { PairId = OtherPair, Name = OtherPair });
                for (var index = 0; index < bad.Length; index++)
                {
                    var target = index.ToString(CultureInfo.InvariantCulture);
                    var crossed = Saved(target + "/Partner") with
                    {
                        DropboxKey = PathNames.DropboxKey(target), DropboxPath = target
                    };
                    store.Commit(index % BothSides == 0 ? PairId : OtherPair,
                        StateChange.Empty with { Record = [crossed] });
                }
                var releaseStart = store.ReadWorkWatermark();
                var initial = Saved(Parent);
                using var release = new ManualResetEventSlim(false);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var enumerations = 0;
                var gated = new ObservedList<SyncedEntry>([firstFails ? initial with { DropboxId = null! } : initial], () =>
                {
                    // Eligibility visits first; application visits next, while the first transaction owns the write gate.
                    if (Interlocked.Increment(ref enumerations) != 2) return;
                    entered.TrySetResult();
                    release.Wait();
                });
                var first = Task.Run(() => store.CommitRecordAsync(PairId,
                    StateChange.Empty with { Record = gated }, null));
                var ready = new List<(Task Completion, string Pair, SyncedEntry Row, bool Bad)>();
                Exception? setupFailure = null;
                try
                {
                    await Task.WhenAny(entered.Task, first);
                    Check.Require(entered.Task.IsCompletedSuccessfully, nameof(GroupedRecordCases));
                    for (var index = 0; index < bad.Length; index++)
                    {
                        var pair = index % BothSides == 0 ? PairId : OtherPair;
                        var saved = Saved(index.ToString(CultureInfo.InvariantCulture));
                        IReadOnlyList<SyncedEntry> rows = bad[index]
                            ? new[] { saved, failureType == typeof(SqliteException)
                                ? Saved(saved.LocalPath + Other) with { DropboxId = null! }
                                : Saved(saved.LocalPath + Other) with { LocalKey = null! } }
                            : new[] { saved };
                        if (bad[index] && failureType == typeof(IOException))
                        {
                            var visits = 0;
                            rows = new ObservedList<SyncedEntry>([saved], () =>
                            {
                                if (Interlocked.Increment(ref visits) > 1) throw new IOException(nameof(GroupedRecordCases));
                            });
                        }
                        var queued = store.CommitRecordAsync(pair, StateChange.Empty with { Record = rows }, null);
                        Check.Require(!queued.IsCompleted, nameof(GroupedRecordCases));
                        ready.Add((queued, pair, saved, bad[index] || failureType == typeof(IOException)));
                    }
                }
                catch (Exception error) { setupFailure = error; }
                finally { release.Set(); }
                try { await Task.WhenAll(ready.Select(member => member.Completion).Prepend(first)); }
                catch { /* Each task's exact expected result is asserted below after every borrower has joined. */ }
                if (setupFailure is not null) throw setupFailure;
                if (firstFails) await RefusesAsync<SqliteException>(() => first);
                else await first;
                foreach (var member in ready)
                {
                    if (!member.Bad) await member.Completion;
                    else if (failureType == typeof(SqliteException)) await RefusesAsync<SqliteException>(() => member.Completion);
                    else if (failureType == typeof(IOException)) await RefusesAsync<IOException>(() => member.Completion);
                    else await RefusesAsync<ArgumentException>(() => member.Completion);
                    Check.Require(store.SyncedByKey(member.Pair, Side.Local, member.Row.LocalKey) ==
                        (member.Bad ? null : member.Row), nameof(GroupedRecordCases));
                }
                Check.Require(store.ReadWorkWatermark() == releaseStart + ready.Count(member => !member.Bad),
                    "Batch rollback and isolated reapply register exactly one release per successful member.");
                foreach (var member in ready)
                    Check.Require((store.SyncedByKey(member.Pair, Side.Local,
                        PathNames.Key(member.Row.LocalPath + "/Partner")) is not null) == member.Bad,
                        "Failed batch members retain their crossed binding; successful members retire it.");
                // A completed write barrier must neither hide a failed member nor undo a durable sibling.
                store.SaveLimiterLevel(Account, Single);
                Check.Require(ready.All(member => member.Completion.IsCompleted), nameof(GroupedRecordCases));
                using var reopened = StateStore.OpenReadOnly(database)!;
                foreach (var member in ready)
                    Check.Require(reopened.SyncedByKey(member.Pair, Side.Local, member.Row.LocalKey) ==
                        (member.Bad ? null : member.Row), nameof(GroupedRecordCases));
            }
        }
    }

    private static async Task PostCommitCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        store.SaveLimiterLevel(Account, 0);
        store.Commit(PairId, Raise(TextKey.FailChanged));
        store.Commit(PairId, StateChange.Empty with { Close = [(Other, IssueClosure.AfterEffect)] });
        using var inspect = Inspect(path);
        Execute(inspect, "CREATE TRIGGER count_records AFTER INSERT ON synced_entry BEGIN " +
            "UPDATE account SET limiter_level=limiter_level+1; END;");
        var fault = new InvalidOperationException(nameof(PostCommitCases));
        var observed = false;
        var closures = new ObservedList<(string Path, IReadOnlyList<IssueKind> Kinds)>(
            [(Child, IssueClosure.AfterEffect)], () =>
            {
                if (observed) return;
                using var visible = StorageCommand.Create(inspect, null, StateSchema.SelectSyncedByLocalKey,
                    (StateSchema.ParamPair, PairId), (StateSchema.ParamTargetKey, PathNames.Key(Child)));
                using var reader = visible.ExecuteReader();
                if (!reader.Read()) return;
                observed = true;
                throw fault;
            });
        try
        {
            Exception? reported = null;
            try { await store.CommitRecordAsync(PairId, StateChange.Empty with { Record = [Saved()], Close = closures }, null); }
            catch (Exception error) { reported = error; }
            Check.Require(ReferenceEquals(reported, fault) && observed && store.LimiterLevel(Account) == Single,
                nameof(PostCommitCases));
        }
        finally { Execute(inspect, "DROP TRIGGER count_records;"); }
        Check.Require(store.SyncedByKey(PairId, Side.Local, PathNames.Key(Child)) == Saved() &&
            store.Issues(PairId).Count == 0, nameof(PostCommitCases));
        store.Commit(PairId, Raise(TextKey.FailChanged));
        store.Commit(PairId, StateChange.Empty with { Close = [(Child, IssueClosure.AfterEffect)] });
        Check.Require(store.Issues(PairId).Count == 0, nameof(PostCommitCases));
    }

    private static async Task DecoratedCases(string path)
    {
        using var store = new Massdex.SyncShark.Engine.Tests.TestDoubles.StoreCounters(StateStore.Open(path));
        store.SavePair(Pair());
        store.Reset();
        var subject = Subject(kind: IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice);
        var raised = new List<Issue>();
        store.Commit(PairId, Raise(TextKey.FailChanged, subject: subject), raised.Add);
        Check.Require(store.CommitCalls == Single && raised.Single().Subject.FirstRunDeletion == subject.FirstRunDeletion,
            nameof(DecoratedCases));
        var settlement = StateChange.Empty with { Record = [Saved()], FirstRunAcknowledged = subject.Items,
            Close = [(Child, IssueClosure.AfterEffect)],
            Raise = Raise(TextKey.InfoDropboxHistoryUnchecked, kind: IssueKind.Info).Raise };
        RollbackAt(path, "INSERT ON issue", () => store.Commit(PairId, settlement, raised.Add));
        Check.Require(raised.Count == Single, nameof(DecoratedCases));
        store.Commit(PairId, settlement, raised.Add);
        Check.Require(raised.Count == BothSides && raised[^1].Kind == IssueKind.Info, nameof(DecoratedCases));
        store.Reset();
        await store.CommitRecordAsync(PairId, StateChange.Empty with { Record = [Saved(Other)] }, null);
        Check.Require(store.RecordCommitCalls == Single && store.RecordCommittedMembers == Single &&
            store.RecordCommitFailures == 0 && store.CommitCalls == Single, nameof(DecoratedCases));
    }

    private static async Task MarkerCases(string path)
    {
        foreach (var retained in new[] { IssueSubject.FirstRunDeletionKind.Item,
            IssueSubject.FirstRunDeletionKind.UnscannedSubtree,
            IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice })
        {
            using (var store = StateStore.Open(path))
            {
                if (store.Pair(PairId) is null) store.SavePair(Pair());
                var subject = Subject(kind: retained);
                store.Commit(PairId, Raise(TextKey.FailChanged, subject: subject));
                foreach (var cause in new[] { TextKey.FailFirstSyncCheckWaiting, TextKey.SkipUnreadable })
                {
                    store.Commit(PairId, Raise(cause, subject: Subject()));
                    var issue = store.Issues(PairId).Single(item => item.Kind == IssueKind.Failure);
                    Check.Require(issue.Cause == TextKey.FailChanged && issue.Subject.FirstRunDeletion == retained,
                        nameof(MarkerCases));
                }
                store.Commit(PairId, StateChange.Empty with { Close = [(Child, IssueClosure.AfterEffect)] });
                Check.Require(store.Issues(PairId).Any(issue => issue.Kind == IssueKind.Failure), nameof(MarkerCases));
                var settlement = StateChange.Empty with
                {
                    Record = [Saved()], FirstRunAcknowledged = subject.Items,
                    Close = [(Child, IssueClosure.AfterEffect)]
                };
                if (retained == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice)
                    settlement = settlement with
                        { Raise = Raise(TextKey.InfoDropboxHistoryUnchecked, kind: IssueKind.Info).Raise };
                Refuses<SqliteException>(() => store.Commit(PairId, settlement with
                    { Raise = Raise(TextKey.FailChanged, null!).Raise }));
                Check.Require(store.Issues(PairId).Single(issue => issue.Kind == IssueKind.Failure)
                    .Subject.FirstRunDeletion == retained &&
                    store.SyncedByKey(PairId, Side.Local, PathNames.Key(Parent)) is null, nameof(MarkerCases));
                if (settlement.Raise.Count == 0) await store.CommitRecordAsync(PairId, settlement, null);
                else store.Commit(PairId, settlement);
                Check.Require(store.Issues(PairId).All(issue => issue.Kind == IssueKind.Info), nameof(MarkerCases));
                store.Commit(PairId, StateChange.Empty with { Close = [(Child, new[] { IssueKind.Info })] });
            }
            using var reopened = StateStore.Open(path);
            Check.Require(reopened.SyncedByKey(PairId, Side.Local, PathNames.Key(Child)) == Saved() &&
                reopened.Issues(PairId).Count == 0, nameof(MarkerCases));
        }
        using var final = StateStore.Open(path);
        final.Commit(PairId, Raise(TextKey.FailFirstSyncCheckWaiting,
            subject: Subject(kind: IssueSubject.FirstRunDeletionKind.Item)));
        final.Commit(PairId, Raise(TextKey.FailChanged, subject: Subject(id: Other)));
        Check.Require(final.Issues(PairId).Single().Subject.FirstRunDeletion ==
            IssueSubject.FirstRunDeletionKind.None, nameof(MarkerCases));
    }

    private static PendingEffect Pending(SyncedEntry? saved = null) => new(PendingVersion,
        Guid.NewGuid().ToString(), 0, PendingKind.LocalDelete, PendingPhase.Armed, Child,
        new RemovalProof(Entry(), Domain, ZeroHash), saved, null, null, null, null, false, null);

    private static Task PendingCases(string path)
    {
        var pending = Pending();
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            Check.Require(store.Pending(PairId) is NoPending, nameof(PendingCases));
            store.ArmPending(PairId, pending);
            using (var inspect = Inspect(path))
            {
                using var type = StorageCommand.Create(inspect, null,
                    "SELECT typeof(pending_json) FROM pair WHERE pair_id=$p;", (StateSchema.ParamPair, PairId));
                Check.Require((string)type.ExecuteScalar()! == "text", nameof(PendingCases));
            }
            Refuses<InvalidOperationException>(() => store.ArmPending(PairId, Pending()));
            Check.Require(store.Pending(PairId) is CurrentPending current && current.Value == pending, nameof(PendingCases));
            var encoded = StateEncoding.EncodePending(pending);
            var firstDecode = (CurrentPending)StateEncoding.DecodePending(encoded);
            var secondDecode = (CurrentPending)StateEncoding.DecodePending(encoded);
            Check.Require(firstDecode.Value == secondDecode.Value &&
                StateEncoding.EncodePending(firstDecode.Value) == encoded, nameof(PendingCases));
            Refuses<InvalidOperationException>(() => store.RemovePair(PairId));
            var pair = store.Pair(PairId)!;
            Refuses<InvalidOperationException>(() =>
                store.SavePairDecision(pair, Generation(store), pair, null, true, null));
            var next = pending with { Generation = pending.Generation + 1, Phase = PendingPhase.Removed };
            Refuses<InvalidOperationException>(() => store.AdvancePending(PairId,
                pending with { Cause = TextKey.FailChanged }, next));
            RollbackAt(path, "UPDATE ON pair", () => store.AdvancePending(PairId, pending, next));
            store.AdvancePending(PairId, pending, next);
            using (var inspect = Inspect(path))
            {
                using var type = StorageCommand.Create(inspect, null,
                    "SELECT typeof(pending_json) FROM pair WHERE pair_id=$p;", (StateSchema.ParamPair, PairId));
                Check.Require((string)type.ExecuteScalar()! == "text", nameof(PendingCases));
            }
            Refuses<InvalidOperationException>(() => store.AdvancePending(PairId, pending, next));
            Refuses<ArgumentException>(() => store.SettlePending(PairId, next,
                StateChange.Empty with { ForgetSubtrees = [(Side.Local, PathNames.Key(Parent))] }));
            pending = next;
        }
        using var reopened = StateStore.Open(path);
        Check.Require(reopened.Pending(PairId) is CurrentPending currentPending && currentPending.Value == pending,
            nameof(PendingCases));
        RollbackAt(path, "UPDATE ON pair", () => reopened.SettlePending(PairId, pending, StateChange.Empty));
        reopened.SettlePending(PairId, pending, StateChange.Empty);
        Check.Require(reopened.Pending(PairId) is NoPending, nameof(PendingCases));
        var saved = Saved() with { LocalId = LocalId };
        reopened.Commit(PairId, StateChange.Empty with { Record = [saved] });
        Refuses<InvalidOperationException>(() => reopened.ArmPending(PairId, Pending()));
        var withSaved = Pending(saved);
        reopened.ArmPending(PairId, withSaved);
        var removed = withSaved with { Generation = withSaved.Generation + 1, Phase = PendingPhase.Removed };
        reopened.AdvancePending(PairId, withSaved, removed);
        reopened.Commit(PairId, StateChange.Empty with { Record = [saved with { DropboxId = Other }] });
        Refuses<InvalidOperationException>(() => reopened.SettlePending(PairId, removed,
            StateChange.Empty with { ForgetSelf = [(Side.Local, saved.LocalKey)] }));
        Check.Require(reopened.Pending(PairId) is CurrentPending &&
            reopened.SyncedByKey(PairId, Side.Local, saved.LocalKey)!.DropboxId == Other, nameof(PendingCases));
        return Task.CompletedTask;
    }

    private static Task SelectedPendingSettlement(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(BoundPair());
        var provenance = InstallDropbox(store);
        var saved = Saved() with { LocalId = LocalId };
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var source = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var through = store.AcquireDropboxPage(PairId, provenance.PairGeneration, source.Epoch, source.Revision,
            provenance, source.Cursor,
            [new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, Child, null, saved.DropboxId, null, false, true)], NextCursor);
        var selection = new WorkSelection(PairId, provenance.PairGeneration, source.Epoch, Side.Dropbox,
            provenance.NamespaceId, saved.DropboxId, saved.DropboxKey, through, through);
        store.BeginSelection(selection);
        var pending = Pending(saved);
        store.ArmPending(PairId, pending);
        var removed = pending with { Generation = pending.Generation + 1, Phase = PendingPhase.Removed };
        store.AdvancePending(PairId, pending, removed);
        store.SettlePending(PairId, removed, StateChange.Empty with { ForgetSelf = [(Side.Local, saved.LocalKey)] }, selection, null);
        Check.Require(store.Pending(PairId) is NoPending && store.SyncedById(PairId, Side.Dropbox, saved.DropboxId) is null &&
            store.ReadWork(selection).Single().Before is null,
            "M1: pending Local delete and selected baseline settle in one transaction without a retry episode");
        return Task.CompletedTask;
    }

    private static Task PendingAcknowledgmentCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var subject = Subject(kind: IssueSubject.FirstRunDeletionKind.Item);
        store.Commit(PairId, Raise(TextKey.FailChanged, subject: subject));
        var pending = Pending();
        store.ArmPending(PairId, pending);
        var acknowledgment = StateChange.Empty with
        {
            ForgetSelf = [(Side.Local, PathNames.Key(Child))],
            FirstRunAcknowledged = subject.Items,
            Close = [(Child, IssueClosure.AfterEffect)]
        };
        Refuses<ArgumentException>(() => store.SettlePending(PairId, pending, acknowledgment));
        Check.Require(store.Pending(PairId) is CurrentPending &&
            store.Issues(PairId).Single().Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item,
            nameof(PendingAcknowledgmentCases));
        var removed = pending with { Generation = pending.Generation + 1, Phase = PendingPhase.Removed };
        store.AdvancePending(PairId, pending, removed);
        foreach (var invalid in new[]
        {
            acknowledgment with { ForgetSelf = [(Side.Dropbox, PathNames.Key(Child))] },
            acknowledgment with { ForgetSelf = [(Side.Local, PathNames.Key(Other))] },
            acknowledgment with { ForgetSubtrees = [(Side.Local, PathNames.Key(Parent))] },
            acknowledgment with { FirstRunAcknowledged = [new IssueItem(Side.Local, Child, Other)] },
            acknowledgment with { FirstRunAcknowledged = [new IssueItem(Side.Local, Other, LocalId)] },
            acknowledgment with { Record = [Saved()] }
        })
        {
            Refuses<ArgumentException>(() => store.SettlePending(PairId, removed, invalid));
            Check.Require(store.Pending(PairId) is CurrentPending current && current.Value == removed &&
                store.Issues(PairId).Single().Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item,
                "Rejected first-run settlement retains the exact pending operation and discovery obligation.");
        }
        store.SettlePending(PairId, removed, acknowledgment);
        Check.Require(store.Pending(PairId) is NoPending && store.Issues(PairId).Count == 0 &&
            store.SyncedByKey(PairId, Side.Local, PathNames.Key(Child)) is null,
            "An unsaved first-run history deletion settles and acknowledges without creating a saved mapping.");
        return Task.CompletedTask;
    }

    private static Task UnsavedPendingBindingRaces(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var pending = Pending();
        store.ArmPending(PairId, pending);
        var removed = pending with { Generation = pending.Generation + 1, Phase = PendingPhase.Removed };
        store.AdvancePending(PairId, pending, removed);
        var change = StateChange.Empty with { ForgetSelf = [(Side.Local, PathNames.Key(Child))] };
        foreach (var arrival in new[]
        {
            Saved() with { LocalId = Other },
            Saved(Other) with { LocalId = LocalId }
        })
        {
            store.Commit(PairId, StateChange.Empty with { Record = [arrival] });
            Refuses<InvalidOperationException>(() => store.SettlePending(PairId, removed, change));
            Check.Require(store.Pending(PairId) is CurrentPending current && current.Value == removed &&
                store.SyncedByKey(PairId, Side.Local, arrival.LocalKey) == arrival,
                "A saved binding arriving at either the losing path or identity refuses settlement without forgetting it.");
            store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, arrival.LocalKey)] });
        }
        store.SettlePending(PairId, removed, change);
        Check.Require(store.Pending(PairId) is NoPending, nameof(UnsavedPendingBindingRaces));
        return Task.CompletedTask;
    }

    private static Task CrossedPendingReplacement(string path)
    {
        foreach (var real in new[] { true, false })
        foreach (var race in new[] { "none", "path", "identity", "changed", "retired", "moved" })
        {
            var database = path + "-" + race;
            using IStateStore store = real ? StateStore.Open(database) : new InMemoryStateStore();
            store.SavePair(BoundPair());
            var saved = Saved(Other) with { DropboxKey = PathNames.DropboxKey(Child), DropboxPath = Child };
            store.Commit(PairId, StateChange.Empty with { Record = [saved] });
            var source = Entry() with { Id = saved.DropboxId };
            var stage = new StageProof(Stage, Domain, "published-id", 0, ZeroHash);
            var pending = Pending(saved) with { Kind = PendingKind.LocalReplace, Source = source, Stage = stage };
            var final = SyncedEntries.From(Entry() with { Id = stage.FileId }, source);
            var change = StateChange.Empty with { Record = [final] };
            var before = store.ReadWorkWatermark();

            // A moved source and every non-replacement effect retain the original exact-binding refusal.
            Refuses<InvalidOperationException>(() => store.ArmPending(PairId,
                pending with { Source = source with { Path = Moved } }));
            Refuses<InvalidOperationException>(() => store.ArmPending(PairId, Pending(saved)));
            var arrival = race switch
            {
                "path" => Saved() with { LocalId = "unrelated-id", DropboxKey = PathNames.DropboxKey(Sibling), DropboxPath = Sibling },
                "identity" => Saved(Sibling) with { LocalId = LocalId },
                "changed" => saved with { LocalSize = saved.LocalSize + 1 },
                "moved" => saved with { DropboxKey = PathNames.DropboxKey(Moved), DropboxPath = Moved },
                _ => null
            };
            if (race is "path" or "identity")
            {
                store.Commit(PairId, StateChange.Empty with { Record = [arrival!] });
                Refuses<InvalidOperationException>(() => store.ArmPending(PairId, pending));
                store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, arrival!.LocalKey)] });
            }
            store.ArmPending(PairId, pending);
            Check.Require(store.Pending(PairId) is CurrentPending armed && armed.Value.Saved == saved,
                "Returning Local replacement preserves the original crossed binding in custody.");
            var published = pending with { Generation = pending.Generation + 1, Phase = PendingPhase.Published };
            store.AdvancePending(PairId, pending, published);
            if (arrival is not null) store.Commit(PairId, StateChange.Empty with { Record = [arrival] });
            if (race == "retired")
                store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, saved.LocalKey)] });
            if (race != "none")
            {
                var watermark = store.ReadWorkWatermark();
                Refuses<InvalidOperationException>(() => store.SettlePending(PairId, published, change));
                Check.Require(store.Pending(PairId) is CurrentPending held && held.Value == published &&
                    store.ReadWorkWatermark() == watermark &&
                    store.SyncedByKey(PairId, Side.Local, final.LocalKey) != final,
                    "Changed custody refuses settlement without binding, release or pending mutation.");
                continue;
            }
            if (real)
                RollbackAt(database, "UPDATE ON pair", () => store.SettlePending(PairId, published, change));
            Check.Require(store.Pending(PairId) is CurrentPending &&
                store.SyncedByKey(PairId, Side.Dropbox, saved.DropboxKey) == saved && store.ReadWorkWatermark() == before,
                "Pending rollback retains the original binding and inserts no release work.");
            store.SettlePending(PairId, published, change);
            Check.Require(store.Pending(PairId) is NoPending &&
                store.SyncedByKey(PairId, Side.Local, final.LocalKey) == final &&
                store.SyncedByKey(PairId, Side.Local, saved.LocalKey) is null && store.ReadWorkWatermark() == before + 1,
                "Published returning Local replacement settles with one durable released partner.");
            if (real)
            {
                using var reopened = StateStore.OpenReadOnly(database)!;
                Check.Require(reopened.Pending(PairId) is NoPending && reopened.ReadWorkWatermark() == before + 1 &&
                    reopened.SyncedByKey(PairId, Side.Local, final.LocalKey) == final,
                    "Pending settlement and release survive reopening the real database.");
            }
        }
        return Task.CompletedTask;
    }

    private static SourceProvenance InstallDropbox(IStateStore store)
    {
        var generation = Generation(store);
        var source = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var provenance = new SourceProvenance(Account, Namespace, RemoteId, RemoteRoot,
            true, true, true, true, generation, source.Epoch);
        store.AdvanceSource(PairId, Side.Dropbox, generation, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch, source.Revision, provenance, Cursor, source.Obligation);
        return provenance;
    }

    private static IEnumerable<ChangeNotice> BrokenPage(ChangeNotice notice, Exception error)
    {
        yield return notice;
        throw error;
    }

    private static Task PageCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var provenance = InstallDropbox(store);
        var source = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var generation = Generation(store);
        var notice = new ChangeNotice(Side.Dropbox, ChangeKind.Changed, Child, null, RemoteId,
            Entry() with { Id = RemoteId }, false, false);
        var watermark = store.ReadWorkWatermark();
        foreach (var failure in new Exception[] { new IOException(nameof(PageCases)), new OperationCanceledException() })
        {
            try
            {
                store.AcquireDropboxPage(PairId, generation, source.Epoch, source.Revision, provenance,
                    Cursor, BrokenPage(notice, failure), NextCursor);
                throw new InvalidOperationException(nameof(PageCases));
            }
            catch (Exception error) when (ReferenceEquals(error, failure)) { }
            Check.Require(store.ReadWorkWatermark() == watermark && store.ReadSource(PairId, Side.Dropbox) == source &&
                store.ReadPrerequisiteRetry(PairId) is null, nameof(PageCases));
        }
        foreach (var stale in new[]
        {
            (generation + 1, source.Epoch, source.Revision, Cursor),
            (generation, source.Epoch + 1, source.Revision, Cursor),
            (generation, source.Epoch, source.Revision + 1, Cursor),
            (generation, source.Epoch, source.Revision, NextCursor)
        })
            Refuses<LiveAdmissionStaleException>(() => store.AcquireDropboxPage(PairId,
                stale.Item1, stale.Item2, stale.Item3, provenance, stale.Item4, [notice], NextCursor));
        foreach (var boundary in new[] { "INSERT ON change_work", "UPDATE ON source_state" })
            RollbackAt(path, boundary, () => store.AcquireDropboxPage(PairId, generation,
                source.Epoch, source.Revision, provenance, Cursor, [notice], NextCursor));
        var notices = new[] { notice, notice with { Kind = ChangeKind.Deleted, Observed = null }, notice };
        long committed;
        using (var inspect = Inspect(path))
        {
            Execute(inspect, "CREATE TRIGGER no_page_pair_write BEFORE UPDATE ON pair BEGIN SELECT RAISE(ABORT,'page-pair-write'); END;");
            try
            {
                committed = store.AcquireDropboxPage(PairId, generation, source.Epoch, source.Revision,
                    provenance, Cursor, notices, NextCursor);
            }
            finally { Execute(inspect, "DROP TRIGGER no_page_pair_write;"); }
        }
        Check.Require(committed == watermark + notices.Length && store.ReadSource(PairId, Side.Dropbox)!.Value.Cursor == NextCursor &&
            store.ReadPrerequisiteRetry(PairId) is null, nameof(PageCases));
        Refuses<LiveAdmissionStaleException>(() => store.AcquireDropboxPage(PairId, generation,
            source.Epoch, source.Revision, provenance, Cursor, notices, NextCursor));
        using var reopened = StateStore.OpenReadOnly(path)!;
        Check.Require(reopened.ReadSource(PairId, Side.Dropbox)!.Value.Cursor == NextCursor &&
            reopened.ReadWorkWatermark() == committed, nameof(PageCases));
        var selection = new WorkSelection(PairId, generation, source.Epoch, Side.Dropbox, Namespace,
            RemoteId, PathNames.Key(Child), watermark + 1, committed);
        Check.Require(store.ReadWork(selection).Select(item => item.Notice).SequenceEqual(notices), nameof(PageCases));
        Check.Require(store.AcquireDropboxPage(PairId, generation, source.Epoch, source.Revision,
            provenance, NextCursor, [], NextCursor) == committed, nameof(PageCases));
        return Task.CompletedTask;
    }

    private static IEnumerable<(string PairId, ChangeNotice Notice)> BrokenJournal(ChangeNotice notice)
    {
        yield return (PairId, notice);
        throw new IOException(nameof(BrokenJournal));
    }

    private static Task JournalCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        store.SavePair(Pair(OtherPair));
        var generation = Generation(store);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var other = store.ReadSource(OtherPair, Side.Local)!.Value;
        var members = new[] { (PairId, generation, local.Epoch, local.Revision, false),
            (OtherPair, store.ReadPairExecution(OtherPair)!.Value.Generation, other.Epoch, other.Revision, false) };
        var position = new JournalPosition(Single, 0);
        Check.Require(store.AcquireJournalPage(Domain, null, position, members, []) == 0, nameof(JournalCases));
        var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, Child, null, LocalId, Entry(), true, false);
        Refuses<IOException>(() => store.AcquireJournalPage(Domain, position,
            position with { NextUsn = position.NextUsn + 1 }, members, BrokenJournal(notice)));
        Check.Require(store.ReadJournal(Domain) == position && store.ReadWorkWatermark() == 0, nameof(JournalCases));
        var wrong = members.ToArray();
        wrong[0] = (PairId, generation + 1, local.Epoch, local.Revision, false);
        Check.Require(store.AcquireJournalPage(Domain, position, position, wrong, []) is null,
            nameof(JournalCases));
        foreach (var stale in new[]
        {
            (PairId, generation, local.Epoch + 1, local.Revision, false),
            (PairId, generation, local.Epoch, local.Revision + 1, false)
        })
            Check.Require(store.AcquireJournalPage(Domain, position, position, [stale], []) is null,
                nameof(JournalCases));
        Check.Require(store.AcquireJournalPage(Domain, position with { NextUsn = position.NextUsn + 1 },
            position, members, []) is null, nameof(JournalCases));
        Check.Require(store.ReadJournal(Domain) == position && store.ReadWorkWatermark() == 0,
            nameof(JournalCases));
        var next = position with { NextUsn = position.NextUsn + 1 };
        RollbackAt(path, "UPDATE ON journal_position", () =>
            store.AcquireJournalPage(Domain, position, next, members, [(PairId, notice)]));
        store.AcquireJournalPage(Domain, position, next, members, [(PairId, notice)]);
        var evidence = store.ReadLocalDirty(PairId, generation, local.Epoch, Domain, LocalId,
            PathNames.Key(Child), null);
        Check.Require(evidence.Dirty && evidence.EvidenceVersion > 0, nameof(JournalCases));
        var pair = store.Pair(PairId)!;
        store.SavePairDecision(pair, generation, pair with { Name = "renamed" }, null, false, null);
        var current = Generation(store);
        var watermark = store.ReadWorkWatermark();
        var gap = store.ReadSource(PairId, Side.Local)!.Value;
        Check.Require(gap.Obligation == SourceObligation.LocalSkippedCoverage && gap.Epoch == local.Epoch + 1 &&
            gap.Revision == local.Revision + 1 && store.ReadLocalGap(PairId, gap.Epoch) == gap.Revision,
            nameof(JournalCases));
        members[0] = (PairId, current, gap.Epoch, gap.Revision, false);
        store.AcquireJournalPage(Domain, next, next, members, []);
        Check.Require(store.ReadSource(PairId, Side.Local) == gap && store.ReadWorkWatermark() == watermark,
            nameof(JournalCases));
        var renamed = store.Pair(PairId)!;
        store.SavePairDecision(renamed, current, renamed with { Enabled = false }, null, false, null);
        Check.Require(store.ReadSource(PairId, Side.Local)!.Value.Obligation == SourceObligation.LocalSkippedCoverage,
            nameof(JournalCases));
        return Task.CompletedTask;
    }

    private static Task PageWaitCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var generation = Generation(store);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var failure = Raise(TextKey.RequiredRepairRetry, string.Empty, new IssueSubject([], null));
        RollbackAt(path, "INSERT ON issue", () =>
            store.RecordPrerequisitePageWait(PairId, generation, new object(), failure));
        foreach (var end in new[] { RequestResult.Success, RequestResult.CallerStopped, RequestResult.PermanentFailure })
        {
            var operation = new object();
            var first = store.RecordPrerequisitePageWait(PairId, generation, operation, failure);
            var repeat = store.RecordPrerequisitePageWait(PairId, generation, operation, failure);
            Check.Require(first.Id == repeat.Id && store.Issues(PairId).Count == Single &&
                store.ReadPrerequisiteRetry(PairId) is null && store.ReadSource(PairId, Side.Local) == local &&
                store.ReadSource(PairId, Side.Dropbox) == remote, nameof(PageWaitCases) + end);
            Refuses<InvalidOperationException>(() =>
                store.CompletePrerequisitePageWait(PairId, generation, new object(), repeat));
            Refuses<InvalidOperationException>(() =>
                store.CompletePrerequisitePageWait(PairId, generation, operation, repeat with { Id = repeat.Id + 1 }));
            RollbackAt(path, "DELETE ON issue", () =>
                store.CompletePrerequisitePageWait(PairId, generation, operation, repeat));
            store.CompletePrerequisitePageWait(PairId, generation, operation, repeat);
            Check.Require(store.Issues(PairId).Count == 0 && store.ReadSource(PairId, Side.Local) == local,
                nameof(PageWaitCases));
        }
        var episode = new Massdex.SyncShark.V2.FailureRetryOwner.State(Single, DateTimeOffset.UnixEpoch.AddTicks(Single));
        RollbackAt(path, "INSERT ON issue", () => store.RecordPrerequisiteFailure(PairId, generation,
            local.Epoch, remote.Epoch, local.Revision, remote.Revision, null, episode, failure));
        store.RecordPrerequisiteFailure(PairId, generation, local.Epoch, remote.Epoch,
            local.Revision, remote.Revision, null, episode, failure);
        var owner = new object();
        var protectedNotice = store.RecordPrerequisitePageWait(PairId, generation, owner, failure);
        store.CompletePrerequisitePageWait(PairId, generation, owner, protectedNotice);
        Check.Require(store.ReadPrerequisiteRetry(PairId) == episode && store.Issues(PairId).Single().Id == protectedNotice.Id &&
            store.RetryNoticeProtected(PairId, protectedNotice.Id), nameof(PageWaitCases));
        store.Commit(PairId, StateChange.Empty with { Close = [(string.Empty, IssueClosure.AfterEffect)] });
        store.Commit(PairId, Raise(TextKey.FailChanged, string.Empty, new IssueSubject([], null)));
        Check.Require(store.Issues(PairId).Single().Cause == TextKey.RequiredRepairRetry, nameof(PageWaitCases));
        Refuses<InvalidOperationException>(() => store.RecordPrerequisiteFailure(PairId, generation,
            local.Epoch, remote.Epoch, local.Revision, remote.Revision, null, episode, failure));
        var pair = store.Pair(PairId)!;
        RollbackAt(path, "UPDATE ON source_state", () =>
            store.SavePairDecision(pair, generation, pair with { Enabled = false }, null, false, null));
        store.SavePairDecision(pair, generation, pair with { Enabled = false }, null, false, null);
        Check.Require(store.ReadPrerequisiteRetry(PairId) == episode &&
            store.Issues(PairId).Single().Cause == TextKey.RequiredRepairRetry, nameof(PageWaitCases));
        using var reopened = StateStore.OpenReadOnly(path)!;
        Check.Require(reopened.ReadPrerequisiteRetry(PairId) == episode, nameof(PageWaitCases));
        return Task.CompletedTask;
    }

    private static Task DropboxResetRetryCases(string path)
    {
        foreach (var real in new[] { true, false })
        foreach (var changedDomain in new[] { false, true })
        foreach (var acquired in new[] { false, true })
        {
            using IStateStore store = real
                ? StateStore.Open(path + changedDomain + acquired, (_, _, _, _, _) => true)
                : new InMemoryStateStore();
            var pair = BoundPair();
            store.SavePair(pair);
            InstallDropbox(store);
            var generation = Generation(store);
            var remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
            WorkSelection? selection = null;
            if (acquired)
            {
                var work = store.AcquireDropboxPage(PairId, generation, remote.Epoch, remote.Revision,
                    remote.Provenance!, remote.Cursor,
                    [new ChangeNotice(Side.Dropbox, ChangeKind.Changed, Child, null, RemoteId,
                        Entry() with { Id = RemoteId }, false, false)], NextCursor);
                selection = new WorkSelection(PairId, generation, remote.Epoch, Side.Dropbox, Namespace,
                    RemoteId, PathNames.DropboxKey(Child), work, work);
            }
            var subject = new IssueSubject([new IssueItem(Side.Dropbox, Child, RemoteId)], null);
            var attempt = Guid.NewGuid();
            var retry = store.RecordInUseFailure(PairId, generation, remote.Epoch, Child, subject,
                TextKey.FailLocalInUse, selection, ItemRetryDispatch.LocalBranch, null, null, attempt);
            var local = store.RecordInUseFailure(PairId, generation,
                store.ReadSource(PairId, Side.Local)!.Value.Epoch, Sibling, Subject(Sibling),
                TextKey.FailLocalInUse, null, ItemRetryDispatch.LocalBranch, null, null, attempt);
            if (changedDomain)
                store.SavePair(pair with { Roots = pair.Roots! with
                { Dropbox = pair.Roots.Dropbox with { RootNamespaceId = Other } } });
            // The feed first invalidates the expired cursor, then installs a fresh base in another epoch.
            foreach (var cursor in new string?[] { null, NextCursor })
            {
                remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
                var nextEpoch = remote.Epoch + 1;
                store.AdvanceSource(PairId, Side.Dropbox, generation, remote.Epoch, remote.Revision,
                    remote.Provenance, remote.Cursor, nextEpoch, remote.Revision + 1,
                    cursor is null ? null : new SourceProvenance(Account, changedDomain ? Other : Namespace,
                        RemoteId, RemoteRoot, true, true, true, true, generation, nextEpoch), cursor,
                    SourceObligation.NormalRepair);
                var current = store.ReadItemRetryState(PairId, [retry.Token.IssueId])[retry.Token.IssueId];
                if (changedDomain || acquired)
                {
                    Check.Require(current is null && store.IssueById(PairId, retry.Token.IssueId) is not null,
                        "Changed-domain and acquired-notice episodes retire while their Failure stays visible.");
                    if (!acquired)
                        Refuses<LiveAdmissionStaleException>(() => store.ReadWork(new WorkSelection(PairId,
                            generation, nextEpoch, Side.Dropbox, Namespace, RemoteId, PathNames.DropboxKey(Child),
                            retry.Token.WorkId, retry.Token.WorkId)).ToArray());
                }
                else
                {
                    Check.Require(current is not null && current.Token.SourceEpoch == nextEpoch &&
                        current.Token.WorkId == retry.Token.WorkId && current.Token.EpisodeId == retry.Token.EpisodeId &&
                        store.TryClaimItemRetry(current, attempt),
                        "Reset and reinstall retain one claimable Dropbox-origin local-access retry.");
                    retry = current!;
                }
                Check.Require(store.TryClaimItemRetry(local, attempt),
                    "A Dropbox reset leaves the Local-source retry unchanged and claimable.");
            }
            if (!changedDomain && !acquired)
            {
                store.CompleteItemAttempt(retry, attempt,
                    StateChange.Empty with { Close = [(Child, IssueClosure.AfterEffect)] }, null, null);
                Check.Require(store.ReadItemRetryState(PairId, [retry.Token.IssueId])[retry.Token.IssueId] is null,
                    "The re-homed retry completes instead of stranding its episode in the old epoch.");
            }
        }
        return Task.CompletedTask;
    }

    private static async Task DropboxBookmarkCases(string path)
    {
        foreach (var real in new[] { true, false })
        foreach (var acquired in new[] { true, false })
        {
            using IStateStore store = real ? StateStore.Open(path + acquired) : new InMemoryStateStore();
            store.SavePair(BoundPair());
            InstallDropbox(store);
            var generation = Generation(store);
            var local = store.ReadSource(PairId, Side.Local)!.Value;
            var remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
            store.AcquireJournalPage(Domain, null, new JournalPosition(Single, 0),
                [(PairId, generation, local.Epoch, local.Revision, false)], []);
            var token = store.BeginScan(PairId, generation, false);
            foreach (var side in Enum.GetValues<Side>()) await store.OpenScan(PairId, side, ScanTestSettings.BatchRows).CompleteAsync();
            var localCatchUp = new LocalCatchUp(PairId, generation, local.Epoch,
                new NtfsUsnCheckpoint(Single, 0, 0, 0), store.ReadWorkWatermark());
            var catchUp = new DropboxCatchUp(PairId, generation, remote.Epoch, remote.Cursor!,
                store.ReadWorkWatermark());
            var change = new ChangeNotice(Side.Dropbox, ChangeKind.Changed, Child, null, RemoteId,
                Entry() with { Id = RemoteId }, false, false);
            if (!acquired)
            {
                store.AdvanceSource(PairId, Side.Dropbox, generation, remote.Epoch, remote.Revision,
                    remote.Provenance, remote.Cursor, remote.Epoch, remote.Revision, remote.Provenance,
                    NextCursor, remote.Obligation);
                Refuses<LiveAdmissionStaleException>(() => store.FinishRepair(PairId, Summary(), generation,
                    token, localCatchUp, catchUp));
            }
            remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
            var through = store.AcquireDropboxPage(PairId, generation, remote.Epoch, remote.Revision,
                remote.Provenance!, remote.Cursor, [change], NextCursor);
            // Even a subsequent committed page cannot launder a cursor changed outside acquisition.
            if (!acquired)
            {
                Refuses<LiveAdmissionStaleException>(() => store.FinishRepair(PairId, Summary(), generation,
                    token, localCatchUp, catchUp));
                Check.Require(!store.FirstRepairDone(PairId), "Unexplained cursor changes invalidate Repair.");
                continue;
            }
            // Empty committed pages also advance opaque cursors without creating work IDs.
            store.AcquireDropboxPage(PairId, generation, remote.Epoch, remote.Revision,
                remote.Provenance!, NextCursor, [], EmptyPageCursor);
            Check.Require(store.ReadSource(PairId, Side.Dropbox)!.Value.Cursor == EmptyPageCursor,
                "An empty page durably advances to its distinct cursor without adding work.");
            store.FinishRepair(PairId, Summary(), generation, token, localCatchUp, catchUp);
            var pending = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
            Check.Require(store.FirstRepairDone(PairId) && pending.ThroughWorkId == through &&
                store.ReadWork(pending).Single().Notice == change,
                "A page committed after catch-up remains owned by AutoSync after Repair completes.");
            store.CompleteWork(pending, null);
            store.EndWorkPass(PairId);
            Check.Require(store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) is null,
                "The handed-off change is consumed exactly once.");
        }
    }

    private static async Task CompletionCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(BoundPair());
        InstallDropbox(store);
        var generation = Generation(store);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var position = new JournalPosition(Single, 0);
        var members = new[] { (PairId, generation, local.Epoch, local.Revision, false) };
        store.AcquireJournalPage(Domain, null, position, members, []);
        var token = store.BeginScan(PairId, generation, true);
        foreach (var side in Enum.GetValues<Side>())
        {
            var sink = store.OpenScan(PairId, side, ScanTestSettings.BatchRows);
            if (side == Side.Dropbox) await sink.AddAsync(new Listed(Parent, null), CancellationToken.None);
            await sink.CompleteAsync();
        }
        store.SaveDeletedHistory(PairId, token, Cursor);
        var localCatchUp = new LocalCatchUp(PairId, generation, local.Epoch,
            new NtfsUsnCheckpoint(Single, 0, 0, 0), 0);
        var remoteCatchUp = new DropboxCatchUp(PairId, generation, remote.Epoch, Cursor, 0);
        Refuses<LiveAdmissionStaleException>(() => store.FinishRepair(PairId, Summary(), generation,
            Guid.NewGuid(), localCatchUp, remoteCatchUp));
        Check.Require(!store.FirstRepairDone(PairId) && store.ReadSource(PairId, Side.Local) == local,
            nameof(CompletionCases));
        foreach (var boundary in new[] { "UPDATE ON pair", "UPDATE ON source_state" })
            RollbackAt(path, boundary, () => store.FinishRepair(PairId, Summary(), generation, token,
                localCatchUp, remoteCatchUp));
        store.FinishRepair(PairId, Summary(), generation, token, localCatchUp, remoteCatchUp);
        Check.Require(store.FirstRepairDone(PairId) && store.ReadSource(PairId, Side.Local)!.Value.Obligation is null &&
            store.ReadSource(PairId, Side.Dropbox)!.Value.Obligation is null, nameof(CompletionCases));
        using (var inspect = Inspect(path))
            Check.Require(Snapshot(inspect, "SELECT deleted_history_cursor IS NULL FROM pair;") == "[[0]]" &&
                Snapshot(inspect, "SELECT sum(name_count) FROM discovery_deleted_block;") == "[[1]]" &&
                Snapshot(inspect, "PRAGMA freelist_count;") == "[[0]]",
                "First Repair keeps its published history and reclaims remaining cleanup pages.");
        var readyLocal = store.ReadSource(PairId, Side.Local)!.Value;
        members[0] = (PairId, generation, readyLocal.Epoch, readyLocal.Revision, false);
        var change = new ChangeNotice(Side.Local, ChangeKind.Changed, Child, null, LocalId, Entry(), true, false);
        var nextPosition = position with { NextUsn = position.NextUsn + 1 };
        var through = store.AcquireJournalPage(Domain, position, nextPosition, members, [(PairId, change)])!.Value;
        position = nextPosition;
        localCatchUp = localCatchUp with { Checkpoint = localCatchUp.Checkpoint with { NextUsn = (long)position.NextUsn },
            ThroughWorkId = through };
        remoteCatchUp = remoteCatchUp with { ThroughWorkId = through };
        var selection = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        Check.Require(selection is not null, nameof(CompletionCases));
        var coverage = store.CaptureLiveCoverage(selection!, localCatchUp, remoteCatchUp);
        var footprint = new[] { new LiveEffectScope(Side.Local, Path.Combine(LocalRoot, Child), false) };
        store.ValidateLiveCoverage(selection!, coverage, footprint);
        Refuses<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection!,
            coverage with { Local = localCatchUp with { ThroughWorkId = 0 } }, footprint));
        Refuses<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection!, coverage, []));
        var baseline = Saved() with { LocalId = LocalId };
        store.Commit(PairId, StateChange.Empty with { Record = [baseline] });
        Refuses<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection!, coverage, footprint));
        store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, baseline.LocalKey)] });
        store.Commit(PairId, Raise(TextKey.TieEqualTimes, kind: IssueKind.Tie));
        Refuses<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection!, coverage, footprint));
        store.Commit(PairId, StateChange.Empty with { Close = [(Child, new[] { IssueKind.Tie })] });
        var later = store.AcquireJournalPage(Domain, position, position, members, [(PairId, change)])!.Value;
        Refuses<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection!, coverage, footprint));
        store.CompleteWork(selection!, null);
        var remaining = new WorkSelection(PairId, generation, readyLocal.Epoch, Side.Local, Domain, LocalId,
            PathNames.Key(Child), later, later);
        Check.Require(store.ReadWork(remaining).Single().Notice == change, nameof(CompletionCases));
        var original = store.ReadSource(PairId, Side.Local)!.Value;
        token = store.BeginScan(PairId, generation, false);
        foreach (var side in Enum.GetValues<Side>()) await store.OpenScan(PairId, side, ScanTestSettings.BatchRows).CompleteAsync();
        members[0] = (PairId, generation, original.Epoch, original.Revision, true);
        store.AcquireJournalPage(Domain, position, position, members, []);
        var newer = store.ReadSource(PairId, Side.Local);
        Refuses<InvalidOperationException>(() => store.FinishRepair(PairId, Summary(), generation,
            token, localCatchUp with { ThroughWorkId = later }, remoteCatchUp with { ThroughWorkId = later }));
        Check.Require(store.ReadSource(PairId, Side.Local) == newer, nameof(CompletionCases));
        store.Commit(PairId, StateChange.Empty with { Record = [baseline] });
        store.BeginSelection(remaining);
        Check.Require(store.ReadWork(remaining).Single().Before == baseline,
            "Repair retirement fixture retains a non-null preimage in the current epoch");
        store.EndSelection(remaining);
        token = store.BeginScan(PairId, generation, false);
        foreach (var side in Enum.GetValues<Side>()) await store.OpenScan(PairId, side, ScanTestSettings.BatchRows).CompleteAsync();
        var current = store.ReadSource(PairId, Side.Local)!.Value;
        members[0] = (PairId, generation, current.Epoch, current.Revision, false);
        var newest = store.AcquireJournalPage(Domain, position, position, members, [(PairId, change)])!.Value;
        store.FinishRepair(PairId, Summary(), generation, token,
            localCatchUp with { ThroughWorkId = newest }, remoteCatchUp with { ThroughWorkId = newest });
        Refuses<LiveAdmissionStaleException>(() => store.ReadWork(remaining).ToArray());
        var surviving = remaining with { AnchorWorkId = newest, ThroughWorkId = newest };
        Check.Require(store.ReadWork(surviving).Single().Notice == change,
            "completed Repair consumes current-epoch preimages only through its pre-list watermark");
        store.Commit(PairId, Raise(TextKey.FailChanged));
        current = store.ReadSource(PairId, Side.Local)!.Value;
        members[0] = (PairId, generation, current.Epoch, current.Revision, false);
        var dirtyLatest = store.AcquireJournalPage(Domain, position, position, members, [(PairId, change)])!.Value;
        token = store.BeginScan(PairId, generation, false);
        foreach (var side in Enum.GetValues<Side>()) await store.OpenScan(PairId, side, ScanTestSettings.BatchRows).CompleteAsync();
        store.FinishRepair(PairId, Summary(), generation, token,
            localCatchUp with { ThroughWorkId = dirtyLatest }, remoteCatchUp with { ThroughWorkId = dirtyLatest });
        Refuses<LiveAdmissionStaleException>(() => store.ReadWork(surviving).ToArray());
        store.EndWorkPass(PairId);
        var dirtyWork = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        store.BeginSelection(dirtyWork);
        Check.Require(dirtyWork.AnchorWorkId == dirtyLatest && store.ReadWork(dirtyWork).Count() == 1 &&
            store.ReadLocalDirty(PairId, generation, current.Epoch, Domain, LocalId, PathNames.Key(Child), null).Dirty,
            "failed Repair retains one latest dirty responsibility instead of losing or accumulating edits");
        store.Commit(PairId, StateChange.Empty with { Close = [(Child, new[] { IssueKind.Failure })] });
        token = store.BeginScan(PairId, generation, false);
        foreach (var side in Enum.GetValues<Side>()) await store.OpenScan(PairId, side, ScanTestSettings.BatchRows).CompleteAsync();
        store.FinishRepair(PairId, Summary(), generation, token,
            localCatchUp with { ThroughWorkId = dirtyLatest }, remoteCatchUp with { ThroughWorkId = dirtyLatest });
        Check.Require(store.ReadWork(dirtyWork).Count() == 1,
            "Repair preserves responsibility selected by an active live pass until its owner completes it");
        store.CompleteWork(dirtyWork, null);
        store.EndSelection(dirtyWork);
        store.EndWorkPass(PairId);
        Check.Require(store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) is null,
            "successful recovery closes the Failure and completes retained dirty responsibility");
    }

    public static void ClaimedRepair()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
        try { ClaimedRepairCases(Path.Combine(folder, "claims")).GetAwaiter().GetResult(); }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task ClaimedRepairCases(string path)
    {
        foreach (var side in Enum.GetValues<Side>())
        foreach (var identity in new[] { true, false })
        foreach (var release in new[] { "complete", "refuse", "end-pass", "retire-first", "missing-anchor" })
        {
            using var store = StateStore.Open(path + side + identity + release);
            store.SavePair(BoundPair());
            InstallDropbox(store);
            var generation = Generation(store);
            var local = store.ReadSource(PairId, Side.Local)!.Value;
            var remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
            var position = new JournalPosition(Single, 0);
            store.AcquireJournalPage(Domain, null, position,
                [(PairId, generation, local.Epoch, local.Revision, false)], []);
            async Task Finish(bool measure = false)
            {
                var token = store.BeginScan(PairId, generation, false);
                foreach (var scanSide in Enum.GetValues<Side>()) await store.OpenScan(PairId, scanSide, ScanTestSettings.BatchRows).CompleteAsync();
                var through = store.ReadWorkWatermark();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                store.FinishRepair(PairId, Summary(), generation, token,
                    new LocalCatchUp(PairId, generation, local.Epoch, new NtfsUsnCheckpoint(Single, 0, 0, 0), through),
                    new DropboxCatchUp(PairId, generation, remote.Epoch, store.ReadSource(PairId, Side.Dropbox)!.Value.Cursor!, through));
                if (measure)
                    Console.WriteLine($"claim-retirement tiny-fixture diagnostic: work=4; active=1; " +
                        $"FinishRepair-including-retirement-ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}");
            }
            await Finish();
            long Acquire(Side sourceSide, string itemPath, string? id)
            {
                var notice = new ChangeNotice(sourceSide, ChangeKind.Changed, itemPath, null, id, null, false, false);
                var source = store.ReadSource(PairId, sourceSide)!.Value;
                return sourceSide == Side.Local
                    ? store.AcquireJournalPage(Domain, position, position,
                        [(PairId, generation, source.Epoch, source.Revision, false)], [(PairId, notice)])!.Value
                    : store.AcquireDropboxPage(PairId, generation, source.Epoch, source.Revision,
                        source.Provenance!, source.Cursor, [notice], NextCursor);
            }
            var ownId = identity ? side == Side.Local ? LocalId : RemoteId : null;
            var anchor = Acquire(side, Child, ownId);
            var member = identity ? Acquire(side, Moved, ownId) : anchor;
            var otherSide = PathNames.Other(side);
            var peerId = otherSide == Side.Local ? LocalId : RemoteId;
            var peerWork = Acquire(otherSide, Child, peerId);
            var unrelated = Acquire(side, Other, side == Side.Local ? RemoteId : LocalId);
            var through = store.ReadWorkWatermark();
            var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
            var selection = store.SelectWork(PairId, generation, scope)!;
            Check.Require(selection.AnchorWorkId == anchor, "FIFO selection starts before Repair's watermark");
            var peer = new SelectionPeer(PathNames.KeyFor(otherSide, Child), through);
            var peerSelection = new WorkSelection(PairId, generation,
                store.ReadSource(PairId, otherSide)!.Value.Epoch, otherSide, otherSide == Side.Local ? Domain : Namespace,
                peerId, peer.Key, peerWork, through);
            var unrelatedSelection = selection with { AnchorWorkId = unrelated, Key = PathNames.KeyFor(side, Other),
                Identity = side == Side.Local ? RemoteId : LocalId };
            if (release != "retire-first")
            {
                store.BeginSelection(selection);
                store.ClaimSelectionPeer(selection, peer);
                if (release == "refuse")
                {
                    store.EndSelection(selection);
                    store.EndSelection(selection);
                }
                if (release == "end-pass") store.EndWorkPass(PairId);
                if (release == "missing-anchor")
                {
                    using var connection = Inspect(path + side + identity + release);
                    Execute(connection, "DELETE FROM change_work WHERE work_id=$work;", (StateSchema.ParamWork, anchor));
                }
            }
            await Finish(measure: side == Side.Local && identity && release == "complete");
            Refuses<LiveAdmissionStaleException>(() => store.ReadWork(unrelatedSelection).ToArray());
            if (release == "complete")
            {
                Check.Require(store.ReadWork(selection).Select(row => row.WorkId).SequenceEqual(
                    identity ? new[] { anchor, member } : new[] { anchor }),
                    "Repair preserves the in-flight anchor and every finite member");
                Check.Require(store.ReadWork(peerSelection).Single().WorkId == peerWork,
                    "Repair preserves the selection's opposite-side peer");
                Check.Require(store.CompleteWork(selection, peer), "the live selection settles its own retained work");
            }
            else
                Refuses<LiveAdmissionStaleException>(() => store.BeginSelection(selection));
            Refuses<LiveAdmissionStaleException>(() => store.ReadWork(selection).ToArray());
            Refuses<LiveAdmissionStaleException>(() => store.ReadWork(peerSelection).ToArray());
            store.EndWorkPass(PairId);
            Check.Require(store.SelectWork(PairId, generation, scope) is null,
                "a new selection never resurrects retired work or abandoned claims");
        }
    }

    private static Task LegacyPendingCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        using var connection = Inspect(path);
        var raw = JsonSerializer.Serialize(new { Path = Child, StagePath = Stage, Source = Entry() });
        using var pair = StorageCommand.Create(connection, null, StateSchema.PairFence,
            (StateSchema.ParamPair, PairId));
        long row;
        using (var reader = pair.ExecuteReader())
        {
            Check.Require(reader.Read(), nameof(LegacyPendingCases));
            row = reader.GetInt64(StateSchema.PairFenceId);
        }
        Execute(connection, StateSchema.PendingArm, (StateSchema.ParamPairRow, row),
            (StateSchema.ParamNext, raw));
        var legacy = (LegacyPending)store.Pending(PairId);
        Check.Require(legacy.RawJson == raw && legacy.Path == Child && legacy.StagePath == Stage,
            nameof(LegacyPendingCases));
        Refuses<InvalidOperationException>(() => store.ArmPending(PairId, Pending()));
        Refuses<ArgumentException>(() => store.SettleLegacyPending(PairId, legacy.RawToken,
            StateChange.Empty with { Record = [Saved()] }));
        Refuses<InvalidOperationException>(() => store.SettleLegacyPending(PairId, Other, StateChange.Empty));
        Check.Require(((LegacyPending)store.Pending(PairId)).RawJson == raw, nameof(LegacyPendingCases));
        store.SettleLegacyPending(PairId, legacy.RawToken, StateChange.Empty);
        Check.Require(store.Pending(PairId) is NoPending, nameof(LegacyPendingCases));
        return Task.CompletedTask;
    }

    private static Task KeepRulesCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        store.Commit(PairId, Raise(TextKey.InfoIgnoredFolderKept, Parent, Subject(Parent), IssueKind.Info));
        var notice = store.Issues(PairId).Single();
        var rules = SyncIgnorePolicy.Parse(KeepRule);
        Refuses<InvalidOperationException>(() => store.KeepFolderAlways(PairId, Generation(store),
            notice with { Subject = Subject(Other) }, rules));
        Check.Require(store.Pair(PairId)!.IgnoredFolderKeepRules is null &&
            store.IssueById(PairId, notice.Id) is not null, nameof(KeepRulesCases));
        RollbackAt(path, "UPDATE ON pair", () => store.KeepFolderAlways(PairId, Generation(store), notice, rules));
        store.KeepFolderAlways(PairId, Generation(store), notice, rules);
        var saved = store.Pair(PairId)!.IgnoredFolderKeepRules;
        Check.Require(saved is { Count: > 0 }, nameof(KeepRulesCases));
        store.KeepFolderAlways(PairId, Generation(store), notice, rules);
        Check.Require(store.Pair(PairId)!.IgnoredFolderKeepRules!.SequenceEqual(saved!), nameof(KeepRulesCases));
        return Task.CompletedTask;
    }

    private static Task ConfigurationCases(string path)
    {
        using var store = StateStore.Open(path);
        var imported = Pair() with { Enabled = false };
        foreach (var boundary in new[] { "INSERT ON pair", "INSERT ON source_state", "INSERT ON issue" })
            RollbackAt(path, boundary, () => store.SaveImportedPair(imported, true));
        store.SaveImportedPair(imported, true);
        Check.Require(store.Issues(PairId).Single().Cause == TextKey.InfoPairPausedInOldApp &&
            !store.Pair(PairId)!.Enabled, nameof(ConfigurationCases));
        Refuses<SqliteException>(() => store.SaveImportedPair(imported, true));
        store.SaveImportedPair(Pair(OtherPair) with { Enabled = false }, false);
        Check.Require(store.Issues(OtherPair).Count == 0, nameof(ConfigurationCases));
        var generation = Generation(store);
        var source = store.ReadSource(PairId, Side.Local);
        var drive = new DriveEvidence(uint.MaxValue, ulong.MaxValue, long.MaxValue);
        store.MergeDriveEvidence(PairId, generation, drive);
        using (var inspect = Inspect(path))
        {
            Execute(inspect, "CREATE TRIGGER no_drive_write BEFORE UPDATE ON pair BEGIN SELECT RAISE(ABORT,'no-op-write'); END;");
            try { store.MergeDriveEvidence(PairId, generation, drive); }
            finally { Execute(inspect, "DROP TRIGGER no_drive_write;"); }
        }
        Check.Require(store.Pair(PairId)!.Drive == drive && Generation(store) == generation &&
            store.ReadSource(PairId, Side.Local) == source, nameof(ConfigurationCases));
        Refuses<LiveAdmissionStaleException>(() => store.MergeDriveEvidence(PairId, generation + 1, drive));
        var expected = store.Pair(PairId)!;
        var rootIssue = Raise(TextKey.RootUnbound, string.Empty, new IssueSubject([], null), IssueKind.RootIdentity);
        store.ReconcileRootIdentity(PairId, generation, null, null, rootIssue);
        var issue = store.Issues(PairId).Single(item => item.Kind == IssueKind.RootIdentity);
        Refuses<LiveAdmissionStaleException>(() => store.ReconcileRootIdentity(PairId, generation, null,
            issue with { Dismissed = !issue.Dismissed }, StateChange.Empty with
                { Close = [(string.Empty, new[] { IssueKind.RootIdentity })] }));
        RollbackAt(path, "DELETE ON issue", () => store.ReconcileRootIdentity(PairId, generation, null,
            issue, StateChange.Empty with { Close = [(string.Empty, new[] { IssueKind.RootIdentity })] }));
        store.ReconcileRootIdentity(PairId, generation, null, issue, StateChange.Empty with
            { Close = [(string.Empty, new[] { IssueKind.RootIdentity })] });
        Check.Require(Generation(store) == generation && store.Pair(PairId)!.Drive == drive, nameof(ConfigurationCases));
        store.Commit(PairId, StateChange.Empty with { Record = [Saved(Parent), Saved(), Saved(Sibling)] });
        Refuses<ArgumentException>(() => store.SavePairDecision(expected, generation, expected,
            null, true, PathNames.Key(Parent)));
        Check.Require(store.SyncedByKey(PairId, Side.Local, PathNames.Key(Child)) is not null, nameof(ConfigurationCases));
        foreach (var boundary in new[] { "UPDATE ON pair", "UPDATE ON source_state" })
            RollbackAt(path, boundary, () => store.SavePairDecision(expected, generation, expected,
                null, false, PathNames.Key(Parent)));
        store.SavePairDecision(expected, generation, expected, null, false, PathNames.Key(Parent));
        Check.Require(store.SyncedByKey(PairId, Side.Local, PathNames.Key(Child)) == Saved() &&
            store.SyncedByKey(PairId, Side.Dropbox, PathNames.Key(Parent)) == Saved(Parent) &&
            store.SyncedByKey(PairId, Side.Local, PathNames.Key(Sibling)) == Saved(Sibling) &&
            store.Pair(PairId)!.Drive == drive && Generation(store) == generation + 1, nameof(ConfigurationCases));
        Refuses<LiveAdmissionStaleException>(() => store.SavePairDecision(expected, generation, expected, null, false, null));
        var current = store.Pair(PairId)!;
        store.SavePairDecision(current, Generation(store), current, null, true, null);
        Check.Require(store.SyncedByKey(PairId, Side.Local, PathNames.Key(Sibling)) is null &&
            !store.FirstRepairDone(PairId), nameof(ConfigurationCases));
        return Task.CompletedTask;
    }

    private static async Task ItemCases(string path)
    {
        foreach (var identity in new[] { null, string.Empty, LocalId })
            await ItemIdentityCase(path + Path.GetRandomFileName(), identity);
    }

    private static Task ItemIdentityCase(string path, string? identity)
    {
        var id = Guid.NewGuid();
        var subject = Subject(id: identity);
        ItemRetryToken? authority = null;
        long generation = 0;
        using (var store = StateStore.Open(path, (attempt, pair, actualGeneration, actualSubject, expected) =>
            attempt == id && pair == PairId && actualGeneration == generation &&
            actualSubject.HasSameItems(subject) && expected == authority))
        {
            store.SavePair(BoundPair());
            generation = Generation(store);
            var epoch = store.ReadSource(PairId, Side.Local)!.Value.Epoch;
            Refuses<LiveAdmissionStaleException>(() => store.RecordInUseFailure(PairId, generation, epoch,
                Child, Subject(id: Other), TextKey.FailLocalInUse, null, ItemRetryDispatch.LocalBranch, null, null, id));
            foreach (var boundary in new[] { "INSERT ON issue", "INSERT ON change_work", "INSERT ON issue_member",
                "UPDATE OF item_retry_episode ON issue" })
                RollbackAt(path, boundary, () => store.RecordInUseFailure(PairId, generation, epoch,
                    Child, subject, TextKey.FailLocalInUse, null, ItemRetryDispatch.LocalBranch, null, null, id));
            var candidate = store.RecordInUseFailure(PairId, generation, epoch, Child, subject,
                TextKey.FailLocalInUse, null, ItemRetryDispatch.LocalBranch, null, null, id);
            Check.Require(candidate.Token.Revision == 0 && store.ReadWorkWatermark() > 0 &&
                store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) is null, nameof(ItemCases));
            authority = candidate.Token;
            var loaded = store.ReadItemRetryForSubject(PairId, generation, Child, subject)!;
            Check.Require((loaded with { Token = candidate.Token }) == candidate &&
                (loaded.Token with { Subject = candidate.Token.Subject }) == candidate.Token &&
                loaded.Token.Subject.HasSameItems(subject) &&
                store.ReadItemRetryForSubject(PairId, generation, Child, Subject(id: Other)) is null &&
                store.ReadItemRetryState(PairId, [candidate.Token.IssueId, candidate.Token.IssueId]).Count == Single,
                nameof(ItemCases));
            var duplicated = subject with { Items = subject.Items.Concat(subject.Items).ToArray() };
            Check.Require(store.ReadItemRetryForSubject(PairId, generation, Child, duplicated)!.Token.IssueId == candidate.Token.IssueId,
                nameof(ItemCases));
            var conflicting = subject with { Items = subject.Items.Append(new IssueItem(Side.Dropbox, Child, RemoteId)).ToArray() };
            Refuses<LiveAdmissionStaleException>(() => store.ReadItemRetryForSubject(PairId, generation, Child, conflicting));
            Refuses<LiveAdmissionStaleException>(() => store.ReadItemRetryForSubject(PairId, generation + 1, Child, subject));
            Refuses<LiveAdmissionStaleException>(() => store.TryClaimItemRetry(candidate, Guid.NewGuid()));
            Check.Require(store.TryClaimItemRetry(candidate, id), nameof(ItemCases));
            store.DismissIssue(PairId, candidate.Token.IssueId);
            var dismissed = store.IssueById(PairId, candidate.Token.IssueId)!;
            RollbackAt(path, "UPDATE OF item_retry_revision ON issue", () =>
                store.CompleteItemAttempt(candidate, id, Raise(TextKey.FailLocalInUse, subject: subject), null, null));
            var delivered = new List<Issue>();
            store.CompleteItemAttempt(candidate, id, Raise(TextKey.FailLocalInUse, subject: subject), null, null, delivered.Add);
            Check.Require(delivered.Count == 1 && delivered[0].Id == candidate.Token.IssueId &&
                store.IssueById(PairId, delivered[0].Id)?.Cause == delivered[0].Cause,
                "Attempt completion delivers its committed continuing issue once, after durability.");
            var changed = store.ReadItemRetryState(PairId, [candidate.Token.IssueId])[candidate.Token.IssueId]!;
            Check.Require(changed.Token.Revision == candidate.Token.Revision + 1 &&
                changed.Token.WorkId == candidate.Token.WorkId && changed.Token.EpisodeId == candidate.Token.EpisodeId &&
                store.IssueById(PairId, candidate.Token.IssueId)!.Dismissed &&
                store.IssueById(PairId, candidate.Token.IssueId)!.At == dismissed.At, nameof(ItemCases));
            Check.Require(!store.TryClaimItemRetry(candidate, id), nameof(ItemCases));
            authority = changed.Token;
            store.CompleteItemAttempt(changed, id, Raise(TextKey.FailLocalInUseOrAccessDenied, subject: subject), null, null);
            var current = store.ReadItemRetryState(PairId, [candidate.Token.IssueId])[candidate.Token.IssueId]!;
            Check.Require(!store.IssueById(PairId, candidate.Token.IssueId)!.Dismissed &&
                current.Token.EpisodeId == candidate.Token.EpisodeId, nameof(ItemCases));
            authority = current.Token;
            store.CompleteItemAttempt(current, id, StateChange.Empty with
                { Close = [(Child, IssueClosure.AfterEffect)] }, null, null);
            Check.Require(store.Issues(PairId).Count == 0 &&
                store.ReadItemRetryForSubject(PairId, generation, Child, subject) is null, nameof(ItemCases));
        }
        using var withoutAuthority = StateStore.Open(path);
        Refuses<InvalidOperationException>(() => withoutAuthority.RecordInUseFailure(PairId, generation,
            withoutAuthority.ReadSource(PairId, Side.Local)!.Value.Epoch, Child, subject,
            TextKey.FailLocalInUse, null, ItemRetryDispatch.LocalBranch, null, null, id));
        return Task.CompletedTask;
    }

    private static async IAsyncEnumerable<Listed> Inventory(params Entry[] entries)
    {
        foreach (var entry in entries) yield return new Listed(entry.Path, entry);
        await Task.CompletedTask;
    }

    private static async Task<FolderPlan> CompletedPlanAsync(StateStore store, string path, RowContext? context = null)
    {
        var root = Entry(path, true);
        var row = Row.ForOneSide(Side.Dropbox, PathNames.Key(path), root, null, [], null, context ?? Context);
        var plan = store.OpenFolderPlan(PairId, row, Side.Dropbox);
        var child = Entry(path + PathNames.PathSeparator + Other);
        await plan.CaptureAsync(Inventory(child), CancellationToken.None);
        foreach (var found in plan.Children(CancellationToken.None))
            plan.MarkClassified(found.Key, found.NativeKey,
                new FolderChild(found.Entry, found.Synced, FolderVerdict.Removed), false, CancellationToken.None);
        plan.Complete(true, CancellationToken.None);
        return plan;
    }

    private static async Task<string> PrepareFolder(StateStore store, string path, RowContext? context = null)
    {
        using var plan = await CompletedPlanAsync(store, path, context);
        return store.BeginFolderRemoval(PairId, plan, StateSchema.OneRow, CancellationToken.None);
    }

    private static async Task RootContextCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var original = Context with { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice };
        var operation = await PrepareFolder(store, Parent, original);
        using var inspect = Inspect(path);
        using var read = StorageCommand.Create(inspect, null,
            "SELECT root_context_json FROM folder_removal WHERE operation_id=$operation;",
            (StateSchema.ParamOperation, operation));
        var raw = (string)read.ExecuteScalar()!;
        Check.Require(store.FolderRemoval(PairId, operation).RootContext.FirstRunDeletion == original.FirstRunDeletion,
            nameof(RootContextCases));
        var node = JsonNode.Parse(raw)!.AsObject();
        var value = node["Value"]!.AsObject();
        value.Remove(nameof(RowContext.FirstRunDeletion));
        void Set(string json) => Execute(inspect,
            "UPDATE folder_removal SET root_context_json=$next WHERE operation_id=$operation;",
            (StateSchema.ParamNext, json), (StateSchema.ParamOperation, operation));
        Set(node.ToJsonString());
        Check.Require(store.FolderRemoval(PairId, operation).RootContext.FirstRunDeletion ==
            IssueSubject.FirstRunDeletionKind.None, nameof(RootContextCases));
        foreach (var invalid in new JsonNode?[] { null, JsonValue.Create(Single), JsonValue.Create(Other) })
        {
            value[nameof(RowContext.FirstRunDeletion)] = invalid;
            Set(node.ToJsonString());
            Refuses<InvalidDataException>(() => store.FolderRemoval(PairId, operation));
        }
        value[nameof(RowContext.FirstRunDeletion)] = nameof(IssueSubject.FirstRunDeletionKind.None);
        value[Other] = JsonValue.Create(Single);
        Set(node.ToJsonString());
        Refuses<InvalidDataException>(() => store.FolderRemoval(PairId, operation));
        value.Remove(Other);
        value[nameof(RowContext.FirstRunDeletion)] = Other;
        Set(node.ToJsonString());
        Refuses<InvalidDataException>(() => store.FolderRemoval(PairId, operation));
        value[nameof(RowContext.FirstRunDeletion)] = nameof(IssueSubject.FirstRunDeletionKind.None);
        // The schema's envelope CHECK owns the version rule, so a wrong or missing version cannot be stored.
        node["Version"] = JsonValue.Create(BothSides);
        Refuses<SqliteException>(() => Set(node.ToJsonString()));
        node["Version"] = JsonValue.Create(Single);
        Refuses<SqliteException>(() => Set(value.ToJsonString()));
        var valueStart = raw.IndexOf("\"Value\":", StringComparison.Ordinal);
        Set(raw[..^1] + "," + raw[valueStart..]);
        Refuses<InvalidDataException>(() => store.FolderRemoval(PairId, operation));
        Set(raw);
        using var reopened = StateStore.OpenReadOnly(path)!;
        Check.Require(reopened.FolderRemoval(PairId, operation).RootContext == original, nameof(RootContextCases));
    }

    private static async Task FolderCases(string path)
    {
        string first;
        string second;
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            first = await PrepareFolder(store, Parent);
            second = await PrepareFolder(store, Other);
            Check.Require(store.ActiveFolderRemovals(PairId, CancellationToken.None).Count() == BothSides,
                nameof(FolderCases));
            Refuses<InvalidOperationException>(() => store.RemovePair(PairId));
        }
        using var reopened = StateStore.Open(path);
        var effects = new[] { first, second }.SelectMany(operation =>
            reopened.ReadyFolderEffects(PairId, operation, BeforeFirstOrdinal, Single)).ToArray();
        Check.Require(effects.Length == BothSides && effects.All(effect => effect.ItemOrdinal == 0 &&
            effect.Kind == FolderEffectKind.RecursiveFolder), nameof(FolderCases));
        foreach (var operation in new[] { first, second })
        {
            var rows = reopened.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, Single).ToArray();
            var next = reopened.FolderRemovalItems(PairId, operation, rows.Single().Ordinal, Single).ToArray();
            Check.Require(rows.Single().Ordinal == 0 && next.Length == Single &&
                !reopened.FolderRemovalItems(PairId, operation, next.Single().Ordinal, Single).Any(), nameof(FolderCases));
        }
        var submissionId = reopened.ArmFolderSubmission(PairId, effects);
        Check.Require(reopened.FolderSubmissionEffects(submissionId).Select(effect => effect.OperationId)
            .SequenceEqual(effects.Select(effect => effect.OperationId)), nameof(FolderCases));
        Refuses<InvalidOperationException>(() => reopened.ArmFolderSubmission(PairId, effects));
        var submission = reopened.FolderSubmission(submissionId);
        var terminal = submission with { Generation = submission.Generation + 1,
            Phase = FolderSubmission.SubmissionPhase.Unknown };
        reopened.AdvanceFolderSubmission(submission, terminal, []);
        Refuses<InvalidOperationException>(() => reopened.AdvanceFolderSubmission(submission, terminal, []));
        foreach (var operation in new[] { first, second })
        {
            var state = reopened.FolderRemoval(PairId, operation);
            var uncertain = reopened.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, Single).Single();
            var falseSuccess = uncertain with { Generation = uncertain.Generation + 1,
                EffectGeneration = uncertain.EffectGeneration + 1, EffectState = FolderRemovalItem.EffectStatus.Settled,
                Phase = FolderRemovalItem.RecoveryPhase.Settled };
            Refuses<InvalidOperationException>(() => reopened.SettleFolderRemovalItem(PairId, operation,
                uncertain.Ordinal, uncertain.Generation, falseSuccess, StateChange.Empty));
            Refuses<InvalidOperationException>(() => reopened.RetireFolderRemoval(PairId, operation,
                state.Generation, FolderRetirementDisposition.NoEffect, StateChange.Empty));
            Check.Require(reopened.FolderRemoval(PairId, operation).Active, nameof(FolderCases));
        }
        Refuses<InvalidOperationException>(() => reopened.RemovePair(PairId));
        Check.Require(reopened.FolderSubmission(submissionId) == terminal, nameof(FolderCases));
    }

    // RB-3: two provenance rows can share one key, so the exact member is read by ordinal. The row that
    // follows the planned root must come back as that exact row, never as its key-sharing predecessor.
    private static async Task FolderMemberOrdinalCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var operation = await PrepareFolder(store, Parent);
        var planned = store.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, Single).Single();
        var blocking = store.InsertFolderMember(PairId, operation, store.FolderRemoval(PairId, operation).Generation,
            FolderRemovalItem.ProvenanceKind.BlockingParent, Parent, Entry(Parent, true), null);
        Check.Require(planned.Provenance == FolderRemovalItem.ProvenanceKind.Planned && planned.Path == blocking.Path &&
            blocking.Provenance == FolderRemovalItem.ProvenanceKind.BlockingParent && blocking.Ordinal > planned.Ordinal,
            "a second provenance row is stored beside the planned member that already owns its key");
        var exact = store.FolderRemovalItems(PairId, operation, checked(blocking.Ordinal - 1), Single).Single();
        Check.Require(exact == blocking,
            "the member read by ordinal is the exact row, never the key-sharing planned member");
    }

    private static async Task CounterpartPagesCases(string path)
    {
        using var settings = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        var pageSize = SettingsParser.Parse(settings.RootElement.GetRawText(), null).StoragePageSize;
        foreach (var side in Enum.GetValues<Side>())
        foreach (var count in new[] { pageSize + Single, pageSize * BothSides + Single })
        {
            using var store = StateStore.Open(path + side + count);
            store.SavePair(Pair());
            var savedRoot = Saved(Parent) with { IsFolder = true, DropboxKey = PathNames.DropboxKey(Moved), DropboxPath = Moved };
            var otherRoot = savedRoot.PathOn(PathNames.Other(side));
            var saved = Enumerable.Range(0, count).Select(index => side == Side.Local
                ? Saved(Other + "/" + index) with { DropboxKey = PathNames.DropboxKey(otherRoot + "/" + index), DropboxPath = otherRoot + "/" + index }
                : Saved(otherRoot + "/" + index) with { DropboxKey = PathNames.DropboxKey(Other + "/" + index), DropboxPath = Other + "/" + index }).ToArray();
            if (side == Side.Local)
                saved = saved.Concat(Enumerable.Range(0, count).Select(index => Saved(otherRoot + "/Inherited/" + index))).ToArray();
            store.Commit(PairId, StateChange.Empty with { Record = saved.Prepend(savedRoot).ToArray() });
            var root = Entry(savedRoot.PathOn(side), true);
            using var plan = store.OpenFolderPlan(PairId, Row.ForOneSide(side, savedRoot.LocalKey, root, savedRoot, [], null, Context), side);
            await plan.CaptureAsync(Inventory(), default);
            foreach (var child in plan.Children(default))
                plan.MarkClassified(child.Key, child.NativeKey,
                    new FolderChild(child.Entry, child.Synced, FolderVerdict.Removed), false, default);
            plan.Complete(true, default);
            var connection = (SqliteConnection)typeof(StateStore).GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            var pages = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
            {
                var text = sql.utf8_to_string();
                if (text.Contains("ORDER BY pathkey(local_path) LIMIT") || text.Contains("ORDER BY dropbox_key_delta LIMIT")) pages++;
            }), null);
            string operation;
            try { operation = store.BeginFolderRemoval(PairId, plan, pageSize, default); }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            Check.Require(pages == ((count + pageSize - Single) / pageSize + Single) * (side == Side.Local ? BothSides : Single),
                "counterpart capture consumes full configured pages plus one terminal read per index stream");
            var members = store.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, saved.Length + Single).ToArray();
            Check.Require(members.Length == saved.Length + Single && members.Skip(Single).All(item => item.Expected is null &&
                item.EffectKind == FolderEffectKind.None && item.Saved is not null),
                "every counterpart binding is frozen exactly once without an effect");
        }
    }

    private static async Task CounterpartSiblingAnchorCases(string path)
    {
        using var settings = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        var pageSize = SettingsParser.Parse(settings.RootElement.GetRawText(), null).StoragePageSize;
        foreach (var side in Enum.GetValues<Side>())
        foreach (var count in new[] { pageSize + Single, pageSize * BothSides + Single })
        {
            using var store = StateStore.Open(path + side + count);
            store.SavePair(Pair());
            SyncedEntry Binding(string deleting, string other, bool folder = false) => side == Side.Local
                ? Saved(deleting) with { DropboxKey = PathNames.DropboxKey(other), DropboxPath = other, IsFolder = folder }
                : Saved(other) with { DropboxKey = PathNames.DropboxKey(deleting), DropboxPath = deleting, IsFolder = folder };
            var savedRoot = Binding("R", "Q", true);
            var savedBranch = Binding("R/B", "Q/C", true);
            var outside = new[] { "C", "D" }.SelectMany(parent => Enumerable.Range(0, count)
                .Select(index => Binding("U/" + parent + "/" + index, "Q/" + parent + "/" + index))).ToArray();
            store.Commit(PairId, StateChange.Empty with { Record = [savedRoot, savedBranch, .. outside] });
            var root = Entry("R", true);
            using var plan = store.OpenFolderPlan(PairId,
                Row.ForOneSide(side, savedRoot.LocalKey, root, savedRoot, [], null, Context), side);
            await plan.CaptureAsync(Inventory(Entry("R/B", true), Entry("R/Keep")), default);
            foreach (var child in plan.Children(default))
                plan.MarkClassified(child.Key, child.NativeKey, new FolderChild(child.Entry, child.Synced,
                    child.Synced is null ? FolderVerdict.Survives : FolderVerdict.Removed), false, default);
            plan.PrepareBranches(default);
            foreach (var child in plan.RemovedChildren(default)) plan.SelectBranch(child.Id, default);
            plan.Complete(false, default);
            var connection = (SqliteConnection)typeof(StateStore).GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            var savedSql = side == Side.Local ? StateSchema.SelectSyncedByDropboxKey : StateSchema.SelectSyncedByLocalKey;
            var savedPrefix = savedSql[..savedSql.IndexOf('$')];
            var savedReads = 0;
            var branchReads = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
            {
                var text = sql.utf8_to_string();
                if (text.StartsWith(savedPrefix, StringComparison.Ordinal)) savedReads++;
                if (text.Contains("AND selected_branch=1;", StringComparison.Ordinal)) branchReads++;
            }), null);
            string operation;
            try { operation = store.BeginFolderRemoval(PairId, plan, pageSize, default); }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            Check.Require(savedReads == BothSides && branchReads == Single,
                "two sibling parent groups cost two saved lookups and one selected-branch lookup at either size, across pages");
            var members = store.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, int.MaxValue).ToArray();
            foreach (var item in members.Where(item => item.Expected is null && item.Saved is not null))
            {
                var counterpart = item.Saved!.PathOn(PathNames.Other(side));
                Check.Require(item.Path == (counterpart.StartsWith("Q/C/", StringComparison.Ordinal)
                    ? "R/B/" + counterpart["Q/C/".Length..] : "R/D/" + counterpart["Q/D/".Length..]),
                    "sibling reuse preserves selected anchors and resets to the root for the next parent");
            }
        }
    }

    private static async Task FolderSettlementCases(string path)
    {
        foreach (var receipt in new[] { false, true })
        {
            var database = path + Path.GetRandomFileName();
            using var store = StateStore.Open(database);
            store.SavePair(Pair());
            var operation = await PrepareFolder(store, Parent);
            var effects = store.ReadyFolderEffects(PairId, operation, BeforeFirstOrdinal, Single).ToArray();
            var id = store.ArmFolderSubmission(PairId, effects);
            var submission = store.FolderSubmission(id);
            var terminal = submission with { Generation = submission.Generation + 1,
                Phase = FolderSubmission.SubmissionPhase.Terminal };
            FolderMemberResult result = receipt ? new FolderMemberResult.Receipt(0, effects.Single().Expected!) :
                new FolderMemberResult.KnownNoEffect(0, null);
            RollbackAt(database, "UPDATE ON folder_removal_submission", () =>
                store.AdvanceFolderSubmission(submission, terminal, [result]));
            store.AdvanceFolderSubmission(submission, terminal, [result]);
            var item = store.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, Single).Single();
            var settled = item with { Generation = item.Generation + 1, EffectGeneration = item.EffectGeneration + 1,
                EffectState = FolderRemovalItem.EffectStatus.Settled, Phase = FolderRemovalItem.RecoveryPhase.Settled };
            RollbackAt(database, "UPDATE ON folder_removal_item", () =>
                store.SettleFolderRemovalItem(PairId, operation, item.Ordinal, item.Generation, settled, StateChange.Empty));
            Check.Require(!store.DeletedNameSpent(PairId, PathNames.DropboxKey(item.Path)),
                "A rolled-back folder settlement publishes no spent fact.");
            store.SettleFolderRemovalItem(PairId, operation, item.Ordinal, item.Generation, settled, StateChange.Empty);
            Check.Require(store.DeletedNameSpent(PairId, PathNames.DropboxKey(item.Path)) ==
                (receipt && store.FolderRemoval(PairId, operation).DeletingSide == Side.Local),
                "Only a verified Local removal spends an unbound member; known no-effect does not.");
            Refuses<InvalidOperationException>(() => store.SettleFolderRemovalItem(PairId, operation,
                item.Ordinal, item.Generation, settled, StateChange.Empty));
            using var reopened = StateStore.OpenReadOnly(database)!;
            var persisted = reopened.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, Single).Single();
            Check.Require(persisted.EffectState == FolderRemovalItem.EffectStatus.Settled &&
                persisted.EffectResult == result, nameof(FolderSettlementCases));
            var unknown = "{\"Version\":1,\"Value\":{\"Kind\":\"Unknown\",\"Ordinal\":" +
                item.SubmissionMember!.Value.ToString(CultureInfo.InvariantCulture) +
                ",\"Entry\":null,\"Cause\":null}}";
            using (var inspect = Inspect(database))
                Execute(inspect, "UPDATE folder_removal_item SET effect_result_json=$next " +
                    "WHERE operation_id=$operation AND ordinal=$ordinal;",
                    (StateSchema.ParamNext, unknown), (StateSchema.ParamOperation, operation),
                    (StateSchema.ParamOrdinal, item.SubmissionMember!.Value));
            Refuses<InvalidDataException>(() =>
                store.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, Single).ToArray());
        }
    }

    private static async Task FolderBatchSettlementCases(string path)
    {
        // Two batches distinguish a committed prefix from an interrupted batch at both member counts.
        foreach (var count in new[] { BothSides, BothSides * BothSides })
        {
            var database = path + count;
            string operation;
            FolderRemovalItem[] members;
            using (var store = StateStore.Open(database))
            {
                store.SavePair(Pair());
                var root = Entry(Parent, true);
                using var plan = store.OpenFolderPlan(PairId,
                    Row.ForOneSide(Side.Local, Parent, root, null, [], null, Context), Side.Local);
                var survivor = Entry(Parent + "/" + Other);
                await plan.CaptureAsync(Inventory(Enumerable.Range(0, count * BothSides)
                    .Select(index => Entry(Parent + "/" + index) with { Revision = string.Empty })
                    .Append(survivor).ToArray()), default);
                foreach (var child in plan.Children(default))
                    plan.MarkClassified(child.Key, child.NativeKey,
                        new FolderChild(child.Entry, child.Synced, child.Entry == survivor
                            ? FolderVerdict.Survives : FolderVerdict.Removed), false, default);
                plan.PrepareBranches(default);
                foreach (var child in plan.RemovedChildren(default)) plan.SelectBranch(child.Id, default);
                plan.Complete(false, default);
                operation = store.BeginFolderRemoval(PairId, plan, count, default);
                var effects = store.ReadyFolderEffects(PairId, operation, BeforeFirstOrdinal, int.MaxValue).ToArray();
                Check.Require(effects.Length == count * BothSides, "the partial fixture has every intended effect");
                foreach (var batch in effects.Chunk(count))
                {
                    var id = store.ArmFolderSubmission(PairId, batch);
                    var submission = store.FolderSubmission(id);
                    store.AdvanceFolderSubmission(submission, submission with
                    {
                        Generation = submission.Generation + 1, Phase = FolderSubmission.SubmissionPhase.Terminal
                    }, batch.Select((effect, index) => (FolderMemberResult)new FolderMemberResult.Receipt(index,
                        effect.Expected!)).ToArray());
                }
                members = store.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, int.MaxValue)
                    .Where(item => item.EffectState == FolderRemovalItem.EffectStatus.Verified).ToArray();
                var first = members.Take(count).Select(Change).ToArray();
                var second = members.Skip(count).Select(Change).ToArray();
                var connection = (SqliteConnection)typeof(StateStore).GetField("_connection",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
                var commits = 0;
                var updates = 0;
                SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
                {
                    var text = sql.utf8_to_string();
                    if (text.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)) commits++;
                    if (text.StartsWith("UPDATE folder_removal_item", StringComparison.Ordinal)) updates++;
                }), null);
                try { Check.Require(store.SettleFolderRemovalItems(PairId, operation, first).All(value => value),
                    "all verified members settle"); }
                finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
                Check.Require(commits == Single && updates == count,
                    "settlement has one commit per batch and one member update, with no redundant recovery update");
                RollbackAt(database, "UPDATE ON folder_removal_item WHEN OLD.ordinal=" + second[^1].Next.Ordinal,
                    () => store.SettleFolderRemovalItems(PairId, operation, second));
            }
            using var reopened = StateStore.Open(database);
            var afterCrash = reopened.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, int.MaxValue).ToArray();
            Check.Require(afterCrash.Count(item => item.EffectState == FolderRemovalItem.EffectStatus.Settled) == count &&
                afterCrash.Count(item => item.EffectState == FolderRemovalItem.EffectStatus.Verified) == count,
                "restart preserves the committed first batch and all receipts of the rolled-back second batch");
            Check.Require(members.Take(count).All(item => reopened.DeletedNameSpent(PairId, PathNames.DropboxKey(item.Path))) &&
                members.Skip(count).All(item => !reopened.DeletedNameSpent(PairId, PathNames.DropboxKey(item.Path))),
                "Restart preserves spent facts only for the committed batch, not the rolled-back batch.");
            var pending = members.Skip(count).Select(Change).ToArray();
            var mixed = reopened.SettleFolderRemovalItems(PairId, operation,
                [Change(members[0]), .. pending]);
            Check.Require(mixed.Count == count + Single && !mixed[0] && mixed.Skip(Single).All(value => value),
                "restart skips a stale committed member and settles the second batch from durable verified receipts");
            Check.Require(reopened.SettleFolderRemovalItems(PairId, operation, pending).All(value => !value),
                "replaying stale settlement changes nothing");
            Check.Require(members.All(item => reopened.DeletedNameSpent(PairId, PathNames.DropboxKey(item.Path))) &&
                !reopened.DeletedNameSpent(PairId, PathNames.DropboxKey(Parent + "/" + Other)),
                "Isolated reapply spends every confirmed member, never the untouched survivor.");
            Check.Require(reopened.FolderRemoval(PairId, operation).Active,
                "member settlement never completes the enclosing root");
        }

        static (long ExpectedGeneration, FolderRemovalItem Next, StateChange Change) Change(FolderRemovalItem item) =>
            (item.Generation, item with
            {
                Generation = item.Generation + 1, EffectGeneration = item.EffectGeneration + 1,
                EffectState = FolderRemovalItem.EffectStatus.Settled, Phase = FolderRemovalItem.RecoveryPhase.Settled
            }, StateChange.Empty);
    }

    // EX-11/A134-A137: stale state refuses typed; programmer errors stay plain; nothing changes on a refusal.
    private static async Task FolderRefusalTypeCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var operation = await PrepareFolder(store, Parent);
        var state = store.FolderRemoval(PairId, operation);
        var root = store.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, Single).Single();
        static Exception? Catch(Action action)
        {
            try { action(); }
            catch (Exception error) { return error; }
            return null;
        }
        Check.Require(Catch(() => store.SettleFolderRemovalItem(PairId, operation, root.Ordinal,
                root.Generation + Single, root with { Generation = root.Generation + 1 }, StateChange.Empty))
            is FolderStateRefusedException, "A stale member generation refuses with the typed refusal.");
        Check.Require(Catch(() => store.CompleteFolderRemoval(PairId, operation, state.Generation + Single,
                StateChange.Empty, null)) is FolderStateRefusedException,
            "A stale operation generation refuses the final settlement with the typed refusal.");
        Check.Require(Catch(() => store.SettleFolderRemovalItem(PairId, operation, root.Ordinal,
                root.Generation, root, StateChange.Empty))?.GetType() == typeof(InvalidOperationException),
            "A next-shape programmer error stays a plain InvalidOperationException.");
        Check.Require(store.FolderRemoval(PairId, operation).Active &&
            store.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, Single).Single() == root,
            "A refused settlement changes nothing and keeps custody.");
    }

    // F-04/A147: a cancelled freeze rolls back the operation and every inserted member.
    private static async Task BeginFolderRemovalCancellationCase(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var root = Entry(Parent, true);
        var row = Row.ForOneSide(Side.Dropbox, PathNames.Key(Parent), root, null, [], null, Context);
        using var plan = store.OpenFolderPlan(PairId, row, Side.Dropbox);
        var child = Entry(Parent + PathNames.PathSeparator + Other);
        await plan.CaptureAsync(Inventory(child), CancellationToken.None);
        foreach (var found in plan.Children(CancellationToken.None))
            plan.MarkClassified(found.Key, found.NativeKey,
                new FolderChild(found.Entry, found.Synced, FolderVerdict.Removed), false, CancellationToken.None);
        plan.Complete(true, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Exception? caught = null;
        try { store.BeginFolderRemoval(PairId, plan, StateSchema.OneRow, cancel.Token); }
        catch (Exception error) { caught = error; }
        Check.Require(caught is OperationCanceledException, "A cancelled freeze stops before committing the operation.");
        using var inspect = Inspect(path);
        using var rows = StorageCommand.Create(inspect, null,
            "SELECT (SELECT COUNT(*) FROM folder_removal) + (SELECT COUNT(*) FROM folder_removal_item);");
        Check.Require(Convert.ToInt64(rows.ExecuteScalar(), CultureInfo.InvariantCulture) == 0 &&
            !store.ActiveFolderRemovals(PairId, CancellationToken.None).Any(),
            "A cancelled freeze leaves no operation row, member row or effect.");
    }

    // F-04/A147 (BD-4): a real enumeration boundary cancels after the root and one child were inserted.
    private static async Task BeginFolderRemovalMidCancellationCase(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        var plan = await CompletedPlanAsync(store, Parent);
        using var cancel = new CancellationTokenSource();
        using var decorated = new CancellingFolderPlan(plan, cancel, after: Single);
        Exception? caught = null;
        try { store.BeginFolderRemoval(PairId, decorated, StateSchema.OneRow, cancel.Token); }
        catch (Exception error) { caught = error; }
        Check.Require(caught is OperationCanceledException && decorated.EntryYielded,
            "A freeze cancelled after a child yields stops instead of committing partial rows.");
        using var inspect = Inspect(path);
        using var rows = StorageCommand.Create(inspect, null,
            "SELECT (SELECT COUNT(*) FROM folder_removal) + (SELECT COUNT(*) FROM folder_removal_item);");
        Check.Require(Convert.ToInt64(rows.ExecuteScalar(), CultureInfo.InvariantCulture) == 0 &&
            !store.ActiveFolderRemovals(PairId, CancellationToken.None).Any(),
            "The root and already-inserted child rows roll back with no effect.");
    }

    private static async Task InvalidJobCases(string path)
    {
        foreach (var protectedFirst in new[] { false, true })
        foreach (var inUse in new[] { false, true })
        {
            var database = path + Path.GetRandomFileName();
            var attempt = Guid.NewGuid();
            ItemRetryToken? authority = null;
            long generation = 0;
            var protectedSubject = Subject(Parent, kind: inUse ? IssueSubject.FirstRunDeletionKind.None :
                IssueSubject.FirstRunDeletionKind.Item);
            using var store = StateStore.Open(database, (id, pair, actualGeneration, subject, token) =>
                id == attempt && pair == PairId && actualGeneration == generation &&
                subject.HasSameItems(protectedSubject) && token == authority);
            store.SavePair(BoundPair());
            generation = Generation(store);
            var operation = await PrepareFolder(store, Parent);
            var id = store.ArmFolderSubmission(PairId,
                store.ReadyFolderEffects(PairId, operation, BeforeFirstOrdinal, Single).ToArray());
            var submission = store.FolderSubmission(id);
            var accepted = submission with { Generation = submission.Generation + 1,
                Phase = FolderSubmission.SubmissionPhase.Accepted, JobId = Revision };
            store.AdvanceFolderSubmission(submission, accepted, []);
            Check.Require(store.HasFolderJobProgress(PairId, default) &&
                !store.HasFolderJobProgress("missing-pair", default), "progress existence respects pair identity");
            using (var inspect = Inspect(database))
            using (var explain = StorageCommand.Create(inspect, null, "EXPLAIN QUERY PLAN " + StateSchema.HasFolderJobProgress,
                StateSchema.ParamPair, PairId))
            using (var rows = explain.ExecuteReader())
            {
                var plan = new List<string>();
                while (rows.Read()) plan.Add(rows.GetString(3));
                Check.Require(plan.Any(line => line.Contains("folder_submission_pending", StringComparison.Ordinal)) &&
                    !plan.Any(line => line.StartsWith("SCAN s", StringComparison.Ordinal)),
                    "progress existence uses the accepted-submission index without statistics");
            }
            ItemRetryCandidate? candidate = null;
            void Protect()
            {
                if (!inUse) store.Commit(PairId, Raise(TextKey.FailChanged, Parent, protectedSubject));
                else
                {
                    candidate = store.RecordInUseFailure(PairId, generation,
                        store.ReadSource(PairId, Side.Local)!.Value.Epoch, Parent, protectedSubject,
                        TextKey.FailLocalInUse, null, ItemRetryDispatch.LocalBranch, null, null, attempt);
                    authority = candidate.Token;
                }
            }
            if (protectedFirst) Protect();
            store.RecordFolderJobObservation(accepted, null, TextKey.FailDropboxDeleteJobInvalid);
            try
            {
                store.RecordFolderJobObservation(accepted, new FolderJobResult.InProgress(), null);
                throw new Exception("stale folder observation was accepted");
            }
            catch (InvalidOperationException error)
            {
                Check.Require(error.GetType().Name == "FolderStateRefusedException",
                    "stale folder observations have a specific refusal type");
            }
            if (!protectedFirst) Protect();
            Check.Require(store.HasInvalidFolderJob(PairId, Parent) && !store.HasFolderJobProgress(PairId, default) &&
                !store.PendingFolderSubmissions(PairId, default, progressOnly: true).Any(), nameof(InvalidJobCases));
            // A projection raised while closing another notice carries its own first-seen time, not that notice's.
            if (protectedFirst && !inUse)
                using (var inspect = Inspect(database))
                    Execute(inspect, "UPDATE issue SET at_ticks=$at WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND path=$path;",
                        (StateSchema.ParamAt, 1L), (StateSchema.ParamPair, PairId), (StateSchema.ParamPath, Parent));
            var protectedIssue = store.Issues(PairId).Single(issue => issue.Path == Parent);
            Check.Require(protectedIssue.Subject.FirstRunDeletion == protectedSubject.FirstRunDeletion &&
                protectedIssue.Cause == (inUse ? TextKey.FailLocalInUse : TextKey.FailChanged), nameof(InvalidJobCases));
            var acknowledgment = StateChange.Empty with { Close = [(Parent, IssueClosure.AfterEffect)] };
            if (inUse) store.CompleteItemAttempt(candidate!, attempt, acknowledgment, null, null);
            else store.Commit(PairId, acknowledgment with { FirstRunAcknowledged = Subject(Parent).Items });
            Check.Require(store.Issues(PairId).Single(issue => issue.Path == Parent).Cause ==
                TextKey.FailDropboxDeleteJobInvalid, nameof(InvalidJobCases));
            if (protectedFirst && !inUse)
                Check.Require(store.Issues(PairId).Single(issue => issue.Path == Parent).At.UtcTicks > 1,
                    "an invalid-job projection is stamped when raised");
            var current = store.FolderSubmission(id);
            store.RecordFolderJobObservation(current, new FolderJobResult.Terminal(
                [new FolderMemberResult.KnownNoEffect(0, null)]), null);
            Check.Require(!store.HasInvalidFolderJob(PairId, Parent), nameof(InvalidJobCases));
        }
    }

    private static void SeedLegacy(string path, params SyncedEntry[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Inspect(path);
        Execute(connection, StateSchema.Create);
        Execute(connection, StateSchema.PublishVersion.Replace(
            StateSchema.Version.ToString(CultureInfo.InvariantCulture),
            StateSchema.UpgradeFromVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Execute(connection, StateSchema.UpsertPair, (StateSchema.ParamPair, PairId),
            (StateSchema.ParamDef, JsonSerializer.Serialize(Pair())));
        Execute(connection, StateSchema.UpsertLimiterLevel, (StateSchema.ParamConnection, Account),
            (StateSchema.ParamLevel, Single));
        Execute(connection, StateSchema.InsertIssue, (StateSchema.ParamPair, PairId),
            (StateSchema.ParamPath, Other), (StateSchema.ParamKind, (int)IssueKind.Info),
            (StateSchema.ParamCause, nameof(TextKey.FailChanged)),
            (StateSchema.ParamSubject, JsonSerializer.Serialize(new IssueSubject([], null))),
            (StateSchema.ParamAt, DateTimeOffset.UnixEpoch.UtcTicks));
        foreach (var row in rows)
            Execute(connection, LegacyInsertSynced,
                (StateSchema.ParamPair, PairId), (StateSchema.ParamLocalPath, row.LocalPath),
                (StateSchema.ParamLocalKey, row.LocalKey), (StateSchema.ParamDropboxKey, row.DropboxKey),
                (StateSchema.ParamDropboxPath, row.DropboxPath), (StateSchema.ParamIsFolder, row.IsFolder),
                (StateSchema.ParamLocalId, row.LocalId), (StateSchema.ParamLocalSize, row.LocalSize),
                (StateSchema.ParamLocalMtime, row.LocalModifiedTicks), (StateSchema.ParamDropboxId, row.DropboxId),
                (StateSchema.ParamDropboxHash, row.DropboxContentHash));
    }

    // The saved-row shape versions before 11 wrote, without the publication boundary columns.
    internal const string LegacyInsertSynced = "INSERT INTO synced_entry(pair_id, local_path, dropbox_key_delta, " +
        "dropbox_path_delta, is_folder, local_id, local_size, local_mtime, dropbox_id, dropbox_hash) " +
        "VALUES((SELECT id FROM pair WHERE pair_id = $p), $localPath, nullif($dropboxKey, $localKey), " +
        "nullif($dropboxPath, $localPath), $isFolder, $localId, $localSize, $localMtime, $dropboxId, $dropboxHash);";

    private static async Task UpgradeCases(string path)
    {
        var mapped = Saved(MappedName) with { DropboxPath = InvalidName };
        SeedLegacy(path, mapped, Saved(Sibling));
        using (var store = StateStore.Open(path))
        {
            var expected = mapped with { DropboxKey = PathNames.Key(InvalidName) };
            Check.Require(store.SyncedByKey(PairId, Side.Local, mapped.LocalKey) == expected &&
                store.SyncedById(PairId, Side.Dropbox, mapped.DropboxId) == expected &&
                store.SyncedByKey(PairId, Side.Dropbox, mapped.DropboxKey) is null &&
                store.SyncedByKey(PairId, Side.Local, PathNames.Key(Sibling)) == Saved(Sibling), nameof(UpgradeCases));
            store.BeginScan(PairId, Generation(store), false);
            await store.OpenScan(PairId, Side.Local, ScanTestSettings.BatchRows).CompleteAsync();
            var remote = store.OpenScan(PairId, Side.Dropbox, ScanTestSettings.BatchRows);
            var observed = Entry(ValidName) with { Id = mapped.DropboxId };
            remote.AddAsync(new Listed(ValidName, observed), default).GetAwaiter().GetResult();
            await remote.CompleteAsync();
            var rename = store.ScanRenames(PairId).Single();
            Check.Require(rename.Side == Side.Dropbox && rename.Moved == observed && rename.Synced == expected &&
                store.Issues(PairId).Single().Path == Other && store.LimiterLevel(Account) == Single,
                nameof(UpgradeCases));
        }
        using (var inspect = Inspect(path)) Check.Require(Version(inspect) == StateSchema.Version, nameof(UpgradeCases));
        foreach (var collision in new[]
        {
            new[] { mapped, Saved(InvalidName) },
            new[] { mapped, Saved(Other) with { DropboxKey = AlternateKey, DropboxPath = InvalidName } }
        })
        {
            var collisionPath = path + Path.GetRandomFileName();
            SeedLegacy(collisionPath, collision);
            string beforeSchema;
            string beforeDefinition;
            string beforeRows;
            using (var before = Inspect(collisionPath))
            {
                beforeSchema = Snapshot(before, StateSchema.SchemaObjects);
                beforeDefinition = Definition(before);
                beforeRows = DatabaseImage(before);
            }
            Refuses<InvalidDataException>(() => { using var refused = StateStore.Open(collisionPath); });
            using var inspect = Inspect(collisionPath);
            Check.Require(Version(inspect) == StateSchema.UpgradeFromVersion &&
                Snapshot(inspect, StateSchema.SchemaObjects) == beforeSchema &&
                Definition(inspect) == beforeDefinition && DatabaseImage(inspect) == beforeRows, nameof(UpgradeCases));
            using var legacy = StateStore.OpenReadOnly(collisionPath)!;
            foreach (var row in collision)
                Check.Require(legacy.SyncedByKey(PairId, Side.Local, row.LocalKey) == row, nameof(UpgradeCases));
        }
        var newerPath = path + Path.GetRandomFileName();
        SeedLegacy(newerPath);
        using (var inspect = Inspect(newerPath)) Execute(inspect, StateSchema.PublishVersion.Replace(
            StateSchema.Version.ToString(CultureInfo.InvariantCulture),
            (StateSchema.Version + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var newer = File.ReadAllBytes(newerPath);
        foreach (var readOnly in new[] { false, true })
        {
            Refuses<InvalidDataException>(() =>
            { using var refused = readOnly ? StateStore.OpenReadOnly(newerPath) : StateStore.Open(newerPath); });
            Check.Require(File.ReadAllBytes(newerPath).SequenceEqual(newer), nameof(UpgradeCases));
        }
    }

    private static async Task PhaseIndexUpgradeCases(string path)
    {
        string operation;
        FolderRemovalItem[] members;
        using (var store = StateStore.Open(path))
        {
            store.SavePair(Pair());
            store.Commit(PairId, StateChange.Empty with { Record = [Saved()] });
            operation = await PrepareFolder(store, Parent);
            members = store.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, int.MaxValue).ToArray();
        }
        string beforeRows;
        string beforeSchema;
        string expectedRows;
        using (var inspect = Inspect(path))
        {
            Execute(inspect, "UPDATE source_state SET repair_revision=repair_revision+1;");
            expectedRows = DatabaseImage(inspect);
            Execute(inspect, "UPDATE source_state SET repair_revision=repair_revision-1;");
            DowngradeIssueFixture(inspect);
            Execute(inspect, "DROP TRIGGER invalidate_deleted_history;DROP TABLE discovery_deleted_name;ALTER TABLE pair DROP COLUMN deleted_history_cursor;");
            Execute(inspect, StateSchema.CreateObsoletePhaseIndex);
            Execute(inspect, "PRAGMA user_version=" + StateSchema.PhaseIndexVersion + ";");
            beforeRows = DatabaseImage(inspect);
            beforeSchema = Snapshot(inspect, StateSchema.SchemaObjects);
        }
        RefuseOldRead(path);
        using (var inspect = Inspect(path))
            Check.Require(Version(inspect) == StateSchema.PhaseIndexVersion &&
                Snapshot(inspect, StateSchema.SchemaObjects) == beforeSchema && DatabaseImage(inspect) == beforeRows,
                "read-only opening never upgrades the old version or removes its index");
        using (var upgraded = StateStore.Open(path))
            Check.Require(upgraded.SyncedByKey(PairId, Side.Local, Saved().LocalKey) == Saved() &&
                upgraded.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, int.MaxValue).SequenceEqual(members),
                "migration preserves mappings and pending receipts exactly");
        using (var inspect = Inspect(path))
        {
            Check.Require(Version(inspect) == StateSchema.Version && DatabaseImage(inspect) == expectedRows,
                "migration preserves rows and schedules the required history acquisition with one source revision advance");
            Check.Require(!Snapshot(inspect, StateSchema.SchemaObjects).Contains("folder_item_phase", StringComparison.Ordinal),
                "writable opening removes the old write-only phase index");
        }
        using var reopened = StateStore.OpenReadOnly(path)!;
        Check.Require(reopened.FolderRemovalItems(PairId, operation, BeforeFirstOrdinal, int.MaxValue)
            .SequenceEqual(members), "current schema reopens read-only without an obsolete index");
    }

    private static Task CodecCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair());
        using var connection = Inspect(path);
        foreach (var identity in new[]
        {
            new DirectoryIdentity(0, 0, null), new DirectoryIdentity(uint.MaxValue, ulong.MaxValue, null),
            new DirectoryIdentity(ulong.MaxValue, ulong.MaxValue, 0),
            new DirectoryIdentity(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)
        })
        {
            var roots = new RootBinding(LocalRoot, RemoteRoot, Account, identity,
                new DropboxRootIdentity(Account, Namespace, Namespace, RemoteId));
            var pair = Pair() with { Roots = roots };
            store.SavePair(pair);
            Check.Require(store.Pair(PairId)!.Roots == roots, nameof(CodecCases));
            var valid = JsonNode.Parse(Definition(connection))!.AsObject();
            foreach (var property in new[] { nameof(DirectoryIdentity.VolumeSerialNumber),
                nameof(DirectoryIdentity.FileId), nameof(DirectoryIdentity.FileIdHigh) })
            {
                var invalid = valid.DeepClone().AsObject();
                invalid[nameof(PairConfig.Roots)]![nameof(RootBinding.Local)]!.AsObject().Remove(property);
                Execute(connection, StateSchema.UpsertPair, (StateSchema.ParamPair, PairId),
                    (StateSchema.ParamDef, invalid.ToJsonString()));
                Refuses<InvalidDataException>(() => store.Pair(PairId));
            }
            Execute(connection, StateSchema.UpsertPair, (StateSchema.ParamPair, PairId),
                (StateSchema.ParamDef, valid.ToJsonString()));
        }
        var invalidBasic = Pair() with { Roots = new RootBinding(LocalRoot, RemoteRoot, Account,
            new DirectoryIdentity((ulong)uint.MaxValue + 1, 0, null),
            new DropboxRootIdentity(Account, Namespace, Namespace, RemoteId)) };
        Execute(connection, StateSchema.UpsertPair, (StateSchema.ParamPair, PairId),
            (StateSchema.ParamDef, JsonSerializer.Serialize(invalidBasic)));
        Refuses<InvalidDataException>(() => store.Pair(PairId));
        var raw = JsonNode.Parse(JsonSerializer.Serialize(Pair()))!.AsObject();
        raw.Remove(nameof(PairConfig.Roots));
        Execute(connection, StateSchema.UpsertPair, (StateSchema.ParamPair, PairId),
            (StateSchema.ParamDef, raw.ToJsonString()));
        Check.Require(store.Pair(PairId)!.Roots is null && store.Pair(PairId)!.IgnoredFolderKeepRules is null,
            nameof(CodecCases));
        store.SavePair(Pair() with { IgnoredFolderKeepRules = [KeepRule] });
        Check.Require(store.Pair(PairId)!.IgnoredFolderKeepRules!.SequenceEqual(new[] { KeepRule }), nameof(CodecCases));
        store.SavePair(store.Pair(PairId)! with { Enabled = false });
        Check.Require(store.Pair(PairId)!.IgnoredFolderKeepRules!.SequenceEqual(new[] { KeepRule }), nameof(CodecCases));
        store.SavePair(store.Pair(PairId)! with { IgnoredFolderKeepRules = [] });
        Check.Require(!JsonNode.Parse(Definition(connection))!.AsObject().ContainsKey(nameof(PairConfig.IgnoredFolderKeepRules)),
            nameof(CodecCases));
        return Task.CompletedTask;
    }
}
