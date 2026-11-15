namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Records each temporary responsibility with its attempt owner's wait, and probes that owner for eligibility.</summary>
internal sealed class TemporaryRecords(ExecutorContext context)
{
    private const long RestOfRun = long.MaxValue;
    private readonly Dictionary<(Side Side, string Key, string Domain, string? Identity, string? Obligation), long> _exact = [];
    private readonly Dictionary<(Side Side, string Key, string? Identity), long> _at = [];

    internal void Clear() { lock (_exact) { _exact.Clear(); _at.Clear(); } }

    // Exact per preimage, until the wait its item-attempt owner named; without one, the rest of the run.
    internal void Record(WorkSelection work, long? wait) => Keep(work, wait ?? RestOfRun);

    // A certified completion failure names no wait; it and its settlement-stable tuple stay out for the run.
    internal void RecordCertified(WorkSelection work)
    {
        Keep(work, RestOfRun);
        lock (_exact) _exact[(work.Side, work.Key, work.Domain, work.Identity, null)] = RestOfRun;
    }

    // Own records answer before the host's key hold, so dependents inherit the blocker's wait. A key hold without
    // this run's holding record is unattributed and keeps the rest of the run.
    internal bool Skips(WorkSelection work, out long until)
    {
        until = RestOfRun;
        bool exact = false, stable = false;
        long own = 0, settled = 0;
        lock (_exact)
            if (_exact.Count != 0)
            {
                exact = _exact.TryGetValue(Responsibility(work), out own);
                stable = _exact.TryGetValue((work.Side, work.Key, work.Domain, work.Identity, null), out settled);
            }
        // Asked outside the lock: the owner takes its own gate, which is never nested inside this one.
        if (exact && Holds(own)) until = own;
        else if (stable && Holds(settled)) until = settled;
        else if (context.ItemAttempts.HeldForRetry(work.Side, work.Key))
            until = HoldsAt(work.Side, work.Key, work.Identity) ?? RestOfRun;
        else return false;
        return true;
    }

    // A folder root or survivor carries no selection: its host key hold or exact/path-only record answers.
    internal bool SkipsAt(Side side, string key, string? identity) =>
        context.ItemAttempts.HeldForRetry(side, key) || HoldsAt(side, key, identity) is not null;

    // Waiting dependents never ran: retain their responsibility and end suspension without a prerequisite clash.
    // The blocker keeps its own record or host hold, rather than acquiring a new run-long record.
    internal void Retain(WorkSelection blocker, long until, IEnumerable<(WorkSelection Work, bool RenameDependency)> pending)
    {
        foreach (var (work, _) in pending)
        {
            if (!ReferenceEquals(work, blocker)) Keep(work, until);
            context.Store.EndSelection(work);
        }
    }

    private long? HoldsAt(Side side, string key, string? identity)
    {
        bool exact, path;
        long own, any;
        lock (_exact)
        {
            exact = _at.TryGetValue((side, key, identity), out own);
            path = _at.TryGetValue((side, key, null), out any);
        }
        return exact && Holds(own) ? own : path && Holds(any) ? any : null;
    }

    private bool Holds(long wait) => wait == RestOfRun || context.ItemAttempts.RetryPending(wait);

    private void Keep(WorkSelection work, long wait)
    {
        lock (_exact)
        {
            Keep(_exact, Responsibility(work), wait);
            Keep(_at, (work.Side, work.Key, work.Identity), wait);
        }
    }

    // Wait identities rise with time, so the larger names the later decision; the rest of the run is never shortened.
    private static void Keep<TKey>(Dictionary<TKey, long> records, TKey key, long wait) where TKey : notnull =>
        records[key] = records.TryGetValue(key, out var kept) ? Math.Max(kept, wait) : wait;

    private static (Side, string, string, string?, string?) Responsibility(WorkSelection work) =>
        (work.Side, work.Key, work.Domain, work.Identity, work.Obligation);
}
