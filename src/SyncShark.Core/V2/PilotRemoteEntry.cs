namespace Massdex.SyncShark;

/// <summary>
/// One listed provider item. The provider-issued file/folder id remains the only key a provider API call
/// uses; the optional aliases are additional provider-issued keys read back from the same metadata (for
/// Dropbox, the shared folder mounted at this exact folder) and never replace or reinterpret that native id.
/// </summary>
public sealed record PilotRemoteEntry(
    string RelativePath,
    PilotEntryKind Kind,
    string? Id,
    string? Revision,
    long Size,
    string? ContentHash,
    DateTimeOffset? ModifiedUtc = null,
    string? TransferBlockedReason = null,
    IReadOnlyList<SyncCloudObjectKey>? ObjectAliases = null)
{
    /// <summary>Dropbox client_modified (the uploader's file mtime), when listed. Inventory evidence only.</summary>
    public DateTimeOffset? ClientModifiedUtc { get; init; }

    /// <summary>Provider metadata explicitly says direct download is unavailable; export is required.</summary>
    public bool RequiresExport { get; init; }

    /// <summary>Dropbox export_info.export_as: the provider's default export format, when listed.</summary>
    public string? ExportAs { get; init; }

    /// <summary>Dropbox export_info.export_options: exactly the formats the provider supports for this item.</summary>
    public IReadOnlyList<string>? ExportOptions { get; init; }
}
