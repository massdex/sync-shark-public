namespace Massdex.SyncShark.Engine.Run;

/// <summary>One AutoSync read: both sources' intake, their settling pages and the evidence interval they share.</summary>
internal sealed class AutoSyncRead
{
    private readonly PairConfig pair; private readonly EngineSettings settings; private readonly Action<RunEvent> events; private readonly PathScope paths;
    private readonly WorkQueue queue; private readonly ISide local, dropbox; private readonly IStore store;
    private readonly (JournalPosition? Local,string? Dropbox)? captured; private readonly Action? attempted; private readonly CancellationToken ct;
    private readonly Func<(CancellationToken Wake,Func<CancellationToken,Task<JournalPosition?>> Rewind)>? journalReplay;
    private readonly CancellationTokenSource stop;
    private readonly Func<Func<(JournalPosition? Local,string? Dropbox),bool>?> proofNow;
    private readonly TaskCompletionSource? finite;
    private readonly HashSet<Task> pending=new();
    private AutoSync.ReadEvidence evidence;
    private int liveReset, firstSourceDone;
    private Exception? failure;

    internal AutoSyncRead(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, (JournalPosition? Local,string? Dropbox)? captured, Action? attempted, CancellationToken ct,bool continueLive, Func<(CancellationToken Wake,Func<CancellationToken,Task<JournalPosition?>> Rewind)>? journalReplay)
    {
        (this.pair,this.settings,this.events,this.paths,this.queue,this.local,this.dropbox,this.store)=(pair,settings,events,paths,queue,local,dropbox,store);
        (this.captured,this.attempted,this.ct,this.journalReplay)=(captured,attempted,ct,journalReplay);
        stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        evidence=new(stop.Token);
        proofNow=()=>Volatile.Read(ref evidence).ForRemoval();
        finite=continueLive ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
    }

    internal async Task RunAsync()
    {
        using(stop)
        {
            queue.ProofNow=proofNow;
            try {await AutoSync.FinishIntakeAsync(Intake(),()=>{if(finite==null) attempted?.Invoke();},DrainAsync);}
            finally {evidence.Close();}
        }
    }

    private void RestartEvidence(Side lost) {lock(pending) Volatile.Write(ref evidence,evidence.Restart(lost));}
    private async Task Intake()
    {
        try { await Task.WhenAll(new Source(this,local).ReadAsync(),new Source(this,dropbox).ReadAsync()); }
        catch(OperationCanceledException) when(Volatile.Read(ref liveReset)!=0 && !ct.IsCancellationRequested) { }
    }
    private void Abort(Exception error)
    {
        // Save the failing source or effect before cancellation wakes its siblings.
        lock(pending) failure??=error;
        stop.Cancel();
    }
    private async Task RetireAsync(Task work)
    {
        try {await work;}
        catch { /* Settle recorded the failure before cancelling intake. */ }
        finally {lock(pending) pending.Remove(work);}
    }
    private async Task DrainAsync()
    {
        Task[] remaining; lock(pending) remaining=pending.ToArray();
        try {await Task.WhenAll(remaining);}
        finally {lock(pending) if(failure!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();}
    }

    /// <summary>One source's reader: its position, cursor and finite target.</summary>
    private sealed class Source
    {
        private readonly AutoSyncRead read; private readonly ISide side; private readonly CancellationToken token;
        private string? cursor;
        private JournalPosition? position;
        private (JournalPosition? Local,string? Dropbox)? through;
        internal Source(AutoSyncRead read,ISide side){this.read=read;this.side=side;token=read.stop.Token;}

        internal async Task ReadAsync()
        {
            try
            {
                cursor = side.Side == Side.Dropbox ? await read.store.CursorAsync(read.pair.PairId,token) : null;
                position = side.Side == Side.Local && read.pair.Roots?.LocalVolumeKey() is {} volume ? await read.store.PositionAsync(volume,token) : null;
                through=read.captured;
                if (through is {} target && (side.Side==Side.Dropbox ? target.Dropbox==null : target.Local==null) && !await BeginLive()) return;
                // Suspending reads stay in this loop so a page costs no extra Task layer.
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var replay=side.Side==Side.Local && through==null ? read.journalReplay?.Invoke() : null;
                    using var acquisition=replay is { Wake.CanBeCanceled:true } armed ? CancellationTokenSource.CreateLinkedTokenSource(token,armed.Wake) : null;
                    ChangePage page;
                    try
                    {
                        var readToken=acquisition?.Token ?? token;
                        readToken.ThrowIfCancellationRequested();
                        page=await AutoSync.ReadPageAsync(t=>through is {} end ? side.ReadChangesAsync(cursor,position,(end.Dropbox,end.Local),t) : side.ReadChangesAsync(cursor,position,t),side.Side,read.settings,read.events,readToken);
                    }
                    catch(OperationCanceledException) when(!token.IsCancellationRequested && replay is {} wake && wake.Wake.IsCancellationRequested)
                    {
                        // Only acquisition yields; prior effects and the Dropbox feed retain their own lifetimes.
                        read.RestartEvidence(Side.Local);
                        position=await wake.Rewind(token) ?? position;
                        continue;
                    }
                    if (page.ContinuityLost) { if(await ResetAsync(page)) continue; break; }
                    if (!Register(page)) break;
                    if (through!=null && page.CaughtUp && !await BeginLive()) break;
                }
            }
            catch(OperationCanceledException) when(Volatile.Read(ref read.liveReset)!=0 && !read.ct.IsCancellationRequested) {throw;}
            catch(Exception error) {read.Abort(error);throw;}
        }

        /// <summary>False when a drained live reset already stopped intake.</summary>
        private bool Register(ChangePage page)
        {
            if (page.Changes.Count!=0 || page.Cursor!=cursor || page.Position!=position || Volatile.Read(ref read.evidence).NeedsRegistration(side.Side,page))
            {
                Task work;
                lock(read.pending)
                {
                    if(read.liveReset!=0) return false;
                    work=Settle(page,read.evidence);
                    read.pending.Add(work);
                }
                _ = read.RetireAsync(work);
            }
            (cursor,position)=(page.Cursor,page.Position);
            return true;
        }

        private async Task<bool> ResetAsync(ChangePage page)
        {
            read.RestartEvidence(side.Side);
            await read.queue.LoseCoverage(read.pair,side.Side,page,read.store,read.ct);
            (cursor,position)=(page.Cursor,page.Position);
            if(through is {} previous)
            {
                // A reset is not read-through. Capture the new finite target before resuming from its verified base.
                var fresh=await side.BoundaryAsync(token);
                through=side.Side==Side.Local ? (fresh.Position,previous.Dropbox) : (previous.Local,fresh.Cursor);
                return !((side.Side==Side.Local ? fresh.Position==null : fresh.Cursor==null) && !await BeginLive());
            }
            // Held callbacks can need later input. Only yield a continuous reader at a drained reset.
            lock(read.pending)
            {
                if(read.pending.Count!=0) return true;
                Volatile.Write(ref read.liveReset,1);
            }
            read.stop.Cancel();
            return false;
        }

        private async Task<bool> BeginLive()
        {
            if(read.finite==null) return false;
            through=null;
            if(Interlocked.Exchange(ref read.firstSourceDone,1)!=0) {read.attempted!();read.finite.TrySetResult();}
            await read.finite.Task.WaitAsync(token);return true;
        }

        private async Task Settle(ChangePage page,AutoSync.ReadEvidence interval)
        {
            try { await AutoSync.ApplyRegisteredPage(read.pair,read.settings,read.events,read.paths,read.queue,side.Side,page,read.local,read.dropbox,read.store,token,interval,read.proofNow); }
            catch(Exception error) {read.Abort(error);throw;}
        }
    }
}
