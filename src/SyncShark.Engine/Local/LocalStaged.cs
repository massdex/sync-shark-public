namespace Massdex.SyncShark.Engine.Local;

/// <summary>Owns the stage handle until publication, exact note ownership or disposal.</summary>
public sealed class LocalStaged : IStaged
{
    public string Path { get; }
    public bool ConsumesSourceOnPublish => false;
    public StageProof Proof { get; }
    internal Stream Held { get; }
    internal bool AdapterOwned { get; private set; }
    internal bool Published { get; set; }
    private readonly Func<StageProof,Task<bool>> owned;
    private bool disposed;

    public LocalStaged(string path, StageProof proof, Stream held, Func<StageProof,bool> ownedByNote)
        : this(path, proof, held, p => Task.FromResult(ownedByNote(p))) { AdapterOwned = false; }

    internal LocalStaged(string path, StageProof proof, Stream held, Func<StageProof,Task<bool>> ownedByNote)
    {
        AdapterOwned = true; Path = path; Proof = proof; Held = held; owned = ownedByNote;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            // Failure to read note ownership preserves the bytes. Never delete by reopening the name.
            if (!Published && !await owned(Proof) && AdapterOwned && Held is FileStream file)
                LocalSide.Discard(file.SafeFileHandle);
        }
        finally { await Held.DisposeAsync(); }
    }
}
