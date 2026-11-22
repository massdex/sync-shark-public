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
            var abandoned=path+".previous.new";await File.WriteAllTextAsync(abandoned,"abandoned stage");
            var pointer=Path.Combine(root,"product-root.txt");await File.WriteAllTextAsync(pointer,root);bool connectionCheck=false;
            using(var locked=new FileStream(pointer,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var warning=await move.MoveWithOutcomeAsync(root,root,(_,_,ids)=>{connectionCheck=ids.Count==0;return null;},default);
                Check.Require(connectionCheck && !string.IsNullOrEmpty(warning) && File.Exists(path) && File.Exists(pointer),"Post-publication cleanup failure hid the usable store");
            }
            var retry=await move.MoveWithOutcomeAsync(root,root,(_,_,_)=>null,default);
            Check.Require(retry==null && !File.Exists(pointer) && await File.ReadAllTextAsync(abandoned)=="abandoned stage","Next start consumed another attempt's stage or kept the pointer");
            await File.WriteAllTextAsync(pointer,Path.Combine(root,"missing"));
            try {await move.MoveAsync(root,root,(_,_,_)=>null,default);throw new Exception("Dangling pointer accepted");}catch(InvalidDataException){}
            Check.Require(File.Exists(pointer) && original.SequenceEqual(await File.ReadAllBytesAsync(path)),"Refusal modified the source or pointer");
            await Scenarios.StoreMoveChecks.CreateFixtureAsync();
        }
        finally {Directory.Delete(root,true);}
    }
}
