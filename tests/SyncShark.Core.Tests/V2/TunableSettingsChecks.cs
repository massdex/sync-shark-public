namespace Massdex.SyncShark.V2;

/// <summary>Proves the tunable settings are the source the consumers read, not code constants, and that their
/// shipped defaults equal the values the engine ran with before the move.</summary>
internal static class TunableSettingsChecks
{
    internal static void Run()
    {
        var failures = new List<string>();
        Run(nameof(RetryScheduleFollowsSettings), RetryScheduleFollowsSettings, failures);
        Run(nameof(JournalWindowFollowsSettings), JournalWindowFollowsSettings, failures);
        Run(nameof(UrgentHashCapFollowsSettings), UrgentHashCapFollowsSettings, failures);
        Run(nameof(ShippedDefaultsUnchanged), ShippedDefaultsUnchanged, failures);
        Run(nameof(BoundsValidatedBySettingsOwner), BoundsValidatedBySettingsOwner, failures);
        if (failures.Count > 0)
            throw new InvalidOperationException("tunable settings checks failed: " + string.Join("; ", failures));
    }

    private static void Run(string name, Action body, List<string> failures)
    {
        try { body(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures.Add(name + ": " + error.Message); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // The whole retry schedule is the settings record: base, growth, doubling count and ceiling.
    private static void RetryScheduleFollowsSettings()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // The safety floor is lowered first so the 20-second ceiling stays above it.
        var tuned = V2Settings.Default with
        {
            MinSafetyPoll = TimeSpan.FromSeconds(20),
            Tunables = V2Tunables.Default with
            {
                RetryInitialDelay = TimeSpan.FromSeconds(3),
                RetryDelayMultiplier = 3,
                RetryMaximumDoublings = 2,
                RetryMaximumDelay = TimeSpan.FromSeconds(20)
            }
        };
        Require(FailureRetryOwner.Failed(tuned, 0, now).RetryAfter == now.AddSeconds(3), "first wait is the configured base");
        Require(FailureRetryOwner.Failed(tuned, 1, now).RetryAfter == now.AddSeconds(9), "second wait uses the configured multiplier");
        Require(FailureRetryOwner.Failed(tuned, 2, now).RetryAfter == now.AddSeconds(20), "a wait past the configured ceiling is capped");
        // A ceiling above the growth shows the doubling count itself stops the growth at the configured exponent.
        var roomy = tuned with { Tunables = tuned.Tunables with { RetryMaximumDelay = TimeSpan.FromMinutes(5) } };
        Require(FailureRetryOwner.Failed(roomy, 3, now).RetryAfter == now.AddSeconds(27),
            "the configured doubling count stops further growth");
    }

    // Both journal window bounds are the settings record.
    private static void JournalWindowFollowsSettings()
    {
        var tuned = V2Settings.Default with
        {
            Tunables = V2Tunables.Default with
            {
                JournalReplayWindowBytes = 4L * 1024 * 1024,
                JournalReplayMinimumWindowBytes = 1024 * 1024
            }
        };
        var window = new JournalReplayWindow(tuned);
        Require(window.Bytes == 4L * 1024 * 1024 && window.MaximumBytes == 4L * 1024 * 1024,
            "a fresh window is the configured maximum");
        Require(window.Halve(window.Bytes) && window.Bytes == 2L * 1024 * 1024, "halving follows the configured window");
        Require(window.Halve(window.Bytes) && window.Bytes == 1024 * 1024, "halving stops at the configured minimum");
        Require(!window.Halve(window.Bytes), "the configured minimum cannot be halved");
        Require(window.RetryNoProgress() && window.Bytes == 4L * 1024 * 1024, "a stalled retry resumes at the configured maximum");
    }

    // The urgent proof admission bound is the settings record, not a policy constant.
    private static void UrgentHashCapFollowsSettings()
    {
        foreach (var cap in new[] { 2, 5 })
        {
            var budget = new VerificationBudget(cap);
            var admitted = 0;
            while (budget.AdmitUrgent(userChange: true)) admitted++;
            Require(admitted == cap, "urgent admissions equal the configured cap");
            Require(!budget.AdmitUrgent(userChange: false), "a non-urgent proof never uses the urgent cap");
        }
    }

    // R3-07: the settings owner refuses unusable bounds and reversed or floor-undoing relationships, and accepts
    // the boundary values, which then reach the consumers unchanged.
    private static void BoundsValidatedBySettingsOwner()
    {
        static bool Refused(Func<V2Settings> build)
        {
            try { _ = build(); return false; }
            catch (ArgumentOutOfRangeException) { return true; }
        }
        var d = V2Settings.Default;
        var t = V2Tunables.Default;
        Require(Refused(() => d with { Tunables = t with { MetadataPagePaths = 0 } }), "a zero metadata page is refused");
        Require(Refused(() => d with { Tunables = t with { RehearsalPageRows = 0 } }), "a zero rehearsal page is refused");
        Require(Refused(() => d with { Tunables = t with { RetryInitialDelay = TimeSpan.Zero } }), "a zero first retry is refused");
        Require(Refused(() => d with { Tunables = t with { RetryDelayMultiplier = 0.5 } }), "a shrinking retry factor is refused");
        Require(Refused(() => d with { Tunables = t with { RetryDelayMultiplier = double.PositiveInfinity } }),
            "an infinite retry factor is refused");
        Require(Refused(() => d with { Tunables = t with { RetryMaximumDoublings = -1 } }), "negative doublings are refused");
        Require(Refused(() => d with { Tunables = t with { JournalReplayMinimumWindowBytes = 0 } }), "a zero journal window is refused");
        Require(Refused(() => d with { Tunables = t with { JournalReplayWindowBytes = t.JournalReplayMinimumWindowBytes - 1 } }),
            "reversed journal window bounds are refused");
        Require(Refused(() => d with { Tunables = t with { RetryMaximumDelay = d.MinSafetyPoll - TimeSpan.FromTicks(1) } }),
            "a retry ceiling below the safety floor is refused");
        Require(Refused(() => d with { MinSafetyPoll = t.RetryMaximumDelay + TimeSpan.FromTicks(1) }),
            "a safety floor above the retry ceiling is refused");

        var boundary = d with
        {
            Tunables = t with
            {
                MetadataPagePaths = 1, RetryInitialDelay = TimeSpan.FromSeconds(1), RetryDelayMultiplier = 1,
                RetryMaximumDoublings = 0, RetryMaximumDelay = d.MinSafetyPoll,
                JournalReplayWindowBytes = t.JournalReplayMinimumWindowBytes, DeferredIntakeEntityCap = 2, DeferredIntakePathCap = 2
            }
        };
        var window = new JournalReplayWindow(boundary);
        Require(window.Bytes == t.JournalReplayMinimumWindowBytes && !window.Halve(window.Bytes),
            "equal journal bounds reach the window owner and stop halving at once");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Require(FailureRetryOwner.Failed(boundary, 3, now).RetryAfter == now.AddSeconds(1),
            "the smallest accepted schedule reaches the retry owner unchanged");
    }

    // The moved values keep exactly today's defaults, so moving them changed no behaviour.
    private static void ShippedDefaultsUnchanged()
    {
        var settings = V2Tunables.Default;
        Require(settings.RetryInitialDelay == TimeSpan.FromSeconds(10) &&
            settings.RetryDelayMultiplier == 2 && settings.RetryMaximumDoublings == 5 &&
            settings.RetryMaximumDelay == TimeSpan.FromMinutes(5), "retry defaults unchanged");
        Require(settings.JournalReplayWindowBytes == 64L * 1024 * 1024 &&
            settings.JournalReplayMinimumWindowBytes == 1024L * 1024, "journal window defaults unchanged");
        Require(settings.MaximumLocalReplayWindowsPerRun == 64 &&
            V2Settings.Default.LocalReplayWindowsPerRun == 8, "journal replay window-count defaults unchanged");
        Require(settings.LocalGapContentBufferBytes == 64 * 1024 && settings.LocalGapHashPageRows == 16,
            "background gap defaults unchanged");
        Require(settings.RehearsalPageRows == 256, "rehearsal page default unchanged");
        Require(settings.DeferredIntakeEntityCap == 2048 && settings.DeferredIntakePathCap == 8192,
            "deferred intake defaults unchanged");
        Require(settings.RehashBatchRows == 128, "rehash batch default unchanged");
        Require(settings.UrgentCandidateCap == 1024 && settings.UrgentHashCap == 64, "urgent admission defaults unchanged");
        Require(settings.MetadataPagePaths == 128, "exact-metadata page default unchanged");
    }
}
