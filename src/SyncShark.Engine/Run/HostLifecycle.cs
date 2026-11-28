namespace Massdex.SyncShark.Engine.Run;

/// <summary>Admits, runs, rewinds and drains each host member's pair runner on the host's shared state.</summary>
internal sealed class HostLifecycle(Host host)
{
    internal static string LogFolder => ResourceNames.LogDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal void Launch(HostMember m,IReadOnlyList<PairConfig> pairs)
    {
        if(Volatile.Read(ref m.Closing)!=0) return; // Drain owns a closing member
        var old=m.Opening; old?.Cancel(); // fences the retiring launch's observations before a successor exists
        var opening=CancellationTokenSource.CreateLinkedTokenSource(host.Shutdown.Token);
        lock(m) m.Opening=opening;
        old?.Dispose();
        m.Setup=Task.Run(async ()=>
        {
            var token=opening.Token;
            try
            {
                // Admission uses the composition factory, which verifies current connection membership before any provider call.
                lock(m){m.Local=null!;m.Dropbox=null!;m.Admitted=false;} // no member loop runs during a launch: admission restarts cleared
                await Observe(m,pairs,token,opening,retryRead:m.Pair.Enabled); token.ThrowIfCancellationRequested();
                await Membership(m,false,token); token.ThrowIfCancellationRequested();
                Start(m);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { }
            catch(Exception error) {m.Admitted=false; await Failed(m,error,CancellationToken.None);}
        });
    }
    internal HostMember Attach(PairConfig p)
    {
        var m = new HostMember(p); m.Queue=new(ct=>m.Local.SettledJournalAsync(ct)){Unfinished=m.Runner.Unfinished,ScopeNow=()=>m.Paths,WakeOwedRepair=at=>{if(at!=null) Volatile.Write(ref m.JournalWake,at); WakeRepair(m);}};
        m.Runner.JournalReplay=()=>
        {
            lock(m) return ((m.RepairFinished??=new()).Token,ct=>ReplayJournal(m,ct));
        };
        host.Members[p.PairId] = m;
        return m;
    }
    /// <summary>One pair read and one verdict per observation, published only while <paramref name="owner"/> is the member's lifetime. False when that lifetime was replaced.</summary>
    internal async Task<bool> Observe(HostMember m,IReadOnlyList<PairConfig> pairs,CancellationToken ct,CancellationTokenSource? owner,bool liveRootSignal=false,bool retryRead=false)
    {
        int failures=0; // the verdict is published when this check completes; a live member keeps its last verdict meanwhile
        bool Current() { lock(m) return ReferenceEquals(m.Opening,owner); }
        while(true)
        {
            (NoticeKind Kind,TextKey Cause)? refusal=null;
            try
            {
                var (roots,drive,type)=await ReadRoots(m,ct); var pair=m.Pair;
                lock(m)
                {
                    if(!ReferenceEquals(m.Opening,owner)) return false;
                    lock(host.Members) { m.Type=type; m.Paths=new(pair,host.PublishedPairs ?? pairs,type,host.Settings.DefaultIgnoreRules); }
                    if((refusal=PairRunner.Refusal(pair,roots,drive))!=null) m.Admitted=false; // a known mismatch is published before its notice write
                }
                var admitted=await m.Runner.CheckRootsAsync(pair,refusal,host.Store!,ct);
                lock(m) { if(!ReferenceEquals(m.Opening,owner)) return false; m.Admitted=admitted; } return true;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                var refused=refusal!=null || !retryRead || e is EngineRequestException || liveRootSignal && RootGone(e);
                if(refused) lock(m) if(ReferenceEquals(m.Opening,owner)) m.Admitted=false;
                if(liveRootSignal && e is not (ItemFailedException or EngineRequestException)) await host.Store!.SaveNoticeAsync(m.Pair.PairId,RootNotice(),ct);
                else await Failed(m,e,ct);
                if(refused) return Current();
                await RetryDelay(e,failures,ct);
                if(failures<int.MaxValue) ++failures;
            }
        }
    }
    // A missing or replaced root is a definite refusal: admission fails (RecheckRoots stops the run). Other read faults retry.
    private static bool RootGone(Exception e) => e is ChangedUnderneathException or ItemFailedException {Cause:TextKey.RootLocalIdentityChanged or TextKey.LocalRootMissing};
    private static bool RootLost(HostMember m,Exception? e) => ChangedUnderneathException.RootLost(m.Pair,e);
    private static bool WaitOnly(Exception e) => e is ItemFailedException {Disposition:FailureDisposition.TemporaryNoEffect};
    internal async Task<HostMember> Ready(PairConfig p,CancellationToken ct)
    {
        if (!host.Members.TryGetValue(p.PairId,out var m)) m=Attach(p);
        if(!m.Setup.IsCompleted && !m.Admitted) throw new EngineRequestException(TextKey.RootUnavailable);
        if (m.Stop==null || !m.Admitted) { CancellationTokenSource? o; lock(m) o=m.Opening; await Observe(m,await host.Configurations(ct),ct,o); }
        if (!m.Admitted) throw new EngineRequestException(TextKey.PairAwaitingDecision,(TextArg.Pair,p.PairId)); return m;
    }
    internal async Task<(ISide Local,ISide Dropbox)> Adapters(HostMember m,CancellationToken ct)
    {
        lock(m) if(m.Local is {} l) return (l,m.Dropbox); // both from one locked read
        var s=await host.OpenSides(m.Pair,host.Store!,()=>m.Paths,m.Event,ct);
        lock(m) { ct.ThrowIfCancellationRequested(); if(m.Local==null) (m.Local,m.Dropbox)=s; return (m.Local,m.Dropbox); }
    }
    internal async Task<(RootIdentity? Roots,DriveEvidence? Drive,DriveType Type)> ReadRoots(HostMember m,CancellationToken ct,bool acceptDrive=false)
    {
        var (local,dropbox)=await Adapters(m,ct);
        var a=await local.RootAsync(ct); var p=m.Pair;
        if(a.Drive!=null && DriveEvidence.Change(p.Drive,a.Drive)!=null && !acceptDrive)
            return (null,a.Drive,a.LocalDriveType ?? DriveType.Unknown);
        var b=await dropbox.RootAsync(ct);
        return (a.Local != null && b.Dropbox != null ? new(p.LocalRoot,p.DropboxRoot,p.ConnectionId,a.Local,b.Dropbox) : null,
            a.Drive,a.LocalDriveType ?? DriveType.Unknown);
    }
    internal void Start(HostMember m)
    {
        CancellationTokenSource stop;
        lock(m)
        {
            if (!m.Admitted || !m.Pair.Enabled || host.IsPaused(m.Pair.PairId) || m.Stop != null || host.Stopped.IsCancellationRequested || Volatile.Read(ref m.Closing)!=0) return;
            m.Opening ??= CancellationTokenSource.CreateLinkedTokenSource(host.Shutdown.Token); // a resolve after Drain nulled it
            stop = m.Stop = CancellationTokenSource.CreateLinkedTokenSource(m.Opening.Token);
        }
        m.Live=RunMemberAsync(m,stop); // outside the lock: its synchronous prefix does I/O
    }
    // Boot and failures belong to one run: the catch-up callback and the retry loop share them.
    private async Task RunMemberAsync(HostMember m,CancellationTokenSource stop)
    {
        var boot=NewBoot(); int failures=0;
        TaskCompletionSource<bool> NewBoot() {var next=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);m.Boot=next.Task;return next;}
        void Attempted() { if(!stop.IsCancellationRequested) {Interlocked.Exchange(ref failures,0); boot.TrySetResult(true); WakeRepair(m,stop.Token); ReleaseTrim(m,false);} }
        var ct=stop.Token; var id=m.Pair.PairId; bool caughtUp=false, rewind=false;
        try
        {
            while(!ct.IsCancellationRequested && m.Admitted)
            {
                try
                {
                    if(rewind) {await Rewind(m,ct);if(!m.Admitted) return;rewind=false;WakeRepair(m,ct);} // wakes dropped while Closing are re-armed
                    m.Log??=NewLog(id,out _);
                    if(!caughtUp)
                    {
                        var pc=m.Local.BoundaryAsync(ct);var cloud=m.Dropbox.BoundaryAsync(ct);await Task.WhenAll(pc,cloud);
                        await m.Runner.CatchUpAsync(m.Pair,((await pc).Position,(await cloud).Cursor),host.Settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,host.Store!,Attempted,ct,continueLive:true);
                        caughtUp=true;
                    }
                    if(!await BeginOwedRepair(m,ct)) WakeRepair(m,ct);
                    await ReplayJournal(m,ct);
                    await m.Runner.RunAsync(m.Pair,host.Settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,host.Store!,ct);
                }
                catch(ChangedUnderneathException error) when(!ct.IsCancellationRequested && (PathNames.Same(error.Path,m.Pair.LocalRoot) || PathNames.Same(error.Path,m.Pair.DropboxRoot))) {rewind=true;}
                catch(Exception error) when(!ct.IsCancellationRequested)
                {
                    if(boot.Task.IsCompleted) boot=NewBoot();
                    caughtUp=false;rewind=true;
                    if(error is not ItemFailedException {Disposition:FailureDisposition.TemporaryNoEffect}) await Failed(m,error,ct); // a no-effect refusal is a logged wait, no Failure notice
                    await RetryDelay(error,failures,ct,m);
                    if(failures<int.MaxValue) ++failures;
                }
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        finally {boot.TrySetResult(false);m.Boot=Task.FromResult(false);m.Log?.Dispose();m.Log=null;}
    }
    internal void ReleaseTrim(HostMember m,bool removed)
    {
        if(m.Trim is not {} volume) return;
        lock(volume)
        {
            if(!removed && m.Type!=DriveType.Unknown) volume.Type=m.Type;
            if(!volume.Attempted(m.Pair.PairId) || volume.Type==DriveType.Unknown) return;
            if(host.Trim!=null) volume.Work=TrimAsync();
        }
        async Task TrimAsync()
        {
            var token=host.Shutdown.Token;
            try {await host.Trim!(Path.GetPathRoot(m.Pair.LocalRoot)!,volume.Type,token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested) { }
            catch(Exception error) {await Failed(m,error,token);}
        }
    }
    private async Task<JournalPosition?> ReplayJournal(HostMember m,CancellationToken ct)
    {
        // The Local reader (or the gap between readers) consumes each latched completion once; an unpublished signal is never disposed.
        lock(m) if(m.RepairFinished is {IsCancellationRequested:true}) m.RepairFinished=null;
        if(Volatile.Read(ref m.JournalWake) is not {} wake || (await host.Store!.ReadRepairAsync(m.Pair.PairId,Side.Local,ct)).Reason!=null) return null;
        var floor=await m.Queue.RewindJournalAsync(wake,async ()=>
        {
            await m.Local.SetMembershipAsync(m.Pair,true,false,ct);
            await Membership(m,false,ct);
        },ct);
        Interlocked.CompareExchange(ref m.JournalWake,null,wake);
        return floor;
    }
    private async Task Rewind(HostMember m,CancellationToken ct)
    {
        Interlocked.Increment(ref m.Closing);
        // A running Repair keeps going: AutoSync's failure is not its failure, and only a lost root (RecheckRoots) stops it.
        try {
            await m.Local.SetMembershipAsync(m.Pair,true,false,ct); await RecheckRoots(m,ct,m.Opening); m.Queue.RestartPrefix();
        } finally {Interlocked.Decrement(ref m.Closing);}
    }
    private async Task RecheckRoots(HostMember m,CancellationToken ct,CancellationTokenSource? opening)
    {
        CancellationTokenSource watch;
        lock(m) { if(!ReferenceEquals(m.Opening,opening)) return; watch=CancellationTokenSource.CreateLinkedTokenSource(ct,opening?.Token ?? CancellationToken.None); }
        using(watch) try {
            if(await Observe(m,await host.Configurations(watch.Token),watch.Token,opening,true,retryRead:true))
            { Task published=Task.CompletedTask; lock(m) if(ReferenceEquals(m.Opening,opening)) published=Membership(m,false,watch.Token); await published; }
        }
        finally
        {
            CancellationTokenSource? manual=null,stop=null;
            lock(m) if(!m.Admitted && ReferenceEquals(m.Opening,opening)) { manual=m.ManualStop; stop=m.Stop; } // the lifetime test, not the caller's token, says whose refusal this is
            try { try { manual?.Cancel(); } catch(ObjectDisposedException) { } }
            finally { try { stop?.Cancel(); } catch(ObjectDisposedException) { } }
        }
    }
    /// <summary>Request an owed-work evaluation; a <paramref name="lifetime"/> that is no longer the member's Stop wakes nothing.</summary>
    internal void WakeRepair(HostMember m,CancellationToken? lifetime=null)
    {
        if(!m.Admitted || !m.Pair.Enabled || host.IsPaused(m.Pair.PairId)) return;
        lock(m)
        {
            if(m.Closing!=0 || m.Stop is not {} stop || lifetime is {} life && stop.Token!=life) return;
            var ct=stop.Token; // Stop is disposed only under lock(m), so it is live here
            m.DebtChanged=true;
            if(!m.DebtWake.IsCompleted) return;
            m.DebtWake=Task.Run(async ()=>
            {
                try
                {
                    while(true)
                    {
                        await m.Manual.WaitAsync(ct);
                        lock(m){ct.ThrowIfCancellationRequested(); if(!m.DebtChanged) return; m.DebtChanged=false;}
                        if(!await BeginOwedRepair(m,ct)) lock(m) m.DebtChanged=true; // a Repair ran during the reads: read again after it
                    }
                }
                catch(Exception) when(ct.IsCancellationRequested) { } // cancelled in any form, a store fault raised by retirement included
                catch(Exception error) { Rewake(m,error,ct); } // a defect (debt reads are handled inside): keep the one retry
                finally { bool again; lock(m){ m.DebtWake=Task.CompletedTask; again=m.DebtChanged; } if(again) WakeRepair(m); } // one release on every exit; a latch set meanwhile goes to the current lifetime
            });
        }
    }
    private static bool Full(bool repaired,string? local,string? remote) => !repaired || local!=null || remote!=null;
    /// <summary>Called after any escaped failure of an owed or requested Repair, waits included. Reads the pair's full
    /// obligation now; while it stands, saves the pair's one Info RequiredRepairRetry notice. True when the obligation stands or
    /// cannot be read; false only when it was read as cleared. Only cancellation of ct escapes: a store fault, and a throwing log
    /// sink, are swallowed after one log attempt and never change the answer, so a reporting failure can add a re-wake, never drop one.
    /// It never logs the caller's error and never re-wakes; the caller does both.</summary>
    internal async Task<bool> OwedFailed(HostMember m,CancellationToken ct)
    {
        var owed=true;
        try { var id=m.Pair.PairId; owed=Full((await host.Store!.RepairStateAsync(id,ct)).FirstRepairDone,(await host.Store.ReadRepairAsync(id,Side.Local,ct)).Reason,(await host.Store.ReadRepairAsync(id,Side.Dropbox,ct)).Reason);
              if(owed) await host.Store.SaveNoticeAsync(id,new(0,NoticeKind.Info,TextKey.RequiredRepairRetry,string.Empty,new([],null),host.Clock.GetUtcNow(),false),ct); }
        catch(Exception error) when(!ct.IsCancellationRequested) { try { m.Event(new(RunEventKind.Failed,nameof(IStore.SaveNoticeAsync),string.Empty,error.Message,string.Empty){Error=error}); } catch(Exception) { } }
        return owed;
    }
    private static bool Owns(HostMember m,CancellationToken life) => m.Stop is {IsCancellationRequested:false} stop && stop.Token==life; // under lock(m): only the live issuing lifetime publishes
    // Instants from one clock sample, rounded up (saturating, so an unbounded provider wait stays unbounded); Left is the time to one in whole ms rounded up, at most one timer chunk.
    private long Due(long now,TimeSpan delay) { var ticks=Math.Ceiling(delay.Ticks*(double)host.Clock.TimestampFrequency/TimeSpan.TicksPerSecond); return ticks>=long.MaxValue-now ? long.MaxValue : now+(long)ticks; }
    private TimeSpan Left(long at) { var now=host.Clock.GetTimestamp(); return at<=now ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(Math.Ceiling(host.Clock.GetElapsedTime(now,at).TotalMilliseconds),EngineSettings.BackoffSettings.MaximumTimerMilliseconds)); }
    /// <summary>A failure's request for the member's one automatic Repair retry. True when it armed it or moved it later.</summary>
    internal bool Rewake(HostMember m,Exception error,CancellationToken life)
    {
        lock(m)
        {
            var now=host.Clock.GetTimestamp(); var stated=(error as ItemFailedException)?.WaitLeft(host.Clock,now) is {Ticks:>0} left ? left : (TimeSpan?)null; // an elapsed provider wait is no wait
            if(!Owns(m,life) || stated==null && m.BackoffLifetime==life && m.RetryAt!=null) return false; // while its retry waits, an ordinary failure changes nothing
            var at=Due(now,stated ?? host.Settings.Backoff.RetryDelay(m.RepairFailures));
            if(stated!=null && !(at<=m.FloorAt)) m.FloorAt=at; // the provider's deadline outlives this lifetime
            return Retry(m,life,at,true); // no log write: it would run inside the failing pass's Repair slot
        }
    }
    // The one writer of the retry state (under lock(m), after Owns), never before the pending wake or a live provider floor, and its one
    // waiter, which wakes only once the clock has reached the instant still published.
    private bool Retry(HostMember m,CancellationToken life,long at,bool step)
    {
        if(m.FloorAt>at) at=m.FloorAt.Value;
        if(m.BackoffLifetime==life && m.RetryAt is {} pending) { if(at<=pending) return false; m.RetryAt=at; return true; }
        if(step && m.RepairFailures<int.MaxValue) m.RepairFailures++;
        m.BackoffLifetime=life; m.RetryAt=at;
        _=Task.Run(async ()=>
        {
            try
            {
                for(TimeSpan left;;await Task.Delay(left,host.Clock,life))
                    lock(m) { life.ThrowIfCancellationRequested(); if((left=Left(m.RetryAt!.Value))==TimeSpan.Zero) { m.RetryAt=null; break; } }
                WakeRepair(m,life);
            }
            catch(OperationCanceledException) when(life.IsCancellationRequested) { }
        });
        return true;
    }
    /// <summary>False only when the owed work lost the slot to a Repair that ran during the debt reads.</summary>
    private async Task<bool> BeginOwedRepair(HostMember m,CancellationToken ct)
    {
        var id=m.Pair.PairId; Task slot; lock(m) slot=m.Manual; var free=slot.IsCompleted; var full=false; IReadOnlyList<Scope>? scopes=null;
        try
        {
            var state=await host.Store!.RepairStateAsync(id,ct);
            var local=await host.Store.ReadRepairAsync(id,Side.Local,ct); var remote=await host.Store.ReadRepairAsync(id,Side.Dropbox,ct);
            scopes=(await host.Store.PairAsync(id,ct))?.OwedScopes; full=Full(state.FirstRepairDone,local.Reason,remote.Reason);
            if(Volatile.Read(ref m.DebtUnread)) { m.DebtUnread=false; await foreach(var n in host.Store.NoticesAtAsync(id,PathNames.KeyFor(Side.Local,string.Empty),ct)) if(n is {Kind:NoticeKind.Failure,Cause:TextKey.StateReadFailed}) await host.Store.CloseNoticeAsync(id,n,ct); } // the reads work: close a read fault's notice
            lock(m)
            {
                if(!Owns(m,ct) || !ReferenceEquals(m.Manual,slot)) { ct.ThrowIfCancellationRequested(); return false; } // one section (m -> queue.gate): a Repair ran during the reads: read again after it
                if(local.Reason!=null) m.Queue.LoseCoverage(Side.Local);
                if(remote.Reason!=null) m.Queue.LoseCoverage(Side.Dropbox);
                if(!full && scopes is not {Count:>0}) return true;
                if(m.FloorAt>host.Clock.GetTimestamp()) { Retry(m,ct,m.FloorAt.Value,false); return true; } // no automatic owed pass before a live provider floor
                return ClaimRepair(m,ct,Run,false)!=null;
            }
        }
        catch(Exception error) when(!ct.IsCancellationRequested)
        {
            // The one debt-read fault handler (start-up and dispatcher): the retry first; then, only if no Repair ran since the reads began, the read fault's own notice and the Info.
            Rewake(m,error,ct); bool fresh; lock(m) fresh=free && Owns(m,ct) && ReferenceEquals(m.Manual,slot);
            if(fresh) { m.DebtUnread=true; await Failed(m,error is ItemFailedException or EngineRequestException ? error : new ItemFailedException(string.Empty,TextKey.StateReadFailed,error),ct); await OwedFailed(m,ct); }
        }
        return true;
        async Task Run(CancellationToken token)
        {
            // One scope's known acquisition failure stays in its own run; cancellation, pair-root loss and other faults still propagate.
            async Task Isolated(Scope? scope,Func<Task> work)
            {
                try { await work(); }
                catch(InventoryIncompleteException error) when(!RootLost(m,error)) { Rewake(m,error,ct); await Failed(m,error,token); }
                catch(ItemFailedException error) when(scope!=null && (WaitOnly(error) || InScope(scope,error.Path)) && !RootLost(m,error)) { if(WaitOnly(error)) Rewake(m,error,ct); else await Failed(m,error,token); } // a point-root wait keeps its provider wait; any other item failure is an ordinary Failure
            }
            if(full)
            {
                try { await ManualRepair(m,token); lock(m) if(Owns(m,ct)) m.RepairFailures=0; }
                catch(Exception error) when(!token.IsCancellationRequested && !RootLost(m,error)) // a lost pair root at any depth leaves Run for ClaimRepair's recheck
                {
                    if(await OwedFailed(m,ct)) Rewake(m,error,ct); // Info while the full debt stands, then the one retry; neither throws except on Stop
                    if(error is InventoryIncompleteException) try { m.Event(new(RunEventKind.Failed,nameof(Repair),string.Empty,RunLog.FailureTag(error),string.Empty)); } catch(Exception) { } // the one log line, after the retry; a broken sink never replaces the pass's error
                    if(!WaitOnly(error) && error is not InventoryIncompleteException) throw; // a wait is never a Failure; an incomplete walk goes on to the owed scopes
                }
            }
            // A full run neither lists nor retires owed scopes; run those still owed now, including any it owed itself.
            var stillOwed = full ? (await host.Store!.PairAsync(id,token))?.OwedScopes ?? [] : scopes ?? [];
            var served=new HashSet<Scope>(); var caught=m.CaughtUp;
            foreach(var scope in stillOwed)
            {
                served.Add(scope);
                // A pending no-keep exclusion is finished by its own owner, never by a scoped comparison; a fault leaves it owed for the next run.
                if(NoKeepRoot(m.Pair,scope) is {} exclusion) { await Isolated(scope,()=>FinishExclusion(m,scope,exclusion,token)); continue; }
                await Isolated(scope,()=>Repair.RunAsync(m.Pair,host.Settings,m.Event,m.Paths,m.Queue,m.Local,m.Dropbox,host.Store!,scope,null,token,caughtUp:caught));
            }
            await HandOff(m,served,token,ct);
        }
    }
    /// <summary>The one owner of a confirmed no-keep apply, run under ClaimRepair. Its owed scope retires only at the revision read before the walk.</summary>
    internal async Task<(ExclusionOutcome Outcome,TextKey? Cause)> FinishExclusion(HostMember m,Scope scope,Exclusion exclusion,CancellationToken token)
    {
        var id=m.Pair.PairId; var revision=(await host.Store!.ReadRepairAsync(id,Side.Dropbox,token)).Revision;
        if(await host.Store!.PairAsync(id,token) is not {} pair || !pair.Exclusions.Contains(exclusion) || pair.OwedScopes?.Contains(scope)!=true) return (ExclusionOutcome.Refused,TextKey.PairChangedDuringRequest); // only the rule published now, at a root still owed, authorizes a removal
        var applied=await new Executor.Exclusions().ApplyAsync(pair,host.Settings,m.Event,m.Paths,exclusion,m.Local,m.Dropbox,host.Store!,
            (s,side,f,t)=>host.Populate(m,s,side,f,t),(b,before,after,t)=>host.Compare(m,b,before,after,t),token,m.Queue.Held,m.Queue.CoversHop);
        if(applied.Outcome==ExclusionOutcome.Applied) await host.Store!.FinishRepairAsync(id,scope,revision,token);
        return applied;
    }
    // The owed footprint is a no-keep root when its path on the rule's side is exactly that rule's path.
    private static Exclusion? NoKeepRoot(PairConfig pair,Scope scope) =>
        pair.Exclusions.FirstOrDefault(e => !e.Keep && PathNames.Same(e.Path,e.Side==Side.Local ? scope.LocalPath : scope.DropboxPath));
    private static bool InScope(Scope scope,string path) =>
        scope.LocalPath.Length==0 || PathNames.IsSelfOrUnder(path,scope.LocalPath) || scope.DropboxPath.Length==0 || PathNames.IsSelfOrUnder(path,scope.DropboxPath);
    /// <summary>Re-arm the existing wake only for a scope committed after the dispatch snapshot; a retained or re-issued one does not.
    /// A run's <paramref name="lifetime"/> wakes nothing once replaced.</summary>
    internal async Task HandOff(HostMember m,ISet<Scope>? served,CancellationToken ct,CancellationToken? lifetime=null)
    {
        if((await host.Store!.PairAsync(m.Pair.PairId,ct))?.OwedScopes is not {} owed) return;
        for(int i=0;i<owed.Count;i++) if(served==null || !served.Contains(owed[i])) { WakeRepair(m,lifetime); return; }
    }
    /// <summary>The one Repair slot. Null when busy for a background caller, which keeps its own request; an explicit request throws instead.</summary>
    internal Task? ClaimRepair(HostMember m,CancellationToken ct,Func<CancellationToken,Task> work,bool explicitRequest)
    {
        lock(m)
        {
            if(m.Closing!=0) return explicitRequest ? throw new OperationCanceledException(ct) : Task.CompletedTask; // a background wake dropped while Closing is re-armed after Rewind
            if(!m.Manual.IsCompleted)
            {
                if(explicitRequest) throw new EngineRequestException(TextKey.RepairAlreadyRunning,(TextArg.Pair,m.Pair.PairId));
                return null; // busy: a background caller keeps its own request
            }
            var opening=m.Opening;
            m.ManualStop?.Dispose(); m.ManualStop=CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token=m.ManualStop.Token; var run=Task.Run(async ()=>
            {
                var active=new RunEvent(RunEventKind.Stage,nameof(Repair),string.Empty,string.Empty,string.Empty){Outcome=RunLog.InProgressOutcome};
                m.Event(active); // the slot's live entry; in-progress records are never stored
                try
                {
                    try { await work(token); }
                    catch(Exception error) when(!token.IsCancellationRequested && RootLost(m,error))
                    {
                        try { await RecheckRoots(m,token,opening); } catch(Exception) when(explicitRequest) { } // the caller gets the root error, not its report's
                        if(explicitRequest) throw;
                    }
                    catch(Exception error) when(explicitRequest) { try { m.Event(new(RunEventKind.Failed,nameof(Repair),string.Empty,RunLog.FailureTag(error),string.Empty){Provider=(error as ItemFailedException)?.Provider}); } catch(Exception) { } throw; } // the command's one attribution; a throwing sink never replaces its error
                    catch(Exception error) when(!token.IsCancellationRequested) { Rewake(m,error,ct); await Failed(m,error,token); } // an escaped automatic pass fault keeps the one retry; a command's ct owns none
                }
                finally { m.Event(active with {Ended=true}); }
            },token);
            m.Manual=run.ContinueWith(static _=>{},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            return run;
        }
    }
    private RunLog NewLog(string id,out LiveOutput? view,TextWriter? live=null,bool showFiles=false)
    {
        if (!PathNames.IsSingleSegment(id) || !PathNames.IsWindowsTransferPath(id)) throw Host.Unknown(id); var run=ResourceNames.NewId();
        return new(host.Logs,Path.Combine(LogFolder,id + ResourceNames.LogNameSeparator + run + ResourceNames.LogExtension),host.Settings,view=live==null ? null : new LiveOutput(live,host.Clock,showFiles,(id,run)));
    }
    private Task RetryDelay(Exception e,int failures,CancellationToken ct,HostMember? logged=null)
    {
        var delay=(e as ItemFailedException)?.WaitLeft(host.Clock) ?? host.Settings.Backoff.RetryDelay(failures);
        logged?.Event(AutoSync.SourceWait(e,delay)); // the pair's restart wait is logged, never silent
        return host.Settings.Backoff.WaitAsync(delay,host.Clock,ct);
    }
    internal async Task Failed(HostMember m,Exception e,CancellationToken ct)
    {
        if(e is not (ItemFailedException or EngineRequestException)) m.Event(new(RunEventKind.Failed,nameof(Host),string.Empty,e.Message,string.Empty){Error=e});
        try { await host.Store!.SaveNoticeAsync(m.Pair.PairId,e is ItemFailedException {Cause:TextKey.LocalRootMissing} ? RootNotice() :
        new(0,NoticeKind.Failure,e switch {ItemFailedException i=>i.Cause,EngineRequestException request=>request.Key,_=>TextKey.RootUnavailable},e is ItemFailedException f ? f.Path : string.Empty,
            new([],null),host.Clock.GetUtcNow(),false),ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception error) { m.Event(new(RunEventKind.Failed,nameof(IStore.SaveNoticeAsync),string.Empty,error.Message,string.Empty){Error=error}); }
    }
    // A missing local root is the pair's root notice, which a verified root check closes.
    private Notice RootNotice() => new(0,NoticeKind.RootIdentity,TextKey.RootUnavailable,string.Empty,new([],null),host.Clock.GetUtcNow(),false);
    internal Task Membership(HostMember m,bool removed,CancellationToken ct) => m.Local==null || m.Pair.Roots==null ? Task.CompletedTask : m.Local.SetMembershipAsync(m.Pair,!m.Pair.Enabled || !m.Admitted || host.IsPaused(m.Pair.PairId),removed,ct);
    internal static async Task Drain(HostMember m)
    {
        Interlocked.Increment(ref m.Closing);
        try {
        m.Opening?.Cancel(); m.Stop?.Cancel();
        try {await m.Setup;} catch(OperationCanceledException) when(m.Opening?.IsCancellationRequested==true) { }
        m.Stop?.Cancel(); m.ManualStop?.Cancel();
        try { await Task.WhenAll(m.Live,m.Manual,m.DebtWake); }
        catch (OperationCanceledException) when (m.Stop?.IsCancellationRequested==true || m.ManualStop?.IsCancellationRequested==true) { }
        finally
        {
            m.Queue.RestartPrefix(); m.JournalWake=null; lock(m){m.RepairFinished?.Dispose();m.RepairFinished=null; m.Opening?.Dispose(); m.Opening=null; m.Stop?.Dispose(); m.Stop=null;}
            m.ManualStop?.Dispose(); m.ManualStop=null; m.Manual=Task.CompletedTask; m.Log?.Dispose(); m.Log=null;
            if(m.Local!=null && m.Pair.Roots!=null) await m.Local.SetMembershipAsync(m.Pair,true,false,CancellationToken.None);
        }
        } finally {Interlocked.Decrement(ref m.Closing);}
    }
    /// <summary>A live view goes to <paramref name="opened"/>, whose owner drains it outside this member task, so a stalled reader never holds Stop or Pause.</summary>
    internal async Task ManualRepair(HostMember m,CancellationToken ct,TextWriter? live=null,bool showFiles=false,Action<LiveOutput>? opened=null)
    {
        using var log=NewLog(m.Pair.PairId,out var view,live,showFiles);
        if(view!=null) opened!(view);
        m.OperationLog.Value=log.Write;
        await Repair.RunAsync(m.Pair,host.Settings,log.Write,m.Paths,m.Queue,m.Local,m.Dropbox,host.Store!,null,null,ct,caughtUp:m.CaughtUp,localRepaired:()=>
        {
            CancellationTokenSource? done=null; lock(m) if(m.JournalWake!=null) done=m.RepairFinished??=new(); done?.Cancel(); // Cancel outside the monitor
            host.Logs.Prune(LogFolder,TimeSpan.FromDays(host.Settings.LogRetentionDays),host.Clock.GetUtcNow()); // a completed full Repair keeps run logs for the retention period
        },progress:view==null ? null : view.Accept); // background Repair makes no per-item records
    }
}
