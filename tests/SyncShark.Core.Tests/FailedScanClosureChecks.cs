using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class FailedScanClosureChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var local=Path.Combine(root,"local");Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local,"local.txt"),"local fixture bytes");
        using var held=PilotDirectoryLease.Open(local);
        var pair=new SyncPair("failed-scan-pair","Failed scan",local,held.Identity.VolumeSerialNumber,
            held.Identity.FileId,"/FailedScan","id:failed-scan");
        var db=new SyncAuthorityDatabase(Path.Combine(root,"authority.db"));
        await db.InitializeAsync("failed-scan-installation","failed-scan-profile","failed-scan-fixture","{}");
        await db.RegisterPairAsync(pair.Id,1);
        await db.RegisterRootBindingAsync("local-root",pair.Id,"local",1,
            JsonSerializer.Serialize(new{held.Identity.VolumeSerialNumber,held.Identity.FileId}),local);
        await db.RegisterRootBindingAsync("remote-root",pair.Id,"dropbox",1,
            """{"folderId":"id:failed-scan"}""",pair.DropboxRoot);
        var remote=new FakeSyncRemoteStore();remote.Put("remote.txt","remote fixture bytes");
        void Check(bool condition,string text)
        {if(!condition)throw new InvalidDataException(text);Console.WriteLine("PASS "+text);}
        async Task<long> Number(string sql)
        {
            await using var c=new SqliteConnection("Data Source="+db.DatabasePath+";Mode=ReadOnly;Pooling=False");
            await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;
            return Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        Task FailAfterCommit(SyncComparisonProgress p)=>p.Detail.StartsWith("Committed ",StringComparison.Ordinal)
            ?Task.FromException(new IOException("fixture metadata failure")):Task.CompletedTask;
        var failed=false;
        try{await new SyncAuthorityObservationCoordinator(pair,remote,db,"local-root","remote-root",FailAfterCommit).ObserveAsync("initial");}
        catch(IOException){failed=true;}
        Check(failed && await Number("SELECT count(*) FROM scan_generation WHERE status='failed' AND ended_utc IS NOT NULL;")==2 &&
            await Number("SELECT count(*) FROM scan_generation WHERE status='complete';")==0,
            "physical inventory failure closes both exact incomplete generations without claiming completeness");
        Check(await Number("SELECT count(*) FROM observation_delta;")>0 &&
            await Number("SELECT count(*) FROM effective_observation;")==0,
            "failed open metadata is retained without becoming absence or a published observation");
        var late=new List<SyncAuthorityScanGeneration>();
        await using(var c=new SqliteConnection("Data Source="+db.DatabasePath+";Mode=ReadOnly;Pooling=False"))
        {
            await c.OpenAsync();await using var q=c.CreateCommand();
            q.CommandText="SELECT scan_generation_id,pair_id,root_binding_id,side,generation,trigger_kind,started_utc FROM scan_generation;";
            await using var rows=await q.ExecuteReaderAsync();
            while(await rows.ReadAsync())late.Add(new(rows.GetString(0),rows.GetString(1),rows.GetString(2),
                rows.GetString(3),rows.GetInt64(4),rows.GetString(5),DateTimeOffset.Parse(rows.GetString(6))));
        }
        var refused=false;
        try{await db.CompleteScanGenerationAsync(late[0]);}
        catch(Exception e)when(e is InvalidOperationException or InvalidDataException or SqliteException){refused=true;}
        Check(refused,"a late completion cannot reopen a failed inventory");
        Task FailIndex(SyncComparisonProgress p)=>p.Phase=="Updating Folder Index"
            ?Task.FromException(new IOException("fixture index failure")):Task.CompletedTask;
        failed=false;
        try{await new SyncAuthorityObservationCoordinator(pair,remote,db,"local-root","remote-root",FailIndex).ObserveAsync("manual");}
        catch(IOException){failed=true;}
        Check(failed && await Number("SELECT count(*) FROM scan_generation WHERE status='complete';")==2 &&
            await Number("SELECT count(*) FROM scan_generation WHERE status='failed';")==2,
            "failure after publication never relabels completed inventory siblings as failed");
        await new SyncAuthorityObservationCoordinator(pair,remote,db,"local-root","remote-root").ObserveAsync("manual");
        Check(await Number("SELECT count(*) FROM scan_generation WHERE status='complete';")==4 &&
            remote.UploadCount==0 && remote.DownloadCount==0 &&
            await File.ReadAllTextAsync(Path.Combine(local,"local.txt"))=="local fixture bytes",
            "a fresh observation succeeds without replaying failed metadata or changing file contents");
        await db.VerifyHealthAsync();
        await CloudEndpointObservationChecks.RunFailedSaveClosureAsync(Path.Combine(root,"cloud"));
    }
}
