namespace Massdex.SyncShark.Engine;

/// <summary>Admits bytes about to be written under a directory against its volume's one shared free-space pool, the
/// pool downloads use (#36). A refusal throws <see cref="InsufficientSpaceException"/>; disposal retires the grant.</summary>
public interface IWriteSpace
{
    ValueTask<IAsyncDisposable> ReserveAsync(string directory, long bytes, CancellationToken ct);
}
