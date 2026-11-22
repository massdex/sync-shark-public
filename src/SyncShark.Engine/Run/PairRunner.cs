namespace Massdex.SyncShark.Engine.Run;

/// <summary>PairRunner operations for the run module.</summary>
public sealed class PairRunner
{
    /// <summary>Use this pair’s single queue for both AutoSync intake and Repair discoveries; each source keeps its own settled position.</summary>
    public Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Checks the observed binding against the saved roots before admitting work for the pair.</summary>
    public Task<bool> CheckRootsAsync(PairConfig pair, RootIdentity? observed, DriveEvidence? observedDrive, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Complete one finite catch-up before startup trim; the live loop is started separately.</summary>
    public Task CatchUpAsync(PairConfig pair, (JournalPosition? Local,string? Dropbox) captured, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, CancellationToken ct) => throw new NotImplementedException();
}
