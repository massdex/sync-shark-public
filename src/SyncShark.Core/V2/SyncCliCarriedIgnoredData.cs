namespace Massdex.SyncShark;

/// <summary>Ignored descendants removed along with a planned (or conditionally held) folder delete.</summary>
public sealed record SyncCliCarriedIgnoredData(string Path, string Side, int Files, int Folders, long Bytes)
{
    public string? ReviewedSignature { get; init; }
}
