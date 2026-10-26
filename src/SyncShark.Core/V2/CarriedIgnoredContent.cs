namespace Massdex.SyncShark.V2;

/// <summary>Ignored content carried by a parent delete; counts are disclosure, never standalone actions.</summary>
public sealed record CarriedIgnoredContent(int Files, int Folders, long Bytes)
{
    /// <summary>At most twenty carried paths for notices; not identity-based review constraints.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];
    public bool TrivialFileNamesOnly { get; init; }
}
