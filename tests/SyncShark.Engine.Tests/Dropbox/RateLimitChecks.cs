using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for RateLimit.</summary>
public static class RateLimitChecks
{
    /// <summary>Verifies the RateLimit contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var backoff = new EngineSettings.BackoffSettings(1, 2, 0, 30);

        // One slot per file transfer: a third transfer waits until one of the first two finishes.
        var limit = new RateLimit(2, backoff, _ => { });
        Check.Require(limit.Level == 2, "Configured starting level");
        var first = await limit.AdmitAsync(default);
        var second = await limit.AdmitAsync(default);
        var third = limit.AdmitAsync(default);
        Check.Require(!third.IsCompleted, "A third transfer waited for a free slot");
        await first.DisposeAsync();
        var admitted = await third;
        Check.Require(limit.Level == 2, "A finished transfer admitted the waiting one");
        await second.DisposeAsync();
        await admitted.DisposeAsync();

        // Growth only on a finished successful transfer: a full round doubles, a failure does not grow.
        var grow = new RateLimit(2, backoff, _ => { });
        grow.TransferFinished(true);
        Check.Require(grow.Level == 2, "One of a round of two does not grow");
        grow.TransferFinished(true);
        Check.Require(grow.Level == 4, "A full round doubles the level");
        grow.TransferFinished(false);
        Check.Require(grow.Level == 4, "A failed transfer does not grow");

        // After the first slow-down the level halves once and then grows by one per finished transfer.
        grow.SlowDown();
        Check.Require(grow.Level == 2, "A slow-down halves the level");
        grow.TransferFinished(true);
        Check.Require(grow.Level == 3, "Growth is one per transfer after the first slow-down");

        // The adopted retry schedule is the configured backoff, capped (zero jitter here is exact).
        var retry = new RateLimit(1, backoff, _ => { });
        Check.Require(retry.RetryDelay(0) == TimeSpan.FromSeconds(1), "Initial backoff");
        Check.Require(retry.RetryDelay(1) == TimeSpan.FromSeconds(2), "Multiplied backoff");
        Check.Require(retry.RetryDelay(9) == TimeSpan.FromSeconds(30), "Capped backoff");

        // The production schedule (product-0425): exact first delay, symmetric jitter, cap on the nominal delay.
        var jittered = new EngineSettings.BackoffSettings(1, 1.6, 0.2, 120);
        var schedule = new RateLimit(1, jittered, _ => { });
        Check.Require(schedule.RetryDelay(0, () => 0.0) == TimeSpan.FromSeconds(1) &&
            schedule.RetryDelay(0, () => 1.0) == TimeSpan.FromSeconds(1), "The first delay is exact");
        Check.Require(Near(schedule.RetryDelay(1, () => 0.5), 1.6), "The nominal delay at a mid sample");
        Check.Require(Near(schedule.RetryDelay(1, () => 0.0), 1.28), "The lower symmetric jitter");
        Check.Require(Near(schedule.RetryDelay(1, () => 1.0), 1.92), "The upper symmetric jitter");
        Check.Require(Near(schedule.RetryDelay(11, () => 0.5), 120), "The nominal delay caps at the maximum");
        Check.Require(Near(schedule.RetryDelay(11, () => 0.0), 96), "The cap is not clipped below jitter");
        Check.Require(Near(schedule.RetryDelay(11, () => 1.0), 144), "The cap allows the upper jitter range");

        // A stated provider wait is honoured on the supplied clock and recorded in the run log.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var events = new List<RunEvent>();
        var wait = new RateLimit(1, backoff, events.Add);
        var waiting = wait.WaitAsync(TimeSpan.FromSeconds(7), clock, default);
        Check.Require(!waiting.IsCompleted && clock.ScheduledDelay == TimeSpan.FromSeconds(7),
            "The provider wait was ignored or replaced");
        clock.ElapseScheduledDelay();
        await waiting;
        Check.Require(events.Any(item => item.Kind == RunEventKind.Wait), "The provider wait reached the run log");

        // No stated time means no wait at all.
        var none = new RateLimit(1, backoff, events.Add);
        Check.Require(none.WaitAsync(TimeSpan.Zero, clock, default).IsCompleted, "A zero wait returns at once");
    }

    private static bool Near(TimeSpan value, double seconds) => Math.Abs(value.TotalSeconds - seconds) < 1e-6;
}
