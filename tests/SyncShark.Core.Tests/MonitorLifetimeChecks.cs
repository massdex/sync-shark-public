using System.Text.RegularExpressions;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class MonitorLifetimeChecks
{
    internal static async Task RemoveMigration17ForPredecessorFixtureAsync(string path)
    {
        await EssentialSuccessorChecks.RemoveMigration18ForPredecessorFixtureAsync(path);
        await using var c=new SqliteConnection("Data Source="+path+";Pooling=False");
        await c.OpenAsync();
        async Task Sql(string sql){await using var q=c.CreateCommand();q.CommandText=sql;await q.ExecuteNonQueryAsync();}
        // Isolated predecessor construction only. Production never reverses ALTER TABLE.
        foreach(Match m in Regex.Matches(SyncAuthoritySchemaMigration17.Sql,
                    @"CREATE\s+(?:UNIQUE\s+)?(TRIGGER|INDEX)\s+IF\s+NOT\s+EXISTS\s+(\w+)",RegexOptions.IgnoreCase))
            await Sql("DROP "+m.Groups[1].Value+" "+m.Groups[2].Value+";");
        foreach(Match m in Regex.Matches(SyncAuthoritySchemaMigration17.Sql,
                    @"ALTER TABLE (\w+) ADD COLUMN (\w+)",RegexOptions.IgnoreCase))
            await Sql("ALTER TABLE "+m.Groups[1].Value+" DROP COLUMN "+m.Groups[2].Value+";");
        await Sql("DELETE FROM schema_migration WHERE schema_version=17;"+
            "INSERT OR IGNORE INTO schema_migration(schema_version,applied_utc,source_revision) VALUES(16,'2026-01-01T00:00:00Z','monitor-predecessor'); PRAGMA user_version=16;");
    }

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var db=new SyncAuthorityDatabase(Path.Combine(root,"authority.db"));
        await db.InitializeAsync("monitor-installation","monitor-profile","monitor-source","{}");
        await db.RegisterPairAsync("pair",1);
        await db.RegisterRootBindingAsync("root","pair","local",1,
            """{"volumeSerial":"00000001","fileId":"fixture-root"}""",root);
        await db.RegisterMonitorCursorAsync("cursor","pair","root","periodic",1,"{}","c0");
        var now=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero);
        db.ActivityRetentionClockForTest=()=>now;
        Task<SyncAuthorityMonitorWriter> Begin()=>db.BeginMonitorWriterAsync("cursor","pair","root","periodic",1,"{}");
        SyncAuthorityMonitorEventInput Event(string id,string path,string json="{}")=>new("event-"+id,"key-"+id,path,"changed",json);
        async Task<long> Number(string sql)
        {
            await using var c=new SqliteConnection("Data Source="+db.DatabasePath+";Pooling=False");
            await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;
            return Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        async Task Reject(Func<Task> action,string detail)
        {
            try{await action();}
            catch(InvalidOperationException){Console.WriteLine("PASS "+detail);return;}
            throw new InvalidDataException("Expected monitor refusal: "+detail);
        }
        void Check(bool value,string detail){if(!value)throw new InvalidDataException(detail);Console.WriteLine("PASS "+detail);}
        var writer=await Begin();
        var first=db.PrepareMonitorBatch(writer,[Event("one","one.txt","""{"b":2,"a":1}""")],"c0","c1");
        await db.CommitMonitorBatchAsync(writer,first);
        await db.CommitMonitorBatchAsync(writer,first);
        Check(await Number("SELECT event_count FROM dirty_item WHERE normalized_path_key='one.txt';")==1&&
            await Number("SELECT count(*) FROM monitor_event;")==1,"exact current monitor batch replay is idempotent");
        var bad=db.PrepareMonitorBatch(writer,[Event("one","one.txt","""{"a":3}""")],"c1","bad");
        await Reject(()=>db.CommitMonitorBatchAsync(writer,bad),"changed retained event bytes refuse atomically");
        Check((await db.LoadMonitorCursorAsync("cursor"))!.CursorValue=="c1","rejected duplicate never advances the cursor");
        var replacement=await Begin();
        await Reject(()=>db.CommitMonitorBatchAsync(writer,first),"replacement owner fences predecessor replay");
        await Reject(()=>db.SetMonitorCursorStatusAsync(writer,"unavailable"),"old monitor cannot change replacement health");
        writer=replacement;
        var pending=db.PrepareMonitorBatch(writer,[Event("two","two.txt")],"c1","c2");
        db.MonitorCommittedBeforeAcknowledgementForTest=()=>throw new IOException("fixture lost acknowledgement");
        try{await db.CommitMonitorBatchAsync(writer,pending);throw new InvalidDataException("Fault did not fire.");}
        catch(IOException){}
        db.MonitorCommittedBeforeAcknowledgementForTest=null;
        Check(ReferenceEquals(writer.Pending,pending),"ambiguous acknowledgement retains the immutable in-flight ticket");
        await db.CommitMonitorBatchAsync(writer,pending);
        Check(await Number("SELECT event_count FROM dirty_item WHERE normalized_path_key='two.txt';")==1&&writer.Pending is null,
            "lost acknowledgement resolves exact batch without repeating dirty counts");
        var changed=new SyncAuthorityMonitorBatch(writer,pending.Sequence,pending.PriorCursor,pending.NextCursor,
            pending.Events,new string('f',64));
        await Reject(()=>db.CommitMonitorBatchAsync(writer,changed),"same committed sequence with changed digest refuses");
        var gap=new SyncAuthorityMonitorBatch(writer,pending.Sequence+2,"c2","gap",[],new string('e',64));
        await Reject(()=>db.CommitMonitorBatchAsync(writer,gap),"monitor sequence gap refuses");
        var watermark=await db.CaptureMonitorWatermarkAsync("pair");
        var later=db.PrepareMonitorBatch(writer,[Event("three","two.txt")],"c2","c3");
        await db.CommitMonitorBatchAsync(writer,later);
        await db.CloseMonitorHintsThroughAsync("pair",watermark);
        Check(await Number("SELECT count(*) FROM dirty_item WHERE normalized_path_key='two.txt' AND status='dirty';")==1&&
            await Number("SELECT count(*) FROM monitor_event WHERE source_event_key='key-three' AND consumed_utc IS NULL;")==1,
            "captured sequence preserves a later hint at exactly the same timestamp");
        var empty=db.PrepareMonitorBatch(writer,[],"c3","c4");
        await db.CommitMonitorBatchAsync(writer,empty);
        Check((await db.LoadMonitorCursorAsync("cursor"))!.CursorValue=="c4"&&
            await Number("SELECT count(*) FROM monitor_event;")==3,"empty source advancement commits without fabricated hints");
        await db.EnableActivityRetentionForTestAsync();
        now=now.AddDays(8);
        for(var i=0;i<20&&await Number("SELECT count(*) FROM monitor_event;")>0;i++)
            await db.RetireOrdinaryActivityBatchAsync();
        Check(await Number("SELECT count(*) FROM monitor_event;")==0&&
            await Number("SELECT count(*) FROM dirty_item WHERE normalized_path_key='two.txt' AND status='dirty';")==1,
            "aged monitor diagnostics retire while the exact dirty obligation remains");
        var projection=(await db.LoadMonitorProjectionAsync()).Single();
        Check(projection.PendingDirtyItems==1,
            "pending monitor projection follows coalesced dirty state after event eviction");
        var attention=new SyncCliAttentionData([], [new(projection.PairId,projection.Kind,projection.State,
            projection.UpdatedUtc,projection.PendingDirtyItems)],0);
        var human=SyncCli.RenderHuman(attention);
        var json=System.Text.Json.JsonSerializer.Serialize(attention,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Check(human.Contains("pending-dirty-items=1",StringComparison.Ordinal)&&
            !human.Contains("pending-events",StringComparison.Ordinal)&&
            json.Contains("\"pendingDirtyItems\":1",StringComparison.Ordinal)&&
            !json.Contains("pendingEvents",StringComparison.Ordinal),
            "actual retained dirty projection renders as coalesced dirty items in human and JSON CLI output");
        await Reject(()=>db.CommitMonitorBatchAsync(writer,first),"retired earlier batch cannot revive its old sequence");
        var repeated=db.PrepareMonitorBatch(writer,[Event("one","one.txt","""{"a":1,"b":2}""")],"c4","c5");
        await db.CommitMonitorBatchAsync(writer,repeated);
        Check(await Number("SELECT count(*) FROM monitor_event WHERE source_event_key='key-one';")==1&&
            await Number("SELECT count(*) FROM dirty_item WHERE normalized_path_key='one.txt' AND status='dirty';")==1,
            "an evicted source hint in a new valid batch conservatively dirties the path");
        var counts=(await db.LoadActivityRetentionWindowAsync()).Monitors!;
        Check(counts.RetainedEvents==1&&counts.RetainedBytes>0&&counts.ExpiredEvents==3,
            "monitor maintained charges match retained and expired diagnostics");
        await db.CloseMonitorHintsThroughAsync("pair",await db.CaptureMonitorWatermarkAsync("pair"));
        now=now.AddDays(8);
        for(var i=0;i<25&&await Number("SELECT count(*) FROM dirty_item;")>0;i++)
            await db.RetireOrdinaryActivityBatchAsync();
        Check(await Number("SELECT count(*) FROM dirty_item;")==0&&await Number("SELECT count(*) FROM monitor_event;")==0,
            "closed unreferenced dirty markers expire after their own seven-day grace");

        await db.VerifyHealthAsync();
        await RemoveMigration17ForPredecessorFixtureAsync(db.DatabasePath);
        var migrated=new SyncAuthorityDatabase(db.DatabasePath);
        await migrated.InitializeAsync("monitor-installation","monitor-profile","monitor-source","{}");
        Check(await Number("PRAGMA user_version;")==SyncAuthoritySchema.Version&&File.Exists(migrated.Version16RollbackDatabasePath)&&
            (await migrated.LoadMonitorCursorAsync("cursor"))!.CursorValue=="c5",
            "real version-16 migration preserves source cursor and verified rollback");
        await using var old=new SqliteConnection("Data Source="+migrated.Version16RollbackDatabasePath+";Mode=ReadOnly;Pooling=False");
        await old.OpenAsync();await using var inspect=old.CreateCommand();inspect.CommandText="PRAGMA user_version;";
        Check(Convert.ToInt64(await inspect.ExecuteScalarAsync())==16,"monitor rollback remains exact schema16");
    }

    internal static async Task RunBudgetAsync(string root)
    {
        Directory.CreateDirectory(root);
        var db=new SyncAuthorityDatabase(Path.Combine(root,"authority.db"));
        await db.InitializeAsync("monitor-budget","profile","source","{}");
        await db.RegisterPairAsync("pair",1);
        await db.RegisterRootBindingAsync("root","pair","local",1,"{}",root);
        await db.RegisterMonitorCursorAsync("cursor","pair","root","periodic",1,"{}","0");
        var writer=await db.BeginMonitorWriterAsync("cursor","pair","root","periodic",1,"{}");
        var now=new DateTimeOffset(2026,2,1,0,0,0,TimeSpan.Zero);
        db.ActivityRetentionClockForTest=()=>now;
        await db.EnableActivityRetentionForTestAsync();
        async Task<long> Number(string sql)
        {
            await using var c=new SqliteConnection("Data Source="+db.DatabasePath+";Pooling=False");
            await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;
            return Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        async Task Sql(string sql)
        {
            await using var c=new SqliteConnection("Data Source="+db.DatabasePath+";Pooling=False");
            await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;await q.ExecuteNonQueryAsync();
        }
        void Check(bool ok,string detail){if(!ok)throw new InvalidDataException(detail);Console.WriteLine("PASS "+detail);}
        long sent=0;
        for(var batch=0;batch<12;batch++)
        {
            var items=Enumerable.Range(0,1000).Select(index=>
            {
                var id=(batch*1000+index).ToString();
                return new SyncAuthorityMonitorEventInput("event-"+id,"key-"+id,"shared.txt","changed","{}");
            }).ToArray();
            var next=(batch+1).ToString();
            await db.CommitMonitorBatchAsync(writer,db.PrepareMonitorBatch(writer,items,writer.Cursor,next));
            sent+=items.Length;
        }
        Check(await Number("SELECT count(*) FROM monitor_event;")==12000,"actual monitor burst exceeds ten-thousand diagnostic rows");
        var passes=0;var largestRows=0;long largestBytes=0;
        while((await db.LoadActivityRetentionWindowAsync()).Monitors!.RetainedEvents>10000&&passes++<400)
        {
            var result=await db.RetireOrdinaryActivityBatchAsync();
            largestRows=Math.Max(largestRows,result.Monitors?.ExaminedRows??0);
            largestBytes=Math.Max(largestBytes,result.Monitors?.RetiredBytes??0);
        }
        Check(await Number("SELECT count(*) FROM monitor_event;")<=10000&&
            await Number("SELECT event_count FROM dirty_item WHERE normalized_path_key='shared.txt';")==sent,
            "diagnostic count plateaus at ten thousand while all twelve-thousand dirty hints remain counted");
        Check(await Number("SELECT length(last_batch_digest) FROM monitor_cursor;")==64&&
            await Number("SELECT committed_batch_sequence FROM monitor_cursor;")==12,
            "constant-size cursor replay proof remains independent of retained event count");

        var json="{\"payload\":\""+new string('x',750000)+"\"}";
        var bytesBatch=Enumerable.Range(0,14).Select(index=>new SyncAuthorityMonitorEventInput(
            "large-"+index,"large-key-"+index,"shared.txt","changed",json)).ToArray();
        await db.CommitMonitorBatchAsync(writer,db.PrepareMonitorBatch(writer,bytesBatch,writer.Cursor,"large"));
        sent+=bytesBatch.Length;
        Check((await db.LoadActivityRetentionWindowAsync()).Monitors!.RetainedBytes>8*1024*1024,
            "actual encoded diagnostics exceed the eight-MiB byte window without rejecting ingestion");
        passes=0;
        while((await db.LoadActivityRetentionWindowAsync()).Monitors!.RetainedBytes>8*1024*1024&&passes++<500)
        {
            var result=await db.RetireOrdinaryActivityBatchAsync();
            largestRows=Math.Max(largestRows,result.Monitors?.ExaminedRows??0);
            largestBytes=Math.Max(largestBytes,result.Monitors?.RetiredBytes??0);
        }
        var state=(await db.LoadActivityRetentionWindowAsync()).Monitors!;
        Check(state.RetainedBytes<=8*1024*1024&&
            await Number("SELECT event_count FROM dirty_item WHERE normalized_path_key='shared.txt';")==sent,
            "diagnostic bytes plateau under eight MiB with lossless current dirty counts");
        Check(largestRows<=200&&largestBytes<=1048576&&
            state.RetainedBytes==await Number("SELECT coalesce(sum(encoded_bytes),0) FROM monitor_event WHERE lifetime_class='eligible';"),
            "every monitor maintenance batch respects shared row/byte limits and exact maintained totals");

        // Deliberately retained predecessor rows: one has exact dirty coverage,
        // the other lacks it. No provider or original database is involved.
        await Sql("""
            INSERT INTO monitor_event(monitor_event_id,monitor_cursor_id,pair_id,source_event_key,normalized_path_key,
                event_kind,event_json,received_utc)
            VALUES('legacy-covered','cursor','pair','legacy-covered','legacy.txt','changed','{}','2026-01-01T00:00:00.0000000+00:00'),
                  ('legacy-held','cursor','pair','legacy-held','unknown.txt','changed','{}','2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO dirty_item(dirty_item_id,pair_id,side,normalized_path_key,reconciliation_level,
                earliest_event_utc,latest_event_utc,event_count,status)
            VALUES('legacy-dirty','pair','both','legacy.txt','item','2026-01-01T00:00:00.0000000+00:00',
                '2026-01-01T00:00:00.0000000+00:00',1,'dirty');
            """);
        writer=await db.BeginMonitorWriterAsync("cursor","pair","root","periodic",1,"{}");
        for(var i=0;i<20&&await Number("SELECT count(*) FROM monitor_event WHERE monitor_event_id='legacy-covered';")!=0;i++)
            await db.RetireOrdinaryActivityBatchAsync();
        Check(await Number("SELECT count(*) FROM monitor_event WHERE monitor_event_id='legacy-covered';")==0&&
            await Number("SELECT count(*) FROM dirty_item WHERE dirty_item_id='legacy-dirty' AND event_count=1 AND status='dirty';")==1,
            "fenced legacy diagnostic retires only with exact durable dirty coverage");
        Check(await Number("SELECT count(*) FROM monitor_event WHERE monitor_event_id='legacy-held' AND lifetime_class='legacy';")==1&&
            (await db.LoadActivityRetentionWindowAsync()).Monitors!.LegacyHeld,
            "legacy diagnostic without coverage remains explicitly held");
        try{await Sql("DELETE FROM monitor_event WHERE monitor_event_id='legacy-held';");throw new InvalidDataException("Unguarded delete succeeded.");}
        catch(SqliteException){}
        Check(await Number("SELECT count(*) FROM monitor_event WHERE monitor_event_id='legacy-held';")==1,
            "raw SQL cannot bypass the connection-local monitor retirement permit");
        await db.VerifyHealthAsync();
    }

}
