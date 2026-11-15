using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks for TimeRules (`--engine-checks decide`), written by the TimeRules writer.</summary>
public static class TimeRulesChecks
{
    public static void Run()
    {
        Check.Require(TimeRules.FileTimeEpochTicks == new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            "FileTimeEpochTicks is the Windows file-time epoch in .NET ticks.");

        var second = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var sameSecond = new[]
        {
            second,
            second.AddTicks(1),
            second.AddMilliseconds(999),
            second.ToOffset(TimeSpan.FromHours(2)),
            second.ToOffset(TimeSpan.FromHours(-5))
        };
        foreach (var time in sameSecond)
            Check.Require(TimeRules.Seconds(time) == TimeRules.Seconds(second),
                "Seconds is equal within one whole UTC second, whatever the offset.");

        Check.Require(TimeRules.Seconds(second.AddSeconds(1)) - TimeRules.Seconds(second) == 1,
            "Seconds advances by one at the next whole UTC second.");
        Check.Require(TimeRules.Seconds(second.AddSeconds(3)) != TimeRules.Seconds(second),
            "Seconds differs in a different whole UTC second.");

        var epoch = new DateTimeOffset(TimeRules.FileTimeEpochTicks, TimeSpan.Zero);
        var comparedAt = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var unreliable = new (string Name, DateTimeOffset Time, DateTimeOffset ComparedAt, bool Expected)[]
        {
            ("at the file-time epoch", epoch, comparedAt, true),
            ("before the file-time epoch", epoch.AddTicks(-1), comparedAt, true),
            ("the default (unset) time", default, comparedAt, true),
            ("just after the epoch and before the comparison time", epoch.AddTicks(1), comparedAt, false),
            ("well before the comparison time", second, comparedAt, false),
            ("equal to the comparison time", comparedAt, comparedAt, false),
            ("equal to the comparison time in another offset", comparedAt.ToOffset(TimeSpan.FromHours(2)), comparedAt, false),
            ("later than the comparison time", comparedAt.AddTicks(1), comparedAt, true)
        };
        foreach (var item in unreliable)
            Check.Require(TimeRules.Unreliable(item.Time, item.ComparedAt) == item.Expected,
                $"Unreliable is {item.Expected} {item.Name}.");

        foreach (var time in sameSecond)
            Check.Require(TimeRules.SameSecond(time, second), "SameSecond is equal within one whole UTC second.");
        Check.Require(!TimeRules.SameSecond(second.AddSeconds(1), second),
            "SameSecond differs in the next whole UTC second.");
        Check.Require(TimeRules.SameSecond(epoch, epoch.AddTicks(1)),
            "SameSecond deliberately does not reject unreliable timestamps.");

        var older = second.AddSeconds(-2);
        var middle = second.AddSeconds(-1);
        var justAfterEpoch = epoch.AddTicks(1);
        var orders = new (string Name, DateTimeOffset Left, DateTimeOffset Right, DateTimeOffset ComparedAt, TimeOrder Expected)[]
        {
            ("left at the file-time epoch", epoch, second, comparedAt, TimeOrder.Unreliable),
            ("right at the file-time epoch", second, epoch, comparedAt, TimeOrder.Unreliable),
            ("left after the comparison time", comparedAt.AddTicks(1), older, comparedAt, TimeOrder.Unreliable),
            ("right after the comparison time", older, comparedAt.AddTicks(1), comparedAt, TimeOrder.Unreliable),
            ("both unreliable", epoch, second.AddTicks(1), comparedAt, TimeOrder.Unreliable),
            ("both after the comparison time", comparedAt.AddTicks(1), comparedAt.AddSeconds(1), comparedAt, TimeOrder.Unreliable),
            ("both at the file-time epoch", epoch, epoch, comparedAt, TimeOrder.Unreliable),
            ("a same-second time after the comparison time", comparedAt, comparedAt.AddTicks(1), comparedAt, TimeOrder.Unreliable),
            ("same instant", second, second, comparedAt, TimeOrder.Equal),
            ("same whole second, different offsets", second, second.ToOffset(TimeSpan.FromHours(2)), comparedAt, TimeOrder.Equal),
            ("same whole second, trailing ticks", older, older.AddMilliseconds(999), comparedAt, TimeOrder.Equal),
            ("left in the newer whole second", middle, older, comparedAt, TimeOrder.LeftNewer),
            ("right in the newer whole second", older, middle, comparedAt, TimeOrder.RightNewer),
            ("just after the epoch is reliable", justAfterEpoch, justAfterEpoch.AddSeconds(1), comparedAt, TimeOrder.RightNewer),
            ("equal to the comparison time is reliable", second, middle, comparedAt, TimeOrder.LeftNewer)
        };
        foreach (var item in orders)
            Check.Require(TimeRules.Compare(item.Left, item.Right, item.ComparedAt) == item.Expected,
                $"Compare returns {item.Expected} for {item.Name}.");

        // The comparison time is sampled per decision, so a later sample makes an elapsed-run time reliable.
        var runStart = comparedAt.AddHours(-1);
        var elapsed = comparedAt.AddMinutes(-30);
        Check.Require(TimeRules.Unreliable(elapsed, runStart),
            "A time after the run start is unreliable at the run start.");
        Check.Require(!TimeRules.Unreliable(elapsed, comparedAt),
            "The same elapsed-run time is reliable at the current comparison time.");
        Check.Require(TimeRules.Compare(elapsed, elapsed.AddMinutes(-15), comparedAt) == TimeOrder.LeftNewer,
            "An elapsed-run time after the run start orders normally at the current comparison time.");
        Check.Require(TimeRules.Compare(elapsed.AddMinutes(-15), elapsed, comparedAt) == TimeOrder.RightNewer,
            "The later elapsed-run time is the newer side at the current comparison time.");

        Check.Require(!TimeRules.Unreliable(comparedAt, comparedAt), "A time equal to the comparison time is reliable.");
        Check.Require(!TimeRules.Unreliable(comparedAt.AddTicks(-1), comparedAt),
            "A time one tick before the comparison time is reliable.");
        Check.Require(TimeRules.Unreliable(comparedAt.AddTicks(1), comparedAt),
            "A time one tick after the comparison time is unreliable, even inside the same second.");
        Check.Require(TimeRules.Compare(comparedAt.AddTicks(1), comparedAt, comparedAt) == TimeOrder.Unreliable,
            "A one-tick future inside the comparison second is unreliable.");
        Check.Require(TimeRules.Compare(comparedAt.AddSeconds(-1), comparedAt, comparedAt) == TimeOrder.RightNewer,
            "The comparison second is newer than the whole second before it.");

        var repeatedOrder = TimeRules.Compare(middle, older, comparedAt);
        Check.Require(repeatedOrder == TimeOrder.LeftNewer && repeatedOrder == TimeRules.Compare(middle, older, comparedAt),
            "Repeated identical inputs yield the identical whole-second order.");
        var repeatedUnreliable = TimeRules.Compare(epoch, comparedAt, comparedAt);
        Check.Require(repeatedUnreliable == TimeOrder.Unreliable &&
            repeatedUnreliable == TimeRules.Compare(epoch, comparedAt, comparedAt),
            "Repeated identical inputs yield the identical unreliable result.");
        Check.Require(TimeRules.Unreliable(elapsed, comparedAt) == TimeRules.Unreliable(elapsed, comparedAt),
            "Repeated identical inputs yield the identical reliability.");
    }
}
