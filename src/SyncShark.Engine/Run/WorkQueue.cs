namespace Massdex.SyncShark.Engine.Run;

/// <summary>Orders overlapping work while unrelated changes and Repair discoveries run concurrently.</summary>
public sealed class WorkQueue
{
    /// <summary>The exact input cannot yet be reproduced from its durable notice.</summary>
    public sealed class Unsettled : Exception { }

    /// <summary>The runner owns this pair's single pending-job check.</summary>
    public Executor.Unfinished Unfinished { get; internal set; } = null!;

    private readonly object gate = new();
    private readonly WorkOrder graph = new();
    private readonly bool[] gap = new bool[Enum.GetValues<Side>().Length];
    private readonly int[] pendingChanges = [0, 0];
    /// <summary>Admitted page changes not yet settled, per source side.</summary>
    internal int Pending(Side source) { lock (gate) return pendingChanges[(int)source]; }

    /// <summary>A fresh cursor after a reset is not coverage of the history that was lost.</summary>
    public void LoseCoverage(Side source) { lock (gate) gap[(int)source]=true; }

    private readonly SemaphoreSlim[] publishPosition=[new(1,1),new(1,1)];
    private readonly long[] resetAt=[0,0];
    internal Action<JournalPosition?>? WakeOwedRepair { get; init; }
    private long wokenRepairRevision;
    internal void WakeRepairOnce(long revision)
    {
        lock(gate){if(revision<=wokenRepairRevision) return; wokenRepairRevision=revision;}
        WakeOwedRepair?.Invoke(null);
    }
    /// <summary>Install a verified fresh base without waiting for held item work or allowing an older save to follow it.</summary>
    public async Task LoseCoverage(PairConfig pair,Side source,ChangePage reset,IStore store,CancellationToken ct)
    {
        if(!reset.ContinuityLost) throw new ArgumentException(nameof(reset));
        int index=(int)source;var publish=publishPosition[index];await publish.WaitAsync(ct);
        try
        {
            lock(gate){gap[index]=true;resetAt[index]=++graph.Order;}
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
            lock(gate){resetAt[source]=++graph.Order;prefix[source]=Task.CompletedTask;settledLocal=null;}
            await rewind();return floor;
        }
        finally {publish.Release();}
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
    internal Func<Func<(JournalPosition? Local,string? Dropbox),bool>?>? ProofNow { get; set; }
    internal Func<PathScope>? ScopeNow { get; init; }
    public PathScope CurrentScope(PathScope fallback) => ScopeNow?.Invoke() ?? fallback;

    internal Task PublishAsync(IEnumerable<string> paths, Func<Task> publish,CancellationToken ct)
    {
        var input=new WorkInput(Keys(paths),null){Publication=true};
        lock(gate)
        {
            var prior=input.Keys.SelectMany(graph.Matches).Distinct().SelectMany(k=>graph.ByKey[k]).Distinct().ToArray();
            graph.Register(input);
            // Started work drains under the old scope. Unsent work must observe the newly published one.
            input.Before=prior.Where(i=>i.Started || i.Publication).ToHashSet();
            foreach(var i in prior.Where(i=>!i.Started && !i.Publication)) {i.Before.Add(input); i.Changed?.TrySetResult();}
        }
        return Execute(input,publish,ct);
    }
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
        var entries = page.Changes.Select(c => new WorkInput(Keys([c.Path, c.OldPath, c.Observed?.Path,c.AffectedPath]), c)
        {
            Origins = c.Kind == ChangeKind.Renamed && c.OldPath != null ? Keys([c.OldPath]) : [],
            Destinations = c.Kind == ChangeKind.Renamed && c.Path != null ? Keys([c.Path]) : [],
            Removal = c.Kind == ChangeKind.Deleted && c.Path != null ? PathNames.Key(c.Path) : null
        }).ToArray();
        Task previous; long admitted; int source;
        WorkInput[] opportunities;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            opportunities=graph.RegisterWork(entries);
            var side = page.Position != null || settleJournal != null ? Side.Local : Side.Dropbox;
            if(page.ContinuityLost) gap[(int)side]=true;
            source=(int)side; admitted=++graph.Order;
            pendingChanges[source]+=entries.Length;
            previous = prefix[source];
            prefix[(int)side] = completed.Task;
        }
        return FinishPage(page,entries,opportunities,previous,completed,source,admitted,apply,settleJournal,settleCursor,received,ct);
    }

    private async Task Publish(int source,long admitted,Func<Task> write,CancellationToken ct)
    {
        var publish=publishPosition[source];await publish.WaitAsync(ct);
        try {if(admitted>resetAt[source]) await write();} finally {publish.Release();}
    }
    private async Task FinishPage(ChangePage page,WorkInput[] entries,WorkInput[] opportunities,Task previous,TaskCompletionSource completed,int source,long admitted,
        Func<Change,CancellationToken,Task> apply,Func<JournalPosition,CancellationToken,Task>? settleJournal,Func<string,CancellationToken,Task>? settleCursor,
        Func<Change,CancellationToken,Task>? received,CancellationToken ct)
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
                    await Publish(source,admitted,()=>settleJournal(new(end.JournalId,next),ct),ct).ConfigureAwait(false);
            }
            await Publish(source,admitted,async ()=>
            {
                if(settleJournal!=null && page.Position is {} last) await settleJournal(last,ct);
                if(settleCursor!=null && page.Cursor is {} cursor) await settleCursor(cursor,ct);
                var actual=page.Position!=null && actualPosition!=null && !page.ContinuityLost ? await actualPosition(ct) : null;
                lock(gate)
                {
                    if(page.Position is {} offered) settledLocal=actual?.JournalId==offered.JournalId ? new(offered.JournalId,Math.Min(offered.NextUsn,actual.NextUsn)) : null;
                    if(page.Cursor!=null) settledDropbox=page.ContinuityLost ? null : page.Cursor;
                }
            },ct);
            completed.TrySetResult();
        }
        catch (Exception error)
        {
            completed.TrySetException(error);
            try { await Task.WhenAll(jobs).ConfigureAwait(false); } catch { /* The first unsettled boundary is reported below. */ }
            throw;
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
        var item = new WorkInput(Keys(names), null)
        {
            Row=prepared, Moves=bindingCandidates,
            Origins = bindingCandidates.Select(c => PathNames.Key(c.Synced.PathOn(c.Side))).ToArray(),
            Destinations = bindingCandidates.Select(c => PathNames.Key(c.Moved.Path)).ToArray()
        };
        WorkInput[] opportunities; lock (gate) opportunities=graph.RegisterWork([item]);
        return Task.WhenAll(Task.WhenAll(opportunities.Select(i=>Attempt(i,ct))),Execute(item, () => apply(ct), ct));
    }

    /// <summary>Run one Repair attempt only when nothing overlapping is running or held; a waiting Repair row is no queue link. False: declined.</summary>
    internal async Task<bool> AdmitRepairAsync(PairConfig pair, Row? prepared, IReadOnlyList<RenameCandidate> bindingCandidates,
        Func<CancellationToken,Task> apply, CancellationToken ct, bool bypassNoEffect = false)
    {
        ct.ThrowIfCancellationRequested();
        if (prepared == null && bindingCandidates.Count == 0) throw new ArgumentException(nameof(bindingCandidates));
        IEnumerable<string?> names = bindingCandidates.SelectMany(c => new[] { c.Moved.Path, c.Synced.LocalPath, c.Synced.DropboxPath });
        if (prepared != null) names = names.Concat(Row.Keys(prepared));
        var item = new WorkInput(Keys(names), null)
        {
            Row=prepared, Moves=bindingCandidates, Repair=true,
            Origins = bindingCandidates.Select(c => PathNames.Key(c.Synced.PathOn(c.Side))).ToArray(),
            Destinations = bindingCandidates.Select(c => PathNames.Key(c.Moved.Path)).ToArray()
        };
        if(!await ClaimRepairAsync(item,ct,bypassNoEffect).ConfigureAwait(false)) return false;
        Exception? fatal=null;
        try { await apply(ct).ConfigureAwait(false); }
        catch (Unsettled) when (!ct.IsCancellationRequested) { /* A saved job owns this effect; Repair never resends or retains it. */ }
        catch (Exception error) { fatal=error; }
        lock (gate) Release(item,fatal);
        if(fatal!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fatal).Throw();
        return true;
    }
    private async Task<bool> ClaimRepairAsync(WorkInput item,CancellationToken ct,bool bypassNoEffect)
    {
        while(true)
        {
            Task wait = null!;
            lock(gate)
            {
                ct.ThrowIfCancellationRequested();
                var conflicts=item.Keys.SelectMany(graph.Matches).Distinct(StringComparer.Ordinal)
                    .SelectMany(k=>graph.ByKey.TryGetValue(k,out var list)?(IEnumerable<WorkInput>)list:Array.Empty<WorkInput>()).Distinct().Where(i=>i!=item).ToArray();
                var running=conflicts.Where(i=>i.Started || i.Publication).ToArray();
                if(bypassNoEffect)
                {
                    // The end turn may compare afresh past a proved no-effect hold; an active owner or claimed work under the chain still stops it.
                    if(running.Length!=0) wait=Task.WhenAny(running.Select(ChangedOf)).Unwrap();
                    else if(conflicts.Any(i=>Prerequisites(i,static _=>false))) return false;
                    else { Grant(item,bypass:true); return true; }
                }
                else if(running.Length!=0) wait=Task.WhenAny(running.Select(ChangedOf)).Unwrap();
                else
                {
                    var active=new List<WorkInput>();
                    if(conflicts.Any(i=>i.Failed || i.Consumed || Prerequisites(i,static _=>true,active))) return false; // held or claimed work owns the path; never join it
                    // Unsent work ahead is finite; an active prerequisite that parks signals its own change, so recompute then.
                    if(conflicts.Length!=0) wait=Task.WhenAny(conflicts.Concat(active).Distinct().Select(ChangedOf)).Unwrap();
                    else { Grant(item,bypass:false); return true; }
                }
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    // Install a granted Repair row as started; the end turn depends on no unstarted follower.
    private void Grant(WorkInput item,bool bypass)
    {
        graph.Register(item);
        if(bypass) item.Before.RemoveWhere(p=>!p.Started && !p.Publication);
        graph.Claimed(item); item.Started=true;
    }

    // Walk an unsent input's prerequisites, directly or transitively: true at claimed work or a matching park; started ones are collected, not entered.
    private static bool Prerequisites(WorkInput item,Func<WorkInput,bool> parked,List<WorkInput>? active=null)
    {
        var seen=new HashSet<WorkInput>(); var todo=new Stack<WorkInput>(); todo.Push(item);
        while(todo.TryPop(out var current))
        {
            if(!seen.Add(current)) continue;
            foreach(var prior in current.Before)
            {
                if(prior.Consumed || prior.Failed && !prior.Started && parked(prior)) return true;
                if(prior.Started) { active?.Add(prior); continue; }
                todo.Push(prior);
            }
        }
        return false;
    }
    // A fresh signal per wait: a completed one would return immediately and spin until the input finished.
    private static Task ChangedOf(WorkInput item)
    {
        if(item.Changed is null || item.Changed.Task.IsCompleted) item.Changed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        return item.Changed.Task;
    }

    // A started input that parks must let an overlapping publication go first: it asked for the scope change before this retry.
    private void ReversePublications(WorkInput item)
    {
        foreach(var key in item.Keys.SelectMany(graph.Matches).Distinct(StringComparer.Ordinal))
            if(graph.ByKey.TryGetValue(key,out var list))
                foreach(var publication in list.Where(p=>p.Publication && p.Before.Contains(item)).ToArray())
                { publication.Before.Remove(item); item.Before.Add(publication); publication.Changed?.TrySetResult(); }
    }

    private static string[] Keys(IEnumerable<string?> paths) => paths.Where(p => p != null)
        .Select(p => PathNames.TreeKey(PathNames.Key(p!))).Distinct(StringComparer.Ordinal).DefaultIfEmpty("").ToArray();

    // Claim a first attempt or a retained retry under the same publication and overlap order.
    private async Task<bool> Claim(WorkInput item,bool retry,CancellationToken ct)
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
                    if(!retry) graph.Claimed(item);
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
    private async Task Execute(WorkInput item, Func<Task> apply, CancellationToken ct)
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
                // A waiting publication goes first; expose the retry only after that order is established.
                ReversePublications(item);
                // A successor in the same acquired page may sit behind another tail when this input first fails.
                foreach(var key in item.Keys.SelectMany(graph.Matches)) if(graph.ByKey[key].Last!.Value is {Publication:false} last) item.LastOverlap=Math.Max(item.LastOverlap,last.Order);
                item.Failed=true; item.Started=false; item.Retry=apply; graph.Hold(item); item.Changed?.TrySetResult(); }
            // Replaying this input after indexing lets its callback observe proof received while it ran.
            try
            {
                await Attempt(item,ct,item.Order).ConfigureAwait(false);
                if(item.Change!=null || item.Publication) await item.Settled.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch(Exception error) {lock(gate) Release(item,error); throw;}
        }
        catch (Exception error) { lock(gate) Release(item,error); throw; }
    }

    /// <summary>Retry the exact retained callback; never replace an uncommitted move with a synthetic change.</summary>
    public Task<bool> RetryHeldAsync(Scope? scope,CancellationToken ct) => RetryHeldAsync(scope,ct,null);
    /// <summary>Retry only held identities on the reviewed notice; never apply a neighbouring change as its substitute.</summary>
    public async Task<bool> RetryHeldAsync(Scope? scope,CancellationToken ct,Notice? selected)
    {
        WorkInput[] entries;
        lock(gate) entries=(selected!=null ? selected.Subject.Items.Select(x=>PathNames.KeyFor(x.Side,x.Path)).Distinct() : scope==null ? graph.HeldByKey.Keys : Keys([scope.LocalPath,scope.DropboxPath]).SelectMany(graph.Matches))
            .Where(graph.HeldByKey.ContainsKey).SelectMany(k=>graph.HeldByKey[k]).Distinct().Where(i=>i.Failed && (selected==null || i.SameSubject(selected))).OrderBy(i=>i.Order).ToArray();
        foreach(var item in entries) await Attempt(item,ct).ConfigureAwait(false);
        return entries.Length!=0;
    }
    private async Task Attempt(WorkInput item,CancellationToken ct,long? observed=null)
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
                    ReversePublications(item);
                    item.Started=false; item.Changed?.TrySetResult();
                    if(item.LastOverlap<=through) return;
                    through=item.LastOverlap;
                }
            }
            catch(Exception error){lock(gate) Release(item,error);throw;}
        }
    }
    private void Release(WorkInput item,Exception? error=null)
    {
        if(item.Nodes==null) return;
        if(item.Change!=null) pendingChanges[(int)item.Change.Side]--;
        graph.Remove(item); item.Retry=null;
        item.Changed?.TrySetResult();
        if(error==null) item.Settled.TrySetResult(); else item.Settled.TrySetException(error);
    }

    /// <summary>Mark the entire settled cohort before waking any dependent input.</summary>
    public static void ConsumeHeld(IEnumerable<Change> settled)
    {
        List<Action>? releases=null;
        foreach(var report in settled)
        {
            var release=report.MarkHeld is {} mark ? mark() : report.ConsumeHeld;
            if(release!=null) (releases??=[]).Add(release);
        }
        if(releases!=null) foreach(var release in releases) release();
    }

    /// <summary>Borrow waiting reports on their own side in receipt order; only explicit acknowledgment consumes them.</summary>
    public IEnumerable<Change> Held(Scope footprint)
    {
        lock (gate)
        {
            var matches = Keys([footprint.LocalPath, footprint.DropboxPath]).SelectMany(graph.Matches)
                .Distinct(StringComparer.Ordinal).SelectMany(k => graph.ByKey[k]).Distinct().Where(item =>
                {
                    if (item.Started || item.Consumed || item.Change is not { } c) return false;
                    var root = c.Side == Side.Local ? footprint.LocalPath : footprint.DropboxPath;
                    return root.Length == 0 || new[] { c.Path, c.OldPath,c.AffectedPath }.Any(p => p != null && PathNames.IsSelfOrUnder(p, root));
                });
            return matches.OrderBy(i => i.Order).Select(Borrow).ToArray();
        }
        Change Borrow(WorkInput item)
        {
            Action? Mark()
            {
                lock(gate)
                {
                    if(item.Started || item.Consumed) return null;
                    item.Consumed=true;
                    return item.Failed ? () => { lock(gate) Release(item); } : null;
                }
            }
            return item.Change! with {MarkHeld=Mark,ConsumeHeld=()=>Mark()?.Invoke()};
        }
    }
}
