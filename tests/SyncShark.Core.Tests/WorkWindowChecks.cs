using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class WorkWindowChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var db=new SyncAuthorityDatabase(Path.Combine(root,"authority.db"));
        await db.InitializeAsync("window-installation","profile","window-fixture","{}");
        await db.RegisterPairAsync("pair",1);
        foreach(var side in new[]{"local","dropbox"})
            await db.RegisterRootBindingAsync(side+"-root","pair",side,1,
                side=="local"?"""{"volumeSerial":"00000001","fileId":"root"}""":"""{"folderId":"id:root"}""",
                side=="local"?root:"/Fixture");
        var scans=new Dictionary<string,string>();
        foreach(var side in new[]{"local","dropbox"})
        {
            var scan=await db.BeginScanGenerationAsync("pair",side+"-root",side,"manual","{}");
            await db.AppendObservationEntriesAsync(scan,new[]{"a","b","c"}.Select(name=>
                new SyncAuthorityObservationInput(name+".txt","file",side=="local"?
                    JsonSerializer.Serialize(new{volumeSerial="00000001",fileId=name,lastWriteUtc="2026-01-01T00:00:00Z"}):
                    JsonSerializer.Serialize(new{fileId="id:"+name,revision="1"}),new string('a',64),4,"{}")).ToArray());
            await db.CompleteScanGenerationAsync(scan);scans[side]=scan.ScanGenerationId;
        }
        var set=await db.LoadPlanningObservationSetAsync("pair",1,"local-root","dropbox-root",scans["local"],scans["dropbox"]);
        var admission=await db.CreateAcceptedCommandAdmissionAsync(null,"run_pair","pair",null,null,"{}",SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId,"executing");
        async Task<(SyncAuthorityOperationPlan Plan,SyncAuthorityActionRegistration Registration)> Candidate(string name)
        {
            var registration=await db.ReserveActionAsync("window-"+name,admission);
            var local=set.Local.Single(x=>x.RelativePath==name+".txt");
            var remote=set.Dropbox.Single(x=>x.RelativePath==name+".txt");
            var point=set.Bind(SyncAuthorityReconciliationPlanner.ObservationSetIdentity(set),local.ObjectId);
            var plan=new SyncAuthorityOperationPlan(registration.OperationId,"window-"+name,
                SyncAuthorityOperationKind.UpdateRemote,"pair",local.ObjectId,1,"profile","{}",50,
                [new(registration.OperationId+"-source","pair",local.ObjectId,"source","local-root",local.MembershipId,
                    local.ObjectVersionId,local.MembershipGeneration,name+".txt","{}"),
                 new(registration.OperationId+"-destination","pair",remote.ObjectId,"destination","dropbox-root",remote.MembershipId,
                    remote.ObjectVersionId,remote.MembershipGeneration,name+".txt","{}")],[],[],PlanningAuthorities:[point]);
            return(plan,registration);
        }
        async Task Sql(string sql)
        {
            await using var c=new SqliteConnection("Data Source="+db.DatabasePath+";Pooling=False");await c.OpenAsync();
            await using var q=c.CreateCommand();q.CommandText=sql;await q.ExecuteNonQueryAsync();
        }
        async Task<long> Number(string sql)
        {
            await using var c=new SqliteConnection("Data Source="+db.DatabasePath+";Mode=ReadOnly;Pooling=False");await c.OpenAsync();
            await using var q=c.CreateCommand();q.CommandText=sql;return Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        void Check(bool pass,string message)
        {if(!pass)throw new InvalidDataException(message);Console.WriteLine("PASS "+message);}
        // Occupancy is fixture input. The actual admission, ticket and writer decide every tested attempt.
        await Sql("""
            WITH RECURSIVE positions(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM positions WHERE n<255)
            INSERT INTO operation(operation_id,kind,status,priority,configuration_revision,capability_profile_id,
                idempotency_key,preconditions_json,created_utc)
            SELECT 'occupied-'||n,'create_remote','planned',50,1,'profile','occupied-'||n,'{}','2026-01-01T00:00:00Z'
            FROM positions;
            """);
        var a=await Candidate("a");
        var full=false;
        try{await db.CreateOperationAsync(a.Plan,admission,reservation:a.Registration.Reservation);}
        catch(SyncAuthorityWorkWindowFullException){full=true;}
        Check(full && !a.Registration.Reservation!.InsertionClaimed && await db.AvailableWorkWindowAsync()==0 &&
            await Number("SELECT next_action_sequence FROM installation;")==1,
            "full installation window refuses insertion without spending its fresh ticket or sequence");
        await Sql("UPDATE operation SET status='cancelled',terminal_utc='2026-01-02T00:00:00Z' WHERE operation_id='occupied-0';");
        Check(await db.AvailableWorkWindowAsync()==1,"a genuinely terminal action releases one window slot");
        await db.CreateOperationAsync(a.Plan,admission,reservation:a.Registration.Reservation);
        await db.CreateOperationAsync(a.Plan,admission);
        Check(await db.AvailableWorkWindowAsync()==0 && await Number("SELECT next_action_sequence FROM installation;")==2,
            "fresh insertion consumes the released slot; exact retained replay remains valid at capacity");
        await Sql("UPDATE operation SET status='cancelled',terminal_utc='2026-01-02T00:00:00Z' WHERE operation_id='occupied-1';");
        var candidates=new[]{await Candidate("b"),await Candidate("c")};
        var peers=new[]{db,new SyncAuthorityDatabase(db.DatabasePath)};
        var admitted=await Task.WhenAll(candidates.Select((candidate,index)=>Task.Run(async()=>
        {
            try{await peers[index].CreateOperationAsync(candidate.Plan,admission,reservation:candidate.Registration.Reservation);return true;}
            catch(SyncAuthorityWorkWindowFullException){return false;}
        })));
        Check(admitted.Count(value=>value)==1 && await db.AvailableWorkWindowAsync()==0 &&
            candidates.Count(value=>value.Registration.Reservation!.InsertionClaimed)==1,
            "two independent database writers cannot spend the same final window slot");
        await db.VerifyHealthAsync();

        // Strip only additive monitor fixture schema before constructing version15.
        await MonitorLifetimeChecks.RemoveMigration17ForPredecessorFixtureAsync(db.DatabasePath);
        // A current exact version-15 predecessor differs only by this additive index.
        // Exercise the production verified rollback and migration on isolated fixture bytes.
        await Sql("DROP INDEX operation_work_window; DELETE FROM schema_migration WHERE schema_version=16; "+
            "INSERT INTO schema_migration(schema_version,applied_utc,source_revision) VALUES(15,'2026-01-01T00:00:00Z','window-predecessor'); PRAGMA user_version=15;");
        var migrated=new SyncAuthorityDatabase(db.DatabasePath);
        await migrated.InitializeAsync("window-installation","profile","window-fixture","{}");
        Check(await Number("PRAGMA user_version;")==SyncAuthoritySchema.Version && File.Exists(migrated.Version15RollbackDatabasePath) &&
            await migrated.AvailableWorkWindowAsync()==0,
            "exact version-15 predecessor migrates with verified rollback and preserves all occupied slots");
        await using var rollback=new SqliteConnection("Data Source="+migrated.Version15RollbackDatabasePath+";Mode=ReadOnly;Pooling=False");
        await rollback.OpenAsync();await using var verify=rollback.CreateCommand();verify.CommandText="PRAGMA user_version;";
        Check(Convert.ToInt64(await verify.ExecuteScalarAsync())==15,"rollback retains the original version-15 schema");
    }
}

