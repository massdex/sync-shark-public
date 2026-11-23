using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;
using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Tests.Storage;
/// <summary>Exercises closed-image publication on synthetic stores.</summary>
public static class StoreMoveChecks
{
    /// <summary>Startup must not create a store; explicit creation publishes once and preserves an existing image.</summary>
    public static async Task RunAsync()
    {
        var failures=new List<string>();
        async Task Probe(string name,Func<Task> test){try{await test();}catch(Exception ex){failures.Add(name+": "+ex.Message);}}
        await Probe("documentation",DeletedNamesChecks.VerifyAcquisitionAsync);
        await Probe("published lookup",DeletedNamesChecks.PublishedHistoryAsync);
        await Probe("codec ownership",()=>{Check.Require(!typeof(ScanTable).GetMethods(System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Any(m=>m.Name is "EntryAt" or "BindingAt"),"Unexpected scan codec wrapper");return Task.CompletedTask;});
        await Probe("restored partition",async()=>{await DeletedNamesChecks.RestorePartitionAsync(32);await DeletedNamesChecks.RestorePartitionAsync(64);});
        await Probe("saved lookup",DeletedNamesChecks.SavedLookupAsync);
        await Probe("verify before listing",DeletedNamesChecks.VerifyBeforeListingAsync);
        await Probe("corrupt history transition",DeletedNamesChecks.InvalidationBoundaryAsync);
        await Probe("incomplete acquisition",DeletedNamesChecks.VerifyUnreadableHistoryAsync);
        await Probe("indexed ingest",()=>ScanTableChecks.VerifyCountedWorkAsync(true));
        await Probe("open cursor WAL",ScanTableChecks.SnapshotCostAsync);
        await Probe("unchanged hard links",ScanTableChecks.AliasCostAsync);
        await Probe("notice ownership",()=>ScanTableChecks.MappedNoticesAsync(false));
        await Probe("mapped ordering",()=>ScanTableChecks.MappedOrderAsync());
        await Probe("bounded compare",()=>ScanTableChecks.VerifyCountedWorkAsync(false));

        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var move = new StoreMove();
            var path = Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.DatabaseFile);
            await move.MoveWithOutcomeAsync(root, root, (_, _, _) => throw new InvalidOperationException(), default);
            Check.Require(!File.Exists(path), "Startup created a fresh store");
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
            var abandoned = path + ".previous.new";
            await File.WriteAllTextAsync(abandoned, "abandoned stage");
            var pointer = Path.Combine(root, "product-root.txt");
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
            await File.WriteAllTextAsync(pointer, Path.Combine(root, "missing"));
            try
            {
                await move.MoveWithOutcomeAsync(root, root, (_, _, _) => null, default);
                throw new Exception("Dangling pointer accepted");
            }
            catch (InvalidDataException)
            {
            }

            Check.Require(File.Exists(pointer) && original.SequenceEqual(await File.ReadAllBytesAsync(path)), "Refusal modified the source or pointer");
            await Scenarios.StoreMoveChecks.CreateFixtureAsync();
            await Probe("pair info coexistence",()=>RootInfosAsync(Path.Combine(root,"infos")));
            await Probe("mixed skip subject",()=>RecoveredBackupAsync(Path.Combine(root,"recovery")));
            await Probe("external switch restart",()=>ExternalSwitchAsync(Path.Combine(root,"restart")));
            await Probe("external pointer",()=>PointerAsync(Path.Combine(root,"external")));
            await Probe("migrated notice",()=>MigratedAsync(Path.Combine(root,"members"),false));
            await Probe("original backup",()=>MigratedAsync(Path.Combine(root,"backup"),true));
            Check.Require(failures.Count==0,string.Join("; ",failures));
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
                INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed) VALUES
                  (8,1,4,'SkipLink','a','{"Items":[]}',1,0),(9,1,4,'SkipLink','b','{"Items":[{"Side":0,"Path":"b","Id":"old-link"},{"Side":1,"Path":"b","Id":"still-skipped"}],"Incoming":0}',2,0);
                INSERT INTO issue_member(member_id,issue_id,pair_id,side,path_key,subject_json,skip_cause) VALUES
                  (1,8,1,0,'a','{"Version":1,"Value":{"Items":[]}}','SkipLink'),
                  (2,9,1,0,pathkey('b'),'{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"b","Id":"old-link"}]}}','SkipLink'),
                  (3,9,1,1,pathkey('b'),'{"Version":1,"Value":{"Items":[{"Side":"Dropbox","Path":"b","Id":"still-skipped"}]}}','SkipSpecial');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }
    private static async Task RootInfosAsync(string root)
    {
        await CreateReleaseAsync(root,ResourceNames.DatabaseFile);var path=Path.Combine(root,ResourceNames.EngineFolder,ResourceNames.DatabaseFile);
        using(var db=Open(path,SqliteOpenMode.ReadWrite)){using var seed=db.CreateCommand();seed.CommandText="INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed) VALUES(25,1,3,'DeletedHistoryCorrupt','','{\"Items\":[],\"Incoming\":null}',17,1)";await seed.ExecuteNonQueryAsync();}
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
        var expected=new NoticeItem(Side.Dropbox,"b","still-skipped");var actual=System.Text.Json.JsonSerializer.Deserialize<IssueSubject>(mixed.GetString(1));
        Check.Require(mixed.GetString(0)==nameof(TextKey.SkipSpecial) && actual?.Items.SequenceEqual(new[]{expected})==true && actual.Incoming==null,"Mixed notice retained stale link cause or subject");
        if(publicRead){await mixed.DisposeAsync();var notice=await new NoticeTable().NoticeAsync(opened,"p",9,default);Check.Require(notice?.Cause==TextKey.SkipSpecial && notice.Subject.Items.SequenceEqual(new[]{expected}),"Public notice read returned a stale migrated link");}
    }

    internal static async Task VerifyPublicMixedAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try{await RecoveredBackupAsync(root,true);}finally{Directory.Delete(root,true);}
    }
    private static SqliteConnection Open(string path,SqliteOpenMode mode)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=mode,Pooling=false}.ToString());db.Open();db.CreateFunction<string,string>("pathkey",PathNames.Key,true);db.CreateFunction<string,int>("plandepth",PathNames.Depth,true);db.CreateFunction<string,int>("localaccessretrycause",_=>0,true);return db;
    }
    private static async Task ExternalSwitchAsync(string root)
    {
        var external=Path.Combine(root,"external");var fixedRoot=Directory.CreateDirectory(Path.Combine(root,"fixed")).FullName;
        await CreateReleaseAsync(external,ResourceNames.DatabaseFile);var pointer=Path.Combine(fixedRoot,"product-root.txt");await File.WriteAllTextAsync(pointer,external);
        using (var locked=new FileStream(pointer,FileMode.Open,FileAccess.Read,FileShare.Read))
            Check.Require(!string.IsNullOrEmpty(await new StoreMove().MoveWithOutcomeAsync(fixedRoot,fixedRoot,(_,_,_)=>null,default)),"Published external move lost its cleanup warning");
        var original=Path.Combine(external,ResourceNames.EngineFolder,ResourceNames.DatabaseFile);var published=Path.Combine(fixedRoot,ResourceNames.EngineFolder,ResourceNames.DatabaseFile);
        static int Version(string p){using var db=Open(p,SqliteOpenMode.ReadOnly);using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";return Convert.ToInt32(cmd.ExecuteScalar());}
        try{await new StoreMove().MoveWithOutcomeAsync(fixedRoot,fixedRoot,(_,_,_)=>null,default);throw new Exception("Restart treated occupancy as lineage");}catch(InvalidDataException){}
        Check.Require(Version(original)==16 && Version(published)==Schema.Version && await File.ReadAllTextAsync(pointer)==external,"Unproven restart changed either store or its locator");
    }
    private static async Task PointerAsync(string root)
    {
        var oldRoot=Path.Combine(root,"old");var newRoot=Path.Combine(root,"new");await CreateReleaseAsync(oldRoot,ResourceNames.DatabaseFile);
        var move=new StoreMove();await move.CreateAsync(newRoot,default);
        using(var db=Open(Path.Combine(newRoot,ResourceNames.EngineFolder,ResourceNames.DatabaseFile),SqliteOpenMode.ReadWrite)){using var seed=db.CreateCommand();seed.CommandText="INSERT INTO pair(pair_id,def_json) VALUES('other','{\"ConnectionId\":\"c\"}')";seed.ExecuteNonQuery();}
        var pointer=Path.Combine(newRoot,"product-root.txt");await File.WriteAllTextAsync(pointer,oldRoot);bool refused=false;
        try{await move.MoveWithOutcomeAsync(newRoot,newRoot,(_,_,_)=>null,default);}catch(InvalidDataException){refused=true;}
        Check.Require(refused && File.Exists(pointer) && await File.ReadAllTextAsync(pointer)==oldRoot,"Independent occupied target consumed the predecessor pointer");
    }
    private static async Task MigratedAsync(string root,bool backup,bool decode=false)
    {
        await CreateReleaseAsync(root,ResourceNames.DatabaseFile);var folder=Path.Combine(root,ResourceNames.EngineFolder);var target=Path.Combine(folder,ResourceNames.DatabaseFile);
        using(var db=Open(target,SqliteOpenMode.ReadWrite)){using var seed=db.CreateCommand();seed.CommandText="""
            INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks) VALUES(20,1,0,'FailChanged','parent','{"Items":[{"Side":0,"Path":"parent","Id":"folder"}],"Incoming":null}',1);
            INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks) VALUES(21,1,0,'FailChanged','parent/legacy-header','{"Items":[{"Side":0,"Path":"parent/alone","Id":"alone"}],"Incoming":null}',1);
            INSERT INTO change_work(work_id,work_origin,issue_work_key,pair_id,pair_generation,source_epoch,side,domain,path_key) VALUES(1,'Issue','branch',1,0,0,0,'path',pathkey('parent/child'));
            INSERT INTO issue_member(issue_id,pair_id,work_id,side,path_key,identity,subject_json) VALUES(20,1,1,0,pathkey('parent/child'),'child-id','{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"parent","Id":"folder"}],"Incoming":null}}');
            """;seed.ExecuteNonQuery();}
        var move=new StoreMove();await move.MoveWithOutcomeAsync(root,root,(_,_,_)=>null,default);
        if(backup){var original=Path.Combine(folder,"state.v16.db");VerifyBackup(original);
            // Mutation control: the published v17 image must never satisfy the retention proof in place of its predecessor.
            bool rejected=false;try{VerifyBackup(target);}catch(Exception e) when(e is SqliteException or InvalidDataException){rejected=true;}
            Check.Require(rejected,"Backup assertion accepted the replacement as the original");return;}
        using var migrated=Open(target,SqliteOpenMode.ReadWrite);using var attach=migrated.CreateCommand();attach.CommandText="ATTACH ':memory:' AS scan_state;"+Schema.CreateScan;attach.ExecuteNonQuery();var scan=new ScanTable();var id=Guid.NewGuid();scan.Begin("p",id);var seen=false;
        using var counts=migrated.CreateCommand();counts.CommandText="SELECT (SELECT count(*) FROM issue_member WHERE issue_id=20)=1 AND (SELECT count(*) FROM issue_member WHERE issue_id=21)=0";
        Check.Require(Convert.ToInt32(counts.ExecuteScalar())==1,"Unaffected notice acquired a physical member");var alone=false;
        if(!decode)
        {
            using var footprints=ScanTableChecks.PageQuery(migrated,id,[PathNames.Key("parent/child"),PathNames.Key("parent/legacy-header")],"MemberRows");using var rows=await footprints.ExecuteReaderAsync();
            while(await rows.ReadAsync()){if(rows.GetInt64(1)==20)seen=!rows.IsDBNull(9)&&rows.GetString(12)=="child-id" && PathNames.Same(rows.GetString(11),"parent/child");if(rows.GetInt64(1)==21)alone=rows.IsDBNull(9)&&PathNames.Same(rows.GetString(0),"parent/legacy-header") && System.Text.Json.JsonSerializer.Deserialize<IssueSubject>(rows.GetString(5))?.Items.SequenceEqual(new[]{new NoticeItem(Side.Local,"parent/alone","alone")})==true;}
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
    private static void VerifyBackup(string path)
    {
        using var db=Open(path,SqliteOpenMode.ReadOnly);using var read=db.CreateCommand();
        read.CommandText="SELECT (SELECT user_version FROM pragma_user_version)=16 AND EXISTS(SELECT 1 FROM pair p JOIN issue i ON i.pair_id=p.id JOIN issue_member m USING(issue_id) JOIN change_work w USING(work_id) WHERE p.pair_id='p' AND i.issue_id=20 AND m.identity='child-id' AND w.issue_work_key='branch')";
        Check.Require(Convert.ToInt32(read.ExecuteScalar())==1,"Backup did not retain the original v16 facts");
    }

}
