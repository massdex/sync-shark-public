namespace Massdex.SyncShark.Engine.Run;

/// <summary>The work queue's ordering graph: per-key tails, held inputs, and arrivals postponed behind known departures. Callers hold the queue gate.</summary>
internal sealed class WorkOrder
{
    private readonly SortedSet<string> occupied = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, LinkedList<WorkInput>> ByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<WorkInput>> postponed = new(StringComparer.Ordinal),
        arrivals = new(StringComparer.Ordinal), removals = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, HashSet<WorkInput>> HeldByKey = new(StringComparer.Ordinal);
    // One sequence orders registrations, page admissions and source resets.
    internal long Order;

    private static void Index(Dictionary<string,HashSet<WorkInput>> index,IEnumerable<string> keys,WorkInput item,bool add)
    {
        foreach(var key in keys)
        {
            if (!index.TryGetValue(key,out var values)) { if (!add) continue; index[key]=values=[]; }
            if (add) values.Add(item); else if(values.Remove(item) && values.Count==0) index.Remove(key);
        }
    }

    internal IEnumerable<string> Matches(string key)
    {
        if (key.Length == 0) return occupied;
        var bounds = PathNames.DescendantBounds(key);
        return PathNames.Ancestors(key).Append("").Append(key).Where(ByKey.ContainsKey)
            .Concat(occupied.GetViewBetween(bounds.Low, bounds.High).TakeWhile(k => k != bounds.High));
    }

    // Active inputs share the held index so later evidence is not lost behind an unsent same-key follower.
    internal WorkInput[] RegisterWork(WorkInput[] items)
    {
        var active=HeldByKey.Count==0 ? Array.Empty<WorkInput>() : items.SelectMany(i=>i.Keys).Distinct().SelectMany(Matches).Distinct()
            .Where(HeldByKey.ContainsKey).SelectMany(k=>HeldByKey[k]).Distinct().ToArray();
        foreach(var item in items) Register(item);
        foreach(var i in active) i.LastOverlap=Order;
        return active;
    }
    internal void Register(WorkInput item)
    {
        item.Order = ++Order;
        var conflicts = item.Keys.SelectMany(Matches).Distinct(StringComparer.Ordinal).ToArray();
        item.Before = conflicts.Select(key => ByKey[key].Last!.Value).ToHashSet();
        // A Repair row is never a link in receipt order: it bumps no LastOverlap, joins no arrival chain, promotes or postpones nothing, and sits ahead of the unsent work its grant puts behind it (WorkQueue.Grant).
        if(!item.Repair) foreach(var prior in item.Before) prior.LastOverlap=item.Order;
        var tail=item.Destinations is [var destination] && ByKey.TryGetValue(destination,out var arrivalsAtKey) ? arrivalsAtKey.Last!.Value : null;
        // The first unsent arrival at an endpoint is the ordering boundary for all later arrivals there.
        if(tail!=null && !tail.Started && !item.Repair && item.Destinations.Length==1 && tail.Destinations.SequenceEqual(item.Destinations))
        { item.IncomingRun=tail.IncomingRun ??= new(tail); tail.NextArrival=item; item.IncomingRun.Tail=item; }
        var promoted=item.Repair ? new HashSet<WorkInput>() : Promote(item,conflicts);
        // Started and held inputs keep their order with later work even behind a publication.
        if(!item.Publication) item.Before.UnionWith(conflicts.Where(HeldByKey.ContainsKey).SelectMany(k=>HeldByKey[k]).Where(h=>h!=item && !promoted.Contains(h)));
        if(item.Origins.Length!=0 && !item.Repair) IndexOrigins(item,promoted);
        if(item.IncomingRun==null) Index(arrivals,item.Destinations,item,true);
        if(item.Removal!=null) Index(removals,[item.Removal],item,true);
        item.Nodes=new LinkedListNode<WorkInput>[item.Keys.Length];
        for(int i=0;i<item.Keys.Length;i++)
        {
            var key=item.Keys[i]; if(occupied.Add(key)) ByKey.Add(key,new());
            item.Nodes[i]=item.Repair ? ByKey[key].AddFirst(item) : ByKey[key].AddLast(item);
        }
    }
    // A postponed input whose departure this item follows moves behind this item; every other postponed conflict precedes it.
    private HashSet<WorkInput> Promote(WorkInput item,string[] conflicts)
    {
        var promoted=new HashSet<WorkInput>();
        foreach(var key in conflicts)
            if(postponed.TryGetValue(key,out var earlier))
                foreach(var pending in earlier)
                {
                    if(promoted.Contains(pending)) continue;
                    if(pending.LastDeparture is {} previous && item.Before.Contains(previous) &&
                        item.Origins.Any(origin=>pending.Destinations.Contains(origin,StringComparer.Ordinal) || pending.Removal!=null && PathNames.IsUnderKey(origin,pending.Removal)))
                    {
                        pending.Before.Remove(previous);pending.Before.Add(item);pending.LastDeparture=item;
                        pending.Changed?.TrySetResult();promoted.Add(pending);
                    }
                    else item.Before.Add(pending.IncomingRun?.Tail??pending);
                }
        return promoted;
    }
    // Unsent arrivals at this departure's origins, and removals above them, wait for it.
    private void IndexOrigins(WorkInput item,HashSet<WorkInput> promoted)
    {
        var waiting=new HashSet<WorkInput>();
        foreach(var origin in item.Origins)
        {
            if(arrivals.TryGetValue(origin,out var at)) waiting.UnionWith(at);
            foreach(var ancestor in PathNames.Ancestors(origin)) if(removals.TryGetValue(ancestor,out var under)) waiting.UnionWith(under);
        }
        waiting.ExceptWith(promoted);
        if(waiting.Count!=0) Postpone(waiting,item);
    }

    // The known departure and its earlier prerequisites move together ahead of the unsent arrival/removal.
    private void Postpone(HashSet<WorkInput> waiting,WorkInput departure)
    {
        // A previous departure shared by every waiting input already precedes all of them.
        var frontier=waiting.First().LastDeparture;
        if(frontier!=null && waiting.Any(i=>i.LastDeparture!=frontier)) frontier=null;
        var seen=DependentsOf(departure,waiting,frontier);
        // After bypassing the arrivals, preserve their receipt order behind one boundary per destination.
        foreach(var group in waiting.OrderBy(i=>i.Order).GroupBy(i=>i.Destinations is [var dest] && i.Removal==null ? dest : null))
        {
            MoveRun? run=null;
            foreach(var dependent in group)
            {
                if(group.Key!=null && run!=null)
                {
                    var tail=run.Tail;tail.NextArrival=dependent;dependent.Before.Add(tail);
                    for(WorkInput? part=dependent;part!=null;part=part.NextArrival){part.IncomingRun=run;run.Tail=part;}
                    Index(arrivals,dependent.Destinations,dependent,false);
                    dependent.Changed?.TrySetResult();continue;
                }
                if(group.Key!=null) run=dependent.IncomingRun??=new(dependent);
                if(frontier!=null && seen.Contains(frontier)) dependent.Before.Remove(frontier);
                dependent.Before.Add(departure);dependent.LastDeparture=departure;dependent.Changed?.TrySetResult(); Index(postponed,dependent.Keys,dependent,true);
            }
        }
    }
    // Walk the departure's unstarted prerequisites, bypassing waiting inputs; returns every input visited.
    private static HashSet<WorkInput> DependentsOf(WorkInput departure,HashSet<WorkInput> waiting,WorkInput? frontier)
    {
        var todo = new Stack<WorkInput>(); todo.Push(departure);
        var seen = new HashSet<WorkInput>();
        while (todo.TryPop(out var current))
        {
            if (current.Started || !seen.Add(current) || current==frontier) continue;
            foreach(var prior in current.Before.Where(p=>p.IncomingRun?.Head is {} head && head!=p && waiting.Contains(head)).ToArray())
            { current.Before.Remove(prior); current.Before.Add(prior.IncomingRun!.Head!); current.Changed?.TrySetResult(); }
            var bypass = new Stack<WorkInput>(current.Before.Where(waiting.Contains));
            if (bypass.Count != 0) current.Changed?.TrySetResult();
            while (bypass.TryPop(out var dependent))
            {
                if (!current.Before.Remove(dependent)) continue;
                foreach (var earlier in dependent.Before)
                    if (current.Before.Add(earlier) && waiting.Contains(earlier)) bypass.Push(earlier);
            }
            foreach (var predecessor in current.Before) todo.Push(predecessor);
        }
        return seen;
    }

    // A first attempt joins the held index (unless consumed or a publication) and leaves the arrival and removal indexes.
    internal void Claimed(WorkInput item)
    {
        if(!item.Consumed && !item.Publication) Index(HeldByKey,item.Keys,item,true);
        Index(arrivals,item.Destinations,item,false);
        if(item.IncomingRun is {} run && run.Head==item){run.Head=item.NextArrival;if(run.Head!=null) Index(arrivals,run.Head.Destinations,run.Head,true);}
        if(item.Removal!=null) Index(removals,[item.Removal],item,false);
    }
    internal void Hold(WorkInput item) => Index(HeldByKey,item.Keys,item,true);
    // Unlink a finished input from every index and per-key list.
    internal void Remove(WorkInput item)
    {
        Index(arrivals,item.Destinations,item,false); if(item.Removal!=null) Index(removals,[item.Removal],item,false);
        Index(postponed,item.Keys,item,false); Index(HeldByKey,item.Keys,item,false);
        for(int i=0;i<item.Keys.Length;i++)
        {
            var key=item.Keys[i];var list=ByKey[key];list.Remove(item.Nodes![i]);
            if (list.Count == 0) { ByKey.Remove(key); occupied.Remove(key); }
        }
        item.Nodes=null;
    }
}
