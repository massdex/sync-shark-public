using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class RunSummaryLifetimeChecks
{
    internal static async Task RunAsync(string root)
    {
        if(Directory.Exists(root))throw new IOException("Use a fresh run-summary fixture.");
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,"authority.db");
        var db=new SyncAuthorityDatabase(path);
        await db.InitializeAsync("run-lifetime-install","profile","fixture","{}");
        await db.RegisterPairAsync("pair",1);
        await db.EnableActivityRetentionForTestAsync();
        var kept=new List<(SyncAuthorityCommandAdmission Admission,SyncAuthorityCommandReservation Reservation)>();
        async Task<(SyncAuthorityCommandAdmission,SyncAuthorityCommandReservation)> Closed(bool noop=false,string? parent=null,string extra="")
        {
            var reservation=SyncAuthorityCommandReservation.Mint();
            var payload=JsonSerializer.Serialize(new{pairIds=new[]{"pair"},retryOfRunId=parent,extra});
            var a=await db.CreateAcceptedCommandAdmissionAsync(reservation,"run_pair","pair",null,null,
                payload,SyncAuthorityCommandPurpose.NormalEffect);
            await db.TransitionCommandRequestAsync(a.CommandRequestId,"executing");
            var summary=JsonSerializer.Serialize(new{pairs=new[]{new{pairId="pair",uploaded=noop?0:1,downloaded=0,
                unchanged=10,blocked=0,conflicts=0,stopped=false,failed=0,notAttempted=0,needsVerification=0,skipped=0,
                detailsSource="retained_history_and_authority"}}});
            await db.TransitionCommandRequestAsync(a.CommandRequestId,"completed",summary);
            await db.CloseOrdinaryCommandIssuanceAsync(a.CommandRequestId);
            return(a,reservation);
        }
        for(var i=0;i<5;i++)kept.Add(await Closed());
        db.RunSummaryPolicyForTest=new(MaximumRuns:2);
        db.RunSummaryRetirementBoundaryForTest=phase=>{if(phase=="before-commit")throw new InvalidOperationException("fixture interruption");};
        await Refuses(()=>db.RetireRunSummaryBatchAsync(),"interrupted summary collection rolls back");
        Require(await Count(path)==5,"summary rollback retains every original command");
        db.RunSummaryRetirementBoundaryForTest=null;
        for(var i=0;i<12;i++)await db.RetireRunSummaryBatchAsync();
        Require(await Count(path)==2 && await db.LoadRunByIdAsync(kept[0].Admission.CommandRequestId) is null &&
            await db.LoadRunByIdAsync(kept[^1].Admission.CommandRequestId) is not null,
            "summary count pressure retires oldest eligible rows and retains the newest");
        await Refuses(()=>db.CreateAcceptedCommandAdmissionAsync(kept[0].Reservation,"run_pair","pair",null,null,
            JsonSerializer.Serialize(new{pairIds=new[]{"pair"},retryOfRunId=(string?)null,extra=""}),
            SyncAuthorityCommandPurpose.NormalEffect),"a spent command reservation cannot recreate an expired run");
        await Refuses(()=>db.OpenRunActivityWriterAsync(kept[0].Admission),
            "a late callback cannot reopen an expired command writer");
        await Refuses(()=>Change(path,"DELETE FROM command_request;"),"ordinary SQL cannot delete command authority");

        db.RunSummaryPolicyForTest=new(MaximumRuns:100,MaximumNoops:2);
        for(var i=0;i<5;i++)await Closed(noop:true);
        for(var i=0;i<12;i++)await db.RetireRunSummaryBatchAsync();
        Require(await Scalar(path,"SELECT count(*) FROM command_request WHERE summary_noop=1;")==2,
            "no-op summaries obey their separate finite count within the run budget");
        var oversized=await Closed(extra:new string('x',3000));
        db.RunSummaryPolicyForTest=new(MaximumBytes:4000);
        for(var i=0;i<12;i++)await db.RetireRunSummaryBatchAsync();
        var status=(await db.LoadActivityRetentionWindowAsync()).RunSummaries!;
        Require(status.EncodedBytes<=4000 && await db.LoadRunByIdAsync(oversized.Item1.CommandRequestId) is not null,
            "encoded-byte pressure retains the newest fitting summary without an effect-admission cap");
        var measured=await Scalar(path,"SELECT coalesce(sum(summary_bytes),0) FROM command_request WHERE summary_eligible=1;");
        Require(measured==status.EncodedBytes && status.EligibleRuns==await Scalar(path,
            "SELECT count(*) FROM command_request WHERE summary_eligible=1;"),
            "incremental summary counters match exact retained eligible rows");

        db.RunSummaryPolicyForTest=new();
        var parent=await Closed();var child=await Closed(parent:parent.Item1.CommandRequestId);
        for(var i=0;i<6;i++)await db.RetireRunSummaryBatchAsync();
        Require(await Scalar(path,"SELECT summary_eligible FROM command_request WHERE command_request_id='"+parent.Item1.CommandRequestId+"';")==0,
            "a retained retry child pins the exact original command");
        var active=await db.CreateAcceptedCommandAdmissionAsync(null,"run_pair","pair",null,null,"{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        db.ActivityRetentionClockForTest=()=>DateTimeOffset.UtcNow.AddDays(91);
        var delayedPasses=0;
        db.RunSummaryRetirementBoundaryForTest=phase=>
        {
            if(phase=="after-run-maintenance-preflight"){delayedPasses++;Thread.Sleep(75);}
        };
        for(var i=0;i<20;i++)await db.RetireRunSummaryBatchAsync();
        db.RunSummaryRetirementBoundaryForTest=null;
        Require(delayedPasses==20,"slow run-maintenance preflight still permits bounded classification and retirement");
        Require(await db.LoadRunByIdAsync(parent.Item1.CommandRequestId) is null &&
            await db.LoadRunByIdAsync(child.Item1.CommandRequestId) is null &&
            await db.LoadRunByIdAsync(active.CommandRequestId) is not null,
            "age retirement closes leaf retries before their parent while an active run remains pinned");
        await using(var c=new SqliteConnection("Data Source="+path+";Mode=ReadOnly;Pooling=False"))
        {
            await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="PRAGMA foreign_key_check;";
            await using var rows=await q.ExecuteReaderAsync();
            Require(!await rows.ReadAsync(),"run collection preserves every database foreign key");
        }
        var window=await db.LoadActivityRetentionWindowAsync();
        Require(window.RunSummaries is {AccountingPending:false,ExpiredRuns:>0} &&
            window.Describe().Contains("Run summaries"),"shared status discloses exact run expiry and accounting");
    }
    private static Task<long> Count(string path)=>Scalar(path,"SELECT count(*) FROM command_request;");
    private static async Task<long> Scalar(string path,string sql)
    {
        await using var c=new SqliteConnection("Data Source="+path+";Mode=ReadOnly;Pooling=False");
        await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;
        return Convert.ToInt64(await q.ExecuteScalarAsync());
    }
    private static async Task Change(string path,string sql)
    {
        await using var c=new SqliteConnection("Data Source="+path+";Mode=ReadWrite;Pooling=False");
        await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;await q.ExecuteNonQueryAsync();
    }
    private static async Task Refuses(Func<Task> work,string message)
    {
        try{await work();}catch(Exception e)when(e is InvalidOperationException or InvalidDataException or SqliteException)
        {Require(true,message);return;}throw new InvalidDataException("Expected refusal: "+message);
    }
    private static void Require(bool value,string message)
    {if(!value)throw new InvalidDataException(message);Console.WriteLine("PASS "+message);}
}
