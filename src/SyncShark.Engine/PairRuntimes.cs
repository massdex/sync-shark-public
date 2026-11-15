namespace Massdex.SyncShark.Engine;

/// <summary>The process-wide ReplaceTurn and folder-job claim of each pair.</summary>
public static class PairRuntimes
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReplaceTurn> Runtimes = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> FolderJobs = new();

    /// <summary>Claims the one active observation owner of a durable folder submission.</summary>
    public static bool TryClaimFolderJob(string submissionId) => FolderJobs.TryAdd(submissionId, 0);

    public static void ReleaseFolderJob(string submissionId) => FolderJobs.TryRemove(submissionId, out _);

    /// <summary>Returns the pair's ReplaceTurn, creating it on first use.</summary>
    public static ReplaceTurn For(string pairId) => Runtimes.GetOrAdd(pairId, _ => new ReplaceTurn());
}
