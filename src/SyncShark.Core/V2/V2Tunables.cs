namespace Massdex.SyncShark.V2;

/// <summary>The v2 engine's tunable bounds that V2Settings supplies: one unit, one derivation each. The engine's
/// other settings (paths, retention, batching) stay on <see cref="V2Settings"/>.</summary>
public sealed record V2Tunables
{
    /// <summary>Paths per bounded exact-metadata page. The four concurrent metadata reads fill one page at a
    /// time, so a page's retained metadata stays bounded.</summary>
    public int MetadataPagePaths { get; init; } = 128;

    /// <summary>The first wait after a failed attempt. The existing failure policy doubles it each time, up to
    /// <see cref="RetryMaximumDelay"/>; the attempt counter saturates rather than overflowing.</summary>
    public TimeSpan RetryInitialDelay { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>The factor applied to each successive retry wait.</summary>
    public double RetryDelayMultiplier { get; init; } = 2;
    /// <summary>Doublings applied before the ceiling. Five doublings from ten seconds reach the five-minute
    /// ceiling, so a later attempt cannot grow past it.</summary>
    public int RetryMaximumDoublings { get; init; } = 5;
    /// <summary>The retry ceiling: one named limit shared by every retry tier.</summary>
    public TimeSpan RetryMaximumDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The largest USN journal window read in one replay step: 64 kernel journal pages.</summary>
    public long JournalReplayWindowBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>The smallest journal window: one kernel journal page, below which the window stops halving.</summary>
    public long JournalReplayMinimumWindowBytes { get; init; } = 1024L * 1024;

    /// <summary>The upper bound of <see cref="V2Settings.LocalReplayWindowsPerRun"/>: eight times the shipped
    /// eight, so a mistyped setting cannot make one inventory call read the whole retained history.</summary>
    public int MaximumLocalReplayWindowsPerRun { get; init; } = 64;

    /// <summary>The background gap-verification read stride: the same 64 KiB copy stride as the shared transfer
    /// path.</summary>
    public int LocalGapContentBufferBytes { get; init; } = 64 * 1024;
    /// <summary>Rows hashed per background gap work page; a page's proofs finish inside one store slice.</summary>
    public int LocalGapHashPageRows { get; init; } = 16;

    /// <summary>Rows per rehearsal hydration page, matching the planning observation page.</summary>
    public int RehearsalPageRows { get; init; } = 256;

    /// <summary>Deferred-planning entities admitted in one run, oldest first.</summary>
    public int DeferredIntakeEntityCap { get; init; } = 2048;
    /// <summary>Deferred-planning alias paths admitted in one run: four per admitted entity.</summary>
    public int DeferredIntakePathCap { get; init; } = 8192;

    /// <summary>Rehash markers written per transaction, matching the store's rehash gate page.</summary>
    public int RehashBatchRows { get; init; } = 128;

    /// <summary>Deferred candidates examined for one urgent user change: sixteen per admitted proof.</summary>
    public int UrgentCandidateCap { get; init; } = 1024;
    /// <summary>Urgent content proofs admitted per indexing pass.</summary>
    public int UrgentHashCap { get; init; } = 64;

    /// <summary>The shipped values; one home for every default in this unit.</summary>
    public static V2Tunables Default { get; } = new();
}
