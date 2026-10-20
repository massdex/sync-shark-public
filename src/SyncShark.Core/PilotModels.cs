namespace Massdex.SyncShark;

public sealed record DropboxRevisionEntry(string RelativePath, string FileId, string Revision, long Size,
    string? ContentHash, DateTimeOffset? ModifiedUtc);

public sealed record DropboxDeletedEntry(string RelativePath, string Name);

public sealed record DropboxDeletedPage(
    IReadOnlyList<DropboxDeletedEntry> Entries,
    string? ContinueCursor);

public enum PilotEntryKind
{
    File,
    Directory
}

public enum PilotDecisionKind
{
    Uploaded,
    Identical,
    Conflict,
    Blocked,
    Stopped,
    OnlineOnly
}

public sealed record PilotLocalEntry(
    string RelativePath,
    PilotEntryKind Kind,
    string FullPath,
    long Size,
    DateTime LastWriteUtc,
    string? ContentHash,
    uint? VolumeSerialNumber = null,
    ulong? FileId = null,
    SyncCloudFileAvailability? CloudAvailability = null,
    string? CloudRevision = null,
    string? CloudFileId = null,
    bool ContentUnreadable = false,
    bool Link = false);

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

/// <summary>
/// One provider folder identity read back from an exact access route. The optional aliases are the extra
/// provider-issued keys for that same folder (a Dropbox shared-folder mount), kept as evidence only.
/// </summary>
public sealed record PilotRemoteRootBinding(string Path, string FolderId,
    IReadOnlyList<SyncCloudObjectKey>? ObjectAliases = null);

public sealed record PilotUploadResult(string FileId, string Revision, long Size, string ContentHash)
{
    public DateTimeOffset? ClientModifiedUtc { get; init; }
}

public sealed record PilotDecision(string RelativePath, PilotDecisionKind Kind, string Reason);

public sealed record PilotRunResult(IReadOnlyList<PilotDecision> Decisions)
{
    public int Uploaded => Decisions.Count(item => item.Kind == PilotDecisionKind.Uploaded);
    public int Identical => Decisions.Count(item => item.Kind == PilotDecisionKind.Identical);
    public int Conflicts => Decisions.Count(item => item.Kind == PilotDecisionKind.Conflict);
    public int Blocked => Decisions.Count(item => item.Kind == PilotDecisionKind.Blocked);
    public bool Stopped => Decisions.Any(item => item.Kind == PilotDecisionKind.Stopped);
}

public interface IPilotRemoteStore
{
    Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(CancellationToken cancellationToken = default);
    Task<PilotUploadResult> UploadCreateOnlyAsync(string relativePath, Stream source, string expectedContentHash, CancellationToken cancellationToken = default);
}

public sealed class PilotLimitException(string message) : IOException(message);

public sealed class DropboxEffectUncertainException(string message, Exception? innerException = null) : IOException(message, innerException);
