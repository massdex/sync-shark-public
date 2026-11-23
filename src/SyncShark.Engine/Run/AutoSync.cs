using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Feeds each source independently; its read position need not be its settled position.</summary>
public static class AutoSync
{
    public static Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, CancellationToken ct, Func<(CancellationToken Wake,Func<CancellationToken,Task<JournalPosition?>> Rewind)>? journalReplay=null) =>
        ReadAsync(pair,settings,events,paths,queue,local,dropbox,store,null,null,ct,journalReplay:journalReplay);

    public static Task ReadThroughAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, (JournalPosition? Local,string? Dropbox) captured, Action attempted, CancellationToken ct,bool continueLive=false, Func<(CancellationToken Wake,Func<CancellationToken,Task<JournalPosition?>> Rewind)>? journalReplay=null) =>
        ReadAsync(pair,settings,events,paths,queue,local,dropbox,store,captured,attempted,ct,continueLive,journalReplay);

    // Evidence belongs to one uninterrupted reader interval, never to the queue or the durable cursor.
    private sealed class ReadEvidence
    {
        private readonly CancellationToken lifetime;
        private readonly Func<(JournalPosition? Local,string? Dropbox),bool> covers;
        public ReadEvidence(CancellationToken lifetime){this.lifetime=lifetime;covers=Covers;}
        private JournalPosition? projected;
        private string? cursor;
        private bool wholeLocal, closed;
        public ReadEvidence Restart(Side lost)
        {
            lock(this)
            {
                closed=true;
                return new(lifetime){cursor=lost==Side.Local?cursor:null,projected=lost==Side.Dropbox?projected:null,wholeLocal=lost==Side.Dropbox && wholeLocal};
            }
        }
        public void Close(){lock(this) closed=true;}
        public bool Register(Side side,ChangePage page,ref bool registered)
        {
            lock(this)
            {
                if(registered || closed) return false;
                var changed=NeedsRegistration(side,page);
                if(side==Side.Local){projected=page.ProjectedThrough;wholeLocal=projected!=null && projected==page.Position;}
                else cursor=page.Cursor;
                Volatile.Write(ref registered,true);
                return changed && wholeLocal && !string.IsNullOrEmpty(cursor);
            }
        }
        public bool NeedsRegistration(Side side,ChangePage page)
        {lock(this) return side==Side.Local ? (projected!=page.ProjectedThrough || wholeLocal!=(page.ProjectedThrough!=null && page.ProjectedThrough==page.Position)) : cursor!=page.Cursor;}
        public Func<(JournalPosition? Local,string? Dropbox),bool>? ForRemoval()
        {lock(this) return !closed && !lifetime.IsCancellationRequested && wholeLocal && !string.IsNullOrEmpty(cursor) ? covers : null;}
        private bool Covers((JournalPosition? Local,string? Dropbox) fence)
        {
            lock(this) return !closed && !lifetime.IsCancellationRequested && wholeLocal && projected is {} at && fence.Local is {} required &&
                at.JournalId==required.JournalId && at.NextUsn>=required.NextUsn && !string.IsNullOrEmpty(fence.Dropbox) && fence.Dropbox==cursor;
        }
    }

    private static async Task ReadAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, (JournalPosition? Local,string? Dropbox)? captured, Action? attempted, CancellationToken ct,bool continueLive=false, Func<(CancellationToken Wake,Func<CancellationToken,Task<JournalPosition?>> Rewind)>? journalReplay=null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var evidence=new ReadEvidence(stop.Token);
        var proofNow=queue.ProofNow=()=>Volatile.Read(ref evidence).ForRemoval();
        int liveReset=0,firstSourceDone=0;
        var finite=continueLive ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        var pending=new HashSet<Task>();
        Exception? failure=null;
        void RestartEvidence(Side lost) {lock(pending) Volatile.Write(ref evidence,evidence.Restart(lost));}
        async Task Intake()
        {
            try { await Task.WhenAll(ReadSide(local),ReadSide(dropbox)); }
            catch(OperationCanceledException) when(Volatile.Read(ref liveReset)!=0 && !ct.IsCancellationRequested) { }
        }
        try {await FinishIntakeAsync(Intake(),()=>{if(!continueLive) attempted?.Invoke();},DrainAsync);}
        finally {evidence.Close();}
        async Task ReadSide(ISide side)
        {
            var token=stop.Token;
            try
            {
                var cursor = side.Side == Side.Dropbox ? await store.CursorAsync(pair.PairId,token) : null;
                var position = side.Side == Side.Local && pair.Roots?.LocalVolumeKey() is {} volume ? await store.PositionAsync(volume,token) : null;
                var through=captured;
                if (through is {} target && (side.Side==Side.Dropbox ? target.Dropbox==null : target.Local==null) && !await BeginLive()) return;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var replay=side.Side==Side.Local && through==null ? journalReplay?.Invoke() : null;
                    using var acquisition=replay is { Wake.CanBeCanceled:true } armed ? CancellationTokenSource.CreateLinkedTokenSource(token,armed.Wake) : null;
                    ChangePage page;
                    try
                    {
                        var readToken=acquisition?.Token ?? token;
                        readToken.ThrowIfCancellationRequested();
                        page=await ReadPageAsync(t=>through is {} end ? side.ReadChangesAsync(cursor,position,(end.Dropbox,end.Local),t) : side.ReadChangesAsync(cursor,position,t),side.Side,settings,events,readToken);
                    }
                    catch(OperationCanceledException) when(!token.IsCancellationRequested && replay is {} wake && wake.Wake.IsCancellationRequested)
                    {
                        // Only acquisition yields; prior effects and the Dropbox feed retain their own lifetimes.
                        RestartEvidence(Side.Local);
                        position=await wake.Rewind(token) ?? position;
                        continue;
                    }
                    if (page.ContinuityLost)
                    {
                        RestartEvidence(side.Side);
                        await queue.LoseCoverage(pair,side.Side,page,store,ct);
                        (cursor,position)=(page.Cursor,page.Position);
                        if(through is {} previous)
                        {
                            // A reset is not read-through. Capture the new finite target before resuming from its verified base.
                            var fresh=await side.BoundaryAsync(token);
                            through=side.Side==Side.Local ? (fresh.Position,previous.Dropbox) : (previous.Local,fresh.Cursor);
                            if((side.Side==Side.Local ? fresh.Position==null : fresh.Cursor==null) && !await BeginLive()) break;
                        }
                        else
                        {
                            // Held callbacks can need later input. Only yield a continuous reader at a drained reset.
                            lock(pending)
                            {
                                if(pending.Count!=0) continue;
                                Volatile.Write(ref liveReset,1);
                            }
                            stop.Cancel();break;
                        }
                        continue;
                    }
                    if (page.Changes.Count!=0 || page.Cursor!=cursor || page.Position!=position || Volatile.Read(ref evidence).NeedsRegistration(side.Side,page))
                    {
                        Task work;
                        lock(pending)
                        {
                            if(liveReset!=0) break;
                            work=Settle(page,evidence);
                            pending.Add(work);
                        }
                        _ = RetireAsync(work);
                    }
                    (cursor,position)=(page.Cursor,page.Position);
                    if (through!=null && page.CaughtUp && !await BeginLive()) break;
                }
                async Task<bool> BeginLive()
                {
                    if(finite==null) return false;
                    through=null;
                    if(Interlocked.Exchange(ref firstSourceDone,1)!=0) {attempted!();finite.TrySetResult();}
                    await finite.Task.WaitAsync(token);return true;
                }
            }
            catch(OperationCanceledException) when(Volatile.Read(ref liveReset)!=0 && !ct.IsCancellationRequested) {throw;}
            catch(Exception error) {Abort(error);throw;}
            async Task Settle(ChangePage page,ReadEvidence interval)
            {
                try { await ApplyRegisteredPage(pair,settings,events,paths,queue,side.Side,page,local,dropbox,store,token,interval,proofNow); }
                catch(Exception error) {Abort(error);throw;}
            }
        }
        void Abort(Exception error)
        {
            // Save the failing source or effect before cancellation wakes its siblings.
            lock(pending) failure??=error;
            stop.Cancel();
        }
        async Task RetireAsync(Task work)
        {
            try {await work;}
            catch { /* Settle recorded the failure before cancelling intake. */ }
            finally {lock(pending) pending.Remove(work);}
        }
        async Task DrainAsync()
        {
            Task[] remaining; lock(pending) remaining=pending.ToArray();
            try {await Task.WhenAll(remaining);}
            finally {lock(pending) if(failure!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();}
        }
    }

    /// <summary>Both sources have had their opportunities when intake returns. The outstanding work may still be held.</summary>
    public static async Task FinishIntakeAsync(Task intake,Action attempted,Func<Task> drain)
    {
        try {await intake; attempted();}
        finally {await drain();}
    }

    /// <summary>Retry only acquisition of this source page. The caller's continuation is unchanged until it returns.</summary>
    public static async Task<ChangePage> ReadPageAsync(Func<CancellationToken,Task<ChangePage>> read,Side source,EngineSettings settings,Action<RunEvent> events,CancellationToken ct)
    {
        if(source==Side.Local) return await read(ct); // The volume reader owns restart from its committed prefix.
        int consecutiveFailures=0;
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            try {return await read(ct);}
            catch(Exception error) when(!ct.IsCancellationRequested && (error is IOException or ItemFailedException {Disposition:FailureDisposition.TemporaryNoEffect}))
            {
                TimeSpan wait;
                if(error is ItemFailedException {ProviderWait:{} floor} provider && floor>TimeSpan.Zero)
                {
                    wait=provider.ProviderWaitStarted is {} start ? floor-TimeProvider.System.GetElapsedTime(start) : floor;
                    if(wait<TimeSpan.Zero) wait=TimeSpan.Zero; // An elapsed positive floor is not an unstated wait.
                }
                else
                {
                    wait=settings.Backoff.RetryDelay(consecutiveFailures);
                    if(consecutiveFailures<int.MaxValue) ++consecutiveFailures;
                }
                events(new(RunEventKind.Wait,nameof(ISide.ReadChangesAsync),"",Text.Get(TextKey.WaitSourceRetry,(TextArg.Seconds,wait.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))),""){Side=source,Cause=TextKey.WaitSourceRetry,Error=error});
                await settings.Backoff.WaitAsync(wait,TimeProvider.System,ct);
            }
        }
    }

    public static Task ApplyPageAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, Side source, ChangePage page, ISide local, ISide dropbox, IStore store, CancellationToken ct) =>
        ApplyRegisteredPage(pair,settings,events,paths,queue,source,page,local,dropbox,store,ct,null);

    private static async Task ApplyRegisteredPage(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, Side source, ChangePage page, ISide local, ISide dropbox, IStore store, CancellationToken ct,ReadEvidence? interval,Func<Func<(JournalPosition? Local,string? Dropbox),bool>?>? currentProof=null)
    {
        if (page.ContinuityLost) { await queue.LoseCoverage(pair,source,page,store,ct); return; }
        Task Populate(Scope scope,Side side,Recovery.Folder folder,CancellationToken token) => PopulateAsync(pair,settings,events,paths,scope,side,folder,local,dropbox,store,token);
        Task Hop(SyncedEntry binding,Scope before,Scope? after,CancellationToken token) => Repair.RunAsync(pair,settings,events,paths,queue,local,dropbox,store,before,(binding,before,after),token);
        bool registered=false, gained=false;
        void Registered()
        {
            if(interval==null || Volatile.Read(ref registered)) return;
            if(interval.Register(source,page,ref registered)) gained=true;
        }
        async Task Apply(Change change,CancellationToken token)
        {
            Registered(); // WorkQueue registers the whole page before invoking any callback.
            try {await ItemSync.ApplyAsync(pair,settings,events,queue.CurrentScope(paths),change,local,dropbox,store,Populate,Hop,token,queue.Held,(_,reproducible)=>{if(!reproducible) throw new WorkQueue.Unsettled();},coversThrough:currentProof?.Invoke());}
            catch(ChangedUnderneathException e) when(!PathNames.Same(e.Path,pair.LocalRoot) && !PathNames.Same(e.Path,pair.DropboxRoot))
            {
                events(new(RunEventKind.Wait,nameof(ItemSync),e.Path,string.Empty,nameof(TextKey.FailChanged)){Cause=TextKey.FailChanged,Error=e});
                throw new WorkQueue.Unsettled();
            }
        }
        var settlement=queue.AdmitAsync(pair,page,Apply,
            source==Side.Local ? local.SettleJournalAsync : null,
            source==Side.Dropbox ? (cursor,token)=>store.SaveCursorAsync(pair.PairId,cursor,token) : null,
            source==Side.Dropbox ? (change,token)=>queue.Unfinished.CheckAsync(pair,settings,events,paths,change,local,dropbox,store,queue.Held,Populate,Hop,token) : null,ct);
        if(!settlement.IsFaulted && !settlement.IsCanceled) Registered(); // Empty and prerequisite-blocked pages have no immediate callback.
        if(gained)
        {
            if(await store.ArmedAsync(pair.PairId,ct)!=null) await queue.Unfinished.RecoverStageAsync(pair,settings,events,queue.CurrentScope(paths),local,dropbox,store,queue.Held,Populate,Hop,ct,null);
            await Task.WhenAll(settlement,queue.RetryHeldAsync(null,ct));
        }
        else await settlement;
    }

    /// <summary>Use the acquired source listing, never a fresh read of its reusable name.</summary>
    public static async Task PopulateAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Scope scope, Side source, Recovery.Folder folder, ISide local, ISide dropbox, IStore store, CancellationToken ct)
    {
        var target = source==Side.Local ? dropbox : local;
        var destination = target.Side==Side.Local ? scope.LocalPath : scope.DropboxPath;
        var scan = Guid.NewGuid();
        store.BeginScan(pair.PairId,scan);
        try
        {
            await Task.WhenAll(store.AddScanAsync(pair.PairId,scan,source,folder.Contents,null,ct),
                store.AddScanAsync(pair.PairId,scan,target.Side,target.ReadAllAsync(destination,new(true,false),ct),null,ct));
            await foreach (var (row,_) in store.CompareAsync(pair.PairId,scan,scope,(_,_) => (true,false),ct))
            {
                var entry=row.EntryOn(source); if (entry==null || entry.Skip!=null || !PathNames.IsSelfOrUnder(entry.Path,folder.Root.Path) || PathNames.Same(entry.Path,folder.Root.Path)) continue;
                var path=PathNames.Child(destination,entry.Path[(folder.Root.Path.Length+1)..]);
                if(store.ScanTouched(pair.PairId,scan,source,entry.Path,false) || store.ScanTouched(pair.PairId,scan,target.Side,path,false)) continue;
                if(paths.Excludes(source==Side.Local?entry.Path:path,source==Side.Dropbox?entry.Path:path,entry.IsFolder)) continue;
                await ItemSync.ApplyAsync(pair,settings,events,paths,row with {Context=row.Context with {Destination=path}},[],local,dropbox,store,
                    (_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>throw new InvalidOperationException(nameof(PopulateAsync)),ct,addOnly:true);
            }
        }
        finally { await store.ClearScanAsync(pair.PairId,scan,ct); }
    }
}
