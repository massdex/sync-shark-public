namespace Massdex.SyncShark.Engine.Run;

/// <summary>Orders overlapping work while unrelated changes and Repair discoveries run concurrently.</summary>
public sealed class WorkQueue
{
    /// <summary>The exact input cannot yet be reproduced from its durable notice.</summary>
    public sealed class Unsettled : Exception { }

    private sealed class MoveRun(Input first) { internal Input? Head=first; internal Input Tail=first; }
    private sealed class Input(string[] keys, Change? change)
    {
        internal readonly string[] Keys = keys;
        internal LinkedListNode<Input>[]? Nodes;
        internal readonly Change? Change = change;
        internal readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HashSet<Input> Before = [];
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
        internal Input? NextArrival;
        internal Input? LastDeparture;
        internal long Order;
    }

    /// <summary>The runner owns this pair's single pending-job check.</summary>
    public Executor.Unfinished Unfinished { get; internal set; } = null!;

    private readonly object gate = new();
    private readonly SortedSet<string> occupied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedList<Input>> byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Input>> postponed = new(StringComparer.Ordinal),
        arrivals = new(StringComparer.Ordinal), removals = new(StringComparer.Ordinal), heldByKey = new(StringComparer.Ordinal);
    private readonly bool[] gap = new bool[2];

    /// <summary>A fresh cursor after a reset is not coverage of the history that was lost.</summary>
    public void LoseCoverage(Side source) { lock (gate) gap[(int)source]=true; }

    private readonly SemaphoreSlim[] publishPosition=[new(1,1),new(1,1)];
    private readonly long[] resetAt=[0,0];
    internal Action<JournalPosition?>? WakeOwedRepair { get; init; }
    /// <summary>Install a verified fresh base without waiting for held item work or allowing an older save to follow it.</summary>
    public async Task LoseCoverage(PairConfig pair,Side source,ChangePage reset,IStore store,CancellationToken ct)
    {
        if(!reset.ContinuityLost) throw new ArgumentException(nameof(reset));
        int index=(int)source;var publish=publishPosition[index];await publish.WaitAsync(ct);
        try
        {
            lock(gate){gap[index]=true;resetAt[index]=++order;}
            await Monitor.ObserveAsync(pair,source,reset,store,ct);
        }
        finally {publish.Release();}
        WakeOwedRepair?.Invoke(source==Side.Local ? reset.Position : null);
    }
    internal async Task<JournalPosition?> RewindJournalAsync(JournalPosition wake,Func<Task> rewind,CancellationToken ct)
    {
        int source=(int)Side.Local;var publish=publishPosition[source];await publish.WaitAsync(ct);
        try
        {
            if(actualPosition==null || await actualPosition(ct) is not {} floor || floor.JournalId!=wake.JournalId || floor.NextUsn>=wake.NextUsn) return null;
            lock(gate){resetAt[source]=++order;prefix[source]=Task.CompletedTask;settledLocal=null;}
            await rewind();return floor;
        }
        finally {publish.Release();}
    }
    private static void Index(Dictionary<string,HashSet<Input>> index,IEnumerable<string> keys,Input item,bool add)
    {
        foreach(var key in keys)
        {
            if (!index.TryGetValue(key,out var values)) { if (!add) continue; index[key]=values=[]; }
            if (add) values.Add(item); else if(values.Remove(item) && values.Count==0) index.Remove(key);
        }
    }
    // Only after intake and its effects have drained: keep publication fences on the same queue.
    internal void RestartPrefix()
    {
        lock(gate){prefix[0]=prefix[1]=Task.CompletedTask; settledLocal=null; settledDropbox=null; gap[0]=gap[1]=false;}
    }
    private readonly Task[] prefix = [Task.CompletedTask, Task.CompletedTask];
    private readonly Func<CancellationToken,Task<JournalPosition?>>? actualPosition;
    public WorkQueue() { }
    public WorkQueue(Func<CancellationToken,Task<JournalPosition?>> actualPosition) => this.actualPosition=actualPosition;
    internal Func<PathScope>? ScopeNow { get; init; }
    public PathScope CurrentScope(PathScope fallback) => ScopeNow?.Invoke() ?? fallback;

    internal Task PublishAsync(IEnumerable<string> paths, Func<Task> publish,CancellationToken ct)
    {
        var input=new Input(Keys(paths),null){Publication=true};
        lock(gate)
        {
            var prior=input.Keys.SelectMany(Matches).Distinct().SelectMany(k=>byKey[k]).Distinct().ToArray();
            Register(input);
            // Started work drains under the old scope. Unsent work must observe the newly published one.
            input.Before=prior.Where(i=>i.Started || i.Publication).ToHashSet();
            foreach(var i in prior.Where(i=>!i.Started && !i.Publication)) {i.Before.Add(input); i.Changed?.TrySetResult();}
        }
        return Execute(input,publish,ct);
    }
    private long order;
    private JournalPosition? settledLocal;
    private string? settledDropbox;

    /// <summary>Both source prefixes must cover the fresh physical-hop capture; a move receipt or empty queue proves nothing.</summary>
    public bool CoversHop((JournalPosition? Local,string? Dropbox) throughHop)
    {
        lock (gate) return !gap[0] && !gap[1] && throughHop.Local is { } at && settledLocal is { } pc && pc.JournalId == at.JournalId &&
            pc.NextUsn >= at.NextUsn && throughHop.Dropbox != null && throughHop.Dropbox == settledDropbox;
    }

    /// <summary>Publish the whole page before callbacks can run. Journal progress follows only its contiguous settled prefix.</summary>
    public Task AdmitAsync(PairConfig pair, ChangePage page, Func<Change,CancellationToken,Task> apply,
        Func<JournalPosition,CancellationToken,Task>? settleJournal, CancellationToken ct) =>
        AdmitAsync(pair, page, apply, settleJournal, null, null, ct);

    /// <summary>Wake received news before dispatch; persist each source boundary in receipt order.</summary>
    public Task AdmitAsync(PairConfig pair, ChangePage page, Func<Change,CancellationToken,Task> apply,
        Func<JournalPosition,CancellationToken,Task>? settleJournal, Func<string,CancellationToken,Task>? settleCursor,
        Func<Change,CancellationToken,Task>? received, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entries = page.Changes.Select(c => new Input(Keys([c.Path, c.OldPath, c.Observed?.Path,c.AffectedPath]), c)
        {
            Origins = c.Kind == ChangeKind.Renamed && c.OldPath != null ? Keys([c.OldPath]) : [],
            Destinations = c.Kind == ChangeKind.Renamed && c.Path != null ? Keys([c.Path]) : [],
            Removal = c.Kind == ChangeKind.Deleted && c.Path != null ? PathNames.Key(c.Path) : null
        }).ToArray();
        Task previous; long admitted; int source;
        Input[] opportunities;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            opportunities=RegisterWork(entries);
            var side = page.Position != null || settleJournal != null ? Side.Local : Side.Dropbox;
            if(page.ContinuityLost) gap[(int)side]=true;
            source=(int)side; admitted=++order;
            previous = prefix[source];
            prefix[(int)side] = completed.Task;
        }
        return FinishPage();

        async Task Publish(Func<Task> write)
        {
            var publish=publishPosition[source];await publish.WaitAsync(ct);
            try {if(admitted>resetAt[source]) await write();} finally {publish.Release();}
        }
        async Task FinishPage()
        {
            var retries=Task.WhenAll(opportunities.Select(i=>Attempt(i,ct)));
            var wakes = received == null ? Task.CompletedTask : Task.WhenAll(entries.Select(i => Wake(i.Change!)));
            async Task Wake(Change change) => await received!(change, ct).ConfigureAwait(false);
            var jobs = entries.Select(item => Execute(item, () => apply(item.Change!, ct), ct)).ToArray();
            try
            {
                await Task.WhenAll(previous, wakes, retries).ConfigureAwait(false);
                for (var i = 0; i < jobs.Length; i++)
                {
                    await jobs[i].ConfigureAwait(false);
                    if (settleJournal != null && page.Position is { } end && i+1 < jobs.Length &&
                        entries[i+1].Change is { Usn: { } next, JournalId: var journal } && journal == end.JournalId &&
                        entries[i].Change!.Usn < next)
                        await Publish(()=>settleJournal(new(end.JournalId,next),ct)).ConfigureAwait(false);
                }
                await Publish(async ()=>
                {
                    if(settleJournal!=null && page.Position is {} last) await settleJournal(last,ct);
                    if(settleCursor!=null && page.Cursor is {} cursor) await settleCursor(cursor,ct);
                    var actual=page.Position!=null && actualPosition!=null && !page.ContinuityLost ? await actualPosition(ct) : null;
                    lock(gate)
                    {
                        if(page.Position is {} offered) settledLocal=actual?.JournalId==offered.JournalId ? new(offered.JournalId,Math.Min(offered.NextUsn,actual.NextUsn)) : null;
                        if(page.Cursor!=null) settledDropbox=page.ContinuityLost ? null : page.Cursor;
                    }
                });
                completed.TrySetResult();
            }
            catch (Exception error)
            {
                completed.TrySetException(error);
                try { await Task.WhenAll(jobs).ConfigureAwait(false); } catch { /* The first unsettled boundary is reported below. */ }
                throw;
            }
        }
    }

    /// <summary>Order a prepared row or an entire move against both the original and observed endpoints.</summary>
    public Task AdmitAsync(PairConfig pair, Row? prepared, IReadOnlyList<RenameCandidate> bindingCandidates,
        Func<CancellationToken,Task> apply, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (prepared == null && bindingCandidates.Count == 0) throw new ArgumentException(nameof(bindingCandidates));
        IEnumerable<string?> names = bindingCandidates.SelectMany(c => new[] { c.Moved.Path, c.Synced.LocalPath, c.Synced.DropboxPath });
        if (prepared != null) names = names.Concat(Row.Keys(prepared));
        var item = new Input(Keys(names), null)
        {
            Row=prepared, Moves=bindingCandidates,
            Origins = bindingCandidates.Select(c => PathNames.Key(c.Synced.PathOn(c.Side))).ToArray(),
            Destinations = bindingCandidates.Select(c => PathNames.Key(c.Moved.Path)).ToArray()
        };
        Input[] opportunities; lock (gate) opportunities=RegisterWork([item]);
        return Task.WhenAll(Task.WhenAll(opportunities.Select(i=>Attempt(i,ct))),Execute(item, () => apply(ct), ct));
    }

    private static string[] Keys(IEnumerable<string?> paths) => paths.Where(p => p != null)
        .Select(p => PathNames.TreeKey(PathNames.Key(p!))).Distinct(StringComparer.Ordinal).DefaultIfEmpty("").ToArray();

    private IEnumerable<string> Matches(string key)
    {
        if (key.Length == 0) return occupied;
        var bounds = PathNames.DescendantBounds(key);
        return PathNames.Ancestors(key).Append("").Append(key).Where(byKey.ContainsKey)
            .Concat(occupied.GetViewBetween(bounds.Low, bounds.High).TakeWhile(k => k != bounds.High));
    }

    // Active inputs share the held index so later evidence is not lost behind an unsent same-key follower.
    private Input[] RegisterWork(Input[] items)
    {
        var active=heldByKey.Count==0 ? Array.Empty<Input>() : items.SelectMany(i=>i.Keys).Distinct().SelectMany(Matches).Distinct()
            .Where(heldByKey.ContainsKey).SelectMany(k=>heldByKey[k]).Distinct().ToArray();
        foreach(var item in items) Register(item);
        foreach(var i in active) i.LastOverlap=order;
        return active;
    }
    private void Register(Input item)
    {
        item.Order = ++order;
        var conflicts = item.Keys.SelectMany(Matches).Distinct(StringComparer.Ordinal).ToArray();
        item.Before = conflicts.Select(key => byKey[key].Last!.Value).ToHashSet();
        foreach(var prior in item.Before) prior.LastOverlap=item.Order;
        var tail=item.Destinations is [var destination] && byKey.TryGetValue(destination,out var arrivalsAtKey) ? arrivalsAtKey.Last!.Value : null;
        // The first unsent arrival at an endpoint is the ordering boundary for all later arrivals there.
        if(tail!=null && !tail.Started && item.Destinations.Length==1 && tail.Destinations.SequenceEqual(item.Destinations))
        { item.IncomingRun=tail.IncomingRun ??= new(tail); tail.NextArrival=item; item.IncomingRun.Tail=item; }
        var promoted=new HashSet<Input>();
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
        if(item.Origins.Length!=0)
        {
            var waiting=new HashSet<Input>();
            foreach(var origin in item.Origins)
            {
                if(arrivals.TryGetValue(origin,out var at)) waiting.UnionWith(at);
                foreach(var ancestor in PathNames.Ancestors(origin)) if(removals.TryGetValue(ancestor,out var under)) waiting.UnionWith(under);
            }
            waiting.ExceptWith(promoted);
            if(waiting.Count!=0) Postpone(waiting,item);
        }
        if(item.IncomingRun==null) Index(arrivals,item.Destinations,item,true);
        if(item.Removal!=null) Index(removals,[item.Removal],item,true);
        item.Nodes=new LinkedListNode<Input>[item.Keys.Length];
        for(int i=0;i<item.Keys.Length;i++)
        {
            var key=item.Keys[i]; if(occupied.Add(key)) byKey.Add(key,new());
            item.Nodes[i]=byKey[key].AddLast(item);
        }
    }

    // The known departure and its earlier prerequisites move together ahead of the unsent arrival/removal.
    private void Postpone(HashSet<Input> waiting,Input departure)
    {
        // A previous departure shared by every waiting input already precedes all of them.
        var frontier=waiting.First().LastDeparture;
        if(frontier!=null && waiting.Any(i=>i.LastDeparture!=frontier)) frontier=null;
        var todo = new Stack<Input>(); todo.Push(departure);
        var seen = new HashSet<Input>();
        while (todo.TryPop(out var current))
        {
            if (current.Started || !seen.Add(current) || current==frontier) continue;
            foreach(var prior in current.Before.Where(p=>p.IncomingRun?.Head is {} head && head!=p && waiting.Contains(head)).ToArray())
            { current.Before.Remove(prior); current.Before.Add(prior.IncomingRun!.Head!); current.Changed?.TrySetResult(); }
            var bypass = new Stack<Input>(current.Before.Where(waiting.Contains));
            if (bypass.Count != 0) current.Changed?.TrySetResult();
            while (bypass.TryPop(out var dependent))
            {
                if (!current.Before.Remove(dependent)) continue;
                foreach (var earlier in dependent.Before)
                    if (current.Before.Add(earlier) && waiting.Contains(earlier)) bypass.Push(earlier);
            }
            foreach (var predecessor in current.Before) todo.Push(predecessor);
        }
        // After bypassing the arrivals, preserve their receipt order behind one boundary per destination.
        foreach(var group in waiting.OrderBy(i=>i.Order).GroupBy(i=>i.Destinations is [var dest] && i.Removal==null ? dest : null))
        {
            MoveRun? run=null;
            foreach(var dependent in group)
            {
                if(group.Key!=null && run!=null)
                {
                    var tail=run.Tail;tail.NextArrival=dependent;dependent.Before.Add(tail);
                    for(Input? part=dependent;part!=null;part=part.NextArrival){part.IncomingRun=run;run.Tail=part;}
                    Index(arrivals,dependent.Destinations,dependent,false);
                    dependent.Changed?.TrySetResult();continue;
                }
                if(group.Key!=null) run=dependent.IncomingRun??=new(dependent);
                if(frontier!=null && seen.Contains(frontier)) dependent.Before.Remove(frontier);
                dependent.Before.Add(departure);dependent.LastDeparture=departure;dependent.Changed?.TrySetResult(); Index(postponed,dependent.Keys,dependent,true);
            }
        }
    }

    // Claim a first attempt or a retained retry under the same publication and overlap order.
    private async Task<bool> Claim(Input item,bool retry,CancellationToken ct)
    {
        while(true)
        {
            Task wait;
            lock(gate)
            {
                ct.ThrowIfCancellationRequested();
                if(retry && (item.Started || item.Consumed || item.Retry==null)) return false;
                item.Before.RemoveWhere(p=>p.Settled.Task.IsCompletedSuccessfully);
                if(item.Before.Count==0)
                {
                    item.Started=true;
                    if(!retry){
                        if(!item.Consumed && !item.Publication) Index(heldByKey,item.Keys,item,true);
                        Index(arrivals,item.Destinations,item,false);
                        if(item.IncomingRun is {} run && run.Head==item){run.Head=item.NextArrival;if(run.Head!=null) Index(arrivals,run.Head.Destinations,run.Head,true);}
                        if(item.Removal!=null) Index(removals,[item.Removal],item,false);
                    }
                    return !item.Consumed;
                }
                var before=Task.WhenAll(item.Before.Select(p=>p.Settled.Task));
                item.Changed??=new(TaskCreationOptions.RunContinuationsAsynchronously);
                if(item.Changed.Task.IsCompleted) item.Changed=new(TaskCreationOptions.RunContinuationsAsynchronously);
                wait=Task.WhenAny(before,item.Changed.Task).Unwrap();
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }
    private async Task Execute(Input item, Func<Task> apply, CancellationToken ct)
    {
        try
        {
            bool run=await Claim(item,false,ct).ConfigureAwait(false);
            if (run) await apply().ConfigureAwait(false);
            lock (gate)
            {
                Release(item);
            }
        }
        catch (Unsettled) when (!ct.IsCancellationRequested)
        {
            // Retry still needs this report. A following page cannot publish its cursor until it is consumed.
            lock (gate) {
                // A successor in the same acquired page may sit behind another tail when this input first fails.
                foreach(var key in item.Keys.SelectMany(Matches)) if(byKey[key].Last!.Value is {Publication:false} last) item.LastOverlap=Math.Max(item.LastOverlap,last.Order);
                item.Failed=true; item.Started=false; item.Retry=apply; Index(heldByKey,item.Keys,item,true); }
            try {await Attempt(item,ct,item.Order).ConfigureAwait(false); await item.Settled.Task.WaitAsync(ct).ConfigureAwait(false);}
            catch(Exception error) {lock(gate) Release(item,error); throw;}
        }
        catch (Exception error) { lock(gate) Release(item,error); throw; }
    }

    /// <summary>Retry the exact retained callback; never replace an uncommitted move with a synthetic change.</summary>
    public Task<bool> RetryHeldAsync(Scope? scope,CancellationToken ct) => RetryHeldAsync(scope,ct,null);
    /// <summary>Retry only held identities on the reviewed notice; never apply a neighbouring change as its substitute.</summary>
    public async Task<bool> RetryHeldAsync(Scope? scope,CancellationToken ct,Notice? selected)
    {
        Input[] entries;
        lock(gate) entries=(selected!=null ? selected.Subject.Items.Select(x=>PathNames.KeyFor(x.Side,x.Path)).Distinct() : scope==null ? heldByKey.Keys : Keys([scope.LocalPath,scope.DropboxPath]).SelectMany(Matches))
            .Where(heldByKey.ContainsKey).SelectMany(k=>heldByKey[k]).Distinct().Where(i=>i.Failed && (selected==null || SameSubject(i,selected))).OrderBy(i=>i.Order).ToArray();
        foreach(var item in entries) await Attempt(item,ct).ConfigureAwait(false);
        return entries.Length!=0;
    }
    private static bool SameSubject(Input i,Notice n)
    {
        bool Match(Side side,string? id,string? path) => id!=null && path!=null &&
            n.Subject.Items.Any(s=>s.Side==side && s.Id==id && PathNames.Same(s.Path,path));
        if(i.Change is {} c) return Match(c.Side,c.Id,c.Path) || Match(c.Side,c.Id,c.OldPath);
        foreach(var side in new[]{Side.Local,Side.Dropbox})
            if(i.Row?.EntryOn(side) is {} entry && Match(side,entry.Id,entry.Path)) return true;
        return i.Moves.Any(m=>Match(m.Side,m.Moved.Id,m.Moved.Path) || Match(m.Side,m.Synced.IdOn(m.Side),m.Synced.PathOn(m.Side)));
    }
    private async Task Attempt(Input item,CancellationToken ct,long? observed=null)
    {
        long? through=observed;
        while(await Claim(item,true,ct).ConfigureAwait(false))
        {
            lock(gate) through??=item.LastOverlap;
            try {await item.Retry!().ConfigureAwait(false);lock(gate) Release(item);return;}
            catch(Unsettled)
            {
                lock(gate)
                {
                    item.Started=false;
                    if(item.LastOverlap<=through) return;
                    through=item.LastOverlap;
                }
            }
            catch(Exception error){lock(gate) Release(item,error);throw;}
        }
    }
    private void Release(Input item,Exception? error=null)
    {
        if(item.Nodes==null) return;
        Index(arrivals,item.Destinations,item,false); if(item.Removal!=null) Index(removals,[item.Removal],item,false);
        Index(postponed,item.Keys,item,false); Index(heldByKey,item.Keys,item,false); item.Retry=null;
        for(int i=0;i<item.Keys.Length;i++)
        {
            var key=item.Keys[i];var list=byKey[key];list.Remove(item.Nodes![i]);
            if (list.Count == 0) { byKey.Remove(key); occupied.Remove(key); }
        }
        item.Nodes=null; if(error==null) item.Settled.TrySetResult(); else item.Settled.TrySetException(error);
    }

    /// <summary>Borrow waiting reports on their own side in receipt order; only explicit acknowledgment consumes them.</summary>
    public IEnumerable<Change> Held(Scope footprint)
    {
        lock (gate)
        {
            var matches = Keys([footprint.LocalPath, footprint.DropboxPath]).SelectMany(Matches)
                .Distinct(StringComparer.Ordinal).SelectMany(k => byKey[k]).Distinct().Where(item =>
                {
                    if (item.Started || item.Consumed || item.Change is not { } c) return false;
                    var root = c.Side == Side.Local ? footprint.LocalPath : footprint.DropboxPath;
                    return root.Length == 0 || new[] { c.Path, c.OldPath,c.AffectedPath }.Any(p => p != null && PathNames.IsSelfOrUnder(p, root));
                });
            return matches.OrderBy(i => i.Order).Select(i => i.Change! with
            {
                ConsumeHeld = () => { lock (gate) { if (!i.Started && !i.Consumed) { i.Consumed = true; if (i.Failed) Release(i); } } }
            }).ToArray();
        }
    }
}
