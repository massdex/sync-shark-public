namespace Massdex.SyncShark.Engine;

/// <summary>The persisted binding of one source's cursor: identical at capture and continuation. It says nothing
/// about complete-inventory coverage.</summary>
public sealed record SourceProvenance(string AccountId, string NamespaceId, string RootIdentity,
    string ConfiguredRoot, bool Recursive, bool IncludeDeleted, bool IncludeMountedFolders,
    bool IncludeNonDownloadableFiles, long PairGeneration, long SourceEpoch);
