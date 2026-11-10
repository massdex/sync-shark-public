namespace Massdex.SyncShark.Engine;

/// <summary>What applying an exclusion would do (decision 58(h), section 7.7). BlockedByProtected: skipped,
/// other-excluded or nested-pair content prevents the removal.</summary>
public sealed record ExcludePreview(Side Side, string Path, int CopyFiles, long CopyBytes, int RemoveFiles,
    long RemoveBytes, TextKey RecoveryPlace, bool BlockedByProtected);
