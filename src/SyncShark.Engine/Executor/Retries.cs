namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retry a still-current notice, not the operation that once failed.</summary>
public sealed class Retries
{
    private readonly Unfinished? jobOwner;
    public Retries(Unfinished? jobOwner=null) => this.jobOwner=jobOwner;
    /// <summary>Reobserve the current failure or retry the held put-back version.</summary>
    public async Task RetryAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice failure, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null)
    {
        if (await store.NoticeAsync(pair.PairId,failure.Id,ct) is not {} current || !current.Matches(failure)) return;
        if (failure.Kind is not (NoticeKind.PutBack or NoticeKind.Failure)) return;
        if(failure.Kind==NoticeKind.Failure)
        {
            await RetryFailureAsync(pair,settings,events,paths,failure,local,dropbox,store,heldInput,populate,compareOldBinding,ct,coversThrough);
            return;
        }
        var scope = new Scope(failure.Path,failure.Path);
        await HeldRecovery.RetryHeldAsync(pair,scope,failure,local,dropbox,store,heldInput,populate,ct,resolveConflict:(where,source,version,token)=>new Effects().RecoverConflictAsync(pair,settings,events,paths,where,source,version,local,dropbox,store,populate,compareOldBinding,token));
    }

    private async Task RetryFailureAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice failure, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough)
    {
        if(failure.Cause==TextKey.KeepRuleItemMissing) { await Notices.CheckKeepRulesAsync(pair,local,dropbox,store,ct); return; }
        var side = failure.Subject.Incoming ?? failure.Subject.EndpointSide(failure.Path) ?? Side.Local;
        var subjects=failure.Subject.Items.Distinct().ToArray();
        if(await CoveredByJobAsync(pair,failure,side,subjects,store,ct)) return;
        if(failure.Subject.Move is {} move)
        {
            var observed=await ObserveMoveAsync(pair,paths,move,local,dropbox,store,ct);
            if(observed is not {} input) { await ReportStaleAsync(pair,failure,store,ct); return; }
            if(await RowExecution.ExecuteAsync(pair,settings,events,paths,input.Row,input.Group,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,null,false,coversThrough:coversThrough))
                await store.CloseNoticeAsync(pair.PairId,failure,ct);
            return;
        }
        bool done=true;
        if(subjects.Length==0) done=await Apply(side,failure.Path,null);
        foreach(var subject in subjects)
        {
            var endpoint=subject.Side==Side.Local?local:dropbox;
            if(string.IsNullOrEmpty(subject.Id)){await ReportStaleAsync(pair,failure,store,ct); done=false;continue;}
            // The notice saw this member at its own name: that (name, id) is the member. By id only when it moved or left.
            string? present=null;
            try { if(await endpoint.ReadOneAsync(subject.Path,ct) is {} named && named.Id==subject.Id) present=named.Path; }
            catch(ItemFailedException) { present=subject.Path; } // ChangeAsync reads it again and reports this failure on the notice
            if(present==null)
            {
                var at=await endpoint.ReadByIdAsync(subject.Id,ct);
                // A PC item proven out of the pair under its own id: decide its binding by identity, as for a move retry.
                // Only both bound identities gone reaches the planner; a live peer is our own recycle's peer, not a user delete.
                if(subject.Side==Side.Local && at is {InScope:false} binned && binned.Location.Entry.Id==subject.Id && await local.ReadOneAsync(subject.Path,ct)==null)
                {
                    if(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(subject.Path),ct) is not {} bound) continue;
                    var observed=bound.LocalId==subject.Id ? await ItemSync.ObserveBindingAsync(paths,bound,local,dropbox,ct) : default;
                    if(observed.Row is not {Local:null,Dropbox:null}) { await ReportStaleAsync(pair,failure,store,ct); done=false; continue; }
                    done &= await RowExecution.ExecuteAsync(pair,settings,events,paths,observed.Row,observed.Group,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,null,false,coversThrough:coversThrough);
                    continue;
                }
                // A live PC subject under a current no-keep rule is out of scope only because that rule's owner still owes it.
                bool owedNoKeep=subject.Side==Side.Local && paths.Exclusions.Any(x=>!x.Keep && x.Side==Side.Local && PathNames.IsSelfOrUnder(subject.Path,x.Path));
                if(at is not {} hit || !hit.InScope && !owedNoKeep || !PathNames.Same(hit.Location.Entry.Path,subject.Path)) { await ReportStaleAsync(pair,failure,store,ct); done=false; continue; }
                present=hit.Location.Entry.Path;
            }
            if(!await Apply(subject.Side,present!,subject))
            {
                done=false;
                // A fresh refusal replaces the reviewed notice; its peer is not a separate change to apply.
                if(await store.NoticeAsync(pair.PairId,failure.Id,ct) is not {} open || !open.Matches(failure)) return;
            }
        }
        if(done) await store.CloseNoticeAsync(pair.PairId,failure,ct);
        Task<bool> Apply(Side source,string path,NoticeItem? subject)=>ItemSync.ChangeAsync(pair,settings,events,paths,
            new Change(source,ChangeKind.Changed,path,null,subject?.Id,null,source==Side.Local,false),local,dropbox,store,populate,compareOldBinding,ct,heldInput,reviewed:failure,member:subject,coversThrough:coversThrough);
    }

    private static async Task ReportStaleAsync(PairConfig pair,Notice failure,IStore store,CancellationToken ct)
    {
        if(await store.NoticeAsync(pair.PairId,failure.Id,ct) is {} open && open.Matches(failure))
            await new Notices().RaiseAsync(store,pair.PairId,failure with {Cause=TextKey.FailChanged,At=DateTimeOffset.UtcNow,Subject=failure.Subject with {Move=null,FirstRunDeletion=IssueSubject.FirstRunDeletionKind.None}},ct);
    }

    private async Task<bool> CoveredByJobAsync(PairConfig pair,Notice failure,Side side,NoticeItem[] subjects,IStore store,CancellationToken ct)
    {
        // An open job still owns its whole footprint.
        var owner=jobOwner ?? throw new InvalidOperationException(nameof(jobOwner));
        if(failure.Subject.Move is {} pending && (Covered(pending.Source) || Covered(pending.Destination) || pending.Intermediate is {} hop && Covered(hop))) return true;
        if(subjects.Length==0 && await CoveredPath(side,failure.Path)) return true;
        foreach(var subject in subjects)
            if(await CoveredPath(subject.Side,subject.Path)) return true;
        return false;

        bool Covered(string remotePath)=>owner.CoversOpenJob(new(PathNames.LocalSpelling(remotePath),remotePath));
        async Task<bool> CoveredPath(Side endpoint,string path)
        {
            if(endpoint==Side.Dropbox) return Covered(path);
            var saved=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(path),ct);
            return Covered(saved?.DropboxPath ?? await ItemSync.CounterpartAsync(pair,Side.Local,path,store,ct));
        }
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
