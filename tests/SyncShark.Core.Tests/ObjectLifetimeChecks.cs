using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class ObjectLifetimeChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,"authority.db");
        var db=new SyncAuthorityDatabase(path);
        await db.InitializeAsync("object-life-installation","object-life-profile","object-life-fixture","{}");
        await db.RegisterPairAsync("pair",1);
        await db.RegisterRootBindingAsync("local-root","pair","local",1,
            """{"volumeSerial":"00000001","fileId":"root"}""",root);
        await db.RegisterObjectAsync("object","file");
        await db.RegisterObjectVersionExactAsync("version-001","object","local",
            """{"volumeSerial":"00000001","fileId":"file","revision":"old"}""",new string('a',64),4);
        await db.RegisterObjectVersionExactAsync("version-002","object","local",
            """{"volumeSerial":"00000001","fileId":"file","revision":"current"}""",new string('b',64),5);
        await db.RegisterObjectVersionExactAsync("remote-version","object","dropbox",
            """{"fileId":"id:file","revision":"remote"}""",new string('c',64),5);
        await db.RegisterMembershipAsync("member-001","object","local-root","file.txt",1);
        await db.RegisterMembershipAsync("member-002","object","local-root","file.txt",2);
        await db.RegisterObjectAsync("observed","file");
        await db.RegisterObjectVersionExactAsync("observed-version","observed","local",
            """{"volumeSerial":"00000001","fileId":"observed"}""",new string('d',64),6);
        var scan=await db.BeginScanGenerationAsync("pair","local-root","local","manual","{}");
        await db.AppendObservationAsync(scan,"observed-row","observed-version",null,"{}");
        await db.CompleteScanGenerationAsync(scan);
        var now=DateTimeOffset.UtcNow;var start=now;db.ActivityRetentionClockForTest=()=>now;
        await db.EnableActivityRetentionForTestAsync();
        void Check(bool condition,string text)
        {if(!condition)throw new InvalidDataException(text);Console.WriteLine("PASS "+text);}
        async Task<long> Number(string sql)
        {
            await using var c=new SqliteConnection("Data Source="+path+";Mode=ReadOnly;Pooling=False");
            await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;
            return Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        async Task Change(string sql)
        {
            await using var c=new SqliteConnection("Data Source="+path+";Pooling=False");
            await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="PRAGMA foreign_keys=ON;"+sql;
            await q.ExecuteNonQueryAsync();
        }
        async Task Maintain()
        {
            for(var pass=0;pass<12;pass++)
            {
                var result=await db.RetireOrdinaryActivityBatchAsync();
                if(result.DeletedEvents+(result.Contexts?.ExaminedRows??0)+(result.Objects?.ExaminedRows??0)>200 ||
                    result.DeletedPayloadBytes+(result.Contexts?.RetiredBytes??0)+(result.Objects?.RetiredBytes??0)>1_048_576)
                    throw new InvalidDataException("Combined lifetime budget exceeded.");
            }
        }
        // Compare against actual fresh schema FK metadata; an omitted production consumer fails this proof.
        await using(var c=new SqliteConnection("Data Source="+path+";Mode=ReadOnly;Pooling=False"))
        {
            await c.OpenAsync();var tables=new List<string>();
            await using(var q=c.CreateCommand())
            {
                q.CommandText="SELECT name FROM sqlite_schema WHERE type='table';";
                await using var rows=await q.ExecuteReaderAsync();
                while(await rows.ReadAsync())tables.Add(rows.GetString(0));
            }
            foreach(var target in SyncObjectLifetimeSchema.Targets)
            {
                var actual=new HashSet<(string,string)>();
                foreach(var table in tables)
                {
                    await using var q=c.CreateCommand();q.CommandText="PRAGMA foreign_key_list("+table+");";
                    await using var rows=await q.ExecuteReaderAsync();
                    while(await rows.ReadAsync())if(rows.GetString(2)==target.Table)
                        actual.Add((table,rows.GetString(3)));
                }
                Check(actual.SetEquals(target.References),"zero-reference proof covers every actual "+target.Table+" foreign-key consumer");
            }
        }
        await Maintain();
        Check(await Number("SELECT count(*) FROM object_version WHERE object_version_id='version-001' AND unreferenced_since_utc IS NOT NULL;")==1 &&
            await Number("SELECT count(*) FROM membership WHERE membership_id='member-001' AND unreferenced_since_utc IS NOT NULL;")==1,
            "obsolete unreferenced version and older path start seven-day grace");
        Check(await Number("SELECT count(*) FROM object_version WHERE object_version_id IN ('version-002','observed-version') AND unreferenced_since_utc IS NULL;")==2 &&
            await Number("SELECT count(*) FROM membership WHERE membership_id='member-002' AND unreferenced_since_utc IS NULL;")==1,
            "current version, complete observation and highest path generation remain pinned");
        var refused=false;
        try{await Change("DELETE FROM object_version WHERE object_version_id='version-001';");}
        catch(SqliteException){refused=true;}
        Check(refused,"ordinary SQL cannot collect an unreferenced version");
        now=start.AddDays(6);
        await db.RecordConflictAsync("brief-conflict","pair","object","fixture reference",
            "version-001","member-001","remote-version",null);
        await Change("UPDATE conflict SET status='resolved',resolved_utc='2026-10-13T00:00:00.0000000Z' WHERE conflict_id='brief-conflict';"+
            "DELETE FROM conflict_version WHERE conflict_id='brief-conflict';");
        await Maintain();now=start.AddDays(8);await Maintain();
        Check(await Number("SELECT count(*) FROM object_version WHERE object_version_id='version-001';")==1 &&
            await Number("SELECT count(*) FROM membership WHERE membership_id='member-001';")==1,
            "a reference acquired and released between passes restarts the full grace");
        now=start.AddDays(14);
        var before=(await db.LoadActivityRetentionWindowAsync()).Objects;
        db.ActivityRetirementBoundaryForTest=phase=>
        {if(phase=="exact-object-before-delete")throw new IOException("fixture interruption");};
        var stopped=false;
        for(var pass=0;pass<4&&!stopped;pass++)
            try{await db.RetireOrdinaryActivityBatchAsync();}catch(IOException){stopped=true;}
        db.ActivityRetirementBoundaryForTest=null;
        Check(stopped && await Number("SELECT count(*) FROM object_version WHERE object_version_id='version-001';")==1 &&
            await Number("SELECT count(*) FROM membership WHERE membership_id='member-001';")==1 &&
            (await db.LoadActivityRetentionWindowAsync()).Objects==before,
            "interrupted object collection rolls back exact rows and counters");
        db=new SyncAuthorityDatabase(path);
        await db.InitializeAsync("object-life-installation","object-life-profile","object-life-fixture","{}");
        db.ActivityRetentionClockForTest=()=>now;await Maintain();
        // Removing the older membership can conservatively restart the object's version grace.
        now=start.AddDays(22);await Maintain();
        Check(await Number("SELECT count(*) FROM object_version WHERE object_version_id='version-001';")==0 &&
            await Number("SELECT count(*) FROM membership WHERE membership_id='member-001';")==0 &&
            await db.NextMembershipGenerationAsync("local-root","file.txt")==3,
            "restart collects aged obsolete details without resetting path generation");
        Check(await Number("SELECT count(*) FROM object_version WHERE object_version_id IN ('version-002','observed-version');")==2 &&
            await Number("SELECT count(*) FROM pragma_foreign_key_check;")==0,
            "current and observed versions survive with exact foreign-key integrity");
        var status=(await db.LoadActivityRetentionWindowAsync()).Objects!;
        Check(status.VersionBytes==await Number("SELECT coalesce(sum(lifetime_encoded_bytes),0) FROM object_version WHERE unreferenced_since_utc IS NOT NULL;") &&
            status.MembershipBytes==await Number("SELECT coalesce(sum(lifetime_encoded_bytes),0) FROM membership WHERE unreferenced_since_utc IS NOT NULL;") &&
            status.ExpiredRows>=2,"object lifetime counters equal independent retained-row accounting");
    }
}
