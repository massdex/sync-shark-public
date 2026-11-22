using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Feeds each source independently; its read position need not be its settled position.</summary>
public static class AutoSync
{
    public static Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, CancellationToken ct) =>
        ReadAsync(pair,settings,events,paths,queue,local,dropbox,store,null,ct);

    public static Task ReadThroughAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, (JournalPosition? Local,string? Dropbox) captured, CancellationToken ct) =>
        ReadAsync(pair,settings,events,paths,queue,local,dropbox,store,captured,ct);

    private static async Task ReadAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, (JournalPosition? Local,string? Dropbox)? captured, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool continuityLost=false;
        try { await Task.WhenAll(ReadSide(local),ReadSide(dropbox)); }
        catch(OperationCanceledException) when(continuityLost && !ct.IsCancellationRequested) { }
        async Task ReadSide(ISide side)
        {
            var token = stop.Token;
            var pending = new HashSet<Task>();
            var failures = new List<Exception>();
            try
            {
                var cursor = side.Side == Side.Dropbox ? await store.CursorAsync(pair.PairId,token) : null;
                var position = side.Side == Side.Local && pair.Roots?.LocalVolumeKey() is {} volume ? await store.PositionAsync(volume,token) : null;
                if (captured is {} target && (side.Side==Side.Dropbox ? target.Dropbox==null : target.Local==null)) return;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var page = captured is {} end ? await side.ReadChangesAsync(cursor,position,(end.Dropbox,end.Local),token) : await side.ReadChangesAsync(cursor,position,token);
                    if (page.ContinuityLost)
                    {
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
            finally { await DrainAsync(); }
            async Task RetireAsync(Task work)
            {
                try { await work; }
                catch(Exception error) { lock(pending) failures.Add(error); }
                finally { lock(pending) pending.Remove(work); }
            }
            async Task DrainAsync()
            {
                Task[] remaining; lock(pending) remaining=pending.ToArray();
                await Task.WhenAll(remaining);
                lock(pending) if(failures.Count!=0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            async Task Settle(ChangePage page)
            {
                try { await ApplyPageAsync(pair,settings,events,paths,queue,side.Side,page,local,dropbox,store,token); }
                catch { stop.Cancel(); throw; }
            }
        }
    }

    public static async Task ApplyPageAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, Side source, ChangePage page, ISide local, ISide dropbox, IStore store, CancellationToken ct)
    {
        if (page.ContinuityLost) { await Monitor.ObserveAsync(pair,source,page,store,ct); return; }
        Task Populate(Scope scope,Side side,Recovery.Folder folder,CancellationToken token) => PopulateAsync(pair,settings,events,paths,scope,side,folder,local,dropbox,store,token);
        Task Hop(SyncedEntry binding,Scope before,Scope after,CancellationToken token) => Repair.RunAsync(pair,settings,events,paths,queue,local,dropbox,store,before,(binding,before,after),token);
        await queue.AdmitAsync(pair,page,(change,token)=>ItemSync.ApplyAsync(pair,settings,events,paths,change,local,dropbox,store,Populate,Hop,token,queue.Held,(_,reproducible)=>{if(!reproducible) throw new WorkQueue.Unsettled();}),
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
