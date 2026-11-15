using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine;

/// <summary>Every tunable. Values come only from engine-settings.json (embedded defaults) overlaid by the user's
/// settings file; nothing has a default in code.</summary>
/// <param name="StoreBatchRows">Rows per scan save transaction, which is also the scan queue's bounded in-flight
/// window. It is the store's write batch size: a listing-sized queue is committed in chunks, never held whole.</param>
public sealed record EngineSettings(string ConflictLabelFormat, string ConflictLabelTimeFormat,
    string ConflictLabelLocalName, string ConflictLabelDropboxName, string CaseRenameSuffix,
    RecycleFallback WhenRecycleBinUnavailable, int LogRetentionDays, FirstRunPolicy DefaultFirstRunPolicy,
    int InitialLimiterLevel, string DefaultIgnoreRules, int LogBufferCharacters, int StoreBatchRows)
{
    // The accepted atomic-publish policy doubles each successive wait.
    private const int AtomicReplaceDelayMultiplier = 2;

    /// <summary>The label rule built from the configured conflict-label formats.</summary>
    public LabelRule ConflictLabel => new(ConflictLabelFormat, ConflictLabelTimeFormat, TimeSpan.Zero,
        ConflictLabelLocalName, ConflictLabelDropboxName);

    /// <summary>Storage page bound adopted from Core recovery paging; the default is data-owned.</summary>
    public int StoragePageSize { get; init; }

    private readonly FailureRetryOwner.BackoffSettings? _backoff;

    /// <summary>Supplied by the settings parser from the four backoff settings.</summary>
    public FailureRetryOwner.BackoffSettings Backoff { get => _backoff!; init => _backoff = value; }

    /// <summary>The inherited legacy batch poll interval in seconds; one of the two inputs to
    /// <see cref="FolderDeletePollInterval"/>.</summary>
    public double BatchPollIntervalSeconds { get; init; }

    /// <summary>The inherited legacy minimum interval in seconds between whole-folder delete job checks; the other
    /// input to <see cref="FolderDeletePollInterval"/>.</summary>
    public double FolderDeleteMinimumPollSeconds { get; init; }

    /// <summary>The single cadence for checking a submitted folder-delete job: the larger of the two inherited polling
    /// inputs. Derived here once; never stored or recomputed by a caller.</summary>
    public TimeSpan FolderDeletePollInterval =>
        TimeSpan.FromSeconds(Math.Max(BatchPollIntervalSeconds, FolderDeleteMinimumPollSeconds));

    /// <summary>Attempts at publishing one completed file by atomic replace (#135).</summary>
    public int AtomicReplaceAttempts { get; init; }

    /// <summary>The wait before the second publish attempt, in milliseconds; each later wait doubles. The short
    /// exponential backoff is the industry-standard wait for an antivirus or indexer hold, which clears in well
    /// under a second; the last failure still reaches the caller's existing failure and notice path.</summary>
    public int AtomicReplaceInitialDelayMilliseconds { get; init; }

    /// <summary>Read chunk of the read-only log view, in bytes. Independent of the writer's
    /// <see cref="LogBufferCharacters"/>: the view streams records and never buffers a whole file.</summary>
    public int LogReadBufferBytes { get; init; }

    /// <summary>One kernel USN journal page read per call. The kernel continues from the USN prefix it returns,
    /// so this bounds one read, not the replay window.</summary>
    public int JournalPageBufferBytes { get; init; }

    /// <summary>The publish retry schedule derived from the two settings: one wait between each pair of attempts,
    /// doubling from the configured first delay. Derived here once; never stored or recomputed by a caller.</summary>
    public AtomicReplacePolicy AtomicReplace
    {
        get
        {
            var delays = new TimeSpan[Math.Max(0, AtomicReplaceAttempts - 1)];
            var milliseconds = (long)AtomicReplaceInitialDelayMilliseconds;
            for (var index = 0; index < delays.Length; index++)
            {
                delays[index] = TimeSpan.FromMilliseconds(milliseconds);
                milliseconds *= AtomicReplaceDelayMultiplier;
            }
            return new AtomicReplacePolicy(delays);
        }
    }
}
