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
}
