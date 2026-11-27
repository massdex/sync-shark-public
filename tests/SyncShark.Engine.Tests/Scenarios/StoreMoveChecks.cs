using Microsoft.Data.Sqlite;
using Massdex.SyncShark.Engine.Storage;
namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Builds the release schema from its creation DDL, using only synthetic data.</summary>
public static class StoreMoveChecks
{
    public static async Task CreateFixtureAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var folder=Directory.CreateDirectory(Path.Combine(root,ResourceNames.EngineFolder)).FullName;var source=Path.Combine(folder,ResourceNames.PredecessorFile);var path=Path.Combine(folder,ResourceNames.DatabaseFile);
            string Connect(SqliteOpenMode mode,string file)=>new SqliteConnectionStringBuilder{DataSource=file,Mode=mode,Pooling=false}.ToString();
            var pair=ItemChecks.Pair();
            using(var db=new SqliteConnection(Connect(SqliteOpenMode.ReadWriteCreate,source)))
            {
                await db.OpenAsync();db.CreateFunction<string,string>("pathkey",PathNames.Key,isDeterministic:true);db.CreateFunction<string,int>("plandepth",PathNames.Depth,isDeterministic:true);db.CreateFunction<string,int>("localaccessretrycause",_=>0,isDeterministic:true);
                using var cmd=db.CreateCommand();cmd.CommandText=ReleaseSchema;await cmd.ExecuteNonQueryAsync();
                cmd.CommandText="INSERT INTO pair(id,pair_id,def_json) VALUES(1,$id,$def); INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) VALUES(1,'item',0,101,1,10,'cloud',X'01'); INSERT INTO journal_position VALUES('volume',3,17);";
                cmd.Parameters.AddWithValue("$id",pair.PairId);cmd.Parameters.AddWithValue("$def",System.Text.Json.JsonSerializer.Serialize(pair));await cmd.ExecuteNonQueryAsync();
            }
            bool verified=false;await new StoreMove().MoveAsync(root,root,(source,target,ids)=>{verified=ids.Contains(pair.ConnectionId);return null;},default);
            using var migrated=new SqliteConnection(Connect(SqliteOpenMode.ReadOnly,path));await migrated.OpenAsync();migrated.CreateFunction<string,string>("pathkey",PathNames.Key,isDeterministic:true);
            using var read=migrated.CreateCommand();read.CommandText="PRAGMA user_version";Check.Require(verified && Convert.ToInt32(await read.ExecuteScalarAsync())==Schema.Version,"Migration did not verify and switch");
            read.CommandText="SELECT count(*) FROM pair p JOIN synced_entry s ON p.id=s.pair_id JOIN journal_position j ON j.volume_key='volume' WHERE p.pair_id=$id AND s.local_id=101 AND s.dropbox_id='cloud' AND j.journal_id=3 AND j.next_usn=17";read.Parameters.AddWithValue("$id",pair.PairId);Check.Require(Convert.ToInt32(await read.ExecuteScalarAsync())==1 && UserVersion(source)==1016,"Migration lost stored facts or left its source unmarked");
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }

    /// <summary>The v17 predecessor fixture: a closed image and a WAL crash image whose seed is only in its WAL.</summary>
    public static async Task PredecessorFixtureAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach(var wal in new[]{false,true})
            {
                var leg=Path.Combine(root,wal?"wal":"closed");
                var path=await WritePredecessorAsync(leg,"INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p','{\"ConnectionId\":\"c\"}');",wal);
                var copy=Oracle(path,Path.Combine(leg,"oracle"));
                using(var db=Connect(copy))
                    Check.Require(Convert.ToInt32(Scalar(db,"PRAGMA user_version"))==17 && Equals(Scalar(db,"PRAGMA journal_mode"),"wal") && Convert.ToInt32(Scalar(db,"PRAGMA auto_vacuum"))==2 &&
                        Convert.ToInt32(Scalar(db,"PRAGMA page_size"))==4096 && Convert.ToInt32(Scalar(db,"SELECT count(*) FROM pair"))==1,"The v17 fixture is not the previous release's shape or lost its seed");
                if(!wal) continue;
                var main=Path.Combine(leg,"main-only","state.db");Directory.CreateDirectory(Path.GetDirectoryName(main)!);CopyOpen(path,main);
                using(var db=Connect(main)) Check.Require(Convert.ToInt32(Scalar(db,"SELECT count(*) FROM pair"))==0,"The WAL leg's seed reached the main file");
            }
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }

    /// <summary>The interrupted-retirement fixture: a v16 rollback-journal image whose mark is in flight with a hot journal.</summary>
    public static async Task InterruptedRetirementFixtureAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path=await InterruptedRetirementAsync(root,"state.db");
            var readOnly=Path.Combine(root,"ro","state.db");Directory.CreateDirectory(Path.GetDirectoryName(readOnly)!);
            foreach(var suffix in new[]{"","-journal"}) CopyOpen(path+suffix,readOnly+suffix);
            try { using var db=Connect(readOnly,SqliteOpenMode.ReadOnly);Scalar(db,"PRAGMA user_version");throw new InvalidDataException("A read-only open read a hot journal"); }
            catch(SqliteException e) when(e.SqliteExtendedErrorCode==776) { }
            using var recovered=Connect(Oracle(path,Path.Combine(root,"rw")));
            Check.Require(Convert.ToInt32(Scalar(recovered,"PRAGMA user_version"))==16 && Convert.ToInt32(Scalar(recovered,"SELECT count(*) FROM discovery_deleted_delta WHERE path_key LIKE 'U%'"))==0,"SQLite did not roll the interrupted mark back");
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }

    /// <summary>The 0.3.95 WAL floor fixture: the main file alone reads version 0, main plus WAL reads 16.</summary>
    public static async Task ReleaseWalFixtureAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path=ReleaseWal(root);var main=Path.Combine(root,"main-only","state.db");Directory.CreateDirectory(Path.GetDirectoryName(main)!);CopyOpen(path,main);
            using(var db=Connect(main)) Check.Require(Convert.ToInt32(Scalar(db,"PRAGMA user_version"))==0,"The WAL floor's main file already holds the schema");
            Check.Require(UserVersion(path)==16,"The WAL floor does not read version 16 through its WAL");
            await Task.CompletedTask;
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }

    /// <summary>Migration round trip per source version: every binding field-equal by id, v16 conversions as converted, the source marked and unchanged.</summary>
    public static async Task MigrationRoundTripAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;var wrong=new List<string>();
        try
        {
            foreach(var wal in new[]{false,true})
            {
                var leg=Path.Combine(root,wal?"v17-wal":"v17");
                var l=await WritePredecessorAsync(leg,V17Seed,wal);var oracle=Oracle(l,Path.Combine(leg,"oracle"));
                await new StoreMove().MoveWithOutcomeAsync(leg,leg,(_,_,_)=>null,default);
                var n=Path.Combine(leg,ResourceNames.EngineFolder,ResourceNames.DatabaseFile);
                if(!Bindings(oracle,false).SequenceEqual(Bindings(n,true))) wrong.Add($"{leg}: a binding changed: {string.Join(" | ",Bindings(n,true))}");
                if(!SameRows(oracle,n,"synced_entry")) wrong.Add(leg+": an unaffected table changed");
                if(!SameRows(l,oracle) || UserVersion(l)!=1017 || Staged(leg)) wrong.Add(leg+": the source was changed, left unmarked, or a stage was left");
            }
            foreach(var wal in new[]{false,true})
            {
                var leg=Path.Combine(root,wal?"v16-wal":"v16");
                string l;if(wal) l=ReleaseWal(leg); else {l=Path.Combine(Directory.CreateDirectory(Path.Combine(leg,ResourceNames.EngineFolder)).FullName,"state.db");await ReleaseImageAsync(l);using var db=Connect(l);Exec(db,ReleaseJobs);}
                var oracle=Oracle(l,Path.Combine(leg,"oracle"));
                await new StoreMove().MoveWithOutcomeAsync(leg,leg,(_,_,_)=>null,default);
                using(var db=Connect(Path.Combine(leg,ResourceNames.EngineFolder,ResourceNames.DatabaseFile),SqliteOpenMode.ReadOnly))
                    if(Convert.ToInt32(Scalar(db,V16Converted))!=1 || Convert.ToInt32(Scalar(db,"""
                        SELECT (SELECT count(*) FROM accepted_job WHERE route='files/move_batch/check_v2' AND job_id='job-move' AND json_extract(launch,'$[0].Path')='from/child')=1
                        AND (SELECT count(*) FROM source_state WHERE repair_reason IS NOT NULL)=2
                        """))!=1) wrong.Add(leg+": the v16 conversion lost a folded notice, a job, the owed Repair, the cursor or a binding");
                if(!SameRows(l,oracle) || UserVersion(l)!=1016 || Staged(leg)) wrong.Add(leg+": the v16 source was changed, left unmarked, or a stage was left");
            }
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
    }

    /// <summary>Migration states at the next start, refusals naming the exact file, and release of each held handle.</summary>
    public static async Task MigrationStatesAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;var wrong=new List<string>();var leg=0;
        async Task<Exception?> Start(string at,Func<string,string,IReadOnlyList<string>,Action?>? verify=null,CancellationToken ct=default){try{await new StoreMove().MoveWithOutcomeAsync(at,at,verify??((_,_,_)=>null),ct);return null;}catch(Exception e){return e;}}
        string Next()=>Path.Combine(root,"leg"+leg++);
        static string Engine(string at,string name)=>Path.Combine(at,ResourceNames.EngineFolder,name);
        static bool Names(Exception? e,TextKey key,string path)=>e?.Message==Text.Get(key,(key==TextKey.RecoveryStageLocationBlocked?TextArg.Place:TextArg.Path,path));
        try
        {
            // facts committed only in the WAL reach N.
            var a=Next();await WritePredecessorAsync(a,V17Seed,true);
            if(await Start(a) is {} wa || Bindings(Engine(a,ResourceNames.DatabaseFile),true).Count!=4) wrong.Add("the WAL-only facts did not reach N");
            // N beside a marked L is current; L is never read again and later facts in N survive.
            var lBytes=await File.ReadAllBytesAsync(Engine(a,"state.db"));
            using(var db=Connect(Engine(a,ResourceNames.DatabaseFile))) Exec(db,"INSERT INTO pair(pair_id,def_json) VALUES('later','{\"ConnectionId\":\"c\"}')");
            if(await Start(a) is {} wb || !lBytes.SequenceEqual(await File.ReadAllBytesAsync(Engine(a,"state.db"))) || UserVersion(Engine(a,ResourceNames.DatabaseFile))!=18) wrong.Add("a second start migrated again or touched L");
            using(var db=Connect(Engine(a,ResourceNames.DatabaseFile),SqliteOpenMode.ReadOnly)) if(Convert.ToInt32(Scalar(db,"SELECT count(*) FROM pair WHERE pair_id='later'"))!=1) wrong.Add("a fact written into N after the first start was lost");
            // O only, and L(17) beside O(16).
            var o=Next();var oPath=Engine(o,"state.v16.db");Directory.CreateDirectory(Path.GetDirectoryName(oPath)!);await ReleaseImageAsync(oPath);
            if(await Start(o) is {} ob || UserVersion(oPath)!=1016 || UserVersion(Engine(o,ResourceNames.DatabaseFile))!=18) wrong.Add("O only: not migrated from O, or O unmarked");
            var lo=Next();await WritePredecessorAsync(lo,V17Seed);await ReleaseImageAsync(Engine(lo,"state.v16.db"));
            if(await Start(lo) is {} lob || UserVersion(Engine(lo,"state.db"))!=1017 || UserVersion(Engine(lo,"state.v16.db"))!=1016 || Bindings(Engine(lo,ResourceNames.DatabaseFile),true).Count!=4) wrong.Add("L(17)+O(16): not migrated from L with both marked");
            // Wrong O and two current stores: refused naming the file, nothing marked.
            var o17=Next();Directory.CreateDirectory(Path.Combine(o17,ResourceNames.EngineFolder));var built=await WritePredecessorAsync(Path.Combine(o17,"build17"),V17Seed);File.Copy(built,Engine(o17,"state.v16.db"));
            if(!Names(await Start(o17),TextKey.RecoveryStageLocationBlocked,Engine(o17,"state.v16.db")) || UserVersion(Engine(o17,"state.v16.db"))!=17) wrong.Add("O reading 17 was not refused naming it, or was marked");
            var l16=Next();var l16Path=Engine(l16,"state.db");Directory.CreateDirectory(Path.GetDirectoryName(l16Path)!);await ReleaseImageAsync(l16Path);await ReleaseImageAsync(Engine(l16,"state.v16.db"));
            if(!Names(await Start(l16),TextKey.RecoveryStageLocationBlocked,l16Path) || UserVersion(l16Path)!=16 || UserVersion(Engine(l16,"state.v16.db"))!=16) wrong.Add("L(16)+O(16) was not refused naming L, or was marked");
            var nx=Next();var external=Path.Combine(nx,"external");await new StoreMove().CreateAsync(external,default);await new StoreMove().CreateAsync(nx,default);await File.WriteAllTextAsync(Path.Combine(nx,"product-root.txt"),external);
            if(!Names(await Start(nx),TextKey.RecoveryStageLocationBlocked,Engine(external,ResourceNames.DatabaseFile))) wrong.Add("Local N beside an external N was not refused naming the external store");
            // an idle writer on L lets the mark commit but fails the hold; released, the next start migrates.
            var c1=Next();var c1L=await WritePredecessorAsync(c1,V17Seed);
            using(var writer=Connect(c1L,SqliteOpenMode.ReadWrite))
            {
                Scalar(writer,"SELECT count(*) FROM pair");
                if(!Names(await Start(c1),TextKey.StoreMoveInUse,c1L) || Staged(c1) || File.Exists(Engine(c1,ResourceNames.DatabaseFile))) wrong.Add("a held writer was not refused naming L before publication");
            }
            if(UserVersion(c1L)!=1017 || await Start(c1) is {} c1b || Bindings(Engine(c1,ResourceNames.DatabaseFile),true).Count!=4) wrong.Add("the mark was not durable before the refusal, or release did not migrate");
            // a file at N that appears before the rename is refused naming N, its bytes untouched.
            var c4=Next();await WritePredecessorAsync(c4,V17Seed);var c4N=Engine(c4,ResourceNames.DatabaseFile);
            if(!Names(await Start(c4,(_,_,_)=>{File.WriteAllText(c4N,"occupant");return null;}),TextKey.RecoveryStageLocationBlocked,c4N) || File.ReadAllText(c4N)!="occupant") wrong.Add("an occupant at N was replaced or not named");
            File.Delete(c4N);if(await Start(c4) is {} c4b) wrong.Add("the next start did not migrate: "+c4b.Message);
            // a read-only reader that read before the start is compatible; it still reads afterwards.
            var c5=Next();var c5L=await WritePredecessorAsync(c5,V17Seed);
            using(var reader=Connect(c5L,SqliteOpenMode.ReadOnly))
            {
                var before=Convert.ToInt32(Scalar(reader,"SELECT count(*) FROM synced_entry"));
                if(await Start(c5) is {} c5e || Convert.ToInt32(Scalar(reader,"SELECT count(*) FROM synced_entry"))!=before) wrong.Add("a read-only reader refused the start or lost its rows");
            }
            // cancelled inside the callback -> no N, no stage; the next start migrates.
            var c8=Next();await WritePredecessorAsync(c8,V17Seed);using(var stop=new CancellationTokenSource())
                if(await Start(c8,(_,_,_)=>{stop.Cancel();return null;},stop.Token) is not OperationCanceledException || Staged(c8) || File.Exists(Engine(c8,ResourceNames.DatabaseFile))) wrong.Add("a cancelled start published or left its stage");
            if(await Start(c8) is {} c8b) wrong.Add("the next start did not migrate: "+c8b.Message);
            // a held hot journal is refused naming L with nothing dropped; released, SQLite recovers it and the start migrates.
            var c9=Next();var c9L=await InterruptedRetirementAsync(c9,"state.db");
            using(new FileStream(c9L,FileMode.Open,FileAccess.Read,FileShare.Read))
                if(!Names(await Start(c9),TextKey.StoreMoveInUse,c9L) || !File.Exists(c9L+"-journal") || Staged(c9)) wrong.Add("a held hot journal was not refused naming L, or was dropped");
            if(await Start(c9) is {} c9b || UserVersion(c9L)!=1016) wrong.Add("after release the hot journal was not recovered, marked and migrated");
            // inside the callback no SQLite writer commits to L, and L cannot be opened for write or moved.
            var d=Next();var dL=await WritePredecessorAsync(d,V17Seed);var dOracle=Oracle(dL,Path.Combine(d,"oracle"));var control=Oracle(dL,Path.Combine(d,"control"));
            var denied=new List<string>();
            await Start(d,(_,_,_)=>
            {
                using(var free=Connect(control,SqliteOpenMode.ReadWrite)) Exec(free,"INSERT INTO pair(pair_id,def_json) VALUES('attempt','{}')");
                try{using var w=Connect(dL,SqliteOpenMode.ReadWrite);Exec(w,"INSERT INTO pair(pair_id,def_json) VALUES('attempt','{}')");denied.Add("sqlite");}catch(SqliteException){}
                try{using var f=new FileStream(dL,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite);denied.Add("write handle");}catch(IOException){}
                try{File.Move(dL,dL+".moved");denied.Add("move");}catch(IOException){}
                return null;
            });
            using(var db=Connect(Engine(d,ResourceNames.DatabaseFile),SqliteOpenMode.ReadOnly)) if(denied.Count!=0 || Convert.ToInt32(Scalar(db,"SELECT count(*) FROM pair WHERE pair_id='attempt'"))!=0 || !SameRows(dL,dOracle)) wrong.Add("a writer reached L after the hold: "+string.Join(",",denied));
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
    }

    /// <summary>A store below the supported floor is refused as too old, unmarked and unchanged.</summary>
    public static async Task TooOldRefusedAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var l=Path.Combine(Directory.CreateDirectory(Path.Combine(root,ResourceNames.EngineFolder)).FullName,"state.db");
            using(var db=Connect(l)) Exec(db,"CREATE TABLE pair(id INTEGER PRIMARY KEY);PRAGMA user_version=15");
            var bytes=await File.ReadAllBytesAsync(l);Exception? refused=null;
            try { await new StoreMove().MoveWithOutcomeAsync(root,root,(_,_,_)=>null,default); } catch(Exception e) { refused=e; }
            Check.Require(refused?.Message==Text.Get(TextKey.StateSchemaTooOld,(TextArg.Path,l)) && bytes.SequenceEqual(await File.ReadAllBytesAsync(l)) && !Staged(root),"A v15 store was not refused as too old with its file unchanged");
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }

    // The v16 conversion of ReleaseSeed as read on N: the link notices folded, the cursor kept, the binding split into folder and name.
    internal const string V16Converted = """
        SELECT (SELECT count(*) FROM issue i WHERE i.cause='InfoLinks' AND (SELECT count(*) FROM issue_member m WHERE m.issue_id=i.issue_id)=2)=1
        AND (SELECT count(*) FROM journal_position WHERE journal_id=3 AND next_usn=17)=1
        AND (SELECT count(*) FROM synced_entry b JOIN folder f ON f.id=b.folder_id WHERE f.path||b.name='gone' AND b.local_id=101 AND b.dropbox_id='cloud' AND b.dropbox_hash=X'01')=1
        """;

    internal static bool Staged(string root)=>Directory.EnumerateFiles(Path.Combine(root,ResourceNames.EngineFolder),"*.new").Any();

    // Every binding as (id, local path, both deltas, typed fields), read from v17 columns or from the v18 folder split.
    internal static List<string> Bindings(string path,bool v18)
    {
        using var db=Connect(path,SqliteOpenMode.ReadOnly);using var cmd=db.CreateCommand();
        var paths=v18?"f.path||b.name FROM synced_entry b JOIN folder f ON f.id=b.folder_id":"b.local_path FROM synced_entry b";
        cmd.CommandText=$"SELECT {(v18?"b.id":"b.rowid")}||'|'||quote(b.dropbox_key_delta)||'|'||quote(b.dropbox_path_delta)||'|'||b.is_folder||'|'||typeof(b.local_id)||quote(b.local_id)||'|'||b.local_size||'|'||b.local_mtime||'|'||b.dropbox_id||'|'||typeof(b.dropbox_hash)||quote(b.dropbox_hash)||'|'||quote(b.local_published_journal)||'|'||quote(b.local_published_usn)||'|'||quote(b.keep)||'|'||{paths} ORDER BY 1";
        using var rd=cmd.ExecuteReader();var rows=new List<string>();while(rd.Read()) rows.Add(rd.GetString(0));return rows;
    }

    // v17 facts in their own encodings: '#', '%' and a space in names, root and trailing-'/' paths, integer and text ids, blob and text hashes, a keep array, a job and a cursor.
    internal const string V17Seed = """
        INSERT INTO pair(id,pair_id,def_json,first_repair_done) VALUES(1,'p','{"ConnectionId":"c","Enabled":true}',1);
        INSERT INTO synced_entry(pair_id,local_path,dropbox_key_delta,dropbox_path_delta,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash,local_published_journal,local_published_usn,keep) VALUES
          (1,'',NULL,NULL,1,7,0,0,'root-id','',NULL,NULL,NULL),
          (1,'A #1/b%c d',NULL,'A #1/B%c d',0,42,3,9,'db-1',X'0A0B0C0D',5,77,'[""]'),
          (1,'dir/',pathkey('N/x/'),'N/x/',1,'opaque-id',0,0,'db-2','',NULL,NULL,NULL),
          (1,'M/x',pathkey('N/X'),'N/X',0,'pc-text',1,1,'db-3','opaque-hash',NULL,NULL,NULL);
        INSERT INTO accepted_job(pair_id,route,job_id,launch) VALUES(1,'files/delete_batch/check','job-1','[]');
        INSERT INTO journal_position VALUES('volume',3,17);
        """;

    const string ReleaseJobs="INSERT INTO folder_move(pair_id,source_id,root_id,source_path,destination_path,destination_id,phase,launch,job_id,generation) VALUES(1,'s','r','from','to','d','Filled','[[\"m1\",\"child\"]]','job-move',1);";

    /// <summary>The 0.3.95 WAL floor: one v16 transaction (schema, seed and a folder move job) only in the WAL of the crash image.</summary>
    internal static string ReleaseWal(string root)
    {
        var target=Path.Combine(Directory.CreateDirectory(Path.Combine(root,ResourceNames.EngineFolder)).FullName,"state.db");
        var build=Path.Combine(Directory.CreateDirectory(Path.Combine(root,"build-wal")).FullName,"state.db");
        using var db=Connect(build);Exec(db,"PRAGMA journal_mode=WAL;PRAGMA wal_autocheckpoint=0");
        using(var tx=db.BeginTransaction()){Exec(db,ReleaseSchema+ReleaseSeed+ReleaseJobs,tx);tx.Commit();}
        CopyOpen(build,target);CopyOpen(build+"-wal",target+"-wal");
        return target;
    }

    /// <summary>The previous release's literal acceptance rule for a store version.</summary>
    internal static bool OldRefuses(int version) => version!=16 && version!=17;

    /// <summary>Writes a v17 image at root\engine\state.db as the previous release created it, then seeds it in one transaction (in the WAL only for the WAL leg).</summary>
    internal static Task<string> WritePredecessorAsync(string root,string seed,bool walLeg=false,int autoVacuum=2)
    {
        var target=Path.Combine(Directory.CreateDirectory(Path.Combine(root,ResourceNames.EngineFolder)).FullName,"state.db");
        var build=walLeg?Path.Combine(Directory.CreateDirectory(Path.Combine(root,"build")).FullName,"state.db"):target;
        using(var db=Connect(build))
        {
            Exec(db,$"PRAGMA auto_vacuum={autoVacuum};"+PredecessorPragmas);
            using(var tx=db.BeginTransaction()){Exec(db,PredecessorSchema+"PRAGMA user_version=17;",tx);tx.Commit();}
            Exec(db,"PRAGMA wal_checkpoint(TRUNCATE)");
        }
        using(var db=Connect(build))
        {
            if(walLeg) Exec(db,"PRAGMA wal_autocheckpoint=0");
            using(var tx=db.BeginTransaction()){Exec(db,seed,tx);tx.Commit();}
            if(!walLeg) Exec(db,"PRAGMA wal_checkpoint(TRUNCATE)");
            else {CopyOpen(build,target);CopyOpen(build+"-wal",target+"-wal");}
        }
        return Task.FromResult(target);
    }

    /// <summary>A v16 rollback-journal image under root\engine\name with a mark in flight: its journal is hot in the copy.</summary>
    internal static async Task<string> InterruptedRetirementAsync(string root,string name)
    {
        var target=Path.Combine(Directory.CreateDirectory(Path.Combine(root,ResourceNames.EngineFolder)).FullName,name);
        var build=Path.Combine(Directory.CreateDirectory(Path.Combine(root,"build-"+name)).FullName,name);
        await ReleaseImageAsync(build);
        var pre=ReadOpen(build);
        using var db=Connect(build);Exec(db,"PRAGMA cache_size=1");
        using var tx=db.BeginTransaction();Exec(db,"PRAGMA user_version=1016",tx);
        for(var i=0;i<1000 && ReadOpen(build).SequenceEqual(pre);i++)
            Exec(db,$"INSERT INTO discovery_deleted_delta(pair_id,path_key) WITH RECURSIVE n(v) AS (SELECT 1 UNION ALL SELECT v+1 FROM n WHERE v<20) SELECT 1,'U{i}-'||v||hex(randomblob(200)) FROM n",tx);
        Check.Require(!ReadOpen(build).SequenceEqual(pre) && File.Exists(build+"-journal"),"The pager never spilled the in-flight mark into the main file");
        CopyOpen(build,target);CopyOpen(build+"-journal",target+"-journal");
        tx.Rollback();
        return target;
        static byte[] ReadOpen(string path){using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var m=new MemoryStream();f.CopyTo(m);return m.ToArray();}
    }

    /// <summary>A closed v16 image at path with one enabled pair, a binding, a cursor and the two link notices to fold.</summary>
    internal static async Task ReleaseImageAsync(string path)
    {
        using var db=Connect(path);using var cmd=db.CreateCommand();cmd.CommandText=ReleaseSchema;await cmd.ExecuteNonQueryAsync();
        cmd.CommandText=ReleaseSeed;await cmd.ExecuteNonQueryAsync();
    }

    internal const string ReleaseSeed="""
        INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p','{"ConnectionId":"c","Enabled":true}');
        INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) VALUES(1,'gone',0,101,1,10,'cloud',X'01');
        INSERT INTO discovery_deleted_delta(pair_id,path_key,spent) VALUES(1,'legacy',1);
        INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed) VALUES
          (8,1,4,'SkipLink','a','{"Items":[],"Incoming":null}',1,0),(9,1,4,'SkipLink','b','{"Items":[{"Side":0,"Path":"b","Id":"old-link"}],"Incoming":0}',2,0);
        INSERT INTO issue_member(member_id,issue_id,pair_id,side,path_key,subject_json,skip_cause) VALUES
          (1,8,1,0,'a','{"Version":1,"Value":{"Items":[],"Incoming":null}}','SkipLink'),
          (2,9,1,0,pathkey('b'),'{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"b","Id":"old-link"}],"Incoming":null}}','SkipLink');
        INSERT INTO journal_position VALUES('volume',3,17);
        """;

    internal static SqliteConnection Connect(string path,SqliteOpenMode mode=SqliteOpenMode.ReadWriteCreate)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=mode,Pooling=false}.ToString());db.Open();
        db.CreateFunction<string,string>("pathkey",PathNames.Key,true);db.CreateFunction<string,int>("plandepth",PathNames.Depth,true);db.CreateFunction<string,int>("localaccessretrycause",_=>0,true);
        db.CreateFunction<string,string>("pathfolder",PathNames.Folder,true);db.CreateFunction<string,string>("pathname",PathNames.Name,true);
        return db;
    }
    internal static object? Scalar(SqliteConnection db,string sql){using var cmd=db.CreateCommand();cmd.CommandText=sql;return cmd.ExecuteScalar();}
    internal static void Exec(SqliteConnection db,string sql,SqliteTransaction? tx=null){using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;cmd.ExecuteNonQuery();}
    /// <summary>Copies a file that SQLite may hold open.</summary>
    internal static void CopyOpen(string from,string to)
    {
        using var source=new FileStream(from,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var target=new FileStream(to,FileMode.Create,FileAccess.Write,FileShare.None);source.CopyTo(target);
    }
    /// <summary>The oracle image: main plus any -wal or -journal (never -shm, which SQLite rebuilds), copied into its own folder before any test-side open.</summary>
    internal static string Oracle(string path,string folder)
    {
        var copy=Path.Combine(Directory.CreateDirectory(folder).FullName,Path.GetFileName(path));
        foreach(var suffix in new[]{"","-wal","-journal"}) if(File.Exists(path+suffix)) CopyOpen(path+suffix,copy+suffix);
        return copy;
    }
    /// <summary>The version SQLite itself reads from a fresh copy; a hot journal is rolled back on the copy, never on the file.</summary>
    internal static int UserVersion(string path)
    {
        var folder=Path.Combine(Path.GetTempPath(),"oracle-"+Guid.NewGuid().ToString("N"));
        try { using var db=Connect(Oracle(path,folder));return Convert.ToInt32(Scalar(db,"PRAGMA user_version")); }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(folder,true); }
    }
    /// <summary>Equal typed row multisets (storage class plus value) for every table of a; no rowid is named.</summary>
    internal static bool SameRows(string a,string b,params string[] skip)
    {
        using var left=Connect(a,SqliteOpenMode.ReadOnly);using var right=Connect(b,SqliteOpenMode.ReadOnly);
        var tables=new List<string>();
        using(var cmd=left.CreateCommand()){cmd.CommandText="SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";using var rd=cmd.ExecuteReader();while(rd.Read()) tables.Add(rd.GetString(0));}
        return tables.Except(skip).All(t=>Rows(left,t).SequenceEqual(Rows(right,t)));
        static List<string> Rows(SqliteConnection db,string table)
        {
            var columns=new List<string>();
            using(var cmd=db.CreateCommand()){cmd.CommandText=$"SELECT name FROM pragma_table_info('{table}')";using var rd=cmd.ExecuteReader();while(rd.Read()) columns.Add(rd.GetString(0));}
            using var read=db.CreateCommand();read.CommandText=$"SELECT {string.Join("||char(1)||",columns.Select(c=>$"typeof(\"{c}\")||':'||quote(\"{c}\")"))} FROM \"{table}\"";
            var rows=new List<string>();using(var rd=read.ExecuteReader()) while(rd.Read()) rows.Add(rd.GetString(0));
            rows.Sort(StringComparer.Ordinal);return rows;
        }
    }

    // The previous release's creation contract (SchemaPragmas after auto_vacuum, SchemaCreate with the keep index expanded), frozen here.
    internal const string PredecessorPragmas=" PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
    internal const string PredecessorSchema = """
        CREATE TABLE pair(id INTEGER PRIMARY KEY, pair_id TEXT NOT NULL UNIQUE, def_json TEXT NOT NULL, pending_json TEXT,
          last_repair_json TEXT, first_repair_done INTEGER NOT NULL DEFAULT 0,
          deleted_history_cursor TEXT CHECK(deleted_history_cursor IS NULL OR length(deleted_history_cursor)>0),
          deleted_history_owed INTEGER NOT NULL DEFAULT 1 CHECK(deleted_history_owed IN (0,1))) STRICT;
        CREATE TABLE synced_entry(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          local_path TEXT NOT NULL, dropbox_key_delta TEXT, dropbox_path_delta TEXT,
          dropbox_key TEXT GENERATED ALWAYS AS (coalesce(dropbox_key_delta, pathkey(local_path))) VIRTUAL,
          dropbox_path TEXT GENERATED ALWAYS AS (coalesce(dropbox_path_delta, local_path)) VIRTUAL,
          is_folder INTEGER NOT NULL, local_id ANY NOT NULL, local_size INTEGER NOT NULL, local_mtime INTEGER NOT NULL,
          dropbox_id TEXT NOT NULL, dropbox_hash ANY NOT NULL,
          local_published_journal INTEGER, local_published_usn INTEGER,
          keep TEXT CHECK(keep IS NULL OR (json_valid(keep) AND json_type(keep)='array'))) STRICT;
        CREATE UNIQUE INDEX synced_local_key ON synced_entry(pair_id, pathkey(local_path));
        CREATE UNIQUE INDEX synced_dropbox_key ON synced_entry(pair_id, dropbox_key_delta) WHERE dropbox_key_delta IS NOT NULL;
        CREATE INDEX synced_local_id ON synced_entry(pair_id, local_id);
        CREATE INDEX synced_dropbox_id ON synced_entry(pair_id, dropbox_id);
        CREATE INDEX synced_keep ON synced_entry(pair_id) WHERE keep IS NOT NULL;
        CREATE TABLE issue(issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         kind INTEGER NOT NULL, cause TEXT NOT NULL, path TEXT NOT NULL, subject_json TEXT NOT NULL,
         at_ticks INTEGER NOT NULL, dismissed INTEGER NOT NULL DEFAULT 0,
         ignored INTEGER NOT NULL DEFAULT 0 CHECK(ignored IN (0,1)),
         UNIQUE(pair_id,path,kind,cause)) STRICT;
        CREATE INDEX issue_path_key ON issue(pair_id, pathkey(path), kind);
        CREATE TABLE journal_position(
         volume_key TEXT PRIMARY KEY NOT NULL CHECK(length(volume_key)>0),
         journal_id INTEGER NOT NULL, next_usn INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE source_state(
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)),
         cursor TEXT CHECK(cursor IS NULL OR length(cursor)>0),
         repair_reason TEXT CHECK(repair_reason IS NULL OR length(repair_reason)>0),
         repair_revision INTEGER NOT NULL CHECK(repair_revision>=0),
         PRIMARY KEY(pair_id,side),
         CHECK(side=1 OR cursor IS NULL)
        
        ) STRICT;
        CREATE TABLE discovery_deleted_block(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          last_key TEXT NOT NULL, name_count INTEGER NOT NULL CHECK(name_count BETWEEN 1 AND 5000),
          names BLOB NOT NULL, PRIMARY KEY(pair_id,last_key)) STRICT;
        CREATE TABLE discovery_deleted_delta(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          path_key TEXT NOT NULL, spent INTEGER NOT NULL DEFAULT 0 CHECK(spent IN (0,1)), deleted_ticks INTEGER, PRIMARY KEY(pair_id,path_key)) STRICT, WITHOUT ROWID;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json ON pair
        WHEN json_extract(new.def_json,'$.LocalRoot') IS NOT json_extract(old.def_json,'$.LocalRoot') OR
             json_extract(new.def_json,'$.DropboxRoot') IS NOT json_extract(old.def_json,'$.DropboxRoot') OR
             json_extract(new.def_json,'$.ConnectionId') IS NOT json_extract(old.def_json,'$.ConnectionId') OR
             json_extract(new.def_json,'$.Roots') IS NOT json_extract(old.def_json,'$.Roots')
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL,deleted_history_owed=1 WHERE id=new.id;
          DELETE FROM discovery_deleted_delta WHERE pair_id=new.id AND spent=0;
        END;
        CREATE TABLE accepted_job(
          pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          route TEXT NOT NULL, job_id TEXT NOT NULL,
          launch TEXT NOT NULL CHECK(json_valid(launch) AND json_type(launch)='array'),
          PRIMARY KEY(pair_id,route,job_id)) STRICT;
        CREATE TABLE issue_member(
         member_id INTEGER PRIMARY KEY,
         issue_id INTEGER REFERENCES issue(issue_id) ON DELETE SET NULL,
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)), path_key TEXT NOT NULL,
         identity ANY CHECK(identity IS NULL OR typeof(identity) IN ('integer','text')),
         subject_json TEXT NOT NULL CHECK(json_valid(subject_json) AND coalesce(json_extract(subject_json,'$.Version'),0)=1),
         skip_cause TEXT
        ) STRICT;
        CREATE INDEX issue_member_issue ON issue_member(issue_id,side,path_key);
        CREATE INDEX issue_member_scope ON issue_member(pair_id,side,path_key,issue_id);
        CREATE UNIQUE INDEX issue_member_skip ON issue_member(pair_id,side,path_key) WHERE skip_cause IS NOT NULL;
        """;

    private const string ReleaseSchema = """
        CREATE TABLE IF NOT EXISTS pair(id INTEGER PRIMARY KEY, pair_id TEXT NOT NULL UNIQUE, def_json TEXT NOT NULL, pending_json TEXT,
          last_repair_json TEXT, first_repair_done INTEGER NOT NULL DEFAULT 0) STRICT;
        CREATE TABLE IF NOT EXISTS account(connection_id TEXT PRIMARY KEY, limiter_level INTEGER NOT NULL) STRICT;
        CREATE TABLE IF NOT EXISTS synced_entry(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          local_path TEXT NOT NULL, dropbox_key_delta TEXT, dropbox_path_delta TEXT,
          dropbox_key TEXT GENERATED ALWAYS AS (coalesce(dropbox_key_delta, pathkey(local_path))) VIRTUAL,
          dropbox_path TEXT GENERATED ALWAYS AS (coalesce(dropbox_path_delta, local_path)) VIRTUAL,
          is_folder INTEGER NOT NULL, local_id ANY NOT NULL, local_size INTEGER NOT NULL, local_mtime INTEGER NOT NULL,
          dropbox_id TEXT NOT NULL, dropbox_hash ANY NOT NULL) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS synced_local_key ON synced_entry(pair_id, pathkey(local_path));
        CREATE UNIQUE INDEX IF NOT EXISTS synced_dropbox_key ON synced_entry(pair_id, dropbox_key_delta) WHERE dropbox_key_delta IS NOT NULL;
        CREATE INDEX IF NOT EXISTS synced_local_id ON synced_entry(pair_id, local_id);
        CREATE INDEX IF NOT EXISTS synced_dropbox_id ON synced_entry(pair_id, dropbox_id);
        CREATE TABLE IF NOT EXISTS issue(issue_id INTEGER PRIMARY KEY, pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          kind INTEGER NOT NULL, cause TEXT NOT NULL, path TEXT NOT NULL, subject_json TEXT NOT NULL,
          at_ticks INTEGER NOT NULL, dismissed INTEGER NOT NULL DEFAULT 0,
          UNIQUE(pair_id, path, kind)) STRICT;
        ALTER TABLE pair ADD COLUMN execution_generation INTEGER NOT NULL DEFAULT 0 CHECK(execution_generation>=0);
        ALTER TABLE pair ADD COLUMN retry_attempts INTEGER CHECK(retry_attempts IS NULL OR retry_attempts BETWEEN 0 AND 2147483647);
        ALTER TABLE pair ADD COLUMN retry_after_ticks INTEGER CHECK((retry_attempts IS NULL AND retry_after_ticks IS NULL) OR (retry_attempts IS NOT NULL AND retry_after_ticks IS NOT NULL AND retry_after_ticks BETWEEN 0 AND 3155378975999999999));
        CREATE INDEX pair_retry_due ON pair(retry_after_ticks,id) WHERE retry_after_ticks IS NOT NULL;
        CREATE TABLE journal_position(
         volume_key TEXT PRIMARY KEY NOT NULL CHECK(length(volume_key)>0),
         journal_id INTEGER NOT NULL, next_usn INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE source_state(
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)),
         source_epoch INTEGER NOT NULL CHECK(source_epoch>=0),
         provenance_json TEXT CHECK(provenance_json IS NULL OR (json_valid(provenance_json) AND coalesce(json_extract(provenance_json,'$.Version'),0)=1)),
         cursor TEXT CHECK(cursor IS NULL OR length(cursor)>0),
         repair_reason TEXT CHECK(repair_reason IS NULL OR length(repair_reason)>0),
         repair_revision INTEGER NOT NULL CHECK(repair_revision>=0),
         PRIMARY KEY(pair_id,side),
         CHECK(side=1 OR cursor IS NULL),
         CHECK(cursor IS NULL OR provenance_json IS NOT NULL)
        ) STRICT;
        CREATE TABLE change_work(
         work_id INTEGER PRIMARY KEY AUTOINCREMENT,
         work_origin TEXT NOT NULL DEFAULT 'Acquired' CHECK(work_origin IN ('Acquired','Issue')),
         issue_work_key TEXT,
         dispatch_kind TEXT NOT NULL DEFAULT 'OrdinaryItem' CHECK(dispatch_kind IN ('OrdinaryItem','LocalBranch','LocalRecovery')),
         pending_operation_id TEXT,
         pending_generation INTEGER CHECK(pending_generation IS NULL OR pending_generation>=0),
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         pair_generation INTEGER NOT NULL CHECK(pair_generation>=0),
         source_epoch INTEGER NOT NULL CHECK(source_epoch>=0),
         side INTEGER NOT NULL CHECK(side IN (0,1)),
         domain TEXT NOT NULL CHECK(length(domain)>0),
         path_key TEXT NOT NULL, old_path_key TEXT,
         identity ANY CHECK(identity IS NULL OR typeof(identity) IN ('integer','text')),
         notice_json TEXT CHECK(notice_json IS NULL OR (json_valid(notice_json) AND coalesce(json_extract(notice_json,'$.Version'),0)=1)),
         before_json TEXT CHECK(before_json IS NULL OR (json_valid(before_json) AND coalesce(json_extract(before_json,'$.Version'),0)=1)),
         acquisition_work_id INTEGER CHECK(acquisition_work_id IS NULL OR (acquisition_work_id>0 AND acquisition_work_id<=work_id)),
         parent_anchor INTEGER, obligation_key TEXT,
         CHECK(work_id>0),
         CHECK((work_origin='Acquired' AND notice_json IS NOT NULL AND acquisition_work_id IS NOT NULL) OR
               (work_origin='Issue' AND notice_json IS NULL AND acquisition_work_id IS NULL AND parent_anchor IS NULL
                AND obligation_key IS NULL AND issue_work_key IS NOT NULL AND length(issue_work_key)>0)),
         CHECK((dispatch_kind='LocalRecovery' AND pending_operation_id IS NOT NULL AND length(pending_operation_id)>0 AND pending_generation IS NOT NULL) OR
               (dispatch_kind<>'LocalRecovery' AND pending_operation_id IS NULL AND pending_generation IS NULL)),
         CHECK((parent_anchor IS NULL AND obligation_key IS NULL) OR
               (parent_anchor IS NOT NULL AND parent_anchor>0 AND parent_anchor<work_id AND obligation_key IS NOT NULL AND length(obligation_key)>0)),
         UNIQUE(parent_anchor,obligation_key)
        ) STRICT;
        CREATE INDEX work_pair_order ON change_work(pair_id,pair_generation,work_id);
        CREATE INDEX work_source_order ON change_work(pair_id,pair_generation,side,source_epoch,work_id) WHERE work_origin='Acquired';
        CREATE INDEX work_identity ON change_work(pair_id,pair_generation,side,source_epoch,domain,identity,work_id);
        CREATE INDEX work_cross_identity ON change_work(side,domain,identity,pair_id,work_id);
        CREATE INDEX work_path ON change_work(pair_id,pair_generation,side,domain,path_key,work_id);
        CREATE INDEX work_old_path ON change_work(pair_id,pair_generation,side,domain,old_path_key,work_id) WHERE old_path_key IS NOT NULL;
        CREATE TABLE issue_member(
         member_id INTEGER PRIMARY KEY,
         issue_id INTEGER REFERENCES issue(issue_id) ON DELETE SET NULL,
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         work_id INTEGER REFERENCES change_work(work_id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)), path_key TEXT NOT NULL,
         identity ANY CHECK(identity IS NULL OR typeof(identity) IN ('integer','text')),
         subject_json TEXT NOT NULL CHECK(json_valid(subject_json) AND coalesce(json_extract(subject_json,'$.Version'),0)=1),
         skip_cause TEXT,
         CHECK((work_id IS NOT NULL AND skip_cause IS NULL) OR
               (work_id IS NULL AND issue_id IS NOT NULL AND skip_cause IS NOT NULL AND length(skip_cause)>0))
        ) STRICT;
        CREATE UNIQUE INDEX issue_member_work ON issue_member(work_id) WHERE work_id IS NOT NULL;
        CREATE INDEX issue_member_issue ON issue_member(issue_id,side,path_key);
        CREATE UNIQUE INDEX issue_member_skip ON issue_member(pair_id,side,path_key) WHERE work_id IS NULL;
        CREATE TABLE folder_removal(
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         operation_id TEXT NOT NULL CHECK(length(operation_id)>0),
         generation INTEGER NOT NULL CHECK(generation>=0),
         phase TEXT NOT NULL CHECK(phase IN ('Prepared','Observed','SubmitArmed','Submitted','Terminal','Checking','Recovering','Complete','Unknown','Retained')),
         deleting_side INTEGER NOT NULL CHECK(deleting_side IN (0,1)),
         root_entry_json TEXT NOT NULL CHECK(json_valid(root_entry_json) AND coalesce(json_extract(root_entry_json,'$.Version'),0)=1),
         source_evidence_json TEXT NOT NULL CHECK(json_valid(source_evidence_json) AND coalesce(json_extract(source_evidence_json,'$.Version'),0)=1),
         saved_root_json TEXT CHECK(saved_root_json IS NULL OR (json_valid(saved_root_json) AND coalesce(json_extract(saved_root_json,'$.Version'),0)=1)),
         root_context_json TEXT NOT NULL CHECK(json_valid(root_context_json) AND coalesce(json_extract(root_context_json,'$.Version'),0)=1),
         cursor TEXT CHECK(cursor IS NULL OR length(cursor)>0),
         active INTEGER NOT NULL CHECK(active IN (0,1)), cause TEXT,
         PRIMARY KEY(pair_id,operation_id), CHECK(phase<>'Complete' OR active=0)
        ) STRICT;
        CREATE INDEX folder_removal_active ON folder_removal(pair_id,active,operation_id);CREATE INDEX folder_removal_root ON folder_removal(pair_id,json_extract(root_entry_json,'$.Value.Path'),operation_id) WHERE active=1;CREATE TABLE folder_removal_submission(
         submission_id TEXT PRIMARY KEY NOT NULL CHECK(length(submission_id)>0),
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         generation INTEGER NOT NULL CHECK(generation>=0),
         phase TEXT NOT NULL CHECK(phase IN ('Armed','Accepted','Terminal','Unknown')),
         job_id TEXT CHECK(job_id IS NULL OR length(job_id)>0), cause TEXT,
         CHECK(phase<>'Accepted' OR job_id IS NOT NULL),
         CHECK(phase<>'Armed' OR job_id IS NULL)
        ) STRICT;CREATE INDEX folder_submission_pending ON folder_removal_submission(pair_id,submission_id) WHERE phase='Accepted';CREATE TABLE folder_removal_item(
         pair_id INTEGER NOT NULL, operation_id TEXT NOT NULL,
         ordinal INTEGER NOT NULL CHECK(ordinal>=0),
         generation INTEGER NOT NULL CHECK(generation>=0),
         provenance TEXT NOT NULL CHECK(provenance IN ('Planned','Unexpected','BlockingParent')),
         path TEXT NOT NULL, policy_key TEXT NOT NULL,
         native_key TEXT GENERATED ALWAYS AS (pathkey(path)) VIRTUAL,
         present INTEGER GENERATED ALWAYS AS (expected_json IS NOT NULL) VIRTUAL,
         expected_json TEXT CHECK(expected_json IS NULL OR (json_valid(expected_json) AND coalesce(json_extract(expected_json,'$.Version'),0)=1)),
         saved_json TEXT CHECK(saved_json IS NULL OR (json_valid(saved_json) AND coalesce(json_extract(saved_json,'$.Version'),0)=1)),
         verdict TEXT NOT NULL CHECK(verdict IN ('Survives','Removed')),
         follow_up INTEGER NOT NULL CHECK(follow_up IN (0,1)),
         follow_up_state TEXT NOT NULL CHECK(follow_up_state IN ('None','Pending','Done')),
         selected_branch INTEGER NOT NULL CHECK(selected_branch IN (0,1)),
         effect_kind TEXT NOT NULL CHECK(effect_kind IN ('None','RecursiveFolder','ConditionalFile','StateOnly')),
         effect_state TEXT NOT NULL CHECK(effect_state IN ('None','Ready','Armed','Accepted','Verified','NoEffect','Unknown','Settled')),
         effect_generation INTEGER NOT NULL CHECK(effect_generation>=0),
         submission_id TEXT REFERENCES folder_removal_submission(submission_id) ON DELETE NO ACTION,
         submission_member INTEGER CHECK(submission_member IS NULL OR submission_member>=0),
         effect_result_json TEXT CHECK(effect_result_json IS NULL OR (json_valid(effect_result_json) AND coalesce(json_extract(effect_result_json,'$.Version'),0)=1)),
         phase TEXT NOT NULL CHECK(phase IN ('Planned','DeleteArmed','Deleted','RecoverySelected','AllocateArmed','StageArmed','Staged','PublishArmed','Published','Conflict','Unknown','DiscardArmed','Settled')),
         selected_json TEXT CHECK(selected_json IS NULL OR (json_valid(selected_json) AND coalesce(json_extract(selected_json,'$.Version'),0)=1)),
         stage_allocation_json TEXT CHECK(stage_allocation_json IS NULL OR (json_valid(stage_allocation_json) AND coalesce(json_extract(stage_allocation_json,'$.Version'),0)=1)),
         stage_json TEXT CHECK(stage_json IS NULL OR (json_valid(stage_json) AND coalesce(json_extract(stage_json,'$.Version'),0)=1)),
         result_json TEXT CHECK(result_json IS NULL OR (json_valid(result_json) AND coalesce(json_extract(result_json,'$.Version'),0)=1)),
         cause TEXT,
         PRIMARY KEY(pair_id,operation_id,ordinal),
         FOREIGN KEY(pair_id,operation_id) REFERENCES folder_removal(pair_id,operation_id) ON DELETE CASCADE,
         CHECK((follow_up=0 AND follow_up_state='None') OR (follow_up=1 AND follow_up_state IN ('Pending','Done'))),
         CHECK((effect_kind='None' AND effect_state='None') OR (effect_kind<>'None' AND effect_state<>'None')),
         CHECK((submission_id IS NULL AND submission_member IS NULL) OR
               (submission_id IS NOT NULL AND submission_member IS NOT NULL AND effect_kind IN ('RecursiveFolder','ConditionalFile') AND effect_state IN ('Armed','Accepted','Verified','NoEffect','Unknown','Settled'))),
         CHECK(effect_state NOT IN ('Armed','Accepted') OR submission_id IS NOT NULL),
         CHECK(effect_kind NOT IN ('RecursiveFolder','ConditionalFile') OR (expected_json IS NOT NULL AND verdict='Removed')),
         CHECK(phase NOT IN ('RecoverySelected','AllocateArmed','StageArmed','Staged','PublishArmed','Published','DiscardArmed') OR selected_json IS NOT NULL),
         CHECK(phase NOT IN ('AllocateArmed','StageArmed','Staged','PublishArmed','Published','DiscardArmed') OR stage_allocation_json IS NOT NULL),
         CHECK(phase NOT IN ('StageArmed','Staged','PublishArmed','Published','DiscardArmed') OR stage_json IS NOT NULL),
         CHECK(phase<>'Published' OR result_json IS NOT NULL)
        ) STRICT;
        CREATE UNIQUE INDEX folder_item_identity ON folder_removal_item(pair_id,operation_id,native_key,provenance,present);
        CREATE INDEX folder_item_followup ON folder_removal_item(pair_id,operation_id,follow_up_state,ordinal);
        CREATE INDEX folder_item_effect ON folder_removal_item(pair_id,operation_id,effect_state,ordinal);
        CREATE UNIQUE INDEX folder_submission_member ON folder_removal_item(submission_id,submission_member) WHERE submission_id IS NOT NULL;
        CREATE INDEX folder_item_ready_order ON folder_removal_item(pair_id,operation_id,-plandepth(policy_key),policy_key,native_key,present,ordinal) WHERE effect_state='Ready';

        CREATE UNIQUE INDEX work_issue_registration ON change_work(pair_id,issue_work_key) WHERE issue_work_key IS NOT NULL;
        ALTER TABLE issue ADD COLUMN item_retry_episode TEXT;
        ALTER TABLE issue ADD COLUMN item_retry_revision INTEGER CHECK(item_retry_revision IS NULL OR item_retry_revision>=0);
        ALTER TABLE issue ADD COLUMN item_retry_work_id INTEGER REFERENCES change_work(work_id) ON DELETE NO ACTION
         CHECK((item_retry_episode IS NULL AND item_retry_revision IS NULL AND item_retry_work_id IS NULL) OR
               (item_retry_episode IS NOT NULL AND length(item_retry_episode)>0
                AND item_retry_revision IS NOT NULL AND item_retry_work_id IS NOT NULL
                AND kind=0 AND localaccessretrycause(cause)=1));
        CREATE UNIQUE INDEX issue_item_retry_work ON issue(item_retry_work_id) WHERE item_retry_work_id IS NOT NULL;
        CREATE INDEX issue_member_retry_subject ON issue_member(pair_id,side,path_key,identity) WHERE work_id IS NOT NULL;
        ALTER TABLE folder_removal ADD COLUMN retirement_disposition TEXT
         CHECK(retirement_disposition IS NULL OR (retirement_disposition IN ('Acknowledged','NoEffect') AND active=0 AND phase IN ('Unknown','Retained')));
        ALTER TABLE pair ADD COLUMN deleted_history_cursor TEXT CHECK(deleted_history_cursor IS NULL OR length(deleted_history_cursor)>0);
        CREATE TABLE discovery_deleted_name(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          path_key TEXT NOT NULL, PRIMARY KEY(pair_id,path_key)) STRICT, WITHOUT ROWID;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json,execution_generation,first_repair_done ON pair
        WHEN json_remove(new.def_json,'$.Drive')<>json_remove(old.def_json,'$.Drive') OR new.execution_generation<>old.execution_generation OR (new.first_repair_done=1 AND old.first_repair_done=0)
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL WHERE id=new.id;
        END;
        CREATE TABLE issue_new(issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         kind INTEGER NOT NULL, cause TEXT NOT NULL, path TEXT NOT NULL, subject_json TEXT NOT NULL,
         at_ticks INTEGER NOT NULL, dismissed INTEGER NOT NULL DEFAULT 0,
         item_retry_episode TEXT,
         item_retry_revision INTEGER CHECK(item_retry_revision IS NULL OR item_retry_revision>=0),
         item_retry_work_id INTEGER REFERENCES change_work(work_id) ON DELETE NO ACTION
          CHECK((item_retry_episode IS NULL AND item_retry_revision IS NULL AND item_retry_work_id IS NULL) OR
                (item_retry_episode IS NOT NULL AND length(item_retry_episode)>0
                 AND item_retry_revision IS NOT NULL AND item_retry_work_id IS NOT NULL
                 AND kind=0 AND localaccessretrycause(cause)=1)),
         ignored INTEGER NOT NULL DEFAULT 0 CHECK(ignored IN (0,1)),
         UNIQUE(pair_id,path,kind)) STRICT;
        INSERT INTO issue_new(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed,
         item_retry_episode,item_retry_revision,item_retry_work_id)
         SELECT issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed,
          item_retry_episode,item_retry_revision,item_retry_work_id FROM issue;
        DROP TABLE issue;
        ALTER TABLE issue_new RENAME TO issue;
        DELETE FROM sqlite_sequence WHERE name='issue' AND seq=0;
        CREATE UNIQUE INDEX issue_item_retry_work ON issue(item_retry_work_id) WHERE item_retry_work_id IS NOT NULL;
        ALTER TABLE pair ADD COLUMN deleted_history_owed INTEGER NOT NULL DEFAULT 1 CHECK(deleted_history_owed IN (0,1));
        CREATE TABLE discovery_deleted_block(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          last_key TEXT NOT NULL, name_count INTEGER NOT NULL CHECK(name_count BETWEEN 1 AND 5000),
          names BLOB NOT NULL, PRIMARY KEY(pair_id,last_key)) STRICT;
        CREATE TABLE discovery_deleted_delta(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          path_key TEXT NOT NULL, PRIMARY KEY(pair_id,path_key)) STRICT, WITHOUT ROWID;
        DROP TRIGGER invalidate_deleted_history;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json,execution_generation ON pair
        WHEN json_remove(new.def_json,'$.Drive')<>json_remove(old.def_json,'$.Drive') OR new.execution_generation<>old.execution_generation
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL,deleted_history_owed=1 WHERE id=new.id;
          DELETE FROM discovery_deleted_delta WHERE pair_id=new.id;
        END;
        UPDATE source_state SET repair_reason=coalesce(repair_reason,'NormalRepair'),repair_revision=repair_revision+1;
        DROP TABLE discovery_deleted_name;
        ALTER TABLE discovery_deleted_delta ADD COLUMN spent INTEGER NOT NULL DEFAULT 0 CHECK(spent IN (0,1));
        DROP TRIGGER invalidate_deleted_history;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json,execution_generation ON pair
        WHEN json_remove(new.def_json,'$.Drive')<>json_remove(old.def_json,'$.Drive') OR new.execution_generation<>old.execution_generation
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL,deleted_history_owed=1 WHERE id=new.id;
          DELETE FROM discovery_deleted_delta WHERE pair_id=new.id AND spent=0;
        END;
        ALTER TABLE synced_entry ADD COLUMN local_published_journal INTEGER;ALTER TABLE synced_entry ADD COLUMN local_published_usn INTEGER;
        ALTER TABLE change_work ADD COLUMN discovery_required INTEGER NOT NULL DEFAULT 0 CHECK(discovery_required IN (0,1));
        CREATE INDEX issue_path_key ON issue(pair_id, pathkey(path), kind);
        ALTER TABLE change_work ADD COLUMN rename_continuation_json TEXT CHECK(rename_continuation_json IS NULL OR (json_valid(rename_continuation_json) AND coalesce(json_extract(rename_continuation_json,'$.Version'),0)=1)); ALTER TABLE folder_removal ADD COLUMN rename_work_id INTEGER REFERENCES change_work(work_id) ON DELETE RESTRICT; CREATE INDEX folder_rename_work ON folder_removal(rename_work_id) WHERE rename_work_id IS NOT NULL;
        CREATE TABLE binding_repair(
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)),
         identity ANY NOT NULL CHECK(typeof(identity) IN ('integer','text')),
         other_first ANY NOT NULL, other_second ANY NOT NULL,
         issue_id INTEGER,
         CHECK(other_first < other_second),
         PRIMARY KEY(pair_id,side,identity)) STRICT;
        ALTER TABLE folder_removal ADD COLUMN authority TEXT NOT NULL DEFAULT 'Legacy'
         CHECK(authority IN ('Inferred','Chosen','Excluded','Vacate','Repair','Legacy'));
        ALTER TABLE folder_removal ADD COLUMN pre_restore_id TEXT CHECK(pre_restore_id IS NULL OR length(pre_restore_id)>0);CREATE INDEX folder_removal_repair ON folder_removal(pair_id,deleting_side,json_extract(saved_root_json,'$.Value.LocalId'),json_extract(saved_root_json,'$.Value.DropboxId'),operation_id) WHERE authority='Repair';CREATE INDEX folder_removal_unresolved ON folder_removal(pair_id,operation_id) WHERE active=1 OR (phase IN ('Unknown','Retained') AND retirement_disposition IS NULL);
        CREATE TABLE folder_move(
          pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          source_id TEXT NOT NULL, root_id TEXT NOT NULL, parent_id TEXT,
          source_path TEXT NOT NULL, destination_path TEXT NOT NULL, destination_id TEXT,
          phase TEXT NOT NULL CHECK(phase IN ('Armed','Filling','Filled','Relabelled')),
          launch TEXT, job_id TEXT, generation INTEGER NOT NULL,
          CHECK(job_id IS NULL OR launch IS NOT NULL),
          CHECK(phase='Armed' OR destination_id IS NOT NULL),
          PRIMARY KEY(pair_id, source_id)) STRICT;
        CREATE INDEX folder_move_root ON folder_move(pair_id, root_id);
        PRAGMA user_version=16;
        """;
}
