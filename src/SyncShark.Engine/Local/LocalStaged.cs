namespace Massdex.SyncShark.Engine.Local;

/// <summary>A local stage file</summary>
public sealed class LocalStaged : IStaged
{
    public string Path => throw new NotImplementedException();
    public bool ConsumesSourceOnPublish => throw new NotImplementedException();
    /// <summary>Dispose operation on the supplied facts.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}
