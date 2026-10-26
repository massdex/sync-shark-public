using System.Text.Json;
using System.Security.Cryptography;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class EssentialSuccessorChecks
{
    internal static async Task RunAsync(string root)
    {
        if(Directory.Exists(root))throw new IOException("Use a fresh successor fixture.");
        Directory.CreateDirectory(root);
        var sourcePath=Path.Combine(root,"source.db");var targetPath=Path.Combine(root,"successor.db");
        var options=new SyncEssentialSuccessorOptions(root);
        var source=new SyncAuthorityDatabase(sourcePath);
        await source.InitializeAsync("installation","profile","fixture","{}");
        await source.RegisterPairAsync("pair",1);
        await source.RegisterRootBindingAsync("local-root","pair","local",1,
            """{"volumeSerial":"00000001","fileId":"root"}""",root);
        await source.RegisterRootBindingAsync("remote-root","pair","dropbox",1,
            """{"folderId":"id:root"}""","/Fixture");
        await SealAsync(sourcePath);
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(sourcePath,options);
        var sourceHash=Hash(sourcePath);
        await RefuseAsync(()=>SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(
            sourcePath,sourcePath,identity,options),"identical source/target refused");
        await RefuseAsync(()=>SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(
            sourcePath,targetPath,identity with{Sha256=new string('0',64)},options),"changed source identity refused before target creation");
        Check(!File.Exists(targetPath),"refused source identity created no target");
        var unrelatedPath=Path.Combine(root,"unrelated.db");
        var unrelated=new SyncAuthorityDatabase(unrelatedPath);
        await unrelated.InitializeAsync("other","other-profile","fixture","{}");
        await SealAsync(unrelatedPath);var unrelatedHash=Hash(unrelatedPath);
        await RefuseAsync(()=>SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(
            sourcePath,unrelatedPath,identity,options),"unrelated target refused");
        Check(Hash(unrelatedPath)==unrelatedHash,"unrelated target bytes unchanged");
        SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=phase=>
        {
            if(phase=="after-table:sync_pair")throw new Injected();
        };
        try
        {
            try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(sourcePath,targetPath,identity,options);}
            catch(Injected){Console.WriteLine("PASS interrupted configuration preparation retained an incomplete target");}
        }
        finally{SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=null;}
        var incomplete=new SyncAuthorityDatabase(targetPath);
        await RefuseAsync(()=>incomplete.CreateAcceptedCommandAdmissionAsync(null,"run_all",null,null,null,"{}",
            SyncAuthorityCommandPurpose.NormalEffect),"incomplete successor refuses ordinary admission");
        await RefuseAsync(()=>incomplete.VerifyHealthAsync(),"incomplete successor refuses normal-ready health");
        await RefuseAsync(()=>SqlAsync(targetPath,
            "UPDATE installation SET essential_successor_json=json_set(essential_successor_json,'$.Complete',1);"),
            "ordinary SQL cannot forge completion");
        var prepared=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(sourcePath,targetPath,identity,options);
        Check(prepared.Complete&&prepared.TargetGeneration!=identity.DatabaseGeneration,"resumed target has a new complete generation");
        Check(prepared.Tables.Count==62&&prepared.Tables.All(x=>x.SourceRows==x.SelectedRows+x.OriginalOnlyRows),
            "all62 source tables have exact selected/original-only accounting");
        Check(Hash(sourcePath)==sourceHash&&!File.Exists(sourcePath+"-wal")&&!File.Exists(sourcePath+"-shm"),
            "source bytes and sidecar absence preserved");
        var verified=await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(sourcePath,targetPath,identity,options);
        Check(verified.Complete&&verified.TargetGeneration==prepared.TargetGeneration,"reopened exact source-bound successor verifies");
        var db=new SyncAuthorityDatabase(targetPath);
        await db.VerifyHealthAsync();
        var admission=await db.CreateAcceptedCommandAdmissionAsync(null,"run_pair","pair",null,null,"{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        Check(admission.DatabaseGeneration!=identity.DatabaseGeneration,"new command admits only under the successor generation");
        await RefuseAsync(()=>db.ReserveActionAsync("stale",admission with{DatabaseGeneration=identity.DatabaseGeneration}),
            "source-generation value cannot grant a retained target command permission");
        var unsupportedPath=Path.Combine(root,"unsupported.db");
        var unsupported=new SyncAuthorityDatabase(unsupportedPath);
        await unsupported.InitializeAsync("unsupported","profile","fixture","{}");
        var unsupportedAdmission=await unsupported.CreateAcceptedCommandAdmissionAsync(null,"run_all",null,null,null,"{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        await unsupported.TransitionCommandRequestAsync(unsupportedAdmission.CommandRequestId,"executing");
        await unsupported.TransitionCommandRequestAsync(unsupportedAdmission.CommandRequestId,"completed");
        await unsupported.RegisterPairAsync("unsupported-pair",1);
        await SqlAsync(unsupportedPath,"INSERT INTO reconciliation_decision(reconciliation_decision_id,pair_id,decision_kind,decision_json,decided_utc) VALUES('unresolved-decision','unsupported-pair','blocked','{}','2026-01-01T00:00:00Z');");
        await SealAsync(unsupportedPath);
        var unsupportedIdentity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(unsupportedPath,options);
        var forbidden=Path.Combine(root,"unsupported-successor.db");
        await RefuseAsync(()=>SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(
            unsupportedPath,forbidden,unsupportedIdentity,options),"unadapted unresolved reconciliation decision refused");
        Check(!File.Exists(forbidden),"unsupported state creates no candidate");
        var sourceMutated=identity with{Bytes=identity.Bytes+1};
        await RefuseAsync(()=>SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(
            sourcePath,targetPath,sourceMutated,options),"resume refuses source drift");
        Console.WriteLine("PASS configuration successor production readers, guards and restart checks complete");
    }
    internal static async Task RunExplorerAsync(string root)
    {
        if(Directory.Exists(root))throw new IOException("Use a fresh Explorer successor fixture.");
        Directory.CreateDirectory(root);var path=Path.Combine(root,"source.db");var target=Path.Combine(root,"successor.db");
        var db=new SyncAuthorityDatabase(path);await db.InitializeAsync("explorer-installation","profile","fixture","{}");
        await db.RegisterPairAsync("pair",1);
        var cuts=new Dictionary<string,List<string>>();
        foreach(var side in new[]{"local","dropbox"})
        {
            await db.RegisterRootBindingAsync(side+"-root","pair",side,1,
                side=="local"?"""{"volumeSerial":"00000001","fileId":"root"}""":"""{"folderId":"id:root"}""",root);
            cuts[side]=[];
            for(var n=0;n<2;n++)
            {
                var scan=await db.BeginScanGenerationAsync("pair",side+"-root",side,"manual","{}");
                await db.CompleteScanGenerationAsync(scan);cuts[side].Add(scan.ScanGenerationId);
            }
        }
        var selection=new SyncSelectionIndex([new("excluded",false)],true);
        SyncPathIndexEntry[] retained=[
            new("excluded",PilotEntryKind.Directory,true,true,false,false,"local:00000001:0000000000000001",null,false),
            new("excluded/child",PilotEntryKind.Directory,true,true,false,false,null,null,false),
            new("excluded/child/kept.txt",PilotEntryKind.File,true,true,false,false,null,null,false)
        ];
        await db.ReplacePathIndexAsync("pair",cuts["local"][0],cuts["dropbox"][0],retained,selection);
        SyncPathIndexEntry[] current=[
            new("current",PilotEntryKind.Directory,true,true,true,false,null,"id:current",true),
            new("current/online.txt",PilotEntryKind.File,true,true,true,true,null,"id:online",true)
        ];
        await db.ReplacePathIndexAsync("pair",cuts["local"][1],cuts["dropbox"][1],current,selection);
        var expected=System.Text.Json.JsonSerializer.Serialize(await db.LoadFolderSelectionPageAsync("pair"));
        var excluded=System.Text.Json.JsonSerializer.Serialize(await db.LoadExcludedPathIndexPathsAsync("pair"));
        await SqlAsync(path,"UPDATE sync_path_index SET subtree_folder_count=999,subtree_excluded_items=999 WHERE object_kind='directory';");
        await SealAsync(path);
        var options=new SyncEssentialSuccessorOptions(root);var hash=Hash(path);
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=phase=>{if(phase=="after-table:sync_path_index")throw new Injected();};
        try
        {
            try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);throw new Exception("Missing interruption.");}
            catch(Injected){Check(true,"Explorer projection interruption leaves a resumable incomplete target");}
        }
        finally{SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=null;}
        var report=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);
        await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,target,identity,options);
        var successor=new SyncAuthorityDatabase(target);
        Check(expected==System.Text.Json.JsonSerializer.Serialize(await successor.LoadFolderSelectionPageAsync("pair")),
            "actual Explorer reader preserves presence and selection while rebuilding corrupted folder totals");
        Check(excluded==System.Text.Json.JsonSerializer.Serialize(await successor.LoadExcludedPathIndexPathsAsync("pair")),
            "excluded descendants absent from current inventory remain visible");
        Check(report.Tables.Single(x=>x.Table=="sync_path_index") is {SourceRows:5,SelectedRows:5,TargetRows:5},
            "every current Explorer scalar row has an explicit derived disposition");
        Check(report.Tables.Single(x=>x.Table=="scan_generation") is {SourceRows:4,SelectedRows:2,OriginalOnlyRows:2},
            "display-only old scan links do not pin historical observation cuts");
        await using(var c=new SqliteConnection("Data Source="+target+";Mode=ReadOnly;Pooling=False"))
        {
            await c.OpenAsync();await using var q=c.CreateCommand();
            q.CommandText="SELECT count(*) FROM sync_path_index WHERE included=0 AND local_scan_generation_id IS NULL AND local_present=1;";
            Check(Convert.ToInt64(await q.ExecuteScalarAsync())==3,"excluded local knowledge is not falsely attributed to a newer scan");
        }
        Check(Hash(path)==hash,"Explorer source remains byte-exact including original cached totals");
        var bad=Path.Combine(root,"wrong-side.db");File.Copy(path,bad);
        await SqlAsync(bad,"UPDATE sync_path_index SET local_scan_generation_id=dropbox_scan_generation_id;");
        await SealAsync(bad);var badId=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(bad,options);
        var refused=Path.Combine(root,"refused.db");
        await RefuseAsync(()=>SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(bad,refused,badId,options),
            "wrong-side Explorer evidence rejects");
        Check(!File.Exists(refused),"wrong-side Explorer reference rejected before target creation");
    }

    internal static async Task RunRewindAsync(string root)
    {
        if(Directory.Exists(root))throw new IOException("Use a fresh Rewind successor fixture.");
        Directory.CreateDirectory(root);var path=Path.Combine(root,"source.db");var target=Path.Combine(root,"successor.db");
        var db=new SyncAuthorityDatabase(path);await db.InitializeAsync("rewind-installation","profile","fixture","{}");
        await db.RegisterPairAsync("pair",1);
        var cuts=new Dictionary<string,string[]>();
        foreach(var side in new[]{"local","dropbox"})
        {
            await db.RegisterRootBindingAsync(side+"-root","pair",side,1,
                side=="local"?"""{"volumeSerial":"00000001","fileId":"root"}""":"""{"folderId":"id:root"}""",root);
            var ids=new List<string>();
            for(var n=0;n<4;n++)
            {
                var scan=await db.BeginScanGenerationAsync("pair",side+"-root",side,"manual","{}");
                await db.AppendObservationEntriesAsync(scan,[new("file.txt","file",
                    side=="local"?"""{"volumeSerial":"00000001","fileId":"file","lastWriteUtc":"2026-01-01T00:00:00Z"}""":
                        """{"fileId":"id:file","revision":"1"}""",new string('a',64),4,"{}")]);
                await db.CompleteScanGenerationAsync(scan);ids.Add(scan.ScanGenerationId);
            }
            cuts[side]=ids.ToArray();
        }
        var session=new SyncRewindSession("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01","rewind-connection","pair",
            "Rewind fixture","/rewind",cuts["local"][1],cuts["dropbox"][1],"checkpoint-cursor",DateTimeOffset.UtcNow,
            SyncRewindStatus.AwaitingDropbox);
        await db.PublishRewindSessionAsync("rewind-installation",session);
        var preview=new[]{new SyncRewindPreviewItem("file.txt","Changed remotely","Review before apply",true,false,"Original reviewed choice.")};
        var published=session with {Status=SyncRewindStatus.PreviewReady,LocalPreviewScanId=cuts["local"][2],
            DropboxPreviewScanId=cuts["dropbox"][2],Preview=preview,
            PathProgress=[new("file.txt",SyncRewindPathStatus.Pending,null,"Original pending checkpoint.",DateTimeOffset.UtcNow)]};
        await db.PublishRewindPreviewAsync("rewind-installation",published,session.SessionId,null,null);
        var resumed=session with {SessionId="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa02",Status=SyncRewindStatus.Resumed};
        await db.PublishRewindSessionAsync("rewind-installation",resumed);
        var expected=System.Text.Json.JsonSerializer.Serialize(await db.LoadActiveRewindSessionAsync("pair"));
        await SealAsync(path);
        var options=new SyncEssentialSuccessorOptions(root);
        await SqlAsync(path,"UPDATE rewind_session SET local_checkpoint_scan_id='missing-cut' WHERE status<>'resumed';");
        await SealAsync(path);
        var malformed=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        var refused=Path.Combine(root,"refused.db");
        try
        {
            await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,refused,malformed,options);
            throw new Exception("Missing Rewind cut was accepted.");
        }
        catch(InvalidDataException e)when(e.Message.Contains("Active Rewind requires",StringComparison.Ordinal))
        {Check(!File.Exists(refused),"missing original Rewind checkpoint refuses before successor creation");}
        await SqlAsync(path,"UPDATE rewind_session SET local_checkpoint_scan_id='"+cuts["local"][1]+"' WHERE status<>'resumed';");
        await SealAsync(path);var hash=Hash(path);
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=phase=>{if(phase=="after-table:rewind_preview_item")throw new Injected();};
        try
        {
            try {await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);throw new Exception("Missing interruption.");}
            catch(Injected){Check(true,"interruption occurs after exact retained Rewind preview");}
        }
        finally{SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=null;}
        var report=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);
        await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,target,identity,options);
        var successor=new SyncAuthorityDatabase(target);
        Check(expected==System.Text.Json.JsonSerializer.Serialize(await successor.LoadActiveRewindSessionAsync("pair")),
            "actual Rewind reader preserves checkpoint cursor, original reviewed paths and pending progress exactly");
        Check(report.Tables.Single(x=>x.Table=="rewind_session") is {SourceRows:2,SelectedRows:1,OriginalOnlyRows:1},
            "active session retained while resumed historical session stays source-only");
        Check(report.Tables.Single(x=>x.Table=="scan_generation") is {SourceRows:8,SelectedRows:6,OriginalOnlyRows:2},
            "original checkpoint and preview cuts survive alongside latest cuts without unrelated oldest history");
        foreach(var side in new[]{"local","dropbox"})
        {
            Check((await successor.LoadCompleteObservationAsync("pair",side,cuts[side][1])).Count==1&&
                (await successor.LoadCompleteObservationAsync("pair",side,cuts[side][2])).Count==1,
                side+" whole original checkpoint and reviewed preview remain readable");
            await RefuseAsync(()=>successor.LoadCompleteObservationAsync("pair",side,cuts[side][0]),
                side+" unrelated oldest cut is not imported");
        }
        Check((await successor.LoadActiveRewindPairIdsAsync()).SetEquals(new[]{"pair"}),
            "active Rewind still pauses its exact pair after restart");
        Check(Hash(path)==hash,"original Rewind source remains byte-exact");
    }

    internal static async Task RunHistoryDispositionAsync(string root)
    {
        if(Directory.Exists(root))throw new IOException("Use a fresh history-disposition fixture.");
        Directory.CreateDirectory(root);var path=Path.Combine(root,"source.db");var target=Path.Combine(root,"successor.db");
        var source=new SyncAuthorityDatabase(path);await source.InitializeAsync("history-installation","profile","fixture","{}");
        var command=await source.CreateAcceptedCommandAdmissionAsync(null,"run_all",null,null,null,"{}",SyncAuthorityCommandPurpose.NormalEffect);
        await source.TransitionCommandRequestAsync(command.CommandRequestId,"executing");
        await source.TransitionCommandRequestAsync(command.CommandRequestId,"completed");
        await SealAsync(path);var hash=Hash(path);var options=new SyncEssentialSuccessorOptions(root);
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        var report=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);
        var history=report.Tables.Single(x=>x.Table=="history_event");
        long payloadBytes=0;
        await using(var read=new SqliteConnection("Data Source="+path+";Mode=ReadOnly;Pooling=False"))
        {
            await read.OpenAsync();await using var query=read.CreateCommand();query.CommandText="SELECT detail_json FROM history_event;";
            await using var events=await query.ExecuteReaderAsync();
            while(await events.ReadAsync())payloadBytes+=System.Text.Encoding.UTF8.GetByteCount(events.GetString(0));
        }
        Check(history.SourceRows==2&&history.SelectedRows==0&&history.OriginalOnlyRows==2,
            "undetached closed-run history has exact original-only accounting rather than blocking the successor");
        Check(payloadBytes>0&&history.SourcePayloadBytes==payloadBytes&&history.SelectedPayloadBytes==0&&
            history.OriginalOnlyPayloadBytes==payloadBytes,"original-only history reports exact UTF8 payload bytes independently of database size");
        Check((await new SyncAuthorityDatabase(target).LoadActivityProjectionAsync()).Count==0,
            "omitted historical events are not recreated as current product activity");
        await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,target,identity,options);
        Check(Hash(path)==hash,"all original history bytes remain unchanged and independently available");
    }

    private sealed class Injected:Exception;
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static async Task SealAsync(string path)
    {
        await using var c=new SqliteConnection("Data Source="+path+";Pooling=False");await c.OpenAsync();
        await using var q=c.CreateCommand();q.CommandText="PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
        await q.ExecuteNonQueryAsync();
    }
    private static async Task SqlAsync(string path,string sql)
    {
        await using var c=new SqliteConnection("Data Source="+path+";Pooling=False");await c.OpenAsync();
        await using var q=c.CreateCommand();q.CommandText=sql;await q.ExecuteNonQueryAsync();
    }
    private static async Task RefuseAsync(Func<Task> action,string message)
    {
        try{await action();}
        catch(Exception e)when(e is InvalidDataException or InvalidOperationException or IOException or SqliteException)
        {Console.WriteLine("PASS "+message);return;}
        throw new InvalidDataException("Unexpected acceptance: "+message);
    }
    private static void Check(bool condition,string message)
    {if(!condition)throw new InvalidDataException(message);Console.WriteLine("PASS "+message);}

    internal static async Task RunObservationAsync(string root)
    {
        if(Directory.Exists(root))throw new IOException("Use a fresh observation successor fixture.");
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,"source.db");var target=Path.Combine(root,"successor.db");
        var db=new SyncAuthorityDatabase(path);await db.InitializeAsync("observed-installation","profile","fixture","{}");
        var volume=await db.ResolveVolumeIdAsync("observed-installation","c0ffee01","NTFS",null,false,false,true);
        await db.RegisterPairAsync("pair",1);
        await db.RegisterLocalRootBindingAsync("local","pair",1,"observed-installation",volume,"c0ffee01","00000000000000a1",root);
        SyncAuthorityObservationInput Entry(int n,string content)
        {
            var hash=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            return new("folder/file-"+n.ToString("D4")+".txt","file",
                SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File,0xc0ffee01,(ulong)(n+4096),
                    new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc),contentHash:hash,size:content.Length),
                hash,content.Length,System.Text.Json.JsonSerializer.Serialize(new{state="present",path="folder/file-"+n.ToString("D4")+".txt"}));
        }
        var original=Enumerable.Range(0,300).Select(n=>Entry(n,"old-"+n)).ToArray();
        var legacy=await db.BeginScanGenerationAsync("pair","local","local","initial","{}");
        await db.AppendObservationEntriesAsync(legacy,original,CancellationToken.None);
        await db.CompleteScanGenerationAsync(legacy);
        var baseline=(await db.LoadCompleteObservationAsync("pair","local",legacy.ScanGenerationId))
            .Single(x=>x.RelativePath=="folder/file-0001.txt");
        await db.AcceptSynchronizationPointAsync("older-point","pair",baseline.ObjectId,baseline.ObjectVersionId,null,baseline.MembershipId,null);
        await db.AcceptSynchronizationPointAsync("current-point","pair",baseline.ObjectId,baseline.ObjectVersionId,null,baseline.MembershipId,null);
        var originalPoints=System.Text.Json.JsonSerializer.Serialize(await db.LoadLatestSynchronizationPointsAsync("pair"));
        await SqlAsync(path,"UPDATE installation SET history_retention_enabled=1;");
        var previous=await db.BeginScanGenerationAsync("pair","local","local","monitor","{}");
        await db.PrepareCompactScanAsync(previous,original,Array.Empty<(string ObservationId,string EvidenceJson)>(),null,null);
        await db.CompleteScanGenerationAsync(previous);
        var currentEntries=Enumerable.Range(1,300).Select(n=>Entry(n,n<8?"changed-"+n:"old-"+n)).ToArray();
        var current=await db.BeginScanGenerationAsync("pair","local","local","monitor","{}");
        await db.PrepareCompactScanAsync(current,currentEntries,Array.Empty<(string ObservationId,string EvidenceJson)>(),null,null);
        await db.CompleteScanGenerationAsync(current);
        var incomplete=await db.BeginScanGenerationAsync("pair","local","local","overflow","{}");
        await db.AppendObservationEntriesAsync(incomplete,[Entry(500,"unverified")],CancellationToken.None);
        var before=System.Text.Json.JsonSerializer.Serialize(await db.LoadCompleteObservationAsync("pair","local",current.ScanGenerationId));
        await SqlAsync(path,"UPDATE current_copy_completion SET point_generation=999;");
        await SealAsync(path);
        var options=new SyncEssentialSuccessorOptions(root);
        var malformedIdentity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        try
        {
            await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(
                path,Path.Combine(root,"malformed-successor.db"),malformedIdentity,options);
            throw new Exception("Malformed current-copy point was accepted.");
        }
        catch(InvalidDataException e)when(e.Message.Contains("does not match its exact accepted point",StringComparison.Ordinal))
        {Check(!File.Exists(Path.Combine(root,"malformed-successor.db")),"mismatched empty-copy tuple refuses before target creation");}
        await SqlAsync(path,"UPDATE current_copy_completion SET point_generation=2;");
        await SealAsync(path);
        var sourceIdentity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);var beforeHash=Hash(path);
        var result=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,sourceIdentity,options);
        Check(result.Complete,"whole observation successor prepared");
        Check(result.Tables.Single(x=>x.Table=="scan_generation") is{SourceRows:4,SelectedRows:2,OriginalOnlyRows:2},
            "only latest complete and latest incomplete scans retained");
        Check(result.Tables.Single(x=>x.Table=="observation").TargetRows==301,
            "whole300-row compact cut and one incomplete row materialised");
        Check(result.Tables.All(x=>x.SourceRows==x.SelectedRows+x.OriginalOnlyRows),
            "transformed compact storage has exact source disposition accounting");
        await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,target,sourceIdentity,options);
        Check(Hash(path)==beforeHash,"observation source bytes unchanged");
        var successor=new SyncAuthorityDatabase(target);
        var after=System.Text.Json.JsonSerializer.Serialize(
            await successor.LoadCompleteObservationAsync("pair","local",current.ScanGenerationId));
        Check(before==after,"actual production complete-observation reader matches every imported member/version/path");
        Check(originalPoints==System.Text.Json.JsonSerializer.Serialize(await successor.LoadLatestSynchronizationPointsAsync("pair")),
            "latest baseline and its older exact version survive independently of current observations");
        Check(result.Tables.Single(x=>x.Table=="synchronization_point") is{SourceRows:2,SelectedRows:1,OriginalOnlyRows:1},
            "obsolete accepted point remains source-only; current independent baseline retained");
        Check(result.Tables.Single(x=>x.Table=="object_version").TargetRows==302,
            "referenced older baseline version retained alongside current and incomplete observation versions");
        await RefuseAsync(()=>successor.LoadCompleteObservationAsync("pair","local",incomplete.ScanGenerationId),
            "incomplete imported scan cannot become complete authority");
        await RefuseAsync(()=>successor.LoadCompleteObservationAsync("pair","local",legacy.ScanGenerationId),
            "source-only historical cut is not invented in successor");
        var next=await successor.BeginScanGenerationAsync("pair","local","local","monitor","{}");
        await successor.PrepareCompactScanAsync(next,currentEntries,Array.Empty<(string ObservationId,string EvidenceJson)>(),null,null);
        await successor.CompleteScanGenerationAsync(next);
        Check(next.Generation>incomplete.Generation,"new scan generation advances beyond retained incomplete head");
        Check((await successor.LoadCompleteObservationAsync("pair","local",next.ScanGenerationId)).Count==300,
            "real compact writer continues from imported legacy encoding");
        await successor.VerifyHealthAsync();
        Console.WriteLine("PASS whole observation successor integrity and real reader/writer continuation");
    }

    internal static async Task RunLegacyWalAsync(string root)
    {
        if(Directory.Exists(root))throw new IOException("Use a fresh legacy successor fixture.");
        Directory.CreateDirectory(root);
        var seedRoot=Path.Combine(root,"seed");await RunObservationAsync(seedRoot);
        var seed=Path.Combine(seedRoot,"source.db");var legacy=Path.Combine(root,"legacy7.db");
        await SeedMonitorAsync(new SyncAuthorityDatabase(seed),"local");
        await SealAsync(seed);
        await using(var c=new SqliteConnection("Data Source="+legacy+";Pooling=False"))
        {
            await c.OpenAsync();
            await using var q=c.CreateCommand();
            q.CommandText="PRAGMA foreign_keys=ON;"+SyncAuthoritySchema.Sql+SyncAuthoritySchemaSupplement.Sql+
                SyncAuthoritySchemaMigration3.Sql+SyncAuthoritySchemaMigration4.Sql+
                SyncAuthoritySchemaMigration5.Sql+SyncAuthoritySchemaMigration6.Sql+SyncAuthoritySchemaMigration7.Sql+
                "INSERT INTO schema_migration VALUES(7,'2026-01-01T00:00:00Z','synthetic-legacy'); PRAGMA user_version=7;";
            await q.ExecuteNonQueryAsync();
            await using var input=new SqliteConnection("Data Source="+seed+";Mode=ReadOnly;Pooling=False");
            await input.OpenAsync();
            // Fixed synthetic predecessor input, explicit original-v7 columns.
            // This is fixture authoring, not an importer implementation shortcut.
            var tables=new(string Table,string Columns,string Source)[]
            {
                ("installation","installation_id,database_generation,created_utc","installation"),
                ("capability_profile","capability_profile_id,source_revision,profile_json,created_utc","capability_profile"),
                ("sync_pair","pair_id,configuration_revision,state,created_utc,pair_json,selected","sync_pair"),
                ("pair_config_revision","pair_id,revision,created_utc,created_by,deletion_policy,deletion_grace_seconds,required_absence_observations,conflict_policy,metadata_policy,recursive_mode,automatic_pruning,recovery_retention_days","pair_config_revision"),
                ("volume","volume_id,installation_id,volume_serial_hex,filesystem,volume_guid_path,first_seen_utc,last_seen_utc,supports_file_id_128,supports_usn,supports_atomic_rename,status","volume"),
                ("root_binding","root_binding_id,pair_id,side,generation,identity_json,display_path,state,observed_utc","root_binding"),
                ("logical_object","object_id,object_kind,created_utc","logical_object"),
                ("object_version","object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc","object_version"),
                ("membership","membership_id,object_id,root_binding_id,relative_path_key,generation,state,observed_utc","membership"),
                ("scan_generation","scan_generation_id,pair_id,root_binding_id,side,generation,trigger_kind,scope_json,started_utc,completed_utc,status,entry_count","scan_generation"),
                ("observation","observation_id,scan_generation_id,root_binding_id,scan_generation,complete_generation,object_version_id,membership_id,evidence_json,observed_utc","effective_observation"),
                ("synchronization_point","synchronization_point_id,pair_id,object_id,local_object_version_id,dropbox_object_version_id,local_membership_id,dropbox_membership_id,accepted_utc,authority_generation","synchronization_point"),
                ("monitor_cursor","monitor_cursor_id,pair_id,root_binding_id,kind,generation,cursor_value,source_identity_json,status,updated_utc","monitor_cursor"),
                ("monitor_event","monitor_event_id,monitor_cursor_id,pair_id,source_event_key,normalized_path_key,object_identity_json,event_kind,event_json,received_utc,consumed_utc","monitor_event"),
                ("dirty_item","dirty_item_id,pair_id,object_id,side,normalized_path_key,reconciliation_level,earliest_event_utc,latest_event_utc,event_count,planner_input_digest,status,lease_id","dirty_item")
            };
            await using var tx=c.BeginTransaction();
            foreach(var table in tables)
            {
                await using var read=input.CreateCommand();read.CommandText=$"SELECT {table.Columns} FROM {table.Source};";
                await using var rows=await read.ExecuteReaderAsync();
                while(await rows.ReadAsync())
                {
                    await using var write=c.CreateCommand();write.Transaction=tx;
                    write.CommandText=$"INSERT INTO {table.Table}({table.Columns}) VALUES("+
                        string.Join(",",Enumerable.Range(0,rows.FieldCount).Select(i=>"$v"+i))+");";
                    for(var i=0;i<rows.FieldCount;i++)write.Parameters.AddWithValue("$v"+i,rows.GetValue(i));
                    await write.ExecuteNonQueryAsync();
                }
            }
            await tx.CommitAsync();
        }
        // Retain an exact zero-length WAL and existing32KiB SHM while the
        // fixture writer closes. The importer must never write either file.
        FileStream? walPin=null,shmPin=null;
        try
        {
            await using(var c=new SqliteConnection("Data Source="+legacy+";Pooling=False"))
            {
                await c.OpenAsync();await using var q=c.CreateCommand();
                q.CommandText="PRAGMA journal_mode=WAL; UPDATE installation SET created_utc=created_utc; PRAGMA wal_checkpoint(TRUNCATE);";
                await q.ExecuteNonQueryAsync();
                walPin=File.Open(legacy+"-wal",FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                shmPin=File.Open(legacy+"-shm",FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            }
        }
        finally{walPin?.Dispose();shmPin?.Dispose();}
        Check(new FileInfo(legacy+"-wal").Length==0 && new FileInfo(legacy+"-shm").Length==32768,
            "synthetic schema7 input retains the observed zero-WAL snapshot shape");
        var hashes=new[]{Hash(legacy),Hash(legacy+"-wal"),Hash(legacy+"-shm")};
        var options=new SyncEssentialSuccessorOptions(root);
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(legacy,options);
        Check(identity.SchemaVersion==7 && identity.SidecarBindingSha256.Length==64,
            "read-only source inspection binds schema7 and exact guarded sidecars");
        var target=Path.Combine(root,"successor.db");
        var result=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(legacy,target,identity,options);
        await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(legacy,target,identity,options);
        Check(result.Complete && result.Tables.Count==62,
            "all51 legacy and11 new table dispositions verified");
        Check(result.Tables.Single(x=>x.Table=="observation") is{SourceRows:901,SelectedRows:301,OriginalOnlyRows:600},
            "legacy essential successor retains301 rows and leaves600 historical observations in source");
        Check(hashes.SequenceEqual(new[]{Hash(legacy),Hash(legacy+"-wal"),Hash(legacy+"-shm")}),
            "source main, zero-WAL and SHM bytes unchanged after prepare and verify");
        var output=new SyncAuthorityDatabase(target);await output.VerifyHealthAsync();
        Check((await output.LoadLatestCompleteObservationAsync("pair","local")).Count==300,
            "real production reader sees whole legacy imported inventory");
        Check((await output.LoadLatestSynchronizationPointsAsync("pair")).Count==1,
            "real production reader sees the exact latest legacy baseline");
        Check((await output.LoadMonitorCursorAsync("cursor")) is{CursorValue:"c2",Status:"unavailable"}&&
            (await output.LoadMonitorProjectionAsync()).All(x=>x.PendingDirtyItems==2),
            "legacy cursor and exact outstanding monitor uncertainty preserved");
        Console.WriteLine("PASS schema7 plus guarded zero-WAL successor proof complete");
    }




    internal static async Task RunJournalAsync(string root,bool retainOlderCuts=true,bool dispatchOnly=false,bool includeOwnership=false,bool includeSafety=false,bool includePoints=false,bool includeRewind=false,bool includeResolution=false,bool includeDecisions=false,bool includeLegacyLineage=false)
    {
        includeSafety|=includeResolution||includeDecisions;
        if(Directory.Exists(root))throw new IOException("Use a fresh journal successor fixture.");
        Directory.CreateDirectory(root);var path=Path.Combine(root,"source.db");var target=Path.Combine(root,"successor.db");
        var db=new SyncAuthorityDatabase(path);await db.InitializeAsync("journal-installation","profile","fixture","{}");
        await db.RegisterPairAsync("pair",1);
        var scans=new Dictionary<string,string>();
        foreach(var side in new[]{"local","dropbox"})
        {
            await db.RegisterRootBindingAsync(side+"-root","pair",side,1,
                side=="local"?"""{"volumeSerial":"00000001","fileId":"root"}""":"""{"folderId":"id:root"}""",root);
            var scan=await db.BeginScanGenerationAsync("pair",side+"-root",side,"manual","{}");
            await db.AppendObservationEntriesAsync(scan,[new("file.txt","file",
                side=="local"?"""{"volumeSerial":"00000001","fileId":"file","lastWriteUtc":"2026-01-01T00:00:00Z"}""":
                    """{"fileId":"id:file","revision":"1"}""",new string('a',64),4,"{}")]);
            await db.CompleteScanGenerationAsync(scan);scans[side]=scan.ScanGenerationId;
        }
        var set=await db.LoadPlanningObservationSetAsync("pair",1,"local-root","dropbox-root",scans["local"],scans["dropbox"]);
        var local=set.Local.Single();var remote=set.Dropbox.Single();
        if(includePoints)
        {
            await db.AcceptSynchronizationPointAsync("unneeded-point","pair",local.ObjectId,local.ObjectVersionId,null,local.MembershipId,null);
            await db.AcceptSynchronizationPointAsync("journal-point","pair",local.ObjectId,local.ObjectVersionId,null,local.MembershipId,null);
            set=await db.LoadPlanningObservationSetAsync("pair",1,"local-root","dropbox-root",scans["local"],scans["dropbox"]);
        }
        string? legacyParent=null;string? legacyPayload=null;string? lineageProbeRun=null;
        SyncBootstrapActionPlan? legacyPlan=null;
        if(includeLegacyLineage)
        {
            var choices=new List<SyncBootstrapActionChoice>{new("file.txt","Local changed since sync","Upload")};
            choices.AddRange(Enumerable.Range(1,4096).Select(i=>new SyncBootstrapActionChoice(
                "unissued-"+i+"-"+new string('x',220)+".txt","Local changed since sync","Upload")));
            legacyPlan=new("pair",scans["local"],scans["dropbox"],choices);
            legacyPayload=JsonSerializer.Serialize(new{reviewedPlan=legacyPlan,policyRevision=1,
                retryOfRunId=(string?)null,pairIds=new[]{"pair"},pairNames=new[]{"Fixture"},trigger="Reviewed first sync",
                connectionId="fixture",selected=choices.Count,skipped=0});
            var origin=await db.CreateAcceptedCommandAdmissionAsync(null,"run_pair","pair",null,null,legacyPayload,SyncAuthorityCommandPurpose.NormalEffect);
            legacyParent=origin.CommandRequestId;
            await db.TransitionCommandRequestAsync(legacyParent,"executing");
            await db.TransitionCommandRequestAsync(legacyParent,"completed",JsonSerializer.Serialize(new{
                pairId="pair",files=new[]{new SyncDecision(legacyPlan.Choices[1].RelativePath,SyncDecisionKind.Identical,
                    "Retained original completion without a file effect")}}));
        }
        var admission=await db.CreateAcceptedCommandAdmissionAsync(null,"run_pair","pair",null,null,
            legacyParent is null?"{}":JsonSerializer.Serialize(new{retryOfRunId=legacyParent,policyRevision=1,
                pairIds=new[]{"pair"},reviewedConsent=new SyncReviewedConsentReference(legacyParent,
                    SyncReviewedConsentReference.LowercaseRequestSha256(legacyPayload!),legacyPlan!.Choices.Count,
                    SyncReviewedConsentReference.EncodeSelection(legacyPlan.Choices,legacyPlan.Choices))}),SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId,"executing");
        if(includeRewind)await db.RegisterOperationGroupAsync("rewind-group","compound","{}");
        if(includeLegacyLineage)await db.RegisterOperationGroupAsync("legacy-reviewed-group","compound",
            JsonSerializer.Serialize(new{purpose="reviewed_first_sync",runId=admission.CommandRequestId,pairId="pair"}));
        var ticket=await db.ReserveActionAsync("unknown-intent",admission);
        var plan=new SyncAuthorityOperationPlan(ticket.OperationId,"unknown-intent",SyncAuthorityOperationKind.UpdateRemote,
            "pair",local.ObjectId,1,"profile","{}",50,
            [new(ticket.OperationId+"-source","pair",local.ObjectId,"source","local-root",local.MembershipId,
                local.ObjectVersionId,local.MembershipGeneration,"file.txt","{}"),
             new(ticket.OperationId+"-destination","pair",remote.ObjectId,"destination","dropbox-root",remote.MembershipId,
                remote.ObjectVersionId,remote.MembershipGeneration,"file.txt","{}")],[],[],
            OperationGroupId:includeRewind?"rewind-group":includeLegacyLineage?"legacy-reviewed-group":null,
            PlanningAuthorities:[set.Bind(SyncAuthorityReconciliationPlanner.ObservationSetIdentity(set),local.ObjectId)]);
        await db.CreateOperationAsync(plan,admission,reservation:ticket.Reservation);
        if(includeRewind)await db.MarkOperationGroupActiveAsync("rewind-group");
        if(includeLegacyLineage)await db.MarkOperationGroupActiveAsync("legacy-reviewed-group");
        var oldLease=await db.AcquireOperationLeaseAsync(ticket.OperationId,"source-worker",TimeSpan.FromMinutes(30));
        if(includeOwnership)
        {
            await db.AuthorizeNamespaceAllocationAsync(new("namespace",ticket.OperationId,"local","fixture-parent",
                ".sync-control",new string('a',64)),oldLease);
            foreach(var state in new[]{SyncAuthorityAllocationStatus.Creating,SyncAuthorityAllocationStatus.Created,
                SyncAuthorityAllocationStatus.Bound,SyncAuthorityAllocationStatus.Verified})
                await db.TransitionNamespaceAllocationAsync("namespace",oldLease,state,"fixture-control");
            await db.RegisterControlNamespaceAsync(new("control","namespace","local","{\"identity\":\"fixture-control\"}",
                "{\"marker\":\"fixture-marker\"}",new string('a',64),"active"));
            await db.BindPairControlNamespaceAsync("pair","local","control",1);
            await db.RegisterRecoveryArtifactAsync("recovery",ticket.OperationId,local.ObjectId,null,"local",
                "{\"originalPath\":\"file.txt\",\"identity\":\"fixture-recovery\"}",new string('a',64),4);
            await db.RegisterProviderAccountAsync("account","journal-installation","fixture-account","fixture-root",
                "fixture-root","Fixture",1);
            await db.RegisterDropboxRecoveryRootAsync("recovery-root","journal-installation","account","/Recovery",
                "id:recovery","fixture-root",1);
            // Supply an opaque retained corpus as fixture input. Its physical
            // verification is outside this offline copy test.
            await SqlAsync(path,"UPDATE recovery_root_binding SET provider_corpus_id='fixture-corpus' WHERE recovery_root_binding_id='recovery-root';");
        }
        var attempt=await db.StartDispatchedAttemptAsync(ticket.OperationId,oldLease,new string('b',64),"fixture-only-no-provider");
        await db.RecordReceiptAsync(attempt.AttemptId,oldLease,SyncAuthorityReceiptCertainty.Unknown,"{}","{}","{}",new string('c',64));
        SyncRewindSession? rewind=null;
        if(includeRewind)
        {
            rewind=new SyncRewindSession("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb01","connection","pair","Rewind","/Rewind",
                scans["local"],scans["dropbox"],"original-cursor",DateTimeOffset.UtcNow,SyncRewindStatus.EffectUnknown,
                scans["local"],scans["dropbox"],[new("file.txt","Reviewed","Replace local",true,false,"Original review.")],
                "Uncertain actual dispatch","rewind-group",[new("file.txt",SyncRewindPathStatus.EffectUnknown,
                    ticket.OperationId,"Original uncertain operation",DateTimeOffset.UtcNow)]);
            await db.PublishRewindSessionAsync("journal-installation",rewind);
            rewind=await db.LoadActiveRewindSessionAsync("pair");
        }
        if(dispatchOnly)
        {
            await db.ReleaseLeaseAsync(oldLease);
            var reconciliation=await db.AcquireReconciliationOperationLeaseAsync(ticket.OperationId,"reconciler",TimeSpan.FromMinutes(1));
            await db.RecordReceiptAsync(attempt.AttemptId,reconciliation,SyncAuthorityReceiptCertainty.KnownNotApplied,
                "{}","{}","{}",new string('d',64));
            await db.ReconcileAttemptAsync(attempt.AttemptId,reconciliation,SyncAuthorityAttemptStatus.VerifiedNotApplied,"{}");
            await db.ReleaseLeaseAsync(reconciliation);
            reconciliation=await db.AcquireReconciliationOperationLeaseAsync(ticket.OperationId,"next-reconciler",TimeSpan.FromMinutes(1));
            try{await db.StartDispatchedAttemptAsync(ticket.OperationId,reconciliation,new string('e',64),"forbidden");}
            catch(Exception error)when(error is InvalidOperationException or SqliteException)
            {
                Console.WriteLine("OBSERVED dispatch rejection: "+error.GetType().Name+": "+error.Message);
                Check(error.Message.Contains("dispatch authority",StringComparison.Ordinal),
                    "current planning refuses reconciliation lease at the dispatch authority boundary");
                await db.ReleaseLeaseAsync(reconciliation);
                var ordinary=await db.AcquireOperationLeaseAsync(ticket.OperationId,"ordinary-retry",TimeSpan.FromMinutes(1));
                var retry=await db.StartDispatchedAttemptAsync(ticket.OperationId,ordinary,new string('f',64),"fixture-only-no-provider");
                Check(retry.AttemptId!=attempt.AttemptId,"the unchanged current command can still retry with an ordinary lease");
                return;
            }
            throw new InvalidDataException("Reconciliation lease unexpectedly started a new effect.");
        }
        if(includePoints)
            await db.AcceptSynchronizationPointAsync("current-point","pair",local.ObjectId,local.ObjectVersionId,null,local.MembershipId,null);
        var absenceCuts=new Dictionary<string,string>();
        for(var n=0;n<(retainOlderCuts?2:0);n++)foreach(var side in new[]{"local","dropbox"})
        {
            var scan=await db.BeginScanGenerationAsync("pair",side+"-root",side,"manual","{}");
            await db.CompleteScanGenerationAsync(scan);
            if(n==0)absenceCuts[side]=scan.ScanGenerationId;
        }
        SyncAuthorityTombstoneObservation? originalTombstone=null;
        if(includeSafety)
        {
            await db.RecordConflictAsync("conflict","pair",local.ObjectId,"both sides changed",local.ObjectVersionId,
                local.MembershipId,remote.ObjectVersionId,remote.MembershipId);
            originalTombstone=await db.RecordTombstoneObservationAsync("tombstone","pair",local.ObjectId,"local",
                local.MembershipId,local.ObjectVersionId,absenceCuts["local"],absenceCuts["dropbox"]);
            if(includeResolution)
                await db.RegisterConflictResolutionAsync("resolution","conflict",1,SyncAuthorityConflictResolutionKind.KeepLocal,"resolution-group");
        }
        if(includeDecisions)
        {
            async Task Decision(string id,string kind,string objectId,string? operationId=null,string? conflictId=null,string? tombstoneId=null)
            {
                var json=System.Text.Json.JsonSerializer.Serialize(new {path="file.txt",kind,
                    operationId,conflictId,tombstoneId,reason="Original "+id+" decision."});
                await db.RecordReconciliationDecisionAsync(id,"pair",objectId,scans["local"],scans["dropbox"],null,
                    kind.ToLowerInvariant(),json);
            }
            await Decision("pending-operation","Operation",local.ObjectId,operationId:ticket.OperationId);
            await Decision("pending-conflict","Conflict",local.ObjectId,conflictId:"conflict");
            await Decision("pending-tombstone","Tombstone",local.ObjectId,tombstoneId:"tombstone");
            await Decision("historical-unchanged","Unchanged",local.ObjectId);
            await Decision("current-blocker","Blocked",remote.ObjectId);
            var legacy="""{"path":"old.txt","reason":"Old imported display","legacyOutcome":"Conflict","migrated":true}""";
            await db.ImportLegacyHistoryBatchAsync([new("historical-legacy","pair","conflict",legacy,
                DateTimeOffset.UtcNow.ToString("O"),System.Text.Encoding.UTF8.GetByteCount(legacy)+64)],default);
        }
        var originalConflict=includeSafety?await db.LoadConflictAsync("conflict"):null;
        var original=await db.LoadOperationAsync(ticket.OperationId);
        if(includeLegacyLineage)
        {
            var bad=await db.CreateAcceptedCommandAdmissionAsync(null,"run_pair","pair",null,null,
                """{"retryOfRunId":"missing-original-approval"}""",SyncAuthorityCommandPurpose.NormalEffect);
            lineageProbeRun=bad.CommandRequestId;
            await SealAsync(path);var badOptions=new SyncEssentialSuccessorOptions(root);
            var badIdentity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,badOptions);
            var badTarget=Path.Combine(root,"missing-ancestor.db");var refused=false;
            try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,badTarget,badIdentity,badOptions);}
            catch(InvalidDataException e)when(e.Message.Contains("missing or non-run approval ancestor",StringComparison.Ordinal)){refused=true;}
            Check(refused && !File.Exists(badTarget),"missing reviewed ancestor refuses at its exact pre-target lineage gate");
            await db.TransitionCommandRequestAsync(bad.CommandRequestId,"cancelled","{}");
        }
        var originalActivity=includeDecisions
            ?await db.LoadActivityProjectionPageAsync(query:new(DetailedLog:true,OldestFirst:true))
            :await db.LoadActivityProjectionAsync();
        if(lineageProbeRun is not null)
        {
            Check(originalActivity.Count(x=>x.RunId==lineageProbeRun)==1,
                "cancelled negative-test request contributes one exact source-only activity row");
            originalActivity=originalActivity.Where(x=>x.RunId!=lineageProbeRun).ToArray();
        }
        if(includeDecisions)originalActivity=originalActivity.Where(x=>x.EventId is not
            ("decision:historical-unchanged" or "decision:historical-legacy")).ToArray();
        var originalRecovery=await db.LoadRecoveryArtifactsAsync();
        var originalControl=await db.LoadPairControlNamespacePreimageAsync("pair","local");
        var originalRecoveryRoot=includeOwnership?await db.FindCloudRecoveryRootAsync("journal-installation",SyncProviders.Dropbox,
            "account","fixture-root","id:recovery","fixture-corpus"):null;
        if(includeOwnership)Check(originalRecoveryRoot is not null,"source typed recovery reader resolves its exact retained corpus");
        await SealAsync(path);var sourceHash=Hash(path);
        var options=new SyncEssentialSuccessorOptions(root);
        if(includeLegacyLineage)
        {
            var badPath=Path.Combine(root,"wrong-approval-fingerprint.db");File.Copy(path,badPath);
            var bad=new SyncAuthorityDatabase(badPath);
            var badRequest=JsonSerializer.Serialize(new{retryOfRunId=legacyParent,policyRevision=1,pairIds=new[]{"pair"},
                reviewedConsent=new SyncReviewedConsentReference(legacyParent!,new string('0',64),legacyPlan!.Choices.Count,
                    SyncReviewedConsentReference.EncodeSelection(legacyPlan.Choices,legacyPlan.Choices))});
            await bad.CreateAcceptedCommandAdmissionAsync(null,"run_pair","pair",null,null,badRequest,SyncAuthorityCommandPurpose.NormalEffect);
            await SealAsync(badPath);
            var badIdentity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(badPath,options);
            var badTarget=Path.Combine(root,"wrong-approval-successor.db");var refused=false;
            try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(badPath,badTarget,badIdentity,options);}
            catch(InvalidDataException e)when(e.Message.Contains("exact original approval bytes",StringComparison.Ordinal)){refused=true;}
            Check(refused&&!File.Exists(badTarget),"changed retry approval fingerprint refuses before target creation");
        }
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        var interrupted=false;
        SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=phase=>
        {if(phase=="journal-row-permit-installed")throw new Injected();};
        try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);}
        catch(Injected){interrupted=true;}
        finally{SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=null;}
        Check(interrupted,"journal import fault rolls back protected row and temporary guard together");
        async Task CheckGuards()
        {
            // A read-only WAL connection leaves empty sidecars behind. Open the
            // isolated target with cleanup capability, then prohibit SQL writes.
            await using var c=new SqliteConnection("Data Source="+target+";Mode=ReadWrite;Pooling=False");await c.OpenAsync();
            await using(var configure=c.CreateCommand())
            {configure.CommandText="PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;";await configure.ExecuteNonQueryAsync();}
            await using var q=c.CreateCommand();q.CommandText="SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND sql LIKE '%syncshark_successor_row_allowed%';";
            Check(Convert.ToInt64(await q.ExecuteScalarAsync())==0,"no private import exception persists outside its transaction");
        }
        await CheckGuards();
        var report=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);
        await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,target,identity,options);
        await CheckGuards();
        var output=new SyncAuthorityDatabase(target);
        if(includeLegacyLineage)
        {
            var retainedParent=await output.LoadRunByIdAsync(legacyParent!);
            Check(retainedParent is not null && retainedParent.RequestJson.Length<4096 &&
                System.Text.Encoding.UTF8.GetByteCount(legacyPayload!)>1_048_576,
                "oversize original approval becomes a bounded compact command");
            var consentId=SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(legacyParent!);
            var header=await output.LoadConsentHeaderAsync(consentId);
            Check(header is not null && header.OriginalChoiceCount==legacyPlan!.Choices.Count &&
                header.OriginalRequestSha256==SyncReviewedConsentReference.LowercaseRequestSha256(legacyPayload!),
                "normalized original retains exact raw approval fingerprint and complete choice count");
            var items=await output.LoadConsentItemsAsync(consentId,[0,1,4096],SyncAuthorityConsentSelectionMode.ExactRead);
            Check(items[0].Lifecycle==SyncAuthorityConsentItemLifecycle.Issued &&
                items[1].Lifecycle==SyncAuthorityConsentItemLifecycle.Completed &&
                items[2].Lifecycle==SyncAuthorityConsentItemLifecycle.Eligible && items[2].Ordinal==4096 && items[2].RelativePath==legacyPlan!.Choices[4096].RelativePath,
                "issued, summary-completed and unissued choices preserve exact original ordinals across bounded batches");
            var retainedChild=await output.LoadRunByIdAsync(admission.CommandRequestId);
            using var childRequest=JsonDocument.Parse(retainedChild!.RequestJson);
            var reference=SyncReviewedConsentReference.ReadRequired(childRequest.RootElement.GetProperty("reviewedConsent"));
            Check(reference.CommandId==legacyParent && reference.ConsentId==consentId &&
                reference.RequestSha256==SyncReviewedConsentReference.LowercaseRequestSha256(retainedParent!.RequestJson),
                "compact retry binds the new root payload while header retains the original approval hash");
            Check(report.Tables.Single(x=>x.Table=="consent_header") is {SourceRows:0,TargetRows:1} &&
                report.Tables.Single(x=>x.Table=="consent_item").TargetRows==legacyPlan!.Choices.Count,
                "receipt separates newly derived consent rows from physical source rows");
            await using var consentRead=new SqliteConnection("Data Source="+target+";Mode=ReadOnly;Pooling=False");
            await consentRead.OpenAsync();await using var consentQuery=consentRead.CreateCommand();
            consentQuery.CommandText="SELECT consent_id FROM operation WHERE operation_id=$id;";
            consentQuery.Parameters.AddWithValue("$id",ticket.OperationId);
            Check((string?)await consentQuery.ExecuteScalarAsync()==consentId,
                "retained uncertain operation pins its exact issued original choice");
        }
        if(includeRewind)
        {
            Check(System.Text.Json.JsonSerializer.Serialize(await output.LoadActiveRewindSessionAsync("pair"))==
                System.Text.Json.JsonSerializer.Serialize(rewind),
                "active uncertain Rewind preserves nonnull original operation and group references exactly");
            await RefuseAsync(()=>output.CompleteOperationGroupAsync("rewind-group"),
                "retained active Rewind group cannot complete across its unresolved effect");
        }
        var retained=await output.LoadOperationAsync(ticket.OperationId);
        if(includePoints)
        {
            var points=report.Tables.Single(x=>x.Table=="synchronization_point");
            Check(points.SourceRows==3 && points.SelectedRows==2 && points.OriginalOnlyRows==1,
                "unknown journal retains its exact older baseline plus current point without unrelated history");
            await using var c=new SqliteConnection("Data Source="+target+";Mode=ReadOnly;Pooling=False");await c.OpenAsync();
            await using var q=c.CreateCommand();q.CommandText="SELECT group_concat(synchronization_point_id,',') FROM (SELECT synchronization_point_id FROM synchronization_point ORDER BY authority_generation);";
            Check((string?)await q.ExecuteScalarAsync()=="journal-point,current-point",
                "historical point identity and order are preserved exactly");
            q.CommandText="SELECT synchronization_point_id FROM current_synchronization_point;";
            Check((string?)await q.ExecuteScalarAsync()=="current-point",
                "retaining an old referenced point cannot replace the current baseline");
        }
        var targetActivity=includeDecisions
            ?await output.LoadActivityProjectionPageAsync(query:new(DetailedLog:true,OldestFirst:true))
            :await output.LoadActivityProjectionAsync();
        if(lineageProbeRun is not null)
            Check(targetActivity.All(x=>x.RunId!=lineageProbeRun) &&
                report.Tables.Single(x=>x.Table=="history_event") is{SourceRows:4,SelectedRows:3,OriginalOnlyRows:1},
                "only the exact cancelled test request stays source-only; original reviewed activity remains selected");
        Check(System.Text.Json.JsonSerializer.Serialize(targetActivity)==
            System.Text.Json.JsonSerializer.Serialize(originalActivity),"selected pending activity and decisions preserve the real presentation reader");
        if(includeDecisions)
        {
            var selected=report.Tables.Single(x=>x.Table=="reconciliation_decision");
            Check(selected is {SourceRows:6,SelectedRows:4,OriginalOnlyRows:2},
                "only operationally referenced decisions and latest blocker survive; recognized unneeded history stays original-only");
            Check(selected.SourcePayloadBytes==selected.SelectedPayloadBytes+selected.OriginalOnlyPayloadBytes&&
                selected.SelectedPayloadBytes>0&&selected.OriginalOnlyPayloadBytes>0,
                "selected and original-only decision JSON byte counts reconcile exactly");
            var badPath=Path.Combine(root,"foreign-decision-cut-source.db");File.Copy(path,badPath);
            await SqlAsync(badPath,"UPDATE reconciliation_decision SET local_scan_generation_id='"+scans["dropbox"]+"' WHERE reconciliation_decision_id='pending-operation';");
            await SealAsync(badPath);
            var badIdentity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(badPath,options);
            var badTarget=Path.Combine(root,"foreign-decision-cut-successor.db");
            await RefuseAsync(()=>SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(badPath,badTarget,badIdentity,options),
                "selected decision cannot substitute the other side's complete cut");
            Check(!File.Exists(badTarget),"invalid decision cut refuses before successor creation");
        }
        if(includeOwnership)
        {
            Check(System.Text.Json.JsonSerializer.Serialize(await output.LoadRecoveryArtifactsAsync())==
                System.Text.Json.JsonSerializer.Serialize(originalRecovery),"retained recovery catalog reads back without claiming physical verification");
            Check(System.Text.Json.JsonSerializer.Serialize(await output.LoadPairControlNamespacePreimageAsync("pair","local"))==
                System.Text.Json.JsonSerializer.Serialize(originalControl),"control ownership and exact marker binding survive through the actual reader");
            Check(report.Tables.Single(x=>x.Table=="recovery_root_binding").SelectedRows==1,
                "provider-qualified recovery root and account reference retained");
            Check(System.Text.Json.JsonSerializer.Serialize(await output.FindCloudRecoveryRootAsync("journal-installation",
                SyncProviders.Dropbox,"account","fixture-root","id:recovery","fixture-corpus"))==
                System.Text.Json.JsonSerializer.Serialize(originalRecoveryRoot),"typed recovery lookup preserves exact account, root and corpus");
            Check(await output.FindCloudRecoveryRootAsync("journal-installation",SyncProviders.Dropbox,"account",
                "fixture-root","id:recovery","wrong-corpus") is null,"foreign corpus cannot resolve retained recovery ownership");
            await RefuseAsync(()=>output.TransitionNamespaceAllocationAsync("namespace",oldLease,SyncAuthorityAllocationStatus.Released),
                "source namespace lease cannot release retained target ownership");
        }
        Check(System.Text.Json.JsonSerializer.Serialize(retained)==System.Text.Json.JsonSerializer.Serialize(original),
            "retained unknown operation and exact request/receipt/member state read back");
        var cutRows=report.Tables.Single(x=>x.Table=="scan_generation");
        Check((await output.LoadCompleteObservationAsync("pair","local",scans["local"])).Count==1&&
            cutRows.SourceRows==(retainOlderCuts?6:2)&&cutRows.SelectedRows==(includeSafety?6:retainOlderCuts?4:2)&&
            cutRows.OriginalOnlyRows==(includeSafety?0:retainOlderCuts?2:0),
            "whole older planning cuts survive independently of latest empty inventories");
        if(includeSafety)
        {
            Check(System.Text.Json.JsonSerializer.Serialize(await output.LoadConflictAsync("conflict"))==
                System.Text.Json.JsonSerializer.Serialize(originalConflict),"open conflict preserves exact original versions and memberships");
            var replay=await output.RecordTombstoneObservationAsync("tombstone","pair",local.ObjectId,"local",
                local.MembershipId,local.ObjectVersionId,absenceCuts["local"],absenceCuts["dropbox"]);
            Check(System.Text.Json.JsonSerializer.Serialize(replay)==System.Text.Json.JsonSerializer.Serialize(originalTombstone),
                "replaying retained complete absence cuts cannot count another absence");
        }
        if(includeResolution)
        {
            Check(report.Tables.Single(x=>x.Table=="conflict_resolution").SelectedRows==1&&
                report.Tables.Single(x=>x.Table=="operation_group").SelectedRows==1,
                "unresolved original conflict choice and its empty planned group survive before action admission");
            await output.RegisterConflictResolutionAsync("resolution","conflict",1,SyncAuthorityConflictResolutionKind.KeepLocal,"resolution-group");
            await RefuseAsync(()=>output.RegisterConflictResolutionAsync("stale-resolution","conflict",2,
                SyncAuthorityConflictResolutionKind.KeepLocal,"stale-resolution-group"),
                "actual conflict continuation rejects another generation");
            await output.MarkConflictResolutionExecutingAsync("conflict","resolution-group");
            Check((await output.LoadConflictAsync("conflict")).Status=="resolving",
                "actual retained conflict reader and existing transition continue the original choice");
            var badPath=Path.Combine(root,"missing-resolution-action-source.db");File.Copy(path,badPath);
            await SqlAsync(badPath,"UPDATE conflict_resolution SET resolution_action_id='missing-action' WHERE conflict_resolution_id='resolution';");
            await SealAsync(badPath);
            var badIdentity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(badPath,options);
            var badTarget=Path.Combine(root,"missing-resolution-action-successor.db");
            await RefuseAsync(()=>SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(badPath,badTarget,badIdentity,options),
                "missing exact resolution action is never dropped from a pending choice");
            Check(!File.Exists(badTarget),"invalid resolution binding refuses before candidate creation");
        }
        await RefuseAsync(()=>output.AcquireOperationLeaseAsync(ticket.OperationId,"ordinary",TimeSpan.FromMinutes(1)),
            "old-generation command cannot acquire ordinary lease");
        await RefuseAsync(()=>output.RecordReceiptAsync(attempt.AttemptId,oldLease,SyncAuthorityReceiptCertainty.KnownSuccess,
            "{}","{}","{}",new string('d',64)),"old active source lease cannot append a target receipt");
        await RefuseAsync(()=>output.ReserveActionAsync("replay",admission),"old command cannot reserve a new target action");
        var reconcile=await output.AcquireReconciliationOperationLeaseAsync(ticket.OperationId,"target-reconciler",TimeSpan.FromMinutes(1));
        Check(reconcile.FencingToken>oldLease.FencingToken,"new reconciliation-only owner advances retained fence highwater");
        await RefuseAsync(()=>output.StartDispatchedAttemptAsync(ticket.OperationId,reconcile,new string('e',64),"forbidden"),
            "reconciliation lease cannot issue another effect");
        await output.RecordReceiptAsync(attempt.AttemptId,reconcile,SyncAuthorityReceiptCertainty.KnownNotApplied,
            "{}","{}","{}",new string('f',64));
        await output.ReconcileAttemptAsync(attempt.AttemptId,reconcile,SyncAuthorityAttemptStatus.VerifiedNotApplied,"{}");
        await RefuseAsync(()=>output.StartDispatchedAttemptAsync(ticket.OperationId,reconcile,new string('e',64),"forbidden"),
            "reconciled absence still cannot turn old intent into ordinary permission");
        Check(Hash(path)==sourceHash,"source journal remains byte-exact through target reconciliation");
        await output.VerifyHealthAsync();
        if(includeSafety)
        {
            var badPath=Path.Combine(root,"incomplete-absence-source.db");File.Copy(path,badPath);
            var bad=new SyncAuthorityDatabase(badPath);
            var incomplete=await bad.BeginScanGenerationAsync("pair","local-root","local","manual","{}");
            await SqlAsync(badPath,"UPDATE tombstone_observation SET local_scan_generation_id='"+incomplete.ScanGenerationId+"';");
            await SealAsync(badPath);
            var badIdentity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(badPath,options);
            var badTarget=Path.Combine(root,"incomplete-absence-successor.db");
            await RefuseAsync(()=>SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(badPath,badTarget,badIdentity,options),
                "incomplete scan cannot become retained qualifying absence evidence");
            Check(!File.Exists(badTarget),"invalid absence proof creates no successor target");
        }
        if(includeLegacyLineage)
            await RunLegacyConsentSliceAsync(path,Path.Combine(root,"schema7"),legacyParent!,admission.CommandRequestId,
                ticket.OperationId,legacyPlan!,legacyPayload!);
        Console.WriteLine("PASS essential unknown-journal successor proof complete");
    }

    private static async Task RunLegacyConsentSliceAsync(string producedPath,string root,string originalId,
        string retryId,string operationId,SyncBootstrapActionPlan plan,string originalJson)
    {
        Directory.CreateDirectory(root);
        var source=Path.Combine(root,"source.db");var target=Path.Combine(root,"successor.db");
        await CreateLegacyCopyInputAsync(producedPath,source);
        await SealAsync(source);var sourceHash=Hash(source);
        var options=new SyncEssentialSuccessorOptions(root);
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(source,options);
        var report=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(source,target,identity,options);
        await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(source,target,identity,options);
        var db=new SyncAuthorityDatabase(target);
        var id=SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(originalId);
        var header=await db.LoadConsentHeaderAsync(id);
        Check(header is not null&&header.OriginalChoiceCount==plan.Choices.Count&&
            header.OriginalRequestSha256==SyncReviewedConsentReference.LowercaseRequestSha256(originalJson),
            "schema7 creates normalized consent from the exact original approval");
        var items=await db.LoadConsentItemsAsync(id,[0,1,4096],SyncAuthorityConsentSelectionMode.ExactRead);
        Check(items[0].Lifecycle==SyncAuthorityConsentItemLifecycle.Issued&&
            items[1].Lifecycle==SyncAuthorityConsentItemLifecycle.Completed&&
            items[2].Lifecycle==SyncAuthorityConsentItemLifecycle.Eligible,
            "schema7 preserves uncertain issuance, summary-only completion and unissued eligibility");
        Check(report.Tables.Single(x=>x.Table=="consent_header") is{SourceRows:0,SelectedRows:0,TargetRows:1}&&
            report.Tables.Single(x=>x.Table=="consent_item") is{SourceRows:0,SelectedRows:0}&&
            report.Tables.Single(x=>x.Table=="consent_item").TargetRows==plan.Choices.Count,
            "schema7 receipt accounts for newly derived consent separately from absent source structures");
        var original=await db.LoadRunByIdAsync(originalId);
        var retry=await db.LoadRunByIdAsync(retryId);
        using var json=JsonDocument.Parse(retry!.RequestJson);
        var reference=SyncReviewedConsentReference.ReadRequired(json.RootElement.GetProperty("reviewedConsent"));
        Check(original!.RequestJson.Length<4096&&reference.ConsentId==id&&
            reference.RequestSha256==SyncReviewedConsentReference.LowercaseRequestSha256(original.RequestJson),
            "schema7 retry reference binds its compact successor root");
        await RefuseAsync(()=>db.AcquireOperationLeaseAsync(operationId,"forbidden-old-worker",TimeSpan.FromMinutes(1)),
            "schema7 retained operation still cannot gain ordinary dispatch");
        await RefuseAsync(()=>db.LoadConsentItemsAsync(id,[1],SyncAuthorityConsentSelectionMode.ReissueSelection),
            "schema7 completed original choice cannot be selected again");
        await db.VerifyHealthAsync();
        Check(Hash(source)==sourceHash,"schema7 original remains byte-exact after normalized readback and refusal");
        var unknown=Path.Combine(root,"unowned-source.db");
        await CreateLegacyCopyInputAsync(producedPath,unknown,omitReviewedOwner:true);
        await SealAsync(unknown);
        var unknownIdentity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(unknown,options);
        var unknownTarget=Path.Combine(root,"unowned-successor.db");var refused=false;
        try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(unknown,unknownTarget,unknownIdentity,options);}
        catch(InvalidDataException e)when(e.Message.Contains("no original approval link",StringComparison.Ordinal)){refused=true;}
        Check(refused&&!File.Exists(unknownTarget),"unresolved legacy reviewed ownership refuses before target creation");
    }

    internal static async Task RunLegacyCopySliceAsync(string producedPath,string root,string pairId,
        SyncCompletedCopyMove expected, bool legacySchema=false)
    {
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,"source.db");var target=Path.Combine(root,"successor.db");
        if(legacySchema)await CreateLegacyCopyInputAsync(producedPath,path);
        else
        await using(var input=new SqliteConnection("Data Source="+producedPath+";Mode=ReadOnly;Pooling=False"))
        await using(var output=new SqliteConnection("Data Source="+path+";Pooling=False"))
        {
            await input.OpenAsync();await output.OpenAsync();input.BackupDatabase(output);
            await using var tx=output.BeginTransaction();await using var reset=output.CreateCommand();reset.Transaction=tx;
            reset.CommandText="SELECT sql FROM sqlite_schema WHERE type='trigger' AND name='both_absent_empty_copy_delete_guard';";
            var guard=(string)(await reset.ExecuteScalarAsync())!;
            reset.CommandText="DROP TRIGGER both_absent_empty_copy_delete_guard; DELETE FROM copy_predecessor; DELETE FROM current_copy_completion; "+guard+";";
            await reset.ExecuteNonQueryAsync();await tx.CommitAsync();
        }
        if(!legacySchema)
        {
            var original=new SyncAuthorityDatabase(path);
            Check((await original.LoadCompletedCopyMovesAsync(pairId,default)).SequenceEqual([expected]),
                "legacy fallback returns real executed copy candidate without compact projections");
        }
        if(legacySchema)await CheckAmbiguousLegacyCopyAsync(path,root,expected);
        await SealAsync(path);var hash=Hash(path);var options=new SyncEssentialSuccessorOptions(root);
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=phase=>
        {if(phase=="after-table:current_copy_completion")throw new Injected();};
        var interrupted=false;
        try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);}
        catch(Injected){interrupted=true;}
        finally{SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=null;}
        Check(interrupted,"interruption leaves legacy normalization incomplete");
        var report=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);
        if(legacySchema)
            Check(report.Tables.Where(x=>x.Table is "current_copy_completion" or "copy_predecessor")
                .All(x=>x.SourceRows==0 && x.SelectedRows==0 && x.OriginalOnlyRows==0 && x.TargetRows==1),
                "schema7 receipt distinguishes absent source tables from newly derived proof rows");
        await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,target,identity,options);
        var successor=new SyncAuthorityDatabase(target);
        Check((await successor.LoadCompletedCopyMovesAsync(pairId,default)).SequenceEqual([expected]) &&
            await successor.CompletedCopyMoveMatchesAsync(pairId,expected,default) &&
            await successor.IsNormalizedCompletedCopyMoveAsync(pairId,expected,default),
            "successor normalizes all14 legacy candidate fields through real compact readers");
        await using(var c=new SqliteConnection("Data Source="+target+";Mode=ReadOnly;Pooling=False"))
        {
            await c.OpenAsync();await using var q=c.CreateCommand();
            q.CommandText="SELECT (SELECT count(*) FROM current_copy_completion WHERE coverage_verified=1)=1 AND (SELECT count(*) FROM copy_predecessor)=1;";
            Check(Convert.ToInt64(await q.ExecuteScalarAsync())==1,
                "legacy evidence produces exactly one current copy and predecessor");
        }
        Check(Hash(path)==hash,"read-only legacy copy normalization preserves exact source bytes");
        await successor.VerifyHealthAsync();
        if(legacySchema)
        {
            Check(!await successor.IsOperationRetainedAsync(expected.CopyOperationId,default),
                "historical schema7 copy operation stays original-only after compact evidence conversion");
            await RefuseAsync(()=>successor.AcquireOperationLeaseAsync(expected.CopyOperationId,"old-worker",TimeSpan.FromMinutes(1)),
                "legacy copy alias cannot acquire successor dispatch authority");
        }
        Console.WriteLine("PASS essential legacy copy conversion complete schema="+(legacySchema?"7":"18"));
    }

    private static async Task CheckAmbiguousLegacyCopyAsync(string path,string root,SyncCompletedCopyMove expected)
    {
        const string ambiguous="synthetic-ambiguous-predecessor";
        await using(var c=new SqliteConnection("Data Source="+path+";Pooling=False"))
        {
            await c.OpenAsync();await using var q=c.CreateCommand();
            q.Parameters.AddWithValue("$current",expected.CurrentPointId);q.Parameters.AddWithValue("$prior",expected.PriorPointId);
            q.Parameters.AddWithValue("$ambiguous",ambiguous);
            q.CommandText="""
                UPDATE synchronization_point SET authority_generation=authority_generation+1 WHERE synchronization_point_id=$current;
                INSERT INTO synchronization_point
                SELECT $ambiguous,pair_id,object_id,local_object_version_id,dropbox_object_version_id,
                    local_membership_id,dropbox_membership_id,accepted_utc,
                    (SELECT authority_generation-1 FROM synchronization_point WHERE synchronization_point_id=$current)
                FROM synchronization_point WHERE synchronization_point_id=$prior;
                """;
            await q.ExecuteNonQueryAsync();
        }
        await SealAsync(path);var options=new SyncEssentialSuccessorOptions(root);
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        var target=Path.Combine(root,"ambiguous-successor.db");var rejected=false;
        try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);}
        catch(InvalidDataException e)when(e.Message.Contains("ambiguous current/predecessor",StringComparison.Ordinal)){rejected=true;}
        Check(rejected && !File.Exists(target),"ambiguous legacy predecessor fails its exact proof gate before target creation");
        await using(var c=new SqliteConnection("Data Source="+path+";Pooling=False"))
        {
            await c.OpenAsync();await using var q=c.CreateCommand();
            q.Parameters.AddWithValue("$current",expected.CurrentPointId);q.Parameters.AddWithValue("$ambiguous",ambiguous);
            q.CommandText="DELETE FROM synchronization_point WHERE synchronization_point_id=$ambiguous; UPDATE synchronization_point SET authority_generation=authority_generation-1 WHERE synchronization_point_id=$current;";
            await q.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateLegacyCopyInputAsync(string producedPath,string path,bool omitReviewedOwner=false)
    {
        // Author a synthetic v7 fixture from one real bounded source producer.
        // No product path uses this schema projection or temporarily suspends guards.
        await using var output=new SqliteConnection("Data Source="+path+";Pooling=False");
        await output.OpenAsync();await using var q=output.CreateCommand();
        q.CommandText=SyncAuthoritySchema.Sql+SyncAuthoritySchemaSupplement.Sql+
            SyncAuthoritySchemaMigration3.Sql+SyncAuthoritySchemaMigration4.Sql+
            SyncAuthoritySchemaMigration5.Sql+SyncAuthoritySchemaMigration6.Sql+SyncAuthoritySchemaMigration7.Sql+
            "INSERT INTO schema_migration VALUES(7,'2026-01-01T00:00:00Z','synthetic-copy-legacy'); PRAGMA user_version=7;";
        await q.ExecuteNonQueryAsync();
        await using var input=new SqliteConnection("Data Source="+producedPath+";Mode=ReadOnly;Pooling=False");
        await input.OpenAsync();
        var tables=new List<string>();var guards=new List<(string Name,string Sql)>();
        q.CommandText="SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'schema_migration' ORDER BY name;";
        await using(var rows=await q.ExecuteReaderAsync())while(await rows.ReadAsync())tables.Add(rows.GetString(0));
        q.CommandText="SELECT name,sql FROM sqlite_schema WHERE type='trigger';";
        await using(var rows=await q.ExecuteReaderAsync())while(await rows.ReadAsync())guards.Add((rows.GetString(0),rows.GetString(1)));
        await using var tx=output.BeginTransaction();q.Transaction=tx;
        q.CommandText="PRAGMA defer_foreign_keys=ON;";await q.ExecuteNonQueryAsync();
        foreach(var guard in guards){q.CommandText="DROP TRIGGER "+guard.Name+";";await q.ExecuteNonQueryAsync();}
        foreach(var table in tables)
        {
            q.CommandText="SELECT count(*) FROM "+table+";";
            if(Convert.ToInt64(await q.ExecuteScalarAsync())!=0)continue; // fixed schema-owned seed rows
            q.CommandText="PRAGMA table_info("+table+");";
            var columns=new List<string>();
            await using(var rows=await q.ExecuteReaderAsync())while(await rows.ReadAsync())columns.Add(rows.GetString(1));
            var list=string.Join(",",columns);
            await using var read=input.CreateCommand();
            read.CommandText="SELECT "+list+" FROM "+(table=="observation"?"effective_observation":table)+";";
            await using var data=await read.ExecuteReaderAsync();
            while(await data.ReadAsync())
            {
                q.Parameters.Clear();q.CommandText="INSERT INTO "+table+"("+list+") VALUES("+
                    string.Join(",",Enumerable.Range(0,columns.Count).Select(i=>"$v"+i))+");";
                for(var i=0;i<columns.Count;i++)
                {
                    object value=data.GetValue(i);
                    if(omitReviewedOwner&&table=="operation_group"&&columns[i]=="detail_json")
                    {
                        var detail=System.Text.Json.Nodes.JsonNode.Parse((string)value)!.AsObject();
                        if((string?)detail["purpose"]=="reviewed_first_sync")detail.Remove("runId");
                        value=detail.ToJsonString();
                    }
                    q.Parameters.AddWithValue("$v"+i,value);
                }
                await q.ExecuteNonQueryAsync();
            }
        }
        q.Parameters.Clear();
        foreach(var guard in guards){q.CommandText=guard.Sql;await q.ExecuteNonQueryAsync();}
        await tx.CommitAsync();q.Transaction=null;q.CommandText="PRAGMA foreign_key_check;";
        await using var check=await q.ExecuteReaderAsync();
        Check(!await check.ReadAsync(),"synthetic schema7 graph retains valid original references");
    }

    internal static async Task RunNormalizedCopySliceAsync(string producedPath,string root,string pairId,
        SyncCompletedCopyMove expected)
    {
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,"source.db");var target=Path.Combine(root,"successor.db");
        await using(var input=new SqliteConnection("Data Source="+producedPath+";Mode=ReadOnly;Pooling=False"))
        {
            await input.OpenAsync();await using var readId=input.CreateCommand();
            readId.CommandText="SELECT installation_id FROM installation;";
            var installation=(string)(await readId.ExecuteScalarAsync())!;
            var db=new SyncAuthorityDatabase(path);await db.InitializeAsync(installation,"fixture-profile","fixture","{}");
            await using var output=new SqliteConnection("Data Source="+path+";Pooling=False");await output.OpenAsync();
            await using var clear=output.CreateCommand();clear.CommandText="PRAGMA foreign_keys=ON; DELETE FROM capability_profile;";
            await clear.ExecuteNonQueryAsync();
            // Exact typed fixture projection of REAL producer proof. The isolated
            // input represents already-normalized journal-free current-copy state;
            // it does not claim to import the producer's remaining safety domains.
            const string snapshot="pair_id,object_id,point_id,point_generation,local_version_id,remote_version_id,"+
                "local_membership_id,remote_membership_id,local_root_id,remote_root_id,path_key,"+
                "copy_operation_id,attempt_id,receipt_id,destination_identity_json,attempt_status,receipt_evidence_json";
            var tables=new(string Name,string Columns)[]
            {
                ("capability_profile","capability_profile_id,source_revision,profile_json,created_utc"),
                ("sync_pair","pair_id,configuration_revision,state,created_utc,pair_json,selected"),
                ("pair_config_revision","pair_id,revision,created_utc,created_by,deletion_policy,deletion_grace_seconds,required_absence_observations,conflict_policy,metadata_policy,recursive_mode,automatic_pruning,recovery_retention_days"),
                ("provider_account","provider_account_id,installation_id,provider,account_id,root_namespace_id,home_namespace_id,display_label,binding_revision,verified_utc,status"),
                ("volume","volume_id,installation_id,volume_serial_hex,filesystem,volume_guid_path,first_seen_utc,last_seen_utc,supports_file_id_128,supports_usn,supports_atomic_rename,status"),
                ("root_binding","root_binding_id,pair_id,side,generation,identity_json,display_path,state,observed_utc"),
                ("logical_object","object_id,object_kind,created_utc"),
                ("object_version","object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc"),
                ("membership","membership_id,object_id,root_binding_id,relative_path_key,generation,state,observed_utc"),
                ("synchronization_point","synchronization_point_id,pair_id,object_id,local_object_version_id,dropbox_object_version_id,local_membership_id,dropbox_membership_id,accepted_utc,authority_generation"),
                ("current_copy_completion",snapshot+",coverage_verified"),
                ("copy_predecessor",snapshot)
            };
            await using var tx=output.BeginTransaction();
            foreach(var table in tables)
            {
                await using var read=input.CreateCommand();read.CommandText=$"SELECT {table.Columns} FROM {table.Name};";
                await using var rows=await read.ExecuteReaderAsync();
                while(await rows.ReadAsync())
                {
                    await using var write=output.CreateCommand();write.Transaction=tx;
                    write.CommandText=$"INSERT INTO {table.Name}({table.Columns}) VALUES("+
                        string.Join(",",Enumerable.Range(0,rows.FieldCount).Select(i=>"$v"+i))+");";
                    for(var i=0;i<rows.FieldCount;i++)write.Parameters.AddWithValue("$v"+i,rows.GetValue(i));
                    await write.ExecuteNonQueryAsync();
                }
            }
            await tx.CommitAsync();
        }
        var source=new SyncAuthorityDatabase(path);
        Check((await source.LoadCompletedCopyMovesAsync(pairId,default)).SequenceEqual([expected]),
            "isolated normalized input preserves all14 fields from real executed-copy producer");
        await SqlAsync(path,"UPDATE current_copy_completion SET receipt_evidence_json=json_set(receipt_evidence_json,'$.receiptId','wrong');");
        await SealAsync(path);var options=new SyncEssentialSuccessorOptions(root);
        var bad=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        await RefuseAsync(()=>SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,bad,options),
            "mismatched original copy receipt refuses before target creation");
        Check(!File.Exists(target),"malformed copy evidence creates no successor");
        await SqlAsync(path,"UPDATE current_copy_completion SET receipt_evidence_json=json_set(receipt_evidence_json,'$.receiptId',receipt_id);");
        await SealAsync(path);var hash=Hash(path);
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);
        await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,target,identity,options);
        var successor=new SyncAuthorityDatabase(target);
        Check((await successor.LoadCompletedCopyMovesAsync(pairId,default)).SequenceEqual([expected])&&
            await successor.CompletedCopyMoveMatchesAsync(pairId,expected,default)&&
            await successor.IsNormalizedCompletedCopyMoveAsync(pairId,expected,default),
            "actual successor copy readers preserve complete original candidate and predecessor proof");
        await using(var c=new SqliteConnection("Data Source="+path+";Mode=ReadOnly;Pooling=False"))
        {
            await c.OpenAsync();await using var q=c.CreateCommand();
            q.CommandText="SELECT identity_json FROM object_version WHERE object_version_id=$id;";
            q.Parameters.AddWithValue("$id",expected.NewVersionId);
            Check(await successor.CompletedCopyReceiptMatchesAsync(expected.CopyOperationId,
                (string)(await q.ExecuteScalarAsync())!,expected.NewPath,default),
                "retained exact destination receipt remains available without the old journal");
        }
        Check(!await successor.IsOperationRetainedAsync(expected.CopyOperationId,default),
            "copy proof aliases do not recreate historical operation authority");
        await RefuseAsync(()=>successor.AcquireOperationLeaseAsync(expected.CopyOperationId,"old-worker",TimeSpan.FromMinutes(1)),
            "old copy alias cannot acquire ordinary dispatch permission");
        Check(Hash(path)==hash,"normalized source bytes remain unchanged");
        await successor.VerifyHealthAsync();
        Console.WriteLine("PASS essential normalized copy proof import complete");
    }

    internal static async Task RemoveMigration18ForPredecessorFixtureAsync(string path)
    {
        // Synthetic predecessor construction only; never an importer path.
        await using var c=new SqliteConnection("Data Source="+path+";Pooling=False");await c.OpenAsync();
        await using var query=c.CreateCommand();query.CommandText="PRAGMA user_version;";
        if(Convert.ToInt64(await query.ExecuteScalarAsync())!=18)throw new InvalidDataException("Expected exact schema18 fixture.");
        query.CommandText="SELECT essential_successor_json FROM installation;";
        if(await query.ExecuteScalarAsync() is not DBNull)throw new InvalidDataException("Do not strip a successor receipt.");
        query.CommandText="DROP TRIGGER essential_successor_receipt_guard; ALTER TABLE installation DROP COLUMN essential_successor_json; "+
            "DELETE FROM schema_migration WHERE schema_version=18; "+
            "INSERT OR IGNORE INTO schema_migration(schema_version,applied_utc,source_revision) VALUES(17,'2026-01-01T00:00:00Z','schema17-fixture'); PRAGMA user_version=17;";
        await query.ExecuteNonQueryAsync();
    }

    internal static async Task RunMigrationAsync(string root)
    {
        if(Directory.Exists(root))throw new IOException("Use a fresh migration fixture.");
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,"source.db");var db=new SyncAuthorityDatabase(path);
        await db.InitializeAsync("migration-installation","profile","fixture","{}");
        await db.RegisterPairAsync("pair",1);
        var command=await db.CreateAcceptedCommandAdmissionAsync(null,"run_pair","pair",null,null,"{}",SyncAuthorityCommandPurpose.NormalEffect);
        await RemoveMigration18ForPredecessorFixtureAsync(path);
        var migrated=new SyncAuthorityDatabase(path);await migrated.InitializeAsync("migration-installation","profile","fixture","{}");
        await migrated.VerifyHealthAsync();
        Check(File.Exists(migrated.Version17RollbackDatabasePath),"schema17 migration keeps exact verified rollback");
        await using var c=new SqliteConnection("Data Source="+path+";Mode=ReadOnly;Pooling=False");await c.OpenAsync();
        await using var q=c.CreateCommand();q.CommandText="SELECT database_generation FROM installation;";
        Check((string?)await q.ExecuteScalarAsync()==command.DatabaseGeneration,"ordinary schema upgrade preserves existing generation");
        q.CommandText="SELECT essential_successor_json FROM installation;";
        Check(await q.ExecuteScalarAsync() is DBNull,"ordinary upgrade does not invent an essential-import receipt");
        q.CommandText="SELECT count(*) FROM command_request WHERE command_request_id=$id;";
        q.Parameters.AddWithValue("$id",command.CommandRequestId);
        Check(Convert.ToInt64(await q.ExecuteScalarAsync())==1,"ordinary upgrade preserves pending command state");
        await using var old=new SqliteConnection("Data Source="+migrated.Version17RollbackDatabasePath+";Mode=ReadOnly;Pooling=False");
        await old.OpenAsync();await using var inspect=old.CreateCommand();inspect.CommandText="PRAGMA user_version;";
        Check(Convert.ToInt64(await inspect.ExecuteScalarAsync())==17,"rollback remains exact predecessor schema17");
    }

    private static async Task<(SyncAuthorityMonitorWriter Writer,SyncAuthorityMonitorBatch Pending)>
        SeedMonitorAsync(SyncAuthorityDatabase db,string rootId)
    {
        await db.RegisterMonitorCursorAsync("cursor","pair",rootId,"periodic",1,"{}","c0");
        var writer=await db.BeginMonitorWriterAsync("cursor","pair",rootId,"periodic",1,"{}");
        SyncAuthorityMonitorEventInput Event(string id,string? path)=>new("event-"+id,"key-"+id,path,"changed","{}");
        var closed=db.PrepareMonitorBatch(writer,[Event("closed","closed.txt")],"c0","c1");
        await db.CommitMonitorBatchAsync(writer,closed);
        await db.CloseMonitorHintsThroughAsync("pair",await db.CaptureMonitorWatermarkAsync("pair"));
        var open=db.PrepareMonitorBatch(writer,[Event("pending","pending.txt"),Event("uncertain",null)],"c1","c2");
        await db.CommitMonitorBatchAsync(writer,open);
        await db.SetMonitorCursorStatusAsync(writer,"unavailable");
        await db.RegisterMonitorCursorAsync("reset","pair",rootId,"usn",1,"{}","reset-token");
        await db.SetMonitorCursorStatusAsync("reset","pair","reset_required");
        return(writer,db.PrepareMonitorBatch(writer,[Event("old-callback","forbidden.txt")],"c2","c3"));
    }

    internal static async Task RunMonitorAsync(string root)
    {
        if(Directory.Exists(root))throw new IOException("Use a fresh monitor successor fixture.");
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,"source.db");var target=Path.Combine(root,"successor.db");
        var db=new SyncAuthorityDatabase(path);await db.InitializeAsync("monitor-installation","profile","fixture","{}");
        await db.RegisterPairAsync("pair",1);
        await db.RegisterRootBindingAsync("local","pair","local",1,
            """{"volumeSerial":"00000001","fileId":"root"}""",root);
        var (old,pending)=await SeedMonitorAsync(db,"local");
        var oldWatermark=await db.CaptureMonitorWatermarkAsync("pair");
        var originalProjection=System.Text.Json.JsonSerializer.Serialize(await db.LoadMonitorProjectionAsync());
        await SealAsync(path);var sourceHash=Hash(path);
        var options=new SyncEssentialSuccessorOptions(root);
        var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
        var interrupted=false;
        SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=phase=>
        {if(phase=="after-table:monitor_cursor")throw new Injected();};
        try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);}
        catch(Injected){interrupted=true;}
        finally{SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=null;}
        Check(interrupted,"monitor import interruption leaves resumable source-bound target");
        var report=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,target,identity,options);
        await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,target,identity,options);
        Check(report.Tables.Single(x=>x.Table=="monitor_event") is{SourceRows:3,SelectedRows:2,OriginalOnlyRows:1}&&
            report.Tables.Single(x=>x.Table=="dirty_item") is{SourceRows:3,SelectedRows:2,OriginalOnlyRows:1},
            "unconsumed hints and dirty obligations retained; closed diagnostics stay original-only");
        var output=new SyncAuthorityDatabase(target);
        Check(System.Text.Json.JsonSerializer.Serialize(await output.LoadMonitorProjectionAsync())==originalProjection,
            "actual monitor UI projection preserves cursor health, uncertainty and dirty counts");
        Check((await output.LoadMonitorCursorAsync("cursor")) is{CursorValue:"c2",Status:"unavailable"}&&
            await output.LoadMonitorCursorAsync("reset") is null,
            "opaque current cursor preserved and reset-required cursor never becomes active");
        await RefuseAsync(()=>output.CommitMonitorBatchAsync(old,pending),"source monitor capability refuses in successor");
        await RefuseAsync(()=>output.CloseMonitorHintsThroughAsync("pair",oldWatermark),
            "source watermark cannot close successor dirty state");
        var forged=new SyncAuthorityMonitorWriter(output,old.Binding,old.Epoch){Cursor="c2",Sequence=old.Sequence};
        var forgedBatch=new SyncAuthorityMonitorBatch(forged,pending.Sequence,pending.PriorCursor,pending.NextCursor,
            pending.Events,pending.Digest);
        await RefuseAsync(()=>output.CommitMonitorBatchAsync(forged,forgedBatch),
            "old epoch refuses even when rebound to target object");
        var writer=await output.BeginMonitorWriterAsync("cursor","pair","local","periodic",1,"{}");
        Check(writer.Epoch!=old.Epoch&&writer.Cursor=="c2","new writer starts exactly at retained cursor with new epoch");
        var watermark=await output.CaptureMonitorWatermarkAsync("pair");
        var batch=output.PrepareMonitorBatch(writer,
            [new("event-new","key-new","pending.txt","changed","{}")],"c2","c3");
        await output.CommitMonitorBatchAsync(writer,batch);
        await output.CloseMonitorHintsThroughAsync("pair",watermark);
        Check((await output.LoadMonitorProjectionAsync()).All(x=>x.PendingDirtyItems==1),
            "new hint sequence survives closure through imported watermark");
        Check(Hash(path)==sourceHash,"source monitor bytes unchanged throughout target continuation");
        await output.VerifyHealthAsync();
        Console.WriteLine("PASS essential monitor successor producer, restart and stale-callback checks complete");
    }
}
