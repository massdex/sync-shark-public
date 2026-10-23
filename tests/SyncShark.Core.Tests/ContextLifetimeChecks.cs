using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class ContextLifetimeChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,"authority.db");
        var db=new SyncAuthorityDatabase(path);
        await db.InitializeAsync("context-installation","context-profile","context-lifetime-fixture","{}");
        await db.RegisterPairAsync("context-pair",1,new SyncAuthorityPairConfiguration(
            "disabled",0,1,"manual_preserve_both","content_times_basic_attrs",
            RecursiveMode:true,AutomaticPruning:false,RecoveryRetentionDays:30));
        var local=Path.Combine(root,"local");Directory.CreateDirectory(local);
        using var lease=PilotDirectoryLease.Open(local);
        var pair=new SyncPair("context-pair","Context",local,lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId,"/Context","id:context",ConnectionId:"context-connection");
        var context=SyncEndpointContext.Capture(pair);
        var start=DateTimeOffset.UtcNow;var now=start;
        db.ActivityRetentionClockForTest=()=>now;
        await db.EnableActivityRetentionForTestAsync();
        SyncActivityEvent Event()=>new("",now,[SyncActivityCategories.Diagnostics],"Information",
            "Application","Context","Exact saved context",PairId:pair.Id,EndpointContext:context);
        async Task<long> Number(string sql)
        {
            await using var c=new SqliteConnection("Data Source="+path+";Mode=ReadOnly;Pooling=False");
            await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;
            return Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        void Check(bool value,string text)
        {if(!value)throw new InvalidDataException(text);Console.WriteLine("PASS "+text);}
        async Task Maintain()
        {
            for(var i=0;i<4;i++)
            {
                var result=await db.RetireOrdinaryActivityBatchAsync();
                Check(result.DeletedEvents+(result.Contexts?.ExaminedRows??0)<=200 &&
                    result.DeletedPayloadBytes+(result.Contexts?.RetiredBytes??0)<=1_048_576,
                    "context and event maintenance share the row and encoded-byte limits");
            }
        }
        await db.AppendDiagnosticActivityAsync(Event());
        now=start.AddDays(8);await Maintain();
        Check(await Number("SELECT count(*) FROM history_event WHERE activity_context_key IS NOT NULL;")==0 &&
            await Number("SELECT count(*) FROM activity_context;")==1 &&
            (await db.LoadActivityRetentionWindowAsync()).Contexts is {UnreferencedContexts:1,ExpiredContexts:0},
            "last event expiry starts context grace without deleting its descriptive snapshot");
        var refused=false;
        await using(var c=new SqliteConnection("Data Source="+path+";Pooling=False"))
        {
            await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="DELETE FROM activity_context;";
            try{await q.ExecuteNonQueryAsync();}catch(SqliteException){refused=true;}
        }
        Check(refused,"ordinary SQL cannot delete an unreferenced context");
        now=start.AddDays(14);await Maintain();
        Check(await Number("SELECT count(*) FROM activity_context;")==1,"six unreferenced days do not expire context");
        await db.AppendDiagnosticActivityAsync(Event());
        Check(await Number("SELECT count(*) FROM activity_context;")==1 &&
            (await db.LoadActivityRetentionWindowAsync()).Contexts is {UnreferencedContexts:0,UnreferencedBytes:0},
            "a new exact reference reuses the snapshot and atomically resets its grace and counters");
        now=start.AddDays(22);await Maintain();
        now=start.AddDays(28);await Maintain();
        Check(await Number("SELECT count(*) FROM activity_context;")==1,
            "releasing the new last reference starts a fresh full grace period");
        now=start.AddDays(30);
        db.ActivityRetirementBoundaryForTest=phase=>
        {if(phase=="exact-context-before-delete")throw new IOException("fixture interruption");};
        var interrupted=false;
        try{await db.RetireOrdinaryActivityBatchAsync();}catch(IOException){interrupted=true;}
        db.ActivityRetirementBoundaryForTest=null;
        Check(interrupted && await Number("SELECT count(*) FROM activity_context;")==1 &&
            (await db.LoadActivityRetentionWindowAsync()).Contexts is {UnreferencedContexts:1,ExpiredContexts:0},
            "interrupted context retirement rolls back the snapshot and all counters");
        // A real reopen proves that the grace and private authority do not depend on process memory.
        db=new SyncAuthorityDatabase(path);
        await db.InitializeAsync("context-installation","context-profile","context-lifetime-fixture","{}");
        db.ActivityRetentionClockForTest=()=>now;
        await Maintain();
        var status=(await db.LoadActivityRetentionWindowAsync()).Contexts!;
        Check(await Number("SELECT count(*) FROM activity_context;")==0 &&
            status is {UnreferencedContexts:0,UnreferencedBytes:0,ExpiredContexts:1},
            "restart resumes aged zero-reference context expiry with exact counters");
        Check(await Number("SELECT count(*) FROM pragma_foreign_key_check;")==0 &&
            status.Describe().Contains("seven days",StringComparison.Ordinal),
            "context lifetime leaves exact foreign keys and shared status readback");
    }
}
