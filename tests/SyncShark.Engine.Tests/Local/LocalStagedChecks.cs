using Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

public static class LocalStagedChecks
{
    public static async Task RunAsync()
    {
        var target = Path.Combine("folder", "target");
        var name = StageNames.For(target);
        Check.Require(Path.GetDirectoryName(name) == Path.GetDirectoryName(target) && StageNames.IsStage(Path.GetFileName(name)) &&
            !StageNames.IsStage(Path.GetFileName(target)) && !StageNames.IsStage(Path.GetFileName(name) + ".bak"), "stage grammar");
        using var hash = new ContentHash();
        var proof = new StageProof(name, "1", "2", 0, hash.Finish());
        var held = new MemoryStream();
        StageProof? asked = null;
        var stage = new LocalStaged(target, proof, held, p => { asked = p; return p == proof; });
        Check.Require(stage.Path == target && stage.Proof == proof && !stage.ConsumesSourceOnPublish, "stage ownership");
        await stage.DisposeAsync();
        Check.Require(asked == proof && !held.CanRead, "exact armed stage checked before closing");
    }
}
