namespace Massdex.SyncShark.Engine;

/// <summary>Only proven bytes or a proven folder listing are offered to the recovery caller.</summary>
public abstract record Recovery
{
    public sealed record File(Entry Version, Stream Bytes) : Recovery;
    public sealed record Folder(Entry Root, IAsyncEnumerable<Listed> Contents) : Recovery;
}
