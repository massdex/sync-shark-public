using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Massdex.SyncShark.Engine.Storage;
namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Exercises acquisition-bound deletion evidence against SQLite.</summary>
public static class DeletedNamesChecks
{
    internal static async Task<SqliteConnection> OpenAsync()
    {
        var db=new SqliteConnection("Data Source=:memory:"); await db.OpenAsync();
        db.CreateFunction<string,string>("pathkey", PathNames.Key, isDeterministic:true);
        using var setup=db.CreateCommand(); setup.CommandText=Schema.Create+" ATTACH ':memory:' AS scan_state; "+Schema.CreateScan+" INSERT INTO pair(pair_id,def_json) VALUES('p','{}');";
        await setup.ExecuteNonQueryAsync(); return db;
    }

    /// <summary>A history cursor and names must become durable together and never revive spent evidence.</summary>
    public static async Task RunAsync()
    {
        var root=CheckArguments.Parse(Environment.GetCommandLineArgs()[1..]).Root;
        foreach(var file in new[]{"ScanTable","DeletedNames"})
        {
            var source=await File.ReadAllTextAsync(Path.Combine(root,"src","SyncShark.Engine","Storage",file+".cs"));
            var tree=CSharpSyntaxTree.ParseText(source).GetRoot();
            foreach(var method in tree.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m=>m.Modifiers.Any(t=>t.Text=="public")))
            {
                var doc=method.GetLeadingTrivia().ToFullString();
                Check.Require(doc.Contains("<summary>",StringComparison.Ordinal) && !doc.Contains("operation on the supplied facts",StringComparison.OrdinalIgnoreCase),"Missing operation-specific summary: "+method.Identifier.Text);
            }
        }
        using var db=await OpenAsync();var names=new DeletedNames();var scan=Guid.NewGuid();var table=new ScanTable();
        static async IAsyncEnumerable<Listed> History(){await Task.CompletedTask;yield return new("old",null);}
        await table.AddScanAsync(db,"p",scan,Side.Dropbox,History(),()=>"cursor",1,default);
        var key=PathNames.Key("old");
        Check.Require(await names.ContainsAsync(db,"p",scan,key,default) && !await names.ContainsAsync(db,"p",null,key,default),"Unpublished deletion evidence escaped acquisition");
        await names.PublishAsync(db,"p",scan,"cursor",default);await table.ClearScanAsync(db,"p",scan,default);
        Check.Require(await names.ContainsAsync(db,"p",null,key,default) && await names.ReadStateAsync(db,"p",default)==("cursor",false),"Acquisition names or cursor not published");
    }
}
