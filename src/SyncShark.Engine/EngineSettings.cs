
namespace Massdex.SyncShark.Engine;

/// <summary>Every tunable. Values come only from engine-settings.json (embedded); nothing has a default in code.</summary>
/// <param name="StoreBatchRows">Rows per scan save transaction, which is also the scan queue's bounded in-flight
/// window. It is the store's write batch size: a listing-sized queue is committed in chunks, never held whole.</param>
public sealed record EngineSettings(string ConflictLabelFormat, string ConflictLabelTimeFormat,
    string ConflictLabelLocalName, string ConflictLabelDropboxName, string CaseRenameSuffix,
    int LogRetentionDays, FirstRunPolicy DefaultFirstRunPolicy,
    int InitialLimiterLevel, string DefaultIgnoreRules, int LogBufferCharacters, int StoreBatchRows)
{
    // Stream.CopyToAsync(Stream, int, CancellationToken): Microsoft Learn documents the default buffer as 81,920 bytes.
    internal const int StreamCopyBufferBytes = 81_920;
    // The owner's run-output rule: a run with nothing to report says it is still working every 30 seconds (0.3.95 Monitor.QuietSeconds).
    internal const int QuietSeconds = 30;

    /// <summary>The PC name observed at startup, for local conflict labels only.</summary>
    public string ComputerName { get; init; } = string.Empty;
    /// <summary>Bounds the amount of state read per page.</summary>
    public int StoragePageSize { get; init; }
    /// <summary>Retry delays when the transport proves that no effect occurred.</summary>
    public BackoffSettings Backoff { get; init; } = null!;
    /// <summary>Retry timing and jitter from the settings resource.</summary>
    public sealed record BackoffSettings(double InitialBackoffSeconds, double BackoffMultiplier,
        double BackoffJitter, double MaximumBackoffSeconds)
    {
        /// <summary>The adopted retry schedule after a failure that supplied no positive provider wait.</summary>
        public TimeSpan RetryDelay(int failures) => RetryDelay(failures, Random.Shared.NextDouble);

        /// <summary>The adopted schedule with an explicit sample in [0,1]; the first delay is exact, later nominal delays
        /// cap before jitter and then take a symmetric offset (96-144 s at the 120 s cap with 0.2 jitter).</summary>
        public TimeSpan RetryDelay(int failures, Func<double> sample)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(failures);
            ArgumentNullException.ThrowIfNull(sample);
            if (failures <= 0) return TimeSpan.FromSeconds(InitialBackoffSeconds);
            var nominal = Math.Min(InitialBackoffSeconds * Math.Pow(BackoffMultiplier, failures),
                MaximumBackoffSeconds);
            var offset = (2 * sample() - 1) * BackoffJitter * nominal;
            return TimeSpan.FromSeconds(nominal + offset);
        }

        // .NET 8 Task.Delay uses Timer.MaxSupportedTimeout (0xfffffffe ms); uint.MaxValue is the infinite sentinel.
        private const uint MaximumTimerMilliseconds = uint.MaxValue - 1;
        private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(MaximumTimerMilliseconds);

        /// <summary>Wait a positive provider-requested time on the supplied clock in supported Task.Delay chunks, never shortening it.</summary>
        public async Task WaitAsync(TimeSpan delay, TimeProvider clock, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(clock);
            // A zero or missing time is no time given: the attempt owner waits its adopted retry schedule instead.
            if (delay <= TimeSpan.Zero) return;
            while (delay > MaximumTimerDelay)
            {
                await Task.Delay(MaximumTimerDelay, clock, ct).ConfigureAwait(false);
                delay -= MaximumTimerDelay;
            }
            await Task.Delay(delay, clock, ct).ConfigureAwait(false);
        }
    }
    /// <summary>Chunk size for log readers.</summary>
    public int LogReadBufferBytes { get; init; }
    /// <summary>Byte capacity for one journal read.</summary>
    public int JournalPageBufferBytes { get; init; }
    /// <summary>Time allowed to open a Dropbox connection, not a read or transfer limit. Source: dropbox-sdk-java HttpRequestor.DEFAULT_CONNECT_TIMEOUT_MILLIS, 20 s, adopted by the owner (08a9d06b2 docs/known-facts.md D14).</summary>
    public int DropboxConnectAllowanceSeconds { get; init; }
    /// <summary>Time allowed for the incumbent host probe and a client command to open the local pipe, not a read or transfer limit. Source: the original local connection window, retained by e563a0e39.</summary>
    public int HostPipeConnectTimeoutMilliseconds { get; init; }
    /// <summary>How long before expiry an access token is refreshed. Source: the documented policy of both official Dropbox SDKs, 5 minutes (https://github.com/dropbox/dropbox-sdk-java/blob/03eb97984df6/core/src/main/java/com/dropbox/core/oauth/DbxCredential.java#L42-L45; https://github.com/dropbox/dropbox-sdk-python/blob/a8abfc98d76a/dropbox/dropbox_client.py#L52); an SDK policy, not a service contract or a runtime measurement.</summary>
    public int TokenRefreshMarginMinutes { get; init; }
    /// <summary>Age by creation time after which items in a removable drive's root Recycle Bin folder are deleted at host start. Source: the owner-approved trim rule first implemented by 1a3ae9c9d (RecycleBin.TrimAsync); the approval is recorded in the owner's decisions ledger, outside this repository.</summary>
    public int RecycleBinTrimDays { get; init; }
}
