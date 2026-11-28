using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Feeds each source independently; its read position need not be its settled position.</summary>
public static class AutoSync
{
    public static Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, CancellationToken ct, Func<(CancellationToken Wake,Func<CancellationToken,Task<JournalPosition?>> Rewind)>? journalReplay=null) =>
        ReadAsync(pair,settings,events,paths,queue,local,dropbox,store,null,null,ct,journalReplay:journalReplay);

    public static Task ReadThroughAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, (JournalPosition? Local,string? Dropbox) captured, Action attempted, CancellationToken ct,bool continueLive=false, Func<(CancellationToken Wake,Func<CancellationToken,Task<JournalPosition?>> Rewind)>? journalReplay=null, Action<Exception>? aborted=null) =>
        ReadAsync(pair,settings,events,paths,queue,local,dropbox,store,captured,attempted,ct,continueLive,journalReplay,aborted);

    // Evidence belongs to one uninterrupted reader interval, never to the queue or the durable cursor.
    internal sealed class ReadEvidence
    {
        private readonly object gate=new();
        private readonly CancellationToken lifetime;
        private readonly Func<(JournalPosition? Local,string? Dropbox),bool> covers;
        public ReadEvidence(CancellationToken lifetime){this.lifetime=lifetime;covers=Covers;}
        private JournalPosition? projected;
        private string? cursor;
        private bool wholeLocal, closed;
        public ReadEvidence Restart(Side lost)
        {
            lock(gate)
            {
                closed=true;
                return new(lifetime){cursor=lost==Side.Local?cursor:null,projected=lost==Side.Dropbox?projected:null,wholeLocal=lost==Side.Dropbox && wholeLocal};
            }
        }
        public void Close(){lock(gate) closed=true;}
        public bool Register(Side side,ChangePage page,ref bool registered)
        {
            lock(gate)
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
        {lock(gate) return side==Side.Local ? (projected!=page.ProjectedThrough || wholeLocal!=(page.ProjectedThrough!=null && page.ProjectedThrough==page.Position)) : cursor!=page.Cursor;}
        public Func<(JournalPosition? Local,string? Dropbox),bool>? ForRemoval()
        {lock(gate) return !closed && !lifetime.IsCancellationRequested && wholeLocal && !string.IsNullOrEmpty(cursor) ? covers : null;}
        private bool Covers((JournalPosition? Local,string? Dropbox) fence)
        {
            lock(gate) return !closed && !lifetime.IsCancellationRequested && wholeLocal && projected is {} at && fence.Local is {} required &&
                at.JournalId==required.JournalId && at.NextUsn>=required.NextUsn && !string.IsNullOrEmpty(fence.Dropbox) && fence.Dropbox==cursor;
        }
    }

    private static async Task ReadAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, (JournalPosition? Local,string? Dropbox)? captured, Action? attempted, CancellationToken ct,bool continueLive=false, Func<(CancellationToken Wake,Func<CancellationToken,Task<JournalPosition?>> Rewind)>? journalReplay=null, Action<Exception>? aborted=null) =>
        await new AutoSyncRead(pair,settings,events,paths,queue,local,dropbox,store,captured,attempted,ct,continueLive,journalReplay,aborted).RunAsync();

    /// <summary>Both sources have had their opportunities when intake returns. The outstanding work may still be held.</summary>
    public static async Task FinishIntakeAsync(Task intake,Action attempted,Func<Task> drain)
    {
        try {await intake; attempted();}
        finally {await drain();}
    }

    /// <summary>Retry only acquisition of this source read (a page, or the Repair's boundary). The caller's continuation is unchanged until it returns.</summary>
    public static async Task<T> ReadPageAsync<T>(Func<CancellationToken,Task<T>> read,Side source,EngineSettings settings,Action<RunEvent> events,CancellationToken ct)
    {
        if(source==Side.Local) return await read(ct); // The volume reader owns restart from its committed prefix.
        int consecutiveFailures=0;
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            try {return await read(ct);}
            catch(Exception error) when(!ct.IsCancellationRequested && (error is IOException or ItemFailedException {Disposition:FailureDisposition.TemporaryNoEffect}))
            {
                var wait=NoEffectWait(error,settings,ref consecutiveFailures);
                events(SourceWait(error,wait) with {Side=source});
                await settings.Backoff.WaitAsync(wait,settings.Clock,ct);
            }
        }
    }

    /// <summary>The logged wait of a source or pair retry: the scheduled delay, the failure, and Dropbox's reply when it sent one.</summary>
    internal static RunEvent SourceWait(Exception error,TimeSpan wait) => new(RunEventKind.Wait,nameof(ISide.ReadChangesAsync),"",
        Text.Get(TextKey.WaitSourceRetry,(TextArg.Seconds,wait.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))),"")
        {Cause=TextKey.WaitSourceRetry,Error=error,Provider=(error as ItemFailedException)?.Provider};
    // One wait rule for a proved no-effect refusal: the provider's remaining wait, else the adopted schedule.
    private static TimeSpan NoEffectWait(Exception error,EngineSettings settings,ref int consecutiveFailures)
    {
        if((error as ItemFailedException)?.WaitLeft(settings.Clock) is {} left) return left;
        var fallback=settings.Backoff.RetryDelay(consecutiveFailures);
        if(consecutiveFailures<int.MaxValue) ++consecutiveFailures;
        return fallback;
    }

    // A proved no-effect refusal parks its input with its wait; the input's page frame retries when the wait ends and every other attempt
    // coalesces until then. Logged here; no notice.
    private static WorkQueue.Unsettled Refused(ItemFailedException error,string path,Side side,EngineSettings settings,Action<RunEvent> events,CancellationToken ct)
    {
        events(RefusalEvent(error,path,side,settings));
        return new WorkQueue.Unsettled{Floor=n=>settings.Backoff.WaitAsync(NoEffectWait(error,settings,ref n),settings.Clock,ct)};
    }
    private static RunEvent RefusalEvent(ItemFailedException error,string path,Side side,EngineSettings settings) => (error.WaitLeft(settings.Clock) is {} left && error.Cause!=TextKey.WaitTooManyWrites
            ? new RunEvent(RunEventKind.Wait,nameof(ItemSync),path,Text.Get(TextKey.WaitTooManyRequests,(TextArg.Seconds,left.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))),string.Empty){Cause=TextKey.WaitTooManyRequests}
            : new RunEvent(RunEventKind.Wait,nameof(ItemSync),path,string.Empty,nameof(TextKey.WaitItemRetry)){Cause=TextKey.WaitItemRetry}) with {Side=side,Error=error,Provider=error.Provider};
    internal static Action<ItemFailedException> JobRefused(EngineSettings settings,Action<RunEvent> events) => e=>events(RefusalEvent(e,e.Path,Side.Dropbox,settings)); // the item refusal's log line; no floor, no exception

    // A page's deleted names join the saved deleted-name history in its cursor's transaction; a page without any keeps the plain save.
    private static Task SaveCursor(IStore store,PairConfig pair,ChangePage page,string cursor,CancellationToken ct)
    {
        var deleted=page.Changes.Where(c=>c.Kind==ChangeKind.Deleted && c.Path!=null).Select(c=>PathNames.Key(c.Path!)).ToArray();
        return deleted.Length==0 ? store.SaveCursorAsync(pair.PairId,cursor,ct) : store.SaveCursorAsync(pair.PairId,cursor,deleted,ct);
    }

    public static Task ApplyPageAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, Side source, ChangePage page, ISide local, ISide dropbox, IStore store, CancellationToken ct) =>
        ApplyRegisteredPage(pair,settings,events,paths,queue,source,page,local,dropbox,store,ct,null);

    internal static async Task ApplyRegisteredPage(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, Side source, ChangePage page, ISide local, ISide dropbox, IStore store, CancellationToken ct,ReadEvidence? interval,Func<Func<(JournalPosition? Local,string? Dropbox),bool>?>? currentProof=null,Action<Exception>? fatal=null)
    {
        var observed=TimeProvider.System.GetTimestamp();
        var peer=PathNames.Other(source);
        var jobRefused=JobRefused(settings,events);
        if (page.ContinuityLost) { await queue.LoseCoverage(pair,source,page,store,ct); return; }
        Task Populate(Scope scope,Side side,Recovery.Folder folder,CancellationToken token) => PopulateAsync(pair,settings,events,paths,scope,side,folder,local,dropbox,store,token);
        Task Hop(SyncedEntry? binding,Scope before,Scope? after,CancellationToken token) => Repair.RunAsync(pair,settings,events,paths,queue,local,dropbox,store,before,(binding,before,after),token);
        bool registered=false, gained=false;
        void Registered()
        {
            if(interval==null || Volatile.Read(ref registered)) return;
            if(interval.Register(source,page,ref registered)) gained=true;
        }
        async Task Apply(Change change,CancellationToken token)
        {
            Registered(); // WorkQueue registers the whole page before invoking any callback.
            long? completed=null; bool failed=false; Notice? refusal=null;
            void Observe(RunEvent e)
            {
                events(e);
                if(e.Kind!=RunEventKind.Effect || !RunLog.IsCommittedEffect(e.Outcome)) return;
                if(e.Side==peer || e.Side==null && (e.Action==SyncAction.CopyOn(false,peer) || e.Action==SyncAction.CopyOn(true,peer) || e.Action==SyncAction.DeleteOn(peer)))
                    completed=TimeProvider.System.GetTimestamp();
            }
            void Failed(string key,bool reproducible)
            {
                failed=true;
                if(reproducible) return;
                events(new(RunEventKind.Wait,nameof(ItemSync),key,string.Empty,nameof(TextKey.WaitItemRetry)){Cause=TextKey.WaitItemRetry});
                throw new WorkQueue.Unsettled();
            }
            try
            {
                try { await ItemSync.ApplyAsync(pair,settings,Observe,queue.CurrentScope(paths),change,local,dropbox,store,Populate,Hop,token,queue.Held,Failed,coversThrough:currentProof?.Invoke(),failedNotice:n=>{ if(n.Cause==TextKey.WaitItemRetry && change is {Side:Side.Dropbox,Kind:ChangeKind.Deleted,Id:null}) refusal=n; }); }
                // Saved inside this claimed attempt and inside the outer try, so a store fault reaches the fatal catch at once.
                catch(WorkQueue.Unsettled) when(refusal is {} r) { await new Notices().RaiseAsync(store,pair.PairId,r,token); throw; }
                if(!failed && completed is {} end)
                    events(new(RunEventKind.Stage,nameof(ItemSync),change.Path??change.Observed?.Path??string.Empty,Text.Get(TextKey.ChangeLatency,(TextArg.Elapsed,TimeProvider.System.GetElapsedTime(observed,end).ToString())),string.Empty){Side=source,Ended=true});
            }
            catch(ChangedUnderneathException e) when(!PathNames.Same(e.Path,pair.LocalRoot) && !PathNames.Same(e.Path,pair.DropboxRoot))
            {
                events(new(RunEventKind.Wait,nameof(ItemSync),e.Path,string.Empty,nameof(TextKey.FailChanged)){Cause=TextKey.FailChanged,Error=e});
                throw new WorkQueue.Unsettled();
            }
            catch(ItemFailedException e) when(e.Disposition==FailureDisposition.TemporaryNoEffect) { throw Refused(e,change.Path??change.Observed?.Path??string.Empty,source,settings,events,token); }
            catch(Exception e) when(fatal!=null && e is not WorkQueue.Unsettled && !token.IsCancellationRequested) { fatal(e); throw; } // this faults the page anyway; report it now so a sibling's wait cannot hide it
        }
        var settlement=queue.AdmitAsync(pair,page,Apply,
            source==Side.Local ? local.SettleJournalAsync : null,
            source==Side.Dropbox ? (cursor,token)=>SaveCursor(store,pair,page,cursor,token) : null,
            source==Side.Dropbox ? (change,token)=>queue.Unfinished.CheckAsync(pair,settings,events,paths,change,local,dropbox,store,queue.Held,Populate,Hop,token,refused:jobRefused,watched:Rethrown) : null,ct,fatal);
        if(!settlement.IsFaulted && !settlement.IsCanceled) Registered(); // Empty and prerequisite-blocked pages have no immediate callback.
        // The started settlement is joined even when the gained stage fails, so the read cannot drain past it.
        await (gained ? Task.WhenAll(settlement,Gained()) : settlement);
        await WakeOwed(); // debt owed while this page settled (the later-debt probe)
        async Task Gained()
        {
            await Task.WhenAll(Rethrown(WakeOwed()),Rethrown(queue.Unfinished.CheckLiveStageAsync(pair,settings,events,queue.CurrentScope(paths),local,dropbox,store,queue.Held,Populate,Hop,ct,jobRefused,Rethrown)));
            await queue.RetryHeldAsync(null,ct);
        }
        // Each gained task and each covered job reports its own fault at once; every join stays.
        async Task Rethrown(Task work) { try { await work; } catch(Exception e) when(fatal!=null && !ct.IsCancellationRequested) { fatal(e); throw; } }
        // The existing proof-gated owed wake: proof now, and an owed item retry or scope.
        async Task WakeOwed() { if(queue.WakeOwedRepair!=null && currentProof?.Invoke()!=null && await store.ReadRepairAsync(pair.PairId,Side.Dropbox,ct) is (var revision,var reason) &&
            (reason==nameof(TextKey.WaitItemRetry) || (await store.PairAsync(pair.PairId,ct))?.OwedScopes is {Count:>0})) queue.WakeRepairOnce(revision); }
    }

    /// <summary>Use the acquired source listing, never a fresh read of its reusable name.</summary>
    public static async Task PopulateAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Scope scope, Side source, Recovery.Folder folder, ISide local, ISide dropbox, IStore store, CancellationToken ct, Func<Side,string,bool>? unread=null)
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
                if(unread!=null && (unread(source,entry.Path) || unread(target.Side,path))) continue; // a Repair's unread subtree takes no effect
                await RowExecution.ApplyAsync(pair,settings,events,paths,row with {Context=row.Context with {Destination=path}},[],local,dropbox,store,
                    (_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>throw new InvalidOperationException(nameof(PopulateAsync)),ct,addOnly:true);
            }
        }
        finally { await store.ClearScanAsync(pair.PairId,scan,ct); }
    }
}
