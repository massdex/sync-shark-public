using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Time comparisons of D13/D15.</summary>
public static class TimeRules
{
    /// <summary>The Windows file-time epoch (1601-01-01) in .NET ticks: a modified time at or before it is unset.</summary>
    public const long FileTimeEpochTicks = 504911232000000000;

    /// <summary>Whole UTC seconds.</summary>
    public static long Seconds(DateTimeOffset time) => time.ToUnixTimeSeconds();

    /// <summary>At or before the file-time epoch, or later than the current comparison time.</summary>
    public static bool Unreliable(DateTimeOffset time, DateTimeOffset comparedAt) =>
        time.UtcTicks <= FileTimeEpochTicks || time > comparedAt;

    /// <summary>D13's equality: the same whole UTC second, without an unreliable-time guard.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SameSecond(DateTimeOffset left, DateTimeOffset right) =>
        Seconds(left) == Seconds(right);

    /// <summary>D15: unreliable first, then whole-second equality, then the newer side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TimeOrder Compare(DateTimeOffset left, DateTimeOffset right, DateTimeOffset comparedAt)
    {
        if (Unreliable(left, comparedAt) || Unreliable(right, comparedAt)) return TimeOrder.Unreliable;
        if (SameSecond(left, right)) return TimeOrder.Equal;
        return left > right ? TimeOrder.LeftNewer : TimeOrder.RightNewer;
    }
}
