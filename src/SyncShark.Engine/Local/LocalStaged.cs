namespace Massdex.SyncShark.Engine.Local;

/// <summary>Owns the stage handle until publication, exact note ownership or disposal.</summary>
public sealed class LocalStaged : IStaged
{
    public string Path { get; }
    public bool ConsumesSourceOnPublish => false;
    public StageProof Proof { get; }
    internal FileStream Held { get; }
    internal bool Published { get; set; }
    private readonly Func<StageProof,Task<bool>> owned;
    private bool disposed;

    internal LocalStaged(string path, StageProof proof, FileStream held, Func<StageProof,Task<bool>> ownedByNote)
    {
        Path = path; Proof = proof; Held = held; owned = ownedByNote;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            // Failure to read note ownership preserves the bytes. Never delete by reopening the name.
            if (!Published && !await owned(Proof)) LocalSide.Discard(Held.SafeFileHandle);
        }
        finally { await Held.DisposeAsync(); }
    }
}
