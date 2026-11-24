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
    internal Func<Task>? Retry;
    internal Row? Row;
    internal IReadOnlyList<RenameCandidate> Moves=[];
    internal long LastOverlap;
    internal MoveRun? IncomingRun;
    internal WorkInput? NextArrival;
    internal WorkInput? LastDeparture;
    internal long Order;
}
