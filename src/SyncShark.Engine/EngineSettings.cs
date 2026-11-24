
namespace Massdex.SyncShark.Engine;

/// <summary>Every tunable. Values come only from engine-settings.json (embedded); nothing has a default in code.</summary>
/// <param name="StoreBatchRows">Rows per scan save transaction, which is also the scan queue's bounded in-flight
/// window. It is the store's write batch size: a listing-sized queue is committed in chunks, never held whole.</param>
public sealed record EngineSettings(string ConflictLabelFormat, string ConflictLabelTimeFormat,
    string ConflictLabelLocalName, string ConflictLabelDropboxName, string CaseRenameSuffix,
    int LogRetentionDays, FirstRunPolicy DefaultFirstRunPolicy,
    int InitialLimiterLevel, string DefaultIgnoreRules, int LogBufferCharacters, int StoreBatchRows)
{
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
        /// <summary>The adopted retry schedule after a failure that supplied no positive provider wait (product-0425/0526).</summary>
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

        // Task.Delay on .NET 8 accepts at most 0xfffffffe milliseconds per timer.
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
    /// <summary>Byte ceiling for reading a stored connection list. Source: the released 0.3.95 append and dual-slot formats cap stored state at 1 MiB (08a9d06b2 AppendOnlyJsonStore.PhysicalMaximumBytes, DualSlotJsonStore.MaximumBytes), so legacy files must fit; for the canonical id list the value is inherited, not justified; measured by rehearsal step RS-M1.</summary>
    public int ConnectionListMaxBytes { get; init; }
    /// <summary>Records accepted from a legacy connection append log. Derivation: the released append format's physical record maximum, 100 (08a9d06b2 AppendOnlyJsonStore.PhysicalMaximumRecords), not its lower operational write limit.</summary>
    public int ConnectionListMaxAppendRecords { get; init; }
    /// <summary>Decoded bytes read from a Dropbox error body. Source: the released reader's 64 KiB ceiling (08a9d06b2 docs/known-facts.md D17), an application choice, not a provider maximum; inherited, not justified; measured by rehearsal step RS-M6.</summary>
    public int DropboxErrorBodyMaxBytes { get; init; }
    /// <summary>Time allowed to open a Dropbox connection. Source: dropbox-sdk-java HttpRequestor.DEFAULT_CONNECT_TIMEOUT_MILLIS, 20 s, adopted by the owner (08a9d06b2 docs/known-facts.md D14).</summary>
    public int DropboxConnectAllowanceSeconds { get; init; }
    /// <summary>Byte ceiling for one OAuth or account JSON reply. Inherited, not justified; measured by rehearsal step RS-M3.</summary>
    public int SignInMaxJsonBytes { get; init; }
    /// <summary>Byte ceiling for the sign-in loopback request through its blank line. Inherited, not justified; measured by rehearsal step RS-M4.</summary>
    public int SignInMaxCallbackBytes { get; init; }
    /// <summary>Byte ceiling for the sign-in loopback request line. Inherited, not justified; measured by rehearsal step RS-M5.</summary>
    public int SignInMaxCallbackLineBytes { get; init; }
    /// <summary>Read chunk for OAuth JSON replies; scratch space, not a reply limit. Inherited, not justified; measured by rehearsal step RS-M2.</summary>
    public int SignInReadChunkBytes { get; init; }
    /// <summary>How long before expiry an access token is refreshed. Source: the official Dropbox SDKs refresh 5 minutes early (dropbox-sdk-java DbxCredential EXPIRE_MARGIN; dropbox-sdk-python 300 s).</summary>
    public int TokenRefreshMarginMinutes { get; init; }
    /// <summary>Payload ceiling of one host pipe frame, excluding its kind and length header; both ends read it from this file. Derivation: the released host protocol's frame maximum, 1 MiB (08a9d06b2 AutoSyncHostProtocol.MaximumFrameBytes).</summary>
    public int PipeMaxPayloadBytes { get; init; }
    /// <summary>Native buffer per local directory enumerator. Source: the released enumerator's 64 KiB buffer (08a9d06b2 docs/known-facts.md L01); inherited, not justified; measured by rehearsal step RS-M7.</summary>
    public int DirectoryListingBufferBytes { get; init; }
    /// <summary>Age by creation time after which items in a removable drive's root Recycle Bin folder are deleted at host start. Source: owner decision (30 days).</summary>
    public int RecycleBinTrimDays { get; init; }
}
