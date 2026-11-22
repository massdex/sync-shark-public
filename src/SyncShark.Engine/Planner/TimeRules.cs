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
    /// <summary>Returns false without a measured sample or when the difference falls within its uncertainty. Never correct client-modified timestamps.</summary>
    public static bool DefinitelyDeletedLater(DateTimeOffset localModified, DateTimeOffset serverDeleted, (TimeSpan Offset,TimeSpan Uncertainty)? deletionClock) => throw new NotImplementedException();
}
