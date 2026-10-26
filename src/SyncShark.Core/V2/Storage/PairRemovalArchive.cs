namespace Massdex.SyncShark.V2;

public sealed record PairRemovalArchive(string PairId, string ArchivePath, string ManifestPath,
    IReadOnlyDictionary<string, long> RowCounts, IReadOnlyList<string> OrphanedRoots, long UnfinishedEffects);
