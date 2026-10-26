namespace Massdex.SyncShark;

public sealed record SyncDownloadResult(string FileId, string Revision, long Size, string ContentHash);
