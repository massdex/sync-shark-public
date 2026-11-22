namespace Massdex.SyncShark.Engine.Run;

/// <summary>Schedules prerequisite, listener and observational deadlines through one monotonic host wait.</summary>
public static class Monitor
{
    /// <summary>Observe operation on the supplied facts.</summary>
    public static Task ObserveAsync(PairConfig pair, ISide side, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
