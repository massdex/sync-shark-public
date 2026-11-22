namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retry a still-current notice, not the operation that once failed.</summary>
public sealed class Retries
{
    private readonly Unfinished? jobOwner;
    public Retries(Unfinished? jobOwner=null) => this.jobOwner=jobOwner;
    /// <summary>Ordinary failures re-enter ItemSync. Put-back owns its exact ordered input until create-only publication settles.</summary>
    public async Task RetryAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice failure, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
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
                if(await ItemSync.ExecuteAsync(pair,settings,events,paths,input.Row,input.Group,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,null,false))
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
                new Change(source,ChangeKind.Changed,path,null,subject?.Id,null,source==Side.Local,false),local,dropbox,store,populate,compareOldBinding,ct,heldInput,reviewed:failure,member:subject);
            return;
        }
        var scope = new Scope(failure.Path,failure.Path);
        await FolderDelete.RetryHeldAsync(pair,scope,failure,local,dropbox,store,heldInput,populate,ct);
    }

    /// <summary>Read the reviewed move by identity and prepare its ordinary planner input; null selects stale turnover.</summary>
    public static async Task<(Row Row,IReadOnlyList<RenameCandidate> Group)?> ObserveMoveAsync(PairConfig pair,PathScope paths,IssueSubject.UnknownMove move,ISide local,ISide dropbox,IStore store,CancellationToken ct)
    {
        var located=await dropbox.ReadByIdAsync(move.ExpectedId,ct);
        if(located is not {InScope:true} at || at.Location.Entry.Id!=move.ExpectedId || at.Location.Entry.IsFolder!=move.IsFolder) return null;
        var remote=at.Location.Entry;
        var binding=await store.FindAsync(pair.PairId,Side.Dropbox,PathNames.DropboxKey(move.Source),ct);
        if(binding==null || binding.DropboxId!=remote.Id) return null;
        var pc=await local.ReadByIdAsync(binding.LocalId,ct);
        if(pc is not {InScope:true} peer || peer.Location.Entry.Id!=binding.LocalId) return null;
        var localEntry=peer.Location.Entry;
        List<RenameCandidate> group=[];
        if(localEntry.Path!=binding.LocalPath) group.Add(new(Side.Local,localEntry,binding));
        if(remote.Path!=binding.DropboxPath) group.Add(new(Side.Dropbox,remote,binding));
        if(group.Count==0) return null;
        return (new Row(binding.LocalKey,localEntry,remote,binding,[],null,RowContext.Neutral with {OutOfScope=paths.Excludes(localEntry.Path,remote.Path,binding.IsFolder)}),group);
    }
}
