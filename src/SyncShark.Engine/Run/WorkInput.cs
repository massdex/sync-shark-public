namespace Massdex.SyncShark.Engine.Run;

/// <summary>One admitted change, publication, prepared row or move, with its place in the work queue's order.</summary>
internal sealed class WorkInput(string[] keys, Change? change)
{
    internal readonly string[] Keys = keys;
    internal LinkedListNode<WorkInput>[]? Nodes;
    internal readonly Change? Change = change;
    internal readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal HashSet<WorkInput> Before = [];
    internal TaskCompletionSource? Changed;
    internal bool Publication;
    internal string[] Origins = [], Destinations = [];
    internal string? Removal;
    internal bool Started, Failed, Consumed;
    internal bool Repair; // a Repair row: it never advances another input's retry and is never a link in receipt order while it waits
    internal Func<Task>? Retry;
    internal Row? Row;
    internal IReadOnlyList<RenameCandidate> Moves=[];
    internal long LastOverlap;
    internal Task? Floor; internal int Refusals; // an owed refusal retry (null: none owed; set at park, taken by the next Claim) and the input's consecutive refusals
    internal MoveRun? IncomingRun;
    internal WorkInput? NextArrival;
    internal WorkInput? LastDeparture;
    internal long Order;

    /// <summary>True when this input's own change, row or move identity is on the reviewed notice's subject.</summary>
    internal bool SameSubject(Notice n)
    {
        bool Match(Side side,string? id,string? path) => id!=null && path!=null &&
            n.Subject.Items.Any(s=>s.Side==side && s.Id==id && PathNames.Same(s.Path,path));
        if(Change is {Kind:ChangeKind.Deleted,Id:null,Path:{} gone} tombstone) return Executor.ItemSync.WaitsOnPath(n,tombstone.Side,gone);
        if(Change is {} c) return Match(c.Side,c.Id,c.Path) || Match(c.Side,c.Id,c.OldPath);
        foreach(var side in new[]{Side.Local,Side.Dropbox})
            if(Row?.EntryOn(side) is {} entry && Match(side,entry.Id,entry.Path)) return true;
        return Moves.Any(m=>Match(m.Side,m.Moved.Id,m.Moved.Path) || Match(m.Side,m.Synced.IdOn(m.Side),m.Synced.PathOn(m.Side)));
    }
}
