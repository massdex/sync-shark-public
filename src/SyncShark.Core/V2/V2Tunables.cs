using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark.V2;

/// <summary>The v2 engine's tunable bounds that V2Settings supplies: one unit, one derivation each. Their shipped
/// values live in the embedded v2-tunables.json; the engine's other settings (paths, retention, batching) stay on
/// <see cref="V2Settings"/>.</summary>
public sealed record V2Tunables
{
    private const string ResourceName = "SyncShark.V2Tunables.json";

    /// <summary>Paths per bounded exact-metadata page. The four concurrent metadata reads fill one page at a
    /// time, so a page's retained metadata stays bounded.</summary>
    public required int MetadataPagePaths { get; init; }

    /// <summary>The first wait after a failed attempt. The existing failure policy doubles it each time, up to
    /// <see cref="RetryMaximumDelay"/>; the attempt counter saturates rather than overflowing.</summary>
    public required TimeSpan RetryInitialDelay { get; init; }
    /// <summary>The factor applied to each successive retry wait.</summary>
    public required double RetryDelayMultiplier { get; init; }
    /// <summary>Doublings applied before the ceiling. Five doublings from ten seconds reach the five-minute
    /// ceiling, so a later attempt cannot grow past it.</summary>
    public required int RetryMaximumDoublings { get; init; }
    /// <summary>The retry ceiling: one named limit shared by every retry tier.</summary>
    public required TimeSpan RetryMaximumDelay { get; init; }

    /// <summary>The largest USN journal window read in one replay step: 64 kernel journal pages.</summary>
    public required long JournalReplayWindowBytes { get; init; }
    /// <summary>The smallest journal window: one kernel journal page, below which the window stops halving.</summary>
    public required long JournalReplayMinimumWindowBytes { get; init; }

    /// <summary>The upper bound of <see cref="V2Settings.LocalReplayWindowsPerRun"/>: eight times the shipped
    /// eight, so a mistyped setting cannot make one inventory call read the whole retained history.</summary>
    public required int MaximumLocalReplayWindowsPerRun { get; init; }

    /// <summary>The background gap-verification read stride: the same 64 KiB copy stride as the shared transfer
    /// path.</summary>
    public required int LocalGapContentBufferBytes { get; init; }
    /// <summary>Rows hashed per background gap work page; a page's proofs finish inside one store slice.</summary>
    public required int LocalGapHashPageRows { get; init; }

    /// <summary>Rows per rehearsal hydration page, matching the planning observation page.</summary>
    public required int RehearsalPageRows { get; init; }

    /// <summary>Deferred-planning entities admitted in one run, oldest first.</summary>
    public required int DeferredIntakeEntityCap { get; init; }
    /// <summary>Deferred-planning alias paths admitted in one run: four per admitted entity.</summary>
    public required int DeferredIntakePathCap { get; init; }

    /// <summary>Rehash markers written per transaction, matching the store's rehash gate page.</summary>
    public required int RehashBatchRows { get; init; }

    /// <summary>Deferred candidates examined for one urgent user change: sixteen per admitted proof.</summary>
    public required int UrgentCandidateCap { get; init; }
    /// <summary>Urgent content proofs admitted per indexing pass.</summary>
    public required int UrgentHashCap { get; init; }

    /// <summary>The shipped values, read once from the embedded settings data file and validated.</summary>
    public static V2Tunables Default { get; } = Load();

    private static V2Tunables Load()
    {
        using var document = CliCatalogResource.Load(typeof(V2Tunables).Assembly, ResourceName);
        return (document.Deserialize<V2Tunables>(new JsonSerializerOptions
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException(ResourceName)).Validated();
    }

    /// <summary>The one domain check of these bounds; <see cref="V2Settings.Tunables"/> applies it to every
    /// instance it accepts, so no consumer receives an unusable value.</summary>
    internal V2Tunables Validated()
    {
        // A page, count or buffer of zero admits no work, so every such bound is at least one.
        Require(MetadataPagePaths >= 1, nameof(MetadataPagePaths));
        Require(MaximumLocalReplayWindowsPerRun >= 1, nameof(MaximumLocalReplayWindowsPerRun));
        Require(LocalGapContentBufferBytes >= 1, nameof(LocalGapContentBufferBytes));
        Require(LocalGapHashPageRows >= 1, nameof(LocalGapHashPageRows));
        Require(RehearsalPageRows >= 1, nameof(RehearsalPageRows));
        Require(RehashBatchRows >= 1, nameof(RehashBatchRows));
        Require(UrgentCandidateCap >= 1, nameof(UrgentCandidateCap));
        Require(UrgentHashCap >= 1, nameof(UrgentHashCap));
        // Intake gives urgent user changes half of each cap and bulk work the rest; both halves must admit one.
        Require(DeferredIntakeEntityCap >= 2, nameof(DeferredIntakeEntityCap));
        Require(DeferredIntakePathCap >= 2, nameof(DeferredIntakePathCap));
        Require(RetryInitialDelay > TimeSpan.Zero, nameof(RetryInitialDelay));
        Require(double.IsFinite(RetryDelayMultiplier) && RetryDelayMultiplier >= 1, nameof(RetryDelayMultiplier));
        Require(RetryMaximumDoublings >= 0, nameof(RetryMaximumDoublings));
        Require(JournalReplayMinimumWindowBytes >= 1, nameof(JournalReplayMinimumWindowBytes));
        Require(JournalReplayWindowBytes >= JournalReplayMinimumWindowBytes, nameof(JournalReplayWindowBytes));
        return this;
    }

    /// <summary>The deferred wake clamps to the safety floor and then to <see cref="RetryMaximumDelay"/>, so a ceiling
    /// below the floor would undo the floor; <see cref="V2Settings"/> checks the pair as either is set.
    /// shortcut: one initializer changing both must order them so each step stays valid, or it is refused loudly;
    /// upgrade if a real caller needs both at once.</summary>
    internal TimeSpan Floor(TimeSpan floor)
    {
        Require(floor <= RetryMaximumDelay, nameof(V2Settings.MinSafetyPoll));
        return floor;
    }

    internal V2Tunables Ceiling(TimeSpan floor)
    {
        Require(RetryMaximumDelay >= floor, nameof(RetryMaximumDelay));
        return this;
    }

    private static void Require(bool valid, string name)
    {
        if (!valid) throw new ArgumentOutOfRangeException(name);
    }
}
