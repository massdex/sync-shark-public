namespace Massdex.SyncShark;

public sealed record SyncIgnoreRule(SyncIgnoreKind Kind, string Value)
{
    public string Description => Kind switch
    {
        SyncIgnoreKind.FileExtension => $"Files ending in {Value}",
        SyncIgnoreKind.FileName => $"Files named {Value}",
        SyncIgnoreKind.FolderName => $"Folders named {Value} and everything inside",
        _ => throw new InvalidDataException("Unknown ignore rule.")
    };
}
