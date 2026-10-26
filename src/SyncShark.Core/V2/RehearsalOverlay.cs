namespace Massdex.SyncShark.V2;

/// <summary>Changed metadata owned by one isolated rehearsal session.</summary>
internal abstract class RehearsalOverlay : IDisposable
{
    internal abstract long Version { get; }
    internal abstract bool Try(string root, string path, out ItemState? state);
    internal abstract void Save(string root, string path, ItemState? state, ItemState? victim = null);
    internal abstract IEnumerable<ItemState> Items(string root, string? subtree = null);
    internal abstract IEnumerable<Change> Since(string root, long after, long through);
    public abstract void Dispose();
}
