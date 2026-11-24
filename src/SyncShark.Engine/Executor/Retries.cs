namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retry a still-current notice, not the operation that once failed.</summary>
public sealed class Retries
{
    private readonly Unfinished? jobOwner;
    public Retries(Unfinished? jobOwner=null) => this.jobOwner=jobOwner;
    /// <summary>Ordinary failures re-enter ItemSync. Put-back owns its exact ordered input until create-only publication settles.</summary>
    public async Task RetryAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice failure, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null)
    {
        if (await store.NoticeAsync(pair.PairId,failure.Id,ct) is not {} current || !current.Matches(failure)) return;
        if (failure.Kind is not (NoticeKind.PutBack or NoticeKind.Failure)) return;
        var side = failure.Subject.Incoming ?? failure.Subject.EndpointSide(failure.Path) ?? Side.Local;
        if (failure.Kind == NoticeKind.Failure)
        {
            var subjects=failure.Subject.Items.Distinct().ToArray();
            // R40: the caller has already checked this pair owner once. An open job keeps its whole footprint.
            var owner=jobOwner ?? throw new InvalidOperationException(nameof(jobOwner));
            if(failure.Subject.Move is {} pending && (Covered(pending.Source) || Covered(pending.Destination) || pending.Intermediate is {} hop && Covered(hop))) return;
            if(subjects.Length==0 && await CoveredPath(side,failure.Path)) return;
            foreach(var subject in subjects)
                if(await CoveredPath(subject.Side,subject.Path)) return;
            bool Covered(string remotePath)=>owner.CoversOpenJob(new(PathNames.LocalSpelling(remotePath),remotePath));
            async Task<bool> CoveredPath(Side endpoint,string path)
            {
                if(endpoint==Side.Dropbox) return Covered(path);
                var saved=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(path),ct);
                return Covered(saved?.DropboxPath ?? await ItemSync.CounterpartAsync(pair,Side.Local,path,store,ct));
            }
            if(failure.Subject.Move is {} move)
            {
                var observed=await ObserveMoveAsync(pair,paths,move,local,dropbox,store,ct);
                if(observed is not {} input) { await Stale(); return; }
                if(await RowExecution.ExecuteAsync(pair,settings,events,paths,input.Row,input.Group,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,null,false,coversThrough:coversThrough))
                    await store.CloseNoticeAsync(pair.PairId,failure,ct);
                return;
            }
            bool done=true;
            if(subjects.Length==0) done=await Apply(side,failure.Path,null);
            foreach(var subject in subjects)
            {
                var endpoint=subject.Side==Side.Local?local:dropbox;
                if(string.IsNullOrEmpty(subject.Id)){await Stale(); done=false;continue;}
                var at=await endpoint.ReadByIdAsync(subject.Id,ct);
                // A PC item proven out of the pair under its own id: decide its binding by identity, as a move Retry does (:32-34).
                // Only both bound identities gone reaches the planner; a live peer is our own recycle's peer, not a user delete.
                if(subject.Side==Side.Local && at is {InScope:false} binned && binned.Location.Entry.Id==subject.Id && await local.ReadOneAsync(subject.Path,ct)==null)
                {
                    if(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(subject.Path),ct) is not {} bound) continue;
                    var observed=bound.LocalId==subject.Id ? await ItemSync.ObserveBindingAsync(paths,bound,local,dropbox,ct) : default;
                    if(observed.Row is not {Local:null,Dropbox:null}) { await Stale(); done=false; continue; }
                    done &= await RowExecution.ExecuteAsync(pair,settings,events,paths,observed.Row,observed.Group,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,null,false,coversThrough:coversThrough);
                    continue;
                }
                if(at is not {InScope:true} hit || hit.Location.Entry.Path!=subject.Path) { await Stale(); done=false; continue; }
                done &= await Apply(subject.Side,subject.Path,subject);
            }
            if(done) await store.CloseNoticeAsync(pair.PairId,failure,ct);
            async Task Stale()
            {
                if(await store.NoticeAsync(pair.PairId,failure.Id,ct) is {} open && open.Matches(failure))
                    await new Notices().RaiseAsync(store,pair.PairId,failure with {Cause=TextKey.FailChanged,At=DateTimeOffset.UtcNow,Subject=failure.Subject with {Move=null,FirstRunDeletion=IssueSubject.FirstRunDeletionKind.None}},ct);
            }
            Task<bool> Apply(Side source,string path,NoticeItem? subject)=>ItemSync.ChangeAsync(pair,settings,events,paths,
                new Change(source,ChangeKind.Changed,path,null,subject?.Id,null,source==Side.Local,false),local,dropbox,store,populate,compareOldBinding,ct,heldInput,reviewed:failure,member:subject,coversThrough:coversThrough);
            return;
        }
        var scope = new Scope(failure.Path,failure.Path);
        await FolderDelete.RetryHeldAsync(pair,scope,failure,local,dropbox,store,heldInput,populate,ct,resolveConflict:(where,source,version,token)=>new Effects().RecoverConflictAsync(pair,settings,events,paths,where,source,version,local,dropbox,store,populate,compareOldBinding,token));
    }

    /// <summary>Read the reviewed move by identity and prepare its ordinary planner input; null selects stale turnover.</summary>
    public static async Task<(Row Row,IReadOnlyList<RenameCandidate> Group)?> ObserveMoveAsync(PairConfig pair,PathScope paths,IssueSubject.UnknownMove move,ISide local,ISide dropbox,IStore store,CancellationToken ct)
    {
        SyncedEntry? binding=null;
        await foreach(var item in store.ByIdAsync(pair.PairId,Side.Dropbox,move.ExpectedId,ct))
        {
            if(binding!=null) throw new InventoryIncompleteException(move.Source,null);
            binding=item;
        }
        if(binding==null || binding.IsFolder!=move.IsFolder) return null;
        return await ItemSync.ObserveBindingAsync(paths,binding,local,dropbox,ct);
    }
}
