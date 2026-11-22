namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Unfinished operations for the executor module.</summary>
public sealed class Unfinished
{
    /// <summary>Check whether this footprint belongs to a job still held by this pair.</summary>
    public bool CoversOpenJob(Scope scope) => throw new NotImplementedException();
    /// <summary>At boot or explicit Repair check known jobs once. For received feed input wake only its overlapping saved launch footprints, including an unbound child; keep the existing coalesced wake during a check.</summary>
    public Task CheckAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change? received, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>InstalledAsync for the selected Unfinished operation.</summary>
    public Task InstalledAsync(AcceptedJob job,Func<Scope,IEnumerable<Change>> heldInput,Func<Change,Task> wake) => throw new NotImplementedException();
    /// <summary>Check this pair's settled source coverage at the observed boundary.</summary>
    public Func<(JournalPosition? Local, string? Dropbox), bool>? CoversThrough
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }
}
