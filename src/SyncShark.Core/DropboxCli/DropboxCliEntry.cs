namespace Massdex.SyncShark;

/// <summary>One Dropbox metadata object mapped to the CLI entry shape.</summary>
public sealed record DropboxCliEntry(string Tag, string Name, string PathDisplay, string PathLower, string Id,
    string? Rev, long? Size, string? ContentHash, DateTimeOffset? ClientModified, DateTimeOffset? ServerModified,
    bool? IsDownloadable) : IDropboxCliData;
