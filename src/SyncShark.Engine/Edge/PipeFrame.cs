namespace Massdex.SyncShark.Engine.Edge;

/// <summary>PipeFrame operations for the edge module.</summary>
public static class PipeFrame
{
    /// <summary>Write operation on the supplied facts.</summary>
    public static Task WriteAsync(Stream pipe, ReadOnlyMemory<byte> message, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read operation on the supplied facts.</summary>
    public static Task<byte[]> ReadAsync(Stream pipe, CancellationToken ct) => throw new NotImplementedException();
}
