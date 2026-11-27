using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;
using Massdex.SyncShark.Engine.Storage;
using Fx = Massdex.SyncShark.Engine.Tests.Scenarios.StoreMoveChecks;

namespace Massdex.SyncShark.Engine.Tests.Storage;
/// <summary>Exercises closed-image publication on synthetic stores.</summary>
public static class StoreMoveChecks
{
    /// <summary>Startup must not create a store; explicit creation publishes once and preserves an existing image.</summary>
    public static async Task RunAsync()
    {
        var failures=new List<string>();
        async Task Probe(string name,Func<Task> test){try{await test();}catch(Exception ex){failures.Add(name+": "+ex.Message);}}
        await ProbeStorageAsync(Probe);

        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var move = new StoreMove();
            var path = Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.DatabaseFile);
            var pointer = Path.Combine(root, "product-root.txt");
            var original = await CreateOnceAsync(move, root, path, pointer);
            await RetryPointerCleanupAsync(move, root, path, pointer);
            await Probe("dangling pointer after publication", async () =>
            {
                await File.WriteAllTextAsync(pointer, Path.Combine(root, "missing"));
                var dangling = await move.MoveWithOutcomeAsync(root, root, (_, _, _) => throw new InvalidOperationException("Connections read from a missing store"), default);
                Check.Require(dangling == null && !File.Exists(pointer) && original.SequenceEqual(await File.ReadAllBytesAsync(path)), "Published store refused a dangling pointer or changed");
            });
            await Scenarios.StoreMoveChecks.CreateFixtureAsync();
            await Probe("pair info coexistence",()=>RootInfosAsync(Path.Combine(root,"infos")));
            await Probe("mixed skip subject",()=>RecoveredBackupAsync(Path.Combine(root,"recovery")));
            await Probe("damaged backup beside published store",()=>DamagedBackupAsync(Path.Combine(root,"damaged")));
            await Probe("external switch restart",()=>ExternalSwitchAsync(Path.Combine(root,"restart")));
            await Probe("external store removed",()=>ExternalSwitchAsync(Path.Combine(root,"removed"),p=>Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(p))!,true)));
            await Probe("external store unreadable",()=>ExternalSwitchAsync(Path.Combine(root,"unreadable"),p=>File.WriteAllText(p,"not a store")));
            await Probe("external pointer",()=>PointerAsync(Path.Combine(root,"external")));
            await Probe("migrated notice",()=>MigratedAsync(Path.Combine(root,"members"),false));
            await Probe("original backup",()=>MigratedAsync(Path.Combine(root,"backup"),true));
            await Probe("pre-fix shape refused",()=>OldShapeAsync(Path.Combine(root,"oldshape")));
            await Probe("missing keep index refused",()=>OldShapeAsync(Path.Combine(root,"missing-index"),"DROP INDEX synced_keep"));
            await Probe("wrong keep index refused",()=>OldShapeAsync(Path.Combine(root,"wrong-index"),"DROP INDEX synced_keep;CREATE INDEX synced_keep ON synced_entry(pair_id)"));
            await Probe("missing key move trigger refused",()=>OldShapeAsync(Path.Combine(root,"missing-move"),"DROP TRIGGER synced_key_move"));
            await Probe("changed key trigger refused",()=>OldShapeAsync(Path.Combine(root,"changed-trigger"),"DROP TRIGGER synced_key_remove;CREATE TRIGGER synced_key_remove AFTER DELETE ON synced_entry BEGIN SELECT 1; END"));
            await Probe("both marks before publication",()=>MarksBeforePublicationAsync(Path.Combine(root,"marks")));
            await Probe("mixed retirement phase",()=>MixedPhaseAsync(Path.Combine(root,"mixed")));
            await Probe("hot-journal v16 restart",()=>HotJournalRestartAsync(Path.Combine(root,"hot")));
            await Probe("unproven mark retried",()=>UnprovenMarkRetriedAsync(Path.Combine(root,"unproven")));
            await Probe("key upkeep and rebase",KeyUpkeepAsync);
            await Probe("intern count",InternCountAsync);
            await Probe("store read plans",()=>ReadPlansAsync(Path.Combine(root,"plans")));
            await Probe("compaction",()=>CompactionAsync(Path.Combine(root,"compaction")));
            Check.Require(failures.Count==0,string.Join("; ",failures));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task ProbeStorageAsync(Func<string, Func<Task>, Task> probe)
    {
        await probe("commit footprint",async()=>{using var db=await DeletedNamesChecks.OpenAsync();var scans=new ScanTable();var keep=Guid.NewGuid();var drop=Guid.NewGuid();scans.Begin("p",keep);scans.Begin("p",drop);await scans.ClearScanAsync(db,"p",drop,default);ScanTableChecks.VerifyCommitFootprint(scans,keep);});
        await probe("documentation",DeletedNamesChecks.VerifyAcquisitionAsync);
        await probe("published lookup",DeletedNamesChecks.PublishedHistoryAsync);
        await probe("codec ownership",()=>{Check.Require(!typeof(ScanTable).GetMethods(System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Any(m=>m.Name is "EntryAt" or "BindingAt"),"Unexpected scan codec wrapper");return Task.CompletedTask;});
        await probe("restored partition",async()=>{await DeletedNamesChecks.RestorePartitionAsync(32);await DeletedNamesChecks.RestorePartitionAsync(64);});
        await probe("saved lookup",DeletedNamesChecks.SavedLookupAsync);
        await probe("verify before listing",DeletedNamesChecks.VerifyBeforeListingAsync);
        await probe("corrupt history transition",DeletedNamesChecks.InvalidationBoundaryAsync);
        await probe("incomplete acquisition",DeletedNamesChecks.VerifyUnreadableHistoryAsync);
        await probe("indexed ingest",()=>ScanTableChecks.VerifyCountedWorkAsync(true));
        await probe("scan projection columns",ScanTableChecks.ScanReaderColumnsAsync);
        await probe("open cursor WAL",ScanTableChecks.SnapshotCostAsync);
        await probe("rename cursor WAL",ScanTableChecks.RenameReaderReleasesWalAsync);
        await probe("unchanged hard links",ScanTableChecks.AliasCostAsync);
        await probe("Dropbox ancestor case",ScanTableChecks.DropboxAncestorCaseAsync);
        await probe("notice ownership",()=>ScanTableChecks.MappedNoticesAsync(false));
        await probe("mapped ordering",()=>ScanTableChecks.MappedOrderAsync());
        await probe("bounded compare",()=>ScanTableChecks.VerifyCountedWorkAsync(false));
        await probe("folder store counted work",ScanTableChecks.FolderStoreCountedWorkAsync);
    }

    private static async Task<byte[]> CreateOnceAsync(StoreMove move, string root, string path, string pointer)
    {
        await move.MoveWithOutcomeAsync(root, root, (_, _, _) => throw new InvalidOperationException(), default);
        Check.Require(!File.Exists(path), "Startup created a fresh store");
        await File.WriteAllTextAsync(pointer, Path.Combine(root, "missing"));
        try
        {
            await move.MoveWithOutcomeAsync(root, root, (_, _, _) => null, default);
            throw new Exception("Dangling pointer accepted before publication");
        }
        catch (InvalidDataException)
        {
        }

        Check.Require(File.Exists(pointer) && !File.Exists(path), "Refusal created a store or dropped the pointer");
        File.Delete(pointer);
        await move.CreateAsync(root, default);
        var original = await File.ReadAllBytesAsync(path);
        try
        {
            await move.CreateAsync(root, default);
        }
        catch (IOException)
        {
        }

        Check.Require(original.Length > 0 && original.SequenceEqual(await File.ReadAllBytesAsync(path)), "Second create replaced the published image");
        return original;
    }

    private static async Task RetryPointerCleanupAsync(StoreMove move, string root, string path, string pointer)
    {
        var abandoned = path + ".previous.new";
        await File.WriteAllTextAsync(abandoned, "abandoned stage");
        await File.WriteAllTextAsync(pointer, root);
        bool connectionCheck = false;
        using (var locked = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var warning = await move.MoveWithOutcomeAsync(root, root, (_, _, ids) =>
            {
                connectionCheck = ids.Count == 0;
                return null;
            }, default);
            Check.Require(connectionCheck && !string.IsNullOrEmpty(warning) && File.Exists(path) && File.Exists(pointer), "Post-publication cleanup failure hid the usable store");
        }

        var retry = await move.MoveWithOutcomeAsync(root, root, (_, _, _) => null, default);
        Check.Require(retry == null && !File.Exists(pointer) && await File.ReadAllTextAsync(abandoned) == "abandoned stage", "Next start consumed another attempt's stage or kept the pointer");
    }

    private static async Task CreateReleaseAsync(string root,string name)
    {
        // Borrow the actual release fixture DDL, not a lookalike schema built just to pass migration.
        var code=await File.ReadAllTextAsync(Path.Combine(CheckArguments.Parse(Environment.GetCommandLineArgs()[1..]).Root,"tests","SyncShark.Engine.Tests","Scenarios","StoreMoveChecks.cs"));
        var field=CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(n=>n.Identifier.ValueText=="ReleaseSchema");
        var ddl=((LiteralExpressionSyntax)field.Initializer!.Value).Token.ValueText;
        var folder=Directory.CreateDirectory(Path.Combine(root,ResourceNames.EngineFolder)).FullName;
        var backup=Path.Combine(folder,name);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup,Pooling=false}.ToString()))
        {
            await db.OpenAsync();db.CreateFunction<string,string>("pathkey",PathNames.Key,true);db.CreateFunction<string,int>("plandepth",PathNames.Depth,true);db.CreateFunction<string,int>("localaccessretrycause",_=>0,true);
            using var cmd=db.CreateCommand();cmd.CommandText=ddl;await cmd.ExecuteNonQueryAsync();
            cmd.CommandText="""
                INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p','{"ConnectionId":"c"}');
                INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) VALUES(1,'legacy',1);
                INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed) VALUES
                  (8,1,4,'SkipLink','a','{"Items":[],"Incoming":null}',1,0),(9,1,4,'SkipLink','b','{"Items":[{"Side":0,"Path":"b","Id":"old-link"},{"Side":1,"Path":"b","Id":"still-skipped"}],"Incoming":0}',2,0);
                INSERT INTO issue_member(member_id,issue_id,pair_id,side,path_key,subject_json,skip_cause) VALUES
                  (1,8,1,0,'a','{"Version":1,"Value":{"Items":[],"Incoming":null}}','SkipLink'),
                  (2,9,1,0,pathkey('b'),'{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"b","Id":"old-link"}],"Incoming":null}}','SkipLink'),
                  (3,9,1,1,pathkey('b'),'{"Version":1,"Value":{"Items":[{"Side":"Dropbox","Path":"b","Id":"still-skipped"}],"Incoming":null}}','SkipSpecial');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }
    private static async Task RootInfosAsync(string root)
    {
        await CreateReleaseAsync(root,ResourceNames.PredecessorFile);var path=Path.Combine(root,ResourceNames.EngineFolder,ResourceNames.DatabaseFile);
        using(var db=Open(Path.Combine(root,ResourceNames.EngineFolder,ResourceNames.PredecessorFile),SqliteOpenMode.ReadWrite)){using var seed=db.CreateCommand();seed.CommandText="INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed) VALUES(25,1,3,'DeletedHistoryCorrupt','','{\"Items\":[],\"Incoming\":null}',17,1)";await seed.ExecuteNonQueryAsync();}
        await new StoreMove().MoveWithOutcomeAsync(root,root,(_,_,_)=>null,default);
        using var result=Open(path,SqliteOpenMode.ReadOnly);using var read=result.CreateCommand();read.CommandText="SELECT (SELECT count(*) FROM issue i WHERE i.pair_id=1 AND i.kind=3 AND i.issue_id=25 AND i.path='' AND i.cause='DeletedHistoryCorrupt' AND i.at_ticks=17 AND i.dismissed=1 AND NOT EXISTS(SELECT 1 FROM issue_member m WHERE m.issue_id=i.issue_id))=1 AND (SELECT count(*) FROM issue i WHERE i.pair_id=1 AND i.cause='InfoLinks' AND (SELECT count(*) FROM issue_member m WHERE m.issue_id=i.issue_id)=2)=1";
        Check.Require(Convert.ToInt32(read.ExecuteScalar())==1,"Root links info displaced the original pair info");
    }
    private static async Task RecoveredBackupAsync(string root,bool publicRead=false)
    {
        await CreateReleaseAsync(root,"state.v16.db");var folder=Path.Combine(root,ResourceNames.EngineFolder);var backup=Path.Combine(folder,"state.v16.db");var target=Path.Combine(folder,ResourceNames.DatabaseFile);
        var pointer=Path.Combine(root,"product-root.txt");await File.WriteAllTextAsync(pointer,root);
        var move=new StoreMove();var called=false;
        try{await move.MoveWithOutcomeAsync(root,root,(_,_,_)=>{called=true;throw new IOException("refused");},default);throw new InvalidDataException("Publication continued after connection refusal");}catch(IOException error) when(error.Message=="refused"){}
        Check.Require(called && File.Exists(backup) && !File.Exists(target) && File.Exists(pointer),"Pre-publication refusal consumed the backup or published a stage");
        await move.MoveWithOutcomeAsync(root,root,(_,_,ids)=>{Check.Require(ids.SequenceEqual(new[]{"c"}),"Required connection lost");return null;},default);
        using var opened=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=target,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());await opened.OpenAsync();
        using var read=opened.CreateCommand();read.CommandText="SELECT count(*) FROM issue_member m JOIN issue i USING(issue_id) WHERE (m.member_id IN(1,2) AND i.cause='InfoLinks') OR (m.member_id=3 AND i.issue_id=9)";
        Check.Require(Convert.ToInt32(await read.ExecuteScalarAsync())==3 && File.Exists(backup) && !File.Exists(pointer),"Restart lost a retained link or moved an unrelated member");
        read.CommandText="SELECT cause,subject_json FROM issue WHERE issue_id=9";using var mixed=await read.ExecuteReaderAsync();Check.Require(await mixed.ReadAsync(),"Mixed notice disappeared");
        var expected=new NoticeItem(Side.Dropbox,"b","still-skipped");var actual=System.Text.Json.JsonSerializer.Deserialize<IssueSubject>(mixed.GetString(mixed.GetOrdinal("subject_json")));
        Check.Require(mixed.GetString(mixed.GetOrdinal("cause"))==nameof(TextKey.SkipSpecial) && actual?.Items.SequenceEqual(new[]{expected})==true && actual.Incoming==null,"Mixed notice retained stale link cause or subject");
        if(publicRead){await mixed.DisposeAsync();var notice=await new NoticeTable().NoticeAsync(opened,"p",9,default);Check.Require(notice?.Cause==TextKey.SkipSpecial && notice.Subject.Items.SequenceEqual(new[]{expected}),"Public notice read returned a stale migrated link");}
    }

    private static async Task DamagedBackupAsync(string root)
    {
        await RecoveredBackupAsync(root);var folder=Path.Combine(root,ResourceNames.EngineFolder);var backup=Path.Combine(folder,"state.v16.db");var target=Path.Combine(folder,ResourceNames.DatabaseFile);
        await File.WriteAllTextAsync(backup,"not a store");var published=await File.ReadAllBytesAsync(target);
        var warning=await new StoreMove().MoveWithOutcomeAsync(root,root,(_,_,_)=>null,default);
        Check.Require(warning==null && published.SequenceEqual(await File.ReadAllBytesAsync(target)) && await File.ReadAllTextAsync(backup)=="not a store","Damaged backup refused the published store or was touched");
    }

    internal static async Task VerifyPublicMixedAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try{await RecoveredBackupAsync(root,true);}finally{Directory.Delete(root,true);}
    }
    private static SqliteConnection Open(string path,SqliteOpenMode mode)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=mode,Pooling=false}.ToString());db.Open();db.CreateFunction<string,string>("pathkey",PathNames.Key,true);db.CreateFunction<string,int>("plandepth",PathNames.Depth,true);db.CreateFunction<string,int>("localaccessretrycause",_=>0,true);db.CreateFunction<string,string>("pathfolder",PathNames.Folder,true);db.CreateFunction<string,string>("pathname",PathNames.Name,true);return db;
    }
    private static async Task ExternalSwitchAsync(string root,Action<string>? damage=null)
    {
        var external=Path.Combine(root,"external");var fixedRoot=Directory.CreateDirectory(Path.Combine(root,"fixed")).FullName;
        await CreateReleaseAsync(external,ResourceNames.PredecessorFile);var pointer=Path.Combine(fixedRoot,"product-root.txt");await File.WriteAllTextAsync(pointer,external);
        using (var locked=new FileStream(pointer,FileMode.Open,FileAccess.Read,FileShare.Read))
            Check.Require(!string.IsNullOrEmpty(await new StoreMove().MoveWithOutcomeAsync(fixedRoot,fixedRoot,(_,_,_)=>null,default)),"Published external move lost its cleanup warning");
        var original=Path.Combine(external,ResourceNames.EngineFolder,ResourceNames.PredecessorFile);var published=Path.Combine(fixedRoot,ResourceNames.EngineFolder,ResourceNames.DatabaseFile);
        static int Version(string p){using var db=Open(p,SqliteOpenMode.ReadOnly);using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";return Convert.ToInt32(cmd.ExecuteScalar());}
        var before=Fx.Oracle(original,Path.Combine(root,"oracle"));var kept=await File.ReadAllBytesAsync(published);
        damage?.Invoke(original);
        var connectionsRead=false;
        var warning=await new StoreMove().MoveWithOutcomeAsync(fixedRoot,fixedRoot,(_,_,_)=>{connectionsRead=true;return null;},default);
        Check.Require(warning==null && !File.Exists(pointer) && Version(published)==Schema.Version,"Restart did not start on the published store and retire the pointer");
        if(damage==null)Check.Require(connectionsRead && Fx.UserVersion(original)==1016 && Fx.SameRows(original,before),"Restart did not verify against and preserve the predecessor image");
        else Check.Require(!connectionsRead && kept.SequenceEqual(await File.ReadAllBytesAsync(published)),"Restart read connections from a missing or unusable predecessor or changed the published store");
    }
    private static async Task PointerAsync(string root)
    {
        var oldRoot=Path.Combine(root,"old");var newRoot=Path.Combine(root,"new");await CreateReleaseAsync(oldRoot,ResourceNames.PredecessorFile);
        var move=new StoreMove();await move.CreateAsync(newRoot,default);
        using(var db=Open(Path.Combine(newRoot,ResourceNames.EngineFolder,ResourceNames.DatabaseFile),SqliteOpenMode.ReadWrite)){using var seed=db.CreateCommand();seed.CommandText="INSERT INTO pair(pair_id,def_json) VALUES('other','{\"ConnectionId\":\"c\"}')";seed.ExecuteNonQuery();}
        var pointer=Path.Combine(newRoot,"product-root.txt");await File.WriteAllTextAsync(pointer,oldRoot);
        var oldFile=Path.Combine(oldRoot,ResourceNames.EngineFolder,ResourceNames.PredecessorFile);
        var newFile=Path.Combine(newRoot,ResourceNames.EngineFolder,ResourceNames.DatabaseFile);
        var oldBytes=await File.ReadAllBytesAsync(oldFile); var newBytes=await File.ReadAllBytesAsync(newFile);
        bool checkedConnections=false;
        await move.MoveWithOutcomeAsync(newRoot,newRoot,(source,target,ids)=>
        {
            checkedConnections=source==oldRoot && target==newRoot && ids.SequenceEqual(new[]{"c"}); return null;
        },default);
        Check.Require(checkedConnections && !File.Exists(pointer) && oldBytes.SequenceEqual(await File.ReadAllBytesAsync(oldFile)) && newBytes.SequenceEqual(await File.ReadAllBytesAsync(newFile)),"Restart replaced or merged an occupied store instead of retiring only the pointer");
    }
    private static async Task MigratedAsync(string root,bool backup,bool decode=false)
    {
        await CreateReleaseAsync(root,ResourceNames.PredecessorFile);var folder=Path.Combine(root,ResourceNames.EngineFolder);var target=Path.Combine(folder,ResourceNames.DatabaseFile);
        using(var db=Open(Path.Combine(folder,ResourceNames.PredecessorFile),SqliteOpenMode.ReadWrite)){using var seed=db.CreateCommand();seed.CommandText="""
            INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks) VALUES(20,1,0,'FailChanged','parent','{"Items":[{"Side":0,"Path":"parent","Id":"folder"}],"Incoming":null}',1);
            INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks) VALUES(21,1,0,'FailChanged','parent/legacy-header','{"Items":[{"Side":0,"Path":"parent/alone","Id":"alone"}],"Incoming":null}',1);
            INSERT INTO change_work(work_id,work_origin,issue_work_key,pair_id,pair_generation,source_epoch,side,domain,path_key) VALUES(1,'Issue','branch',1,0,0,0,'path',pathkey('parent/child'));
            INSERT INTO issue_member(issue_id,pair_id,work_id,side,path_key,identity,subject_json) VALUES(20,1,1,0,pathkey('parent/child'),'child-id','{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"parent","Id":"folder"}],"Incoming":null}}');
            """;seed.ExecuteNonQuery();}
        var move=new StoreMove();await move.MoveWithOutcomeAsync(root,root,(_,_,_)=>null,default);
        if(backup){var original=Path.Combine(folder,ResourceNames.PredecessorFile);VerifyBackup(original);
            // Mutation control: the published v17 image must never satisfy the retention proof in place of its predecessor.
            bool rejected=false;try{VerifyBackup(target);}catch(Exception e) when(e is SqliteException or InvalidDataException){rejected=true;}
            Check.Require(rejected,"Backup assertion accepted the replacement as the original");return;}
        using var migrated=Open(target,SqliteOpenMode.ReadWrite);using var attach=migrated.CreateCommand();attach.CommandText="ATTACH ':memory:' AS scan_state;"+Schema.CreateScan;attach.ExecuteNonQuery();var scan=new ScanTable();var id=Guid.NewGuid();scan.Begin("p",id);var seen=false;
        using var counts=migrated.CreateCommand();counts.CommandText="SELECT (SELECT count(*) FROM issue_member WHERE issue_id=20)=1 AND (SELECT count(*) FROM issue_member WHERE issue_id=21)=0";
        Check.Require(Convert.ToInt32(counts.ExecuteScalar())==1,"Unaffected notice acquired a physical member");var alone=false;
        // A spend made before the consumed-deletion column keeps its mask and gains no made-up time.
        using var recorded=migrated.CreateCommand();
        recorded.CommandText="SELECT count(*) FROM synced_entry INDEXED BY synced_keep WHERE pair_id=1 AND keep IS NOT NULL";
        Check.Require(Convert.ToInt64(recorded.ExecuteScalar())==0,"Migrated bindings did not start with an empty indexed KEEP column");
        recorded.CommandText="SELECT spent,(deleted_ticks IS NULL) FROM discovery_deleted_delta WHERE pair_id=1 AND path_key='legacy'";
        using(var legacy=recorded.ExecuteReader())
            Check.Require(legacy.Read() && legacy.GetInt64(0)==1 && legacy.GetInt64(1)==1,"An upgraded pre-existing spend was backfilled or lost");
        // The upgraded image accepts and returns a consumed deletion time.
        await new Committer().CommitAsync(migrated,"p",null,null,null,"k1",default,DateTimeOffset.UnixEpoch);
        recorded.CommandText="SELECT deleted_ticks FROM discovery_deleted_delta WHERE pair_id=1 AND path_key='k1'";
        Check.Require(Convert.ToInt64(recorded.ExecuteScalar())==DateTimeOffset.UnixEpoch.UtcTicks,"An upgraded store cannot record a consumed deletion time");
        if(!decode)
        {
            using var footprints=ScanTableChecks.PageQuery(migrated,id,[PathNames.Key("parent/child"),PathNames.Key("parent/legacy-header")],"MemberRows");using var rows=await footprints.ExecuteReaderAsync();
            int issue = rows.GetOrdinal("issue_id"), side = rows.GetOrdinal("member_side"), identity = rows.GetOrdinal("identity");
            int path = rows.GetOrdinal("path_key"), key = rows.GetOrdinal("k"), subject = rows.GetOrdinal("subject");
            while (await rows.ReadAsync())
            {
                if (rows.GetInt64(issue) == 20)
                    seen = !rows.IsDBNull(side) && rows.GetString(identity) == "child-id" && PathNames.Same(rows.GetString(path), "parent/child");
                if (rows.GetInt64(issue) == 21)
                    alone = rows.IsDBNull(side) && PathNames.Same(rows.GetString(key), "parent/legacy-header") && System.Text.Json.JsonSerializer.Deserialize<IssueSubject>(rows.GetString(subject))?.Items.SequenceEqual(new[]{new NoticeItem(Side.Local,"parent/alone","alone")}) == true;
            }
            using var debt=migrated.CreateCommand();debt.CommandText="SELECT count(*) FROM source_state WHERE pair_id=1 AND repair_reason IS NOT NULL";
            Check.Require(Convert.ToInt32(debt.ExecuteScalar())==2,"Upgrade dropped queued work without owing a Repair");
            Check.Require(seen&&alone,"Legacy header-only or descendant work footprint disappeared");return;
        }
        await foreach(var row in scan.CompareAsync(migrated,"p",id,new Scope("parent","parent"),(_,_) => (true,false),1,default))
            foreach(var n in row.Notices){if(n.Notice.Id==20 && n.Member is {} m && PathNames.Same(m.Path,"parent/child"))seen|=n.Notice.Subject.Items.Any(i=>i.Id=="folder") && m.Id=="child-id";if(n.Notice.Id==21 && n.Member is null && row.Row.Key==PathNames.Key("parent/legacy-header") && n.Notice.Subject.Items.SequenceEqual(new[]{new NoticeItem(Side.Local,"parent/alone","alone")}))alone=true;}
        Check.Require(seen && alone,"Legacy header-only or descendant work footprint disappeared");
    }
    internal static async Task VerifyPublicMigratedAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;try{await MigratedAsync(root,false,true);}finally{Directory.Delete(root,true);}
    }
    private static async Task OldShapeAsync(string root,string damage="ALTER TABLE discovery_deleted_delta DROP COLUMN deleted_ticks")
    {
        // A fresh image built the pre-fix way: the engine shape before the consumed-deletion column existed.
        var folder=Directory.CreateDirectory(Path.Combine(root,ResourceNames.EngineFolder)).FullName;
        var image=Path.Combine(folder,ResourceNames.DatabaseFile);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=image,Pooling=false}.ToString()))
        {
            await db.OpenAsync();
            db.CreateFunction<string,string>("pathkey",PathNames.Key,true);
            db.CreateFunction<string,int>("plandepth",PathNames.Depth,true);
            db.CreateFunction<string,int>("localaccessretrycause",_=>0,true);
            db.CreateFunction<string,string>("pathfolder",PathNames.Folder,true);db.CreateFunction<string,string>("pathname",PathNames.Name,true);
            using var create=db.CreateCommand();create.CommandText=Schema.Create;await create.ExecuteNonQueryAsync();
            using var shape=db.CreateCommand();
            shape.CommandText=damage+"; PRAGMA user_version="+Schema.Version;
            await shape.ExecuteNonQueryAsync();
        }
        var before=System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(image));
        var refused=false;
        try { await new StoreMove().MoveWithOutcomeAsync(root,root,(_,_,_)=>null,default); }
        catch(Exception e) when(e is SqliteException or InvalidDataException) { refused=true; }
        Check.Require(refused,"A pre-fix v17 image was accepted instead of refused before any pair work");
        Check.Require(before.SequenceEqual(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(image))),
            "A refused pre-fix v17 image was changed");
    }
    private static string Engine(string root,string name)=>Path.Combine(root,ResourceNames.EngineFolder,name);
    private static async Task<Exception?> Start(string root,Func<string,string,IReadOnlyList<string>,Action?>? verify=null,CancellationToken ct=default)
    {
        try { await new StoreMove().MoveWithOutcomeAsync(root,root,verify??((_,_,_)=>null),ct);return null; } catch(Exception e) { return e; }
    }

    /// <summary>At the last callback before the rename every predecessor already reads a mark an old binary refuses, durably and without a hot journal.</summary>
    private static async Task MarksBeforePublicationAsync(string root)
    {
        var wrong=new List<string>();
        foreach(var (leg,v17) in new[]{("l17-o16",true),("wal16",false)})
        {
            var at=Path.Combine(root,leg);var l=Engine(at,ResourceNames.PredecessorFile);var o=Engine(at,"state.v16.db");var n=Engine(at,ResourceNames.DatabaseFile);
            if(v17){await Fx.WritePredecessorAsync(at,Fx.V17Seed,true);await Fx.ReleaseImageAsync(o);} else Fx.ReleaseWal(at);
            var marks=v17?new[]{(Path:l,Mark:1017),(Path:o,Mark:1016)}:new[]{(Path:l,Mark:1016)};
            var oracles=marks.Select(m=>Fx.Oracle(m.Path,Path.Combine(at,"oracle"))).ToArray();
            var crash=Path.Combine(at,"crash");var stage=new Dictionary<string,byte[]>();
            using var stop=new CancellationTokenSource();
            var first=await Start(at,(_,_,_)=>
            {
                var copy=Directory.CreateDirectory(Path.Combine(crash,ResourceNames.EngineFolder)).FullName;
                foreach(var f in Directory.EnumerateFiles(Path.GetDirectoryName(l)!).Where(f=>!f.EndsWith("-shm"))) Fx.CopyOpen(f,Path.Combine(copy,Path.GetFileName(f)));
                foreach(var (path,mark) in marks)
                {
                    var image=Path.Combine(copy,Path.GetFileName(path));
                    try { using var ro=Fx.Connect(image,SqliteOpenMode.ReadOnly);Fx.Scalar(ro,"PRAGMA user_version"); } catch(SqliteException e) { wrong.Add($"{leg}: {Path.GetFileName(path)} has a hot journal: {e.Message}"); }
                    if(Fx.UserVersion(image) is var v && (v!=mark || !Fx.OldRefuses(v))) wrong.Add($"{leg}: {Path.GetFileName(path)} read {v} at the last point before publication");
                }
                if(File.Exists(Path.Combine(copy,ResourceNames.PredecessorFile+"-journal")) || v17 && new FileInfo(Path.Combine(copy,"state.v16.db-journal")) is not {Exists:true,Length:0} || File.Exists(Path.Combine(copy,ResourceNames.DatabaseFile)))
                    wrong.Add(leg+": the journals are not the finalized state, or N already exists");
                foreach(var f in Directory.EnumerateFiles(copy,"*.new")) stage[f]=File.ReadAllBytes(f);
                stop.Cancel();return null;
            },stop.Token);
            if(first is not OperationCanceledException || Fx.Staged(at) || File.Exists(n) || marks.Any(m=>Fx.UserVersion(m.Path)!=m.Mark)) wrong.Add(leg+": the cancelled start published, kept its stage or lost a mark");
            if(await Start(at) is {} again || Fx.UserVersion(n)!=18 || marks.Select((m,i)=>Fx.UserVersion(m.Path)!=m.Mark || !Fx.SameRows(m.Path,oracles[i])).Any(b=>b)) wrong.Add(leg+": the restart did not publish N beside unchanged marked predecessors");
            using(var db=Fx.Connect(n,SqliteOpenMode.ReadOnly)) if(v17 ? !Fx.Bindings(oracles[0],false).SequenceEqual(Fx.Bindings(n,true)) : Convert.ToInt32(Fx.Scalar(db,Fx.V16Converted))!=1) wrong.Add(leg+": N lost a migrated fact");
            if(await Start(crash) is {} resumed || Fx.UserVersion(Engine(crash,ResourceNames.DatabaseFile))!=18 || stage.Count==0 || stage.Any(s=>!File.Exists(s.Key) || !s.Value.SequenceEqual(File.ReadAllBytes(s.Key)))) wrong.Add(leg+": the crash image did not resume, or its orphan stage was touched");
        }
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
    }

    /// <summary>A held v16 backup cannot take its mark after the predecessor's: refused naming it, N absent; released, the next start marks and publishes.</summary>
    private static async Task MixedPhaseAsync(string root)
    {
        var l=await Fx.WritePredecessorAsync(root,Fx.V17Seed);var o=Engine(root,"state.v16.db");await Fx.ReleaseImageAsync(o);var n=Engine(root,ResourceNames.DatabaseFile);
        Exception? held;
        using(new FileStream(o,FileMode.Open,FileAccess.Read,FileShare.Read)) held=await Start(root);
        Check.Require(held?.Message==Text.Get(TextKey.StoreMoveInUse,(TextArg.Path,o)) && Fx.UserVersion(l)==1017 && Fx.UserVersion(o)==16 && !File.Exists(n) && !Fx.Staged(root),
            "A held v16 backup was not refused naming it after the predecessor's mark, or N was published: "+held?.Message);
        Check.Require(await Start(root) is null && Fx.UserVersion(o)==1016 && Fx.UserVersion(n)==18,"Release did not mark the backup and publish N");
    }

    /// <summary>A v16 image with a mark in flight is recovered by SQLite at the next start, migrated from its rolled-back state and marked.</summary>
    private static async Task HotJournalRestartAsync(string root)
    {
        foreach(var name in new[]{ResourceNames.PredecessorFile,"state.v16.db"})
        {
            var at=Path.Combine(root,name);var source=await Fx.InterruptedRetirementAsync(at,name);var n=Engine(at,ResourceNames.DatabaseFile);
            var start=await Start(at);
            using(var db=Fx.Connect(n,SqliteOpenMode.ReadOnly))
                Check.Require(start==null && Convert.ToInt32(Fx.Scalar(db,Fx.V16Converted))==1 && Convert.ToInt32(Fx.Scalar(db,"SELECT count(*) FROM discovery_deleted_delta WHERE path_key LIKE 'U%'"))==0,
                    $"{name}: the hot-journal image was not migrated from its rolled-back state: {start?.Message}");
            using(var ro=Fx.Connect(source,SqliteOpenMode.ReadOnly))
                Check.Require(Convert.ToInt32(Fx.Scalar(ro,"PRAGMA user_version"))==1016 && new FileInfo(source+"-journal") is {Exists:false} or {Length:0},$"{name}: the source was not marked, or kept a non-empty journal");
        }
    }

    /// <summary>A mark that is visible but has no completion proof is written again by this run's own commit before publication.</summary>
    private static async Task UnprovenMarkRetriedAsync(string root)
    {
        var wrong=new List<string>();
        foreach(var v17 in new[]{false,true})
        {
            var at=Path.Combine(root,v17?"wal-and-rollback":"rollback");var l=Engine(at,ResourceNames.PredecessorFile);var n=Engine(at,ResourceNames.DatabaseFile);
            var marks=new List<(string Path,int Mark)>();
            if(v17)
            {
                await Fx.WritePredecessorAsync(at,Fx.V17Seed+"PRAGMA user_version=1017;",true);
                var main=Path.Combine(Directory.CreateDirectory(Path.Combine(at,"main-only")).FullName,ResourceNames.PredecessorFile);Fx.CopyOpen(l,main);
                if(Fx.UserVersion(main)!=17) wrong.Add("WAL leg: the mark reached the main file");
                marks.Add((l,1017));marks.Add((await UnsyncedMarkAsync(Path.Combine(at,"build-o"),Engine(at,"state.v16.db")),1016));
            }
            else marks.Add((await UnsyncedMarkAsync(Path.Combine(at,"build-l"),l),1016));
            var oracles=marks.Select(m=>Fx.Oracle(m.Path,Path.Combine(at,"oracle"))).ToArray();
            foreach(var (path,mark) in marks) if(Fx.UserVersion(path)!=mark) wrong.Add($"{at}: {Path.GetFileName(path)} does not show its visible mark");
            var readers=marks.Select(m=>Fx.Connect(m.Path,SqliteOpenMode.ReadOnly)).ToArray();
            try
            {
                var d0=readers.Select(r=>Convert.ToInt64(Fx.Scalar(r,"PRAGMA data_version"))).ToArray();
                var start=await Start(at,(_,_,_)=>
                {
                    for(var i=0;i<readers.Length;i++)
                        if(Convert.ToInt64(Fx.Scalar(readers[i],"PRAGMA data_version"))==d0[i] || Fx.UserVersion(marks[i].Path)!=marks[i].Mark) wrong.Add($"{at}: {Path.GetFileName(marks[i].Path)} was not committed again before publication");
                    if(File.Exists(n)) wrong.Add(at+": N existed before the rename");
                    return null;
                });
                if(start!=null || Fx.UserVersion(n)!=18 || marks.Select((m,i)=>Fx.UserVersion(m.Path)!=m.Mark || !Fx.SameRows(m.Path,oracles[i])).Any(b=>b)) wrong.Add(at+": not published beside unchanged marked predecessors: "+start?.Message);
            }
            finally { foreach(var r in readers) r.Dispose(); }
        }
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
        // The "truncated, never synced" image: synchronous=OFF hands the mark to the OS without any sync, leaving a zero-length journal.
        static async Task<string> UnsyncedMarkAsync(string build,string target)
        {
            var image=Path.Combine(Directory.CreateDirectory(build).FullName,Path.GetFileName(target));await Fx.ReleaseImageAsync(image);
            using(var db=Fx.Connect(image)) Fx.Exec(db,"PRAGMA journal_mode=TRUNCATE;PRAGMA synchronous=OFF;PRAGMA user_version=1016");
            Check.Require(new FileInfo(image+"-journal") is {Exists:true,Length:0},"The unsynced mark left no zero-length journal");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(image,target);File.Copy(image+"-journal",target+"-journal");
            using(var ro=Fx.Connect(target,SqliteOpenMode.ReadOnly)) Fx.Scalar(ro,"PRAGMA user_version");
            return target;
        }
    }

    // A v18 image on one connection with foreign keys on, as the Committer's connection runs it.
    private static SqliteConnection V18Image()
    {
        var db=new SqliteConnection("Data Source=:memory:");db.Open();
        db.CreateFunction<string,string>("pathkey",PathNames.Key,true);db.CreateFunction<string,string>("pathfolder",PathNames.Folder,true);db.CreateFunction<string,string>("pathname",PathNames.Name,true);
        Fx.Exec(db,"PRAGMA foreign_keys=ON;"+Schema.Create+"INSERT INTO pair(pair_id,def_json) VALUES('p','{}');");
        return db;
    }
    private static SyncedEntry E(string path,string id,string? dropbox=null,bool folder=false,long size=1)=>
        new(PathNames.Key(path),path,PathNames.Key(dropbox??path),dropbox??path,folder,id,size,1,"db-"+id,"h");

    /// <summary>synced_key equals (pair, key of the joined path, id) after every write; a colliding write or rebase aborts with nothing changed.</summary>
    private static async Task KeyUpkeepAsync()
    {
        using var db=V18Image();var committer=new Committer();var wrong=new List<string>();
        string Snapshot()=>Fx.Scalar(db,"SELECT coalesce((SELECT group_concat(r,';') FROM (SELECT b.id||'|'||f.path||'|'||b.name||'|'||quote(b.dropbox_key_delta)||'|'||quote(b.dropbox_path_delta) r FROM synced_entry b JOIN folder f ON f.id=b.folder_id ORDER BY b.id)),'')||'#'||coalesce((SELECT group_concat(r,';') FROM (SELECT pair_id||'|'||key||'|'||entry r FROM synced_key ORDER BY 1)),'')") as string ?? "";
        string Row(string id)=>Fx.Scalar(db,$"SELECT f.path||'|'||b.name||'|'||quote(b.dropbox_key_delta)||'|'||quote(b.dropbox_path_delta)||'|'||k.key FROM synced_entry b JOIN folder f ON f.id=b.folder_id JOIN synced_key k ON k.entry=b.id WHERE b.local_id='{id}'") as string ?? "absent";
        void Keys(string step)
        {
            if(Convert.ToInt32(Fx.Scalar(db,"SELECT (SELECT count(*) FROM synced_key)=(SELECT count(*) FROM synced_entry) AND NOT EXISTS(SELECT 1 FROM synced_entry b JOIN folder f ON f.id=b.folder_id LEFT JOIN synced_key k ON k.pair_id=b.pair_id AND k.key=pathkey(f.path||b.name) AND k.entry=b.id WHERE k.entry IS NULL)"))!=1)
                wrong.Add(step+": synced_key does not match the bindings");
        }
        async Task Write(string step,SyncedEntry? before,SyncedEntry? after,params (string Id,string Row)[] expected)
        {
            await committer.CommitAsync(db,"p",before,after,null,null,default);Keys(step);
            foreach(var (id,row) in expected) if(Row(id) is var actual && actual!=row) wrong.Add($"{step}: {id} is {actual}");
        }
        async Task Project(string step,string from,string to,params (string Id,string Row)[] expected)
        {
            await committer.ProjectSubtreeAsync(db,"p",new(from,from),new(to,to),default);Keys(step);
            foreach(var (id,row) in expected) if(Row(id) is var actual && actual!=row) wrong.Add($"{step}: {id} is {actual}");
        }
        async Task Aborts(string step,Func<Task> write)
        {
            var snapshot=Snapshot();
            // Only a constraint abort or the guarded write's refusal counts; any other failure is not the collision under test.
            try { await write();wrong.Add(step+": the colliding write committed"); } catch(SqliteException e) when(e.SqliteErrorCode==19) { } catch(ChangedUnderneathException) { }
            if(Snapshot()!=snapshot) wrong.Add(step+": the aborted write changed bindings or keys");
        }
        static string K(string path)=>PathNames.Key(path);
        await Write("insert",null,E("A/x","a"),("a",$"A/|x|NULL|NULL|{K("A/x")}"));
        await Write("same-folder rename",E("A/x","a"),E("A/y","a"),("a",$"A/|y|NULL|NULL|{K("A/y")}"));
        await Write("cross-folder rename",E("A/y","a"),E("B/y","a"),("a",$"B/|y|NULL|NULL|{K("B/y")}"));
        await Write("case-only rename",E("B/y","a"),E("B/Y","a"),("a",$"B/|Y|NULL|NULL|{K("B/Y")}"));
        await Write("content-only update",E("B/Y","a"),E("B/Y","a",size:2),("a",$"B/|Y|NULL|NULL|{K("B/Y")}"));
        await Write("delete",E("B/Y","a",size:2),null,("a","absent"));
        await Write("folder",null,E("F","f",folder:true));await Write("child",null,E("F/c","c"));await Write("remote-only",null,E("G/r","r","F/r"),("r",$"G/|r|'{K("F/r")}'|'F/r'|{K("G/r")}"));
        // The folder row commits alone; its descendants move in the separate projection step its callers run next.
        await Write("folder rename",E("F","f",folder:true),E("H","f",folder:true),("f",$"|H|NULL|NULL|{K("H")}"),("c",$"F/|c|NULL|NULL|{K("F/c")}"));
        await Project("folder rebase","F","H",("f",$"|H|NULL|NULL|{K("H")}"),("c",$"H/|c|NULL|NULL|{K("H/c")}"),("r",$"G/|r|'{K("H/r")}'|'H/r'|{K("G/r")}"));
        await Aborts("colliding insert",()=>committer.CommitAsync(db,"p",null,E("H/c","z"),null,null,default));
        await Aborts("colliding rename",()=>committer.CommitAsync(db,"p",E("G/r","r","H/r"),E("H/c","r","H/r"),null,null,default));
        await Write("bound target",null,E("K/c","k"));
        await Write("folder rename onto a bound child",E("H","f",folder:true),E("K","f",folder:true));
        await Aborts("rebase onto a bound key",()=>committer.ProjectSubtreeAsync(db,"p",new("H","H"),new("K","K"),default));
        // T5: "Rebase success with local-only, remote-only and mixed rows, including KEEP rows on both sides and an explicit row whose Dropbox side collides with a local name"
        await Write("keep folder",null,E("P","pf",folder:true));await Write("mixed",null,E("P/m","m"));await Write("local-only",null,E("P/s","s","Y/s"));
        await Write("explicit on a local name",null,E("Q/x","x","P/s"),("x",$"Q/|x|'{K("P/s")}'|'P/s'|{K("Q/x")}"));await Write("remote-only",null,E("G/q","q","P/q"));
        Fx.Exec(db,"UPDATE synced_entry SET keep='[\"a\"]' WHERE local_id='s';UPDATE synced_entry SET keep='[\"b\"]' WHERE local_id='q'");
        string Derive()
        {
            using var cmd=db.CreateCommand();cmd.CommandText=Sql.Get(SqlKey.KeepRowsDerive);cmd.Parameters.AddWithValue("$p","p");using var rd=cmd.ExecuteReader();var rows=new List<string>();
            while(rd.Read()) rows.Add(string.Join("|",new[]{0,1,2,5,6,7}.Select(rd.GetValue)));
            return string.Join(";",rows);
        }
        var s=Fx.Scalar(db,"SELECT id FROM synced_entry WHERE local_id='s'");var q=Fx.Scalar(db,"SELECT id FROM synced_entry WHERE local_id='q'");
        // T5: "KEEP: the ordered Derive list (id, local path, Dropbox path, key, type, value) equals a literal before and after the rebase"
        if(Derive() is var kept && kept!=$"{s}|P/s|Y/s|0|text|a;{q}|G/q|P/q|0|text|b") wrong.Add("KEEP legs before the rebase: "+kept);
        await Write("keep folder rename",E("P","pf",folder:true),E("T","pf",folder:true));
        await Project("keep rebase","P","T",("m",$"T/|m|NULL|NULL|{K("T/m")}"),("s",$"T/|s|'{K("Y/s")}'|'Y/s'|{K("T/s")}"),("x",$"Q/|x|'{K("T/s")}'|'T/s'|{K("Q/x")}"),("q",$"G/|q|'{K("T/q")}'|'T/q'|{K("G/q")}"));
        if(Derive() is var moved && moved!=$"{s}|T/s|Y/s|0|text|a;{q}|G/q|T/q|0|text|b") wrong.Add("KEEP legs after the rebase: "+moved);
        Fx.Exec(db,"DELETE FROM pair WHERE pair_id='p'");
        if(Snapshot()!="#") wrong.Add("pair removal left bindings or keys: "+Snapshot());
        Fx.Exec(db,"INSERT INTO pair(pair_id,def_json) VALUES('p','{}')");
        await Write("re-created pair",null,E("H/c","c2"),("c2",$"H/|c|NULL|NULL|{K("H/c")}"));
        // T5: "Writes: ... ResolveDrive delete-all"; another pair's binding at the same key keeps its row and key.
        Fx.Exec(db,"INSERT INTO pair(pair_id,def_json) VALUES('q','{}')");await committer.CommitAsync(db,"q",null,E("H/c","cq"),null,null,default);
        using(var resolve=db.CreateCommand()){resolve.CommandText=Sql.Get(SqlKey.PairTableResolveDrive2);resolve.Parameters.AddWithValue("$p","p");resolve.ExecuteNonQuery();}
        Keys("resolve drive");
        if(Row("c2")!="absent" || Row("cq")!=$"H/|c|NULL|NULL|{K("H/c")}") wrong.Add($"resolve drive: p kept {Row("c2")}, q has {Row("cq")}");
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
    }

    /// <summary>Only an insert or a cross-folder move interns a folder: one statement each, none for a content-only update or same-folder rename.</summary>
    private static async Task InternCountAsync()
    {
        using var db=V18Image();var texts=new List<string>();var committer=new Committer();var wrong=new List<string>();
        SQLitePCL.strdelegate_trace trace=(_,text)=>{ if(text!=null && !text.StartsWith("--")) texts.Add(text); };
        SQLitePCL.raw.sqlite3_trace(db.Handle!,trace,null);
        async Task Interns(string step,SyncedEntry? before,SyncedEntry after,int expected)
        {
            texts.Clear();await committer.CommitAsync(db,"p",before,after,null,null,default);
            var n=texts.Count(t=>System.Text.RegularExpressions.Regex.IsMatch(t,@"\bINSERT\s+(?:OR\s+\w+\s+)?INTO\s+folder\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if(n!=expected) wrong.Add($"{step}: {n} folder intern statements");
            // T7: "content-only update and same-folder rename issue no INSERT INTO folder and bind NULL $parent" (legacy trace text carries bound values).
            if(before!=null && texts.FirstOrDefault(t=>t.StartsWith("UPDATE synced_entry SET folder_id=iif(")) is var update && update?.Contains("iif(NULL IS NULL")!=(expected==0)) wrong.Add($"{step}: $parent in {update ?? "no update"}");
        }
        await Interns("insert",null,E("A/x","a"),1);
        await Interns("content-only update",E("A/x","a"),E("A/x","a",size:2),0);
        await Interns("same-folder rename",E("A/x","a",size:2),E("A/y","a",size:2),0);
        await Interns("cross-folder move",E("A/y","a",size:2),E("B/y","a",size:2),1);
        SQLitePCL.raw.sqlite3_trace(db.Handle!,(SQLitePCL.strdelegate_trace)null!,null);GC.KeepAlive(trace);
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
    }

    /// <summary>Every traced store statement on synced_entry, synced_key or folder reaches them by key or index on a v18 image without statistics.</summary>
    private static async Task ReadPlansAsync(string root)
    {
        await new StoreMove().CreateAsync(root,default);var path=Engine(root,ResourceNames.DatabaseFile);
        var texts=new List<string>();
        SQLitePCL.strdelegate_trace trace=(_,text)=>{ if(text!=null && !text.StartsWith("--")) lock(texts) texts.Add(text); };
        using(var store=new Store(path,false,SettingsParser.Load(),(db,opening)=>{ if(opening) SQLitePCL.raw.sqlite3_trace(db.Handle!,trace,null); }))
        {
            await store.SavePairAsync(Scenarios.ItemChecks.Pair() with {PairId="p"},default);
            await store.CommitAsync("p",null,E("A/x","a"),null,null,default);await store.CommitAsync("p",null,E("A/y","b","N/Y"),null,null,default);
            await store.FindAsync("p",Side.Local,PathNames.Key("A/x"),default);await store.FindAsync("p",Side.Dropbox,PathNames.Key("N/Y"),default);
            foreach(var side in new[]{Side.Local,Side.Dropbox})
            {
                await foreach(var _ in store.ByIdAsync("p",side,side==Side.Local?"a":"db-a",default)) { }
                await foreach(var _ in store.DescendantsAsync("p",side,PathNames.Key(side==Side.Local?"A":"N"),default)) { }
            }
            var scan=Guid.NewGuid();store.BeginScan("p",scan);
            await store.AddScanAsync("p",scan,Side.Local,Listing(new("A/z",Scenarios.ItemChecks.File(Side.Local,"A/z",1,"a"))),null,default);
            await foreach(var _ in store.RenamesAsync("p",scan,null,default)) { }
            await foreach(var _ in store.CompareAsync("p",scan,null,(_,_)=>(true,false),default)) { }
            await store.ClearScanAsync("p",scan,default);
            await store.CommitAsync("p",E("A/x","a"),E("B/x","a"),null,null,default);await store.CommitAsync("p",E("B/x","a"),null,null,null,default);
        }
        GC.KeepAlive(trace);
        using var plan=Open(path,SqliteOpenMode.ReadWrite);
        plan.CreateFunction<string,string,bool>("scansubtree",PathNames.IsSelfOrUnder,true);plan.CreateFunction<string,string,bool>("sameplace",PathNames.SamePlace,true);plan.CreateFunction<string,string,bool>("samename",PathNames.SameName,true);
        Fx.Exec(plan,"ATTACH ':memory:' AS scan_state;"+Schema.CreateScan);
        var table=new System.Text.RegularExpressions.Regex(@"\b(synced_entry|synced_key|folder)\b(?:\s+(?:AS\s+)?(?!(?:WHERE|JOIN|ON|LEFT|INNER|CROSS|USING|ORDER|GROUP|LIMIT|UNION|SET|INDEXED|NOT|VALUES|DEFAULT|SELECT)\b)(\w+))?",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var scans=new List<string>();var planned=0;
        foreach(var text in texts.Distinct().Where(t=>table.IsMatch(t)))
        {
            var names=table.Matches(text).SelectMany(m=>new[]{m.Groups[1].Value,m.Groups[2].Value}).Where(v=>v!="").ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var cmd=plan.CreateCommand();cmd.CommandText="EXPLAIN QUERY PLAN "+text;using var rd=cmd.ExecuteReader();planned++;
                while(rd.Read()) if(System.Text.RegularExpressions.Regex.Match(rd.GetString(3),@"^SCAN (\w+)") is {Success:true} m && names.Contains(m.Groups[1].Value)) scans.Add(m.Value+" in: "+text);
            }
            catch(SqliteException e) { scans.Add("unprepared ("+e.Message+"): "+text); }
        }
        Check.Require(planned>0 && scans.Count==0,"A store statement scans a v18 binding table: "+string.Join(" | ",scans));
        static async IAsyncEnumerable<Listed> Listing(Listed row) { await Task.CompletedTask;yield return row; }
    }

    /// <summary>Evidence at two sizes: v18 allocates fewer bytes per binding than v17; with auto_vacuum=2 the published store has no free page.</summary>
    private static async Task CompactionAsync(string root)
    {
        var wrong=new List<string>();
        foreach(var vacuum in new[]{0,2}) foreach(var size in new[]{5110,20440})
        {
            var at=Path.Combine(root,$"av{vacuum}-{size}");var n=Engine(at,ResourceNames.DatabaseFile);
            // Realistic shape: a 125-character folder, a 19-character name, 10 bindings per folder.
            var l=await Fx.WritePredecessorAsync(at,$"INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p','{{\"ConnectionId\":\"c\"}}');INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) WITH RECURSIVE n(v) AS (SELECT 0 UNION ALL SELECT v+1 FROM n WHERE v<{size-1}) SELECT 1,printf('%0125d/%019d',v/10,v),0,v,1,1,'db'||v,X'01' FROM n;",false,vacuum);
            await new StoreMove().MoveWithOutcomeAsync(at,at,(_,_,_)=>null,default);
            var (source,_)=Allocated(l);var (published,free)=Allocated(n);
            Console.WriteLine($"Storage evidence auto_vacuum={vacuum} size={size}: v17 {source/size} B/item, v18 {published/size} B/item, N file {new FileInfo(n).Length} B, N free pages {free}, predecessor kept {new FileInfo(l).Length} B");
            if(published>=source) wrong.Add($"auto_vacuum={vacuum} size={size}: v18 allocates {published} B, v17 {source} B");
            if(vacuum==2 && free!=0) wrong.Add($"auto_vacuum=2 size={size}: N has {free} free pages");
        }
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
        static (long Bytes,long Free) Allocated(string path)
        {
            using var db=Fx.Connect(path,SqliteOpenMode.ReadOnly);var free=Convert.ToInt64(Fx.Scalar(db,"PRAGMA freelist_count"));
            return ((Convert.ToInt64(Fx.Scalar(db,"PRAGMA page_count"))-free)*Convert.ToInt64(Fx.Scalar(db,"PRAGMA page_size")),free);
        }
    }

    /// <summary>Exact-name rename probes on a real store: an ancestor's case change is no rename, the leaf's case change is a candidate.</summary>
    public static async Task RenameExactNameAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var def=System.Text.Json.JsonSerializer.Serialize(Scenarios.ItemChecks.Pair() with {PairId="p"}).Replace("'","''");
            await Fx.WritePredecessorAsync(root,$"""
                INSERT INTO pair(id,pair_id,def_json,first_repair_done) VALUES(1,'p','{def}',1);
                INSERT INTO synced_entry(pair_id,local_path,dropbox_key_delta,dropbox_path_delta,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) VALUES
                  (1,'Parent/x',NULL,'Parent/X',0,'pc-i',1,1,'db-i','h'),(1,'M/x',pathkey('N/X'),'N/X',0,'pc-e',1,1,'db-e','h'),(1,'Ä/x',NULL,NULL,0,'pc-u',1,1,'db-u','h');
                """);
            await new StoreMove().MoveWithOutcomeAsync(root,root,(_,_,_)=>null,default);
            using var store=new Store(Path.Combine(root,ResourceNames.EngineFolder,ResourceNames.DatabaseFile),false,SettingsParser.Load());
            async Task<int> Candidates(Side side,string path,string id)
            {
                var scan=Guid.NewGuid();store.BeginScan("p",scan);
                try
                {
                    await store.AddScanAsync("p",scan,side,One(new(path,Scenarios.ItemChecks.File(side,path,1,id))),null,default);
                    var n=0;await foreach(var _ in store.RenamesAsync("p",scan,null,default)) n++;return n;
                }
                finally { await store.ClearScanAsync("p",scan,default); }
            }
            var wrong=new List<string>();
            foreach(var (side,path,id,expected) in new[]{(Side.Dropbox,"parent/X","db-i",0),(Side.Dropbox,"parent/x","db-i",1),(Side.Local,"parent/x","pc-i",0),(Side.Local,"Parent/X","pc-i",1),(Side.Dropbox,"n/X","db-e",0),(Side.Dropbox,"N/x","db-e",1),(Side.Dropbox,"ä/x","db-u",0)})
                if(await Candidates(side,path,id) is var n && n!=expected) wrong.Add($"{side} '{path}' gave {n} candidates");
            Check.Require(wrong.Count==0,"The exact-name rename probe changed: "+string.Join("; ",wrong));
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
        static async IAsyncEnumerable<Listed> One(Listed row) { await Task.CompletedTask;yield return row; }
    }

    private static void VerifyBackup(string path)
    {
        using var db=Open(path,SqliteOpenMode.ReadOnly);using var read=db.CreateCommand();
        read.CommandText="SELECT (SELECT user_version FROM pragma_user_version)=1016 AND EXISTS(SELECT 1 FROM pair p JOIN issue i ON i.pair_id=p.id JOIN issue_member m USING(issue_id) JOIN change_work w USING(work_id) WHERE p.pair_id='p' AND i.issue_id=20 AND m.identity='child-id' AND w.issue_work_key='branch')";
        Check.Require(Convert.ToInt32(read.ExecuteScalar())==1,"Backup did not retain the original v16 facts");
    }

}
