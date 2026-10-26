namespace Massdex.SyncShark.V2;

/// <summary>One verified download stage in the destination directory, before it is published.</summary>
public sealed record SyncDownloadStage(string Path, string ObjectId, string RecoveryPath, bool Verified = true);
