using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class InventoryLifetimeChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,"authority.db");
        var db=new SyncAuthorityDatabase(path);
        await db.InitializeAsync("inventory-life-install","profile","inventory-life-fixture","{}");
        await db.RegisterPairAsync("pair",1);
        await db.RegisterRootBindingAsync("local-root","pair","local",1,
            """{"volumeSerial":"00000001","fileId":"root"}""",root);
        await db.RegisterRootBindingAsync("dropbox-root","pair","dropbox",1,"""{"folderId":"id:root"}""","/Fixture");
        var generations=new List<(string Local,string Remote)>();
        SyncRewindSession? rewind=null;
        string? actionId=null;
        void Check(bool condition,string text)
        {if(!condition)throw new InvalidDataException(text);Console.WriteLine("PASS "+text);}
        async Task<long> Number(string sql,params (string,object?)[] values)
        {
            await using var c=new SqliteConnection("Data Source="+path+";Mode=ReadOnly;Pooling=False");
            await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;
            foreach(var (name,value) in values)q.Parameters.AddWithValue(name,value??DBNull.Value);
            return Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        async Task<(string Local,string Remote)> Observe(int revision,bool complete=true,int count=1)
        {
            var scans=new List<SyncAuthorityScanGeneration>();
            foreach(var side in new[]{"local","dropbox"})
            {
                var scan=await db.BeginScanGenerationAsync("pair",side+"-root",side,"manual","{}");
                scans.Add(scan);
                var entries=Enumerable.Range(0,count).Select(i=>new SyncAuthorityObservationInput(
                    i==0?"file.txt":"extra-"+i+".txt","file",
                    side=="local"?JsonSerializer.Serialize(new{volumeSerial="00000001",fileId="file-"+i,lastWriteUtc=$"2026-01-{revision:00}T00:00:00Z"}):
                        JsonSerializer.Serialize(new{fileId="id:file-"+i,revision=revision.ToString()}),
                    revision.ToString("x").PadLeft(64,'0'),revision+2,"{}")).ToArray();
                await db.PrepareCompactScanAsync(scan,entries,[],null,null);
                if(complete)await db.CompleteScanGenerationAsync(scan);
            }
            if(!complete)await db.CloseFailedScanGenerationsAsync(scans);
            return(scans[0].ScanGenerationId,scans[1].ScanGenerationId);
        }
        for(var revision=1;revision<=7;revision++)
        {
            var scan=await Observe(revision);generations.Add(scan);
            if(revision==1)
            {
                rewind=new SyncRewindSession("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa31","inventory-connection","pair","Inventory","/Fixture",
                    scan.Local,scan.Remote,"cursor",DateTimeOffset.UtcNow,SyncRewindStatus.AwaitingDropbox);
                await db.PublishRewindSessionAsync((await db.LoadAuthorityInstallationIdAsync())!,rewind);
            }
            if(revision==2)
            {
                var header=JsonSerializer.Serialize(new{plan=new{LocalScanGenerationId=scan.Local,DropboxScanGenerationId=scan.Remote}});
                await db.BeginConsentPreparationAsync(new SyncAuthorityConsentPreparation("inventory-consent",null,"inventory-reserved",
                    "pair",new string('a',64),new string('b',64),header,0));
            }
            if(revision==3)
                await db.CreateAcceptedCommandAdmissionAsync(null,"run_pair","pair",null,null,
                    JsonSerializer.Serialize(new{reviewedPlan=new{LocalScanGenerationId=scan.Local,DropboxScanGenerationId=scan.Remote}}),
                    SyncAuthorityCommandPurpose.NormalEffect);
            if(revision==4)
            {
                var set=await db.LoadPlanningObservationSetAsync("pair",1,"local-root","dropbox-root",scan.Local,scan.Remote);
                var local=set.Local.Single(x=>x.RelativePath=="file.txt");
                var remote=set.Dropbox.Single(x=>x.RelativePath=="file.txt");
                var command=await db.CreateAcceptedCommandAdmissionAsync(null,"run_pair","pair",null,null,"{}",SyncAuthorityCommandPurpose.NormalEffect);
                await db.TransitionCommandRequestAsync(command.CommandRequestId,"executing");
                var ticket=await db.ReserveActionAsync("inventory-pin",command);actionId=ticket.OperationId;
                var plan=new SyncAuthorityOperationPlan(ticket.OperationId,"inventory-pin",SyncAuthorityOperationKind.UpdateRemote,
                    "pair",local.ObjectId,1,"profile","{}",50,
                    [new(ticket.OperationId+"-source","pair",local.ObjectId,"source","local-root",local.MembershipId,
                        local.ObjectVersionId,local.MembershipGeneration,"file.txt","{}"),
                     new(ticket.OperationId+"-destination","pair",remote.ObjectId,"destination","dropbox-root",remote.MembershipId,
                        remote.ObjectVersionId,remote.MembershipGeneration,"file.txt","{}")],[],[],
                    PlanningAuthorities:[set.Bind(SyncAuthorityReconciliationPlanner.ObservationSetIdentity(set),local.ObjectId)]);
                await db.CreateOperationAsync(plan,command,reservation:ticket.Reservation);
                Check(await Number("SELECT sum(planning_reference_count) FROM scan_generation;")==2,
                    "real action registration atomically pins its two exact scan authorities");
            }
        }
        var failed=await Observe(8,false,260);
        var latest=await Observe(9);generations.Add(latest);
        Check(await Number("SELECT count(*) FROM effective_observation WHERE scan_generation_id IN ($left,$right);",
            ("$left",failed.Local),("$right",failed.Remote))==0,
            "failed sealed overlays never project a partial or usable inventory");
        var start=DateTimeOffset.UtcNow;var now=start;db.ActivityRetentionClockForTest=()=>now;
        await db.EnableActivityRetentionForTestAsync();
        async Task Maintain(int passes)
        {
            for(var pass=0;pass<passes;pass++)
            {
                var result=await db.RetireOrdinaryActivityBatchAsync();
                if(result.DeletedEvents+(result.Contexts?.ExaminedRows??0)+(result.Objects?.ExaminedRows??0)+
                        (result.Inventory?.ExaminedRows??0)>200 ||
                    result.DeletedPayloadBytes+(result.Contexts?.RetiredBytes??0)+(result.Objects?.RetiredBytes??0)+
                        (result.Inventory?.RetiredBytes??0)>1_048_576)
                    throw new InvalidDataException("Shared metadata maintenance budget exceeded.");
            }
        }
        await Maintain(50);
        Check(await Number("SELECT count(*) FROM scan_generation WHERE unreferenced_since_utc IS NOT NULL;")>=4,
            "unreferenced historical scans start seven-day grace while actual metadata remains intact");
        for(var i=0;i<4;i++)
            Check(await Number("SELECT count(*) FROM scan_generation WHERE scan_generation_id IN ($left,$right) AND unreferenced_since_utc IS NULL;",
                ("$left",generations[i].Local),("$right",generations[i].Remote))==2,
                new[]{"active Rewind pins exact historical cuts","normalized consent pins exact historical cuts",
                    "legacy reviewed command pins exact historical cuts","retained planning action pins exact historical cuts"}[i]);
        var refused=false;
        await using(var c=new SqliteConnection("Data Source="+path+";Pooling=False"))
        {
            await c.OpenAsync();await using var q=c.CreateCommand();
            q.CommandText="DELETE FROM observation_cut WHERE scan_generation_id=$id;";q.Parameters.AddWithValue("$id",generations[4].Local);
            try{await q.ExecuteNonQueryAsync();}catch(SqliteException){refused=true;}
        }
        Check(refused,"ordinary SQL cannot delete an unreferenced inventory cut");
        now=start.AddDays(6);
        await db.PersistRewindSessionStateAsync(rewind!.SessionId,"pair",SyncRewindStatus.Resumed,"fixture releases checkpoint",null,true);
        await Maintain(45);
        Check(await Number("SELECT inventory_expired_rows FROM installation;")==0,"no inventory detail expires before full grace");
        now=start.AddDays(8);
        var before=await Number("SELECT count(*) FROM observation_delta;");
        db.ActivityRetirementBoundaryForTest=phase=>
        {if(phase=="exact-inventory-before-delete")throw new IOException("fixture interrupted inventory retirement");};
        var interrupted=false;
        for(var pass=0;pass<16 && !interrupted;pass++)
            try{await db.RetireOrdinaryActivityBatchAsync();}catch(IOException){interrupted=true;}
        db.ActivityRetirementBoundaryForTest=null;
        Check(interrupted && await Number("SELECT count(*) FROM observation_delta;")==before &&
            await Number("SELECT inventory_expired_rows FROM installation;")==0,
            "interrupted inventory retirement rolls back metadata and accounting together");
        db=new SyncAuthorityDatabase(path);
        await db.InitializeAsync("inventory-life-install","profile","inventory-life-fixture","{}");
        db.ActivityRetentionClockForTest=()=>now;
        // Large abandoned overlays must progress across bounded passes, not become oversized-cut holds.
        for(var pass=0;pass<600 && await Number("SELECT count(*) FROM observation_delta WHERE scan_generation_id IN ($left,$right);",
            ("$left",failed.Local),("$right",failed.Remote))!=0;pass++)await Maintain(1);
        await Maintain(55);
        Check(await Number("SELECT count(*) FROM observation_delta WHERE scan_generation_id IN ($left,$right);",
                ("$left",failed.Local),("$right",failed.Remote))==0 &&
            await Number("SELECT count(*) FROM observation_cut WHERE scan_generation_id IN ($left,$right);",
                ("$left",failed.Local),("$right",failed.Remote))==0,
            "restart drains both large abandoned overlays and headers through bounded real maintenance");
        Check(await Number("SELECT count(*) FROM observation_cut WHERE scan_generation_id IN ($left,$right);",
            ("$left",generations[0].Local),("$right",generations[0].Remote))==2,
            "recent Rewind release restarts grace and prevents premature historical cut expiry");
        Check(await Number("SELECT count(*) FROM observation_cut WHERE scan_generation_id IN ($left,$right);",
            ("$left",generations[4].Local),("$right",generations[4].Remote))==0,
            "unrelated historical cuts expire while other exact consumer pins remain");
        Check(await Number("SELECT count(*) FROM effective_observation WHERE scan_generation_id IN ($left,$right);",
            ("$left",latest.Local),("$right",latest.Remote))==2 &&
            await Number("SELECT count(*) FROM scan_generation WHERE status='complete' AND generation IN (7,9);")==4,
            "latest two complete generations and current interval coverage remain usable");
        now=start.AddDays(16);await Maintain(100);
        Check(await Number("SELECT count(*) FROM observation_cut WHERE scan_generation_id IN ($left,$right);",
            ("$left",generations[0].Local),("$right",generations[0].Remote))==0,
            "released Rewind cut expires only after its new seven-day grace");
        now=start.AddDays(24);await Maintain(100);
        Check(await Number("SELECT count(*) FROM observation_interval WHERE to_generation IS NOT NULL;")<14 &&
            await Number("SELECT count(*) FROM operation WHERE operation_id=$id;",("$id",actionId))==1 &&
            await Number("SELECT sum(planning_reference_count) FROM scan_generation;")==2,
            "closed ranges retire after uncovered grace while exact action authority remains intact");
        var status=(await db.LoadActivityRetentionWindowAsync()).Inventory!;
        Check(status.ExpiredRows>520 && !status.PlanningAccountingPending &&
            status.UnreferencedScans==await Number("SELECT count(*) FROM scan_generation WHERE unreferenced_since_utc IS NOT NULL;") &&
            status.UnreferencedIntervals==await Number("SELECT count(*) FROM observation_interval WHERE unreferenced_since_utc IS NOT NULL;"),
            "shared inventory status agrees with independently recomputed retained counters");
        var next=await db.BeginScanGenerationAsync("pair","local-root","local","manual","{}");
        Check(next.Generation==10,"scan retirement preserves exact-root generation high water");
        await db.CloseFailedScanGenerationsAsync([next]);await db.VerifyHealthAsync();
        Check(await Number("SELECT count(*) FROM pragma_foreign_key_check;")==0,
            "inventory retirement leaves foreign-key integrity and legitimate future observation intact");
    }
}

