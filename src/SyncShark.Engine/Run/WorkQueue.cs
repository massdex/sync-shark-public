namespace Massdex.SyncShark.Engine.Run;

/// <summary>WorkQueue operations for the run module.</summary>
public sealed class WorkQueue
{
    /// <summary>Order competing journal and feed entries with Repair discoveries. Their direct children execute within the admitted input. Notify Local of each contiguous settled prefix before pause or stop, not on the next read. Dropbox pages have no journal callback.</summary>
    public Task AdmitAsync(PairConfig pair, ChangePage page, Func<Change,CancellationToken,Task> apply, Func<JournalPosition,CancellationToken,Task>? settleJournal, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Admit a Repair row or one binding’s complete group of moves; reject a missing row with no candidates. Order their old and observed footprints with the same competing inputs; no journal or feed position belongs to Repair.</summary>
    public Task AdmitAsync(PairConfig pair, Row? prepared, IReadOnlyList<RenameCandidate> bindingCandidates, Func<CancellationToken,Task> apply, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read retained source evidence for the selected bilateral footprint in receipt order, including path-only reports. Release only with exact settlement.</summary>
    public IEnumerable<Change> Held(Scope footprint) => throw new NotImplementedException();
    /// <summary>The unfinished-job owner bound by the pair runner.</summary>
    public Executor.Unfinished Unfinished { get => throw new NotImplementedException(); internal set => throw new NotImplementedException(); }
    /// <summary>Register a page and settle only contiguous source boundaries.</summary>
    public Task AdmitAsync(PairConfig pair, ChangePage page, Func<Change, CancellationToken, Task> apply, Func<JournalPosition, CancellationToken, Task>? settleJournal, Func<string, CancellationToken, Task>? settleCursor, Func<Change, CancellationToken, Task>? received, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Keep a failed input outstanding.</summary>
    public sealed class Unsettled : Exception { }
    /// <summary>Check both settled source prefixes against boundaries captured at the physical hop.</summary>
    public bool CoversHop((JournalPosition? Local, string? Dropbox) throughHop) => throw new NotImplementedException();
    /// <summary>Invalidate one source's coverage on reset before draining earlier work.</summary>
    public void LoseCoverage(Side source) => throw new NotImplementedException();
    /// <summary>Retry the original retained input or prepared operation within the selected scope.</summary>
    public Task<bool> RetryHeldAsync(Scope? scope, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read the published scope when a queued callback executes.</summary>
    public PathScope CurrentScope(PathScope fallback) => throw new NotImplementedException();
}
