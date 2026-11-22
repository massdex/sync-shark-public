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
        var additions=new List<string>();
        if(typeof(NoticeTable).GetMethods().Count(m=>m.Name==nameof(NoticeTable.NoticesUnderAsync))!=1) additions.Add("Scoped notice reader exposes a second page-size route");
        if(typeof(PairTable).GetMethods().Count(m=>m.Name==nameof(PairTable.ResetJournalAsync))!=1) additions.Add("Reset has a forwarding-only second entry point");
        Check.Require(additions.Count==0,string.Join("; ",additions));
        await NoticeTableChecks.RootInfoSlots();
        await NoticeTableChecks.PagePlan();
        await NoticeTableChecks.ScopePlan();
        var root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory;
        foreach(var file in new[]{nameof(PairTable),nameof(NoticeTable),nameof(RecordTable)})
        {
            var source=File.ReadAllText(Path.Combine(root,"src","SyncShark.Engine","Storage",file+".cs"));
            var methods=CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method=>method.Modifiers.Any(m=>m.RawKind==(int)SyntaxKind.PublicKeyword));
            foreach(var method in methods)
            {
                var summaries=method.GetLeadingTrivia().Select(t=>t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>()
                    .SelectMany(doc=>doc.Content.OfType<XmlElementSyntax>())
                    .Where(element=>!element.ContainsDiagnostics && element.StartTag.Name.ToString()=="summary" && element.EndTag.Name.ToString()=="summary")
                    .Select(element=>string.Concat(element.DescendantNodes().OfType<XmlTextSyntax>().SelectMany(text=>text.TextTokens).Select(token=>token.ValueText)));
                Check.Require(summaries.Any(text=>!string.IsNullOrWhiteSpace(text) && !text.Contains("operation on the supplied facts",StringComparison.Ordinal)),
                    $"Missing behavior summary: {file}.{method.Identifier}");
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
        cmd.CommandText="DELETE FROM issue"; cmd.ExecuteNonQuery();
        using(var removeGuard=db.CreateCommand())
        {
            removeGuard.Transaction=tx; removeGuard.CommandText="SAVEPOINT allowed_removal"; removeGuard.ExecuteNonQuery();
            Check.Require(await table.PublishAsync(tx,"p",null,[],default)==null,"Cleared PutBack still blocked removal");
            removeGuard.CommandText="SELECT count(*) FROM pair"; Check.Require((long)removeGuard.ExecuteScalar()! == 0,"Pair removal returned success but left its row");
            removeGuard.CommandText="ROLLBACK TO allowed_removal; RELEASE allowed_removal";removeGuard.ExecuteNonQuery();
        }
        tx.Commit();
        var job=new AcceptedJob("p","route","token",[new("second","b",null),new("first","a",null)]);
        await table.SaveJobAsync(db,job,default);
        await table.SettleJobAsync(db,job with { Launch=job.Launch.Reverse().ToArray() },default);
        var jobs=new List<AcceptedJob>(); await foreach(var j in table.JobsAsync(db,"p",default)) jobs.Add(j);
        Check.Require(jobs.Count==1 && jobs[0].Launch.SequenceEqual(job.Launch),"A reordered settlement retired the job or lost send order");
        await table.SettleJobAsync(db,job,default);
        await foreach(var j in table.JobsAsync(db,"p",default)) throw new InvalidOperationException("Exact job remained");
        using(var migrated=db.CreateCommand())
        {
            migrated.CommandText="""INSERT INTO accepted_job VALUES(1,'route','token',json_array(json_object('Path','café','Destination',NULL,'Id','one')))""";migrated.ExecuteNonQuery();
        }
        await table.SettleJobAsync(db,new("p","route","token",[new("one","café",null)]),default);
        await foreach(var remaining in table.JobsAsync(db,"p",default)) throw new InvalidOperationException("Migrated ordered launch retained after settlement of identical Unicode path");
        var selected=new RemovalSelection(new RemovalProof(new Entry("folder",true,0,DateTimeOffset.UnixEpoch,"12","","",null),"volume",null),null);
        await table.ArmAsync(db,"p",selected,null,default);
        var stage=new StageProof("stage","volume","13",0,new string('a',64));
        await table.DisarmAsync(db,"p",selected,stage,default);
        Check.Require(await table.ArmedAsync(db,"p",default)==(selected,(StageProof?)null),"Wrong stage cleared the armed victim");
        await table.DisarmAsync(db,"p",selected,null,default);
        Check.Require(await table.ArmedAsync(db,"p",default)==null,"Exact victim and stage did not settle");
        var position=new JournalPosition(ulong.MaxValue,7);
        using(var seed=db.CreateCommand())
        {
            seed.CommandText="""
                UPDATE pair SET def_json='{"Roots":{"Local":{"VolumeSerialNumber":42}}}' WHERE pair_id='p';
                INSERT INTO pair(pair_id,def_json) VALUES('late','{"Roots":{"Local":{"VolumeSerialNumber":42}}}'),('other','{"Roots":{"Local":{"VolumeSerialNumber":12}}}');
                """;seed.ExecuteNonQuery();
        }
        using(var reset=db.BeginTransaction())
        {
            await table.ResetJournalAsync(reset,"42",position,["p","other"],"journal reset",default);
            await table.ResetCursorAsync(reset,"p","opaque","cursor reset",default);
            reset.Commit();
        }
        Check.Require(await table.PositionAsync(db,"42",default)==position && await table.CursorAsync(db,"p",default)=="opaque" &&
            await table.ReadRepairAsync(db,"p",Side.Local,default)==(4L,"journal reset") && await table.ReadRepairAsync(db,"late",Side.Local,default)==(1L,"journal reset") &&
            await table.ReadRepairAsync(db,"other",Side.Local,default)==(0L,null) && await table.ReadRepairAsync(db,"p",Side.Dropbox,default)==(6L,"cursor reset"),"Reset did not use the durable volume membership");
        using(var reset=db.BeginTransaction()) { await table.ResetJournalAsync(reset,"42",new(8,9),[],"aborted",default); reset.Rollback(); }
        Check.Require(await table.PositionAsync(db,"42",default)==position && await table.ReadRepairAsync(db,"late",Side.Local,default)==(1L,"journal reset"),"Rolled-back reset changed cursor or debt");
    }
}
