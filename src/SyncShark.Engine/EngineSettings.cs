
namespace Massdex.SyncShark.Engine;

/// <summary>Every tunable. Values come only from engine-settings.json (embedded defaults) overlaid by the user's
/// settings file; nothing has a default in code.</summary>
/// <param name="StoreBatchRows">Rows per scan save transaction, which is also the scan queue's bounded in-flight
/// window. It is the store's write batch size: a listing-sized queue is committed in chunks, never held whole.</param>
public sealed record EngineSettings(string ConflictLabelFormat, string ConflictLabelTimeFormat,
    string ConflictLabelLocalName, string ConflictLabelDropboxName, string CaseRenameSuffix,
    int LogRetentionDays, FirstRunPolicy DefaultFirstRunPolicy,
    int InitialLimiterLevel, string DefaultIgnoreRules, int LogBufferCharacters, int StoreBatchRows)
{
    /// <summary>Bounds the amount of state read per page.</summary>
    public int StoragePageSize { get; init; }
    /// <summary>Retry delays when the transport proves that no effect occurred.</summary>
    public BackoffSettings Backoff { get; init; } = null!;
    /// <summary>Retry timing and jitter from the settings resource.</summary>
    public sealed record BackoffSettings(double InitialBackoffSeconds, double BackoffMultiplier,
        double BackoffJitter, double MaximumBackoffSeconds);
    /// <summary>Maximum attempts at a held publication.</summary>
    public int AtomicReplaceAttempts { get; init; }
    /// <summary>Initial wait after a temporarily refused publication.</summary>
    public int AtomicReplaceInitialDelayMilliseconds { get; init; }
    /// <summary>Chunk size for log readers.</summary>
    public int LogReadBufferBytes { get; init; }
    /// <summary>Byte capacity for one journal read.</summary>
    public int JournalPageBufferBytes { get; init; }
}
