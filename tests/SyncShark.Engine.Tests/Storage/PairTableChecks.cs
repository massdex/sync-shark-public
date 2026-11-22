using Massdex.SyncShark.Engine.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks publication against the actual SQLite transaction, not a store double.</summary>
public static class PairTableChecks
{
    internal static SqliteConnection Open()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        db.CreateFunction<string,string>("pathkey", PathNames.Key, isDeterministic: true);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;" + Schema.Create + "INSERT INTO pair(pair_id,def_json) VALUES('p','{}');";
        cmd.ExecuteNonQuery();
        return db;
    }

    /// <summary>Neither stale revision may publish first Repair; only the matched pair of revisions may publish it.</summary>
    public static async Task RunAsync()
    {
        var root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory;
        foreach(var file in new[]{nameof(PairTable),nameof(NoticeTable),nameof(RecordTable)})
        {
            var source=File.ReadAllText(Path.Combine(root,"src","SyncShark.Engine","Storage",file+".cs"));
            var methods=CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method=>method.Modifiers.Any(m=>m.RawKind==(int)SyntaxKind.PublicKeyword));
            foreach(var method in methods)
            {
                static bool Documented(string trivia)=>trivia.Contains("<summary>",StringComparison.Ordinal) && !trivia.Contains("operation on the supplied facts",StringComparison.Ordinal);
                Check.Require(!Documented("") && Documented(method.GetLeadingTrivia().ToFullString()),$"Missing behavior summary: {file}.{method.Identifier}");
            }
        }
        using var db = Open();
        var table = new PairTable();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO source_state(pair_id,side,repair_revision,repair_reason) VALUES(1,0,3,'local'),(1,1,5,'cloud');";
        cmd.ExecuteNonQuery();
        foreach (var stale in new[] { (2L,5L), (3L,4L) })
        {
            if (await table.FinishRepairAsync(tx,"p",stale,"{}",default)) throw new InvalidOperationException("Stale Repair reported success");
            cmd.CommandText = "SELECT first_repair_done=0 AND last_repair_json IS NULL AND (SELECT count(repair_reason) FROM source_state)=2 FROM pair";
            if ((long)cmd.ExecuteScalar()! != 1) throw new InvalidOperationException("Stale Repair published or cleared debt");
        }
        if (!await table.FinishRepairAsync(tx,"p",(3L,5L),"{}",default)) throw new InvalidOperationException("Matched Repair was refused");
        cmd.CommandText = "SELECT first_repair_done=1 AND last_repair_json='{}' AND (SELECT count(repair_reason) FROM source_state)=0 FROM pair";
        if ((long)cmd.ExecuteScalar()! != 1) throw new InvalidOperationException("Matched Repair did not publish and settle together");
        cmd.CommandText = "INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks,ignored) VALUES(1,$kind,'FailChanged','x','{}',0,1)";
        cmd.Parameters.AddWithValue("$kind",(int)NoticeKind.PutBack);
        cmd.ExecuteNonQuery();
        if (await table.PublishAsync(tx,"p",null,[],default) != TextKey.PairPutBackOutstanding) throw new InvalidOperationException("Ignored, uncommitted PutBack did not protect its owner");
        tx.Commit();
        var job=new AcceptedJob("p","route","token",[new("second","b",null),new("first","a",null)]);
        await table.SaveJobAsync(db,job,default);
        await table.SettleJobAsync(db,job with { Launch=job.Launch.Reverse().ToArray() },default);
        var jobs=new List<AcceptedJob>(); await foreach(var j in table.JobsAsync(db,"p",default)) jobs.Add(j);
        Check.Require(jobs.Count==1 && jobs[0].Launch.SequenceEqual(job.Launch),"A reordered settlement retired the job or lost send order");
        await table.SettleJobAsync(db,job,default);
        await foreach(var j in table.JobsAsync(db,"p",default)) throw new InvalidOperationException("Exact job remained");
        var selected=new RemovalSelection(new RemovalProof(new Entry("folder",true,0,DateTimeOffset.UnixEpoch,"12","","",null),"volume",null),null);
        await table.ArmAsync(db,"p",selected,null,default);
        var stage=new StageProof("stage","volume","13",0,new string('a',64));
        await table.DisarmAsync(db,"p",selected,stage,default);
        Check.Require(await table.ArmedAsync(db,"p",default)==(selected,(StageProof?)null),"Wrong stage cleared the armed victim");
        await table.DisarmAsync(db,"p",selected,null,default);
        Check.Require(await table.ArmedAsync(db,"p",default)==null,"Exact victim and stage did not settle");
        var position=new JournalPosition(ulong.MaxValue,7);
        using(var reset=db.BeginTransaction())
        {
            await table.ResetJournalAsync(reset,"vol",position,["p"],"journal reset",default);
            await table.ResetCursorAsync(reset,"p","opaque","cursor reset",default);
            reset.Commit();
        }
        Check.Require(await table.PositionAsync(db,"vol",default)==position && await table.CursorAsync(db,"p",default)=="opaque" &&
            await table.ReadRepairAsync(db,"p",Side.Local,default)==(4L,"journal reset") && await table.ReadRepairAsync(db,"p",Side.Dropbox,default)==(6L,"cursor reset"),"Reset lost a boundary or a revision");
    }
}
