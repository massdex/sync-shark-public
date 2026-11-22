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
