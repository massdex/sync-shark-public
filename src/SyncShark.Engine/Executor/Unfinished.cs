namespace Massdex.SyncShark.Engine.Executor;

/// <summary>One pair's saved jobs and their coalesced check opportunities. No polling.</summary>
public sealed class Unfinished
{
    private readonly Dictionary<(string Route,string Id),AcceptedJob> jobs=[];
    private readonly HashSet<(string Route,string Id)> checking=[], again=[], invalid=[];
    private readonly SemaphoreSlim refresh=new(1);
    /// <summary>The reader has projected both endpoints through the removal capture.</summary>
    public Func<(JournalPosition? Local,string? Dropbox),bool>? AcquiredThrough { get; set; }
    private bool loaded;
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
    public async Task CheckAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change? received, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Notice? reviewed = null)
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
        await Task.WhenAll(selected.Select(Check));
        if(received==null) await RecoverStageAsync(pair,settings,events,paths,local,dropbox,store,heldInput,populate,compareOldBinding,ct,footprint);

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
            bool covered=!delete || bound.Dropbox is {Length:>0} && bound.Local!=null && AcquiredThrough?.Invoke(bound)==true;
            ItemFailedException? deferred=null;
            for(int i=0;i<job.Launch.Count;i++)
            {
                if(completed.Contains(i)) continue;
                var member=job.Launch[i]; var receipt=reply.Members[i];
                if(job.Route is "files/create_folder_batch" or "files/create_folder_batch/check")
                {
                    if(!member.ExclusiveCreate && receipt is (null,TextKey.ClashNameTaken,FailureDisposition.Permanent))
                    {
                        var ordinal=(job.Route,job.JobId,i);
                        try
                        {
                            // One active check owns this job. Other jobs share the index, not the provider call.
                            Task<JobReply.Terminal>? read;
                            lock(jobs) createReads.TryGetValue(ordinal,out read);
                            read??=dropbox.ResolveCreateClashAsync(member,new JobReply.Terminal([receipt]),ct);
                            if(!read.IsCompleted)
                            {
                                lock(jobs) createReads[ordinal]=read;
                                continue;
                            }
                            lock(jobs) createReads.Remove(ordinal);
                            receipt=(await read).Members.Single();
                        }
                        catch(ItemFailedException error) when(error.Disposition==FailureDisposition.TemporaryNoEffect) {deferred??=error;continue;}
                    }
                    if(receipt is ({IsFolder:true,Skip:null} folder,null,null) && !string.IsNullOrWhiteSpace(folder.Id) && PathNames.Same(folder.Path,member.Path)) completed.Add(i);
                    continue;
                }
                // A wire delete alone cannot distinguish removal, replacement and move cleanup on restart.
                var origin=member.Origin;
                if(delete && origin is not (ActionKind.DeleteDropbox or ActionKind.ReplaceKind or ActionKind.MoveDropbox)) continue;
                var binding=await store.FindAsync(pair.PairId,Side.Dropbox,PathNames.DropboxKey(member.Path),ct);
                if(binding!=null && binding.DropboxId!=member.Id) binding=null;
                var scope=new Scope(binding?.LocalPath??await FolderDelete.MapAsync(pair,store,Side.Dropbox,member.Path,ct),member.Path);
                bool didSplit;lock(jobs) didSplit=split[jobKey].Contains(i);
                bool absent=delete && member.Id!=null && (reply.AbsentOrdinals.Contains(i)||didSplit);
                if(!didSplit && origin==ActionKind.DeleteDropbox && member.Id!=null && receipt is {Cause:TextKey.FailDropboxTooManyFiles,Failure:FailureDisposition.Permanent})
                {
                    if(!covered) continue;
                    // A saved child owns its effect until terminal settlement. A saved final parent owns the whole continuation.
                    lock(jobs)
                    {
                        if(jobs.Any(x=>x.Key!=jobKey && x.Value.Launch.Any(m=>m.Origin==origin && PathNames.IsSelfOrUnder(m.Path,member.Path)))) continue;
                    }
                    var found=await dropbox.ReadByIdAsync(member.Id,ct);
                    if(found==null)
                    {
                        // The successor can already have settled before restart. Fence its observed absence, not the original refusal.
                        var l=await local.BoundaryAsync(ct);var d=await dropbox.BoundaryAsync(ct);
                        lock(jobs){split[jobKey].Add(i);bounds[jobKey]=(l.Position,d.Cursor);} covered=false;continue;
                    }
                    if(found is not {InScope:true} located || !located.Location.Entry.IsFolder || !PathNames.Same(located.Location.Entry.Path,member.Path)) continue;
                    var res=await new FolderDelete().ApplyAsync(pair,settings,events,paths,located.Location.Entry,binding,Side.Dropbox,ActionKind.DeleteDropbox,local,dropbox,store,heldInput,populate,compareOldBinding,ct,splitSelected:true,coversThrough:AcquiredThrough);
                    if(res is {Outcome:RemovalOutcome.Pending,Accepted:{} accepted} && accepted.Launch.Any(m=>m.Id==member.Id && m.Path==member.Path && m.Origin==origin)) completed.Add(i);
                    else if(res.Outcome==RemovalOutcome.Removed)
                    {
                        var l=await local.BoundaryAsync(ct);var d=await dropbox.BoundaryAsync(ct);
                        lock(jobs){split[jobKey].Add(i);bounds[jobKey]=(l.Position,d.Cursor);} covered=false;
                    }
                    continue;
                }
                bool success=receipt.Entry!=null && receipt.Cause==null && receipt.Failure==null && (member.Id==null || receipt.Entry.Id==member.Id) && (delete || PathNames.Same(member.Destination??member.Path,receipt.Entry.Path));
                if(delete)
                {
                    if(didSplit && await dropbox.ReadByIdAsync(member.Id!,ct) is not null) continue;
                    if(!absent && !success)
                    {
                        if(member.Id==null || await dropbox.ReadByIdAsync(member.Id,ct) is not null) continue;
                    }
                    if(!covered || !await FolderDelete.RecoverHeldAsync(pair,scope,Side.Dropbox,local,dropbox,store,heldInput,populate,ct,origin:origin.GetValueOrDefault(),resolveConflict:(range,side,version,token)=>new Effects().RecoverConflictAsync(pair,settings,events,paths,range,side,version,local,dropbox,store,populate,compareOldBinding,token))) continue;
                    if(binding!=null) await compareOldBinding(binding,scope,scope,ct);
                }
                else if(member.Destination!=null && member.Id!=null)
                {
                    (IdentityLocation Location,bool InScope)? where;
                    try {where=await dropbox.ReadByIdAsync(member.Id,ct);}
                    catch(ItemFailedException) {continue;}
                    if(where is {InScope:true} arrived)
                    {
                        if(arrived.Location.Entry.Id!=member.Id || arrived.Location.Entry.Skip!=null) continue;
                        if(!PathNames.Same(arrived.Location.Entry.Path,member.Destination) || !PathNames.SameName(arrived.Location.Entry.Path,member.Destination))
                        {
                            if(binding==null || PathNames.Same(arrived.Location.Entry.Path,member.Path)) continue;
                            var peer=await local.ReadByIdAsync(binding.LocalId,ct);
                            if(peer is {InScope:true} pc && pc.Location.Entry.Id==binding.LocalId && pc.Location.Entry.Skip==null && arrived.Location.Entry.Skip==null)
                            {
                                var moves=new List<RenameCandidate>();if(pc.Location.Entry.Path!=binding.LocalPath) moves.Add(new(Side.Local,pc.Location.Entry,binding));
                                moves.Add(new(Side.Dropbox,arrived.Location.Entry,binding));
                                if(!await ItemSync.ExecuteAsync(pair,settings,events,paths,null,moves,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,null,false,coversThrough:CoversThrough)) continue;
                            }
                            else await UnknownMove();
                            completed.Add(i);continue;
                        }
                        if(binding==null) continue;
                        await compareOldBinding(binding,scope,new(scope.LocalPath,arrived.Location.Entry.Path),ct);
                    }
                    else if(binding!=null)
                    {
                        var peer=await local.ReadByIdAsync(binding.LocalId,ct);
                        if(peer is {InScope:true} present && present.Location.Entry.Skip==null && present.Location.Entry.Path!=binding.LocalPath)
                        {
                            if(!await ItemSync.ExecuteAsync(pair,settings,events,paths,null,[new(Side.Local,present.Location.Entry,binding)],false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,null,false,coversThrough:CoversThrough)) continue;
                        }
                        else await UnknownMove();
                    }
                    else continue;
                    Task UnknownMove()=>store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailDropboxOutcomeUnknown,member.Path,
                        new IssueSubject([new(Side.Dropbox,member.Path,member.Id)],Side.Dropbox){Move=new(member.Id,member.Path,member.Destination,null,binding!.IsFolder)},DateTimeOffset.UtcNow,false),ct);
                }
                else if(!success) continue;
                completed.Add(i);
            }
            if(deferred!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(deferred).Throw();
            return completed.Count==job.Launch.Count;
        }
    }

    private async Task RecoverStageAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding,CancellationToken ct,PrefixSet? footprint)
    {
        if(await store.ArmedAsync(pair.PairId,ct) is not {} note) return;
        var victim=note.Victim; var proof=victim.Proof;
        var saved=victim.Saved;
        var cloudPath=saved?.DropboxPath??await FolderDelete.MapAsync(pair,store,Side.Local,proof.Expected.Path,ct);
        if(footprint!=null && !footprint.Overlaps(PathNames.DropboxKey(cloudPath))) return;
        var location=await local.ReadByIdAsync(proof.Expected.Id,ct);
        if(location is not {InScope:false} actual || actual.Location.Domain!=proof.VolumeId || actual.Location.Entry.Id!=proof.Expected.Id) return;
        if(!proof.Expected.IsFolder)
        {
            if(actual.Location.Entry.Size!=proof.Expected.Size) return;
            if(!ContentHash.IsValid(actual.Location.Entry.ContentHash))
            {
                if(await local.RecoverAsync(actual.Location,proof.Expected with {ContentHash=proof.ContentHash??""},ct) is not Recovery.File file) return;
                await using var bytes=file.Bytes;
                if(file.Version.Id!=proof.Expected.Id || file.Version.ContentHash!=proof.ContentHash) return;
            }
            else if(actual.Location.Entry.ContentHash!=proof.ContentHash) return;
        }
        if(note.Stage==null && proof.Expected.IsFolder)
        {
            var pc=local.BoundaryAsync(ct);var db=dropbox.BoundaryAsync(ct);await Task.WhenAll(pc,db);
            if(CoversThrough?.Invoke(((await pc).Position,(await db).Cursor))!=true) return;
        }
        var current=await local.ReadOneAsync(proof.Expected.Path,ct);
        var other=await dropbox.ReadOneAsync(cloudPath,ct);
        var localKey=PathNames.Key(proof.Expected.Path);
        var currentBinding=await store.FindAsync(pair.PairId,Side.Local,saved?.LocalKey??localKey,ct);
        if(saved!=null)
        {
            var peer=await dropbox.ReadByIdAsync(saved.DropboxId,ct);
            if(peer is {} live && (!live.InScope || live.Location.Entry!=other)) return;
            // A stage at the arrival name is not proof that the original pair moved there.
            if(note.Stage!=null && localKey!=saved.LocalKey) return;
        }
        if(currentBinding!=saved)
        {
            if(note.Stage is {} committed && current!=null && currentBinding is {} linked && current.Id==committed.FileId && linked.LocalId==current.Id && linked.DropboxContentHash==committed.ContentHash && current.Size==committed.Size && await local.ContentHashAsync(current,ct)==committed.ContentHash)
            {
                if(other!=null && SyncedEntries.From(current,other)==(linked with {LocalPublished=null}))
                {await store.DisarmAsync(pair.PairId,victim,committed,ct);return;}
                saved=linked;
            }
            else
            {
                if(note.Stage==null && saved!=null && currentBinding==null && current==null && other==null && await FolderDelete.RecoverHeldAsync(pair,new(proof.Expected.Path,cloudPath),Side.Local,local,dropbox,store,heldInput,populate,ct))
                    await store.DisarmAsync(pair.PairId,victim,null,ct);
                return;
            }
        }
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
        if(current!=null && (current.Id!=stage.FileId || current.Size!=stage.Size || await local.ContentHashAsync(current,ct)!=stage.ContentHash)) return;
        try
        {
            if(other==null || other.IsFolder || other.Skip!=null || saved!=null && other.Id!=saved.DropboxId || other.Size!=stage.Size || other.ContentHash!=stage.ContentHash)
            {
                if(other?.Skip!=null) return;
                await using var obsolete=current==null?await local.ReopenStageAsync(victim,stage,ct):null;
                if(other==null)
                {
                    if(saved!=null && await dropbox.ReadByIdAsync(saved.DropboxId,ct) is not null) return;
                    // A published stage is not a user's Local edit. Never replay it as an upload.
                    if(current!=null)
                    {
                        // Finish the interrupted publication from actual source history first. Its durable binding makes the later deletion restartable.
                        if(await dropbox.DeletedAsync(cloudPath,ct) is not DeletionEvidence.DeletedFile history || history.Path!=cloudPath || history.Domain!=pair.Roots?.Dropbox.RootNamespaceId || history.ContentHash!=stage.ContentHash || saved!=null && history.RemoteId!=saved.DropboxId) return;
                        var historical=current with {Path=cloudPath,Id=history.RemoteId,ContentHash=history.ContentHash};
                        var published=SyncedEntries.From(current,historical);
                        var action=new Planner.SyncAction(ActionKind.DeleteLocal,new(PathNames.Key(current.Path),current,null,published,[],stage.ContentHash,RowContext.Neutral));
                        await new ItemLocks().RunAsync(pair,action,[],async ()=>
                        {
                            if(await store.ArmedAsync(pair.PairId,ct)!=note || await store.FindAsync(pair.PairId,Side.Local,published.LocalKey,ct)!=saved || await local.ReadOneAsync(current.Path,ct)!=current || await local.ContentHashAsync(current,ct)!=stage.ContentHash || await dropbox.ReadOneAsync(cloudPath,ct)!=null || await dropbox.ReadByIdAsync(history.RemoteId,ct)!=null) return false;
                            if(saved==null || published!=(saved with {LocalPublished=null})) await store.CommitAsync(pair.PairId,saved,published,null,null,ct);
                            await store.DisarmAsync(pair.PairId,victim,stage,ct);
                            return await ItemSync.ChangeAsync(pair,settings,events,paths,new(Side.Dropbox,ChangeKind.Deleted,cloudPath,null,history.RemoteId,null,false,false),local,dropbox,store,populate,compareOldBinding,ct,heldInput,coversThrough:AcquiredThrough);
                        },ct);
                        return;
                    }
                    if(saved!=null) await store.CommitAsync(pair.PairId,saved,null,null,null,ct);
                }
                else
                {
                    if(current!=null || paths.Excludes(proof.Expected.Path,cloudPath,other.IsFolder)) return;
                    // The missing PC was recycled by this operation. Decide the surviving source as a create, not a user's deletion.
                    var row=new Row(PathNames.Key(proof.Expected.Path),null,other,null,[],null,RowContext.Neutral with {Destination=proof.Expected.Path});
                    var action=Planner.Decide.For(row,pair.FirstRun,DateTimeOffset.UtcNow,false) with {Row=row with {Synced=saved}};
                    if(action.Kind is not (ActionKind.Download or ActionKind.EnsureLocalFolder)) return;
                    if(!await new ItemLocks().RunAsync(pair,action,[],async ()=>
                    {
                        if(await local.ReadOneAsync(proof.Expected.Path,ct)!=null || await dropbox.ReadOneAsync(cloudPath,ct)!=other || await store.FindAsync(pair.PairId,Side.Local,row.Key,ct)!=saved || await store.ArmedAsync(pair.PairId,ct) is not {} active || active!=note) return false;
                        return await new Effects().ApplyAsync(pair,settings,events,paths,action,local,dropbox,store,populate,compareOldBinding,ct,heldInput,AcquiredThrough)==EffectOutcome.Applied;
                    },ct)) return;
                    var pc=await local.ReadOneAsync(proof.Expected.Path,ct);var db=await dropbox.ReadOneAsync(cloudPath,ct);
                    if(pc==null || db==null || pc.IsFolder!=db.IsFolder || !pc.IsFolder && await local.ContentHashAsync(pc,ct)!=db.ContentHash || await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(pc.Path),ct) is not {} final || SyncedEntries.From(pc,db)!=(final with {LocalPublished=null})) return;
                }
                await store.DisarmAsync(pair.PairId,victim,stage,ct);return;
            }
            if(current==null)
            {
                await using var reopened=await local.ReopenStageAsync(victim,stage,ct);
                var result=await local.PublishAsync(reopened,null,null,ct); current=result.Final;
                if(result.Outcome!=RemovalOutcome.Published || current?.Id!=stage.FileId || current.Size!=stage.Size || await local.ContentHashAsync(current,ct)!=stage.ContentHash) return;
            }
            await store.CommitAsync(pair.PairId,saved,SyncedEntries.From(current,other),null,null,ct);
            await store.DisarmAsync(pair.PairId,victim,stage,ct);
        }
        catch(ChangedUnderneathException) { /* An unverified stage stays armed. */ }
    }
}
