using System.IO;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class BoundedPresentationHistoryChecks
{
    internal static async Task RunAsync(string root,bool successorOnly=false)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("installation-history-window", "profile-history-window", "history-window-fixture", "{}");
        await db.RegisterPairAsync("pair-history", 1,
            new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs",
                RecursiveMode: true, AutomaticPruning: false, RecoveryRetentionDays: 30));
        var assertions = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException("History window: " + label);
            assertions++;
            Console.WriteLine("PASS " + label);
        }
        async Task Refuses(Func<Task> action, string label)
        {
            var refused = false;
            try { await action(); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or SqliteException)
            { refused = true; }
            Check(refused, label);
        }
        async Task<long> Number(string sql)
        {
            await using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        async Task Execute(string sql)
        {
            await using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        async Task<SyncAuthorityCommandAdmission> Admit() => await db.CreateAcceptedCommandAdmissionAsync(
            SyncAuthorityCommandReservation.Mint(), "run_pair", "pair-history", null, null,
            "{\"pairIds\":[\"pair-history\"]}", SyncAuthorityCommandPurpose.NormalEffect);
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var pair = new SyncPair("pair-history", "History", localRoot,
            localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId, "/History", "id:history",
            ConnectionId: "connection-history");
        var context = SyncEndpointContext.Capture(pair);
        var instant = DateTimeOffset.UtcNow;
        SyncActivityEvent Event(string command, string message) => new("", instant,
            [SyncActivityCategories.Sync], "Success", "File", "Copied", message,
            PairId: "pair-history", RunId: command, RelativePath: "file.txt", EndpointContext: context);
        var admission = await Admit();
        var writer = await db.OpenRunActivityWriterAsync(admission);
        Check(ReferenceEquals(writer, await db.OpenRunActivityWriterAsync(admission)),
            "one current writer is retained for the exact accepted command");
        await Refuses(() => db.OpenRunActivityWriterAsync(admission with { AdmissionSequence = admission.AdmissionSequence + 1 }),
            "a forged admission cannot open history");
        Check(!(await db.LoadActivityRetentionWindowAsync()).Enabled, "fresh and migrated history expiry defaults off");
        Check(await Number("PRAGMA auto_vacuum;") == 2, "fresh databases select incremental auto-vacuum before their schema");

        var first = writer.Enqueue(Event(admission.CommandRequestId, "first exact outcome"));
        db.ActivityAppendBoundaryForTest = (stage, _) => stage == "after"
            ? Task.FromException(new IOException("fixture: committed acknowledgement lost")) : Task.CompletedTask;
        try { await db.AppendRunActivityAsync([first]); }
        catch (IOException) { }
        db.ActivityAppendBoundaryForTest = null;
        Check((await db.AppendRunActivityAsync([first])).Single().Disposition == SyncActivityAppendDisposition.AlreadyCommitted,
            "an ambiguously acknowledged append replays the exact committed ordinal without insertion");
        await Refuses(() => db.AppendRunActivityAsync([new SyncPendingActivityEvent(writer, first.Ordinal,
            first.Event with { Message = "changed same identity" })]), "a retained ordinal rejects different logical content");
        await Refuses(() => db.AppendActivityEventAsync(first.Event with { EventId = "supplied-new-identity" }),
            "a caller supplied arbitrary event identity cannot create history");
        await db.AppendActivityEventAsync(first.Event);
        Check(await Number("SELECT count(*) FROM history_event WHERE retention_class='ordinary';") == 1,
            "compatibility replay retains exactly the original event");
        await Refuses(() => db.AppendDiagnosticActivityAsync(first.Event), "diagnostics cannot borrow a run or caller event identity");
        await Refuses(() => db.AppendRunActivityAsync([new SyncPendingActivityEvent(writer, first.Ordinal + 2,
            first.Event with { EventId = SyncRunActivityWriter.EventIdentity(admission, first.Ordinal + 2) })]),
            "a gap refuses without advancing the committed ordinal");
        var second = writer.Enqueue(Event(admission.CommandRequestId, "second exact outcome"));
        await Refuses(() => db.AppendRunActivityAsync([new SyncPendingActivityEvent(writer, second.Ordinal,
            second.Event with { PairId = "foreign-pair", EndpointContext = null })]), "a run writer cannot enroll a foreign pair");
        var scopeRejected = false;
        try
        {
            await db.AppendRunActivityAsync([new SyncPendingActivityEvent(writer, second.Ordinal,
                second.Event with { PairId = "foreign-pair", OperationId = "unretained-operation", EndpointContext = null })]);
        }
        catch (InvalidDataException) { scopeRejected = true; }
        Check(scopeRejected, "an operation-attributed foreign pair reaches the real scope query and is rejected");
        await db.AppendRunActivityAsync([second]);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "completed", "{}");
        var terminal = writer.Enqueue(Event(admission.CommandRequestId, "final drained outcome"));
        await db.AppendRunActivityAsync([terminal]);
        Check(await Number("SELECT next_activity_ordinal FROM command_request WHERE activity_opened=1;") == 4,
            "terminal command status still permits its exact final history drain");
        if(successorOnly)
        {
            await db.AppendRunActivityAsync([
                writer.Enqueue(Event(admission.CommandRequestId,"progress chatter") with {Progress=new(1,10,"files",1)}),
                writer.Enqueue(Event(admission.CommandRequestId,"expired old outcome") with {OccurredUtc=instant.AddYears(-2)})]);
        }
        await db.CloseRunActivityWriterAsync(writer);
        await Refuses(() => db.AppendRunActivityAsync([new SyncPendingActivityEvent(writer, 4,
            terminal.Event with { EventId = SyncRunActivityWriter.EventIdentity(admission, 4) })]),
            "a closed writer cannot insert a new ordinal");

        if(successorOnly)
        {
            var selectedIds=new[]{first.Event.EventId,second.Event.EventId,terminal.Event.EventId}.Select(x=>"history:"+x).ToHashSet(StringComparer.Ordinal);
            var expected=(await db.LoadActivityProjectionPageAsync(query:new(DetailedLog:true,OldestFirst:true))).Where(x=>selectedIds.Contains(x.EventId!)).ToArray();
            Check(expected.Length==3,"real source reader returns three exact completed outcomes");
            var originalHighwater=await db.LoadActivitySequenceAsync();
            await Execute("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
            var sourceHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var options=new SyncEssentialSuccessorOptions(root);
            var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
            var targetPath=Path.Combine(root,"successor.db");
            SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=phase=>
            {if(phase=="after-table:history_event")throw new InvalidOperationException("fixture exact history interruption");};
            try
            {
                var interrupted=false;
                try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,targetPath,identity,options);}
                catch(InvalidOperationException error) when(error.Message=="fixture exact history interruption"){interrupted=true;}
                Check(interrupted,"interruption retains exact history rows with incomplete receipt");
            }
            finally{SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=null;}
            async Task<string> Text(string sql)
            {
                await using var c=new SqliteConnection("Data Source="+targetPath+";Mode=ReadOnly;Pooling=False");await c.OpenAsync();
                await using var q=c.CreateCommand();q.CommandText=sql;return Convert.ToString(await q.ExecuteScalarAsync())!;
            }
            var fixedTime=await Text("SELECT json_extract(essential_successor_json,'$.HistoryAsOfUtc') FROM installation;");
            var result=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,targetPath,identity,options);
            await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,targetPath,identity,options);
            Check(result.Complete&&fixedTime==await Text("SELECT json_extract(essential_successor_json,'$.HistoryAsOfUtc') FROM installation;"),
                "resumed import uses the original fixed history selection time");
            var history=result.Tables.Single(x=>x.Table=="history_event");var contexts=result.Tables.Single(x=>x.Table=="activity_context");
            Check(history.SelectedRows==3&&history.OriginalOnlyRows>0&&history.SelectedPayloadBytes>0&&
                history.SourcePayloadBytes==history.SelectedPayloadBytes+history.OriginalOnlyPayloadBytes,
                "expired/progress rows remain source-only with exact completed-event payload accounting");
            Check(contexts.SelectedRows==1&&contexts.SelectedPayloadBytes>0,
                "three retained outcomes share one exact detached historical context");
            Check(await Text("SELECT count(*) FROM command_request;")=="0",
                "completed display history does not import old command authority");
            var target=new SyncAuthorityDatabase(targetPath);
            Check(JsonSerializer.Serialize(await target.LoadActivityProjectionPageAsync(query:new(DetailedLog:true,OldestFirst:true)))==JsonSerializer.Serialize(expected),
                "real successor reader preserves exact event order, text, sequence and captured endpoints");
            var window=await target.LoadActivityRetentionWindowAsync();
            Check(window.RetainedEvents==3&&window.EncodedBytes>0&&await target.LoadActivitySequenceAsync()==originalHighwater,
                "shared-context budget and non-reused source sequence highwater remain exact");
            await Refuses(()=>target.OpenRunActivityWriterAsync(admission),"old completed producer cannot append successor events");
            Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))==sourceHash,
                "original history database remains byte-exact");
            return;
        }
        db.ActivityRetentionPolicyForTest = new(MaximumEvents: 1);
        await db.EnableActivityRetentionForTestAsync();
        var safetyBefore = await Number("SELECT count(*) FROM history_event WHERE retention_class='safety';");
        Check(safetyBefore > 0, "real command transitions retain independently held journal evidence");
        var before = await db.LoadActivityRetentionWindowAsync();
        await Refuses(() => Execute("DELETE FROM history_event WHERE retention_class='ordinary';"),
            "a direct SQL connection cannot retire an ordinary event");
        await Refuses(() => Execute("DELETE FROM history_event WHERE retention_class='safety';"),
            "a direct SQL connection cannot retire held safety evidence");
        db.ActivityRetirementBoundaryForTest = stage =>
        {
            if (stage == "before-retirement-commit") throw new IOException("fixture: retirement rollback");
        };
        try { await db.RetireOrdinaryActivityBatchAsync(); }
        catch (IOException) { }
        db.ActivityRetirementBoundaryForTest = null;
        Check((await db.LoadActivityRetentionWindowAsync()) == before,
            "a failed retirement transaction preserves rows, counters and frontier");
        var batches = new List<SyncActivityRetirementResult>();
        while ((await db.LoadActivityRetentionWindowAsync()).RetainedEvents > 1)
        {
            var batch = await db.RetireOrdinaryActivityBatchAsync();
            Check(batch.DeletedEvents > 0 && batch.DeletedEvents <= 200 && batch.DeletedPayloadBytes <= 1_048_576,
                "retirement makes bounded progress under the row and encoded-byte limits");
            batches.Add(batch);
        }
        Check((await db.AppendRunActivityAsync([first])).Single().Disposition == SyncActivityAppendDisposition.AlreadyCommittedExpired,
            "replaying an expired committed ordinal returns expired and does not resurrect it");
        Check(await Number("SELECT count(*) FROM history_event WHERE retention_class='safety';") == safetyBefore,
            "presentation expiry never removes command/effect evidence");
        await Refuses(() => Execute("DELETE FROM history_event WHERE retention_class='ordinary';"),
            "retirement capability is cleared after its transaction");

        var beforeSequence = await db.LoadActivitySequenceAsync();
        await db.AppendDiagnosticActivityAsync(new SyncActivityEvent("", instant.AddDays(-8),
            [SyncActivityCategories.Diagnostics], "Warning", "Application", "Old diagnostic", "fixture"));
        var highest = await db.LoadActivitySequenceAsync();
        await db.RetireOrdinaryActivityBatchAsync();
        var reopened = new SyncAuthorityDatabase(path);
        await reopened.InitializeAsync("installation-history-window", "profile-history-window", "history-window-fixture", "{}");
        await reopened.AppendDiagnosticActivityAsync(new SyncActivityEvent("", instant,
            [SyncActivityCategories.Diagnostics], "Information", "Application", "Fresh diagnostic", "fixture"));
        Check(highest > beforeSequence && await reopened.LoadActivitySequenceAsync() > highest,
            "a deleted maximum sequence is never reused after reopening");
        Check((await reopened.LoadActivityRetentionWindowAsync()).ExpiredEvents >= 3,
            "the public window discloses age/count expiry across reopen");

        // Every append is its own real transaction; only parsed SQLite connections are reused.
        db.ActivityRetentionPolicyForTest = new(MaximumEvents: 25);
        var scaleAdmission = await Admit();
        var scaleWriter = await db.OpenRunActivityWriterAsync(scaleAdmission);
        for (var group = 0; group < 20; group++)
        {
            await using (db.BeginConnectionReuseScope())
                for (var index = 0; index < 100; index++)
                    await db.AppendRunActivityAsync([scaleWriter.Enqueue(Event(scaleAdmission.CommandRequestId,
                        "terminal outcome " + (group * 100 + index)))]);
            while ((await db.LoadActivityRetentionWindowAsync()).RetainedEvents > 25)
            {
                var batch = await db.RetireOrdinaryActivityBatchAsync();
                if (batch.DeletedEvents == 0) throw new InvalidDataException("The bounded fixture cannot make retirement progress.");
                batches.Add(batch);
            }
        }
        var plateau = await db.LoadActivityRetentionWindowAsync();
        Check(plateau.RetainedEvents == 25 && plateau.ExpiredEvents >= 1975,
            "2000 separate committed appends settle to the configured retained event window");
        Check(batches.All(batch => batch.DeletedEvents <= 200 && batch.DeletedPayloadBytes <= 1_048_576),
            "every measured retirement transaction respects both hard batch budgets");
        Check(plateau.EncodedBytes == await Number("SELECT coalesce(sum(encoded_bytes),0) FROM history_event WHERE retention_class IN ('ordinary','diagnostic');") +
                await Number("SELECT coalesce(sum(encoded_bytes),0) FROM activity_context WHERE ordinary_references>0;"),
            "incremental encoded counters equal independent surviving-row recomputation");
        Check(await Number("""
            SELECT count(*) FROM history_event WHERE retention_class IN ('ordinary','diagnostic')
            AND encoded_bytes <> length(CAST(detail_json AS BLOB))
                + length(CAST(history_event_id AS BLOB)) + length(CAST(occurred_utc AS BLOB))
                + length(CAST(expires_utc AS BLOB)) + length(CAST(event_kind AS BLOB))
                + length(CAST(retention_class AS BLOB)) + length(CAST(producer_kind AS BLOB))
                + coalesce(length(CAST(activity_context_key AS BLOB)),0)
                + coalesce(length(CAST(producer_command_id AS BLOB)),0)
                + 18 + CASE WHEN producer_ordinal IS NULL THEN 0 ELSE 9 END;
            """) == 0, "each event budgets every actual UTF-8 scalar and its integer allowance");
        Check(await Number("SELECT count(*) FROM activity_context WHERE ordinary_references>0;") == 1 &&
                await Number("SELECT expired_context_count FROM installation;") == 0,
            "retirement shares one live snapshot and retains unreferenced context for its seven-day grace");
        await reopened.CloseInterruptedActivityWritersAsync(default);
        Check((await db.LoadActivityRetentionWindowAsync(runId: scaleAdmission.CommandRequestId)).IncompleteRunHistory,
            "startup marks an undrained prior writer incomplete and retains a fresh diagnostic");
        db.ActivityRetentionPolicyForTest = new(MaximumEncodedBytes: 3_000, MaximumDiagnosticBytes: 900);
        for (var index = 0; index < 4; index++)
            await db.AppendDiagnosticActivityAsync(new SyncActivityEvent("", instant,
                [SyncActivityCategories.Diagnostics], "Information", "Application", "Bytes", "Unicode: \u03bb \u20ac \u65e5\u672c\u8a9e " + index));
        for (var pass = 0; pass < 50; pass++)
        {
            var batch = await db.RetireOrdinaryActivityBatchAsync();
            batches.Add(batch);
            if (batch.DeletedEvents == 0) break;
        }
        var byteWindow = await db.LoadActivityRetentionWindowAsync();
        Check(byteWindow.EncodedBytes <= 3_000 && byteWindow.DiagnosticEncodedBytes <= 900,
            "independent total and diagnostic byte limits retire only actual overage");
        Check(byteWindow.EncodedBytes == await Number("SELECT coalesce(sum(encoded_bytes),0) FROM history_event WHERE retention_class IN ('ordinary','diagnostic');") +
                await Number("SELECT coalesce(sum(encoded_bytes),0) FROM activity_context WHERE ordinary_references>0;"),
            "byte expiry keeps exact counters including shared context lifetime");
        db.ActivityRetentionPolicyForTest = new(MaximumEvents: 7);
        var racingAdmission = await Admit();
        var racingWriter = await db.OpenRunActivityWriterAsync(racingAdmission);
        var racingEvents = new List<SyncPendingActivityEvent>();
        var rendezvous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = Task.Run(async () =>
        {
            await rendezvous.Task;
            for (var index = 0; index < 50; index++)
            {
                var pending = racingWriter.Enqueue(Event(racingAdmission.CommandRequestId, "race " + index));
                racingEvents.Add(pending);
                await db.AppendRunActivityAsync([pending]);
            }
        });
        var maintenance = Task.Run(async () =>
        {
            await rendezvous.Task;
            for (var index = 0; index < 50; index++)
            { await db.RetireOrdinaryActivityBatchAsync(); await Task.Yield(); }
        });
        rendezvous.SetResult();
        await Task.WhenAll(producer, maintenance);
        while ((await db.LoadActivityRetentionWindowAsync()).RetainedEvents > 7)
            await db.RetireOrdinaryActivityBatchAsync();
        Check(await Number("SELECT next_activity_ordinal FROM command_request WHERE command_request_id='" +
                racingAdmission.CommandRequestId + "';") == 51,
            "concurrent real append and retirement preserve every committed ordinal");
        var beforeRaceReplay = await db.LoadActivityRetentionWindowAsync();
        var replay = await db.AppendRunActivityAsync(racingEvents);
        Check(replay.Count == 50 && replay.All(item => item.Disposition != SyncActivityAppendDisposition.Committed) &&
                await db.LoadActivityRetentionWindowAsync() == beforeRaceReplay,
            "replaying the raced stream inserts nothing, including its retired prefix");
        db.ActivityRetentionPolicyForTest = new(MaximumEvents: 1);
        var beforeCancellation = await db.LoadActivityRetentionWindowAsync();
        using (var cancelled = new CancellationTokenSource())
        {
            db.ActivityRetirementBoundaryForTest = stage =>
            { if (stage == "exact-row-bound-before-delete") cancelled.Cancel(); };
            var stopped = false;
            try { await db.RetireOrdinaryActivityBatchAsync(cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            finally { db.ActivityRetirementBoundaryForTest = null; }
            Check(stopped && await db.LoadActivityRetentionWindowAsync() == beforeCancellation,
                "cancellation after exact-row authorization rolls back rows, counters and frontier");
        }
        await Refuses(() => reopened.AppendRunActivityAsync([racingEvents[0]]),
            "another database instance cannot borrow a run writer");
        await db.CloseRunActivityWriterAsync(racingWriter);
        await Refuses(() => Execute("DELETE FROM command_request WHERE command_request_id='" +
                racingAdmission.CommandRequestId + "';"), "ordinary SQL cannot remove a closed run identity");
        // Force a missing identity only in this isolated stale-callback fixture.
        // RunSummaryLifetimeChecks exercises real guarded run collection separately.
        await using (var missing = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            await missing.OpenAsync();
            missing.CreateFunction<string, long>("syncshark_run_retirement_allowed",
                id => id == racingAdmission.CommandRequestId ? 1L : 0L);
            await using var erase = missing.CreateCommand();
            erase.CommandText = "DELETE FROM command_request WHERE command_request_id=$id;";
            erase.Parameters.AddWithValue("$id", racingAdmission.CommandRequestId);
            await erase.ExecuteNonQueryAsync();
        }
        await Refuses(() => db.AppendRunActivityAsync([racingEvents[0]]),
            "a removed command cannot revive its former history writer");
        await db.VerifyHealthAsync();
        await CheckVersion12MigrationAsync(root, Check);
        await File.WriteAllTextAsync(Path.Combine(root, "bounded-presentation-history-checks.json"),
            JsonSerializer.Serialize(new { assertions, batches, window = await db.LoadActivityRetentionWindowAsync(),
                scope = "isolated database; no original, installed or provider state" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS bounded presentation history: {assertions} assertions; real committed appends, expiry and replay.");
    }
    private static async Task CheckVersion12MigrationAsync(string root, Action<bool, string> check)
    {
        var path = Path.Combine(root, "version12.db");
        await using (var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            await connection.OpenAsync();
            foreach (var sql in new[] { SyncAuthoritySchema.Sql, SyncAuthoritySchemaSupplement.Sql,
                SyncAuthoritySchemaMigration3.Sql, SyncAuthoritySchemaMigration4.Sql,
                SyncAuthoritySchemaMigration5.Sql, SyncAuthoritySchemaMigration6.Sql,
                SyncAuthoritySchemaMigration7.Sql, SyncAuthoritySchemaMigration8.Sql,
                SyncAuthoritySchemaMigration9.Sql, SyncAuthoritySchemaMigration10.Sql,
                SyncAuthoritySchemaMigration11.Sql, SyncAuthoritySchemaMigration12.Sql,
                """
                INSERT INTO installation(installation_id,database_generation,created_utc)
                  VALUES('installation-history-v12','generation-history-v12','2026-01-01T00:00:00.0000000Z');
                INSERT INTO history_event(rowid,history_event_id,event_kind,detail_json,occurred_utc)
                  VALUES(42,'retained-legacy-history','presentation','{}','2026-01-01T00:00:00.0000000Z');
                INSERT INTO schema_migration(schema_version,applied_utc,source_revision)
                  VALUES(12,'2026-01-01T00:00:00.0000000Z','predecessor-history-v12');
                PRAGMA user_version=12;
                """ })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
        }
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("installation-history-v12", "history-v12", "history-v13", "{}");
        check(await db.LoadActivitySequenceAsync() == 42,
            "version 12 migration initializes the highwater from the retained maximum");
        await db.AppendDiagnosticActivityAsync(new SyncActivityEvent("", DateTimeOffset.UtcNow,
            [SyncActivityCategories.Diagnostics], "Information", "Application", "Migrated", "fixture"));
        check(await db.LoadActivitySequenceAsync() == 43,
            "first migrated insertion uses the next exact sequence");
        var window = await db.LoadActivityRetentionWindowAsync();
        check(window.LegacyHeld && !window.Enabled,
            "migration holds predecessor history and leaves expiry disabled");
        await using var rollback = new SqliteConnection("Data Source=" + db.Version12RollbackDatabasePath + ";Mode=ReadOnly;Pooling=False");
        await rollback.OpenAsync();
        await using var query = rollback.CreateCommand();
        query.CommandText = "PRAGMA user_version;";
        check(Convert.ToInt64(await query.ExecuteScalarAsync()) == 12,
            "migration retains a readable exact predecessor-version rollback");
        await db.VerifyHealthAsync();
    }

}
