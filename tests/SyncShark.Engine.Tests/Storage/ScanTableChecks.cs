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
        scans.Touch("p",Side.Dropbox,"folder",true);var count=0;
        await foreach(var row in scans.CompareAsync(db,"p",id,null,(_,_) => (true,false),1,default))
        {
            var n=row.Notices.Single();Check.Require(row.Row.Key==PathNames.DropboxKey(member.Path) && n.Notice.Kind==NoticeKind.PutBack && n.Member==member && n.Touched && n.Covered,"A legacy put-back member lost its endpoint or freshness");count++;
        }
        Check.Require(count==1,"Notice-only binding duplicated or absent");
    }

    internal static async Task VerifyCountedWorkAsync(bool delete)
    {
        async Task<long> Steps(int count, bool scoped)
        {
            using var db=await DeletedNamesChecks.OpenAsync();var id=Guid.NewGuid();var scan=new ScanTable();scan.Begin("p",id);
            using var seed=db.CreateCommand();seed.CommandText="""
                WITH RECURSIVE n(i) AS(VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<$n)
                INSERT INTO scan_state.scan(pair_id,scan_id,side,path,is_folder,size,mtime,id,rev,hash,shared)
                SELECT 1,$id,1,printf('z/%06d',i),0,1,0,i,'','',0 FROM n;
                INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash)
                SELECT 1,path,0,id,1,0,cast(id AS text),'' FROM scan_state.scan;
                INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks) VALUES(1,0,'FailChanged','a','{"Items":[]}',0);
                """;seed.Parameters.AddWithValue("$id",id.ToByteArray());seed.Parameters.AddWithValue("$n",count);await seed.ExecuteNonQueryAsync();
            long steps=0;SQLitePCL.raw.sqlite3_progress_handler(db.Handle!,1,_=>{steps++;return 0;},null);
            try
            {
                if(delete){static async IAsyncEnumerable<Listed> Gone(){await Task.CompletedTask;yield return new("none",null);}await scan.AddScanAsync(db,"p",id,Side.Dropbox,Gone(),null,1,default);}
                else{await foreach(var row in scan.CompareAsync(db,"p",id,scoped?new Scope("a","a"):null,(_,_) => (true,false),1,default)){Check.Require(row.Row.Key==PathNames.Key("a"),"First notice-only key was lost");break;}}
            } finally{SQLitePCL.raw.sqlite3_progress_handler(db.Handle!,0,null,null);}
            return steps;
        }
        var failure=new List<string>();foreach(var scoped in delete?new[]{false}:new[]{false,true}){var small=await Steps(200,scoped);var large=await Steps(400,scoped);Console.WriteLine($"Storage counted work: delete={delete} scoped={scoped} rows=200:{small},400:{large}");if(large>small+small/4)failure.Add($"{small}->{large} scoped={scoped}");}
        Check.Require(failure.Count==0,"Whole-pair VM work repeated: "+string.Join(",",failure));
    }
    /// <summary>One acquisition's clear must not remove another's completed listing.</summary>
    public static async Task RunAsync()
    {
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
