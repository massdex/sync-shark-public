namespace Massdex.SyncShark.Engine.Local;

/// <summary>A local stage file</summary>
public sealed class LocalStaged : IStaged
{
    public string Path => throw new NotImplementedException();
    public bool ConsumesSourceOnPublish => throw new NotImplementedException();
    /// <summary>Dispose operation on the supplied facts.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();

    /// <summary>Exact held version; replay uses the stamp on this stage, not a newly observed source Entry.</summary>
    public StageProof Proof => throw new NotImplementedException();
    public LocalStaged(string path, StageProof proof, Stream held, Func<StageProof,bool> ownedByNote) => throw new NotImplementedException();

}
