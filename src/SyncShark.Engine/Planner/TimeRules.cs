namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Time comparisons of D13/D15.</summary>
public static class TimeRules
{
    /// <summary>Compare operation on the supplied facts.</summary>
    public static TimeOrder Compare(DateTimeOffset left, DateTimeOffset right, DateTimeOffset comparedAt) => throw new NotImplementedException();
    /// <summary>SameSecond operation on the supplied facts.</summary>
    public static bool SameSecond(DateTimeOffset left, DateTimeOffset right) => throw new NotImplementedException();
    /// <summary>Unreliable operation on the supplied facts.</summary>
    public static bool Unreliable(DateTimeOffset time, DateTimeOffset comparedAt) => throw new NotImplementedException();
}
