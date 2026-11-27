using Massdex.SyncShark.Engine.Storage;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Storage;
/// <summary>Checks isolation and ordered listing writes on the attached scan database.</summary>
public static class ScanTableChecks
{
    internal static async Task VerifyNoticeOnlyAsync()
    {
        using var db=await DeletedNamesChecks.OpenAsync();PairTableChecks.Entries(db);var scans=new ScanTable();var id=Guid.NewGuid();scans.Begin("p",id);
        using var seed=db.CreateCommand();seed.CommandText="INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks) VALUES(1,$kind,$cause,'notice path',$subject,1)";
        var member=new NoticeItem(Side.Dropbox,"folder/file","cloud");seed.Parameters.AddWithValue("$kind",(int)NoticeKind.PutBack);seed.Parameters.AddWithValue("$cause",nameof(TextKey.FailChanged));seed.Parameters.AddWithValue("$subject",System.Text.Json.JsonSerializer.Serialize(new IssueSubject([member],null)));await seed.ExecuteNonQueryAsync();
        using var footprint=db.CreateCommand();footprint.CommandText="INSERT INTO issue_member(issue_id,pair_id,side,path_key,identity,subject_json) SELECT issue_id,1,1,pathkey($path),$id,$json FROM issue";footprint.Parameters.AddWithValue("$path",member.Path);footprint.Parameters.AddWithValue("$id",member.Id);footprint.Parameters.AddWithValue("$json",System.Text.Json.JsonSerializer.Serialize(new {Version=1,Value=new {Items=new[]{new {Side="Dropbox",member.Path,member.Id}},Incoming=(string?)null}}));await footprint.ExecuteNonQueryAsync();
        scans.Touch("p",Side.Dropbox,PathNames.KeyFor(Side.Dropbox,"folder"),true);var count=0;
        await foreach(var row in scans.CompareAsync(db,"p",id,null,(_,_) => (true,false),1,default))
        {
            var n=row.Notices.Single();Check.Require(row.Row.Key==PathNames.DropboxKey(member.Path) && n.Notice.Kind==NoticeKind.PutBack && n.Member==member && n.Touched && n.Covered,"A legacy put-back member lost its endpoint or freshness");count++;
        }
        Check.Require(count==1,"Notice-only binding duplicated or absent");
    }

    private static Type CompareType => typeof(ScanTable).Assembly.GetType(typeof(ScanTable).Namespace+".ScanCompare",true)!;
    private const System.Reflection.BindingFlags SqlFields=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
    private static string Fragment(string name)=>Sql.Get(Enum.Parse<SqlKey>("ScanCompare"+name));
    internal static Microsoft.Data.Sqlite.SqliteCommand PageQuery(Microsoft.Data.Sqlite.SqliteConnection db,Guid scan,string[] keys,string fragment)
    {
        var command=db.CreateCommand();command.CommandText=Fragment("PagePrefix")+Fragment(fragment);command.Parameters.AddWithValue("$keys",System.Text.Json.JsonSerializer.Serialize(keys));command.Parameters.AddWithValue("$p","p");command.Parameters.AddWithValue("$s",scan.ToByteArray());return command;
    }
    private static async Task<string> CountFirstSqlPage(Microsoft.Data.Sqlite.SqliteConnection db,Guid scan,Scope? scope,string? after=null)
    {
        using var command=(Microsoft.Data.Sqlite.SqliteCommand)CompareType.GetMethod("KeyStream",SqlFields)!.Invoke(null,new object?[]{db,"p",scan,scope,after})!;
        using var keys=await command.ExecuteReaderAsync();Check.Require(await keys.ReadAsync(),"No first key");var key=keys.GetString(0);
        foreach(var fragment in new[]{"MemberRows","CompareRows"}){using var page=PageQuery(db,scan,[key],fragment);using var rows=await page.ExecuteReaderAsync();Check.Require(await rows.ReadAsync(),"First page lost the notice or row");while(await rows.ReadAsync()){} }
        return key;
    }
    internal static async Task ScanReaderColumnsAsync()
    {
        var root=CheckArguments.Parse(Environment.GetCommandLineArgs()[1..]).Root;
        var violations=new List<string>();
        foreach(var name in new[]{"ScanCompare", "ScanTable", "DeletedNames", "StoreMove", "StoreImage", "SchemaUpgrade"})
        {
            var file=Path.Combine(root,"src","SyncShark.Engine","Storage",name+".cs");
            var syntax=CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(file)).GetRoot();
            foreach(var method in syntax.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                foreach(var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if(call.Expression is not MemberAccessExpressionSyntax access || access.Name.Identifier.Text is not ("GetString" or "GetInt32" or "GetInt64" or "GetBoolean" or "IsDBNull" or "ReadSynced" or "ReadEntry")) continue;
                    if(call.ArgumentList.Arguments.Any(a=>a.Expression is LiteralExpressionSyntax n && n.RawKind==(int)SyntaxKind.NumericLiteralExpression))
                    {
                        // These readers each project one column; only ordinal zero is valid there.
                        var single=(name,method.Identifier.Text,access.Expression.ToString()) is ("DeletedNames","PublishBlocks","reader") or ("StoreMove","Required","rd") or ("SchemaUpgrade","Names" or "RebuildTables","rd");
                        if(!single || call.ArgumentList.Arguments.ToString()!="0") violations.Add(name+"."+method.Identifier.Text+"."+access.Name.Identifier.Text);
                    }
                }
            }
        }
        Check.Require(violations.Count==0,"Scan projection relies on literal columns: "+string.Join(",",violations));
    }

    internal static Task SnapshotCostAsync() => WithWalAsync(async (reader, writer, scan) =>
    {
        writer.CommandText = "INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks) VALUES(1,0,'FailChanged','a','{\"Items\":[],\"Incoming\":null}',1),(1,0,'FailChanged','b','{\"Items\":[],\"Incoming\":null}',1)";
        await writer.ExecuteNonQueryAsync();
        writer.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await writer.ExecuteNonQueryAsync();
        await using var rows = new ScanTable().CompareAsync(reader, "p", scan, null, (_, _) => (true,false), 1, default).GetAsyncEnumerator();
        Check.Require(await rows.MoveNextAsync(), "No first compare row");
        var keys = new List<string> { rows.Current.Row.Key };
        await VerifyCheckpointAsync(writer);
        while (await rows.MoveNextAsync()) keys.Add(rows.Current.Row.Key);
        Check.Require(keys.SequenceEqual(new[]{"a","b","later"}.Select(PathNames.Key)), "Compare skipped or repeated a key after resume");
    });

    internal static Task RenameReaderReleasesWalAsync() => WithWalAsync(async (reader, writer, scan) =>
    {
        using var seed = reader.CreateCommand();
        seed.CommandText = "INSERT INTO entry_rows(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) VALUES(1,'a1',0,1,0,0,'one',''),(1,'a2',0,2,0,0,'two',''); INSERT INTO scan_state.scan(pair_id,scan_id,side,path,is_folder,size,mtime,id,rev,hash) VALUES(1,$s,0,'b1',0,0,0,1,'',''),(1,$s,0,'b2',0,0,0,2,'','');";
        seed.Parameters.AddWithValue("$s",scan.ToByteArray()); await seed.ExecuteNonQueryAsync();
        seed.CommandText = "SELECT compile_options FROM pragma_compile_options WHERE compile_options LIKE 'TEMP_STORE=%'";
        Console.WriteLine($"Rename SQLite build: {await seed.ExecuteScalarAsync()}");
        writer.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; await writer.ExecuteNonQueryAsync();
        await using (var rows = new ScanTable().RenamesAsync(reader,"p",scan,null,1,default).GetAsyncEnumerator())
        {
            Check.Require(await rows.MoveNextAsync() && rows.Current.Synced.LocalPath=="a1", "Missing first rename");
            seed.CommandText = "PRAGMA temp_store";
            Check.Require(Convert.ToInt64(await seed.ExecuteScalarAsync())==1,"Rename reader did not select file-backed temporary storage");
            await VerifyCheckpointAsync(writer);
            Check.Require(await rows.MoveNextAsync() && rows.Current.Synced.LocalPath=="a2" && !await rows.MoveNextAsync(), "Rename order or count changed");
        }
        seed.CommandText="SELECT count(*) FROM temp.sqlite_master WHERE name='rename_order'";
        Check.Require(Convert.ToInt64(await seed.ExecuteScalarAsync())==0,"Rename temporary table retained");
    });

    private static async Task VerifyCheckpointAsync(Microsoft.Data.Sqlite.SqliteCommand writer)
    {
        writer.CommandText="INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks) VALUES(1,0,'FailChanged','later','{\"Items\":[],\"Incoming\":null}',1)";
        await writer.ExecuteNonQueryAsync(); writer.CommandText="PRAGMA wal_checkpoint(PASSIVE)";
        using var checkpoint=await writer.ExecuteReaderAsync();
        int log = checkpoint.GetOrdinal("log"), copied = checkpoint.GetOrdinal("checkpointed");
        Check.Require(await checkpoint.ReadAsync() && checkpoint.GetInt32(log)>0 && checkpoint.GetInt32(copied)==checkpoint.GetInt32(log),"Paused consumer pinned the WAL");
    }

    private static async Task WithWalAsync(Func<Microsoft.Data.Sqlite.SqliteConnection,Microsoft.Data.Sqlite.SqliteCommand,Guid,Task> probe)
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cs=new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"pages.sqlite"),Pooling=false}.ToString();
            using var writer=new Microsoft.Data.Sqlite.SqliteConnection(cs);await writer.OpenAsync();writer.CreateFunction<string,string>("pathkey",PathNames.Key,true);
            using var cmd=writer.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL;"+Schema.Create+" INSERT INTO pair(pair_id,def_json) VALUES('p','{}');";await cmd.ExecuteNonQueryAsync();
            using var reader=new Microsoft.Data.Sqlite.SqliteConnection(cs);await reader.OpenAsync();reader.CreateFunction<string,string>("pathkey",PathNames.Key,true);
            using var attach=reader.CreateCommand();attach.CommandText="ATTACH ':memory:' AS scan_state;"+Schema.CreateScan;await attach.ExecuteNonQueryAsync();PairTableChecks.Entries(reader);
            await probe(reader,cmd,Guid.NewGuid());
        }finally{Directory.Delete(root,true);}
    }
    internal static async Task VerifyCountedWorkAsync(bool delete)
    {
        async Task<long> Steps(int count, bool scoped,bool mapped=false,bool resumed=false)
        {
            using var db=await DeletedNamesChecks.OpenAsync();PairTableChecks.Entries(db);var id=Guid.NewGuid();var scan=new ScanTable();scan.Begin("p",id);
            using var seed=db.CreateCommand();seed.CommandText="""
                WITH RECURSIVE n(i) AS(VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<$n)
                INSERT INTO scan_state.scan(pair_id,scan_id,side,path,is_folder,size,mtime,id,rev,hash,shared)
                SELECT 1,$id,1,printf('z/%06d',i),0,1,0,i,'','',0 FROM n;
                INSERT INTO entry_rows(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash)
                SELECT 1,path,0,id,1,0,cast(id AS text),'' FROM scan_state.scan;
                INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks) SELECT 1,0,'FailChanged',path,'{"Items":[],"Incoming":null}',0 FROM scan_state.scan;
                INSERT INTO issue_member(issue_id,pair_id,side,path_key,identity,subject_json) SELECT issue_id,1,1,pathkey(path),'','{"Version":1,"Value":{"Items":[],"Incoming":null}}' FROM issue;
                INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks) VALUES(1,0,'FailChanged','a','{"Items":[],"Incoming":null}',0);
                """;seed.Parameters.AddWithValue("$id",id.ToByteArray());seed.Parameters.AddWithValue("$n",count);await seed.ExecuteNonQueryAsync();
            if (mapped)
            {
                using var update = db.CreateCommand();
                update.CommandText = "UPDATE pair SET first_repair_done=1;UPDATE synced_entry SET dropbox_path_delta='remote/'||(SELECT path FROM folder WHERE id=folder_id)||name,dropbox_key_delta=pathkey('remote/'||(SELECT path FROM folder WHERE id=folder_id)||name);";
                await update.ExecuteNonQueryAsync();
            }
            var statements = new List<string>();
            var deletes = new List<string>();
            SQLitePCL.strdelegate_trace trace = (_, sql) =>
            {
                if (sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) statements.Add(sql);
                if (sql.StartsWith("DELETE FROM scan_state.scan", StringComparison.OrdinalIgnoreCase)) deletes.Add(sql);
            };
            SQLitePCL.raw.sqlite3_trace(db.Handle!, trace, null);
            long steps = 0;
            SQLitePCL.raw.sqlite3_progress_handler(db.Handle!, 1, _ => { steps++; return 0; }, null);
            try
            {
                if (delete)
                {
                    static async IAsyncEnumerable<Listed> Gone() { await Task.CompletedTask; yield return new("none", null); }
                    await scan.AddScanAsync(db, "p", id, Side.Dropbox, Gone(), null, 1, default);
                }
                else Check.Require(await CountFirstSqlPage(db, id, mapped ? new Scope("unused", "remote") : scoped ? new Scope(resumed ? "z" : "a", resumed ? "z" : "a") : null,
                    resumed ? PathNames.Key("z/000100") : null) == PathNames.Key(resumed ? "z/000101" : mapped ? "z/000001" : "a"), "Incorrect first mapped or notice-only key");
            }
            finally
            {
                SQLitePCL.raw.sqlite3_progress_handler(db.Handle!, 0, null, null);
                SQLitePCL.raw.sqlite3_trace(db.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
            }
            if (delete)
            {
                Check.Require(deletes.Count == 2, "Exact and descendant deletes are not separate seeks");
                foreach (var sql in deletes)
                {
                    using var plan = db.CreateCommand();
                    plan.CommandText = "EXPLAIN QUERY PLAN " + sql;
                    foreach (var name in new[] { "$p", "$s", "$side", "$k", "$lo", "$hi" }) plan.Parameters.AddWithValue(name, DBNull.Value);
                    using var rd = plan.ExecuteReader();
                    int detailColumn = rd.GetOrdinal("detail");
                    bool seek = false;
                    while (rd.Read())
                    {
                        var detail = rd.GetString(detailColumn);
                        if (detail.Contains("scan_own", StringComparison.Ordinal) && detail.Contains("<expr>", StringComparison.Ordinal)) seek = true;
                    }
                    Check.Require(seek, "Delete seeks the partition but not the path expression");
                }
            }
            if (!delete && count == 400)
            {
                int number = 0;
                foreach (var sql in statements)
                {
                    using var plan = db.CreateCommand();
                    plan.CommandText = "EXPLAIN QUERY PLAN " + sql;
                    foreach (var name in new[] { "$p", "$s", "$keys", "$l", "$d", "$a" }) plan.Parameters.AddWithValue(name, DBNull.Value);
                    using var rd = plan.ExecuteReader();
                    int detailColumn = rd.GetOrdinal("detail");
                    while (rd.Read()) Console.WriteLine($"RepairPlan {scoped} {number}: {rd.GetString(detailColumn)}");
                    number++;
                }
            }
            return steps;
        }
        var failure = new List<string>();
        foreach (var scoped in delete ? new[] { false } : new[] { false, true })
        {
            var small = await Steps(200, scoped);
            var large = await Steps(400, scoped);
            Console.WriteLine($"Storage counted work: delete={delete} scoped={scoped} rows=200:{small},400:{large}");
            if (large > small + small / 4) failure.Add($"{small}->{large} scoped={scoped}");
        }
        if(!delete) foreach(var scoped in new[]{false,true})
        {
            var small=await Steps(200,scoped,resumed:true); var large=await Steps(400,scoped,resumed:true);
            Console.WriteLine($"Resumed page VM: scoped={scoped}, 200={small}, 400={large}");
            if(large>small+small/4) failure.Add($"resumed {small}->{large} scoped={scoped}");
        }
        if(!delete){var small=await Steps(200,true,true);var large=await Steps(400,true,true);Console.WriteLine($"Mapped first-page VM: 200={small}, 400={large}; first actual mapped binding, local output order");}
        Check.Require(failure.Count==0,"Whole-pair VM work repeated: "+string.Join(",",failure));
    }
    /// <summary>Folder-store work per operation on a fixed involved scope stays flat while unrelated bindings grow; per-item counts are printed for the handback.</summary>
    internal static async Task FolderStoreCountedWorkAsync()
    {
        var small=await MeasureAsync(200);var large=await MeasureAsync(400);var failure=new List<string>();
        foreach(var (name,s) in small)
        {
            var l=large[name];
            Console.WriteLine($"Folder-store counted work {name}: 200 steps={s.Steps} calls={s.Calls} statements={s.Statements}; 400 steps={l.Steps} calls={l.Calls} statements={l.Statements}");
            if(l.Steps>s.Steps+s.Steps/4 || l.Calls!=s.Calls || l.Statements!=s.Statements) failure.Add($"{name} {s.Steps}/{s.Calls}/{s.Statements}->{l.Steps}/{l.Calls}/{l.Statements}");
        }
        Check.Require(failure.Count==0,"Fixed-scope folder-store work grew with unrelated bindings: "+string.Join(", ",failure));
    }
    private static async Task<Dictionary<string,(long Steps,long Calls,int Statements)>> MeasureAsync(int unrelated)
    {
        using var db=await DeletedNamesChecks.OpenAsync();PairTableChecks.Entries(db);
        long calls=0;
        db.CreateFunction<string,string>("pathkey",p=>{calls++;return PathNames.Key(p);},true);
        db.CreateFunction<string,string>("pathfolder",p=>{calls++;return PathNames.Folder(p);},true);
        db.CreateFunction<string,string>("pathname",p=>{calls++;return PathNames.Name(p);},true);
        var scan=Guid.NewGuid();var table=new ScanTable();table.Begin("p",scan);
        using(var seed=db.CreateCommand())
        {
            seed.CommandText=$"""
                WITH RECURSIVE n(i) AS(VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<{unrelated})
                INSERT INTO entry_rows(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) SELECT 1,printf('other/%03d/%06d',i/10,i),0,1000+i,1,0,'do'||i,'' FROM n;
                INSERT INTO entry_rows(pair_id,local_path,dropbox_key_delta,dropbox_path_delta,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) VALUES
                  (1,'in/a',NULL,NULL,0,7,1,0,'da',''),(1,'in/b',NULL,NULL,0,7,1,0,'db',''),(1,'in/c',pathkey('cloud/c'),'cloud/c',0,8,1,0,'dc','');
                INSERT INTO scan_state.scan(pair_id,scan_id,side,path,is_folder,size,mtime,id,rev,hash) VALUES(1,$s,0,'in/z',0,1,0,8,'',''),(1,$s,1,'cloud/c',0,1,0,'dc','','');
                """;
            seed.Parameters.AddWithValue("$s",scan.ToByteArray());await seed.ExecuteNonQueryAsync();
        }
        var statements=0;SQLitePCL.strdelegate_trace trace=(_,t)=>{ if(t!=null && !t.StartsWith("--")) statements++; };
        var result=new Dictionary<string,(long,long,int)>();
        async Task Count(string name,Func<Task> work)
        {
            long steps=0;calls=0;statements=0;
            SQLitePCL.raw.sqlite3_trace(db.Handle!,trace,null);SQLitePCL.raw.sqlite3_progress_handler(db.Handle!,1,_=>{steps++;return 0;},null);
            try { await work(); }
            finally { SQLitePCL.raw.sqlite3_progress_handler(db.Handle!,0,null,null);SQLitePCL.raw.sqlite3_trace(db.Handle!,(SQLitePCL.strdelegate_trace)null!,null); }
            result[name]=(steps,calls,statements);
        }
        static async Task Drain<T>(IAsyncEnumerable<T> rows) { await foreach(var _ in rows) { } }
        static SyncedEntry E(string path,long size=1)=>new(PathNames.Key(path),path,PathNames.Key(path),path,false,"9",size,0,"dn","");
        var records=new RecordTable();var committer=new Committer();
        await Count("Find hit",()=>records.FindAsync(db,"p",Side.Local,PathNames.Key("in/a"),default));
        await Count("Find mapped",()=>records.FindAsync(db,"p",Side.Dropbox,PathNames.Key("cloud/c"),default));
        await Count("Find miss",()=>records.FindAsync(db,"p",Side.Local,PathNames.Key("in/none"),default));
        await Count("ById alias pages",()=>Drain(records.ByIdAsync(db,"p",Side.Local,"7",1,default)));
        await Count("ById miss",()=>Drain(records.ByIdAsync(db,"p",Side.Local,"404",1,default)));
        await Count("Descendants local",()=>Drain(records.DescendantsAsync(db,"p",Side.Local,PathNames.Key("in"),1,default)));
        await Count("Descendants mapped",()=>Drain(records.DescendantsAsync(db,"p",Side.Dropbox,PathNames.Key("cloud"),1,default)));
        await Count("Renames scoped",()=>Drain(table.RenamesAsync(db,"p",scan,new Scope("in","cloud"),10,default)));
        await Count("Compare scoped",()=>Drain(table.CompareAsync(db,"p",scan,new Scope("in","cloud"),(_,_)=>(true,false),1,default)));
        await Count("insert",()=>committer.CommitAsync(db,"p",null,E("in/new"),null,null,default));
        await Count("content-only update",()=>committer.CommitAsync(db,"p",E("in/new"),E("in/new",2),null,null,default));
        await Count("same-folder rename",()=>committer.CommitAsync(db,"p",E("in/new",2),E("in/new2",2),null,null,default));
        await Count("cross-folder move",()=>committer.CommitAsync(db,"p",E("in/new2",2),E("in2/new2",2),null,null,default));
        await Count("remove",()=>committer.CommitAsync(db,"p",E("in2/new2",2),null,null,null,default));
        await Count("constraint abort",async()=>{ try { await committer.CommitAsync(db,"p",null,E("in/a"),null,null,default); } catch(Exception e) when(e is Microsoft.Data.Sqlite.SqliteException or ChangedUnderneathException) { } });
        GC.KeepAlive(trace);
        return result;
    }
    internal static async Task MappedOrderAsync(bool publicRead=false)
    {
        using var db=await DeletedNamesChecks.OpenAsync();PairTableChecks.Entries(db);using var seed=db.CreateCommand();seed.CommandText="""
            INSERT INTO entry_rows(pair_id,local_path,dropbox_path_delta,dropbox_key_delta,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) VALUES(1,'b','cloud/1',pathkey('cloud/1'),0,2,0,0,'two',''),(1,'a','cloud/2',pathkey('cloud/2'),0,1,0,0,'one',''),(1,'other','outside',pathkey('outside'),0,3,0,0,'three','');
            INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks) VALUES(1,0,'FailChanged','b','{"Items":[],"Incoming":null}',1);
            """;await seed.ExecuteNonQueryAsync();
        var id=Guid.NewGuid();var table=new ScanTable();table.Begin("p",id);var scope=new Scope("unused","cloud");
        async Task<string[]> Keys(int size)
        {
            var result=new List<string>();
            if(publicRead){await foreach(var row in table.CompareAsync(db,"p",id,scope,(_,_) => (true,false),size,default))result.Add(row.Row.Key);}
            else
            {
                using var sql=(Microsoft.Data.Sqlite.SqliteCommand)CompareType.GetMethod("KeyStream",SqlFields)!.Invoke(null,new object?[]{db,"p",id,scope,null})!;
                using var rows=await sql.ExecuteReaderAsync();
                while(true){var page=new List<string>();while(page.Count<size && await rows.ReadAsync())page.Add(rows.GetString(0));if(page.Count==0)break;
                    using var cmd=PageQuery(db,id,page.ToArray(),"CompareRows");using var data=await cmd.ExecuteReaderAsync();int bound=data.GetOrdinal("bound"), key=data.GetOrdinal("k");while(await data.ReadAsync())if(data.GetBoolean(bound))result.Add(data.GetString(key));}
            }
            return result.ToArray();
        }
        foreach(var first in new[]{0,1}){seed.CommandText="UPDATE pair SET first_repair_done="+first;await seed.ExecuteNonQueryAsync();
            var one=await Keys(1);var two=await Keys(2);Check.Require(one.SequenceEqual(two) && one.SequenceEqual(new[]{PathNames.Key("a"),PathNames.Key("b")}),$"Mapped {(publicRead?"CompareAsync":"hydration")} order varies: first_done={first}, page1={string.Join(",",one)} page2={string.Join(",",two)}");}
    }
    internal static async Task AliasCostAsync()
    {
        async Task<long> Cost(int count)
        {
            using var db=await DeletedNamesChecks.OpenAsync();PairTableChecks.Entries(db);using var cmd=db.CreateCommand();var scan=Guid.NewGuid();cmd.CommandText="""
                WITH RECURSIVE n(i) AS(VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<$n)
                INSERT INTO scan_state.scan(pair_id,scan_id,side,path,is_folder,size,mtime,id,rev,hash) SELECT 1,$s,0,printf('aliases/%06d',i),0,1,0,17,'','' FROM n;
                INSERT INTO entry_rows(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) SELECT 1,path,0,17,1,0,path,'' FROM scan_state.scan;
                """;cmd.Parameters.AddWithValue("$n",count);cmd.Parameters.AddWithValue("$s",scan.ToByteArray());await cmd.ExecuteNonQueryAsync();long steps=0;
            SQLitePCL.raw.sqlite3_progress_handler(db.Handle!,1,_=>{steps++;return 0;},null);
            try{await foreach(var _ in new ScanTable().RenamesAsync(db,"p",scan,null,10,default))throw new InvalidDataException("Unchanged alias became a rename");}finally{SQLitePCL.raw.sqlite3_progress_handler(db.Handle!,0,null,null);}
            return steps;
        }
        long small=await Cost(200),large=await Cost(400);Console.WriteLine($"Rename alias VM: 200={small}, 400={large}; per alias={small/200.0:F2}/{large/400.0:F2}");
        Check.Require(large<=2*small+small/10,"Rename work per unchanged hard link grew");
    }
    // Dropbox's listing may keep an ancestor's old case: not a move. A case rename of the item's own name still is.
    internal static async Task DropboxAncestorCaseAsync()
    {
        using var db=await DeletedNamesChecks.OpenAsync();PairTableChecks.Entries(db);using var cmd=db.CreateCommand();var scan=Guid.NewGuid();cmd.CommandText="""
            INSERT INTO entry_rows(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) VALUES(1,'Parent/Child/x.pdf',0,1,1,0,'d1',''),(1,'Parent/y.pdf',0,2,1,0,'d2',''),(1,'Parent/z.pdf',0,3,1,0,'d3','');
            INSERT INTO scan_state.scan(pair_id,scan_id,side,path,is_folder,size,mtime,id,rev,hash) VALUES(1,$s,1,'parent/Child/x.pdf',0,1,0,'d1','',''),(1,$s,1,'Parent/Y.pdf',0,1,0,'d2','',''),(1,$s,0,'parent/z.pdf',0,1,0,3,'','');
            """;cmd.Parameters.AddWithValue("$s",scan.ToByteArray());await cmd.ExecuteNonQueryAsync();
        var moved=new List<string>();await foreach(var c in new ScanTable().RenamesAsync(db,"p",scan,null,10,default))moved.Add(c.Moved.Path);
        Check.Require(moved.SequenceEqual(new[]{"Parent/Y.pdf"}),$"Ancestor case became a move or a name case rename was lost: {string.Join(",",moved)}");
    }
    internal static async Task MappedNoticesAsync(bool decode)
    {
        using var db=await DeletedNamesChecks.OpenAsync();PairTableChecks.Entries(db);using var seed=db.CreateCommand();var id=Guid.NewGuid();seed.CommandText="""
            INSERT INTO entry_rows(pair_id,local_path,dropbox_key_delta,dropbox_path_delta,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) VALUES(1,'pc',pathkey('cloud'),'cloud',0,11,1,0,'bound','');
            INSERT INTO scan_state.scan(pair_id,scan_id,side,path,is_folder,size,mtime,id,rev,hash) VALUES(1,$s,1,'cloud',0,1,0,'bound','',''),(1,$s,1,'pc',0,1,0,'free','','');
            INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks) VALUES(1,1,0,'FailChanged','cloud','{"Items":[{"Side":1,"Path":"cloud","Id":"bound"}],"Incoming":null}',0),(2,1,0,'FailChanged','pc','{"Items":[{"Side":1,"Path":"pc","Id":"free"}],"Incoming":null}',0);
            INSERT INTO issue_member(issue_id,pair_id,side,path_key,identity,subject_json) SELECT issue_id,1,1,pathkey(path),json_extract(subject_json,'$.Items[0].Id'),json_object('Version',1,'Value',json_set(subject_json,'$.Items[0].Side','Dropbox')) FROM issue;
            """;seed.Parameters.AddWithValue("$s",id.ToByteArray());await seed.ExecuteNonQueryAsync();
        if(decode){var n=0;await foreach(var row in new ScanTable().CompareAsync(db,"p",id,null,(_,_) => (true,false),2,default)){Check.Require(row.Notices.Count==1 && row.Notices[0].Member?.Id==row.Row.Dropbox?.Id,"Mapped row received the other item's notice");n++;}Check.Require(n==2,"Expected both mapped and free row");return;}
        // Inspect the actual SQL projection independently of the declaration-only entry codecs in the worker lane.
        using var query=PageQuery(db,id,[PathNames.Key("pc")],"MemberRows");
        using(var groups=PageQuery(db,id,[PathNames.Key("pc")],"CompareRows"))using(var entries=groups.ExecuteReader())
        {
            int bound=entries.GetOrdinal("bound"), entryId=entries.GetOrdinal("id");
            var seen=new List<(bool Bound,string Id)>();while(entries.Read())seen.Add((entries.GetBoolean(bound),entries.GetString(entryId)));
            Check.Require(seen.SequenceEqual(new[]{(true,"bound"),(false,"free")}),"Expected one bound and one unrelated free item");
        }
        using var reader=query.ExecuteReader();
        int ownerBound=reader.GetOrdinal("bound"), issue=reader.GetOrdinal("issue_id"), found=0;
        while(reader.Read())
        {
            Check.Require(reader.GetBoolean(ownerBound)==(reader.GetInt64(issue)==1),"Mapped notice projection does not identify bound vs free row");
            found++;
        }
        Check.Require(found==2,"Wrong mapped notice count");
    }
    /// <summary>One acquisition's clear must not remove another's completed listing.</summary>
    public static async Task RunAsync()
    {
        await MappedOrderAsync(true);
        await VerifyNoticeOnlyAsync();
        await StoreMoveChecks.VerifyPublicMigratedAsync();
        await MappedNoticesAsync(true);
        await StoreMoveChecks.VerifyPublicMixedAsync();
        using var db = await DeletedNamesChecks.OpenAsync();
        var scans = new ScanTable();
        var keep = Guid.NewGuid();
        var clear = Guid.NewGuid();
        static async IAsyncEnumerable<Listed> List(string name)
        {
            await Task.CompletedTask;
            yield return new(name, Scenarios.ItemChecks.File(Side.Local, name, 1, name));
        }

        await scans.AddScanAsync(db, "p", keep, Side.Local, List("kept"), null, 1, default);
        await scans.AddScanAsync(db, "p", clear, Side.Local, List("removed"), null, 1, default);
        await scans.ClearScanAsync(db, "p", clear, default);
        var names = new List<string?>();
        await foreach (var row in scans.CompareAsync(db, "p", keep, null, (_, _) => (true, false), 1, default))
            names.Add(row.Row.Local?.Path);
        Check.Require(names.SequenceEqual(new[] { "kept" }), "Clear removed another acquisition or compare lost a row");
        VerifyCommitFootprint(scans, keep);
    }
    /// <summary>A committed exact and subtree footprint crosses the expected boundaries and nothing else on a live scan.</summary>
    internal static void VerifyCommitFootprint(ScanTable scans, Guid keep)
    {
        scans.Touch("p", Side.Local, PathNames.KeyFor(Side.Local, "parent/file"), false);
        scans.Touch("p", Side.Dropbox, PathNames.KeyFor(Side.Dropbox, "tree"), true);
        Check.Require(scans.Touched("p", keep, Side.Local, "parent", true) && !scans.Touched("p", keep, Side.Local, "parent", false) && scans.Touched("p", keep, Side.Dropbox, "tree/child", false) && !scans.Touched("p", keep, Side.Dropbox, "treehouse", true), "Commit footprint crossed a boundary or missed a descendant");
    }
}
