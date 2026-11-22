using Massdex.SyncShark.Engine.Storage;
namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks isolation and ordered listing writes on the attached scan database.</summary>
public static class ScanTableChecks
{
    /// <summary>One acquisition's clear must not remove another's completed listing.</summary>
    public static async Task RunAsync()
    {
        using var db=await DeletedNamesChecks.OpenAsync();var scans=new ScanTable();var keep=Guid.NewGuid();var clear=Guid.NewGuid();
        static async IAsyncEnumerable<Listed> List(string name){await Task.CompletedTask;yield return new(name,Scenarios.ItemChecks.File(Side.Local,name,1,name));}
        await scans.AddScanAsync(db,"p",keep,Side.Local,List("kept"),null,1,default);
        await scans.AddScanAsync(db,"p",clear,Side.Local,List("removed"),null,1,default);
        await scans.ClearScanAsync(db,"p",clear,default);var names=new List<string?>();
        await foreach(var row in scans.CompareAsync(db,"p",keep,null,(_,_) => (true,false),1,default)) names.Add(row.Row.Local?.Path);
        Check.Require(names.SequenceEqual(new[]{"kept"}),"Clear removed another acquisition or compare lost a row");
    }
}
