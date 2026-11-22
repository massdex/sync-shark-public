namespace Massdex.SyncShark.Engine.Executor;

/// <summary>One pair's saved jobs and their coalesced check opportunities. No polling.</summary>
public sealed class Unfinished
{
    private readonly Dictionary<(string Route,string Id),AcceptedJob> jobs=[];
    private readonly HashSet<(string Route,string Id)> checking=[], again=[], invalid=[];
    private readonly SemaphoreSlim refresh=new(1);
    /// <summary>True when the contiguous Dropbox pages through this capture have been admitted, even if their changes are still held.</summary>
    public Func<string,bool>? AcquiredThrough { get; set; }
    private bool loaded;
    private readonly Dictionary<(string Route,string Id),JobReply.Terminal> finished=[];
    private readonly Dictionary<(string Route,string Id),(JournalPosition? Local,string? Dropbox)> bounds=[];
    private readonly Dictionary<(string Route,string Id),HashSet<int>> settled=[], split=[];
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
                if(path!=null && heldInput(new(PathNames.LocalSpelling(path),path)).FirstOrDefault(c=>Matches(job,c)) is {} change) {await wake(change);return;}
    }

    private static bool Matches(AcceptedJob job,Change c) => c.Side==Side.Dropbox && job.Launch.Any(m=>Overlaps(m.Path,c.Path)||Overlaps(m.Path,c.OldPath)||Overlaps(m.Destination,c.Path)||Overlaps(m.Destination,c.OldPath));
    private static bool Overlaps(string? a,string? b)=>a!=null && b!=null && (PathNames.IsSelfOrUnder(a,b)||PathNames.IsSelfOrUnder(b,a));

    /// <summary>Refresh before first use and at Repair; feed input checks only overlapping jobs.</summary>
    public async Task CheckAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change? received, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        if(received!=null && received.Side!=Side.Dropbox) return;
        await refresh.WaitAsync(ct);
        try
        {
            if(!loaded || received==null)
            {
                await foreach(var job in store.JobsAsync(pair.PairId,ct)) lock(jobs) jobs[(job.Route,job.JobId)]=job;
                lock(jobs) loaded=true;
            }
        }
        finally {refresh.Release();}
        AcceptedJob[] selected;
        lock(jobs) selected=jobs.Values.Where(j=>j.PairId==pair.PairId && (received==null || Matches(j,received))).ToArray();
        await Task.WhenAll(selected.Select(Check));
        if(received==null) await RecoverStageAsync(pair,local,dropbox,store,heldInput,populate,compareOldBinding,ct);

        async Task Check(AcceptedJob job)
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
                    var reply=cached??await dropbox.ReadJobAsync(job,ct);
                    if(cached==null && reply is JobReply.Terminal terminalReply && terminalReply.Members.Count==job.Launch.Count)
                    {
                        var localBound=await local.BoundaryAsync(ct);var remoteBound=await dropbox.BoundaryAsync(ct);
                        lock(jobs) {finished[key]=terminalReply;bounds[key]=(localBound.Position,remoteBound.Cursor);settled[key]=[];split[key]=[];}
                    }
                    bool done=reply is JobReply.Terminal terminal && await SettleMembers(job,terminal);
                    if(reply is JobReply.Invalid)
                    {
                        lock(jobs) invalid.Add(key);
                        foreach(var m in job.Launch)
                        {
                            if(m.Id!=null) await dropbox.ReadByIdAsync(m.Id,ct);
                            bool masked=false;
                            await foreach(var n in store.NoticesAtAsync(pair.PairId,PathNames.DropboxKey(m.Path),ct)) if(n.Kind==NoticeKind.Failure && n.Cause!=TextKey.FailDropboxDeleteJobInvalid) masked=true;
                            if(!masked) await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailDropboxDeleteJobInvalid,m.Path,new([new(Side.Dropbox,m.Path,m.Id??string.Empty)],Side.Dropbox),DateTimeOffset.UtcNow,false),ct);
                        }
                    }
                    else
                    {
                        bool wasInvalid;lock(jobs) wasInvalid=invalid.Remove(key);
                        if(wasInvalid) foreach(var m in job.Launch)
                        {
                            bool stillInvalid; lock(jobs) stillInvalid=invalid.Any(k=>jobs[k].Launch.Any(x=>PathNames.Same(x.Path,m.Path)));
                            if(!stillInvalid) await foreach(var n in store.NoticesAtAsync(pair.PairId,PathNames.DropboxKey(m.Path),ct)) if(n.Cause==TextKey.FailDropboxDeleteJobInvalid) await store.CloseNoticeAsync(pair.PairId,n,ct);
                        }
                    }
                    if(done) await store.SettleJobAsync(job,ct);
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

        async Task<bool> SettleMembers(AcceptedJob job,JobReply.Terminal reply)
        {
            if(reply.Members.Count!=job.Launch.Count) return false;
            var jobKey=(job.Route,job.JobId);HashSet<int> completed; (JournalPosition? Local,string? Dropbox) bound;
            lock(jobs) {completed=settled[jobKey];bound=bounds[jobKey];}
            bool delete=job.Route.Contains("delete",StringComparison.Ordinal);
            bool covered=!delete || bound.Dropbox!=null && (AcquiredThrough?.Invoke(bound.Dropbox)==true || bound.Local!=null && CoversThrough?.Invoke(bound)==true);
            for(int i=0;i<job.Launch.Count;i++)
            {
                if(completed.Contains(i)) continue;
                var member=job.Launch[i]; var receipt=reply.Members[i];
                var binding=await store.FindAsync(pair.PairId,Side.Dropbox,PathNames.DropboxKey(member.Path),ct);
                if(binding!=null && binding.DropboxId!=member.Id) binding=null;
                var scope=new Scope(binding?.LocalPath??await FolderDelete.MapAsync(pair,store,Side.Dropbox,member.Path,ct),member.Path);
                bool didSplit;lock(jobs) didSplit=split[jobKey].Contains(i);
                bool absent=delete && member.Id!=null && (reply.AbsentOrdinals.Contains(i)||didSplit);
                if(!didSplit && delete && member.Id!=null && receipt is {Cause:TextKey.FailDropboxTooManyFiles,Failure:FailureDisposition.Permanent})
                {
                    var found=await dropbox.ReadByIdAsync(member.Id,ct);
                    if(found is not {InScope:true} located || !located.Location.Entry.IsFolder || !PathNames.Same(located.Location.Entry.Path,member.Path)) continue;
                    var res=await new FolderDelete().ApplyAsync(pair,settings,events,paths,located.Location.Entry,binding,Side.Dropbox,ActionKind.DeleteDropbox,local,dropbox,store,heldInput,populate,compareOldBinding,ct,splitSelected:true);
                    if(res.Outcome==RemovalOutcome.Removed)
                    {
                        var l=await local.BoundaryAsync(ct);var d=await dropbox.BoundaryAsync(ct);
                        lock(jobs){split[jobKey].Add(i);bounds[jobKey]=(l.Position,d.Cursor);} covered=false;
                    }
                    continue;
                }
                bool success=receipt.Entry!=null && receipt.Cause==null && receipt.Failure==null && (member.Id==null || receipt.Entry.Id==member.Id) && (delete || PathNames.Same(member.Destination??member.Path,receipt.Entry.Path));
                if(delete)
                {
                    if(!absent && !success)
                    {
                        if(member.Id==null || await dropbox.ReadByIdAsync(member.Id,ct) is not null) continue;
                    }
                    if(!covered || !await FolderDelete.RecoverHeldAsync(pair,scope,Side.Dropbox,local,dropbox,store,heldInput,populate,ct)) continue;
                    if(binding!=null) await compareOldBinding(binding,scope,scope,ct);
                }
                else if(member.Destination!=null && member.Id!=null)
                {
                    var where=await dropbox.ReadByIdAsync(member.Id,ct);
                    if(where is {InScope:true} arrived)
                    {
                        if(arrived.Location.Entry.Id!=member.Id || !PathNames.Same(arrived.Location.Entry.Path,member.Destination)) continue;
                        if(binding!=null) await compareOldBinding(binding,scope,new(scope.LocalPath,arrived.Location.Entry.Path),ct);
                    }
                    else if(binding!=null) await compareOldBinding(binding,scope,scope,ct);
                    else continue;
                }
                else if(!success) continue;
                completed.Add(i);
            }
            return completed.Count==job.Launch.Count;
        }
    }

    private static async Task RecoverStageAsync(PairConfig pair,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding,CancellationToken ct)
    {
        if(await store.ArmedAsync(pair.PairId,ct) is not {} note) return;
        var victim=note.Victim; var proof=victim.Proof;
        var location=await local.ReadByIdAsync(proof.Expected.Id,ct);
        if(location is not {InScope:false} actual || actual.Location.Domain!=proof.VolumeId || actual.Location.Entry.Id!=proof.Expected.Id) return;
        if(!proof.Expected.IsFolder && (actual.Location.Entry.Size!=proof.Expected.Size || actual.Location.Entry.ContentHash!=proof.ContentHash)) return;
        var current=await local.ReadOneAsync(proof.Expected.Path,ct);
        var saved=victim.Saved;
        var cloudPath=saved?.DropboxPath??await FolderDelete.MapAsync(pair,store,Side.Local,proof.Expected.Path,ct);
        var other=await dropbox.ReadOneAsync(cloudPath,ct);
        var currentBinding=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(proof.Expected.Path),ct);
        if(currentBinding!=saved) return;
        if(note.Stage is not {} stage)
        {
            if(current!=null || other!=null) return;
            var range=new Scope(proof.Expected.Path,cloudPath);
            if(!await FolderDelete.RecoverHeldAsync(pair,range,Side.Local,local,dropbox,store,heldInput,populate,ct)) return;
            if(saved!=null)
            {
                if(saved.IsFolder) await compareOldBinding(saved,range,range,ct);
                else if(await local.ReadOneAsync(proof.Expected.Path,ct)==null && await dropbox.ReadOneAsync(cloudPath,ct)==null) await store.CommitAsync(pair.PairId,saved,null,null,null,ct);
            }
            await store.DisarmAsync(pair.PairId,victim,null,ct);return;
        }
        if(other!=null && (other.IsFolder || other.Skip!=null || saved!=null && other.Id!=saved.DropboxId || other.Size!=stage.Size || other.ContentHash!=stage.ContentHash)) return;
        if(current!=null && (current.Id!=stage.FileId || current.Size!=stage.Size || await local.ContentHashAsync(current,ct)!=stage.ContentHash)) return;
        try
        {
            if(current==null)
            {
                await using var reopened=await local.ReopenStageAsync(victim,stage,ct);
                var result=await local.PublishAsync(reopened,null,null,ct); current=result.Final;
                if(result.Outcome!=RemovalOutcome.Published || current?.Id!=stage.FileId || current.Size!=stage.Size || await local.ContentHashAsync(current,ct)!=stage.ContentHash) return;
            }
            if(other!=null || saved!=null)
            {
                var updated=other!=null?SyncedEntries.From(current,other):saved! with {LocalId=current.Id,LocalSize=current.Size,LocalModifiedTicks=current.Modified.UtcTicks,LocalPublished=null};
                await store.CommitAsync(pair.PairId,saved,updated,null,null,ct);
            }
            await store.DisarmAsync(pair.PairId,victim,stage,ct);
        }
        catch(ChangedUnderneathException) { /* An unverified stage stays armed. */ }
    }
}
