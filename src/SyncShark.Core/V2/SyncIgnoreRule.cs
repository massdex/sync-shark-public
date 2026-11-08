namespace Massdex.SyncShark;

public sealed record SyncIgnoreRule(SyncIgnoreKind Kind, string Value)
{
    public string Description => Kind switch
    {
        SyncIgnoreKind.FileExtension => $"Files ending in {Value}",
        SyncIgnoreKind.FileName => $"Files named {Value}",
        SyncIgnoreKind.FolderName => $"Folders named {Value} and everything inside",
        SyncIgnoreKind.FileNamePrefix => $"Files whose names start with {Value}",
        _ => throw new InvalidDataException("Unknown ignore rule.")
    };
}
