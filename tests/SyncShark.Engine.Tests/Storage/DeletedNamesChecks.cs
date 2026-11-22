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
        using var db=await OpenAsync();var table=new ScanTable();var names=table.History;var scan=Guid.NewGuid();
        static async IAsyncEnumerable<Listed> History(){await Task.CompletedTask;yield return new("old",null);}
        await table.AddScanAsync(db,"p",scan,Side.Dropbox,History(),()=>"cursor",1,default);
        var key=PathNames.Key("old");
        Check.Require(await names.ContainsAsync(db,"p",scan,key,default) && !await names.ContainsAsync(db,"p",null,key,default),"Unpublished deletion evidence escaped acquisition");
        Check.Require(!await names.ContainsAsync(db,"p",Guid.NewGuid(),key,default),"History was visible to a different acquisition");
        Check.Require(await names.ContainsAsync(db,"p",scan,key+"/child",true,default) && !await names.ContainsAsync(db,"p",scan,key+"/child",false,default),"Only folders inherit an ancestor tombstone");
        try {await names.PublishAsync(db,"p",scan,"different",default);throw new InvalidDataException("Wrong terminal cursor published");}catch(InvalidOperationException){}
        await names.PublishAsync(db,"p",scan,default);await table.ClearScanAsync(db,"p",scan,default);
        Check.Require(await names.ContainsAsync(db,"p",null,key,default) && await names.ReadStateAsync(db,"p",default)==("cursor",false),"Acquisition names or cursor not published");
        using var spent=db.CreateCommand();spent.CommandText="INSERT INTO discovery_deleted_delta VALUES(1,$k,1)";spent.Parameters.AddWithValue("$k",key);await spent.ExecuteNonQueryAsync();
        Check.Require(!await names.ContainsAsync(db,"p",null,key,default),"Spent name revived from a compacted block");
        var failed=Guid.NewGuid();static async IAsyncEnumerable<Listed> Incomplete(){await Task.CompletedTask;yield return new("partial",null);throw new IOException("Interrupted listing");}
        var cursorRead=false;try{await table.AddScanAsync(db,"p",failed,Side.Dropbox,Incomplete(),()=>{cursorRead=true;return "partial";},1,default);throw new InvalidDataException("Listing failure was ignored");}catch(IOException error) when(error.Message=="Interrupted listing"){}
        try {await names.PublishAsync(db,"p",failed,default);throw new InvalidDataException("Partial history published");}catch(InvalidOperationException){}
        Check.Require(!cursorRead && await names.ReadStateAsync(db,"p",default)==("cursor",false),"Interrupted listing advanced the cursor");
    }
}
