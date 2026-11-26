using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>One Repair row or move group handed to the queue: one distinct turn per admission, its one notice and its end turn.</summary>
internal sealed class RepairSubmission(RepairRun run,Row? row,IReadOnlyList<RenameCandidate> candidates,List<(Scope Footprint,Notice Notice)>? retiring,bool coveredFolder)
{
    private bool reobserve, final, settled, retained, uncertain;
    private Notice? own;
    private ItemFailedException? error; // the last turn's failure, for its logged reason
    private TimeSpan? floor; private long? floorStart; private TextKey floorCause;

    /// <summary>The first turn failed; the in-place reobservation is owed.</summary>
    internal bool OwesInPlace { get; private set; }
    /// <summary>Failed after its in-place retry and owes one end-of-Repair attempt.</summary>
    internal bool Failed { get; private set; }

    /// <summary>The next admission is the in-place retry.</summary>
    internal void InPlace() => reobserve = true;

    internal async Task Apply(CancellationToken token)
    {
        if(run.physicalHop!=null) {await HopAsync(token); return;} // a hop keeps today's single attempt and split
        var since=DateTimeOffset.UtcNow; var endOwn=own;
        TextKey? cause; Notice? raised;
        if(FloorActive()) { cause=floorCause; raised=null; } // a provider floor refused this turn locally, without a send
        else
        {
            try { (cause,raised)=await OnceAsync(token).ConfigureAwait(false); }
            catch(Exception error) when(own!=null && !final && error is not (OperationCanceledException or WorkQueue.Unsettled))
            {
                await SaveAsync(own,token).ConfigureAwait(false); // an independent error stops the retry; the earlier item failure keeps its notice
                throw;
            }
        }
        if(cause==null)
        {
            if(settled)
            {
                if(!reobserve && retiring!=null) await run.retirement.CloseSelected(retiring,token).ConfigureAwait(false);
                if(final && endOwn!=null) await CloseAsync(endOwn,since,token).ConfigureAwait(false);
            }
            else if(own!=null && !final) await SaveAsync(own,token).ConfigureAwait(false); // the retry could not reach the row: the earlier failure keeps its notice
            Failed=false; OwesInPlace=false; return;
        }
        // A witnessed unknown effect keeps its specific notice and its footprint; Repair never resends it.
        if(uncertain || raised?.Subject.Move!=null)
        {
            if(raised!=null) await SaveAsync(raised,token).ConfigureAwait(false);
            throw new WorkQueue.Unsettled();
        }
        own=raised ?? FallbackNotice(cause.Value);
        if(final) return;                              // the end turn failed: the existing notice stays
        if(!reobserve) { OwesInPlace=true; return; }   // the first turn failed: the in-place reobservation is owed
        await SaveAsync(own,token).ConfigureAwait(false); Failed=true; return; // one deduplicated save, then the end turn is owed
    }

    internal async Task Settle()
    {
        // A live identity or a changed binding belongs to its own input. Leave it and finish this removal.
        try { await Apply(run.ct); }
        catch(ChangedUnderneathException race) when(!PathNames.Same(race.Path,run.pair.LocalRoot) && !PathNames.Same(race.Path,run.pair.DropboxRoot)) {}
    }

    /// <summary>The end turn is owed only while this row's own notice is still open.</summary>
    internal async Task<bool> EndAsync(CancellationToken token)
    {
        final=true; reobserve=true;
        if(own==null) return false;
        if(await Repair.CurrentRetryAsync(run.store,run.id,own,token).ConfigureAwait(false) is not {} current) return false;
        own=current;
        return true;
    }

    /// <summary>A declined admission gives this difference its own notice unless an open Failure already covers it.</summary>
    internal async Task DeclinedAsync(CancellationToken token)
    {
        var proposed=FallbackNotice(TextKey.WaitItemRetry);
        if(await CoveredAsync(proposed,token).ConfigureAwait(false)) return;
        await SaveAsync(proposed,token).ConfigureAwait(false);
    }

    // One attempt: null when done; otherwise the failure cause to consider.
    private async Task<(TextKey? Cause,Notice? Raised)> OnceAsync(CancellationToken token)
    {
        settled=false; retained=false; uncertain=false; error=null;
        try
        {
            var (ok,_,raised)=await ExecAsync(token).ConfigureAwait(false);
            settled=ok;
            return (raised is {Kind:NoticeKind.Failure} ? raised.Cause : null,raised);
        }
        catch(ChangedUnderneathException e) when(e.Path.Length!=0 && !Path.IsPathRooted(e.Path) && !PathNames.Same(e.Path,run.pair.LocalRoot) && !PathNames.Same(e.Path,run.pair.DropboxRoot))
        {
            run.Log(new(RunEventKind.Wait,nameof(ItemSync),e.Path,string.Empty,nameof(TextKey.FailChanged)){Cause=TextKey.FailChanged,Error=e});
            return (TextKey.FailChanged,null);
        }
        catch(ItemFailedException failure)
        {
            var error=this.error=failure;
            if(error.Disposition==FailureDisposition.TemporaryNoEffect && error.ProviderWait is {} wait && wait>TimeSpan.Zero)
            { floor=wait; floorStart=error.ProviderWaitStarted; floorCause=error.Cause; }
            own=null;
            if(error.Disposition==FailureDisposition.UnknownEffect) uncertain=true;
            return (error.Cause,BuildNotice(error));
        }
        catch(PathOccupiedException occupied)
        {
            var error=new ItemFailedException(occupied.Path,TextKey.ClashNameTaken,occupied);
            return (error.Cause,BuildNotice(error));
        }
        catch(InventoryIncompleteException) when(retained) { return (TextKey.WaitItemRetry,null); }
        catch(Exception unexpected) when(!token.IsCancellationRequested && ItemFault(unexpected))
        {
            var error=this.error=new ItemFailedException(PathOf(),TextKey.FailChanged,unexpected); // an unknown blocker is this row's failure, never the run's
            own=null;
            return (error.Cause,BuildNotice(error));
        }
    }

    // Cancellation, store and space failures, a lost root or sign-in, network and file-system faults and an owned effect still end the run.
    private static bool ItemFault(Exception error) => error is not (OperationCanceledException or WorkQueue.Unsettled or InsufficientSpaceException
        or Microsoft.Data.Sqlite.SqliteException or ChangedUnderneathException or InventoryIncompleteException or ListingCursorResetException
        or Local.JournalSourceException or Dropbox.DropboxCredentialUnavailableException or HttpRequestException
        or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception);

    private async Task<(bool Settled,EffectOutcome? Outcome,Notice? Raised)> ExecAsync(CancellationToken token)
    {
        var (prepared,moves,done)=await PrepareAsync(token).ConfigureAwait(false);
        if(done) return (false,null,null); // nothing left to prove: not a positive settlement on the reviewed subjects
        var current=run.queue.CurrentScope(run.paths); Notice? raised=null;
        var result=await RowExecution.ExecuteWithOutcomeAsync(run.pair,run.settings,run.Log,current,prepared,moves,false,run.local,run.dropbox,run.store,
            coveredFolder && !reobserve?PopulateCovered:run.Populate,run.Hop,token,run.queue.Held,
            (_,_)=>retained=true,addOnly:false,failedNotice:null,coversThrough:run.queue.ProofNow?.Invoke(),captureFailure:n=>raised=n).ConfigureAwait(false);
        return (result.Settled,result.Outcome,raised);
    }

    // A hop's changed identity reaches its caller (Settle leaves a live descendant to its own input).
    private async Task HopAsync(CancellationToken token)
    {
        var (prepared,moves,done)=await PrepareAsync(token).ConfigureAwait(false);
        if(done) return;
        var current=run.queue.CurrentScope(run.paths);
        var outcome=await RowExecution.ApplyAsync(run.pair,run.settings,run.Log,current,prepared,moves,run.local,run.dropbox,run.store,
            coveredFolder && !reobserve?PopulateCovered:run.Populate,run.Hop,token,run.queue.Held,
            (_,reproducible)=>{if(!reproducible) throw new WorkQueue.Unsettled();},coversThrough:run.queue.ProofNow?.Invoke()).ConfigureAwait(false);
        if(!reobserve && retiring!=null && outcome==EffectOutcome.Applied) await run.retirement.CloseSelected(retiring,token).ConfigureAwait(false);
    }

    // Read the row's live endpoints (and each move leg) so an attempt never acts on a stale scan fact.
    private async Task<(Row? Prepared,IReadOnlyList<RenameCandidate> Moves,bool Done)> PrepareAsync(CancellationToken token)
    {
        var moves=candidates;
        var current=run.queue.CurrentScope(run.paths);
        bool touchedMove=candidates.Any(c=>run.store.ScanTouched(run.id,run.scan,c.Side,c.Moved.Path,c.Moved.IsFolder) || run.store.ScanTouched(run.id,run.scan,c.Side,c.Synced.PathOn(c.Side),c.Moved.IsFolder));
        // A blocked folder's only outcome is its failure notice; a touch under it, such as its unreadable child's own notice, cannot make it stale.
        if(touchedMove || row!=null && row.Context.UnreadableCause==null && !(run.removal && row is {Local:null,Dropbox:null,Synced:not null}) && run.Touched(row,row.IsFolder()))
        {
            if(run.ScanCleared) reobserve=true;
            if(touchedMove)
            {
                moves=await RemainingMoves(token).ConfigureAwait(false);
                if(moves.Count==0) return (null,[],true);
            }
            else if(!final && own==null && !reobserve) return (null,[],true);
        }
        var prepared=row;
        if(reobserve && moves.Count!=0)
        {
            // Keep the admitted move's identity and footprint; the move planner rechecks both ends.
            var fresh=new List<RenameCandidate>(moves.Count);
            foreach(var original in moves)
            {
                var leg=original.Side==Side.Local?run.local:run.dropbox;
                if(await leg.ReadOneAsync(original.Moved.Path,token).ConfigureAwait(false) is not {} now || now.Id!=original.Moved.Id) return (null,[],true);
                fresh.Add(original with {Moved=now});
            }
            moves=fresh;
        }
        if(prepared!=null)
        {
            var (lp,dp)=Row.Paths(prepared);
            if(reobserve)
            {
                var pc=run.local.ReadOneAsync(lp,token);var db=run.dropbox.ReadOneAsync(dp,token);
                try {await Task.WhenAll(pc,db).ConfigureAwait(false);}
                catch { RethrowFatal(pc.Exception?.InnerExceptions.FirstOrDefault()); RethrowFatal(db.Exception?.InnerExceptions.FirstOrDefault()); throw; }
                var left=await pc;var right=await db;
                // An old identity's scan facts are not authority over a different occupant; end this row so the occupant's own input runs.
                if(prepared.Local is {} a && a.Id!=left?.Id || prepared.Dropbox is {} b && b.Id!=right?.Id) return (null,[],true);
                prepared=prepared with {Local=left,Dropbox=right,LocalHash=null,Deletion=null,DeletionClock=null,
                    Context=prepared.Context with {Skipped=left?.Skip!=null || right?.Skip!=null}};
            }
            prepared=prepared with {Context=prepared.Context with {OutOfScope=current.Excludes(lp,dp,prepared.IsFolder())}};
        }
        return (prepared,moves,false);
    }

    private static void RethrowFatal(Exception? error)
    {
        if(error is null || error is ItemFailedException or ChangedUnderneathException or PathOccupiedException) return;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
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
                if(PathNames.SamePlace(carried,c.Moved.Path)) continue;
                (remaining??=[]).Add(c with {Synced=binding});
            }
            return remaining is null ? [] : remaining;
        }
        return []; // The admitted identity pair no longer has a binding; never act on a replacement.
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

    private Notice BuildNotice(ItemFailedException error)
    {
        var subject=RowExecution.Subject(row,candidates) with {Holder=error.Holder,Move=error.Move};
        return new Notice(0,NoticeKind.Failure,error.Cause,error.Path,subject,DateTimeOffset.UtcNow,false);
    }
    private Notice FallbackNotice(TextKey cause) => new(0,NoticeKind.Failure,cause,PathOf(),RowExecution.Subject(row,candidates),DateTimeOffset.UtcNow,false);
    private string PathOf() => row!=null ? Row.Path(row) : candidates.Count!=0 ? candidates[0].Moved.Path : string.Empty;
    private bool FloorActive() => floor is {} wait && wait - (floorStart is {} start ? TimeProvider.System.GetElapsedTime(start) : TimeSpan.Zero) > TimeSpan.Zero;

    private async Task SaveAsync(Notice proposed,CancellationToken token)
    {
        await new Notices().RaiseAsync(run.store,run.id,proposed,token).ConfigureAwait(false);
        run.Log(new(RunEventKind.Failed,nameof(NoticeKind.Failure),proposed.Path,string.Empty,nameof(NoticeKind.Failure)){Cause=proposed.Cause,Provider=error?.Cause==proposed.Cause ? error.Provider : null});
        own=await ResolveAsync(proposed,token).ConfigureAwait(false) ?? proposed;
    }
    // The stored winner by kind, cause, path and items; the store may keep an earlier At.
    private async Task<Notice?> ResolveAsync(Notice proposed,CancellationToken token)
    {
        Notice? winner=null;
        foreach(var key in NoticeKeys(proposed))
            await foreach(var n in run.store.NoticesAtAsync(run.id,key,token))
                if(n.Kind==NoticeKind.Failure && !n.Dismissed && n.Cause==proposed.Cause &&
                    PathNames.Same(n.Path,proposed.Path) && n.Subject.HasSameItems(proposed.Subject) && (winner==null || n.At<winner.At)) winner=n;
        return winner;
    }
    private async Task<bool> CoveredAsync(Notice proposed,CancellationToken token)
    {
        foreach(var key in NoticeKeys(proposed))
            await foreach(var n in run.store.NoticesAtAsync(run.id,key,token))
                if(n.Kind==NoticeKind.Failure && !n.Ignored && !n.Dismissed &&
                    n.Subject.Items.Any(i=>proposed.Subject.Items.Any(j=>j.Side==i.Side && j.Id.Length!=0 && j.Id==i.Id))) return true;
        return false;
    }
    // Only a positive Settled on the end attempt closes the still-current notice; a later At stays open.
    private async Task CloseAsync(Notice expected,DateTimeOffset since,CancellationToken token)
    {
        foreach(var key in NoticeKeys(expected))
            await foreach(var n in run.store.NoticesAtAsync(run.id,key,token))
                if(n.Kind==NoticeKind.Failure && !n.Dismissed && n.At<=since && n.Cause==expected.Cause &&
                    PathNames.Same(n.Path,expected.Path) && n.Subject.HasSameItems(expected.Subject))
                    await new Notices().CloseAsync(run.store,run.id,n,token).ConfigureAwait(false);
    }
    private static IEnumerable<string> NoticeKeys(Notice n) =>
        n.Subject.Items.Select(i=>PathNames.KeyFor(i.Side,i.Path)).Append(PathNames.Key(n.Path)).Distinct(StringComparer.Ordinal);
}
