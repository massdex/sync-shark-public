namespace Massdex.SyncShark.Engine;

/// <summary>Accounts for every legacy baseline, including discarded stale folder identities.</summary>
public sealed record BaselineImportCounts(long SourceRows, long ImportedRows, long DiscardedFolderRows);
