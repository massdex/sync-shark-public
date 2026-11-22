using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Feeds each source independently; its read position need not be its settled position.</summary>
public static class AutoSync
{
    public static Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, CancellationToken ct) =>
        ReadAsync(pair,settings,events,paths,queue,local,dropbox,store,null,null,ct);

    public static Task ReadThroughAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, (JournalPosition? Local,string? Dropbox) captured, Action attempted, CancellationToken ct) =>
        ReadAsync(pair,settings,events,paths,queue,local,dropbox,store,captured,attempted,ct);

    private static async Task ReadAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, (JournalPosition? Local,string? Dropbox)? captured, Action? attempted, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool continuityLost=false;
        var pending=new HashSet<Task>();
        Exception? failure=null;
        async Task Intake()
        {
            try { await Task.WhenAll(ReadSide(local),ReadSide(dropbox)); }
            catch(OperationCanceledException) when(continuityLost && !ct.IsCancellationRequested) { }
        }
        await FinishIntakeAsync(Intake(),()=>{if(!continuityLost) attempted?.Invoke();},DrainAsync);
        async Task ReadSide(ISide side)
        {
            var token=stop.Token;
            try
            {
                var cursor = side.Side == Side.Dropbox ? await store.CursorAsync(pair.PairId,token) : null;
                var position = side.Side == Side.Local && pair.Roots?.LocalVolumeKey() is {} volume ? await store.PositionAsync(volume,token) : null;
                if (captured is {} target && (side.Side==Side.Dropbox ? target.Dropbox==null : target.Local==null)) return;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var page=await ReadPageAsync(t=>captured is {} end ? side.ReadChangesAsync(cursor,position,(end.Dropbox,end.Local),t) : side.ReadChangesAsync(cursor,position,t),side.Side,settings,events,token);
                    if (page.ContinuityLost)
                    {
                        queue.LoseCoverage(side.Side);
                        await DrainAsync();
                        await Monitor.ObserveAsync(pair,side.Side,page,store,token);
                        continuityLost=true; stop.Cancel(); break;
                    }
                    if (page.Changes.Count!=0 || page.Cursor!=cursor || page.Position!=position)
                    {
                        var work=Settle(page);
                        lock(pending) pending.Add(work);
                        _ = RetireAsync(work);
                    }
                    (cursor,position)=(page.Cursor,page.Position);
                    if (captured!=null && page.CaughtUp) break;
                }
            }
            catch { stop.Cancel(); throw; }
            async Task Settle(ChangePage page)
            {
                try { await ApplyPageAsync(pair,settings,events,paths,queue,side.Side,page,local,dropbox,store,token); }
                catch { stop.Cancel(); throw; }
            }
        }
        async Task RetireAsync(Task work)
        {
            try {await work;}
            catch(Exception error) {lock(pending) failure??=error;}
            finally {lock(pending) pending.Remove(work);}
        }
        async Task DrainAsync()
        {
            Task[] remaining; lock(pending) remaining=pending.ToArray();
            await Task.WhenAll(remaining);
            lock(pending) if(failure!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            try {return await read(ct);}
            catch(Exception error) when(!ct.IsCancellationRequested && (error is IOException or ItemFailedException {Disposition:FailureDisposition.TemporaryNoEffect}))
            {
                var wait=error is ItemFailedException {ProviderWait:{} floor} ? floor : TimeSpan.FromSeconds(settings.Backoff.InitialBackoffSeconds);
                events(new(RunEventKind.Wait,nameof(ISide.ReadChangesAsync),"",Text.Get(TextKey.WaitSourceRetry,(TextArg.Seconds,wait.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))),""){Side=source,Error=error});
                await Task.Delay(wait,ct);
            }
        }
    }

    public static async Task ApplyPageAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, Side source, ChangePage page, ISide local, ISide dropbox, IStore store, CancellationToken ct)
    {
        if (page.ContinuityLost) { queue.LoseCoverage(source); await Monitor.ObserveAsync(pair,source,page,store,ct); return; }
        Task Populate(Scope scope,Side side,Recovery.Folder folder,CancellationToken token) => PopulateAsync(pair,settings,events,paths,scope,side,folder,local,dropbox,store,token);
        Task Hop(SyncedEntry binding,Scope before,Scope after,CancellationToken token) => Repair.RunAsync(pair,settings,events,paths,queue,local,dropbox,store,before,(binding,before,after),token);
        async Task Apply(Change change,CancellationToken token)
        {
            try {await ItemSync.ApplyAsync(pair,settings,events,queue.CurrentScope(paths),change,local,dropbox,store,Populate,Hop,token,queue.Held,(_,reproducible)=>{if(!reproducible) throw new WorkQueue.Unsettled();});}
            catch(ChangedUnderneathException e) when(!PathNames.Same(e.Path,pair.LocalRoot) && !PathNames.Same(e.Path,pair.DropboxRoot))
            {
                events(new(RunEventKind.Wait,nameof(ItemSync),e.Path,e.Message,nameof(TextKey.FailChanged)){Cause=TextKey.FailChanged,Error=e});
                throw new WorkQueue.Unsettled();
            }
        }
        await queue.AdmitAsync(pair,page,Apply,
            source==Side.Local ? local.SettleJournalAsync : null,
            source==Side.Dropbox ? (cursor,token)=>store.SaveCursorAsync(pair.PairId,cursor,token) : null,
            source==Side.Dropbox ? (change,token)=>queue.Unfinished.CheckAsync(pair,settings,events,paths,change,local,dropbox,store,queue.Held,Populate,Hop,token) : null,ct);
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
        finally { await store.ClearScanAsync(pair.PairId,scan,CancellationToken.None); }
    }
}
