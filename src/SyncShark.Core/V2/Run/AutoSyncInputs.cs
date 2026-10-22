namespace Massdex.SyncShark.V2;

/// <summary>What every pair loop of <see cref="AutoSync"/> shares: the contracts it drives and the edge's inputs.</summary>
internal sealed record AutoSyncInputs(IStateStore Store, ISyncRun Run, IChangeWatchSource Watches, V2Settings Settings,
    Func<PairDef, bool> Paused, Func<TimeSpan> SafetyPoll, TimeProvider Time, Action<PairAutoState> Publish)
{
    /// <summary>The edge's safety-poll interval, never below <see cref="V2Settings.MinSafetyPoll"/> (zero or negative included).</summary>
    public TimeSpan SafetyInterval()
    {
        var interval = SafetyPoll();
        return interval < Settings.MinSafetyPoll ? Settings.MinSafetyPoll : interval;
    }

    /// <summary>Why the pair may not run automatically now, or null when it may.</summary>
    public (AutoStatus Status, NoticeReason? Reason)? Ineligible(PairDef? pair)
    {
        if (pair is null || !pair.Enabled) return (AutoStatus.Off, null);
        // A saved layout that may not coexist with another pair is held from startup, before any watch or run.
        if (PairAdmission.DecideSaved(pair, Store.Pairs()) is { } layout) return (AutoStatus.Held, layout);
        if (!Store.FirstSyncDone(pair.PairId)) return (AutoStatus.Held, AutoSyncReasons.FirstSyncNeeded);
        return (Store is SqliteStateStore sql && sql.PairPaused(pair.PairId) || Paused(pair)) ? (AutoStatus.Paused, null) : null;
    }
}
