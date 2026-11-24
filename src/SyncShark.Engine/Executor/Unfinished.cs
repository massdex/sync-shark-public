namespace Massdex.SyncShark.Engine.Executor;

/// <summary>One pair's saved jobs and their coalesced check opportunities. No polling.</summary>
public sealed class Unfinished
{
    private readonly Dictionary<(string Route,string Id),AcceptedJob> jobs=[];
    private readonly HashSet<(string Route,string Id)> checking=[], again=[], invalid=[];
    private readonly SemaphoreSlim refresh=new(1);
    /// <summary>The reader has projected both endpoints through the removal capture.</summary>
    public Func<(JournalPosition? Local,string? Dropbox),bool>? AcquiredThrough { get; set; }
    private bool loaded, recoveringLive, recoverAgain;
    private readonly Dictionary<(string Route,string Id),JobReply.Terminal> finished=[];
    private readonly Dictionary<(string Route,string Id),(JournalPosition? Local,string? Dropbox)> bounds=[];
    private readonly Dictionary<(string Route,string Id),HashSet<int>> settled=[], split=[];
    private readonly Dictionary<(string Route,string Id,int Ordinal),Task<JobReply.Terminal>> createReads=[];
    /// <summary>The pair queue's contiguous-prefix proof, for boundaries observed after terminal status.</summary>
    public Func<(JournalPosition? Local,string? Dropbox),bool>? CoversThrough { get; set; }

    /// <summary>Check retained launch footprints without scanning the store a second time. Before initial load no absence is proved.</summary>
    public bool CoversOpenJob(Scope scope)
    {
        lock(jobs) return !loaded || jobs.Values.Any(j=>j.Launch.Any(m=>Overlaps(m.Path,scope.DropboxPath)||Overlaps(m.Destination,scope.DropboxPath)));
    }

    /// <summary>After saving the token, match its actual launch paths against already admitted news.</summary>
    public async Task InstalledAsync(AcceptedJob job,Func<Scope,IEnumerable<Change>> heldInput,Func<Change,Task> wake)
    {
        lock(jobs) jobs[(job.Route,job.JobId)]=job;
        foreach(var member in job.Launch)
            foreach(var path in new[]{member.Path,member.Destination})
                if(path!=null && heldInput(new(PathNames.LocalSpelling(path),path)).FirstOrDefault(c=>Touches(path,c)) is {} change) {await wake(change);return;}
    }

    private static bool Matches(AcceptedJob job,Change c) => job.Launch.Any(m=>Touches(m.Path,c)||Touches(m.Destination,c));
    private static bool Touches(string? path,Change c)=>c.Side==Side.Dropbox && (Overlaps(path,c.Path)||Overlaps(path,c.OldPath)||Overlaps(path,c.AffectedPath));
    private static bool Overlaps(string? a,string? b)=>a!=null && b!=null && (PathNames.IsSelfOrUnder(a,b)||PathNames.IsSelfOrUnder(b,a));

    /// <summary>Refresh before first use and at Repair; feed input checks only overlapping jobs.</summary>
    public async Task CheckAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change? received, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Notice? reviewed = null)
    {
        if(reviewed==null && received!=null && received.Side!=Side.Dropbox) return;
        await refresh.WaitAsync(ct);
        try
        {
            if(!loaded || received==null && reviewed==null)
            {
                await foreach(var job in store.JobsAsync(pair.PairId,ct)) lock(jobs) jobs[(job.Route,job.JobId)]=job;
                lock(jobs) loaded=true;
            }
        }
        finally {refresh.Release();}
        PrefixSet? footprint=null;
        if(reviewed!=null)
        {
            footprint=new();
            var endpoints=reviewed.Subject.Items.Select(i=>(i.Side,i.Path)).DefaultIfEmpty((reviewed.Subject.Incoming??Side.Local,reviewed.Path));
            if(reviewed.Subject.Move is {} move) endpoints=endpoints.Concat(new[]{move.Source,move.Destination,move.Intermediate}.OfType<string>().Select(p=>(Side.Dropbox,p)));
            foreach(var (side,path) in endpoints.Distinct()) footprint.Add(PathNames.DropboxKey(side==Side.Dropbox?path:await FolderDelete.MapAsync(pair,store,side,path,ct)));
        }
        AcceptedJob[] selected;
        lock(jobs) selected=jobs.Values.Where(j=>j.PairId==pair.PairId && (footprint==null ? received==null || Matches(j,received) : j.Launch.Any(m=>footprint.Overlaps(PathNames.DropboxKey(m.Path)) || m.Destination!=null && footprint.Overlaps(PathNames.DropboxKey(m.Destination))))).ToArray();
        // Built only for selected jobs: an input that overlaps no job allocates no call.
        if(selected.Length>0)
        {
            var call=new Call(pair,settings,events,paths,local,dropbox,store,heldInput,populate,compareOldBinding,ct);
            await Task.WhenAll(Array.ConvertAll(selected,j=>Check(call,j)));
        }
        if(received==null)
        {
            await new StageRecovery(this,pair,settings,events,paths,local,dropbox,store,heldInput,populate,compareOldBinding,ct).RunAsync(footprint);
            if(await store.ArmedAsync(pair.PairId,ct) is {} left && !ItemLocks.Busy(pair,Side.Local,left.Victim.Proof.Expected.Path) && (footprint==null || footprint.Overlaps(PathNames.DropboxKey(left.Victim.Saved?.DropboxPath ?? await FolderDelete.MapAsync(pair,store,Side.Local,left.Victim.Proof.Expected.Path,ct)))))
            {
                if(reviewed==null || !PathNames.Same(reviewed.Path,left.Victim.Proof.Expected.Path)) await Unchecked(store,pair,left.Victim.Proof.Expected,ct);
                await StageRecovery.EndNoteAsync(store,local,pair,left.Victim,left.Stage,ct);
            }
        }
    }

    // One CheckAsync call's endpoints and callbacks, shared by the job checks it starts.
    internal sealed record Call(PairConfig Pair,EngineSettings Settings,Action<RunEvent> Events,PathScope Paths,ISide Local,ISide Dropbox,IStore Store,Func<Scope,IEnumerable<Change>> HeldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> Populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> CompareOldBinding,CancellationToken Ct);

    // One active check per job; a request that arrives during it repeats the check once.
    private async Task Check(Call call,AcceptedJob job)
    {
        var key=(job.Route,job.JobId);
        lock(jobs)
        {
            if(!jobs.ContainsKey(key)) return;
            if(!checking.Add(key)) {again.Add(key);return;}
        }
        try
        {
            while(true)
            {
                JobReply.Terminal? cached;lock(jobs) finished.TryGetValue(key,out cached);
                var reply=cached??await call.Dropbox.ReadJobAsync(job,call.Ct);
                if(cached==null && reply is JobReply.Terminal terminalReply && terminalReply.Members.Count==job.Launch.Count)
                {
                    var localBound=await call.Local.BoundaryAsync(call.Ct);var remoteBound=await call.Dropbox.BoundaryAsync(call.Ct);
                    lock(jobs) {finished[key]=terminalReply;bounds[key]=(localBound.Position,remoteBound.Cursor);settled[key]=[];split[key]=[];}
                }
                bool done=reply is JobReply.Terminal terminal && await SettleMembers(call,job,terminal);
                await ValidityAsync(call,job,key,reply is JobReply.Invalid);
                if(done) await call.Store.SettleJobAsync(job,call.Ct);
                lock(jobs)
                {
                    if(done) {jobs.Remove(key);finished.Remove(key);bounds.Remove(key);settled.Remove(key);split.Remove(key);}
                    if(again.Remove(key) && !done) continue;
                    checking.Remove(key); return;
                }
            }
        }
        catch {lock(jobs) {checking.Remove(key);again.Remove(key);} throw;}
    }

    // An invalid reply raises one failure per launch path; a later valid reply closes them unless another invalid job shares the path.
    private async Task ValidityAsync(Call call,AcceptedJob job,(string Route,string Id) key,bool isInvalid)
    {
        if(isInvalid)
        {
            lock(jobs) invalid.Add(key);
            foreach(var m in job.Launch)
            {
                if(m.Id!=null) await call.Dropbox.ReadByIdAsync(m.Id,call.Ct);
                bool masked=false;
                await foreach(var n in call.Store.NoticesAtAsync(call.Pair.PairId,PathNames.DropboxKey(m.Path),call.Ct)) if(n.Kind==NoticeKind.Failure && n.Cause!=TextKey.FailDropboxDeleteJobInvalid) masked=true;
                if(!masked) await call.Store.SaveNoticeAsync(call.Pair.PairId,new(0,NoticeKind.Failure,TextKey.FailDropboxDeleteJobInvalid,m.Path,new([new(Side.Dropbox,m.Path,m.Id??string.Empty)],Side.Dropbox),DateTimeOffset.UtcNow,false),call.Ct);
            }
        }
        else
        {
            bool wasInvalid;lock(jobs) wasInvalid=invalid.Remove(key);
            if(wasInvalid) foreach(var m in job.Launch)
            {
                bool stillInvalid; lock(jobs) stillInvalid=invalid.Any(k=>jobs[k].Launch.Any(x=>PathNames.Same(x.Path,m.Path)));
                if(!stillInvalid) await foreach(var n in call.Store.NoticesAtAsync(call.Pair.PairId,PathNames.DropboxKey(m.Path),call.Ct)) if(n.Cause==TextKey.FailDropboxDeleteJobInvalid) await call.Store.CloseNoticeAsync(call.Pair.PairId,n,call.Ct);
            }
        }
    }

    // Settle each member of a terminal reply once; true when every member settled.
    private async Task<bool> SettleMembers(Call call,AcceptedJob job,JobReply.Terminal reply)
    {
        if(reply.Members.Count!=job.Launch.Count) return false;
        var jobKey=(job.Route,job.JobId);HashSet<int> completed; (JournalPosition? Local,string? Dropbox) bound;
        lock(jobs) {completed=settled[jobKey];bound=bounds[jobKey];}
        bool delete=job.Route.Contains("delete",StringComparison.Ordinal);
        bool covered=!delete || bound.Dropbox is {Length:>0} && bound.Local!=null && AcquiredThrough?.Invoke(bound)==true;
        ItemFailedException? deferred=null;
        for(int i=0;i<job.Launch.Count;i++)
        {
            if(completed.Contains(i)) continue;
            var member=job.Launch[i]; var receipt=reply.Members[i];
            if(job.Route is "files/create_folder_batch" or "files/create_folder_batch/check") {var waiting=await CreatedAsync(call,job,i,member,receipt,completed);deferred??=waiting;continue;}
            // A wire delete alone cannot distinguish removal, replacement and move cleanup on restart.
            var origin=member.Origin;
            if(delete && origin is not (null or ActionKind.DeleteDropbox or ActionKind.ReplaceKind or ActionKind.MoveDropbox)) continue;
            var binding=await call.Store.FindAsync(call.Pair.PairId,Side.Dropbox,PathNames.DropboxKey(member.Path),call.Ct);
            if(binding!=null && binding.DropboxId!=member.Id) binding=null;
            var scope=new Scope(binding?.LocalPath??await FolderDelete.MapAsync(call.Pair,call.Store,Side.Dropbox,member.Path,call.Ct),member.Path);
            bool didSplit;lock(jobs) didSplit=split[jobKey].Contains(i);
            bool absent=delete && member.Id!=null && (reply.AbsentOrdinals.Contains(i)||didSplit);
            if(!didSplit && origin==ActionKind.DeleteDropbox && member.Id!=null && receipt is {Cause:TextKey.FailDropboxTooManyFiles,Failure:FailureDisposition.Permanent})
            {
                if(covered) covered=await SplitAsync(call,jobKey,i,member,member.Id,binding,scope,completed);
                continue;
            }
            bool success=receipt.Entry!=null && receipt.Cause==null && receipt.Failure==null && (member.Id==null || receipt.Entry.Id==member.Id) && (delete || PathNames.Same(member.Destination??member.Path,receipt.Entry.Path));
            bool settles=delete?await SettleDeleteAsync(call,member,binding,scope,didSplit,absent,success,covered,receipt.Cause,receipt.Failure):member.Destination!=null && member.Id!=null?await JobMoves.SettleAsync(this,call,member,member.Id,member.Destination,binding,scope):success;
            if(settles) completed.Add(i);
        }
        if(deferred!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(deferred).Throw();
        return completed.Count==job.Launch.Count;
    }

    // A folder create settles on the exact created folder; a non-exclusive name clash is resolved once per member.
    private async Task<ItemFailedException?> CreatedAsync(Call call,AcceptedJob job,int i,JobMember member,(Entry? Entry,TextKey? Cause,FailureDisposition? Failure) receipt,HashSet<int> completed)
    {
        if(!member.ExclusiveCreate && receipt is (null,TextKey.ClashNameTaken,FailureDisposition.Permanent))
        {
            var ordinal=(job.Route,job.JobId,i);
            try
            {
                // One active check owns this job. Other jobs share the index, not the provider call.
                Task<JobReply.Terminal>? read;
                lock(jobs) createReads.TryGetValue(ordinal,out read);
                read??=call.Dropbox.ResolveCreateClashAsync(member,new JobReply.Terminal([receipt]),call.Ct);
                if(!read.IsCompleted)
                {
                    lock(jobs) createReads[ordinal]=read;
                    return null;
                }
                lock(jobs) createReads.Remove(ordinal);
                receipt=(await read).Members.Single();
            }
            catch(ItemFailedException error) when(error.Disposition==FailureDisposition.TemporaryNoEffect) {return error;}
        }
        if(receipt is ({IsFolder:true,Skip:null} folder,null,null) && !string.IsNullOrWhiteSpace(folder.Id) && PathNames.Same(folder.Path,member.Path)) completed.Add(i);
        return null;
    }

    // A too-many-files refusal continues as this pair's own split removal. False: a split was recorded, so coverage must be proved again.
    private async Task<bool> SplitAsync(Call call,(string Route,string Id) jobKey,int i,JobMember member,string id,SyncedEntry? binding,Scope scope,HashSet<int> completed)
    {
        var origin=member.Origin;
        // A saved child owns its effect until terminal settlement. A saved final parent owns the whole continuation.
        lock(jobs)
        {
            if(jobs.Any(x=>x.Key!=jobKey && x.Value.Launch.Any(m=>m.Origin==origin && PathNames.IsSelfOrUnder(m.Path,member.Path)))) return true;
        }
        var found=await call.Dropbox.ReadByIdAsync(id,call.Ct);
        // The successor can already have settled before restart. Fence its observed absence, not the original refusal.
        if(found==null) {await FenceSplitAsync(call,jobKey,i);return false;}
        if(found is not {InScope:true} located || !located.Location.Entry.IsFolder || !PathNames.Same(located.Location.Entry.Path,member.Path))
        {
            await CompareReturnedAsync(call,binding,scope,found.Value);
            completed.Add(i);return true;
        }
        var res=await new FolderDelete().ApplyAsync(call.Pair,call.Settings,call.Events,call.Paths,located.Location.Entry,binding,Side.Dropbox,ActionKind.DeleteDropbox,call.Local,call.Dropbox,call.Store,call.HeldInput,call.Populate,call.CompareOldBinding,call.Ct,splitSelected:true,coversThrough:AcquiredThrough);
        if(res is {Outcome:RemovalOutcome.Pending,Accepted:{} accepted} && accepted.Launch.Any(m=>m.Id==id && m.Path==member.Path && m.Origin==origin)) completed.Add(i);
        else if(res.Outcome==RemovalOutcome.Removed) {await FenceSplitAsync(call,jobKey,i);return false;}
        return true;
    }

    // Record the split member and fence the job again at the boundaries observed now.
    private async Task FenceSplitAsync(Call call,(string Route,string Id) jobKey,int i)
    {
        var l=await call.Local.BoundaryAsync(call.Ct);var d=await call.Dropbox.BoundaryAsync(call.Ct);
        lock(jobs)
        {
            split[jobKey].Add(i);
            bounds[jobKey]=(l.Position,d.Cursor);
        }
    }

    private static Task CompareReturnedAsync(Call call,SyncedEntry? binding,Scope scope,(IdentityLocation Location,bool InScope) found) =>
        binding==null?Task.CompletedTask:call.CompareOldBinding(binding,scope,found.InScope?new(scope.LocalPath,found.Location.Entry.Path):scope,call.Ct);

    // A terminal refusal retires the job, not the binding or its held input.
    private static async Task<bool> SettleDeleteAsync(Call call,JobMember member,SyncedEntry? binding,Scope scope,bool didSplit,bool absent,bool success,bool covered,TextKey? cause,FailureDisposition? failure)
    {
        var origin=member.Origin;
        if(didSplit && await call.Dropbox.ReadByIdAsync(member.Id!,call.Ct) is {} back)
        {
            if(!covered) return false;
            await CompareReturnedAsync(call,binding,scope,back);return false;
        }
        if(!absent && !success)
        {
            if(member.Id==null || await call.Dropbox.ReadByIdAsync(member.Id,call.Ct) is not null)
            {
                if(failure!=FailureDisposition.TemporaryNoEffect)
                    await new Notices().RaiseAsync(call.Store,call.Pair.PairId,new(0,NoticeKind.Failure,cause??TextKey.FailDropboxOutcomeUnknown,member.Path,new([new(Side.Dropbox,member.Path,member.Id??string.Empty)],Side.Dropbox),DateTimeOffset.UtcNow,false),call.Ct);
                return true;
            }
        }
        if(!covered || !await HeldRecovery.RecoverHeldAsync(call.Pair,scope,Side.Dropbox,call.Local,call.Dropbox,call.Store,call.HeldInput,call.Populate,call.Ct,origin:origin.GetValueOrDefault(),resolveConflict:(range,side,version,token)=>new Effects().RecoverConflictAsync(call.Pair,call.Settings,call.Events,call.Paths,range,side,version,call.Local,call.Dropbox,call.Store,call.Populate,call.CompareOldBinding,token))) return false;
        if(binding!=null) await call.CompareOldBinding(binding,scope,scope,call.Ct);
        return true;
    }

    /// <summary>Recover the armed removal, if any, against its recorded identity and endpoints.</summary>
    public async Task CheckLiveStageAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct)
    {
        lock(jobs)
        {
            if(recoveringLive) {recoverAgain=true;return;}
            recoveringLive=true;
        }
        try
        {
            while(true)
            {
                await new StageRecovery(this,pair,settings,events,paths,local,dropbox,store,heldInput,populate,compareOldBinding,ct).RunAsync(null);
                lock(jobs)
                {
                    if(recoverAgain) {recoverAgain=false;continue;}
                    recoveringLive=false;
                    return;
                }
            }
        }
        catch
        {
            lock(jobs) {recoveringLive=false;recoverAgain=false;}
            throw;
        }
    }

    internal static Task Unchecked(IStore store,PairConfig pair,Entry v,CancellationToken ct) => new Notices().RaiseAsync(store,pair.PairId,
        new(0,NoticeKind.Failure,TextKey.RecycleUnchecked,v.Path,new([new(Side.Local,v.Path,v.Id)],Side.Local),DateTimeOffset.UtcNow,false),ct);
}
