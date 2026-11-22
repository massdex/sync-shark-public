namespace Massdex.SyncShark.Engine.Run;

/// <summary>WorkQueue operations for the run module.</summary>
public sealed class WorkQueue
{
    /// <summary>Order competing journal and feed entries with Repair discoveries. Their direct children execute within the admitted input. Notify Local of each contiguous settled prefix before pause or stop, not on the next read. Dropbox pages have no journal callback.</summary>
    public Task AdmitAsync(PairConfig pair, ChangePage page, Func<Change,CancellationToken,Task> apply, Func<JournalPosition,CancellationToken,Task>? settleJournal, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Admit a Repair row or one binding’s complete group of moves; reject a missing row with no candidates. Order their old and observed footprints with the same competing inputs; no journal or feed position belongs to Repair.</summary>
    public Task AdmitAsync(PairConfig pair, Row? prepared, IReadOnlyList<RenameCandidate> bindingCandidates, Func<CancellationToken,Task> apply, CancellationToken ct) => throw new NotImplementedException();
}
