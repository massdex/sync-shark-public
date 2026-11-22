using Massdex.SyncShark.Engine.Storage;
namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Exercises closed-image publication on synthetic stores.</summary>
public static class StoreMoveChecks
{
    /// <summary>Startup must not create a store; explicit creation publishes once and preserves an existing image.</summary>
    public static async Task RunAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var move=new StoreMove();var path=Path.Combine(root,ResourceNames.EngineFolder,ResourceNames.DatabaseFile);
            await move.MoveAsync(root,root,(_,_,_)=>throw new InvalidOperationException(),default);
            Check.Require(!File.Exists(path),"Startup created a fresh store");await move.CreateAsync(root,default);
            var original=await File.ReadAllBytesAsync(path);try{await move.CreateAsync(root,default);}catch(IOException){}
            Check.Require(original.Length>0 && original.SequenceEqual(await File.ReadAllBytesAsync(path)),"Second create replaced the published image");
            await Scenarios.StoreMoveChecks.CreateFixtureAsync();
        }
        finally {Directory.Delete(root,true);}
    }
}
