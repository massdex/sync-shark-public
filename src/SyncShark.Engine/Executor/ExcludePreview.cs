namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Expected effects of applying the exclusion.</summary>
public sealed record ExcludePreview(Side Side, string Path, int CopyFiles, long CopyBytes, int RemoveFiles,
    long RemoveBytes, TextKey RecoveryPlace, bool BlockedByProtected);
