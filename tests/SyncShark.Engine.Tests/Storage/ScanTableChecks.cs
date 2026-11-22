using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Tests.Storage;
/// <summary>Checks isolation and ordered listing writes on the attached scan database.</summary>
public static class ScanTableChecks
{
    internal static async Task VerifyNoticeOnlyAsync()
    {
        using var db=await DeletedNamesChecks.OpenAsync();var scans=new ScanTable();var id=Guid.NewGuid();scans.Begin("p",id);
        using var seed=db.CreateCommand();seed.CommandText="INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks) VALUES(1,$kind,$cause,'notice path',$subject,1)";
        var member=new NoticeItem(Side.Dropbox,"folder/file","cloud");seed.Parameters.AddWithValue("$kind",(int)NoticeKind.PutBack);seed.Parameters.AddWithValue("$cause",nameof(TextKey.FailChanged));seed.Parameters.AddWithValue("$subject",System.Text.Json.JsonSerializer.Serialize(new IssueSubject([member],null)));await seed.ExecuteNonQueryAsync();
        using var footprint=db.CreateCommand();footprint.CommandText="INSERT INTO issue_member(issue_id,pair_id,side,path_key,identity,subject_json) SELECT issue_id,1,1,pathkey($path),$id,$json FROM issue";footprint.Parameters.AddWithValue("$path",member.Path);footprint.Parameters.AddWithValue("$id",member.Id);footprint.Parameters.AddWithValue("$json",System.Text.Json.JsonSerializer.Serialize(new {Version=1,Value=new {Items=new[]{new {Side="Dropbox",member.Path,member.Id}}}}));await footprint.ExecuteNonQueryAsync();
        scans.Touch("p",Side.Dropbox,"folder",true);var count=0;
        await foreach(var row in scans.CompareAsync(db,"p",id,null,(_,_) => (true,false),1,default))
        {
            var n=row.Notices.Single();Check.Require(row.Row.Key==PathNames.DropboxKey(member.Path) && n.Notice.Kind==NoticeKind.PutBack && n.Member==member && n.Touched && n.Covered,"A legacy put-back member lost its endpoint or freshness");count++;
        }
        Check.Require(count==1,"Notice-only binding duplicated or absent");
    }

    internal static async Task VerifyCountedWorkAsync(bool delete)
    {
        async Task<long> Steps(int count, bool scoped,bool mapped=false)
        {
            using var db=await DeletedNamesChecks.OpenAsync();var id=Guid.NewGuid();var scan=new ScanTable();scan.Begin("p",id);
            using var seed=db.CreateCommand();seed.CommandText="""
                WITH RECURSIVE n(i) AS(VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<$n)
                INSERT INTO scan_state.scan(pair_id,scan_id,side,path,is_folder,size,mtime,id,rev,hash,shared)
                SELECT 1,$id,1,printf('z/%06d',i),0,1,0,i,'','',0 FROM n;
                INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash)
                SELECT 1,path,0,id,1,0,cast(id AS text),'' FROM scan_state.scan;
                INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks) SELECT 1,0,'FailChanged',path,'{"Items":[]}',0 FROM scan_state.scan;
                INSERT INTO issue_member(issue_id,pair_id,side,path_key,identity,subject_json) SELECT issue_id,1,1,pathkey(path),'','{"Version":1,"Value":{"Items":[]}}' FROM issue;
                INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks) VALUES(1,0,'FailChanged','a','{"Items":[]}',0);
                """;seed.Parameters.AddWithValue("$id",id.ToByteArray());seed.Parameters.AddWithValue("$n",count);await seed.ExecuteNonQueryAsync();
            if(mapped){using var update=db.CreateCommand();update.CommandText="UPDATE synced_entry SET dropbox_path_delta='remote/'||local_path,dropbox_key_delta=pathkey('remote/'||local_path);";await update.ExecuteNonQueryAsync();}
            var statements=new List<string>();var deletes=new List<string>();SQLitePCL.strdelegate_trace trace=(_,sql)=>{if(sql.StartsWith("WITH",StringComparison.OrdinalIgnoreCase)||sql.StartsWith("SELECT",StringComparison.OrdinalIgnoreCase))statements.Add(sql);if(sql.StartsWith("DELETE FROM scan_state.scan",StringComparison.OrdinalIgnoreCase))deletes.Add(sql);};
            SQLitePCL.raw.sqlite3_trace(db.Handle!,trace,null);
            long steps=0;SQLitePCL.raw.sqlite3_progress_handler(db.Handle!,1,_=>{steps++;return 0;},null);
            try
            {
                if(delete){static async IAsyncEnumerable<Listed> Gone(){await Task.CompletedTask;yield return new("none",null);}await scan.AddScanAsync(db,"p",id,Side.Dropbox,Gone(),null,1,default);}
                else{await foreach(var row in scan.CompareAsync(db,"p",id,mapped?new Scope("a","remote"):scoped?new Scope("a","a"):null,(_,_) => (true,false),1,default)){Check.Require(row.Row.Key==PathNames.Key("a"),"First notice-only key was lost");break;}}
            } finally{SQLitePCL.raw.sqlite3_progress_handler(db.Handle!,0,null,null);SQLitePCL.raw.sqlite3_trace(db.Handle!,(SQLitePCL.strdelegate_trace)null!,null);}
            if(delete){Check.Require(deletes.Count==2,"Exact and descendant deletes are not separate seeks");foreach(var sql in deletes){using var plan=db.CreateCommand();plan.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var name in new[]{"$p","$s","$side","$k","$lo","$hi"})plan.Parameters.AddWithValue(name,DBNull.Value);using var rd=plan.ExecuteReader();bool seek=false;while(rd.Read()){var detail=rd.GetString(3);if(detail.Contains("scan_own",StringComparison.Ordinal) && detail.Contains("<expr>",StringComparison.Ordinal))seek=true;}Check.Require(seek,"Delete seeks the partition but not the path expression");}}
            if(!delete && count==400){int number=0;foreach(var sql in statements){using var plan=db.CreateCommand();plan.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var name in new[]{"$p","$s","$keys","$l","$d"})plan.Parameters.AddWithValue(name,DBNull.Value);using var rd=plan.ExecuteReader();while(rd.Read())Console.WriteLine($"RepairPlan {scoped} {number}: {rd.GetString(3)}");number++;}}
            return steps;
        }
        var failure=new List<string>();foreach(var scoped in delete?new[]{false}:new[]{false,true}){var small=await Steps(200,scoped);var large=await Steps(400,scoped);Console.WriteLine($"Storage counted work: delete={delete} scoped={scoped} rows=200:{small},400:{large}");if(large>small+small/4)failure.Add($"{small}->{large} scoped={scoped}");}
        if(!delete){var small=await Steps(200,true,true);var large=await Steps(400,true,true);Console.WriteLine($"Mapped first-page VM: 200={small}, 400={large}; includes sorting the selected remote range by local key");}
        Check.Require(failure.Count==0,"Whole-pair VM work repeated: "+string.Join(",",failure));
    }
    internal static async Task AliasCostAsync()
    {
        async Task<long> Cost(int count)
        {
            using var db=await DeletedNamesChecks.OpenAsync();using var cmd=db.CreateCommand();var scan=Guid.NewGuid();cmd.CommandText="""
                WITH RECURSIVE n(i) AS(VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<$n)
                INSERT INTO scan_state.scan(pair_id,scan_id,side,path,is_folder,size,mtime,id,rev,hash) SELECT 1,$s,0,printf('aliases/%06d',i),0,1,0,17,'','' FROM n;
                INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) SELECT 1,path,0,17,1,0,path,'' FROM scan_state.scan;
                """;cmd.Parameters.AddWithValue("$n",count);cmd.Parameters.AddWithValue("$s",scan.ToByteArray());await cmd.ExecuteNonQueryAsync();long steps=0;
            SQLitePCL.raw.sqlite3_progress_handler(db.Handle!,1,_=>{steps++;return 0;},null);
            try{await foreach(var _ in new ScanTable().RenamesAsync(db,"p",scan,null,10,default))throw new InvalidDataException("Unchanged alias became a rename");}finally{SQLitePCL.raw.sqlite3_progress_handler(db.Handle!,0,null,null);}
            return steps;
        }
        long small=await Cost(200),large=await Cost(400);Console.WriteLine($"Rename alias VM: 200={small}, 400={large}; per alias={small/200.0:F2}/{large/400.0:F2}");
        Check.Require(large<=2*small+small/10,"Rename work per unchanged hard link grew");
    }
    internal static async Task MappedNoticesAsync(bool decode)
    {
        using var db=await DeletedNamesChecks.OpenAsync();using var seed=db.CreateCommand();var id=Guid.NewGuid();seed.CommandText="""
            INSERT INTO synced_entry(pair_id,local_path,dropbox_key_delta,dropbox_path_delta,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) VALUES(1,'pc',pathkey('cloud'),'cloud',0,11,1,0,'bound','');
            INSERT INTO scan_state.scan(pair_id,scan_id,side,path,is_folder,size,mtime,id,rev,hash) VALUES(1,$s,1,'cloud',0,1,0,'bound','',''),(1,$s,1,'pc',0,1,0,'free','','');
            INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks) VALUES(1,1,0,'FailChanged','cloud','{"Items":[{"Side":1,"Path":"cloud","Id":"bound"}],"Incoming":null}',0),(2,1,0,'FailChanged','pc','{"Items":[{"Side":1,"Path":"pc","Id":"free"}],"Incoming":null}',0);
            INSERT INTO issue_member(issue_id,pair_id,side,path_key,identity,subject_json) SELECT issue_id,1,1,pathkey(path),json_extract(subject_json,'$.Items[0].Id'),'{}' FROM issue;
            """;seed.Parameters.AddWithValue("$s",id.ToByteArray());await seed.ExecuteNonQueryAsync();
        if(decode){var n=0;await foreach(var row in new ScanTable().CompareAsync(db,"p",id,null,(_,_) => (true,false),2,default)){Check.Require(row.Notices.Count==1 && row.Notices[0].Member?.Id==row.Row.Dropbox?.Id,"Mapped row received the other item's notice");n++;}Check.Require(n==2,"Expected both mapped and free row");return;}
        // Inspect the actual SQL projection independently of the declaration-only entry codecs in the worker lane.
        var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
        string Fragment(string name)=>(string)typeof(ScanTable).GetField(name,flags)!.GetRawConstantValue()!;
        using var query=db.CreateCommand();query.CommandText=Fragment("PagePrefix")+Fragment("MemberRows");query.Parameters.AddWithValue("$keys",System.Text.Json.JsonSerializer.Serialize(new[]{PathNames.Key("pc")}));query.Parameters.AddWithValue("$p","p");query.Parameters.AddWithValue("$s",id.ToByteArray());
        using var reader=query.ExecuteReader();int found=0;while(reader.Read()){Check.Require(reader.FieldCount>13 && reader.GetBoolean(13)==(reader.GetInt64(1)==1),"Mapped notice projection does not identify bound vs free row");found++;}Check.Require(found==2,"Wrong mapped notice count");
    }
    /// <summary>One acquisition's clear must not remove another's completed listing.</summary>
    public static async Task RunAsync()
    {
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
        scans.Touch("p", Side.Local, "parent/file", false);
        scans.Touch("p", Side.Dropbox, "tree", true);
        Check.Require(scans.Touched("p", keep, Side.Local, "parent", true) && !scans.Touched("p", keep, Side.Local, "parent", false) && scans.Touched("p", keep, Side.Dropbox, "tree/child", false) && !scans.Touched("p", keep, Side.Dropbox, "treehouse", true), "Commit footprint crossed a boundary or missed a descendant");
    }
}
