namespace Massdex.SyncShark.Engine.Local;

/// <summary>RecycleBin operations for the local module.</summary>
public sealed class RecycleBin
{
    /// <summary>Remove operation on the supplied facts.</summary>
    public Task<RemovalResult> RemoveAsync(string root, Entry expected, RemovalSelection? selection, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Trim operation on the supplied facts.</summary>
    public Task TrimAsync(string root, DateTimeOffset now, CancellationToken ct) => throw new NotImplementedException();
}
