using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>One Repair row or move group handed to the queue: its attempts, its retained failure and its reobservation.</summary>
internal sealed class RepairSubmission(RepairRun run,Row? row,IReadOnlyList<RenameCandidate> candidates,List<(Scope Footprint,Notice Notice)>? retiring,bool coveredFolder)
{
    private bool endAttempted, reobserve;
    private Notice? retained;

    internal async Task Apply(CancellationToken token)
    {
        try {await Attempt(token);}
        catch(ChangedUnderneathException e) when(run.physicalHop==null && e.Path.Length!=0 && !Path.IsPathRooted(e.Path) && !PathNames.Same(e.Path,run.pair.LocalRoot) && !PathNames.Same(e.Path,run.pair.DropboxRoot))
        {
            reobserve=true;
            run.Log(new(RunEventKind.Wait,nameof(ItemSync),e.Path,string.Empty,nameof(TextKey.FailChanged)){Cause=TextKey.FailChanged,Error=e});
            throw new WorkQueue.Unsettled();
        }
    }

    internal async Task Settle()
    {
        // A live identity or a changed binding belongs to its own input. Leave it and finish this removal.
        try { await Apply(run.ct); }
        catch(ChangedUnderneathException race) when(!PathNames.Same(race.Path,run.pair.LocalRoot) && !PathNames.Same(race.Path,run.pair.DropboxRoot)) {}
    }

    /// <summary>One attempt of this submission. Not split by job: an async preparation helper would allocate a task per row even when nothing suspends.</summary>
    private async Task Attempt(CancellationToken token)
    {
        var moves=candidates;
        bool touchedMove=candidates.Any(c=>run.store.ScanTouched(run.id,run.scan,c.Side,c.Moved.Path,c.Moved.IsFolder) || run.store.ScanTouched(run.id,run.scan,c.Side,c.Synced.PathOn(c.Side),c.Moved.IsFolder));
        if(touchedMove || row!=null && !(run.removal && row is {Local:null,Dropbox:null,Synced:not null}) && run.Touched(row,row.IsFolder()))
        {
            if(run.ScanCleared) reobserve=true;
            if(touchedMove)
            {
                moves=await RemainingMoves(token);
                if(moves.Count==0) return;
            }
            else if(retained==null && !reobserve) return;
            // Our own Failure touched this scan. Retry the retained input, not a notice that replaced it.
            if(retained!=null && await Repair.CurrentRetryAsync(run.store,run.id,retained,token)==null) throw new WorkQueue.Unsettled();
        }
        var current=run.queue.CurrentScope(run.paths); var prepared=row;
        if(reobserve && moves.Count!=0)
        {
            // Keep the admitted move's identity and footprint; the move planner rechecks both ends.
            var fresh=new List<RenameCandidate>(moves.Count);
            foreach(var original in moves)
            {
                var leg=original.Side==Side.Local?run.local:run.dropbox;
                if(await leg.ReadOneAsync(original.Moved.Path,token) is not {} now || now.Id!=original.Moved.Id) throw new WorkQueue.Unsettled();
                fresh.Add(original with {Moved=now});
            }
            moves=fresh;
        }
        if(prepared!=null)
        {
            var (lp,dp)=Row.Paths(prepared);
            if(reobserve)
            {
                var pc=run.local.ReadOneAsync(lp,token);var db=run.dropbox.ReadOneAsync(dp,token);await Task.WhenAll(pc,db);
                var left=await pc;var right=await db;
                // An old identity's scan facts are not authority over a different occupant.
                if(prepared.Local is {} a && a.Id!=left?.Id || prepared.Dropbox is {} b && b.Id!=right?.Id) throw new WorkQueue.Unsettled();
                prepared=prepared with {Local=left,Dropbox=right,LocalHash=null,Deletion=null,DeletionClock=null,
                    Context=prepared.Context with {Skipped=left?.Skip!=null || right?.Skip!=null}};
            }
            prepared=prepared with {Context=prepared.Context with {OutOfScope=current.Excludes(lp,dp,prepared.IsFolder())}};
        }
        Notice? own=null; EffectOutcome? outcome;
        try { outcome=await RowExecution.ApplyAsync(run.pair,run.settings,run.Log,current,prepared,moves,run.local,run.dropbox,run.store,coveredFolder && !reobserve?PopulateCovered:run.Populate,run.Hop,token,run.queue.Held,
            (_,reproducible)=>{if(!reproducible) throw new WorkQueue.Unsettled();},failedNotice:n=>own=n,coversThrough:run.queue.ProofNow?.Invoke()); }
        catch(WorkQueue.Unsettled) when(own!=null && run.physicalHop==null)
        {
            retained=own;
            // After discovery the durable notice is the retry. A second failure releases the row so its followers and the run finish.
            if(endAttempted) return;
            await run.discoveryEnded.Task.WaitAsync(token); endAttempted=true;
            throw;
        }
        if(own==null && !reobserve && retiring!=null && outcome==EffectOutcome.Applied) await run.retirement.CloseSelected(retiring,token);
        if(own!=null && !endAttempted && run.physicalHop==null && outcome!=EffectOutcome.Refused)
        {
            await run.discoveryEnded.Task.WaitAsync(token); endAttempted=true;
            await RetryOwn(own,token);
        }
    }

    // A folder projection changes paths, not the identity pair. A local hard-link alias alone is not this binding.
    private async Task<IReadOnlyList<RenameCandidate>> RemainingMoves(CancellationToken token)
    {
        var first=candidates[0];
        await foreach(var binding in run.store.ByIdAsync(run.id,first.Side,first.Moved.Id,token))
        {
            if(binding.LocalId!=first.Synced.LocalId || binding.DropboxId!=first.Synced.DropboxId || binding.IsFolder!=first.Synced.IsFolder) continue;
            List<RenameCandidate>? remaining=null;
            foreach(var c in candidates)
            {
                var carried=binding.PathOn(c.Side);
                if(carried==c.Moved.Path || c.Side==Side.Dropbox && PathNames.Same(carried,c.Moved.Path) && PathNames.Name(carried)==PathNames.Name(c.Moved.Path)) continue;
                (remaining??=[]).Add(c with {Synced=binding});
            }
            return remaining is null ? [] : remaining;
        }
        return []; // The admitted identity pair no longer has a binding; never act on a replacement.
    }

    private async Task RetryOwn(Notice original,CancellationToken token)
    {
        var notice=await Repair.CurrentRetryAsync(run.store,run.id,original,token); if(notice==null) return;
        var remote=notice.Subject.Items.FirstOrDefault(m=>m.Side==Side.Dropbox);
        await run.CheckJob(new Change(Side.Dropbox,ChangeKind.Changed,remote?.Path??notice.Path,null,remote?.Id,null,false,false),token);
        await new Retries(run.queue.Unfinished).RetryAsync(run.pair,run.settings,run.Log,run.queue.CurrentScope(run.paths),notice,run.local,run.dropbox,run.store,run.queue.Held,run.Populate,run.Hop,token,coversThrough:run.queue.ProofNow?.Invoke());
    }

    private async Task PopulateCovered(Scope range,Side source,Recovery.Folder folder,CancellationToken token)
    {
        var sourceRoot=source==Side.Local?range.LocalPath:range.DropboxPath;
        var targetRoot=source==Side.Local?range.DropboxPath:range.LocalPath;
        await foreach(var (child,notices) in run.store.CompareAsync(run.id,run.scan,range,run.Evidence,token))
        {
            if(child.EntryOn(source) is not {Skip:null} entry || PathNames.RelativeUnderDropboxRoot(sourceRoot,entry.Path) is not {} suffix ||
                child.EntryOn(PathNames.Other(source))!=null || run.Touched(child,false)) continue;
            var destination=PathNames.Child(targetRoot,suffix); var current=run.queue.CurrentScope(run.paths);
            if(current.Excludes(source==Side.Local?entry.Path:destination,source==Side.Dropbox?entry.Path:destination,entry.IsFolder)) continue;
            List<(Scope Footprint,Notice Notice)>? selectedNotices=null;
            if(!notices.Any(n=>n.Notice.Kind is NoticeKind.Tie or NoticeKind.NameClash))
                foreach(var (notice,member,covered,touched) in notices)
                    if(covered && !touched && await run.retirement.RetiringFootprint(child,notice,member,token) is {} footprint)
                        (selectedNotices??=[]).Add((footprint,notice));
            var outcome=await RowExecution.ApplyAsync(run.pair,run.settings,run.Log,current,child with {Context=child.Context with {Destination=destination}},[],run.local,run.dropbox,run.store,
                (_,_,_,_)=>Task.CompletedTask,run.Hop,token,run.queue.Held,addOnly:true);
            if(selectedNotices!=null && outcome==EffectOutcome.Applied) await run.retirement.CloseSelected(selectedNotices,token);
        }
    }
}
