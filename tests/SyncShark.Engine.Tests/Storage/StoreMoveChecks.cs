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
        await DeletedNamesChecks.VerifyAcquisitionAsync();
        await ScanTableChecks.VerifyNoticeOnlyAsync();
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var move = new StoreMove();
            var path = Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.DatabaseFile);
            await move.MoveAsync(root, root, (_, _, _) => throw new InvalidOperationException(), default);
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
                await move.MoveAsync(root, root, (_, _, _) => null, default);
                throw new Exception("Dangling pointer accepted");
            }
            catch (InvalidDataException)
            {
            }

            Check.Require(File.Exists(pointer) && original.SequenceEqual(await File.ReadAllBytesAsync(path)), "Refusal modified the source or pointer");
            await Scenarios.StoreMoveChecks.CreateFixtureAsync();
            await RecoveredBackupAsync(Path.Combine(root,"recovery"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
    private static async Task RecoveredBackupAsync(string root)
    {
        // Borrow the actual release fixture DDL, not a lookalike schema built just to pass migration.
        var code=await File.ReadAllTextAsync(Path.Combine(CheckArguments.Parse(Environment.GetCommandLineArgs()[1..]).Root,"tests","SyncShark.Engine.Tests","Scenarios","StoreMoveChecks.cs"));
        var field=CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(n=>n.Identifier.ValueText=="ReleaseSchema");
        var ddl=((LiteralExpressionSyntax)field.Initializer!.Value).Token.ValueText;
        var folder=Directory.CreateDirectory(Path.Combine(root,ResourceNames.EngineFolder)).FullName;
        var backup=Path.Combine(folder,"state.v16.db");var target=Path.Combine(folder,ResourceNames.DatabaseFile);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup,Pooling=false}.ToString()))
        {
            await db.OpenAsync();db.CreateFunction<string,string>("pathkey",PathNames.Key,true);db.CreateFunction<string,int>("plandepth",PathNames.Depth,true);db.CreateFunction<string,int>("localaccessretrycause",_=>0,true);
            using var cmd=db.CreateCommand();cmd.CommandText=ddl;await cmd.ExecuteNonQueryAsync();
            cmd.CommandText="""
                INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p','{"ConnectionId":"c"}');
                INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed) VALUES
                  (8,1,4,'SkipLink','a','{"Items":[]}',1,0),(9,1,4,'SkipLink','b','{"Items":[]}',2,0);
                INSERT INTO issue_member(member_id,issue_id,pair_id,side,path_key,subject_json,skip_cause) VALUES
                  (1,8,1,0,'a','{"Version":1,"Value":{"Items":[]}}','SkipLink'),
                  (2,9,1,0,'b','{"Version":1,"Value":{"Items":[]}}','SkipLink'),
                  (3,9,1,1,'b','{"Version":1,"Value":{"Items":[]}}','SkipSpecial');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var pointer=Path.Combine(root,"product-root.txt");await File.WriteAllTextAsync(pointer,root);
        var move=new StoreMove();var called=false;
        try{await move.MoveAsync(root,root,(_,_,_)=>{called=true;throw new IOException("refused");},default);throw new InvalidDataException("Publication continued after connection refusal");}catch(IOException error) when(error.Message=="refused"){}
        Check.Require(called && File.Exists(backup) && !File.Exists(target) && File.Exists(pointer),"Pre-publication refusal consumed the backup or published a stage");
        await move.MoveAsync(root,root,(_,_,ids)=>{Check.Require(ids.SequenceEqual(new[]{"c"}),"Required connection lost");return null;},default);
        using var opened=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=target,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());await opened.OpenAsync();
        using var read=opened.CreateCommand();read.CommandText="SELECT count(*) FROM issue_member m JOIN issue i USING(issue_id) WHERE (m.member_id IN(1,2) AND i.cause='InfoLinks') OR (m.member_id=3 AND i.issue_id=9)";
        Check.Require(Convert.ToInt32(await read.ExecuteScalarAsync())==3 && File.Exists(backup) && !File.Exists(pointer),"Restart lost a retained link or moved an unrelated member");
    }

}
