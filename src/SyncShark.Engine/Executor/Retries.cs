namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retry a still-current notice, not the operation that once failed.</summary>
public sealed class Retries
{
    private readonly Func<Change,CancellationToken,Task>? checkJob;
    public Retries(Func<Change,CancellationToken,Task>? checkJob=null) => this.checkJob=checkJob;

    /// <summary>Ordinary failures re-enter ItemSync. Put-back owns its exact ordered input until create-only publication settles.</summary>
    public async Task RetryAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice failure, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        if (await store.NoticeAsync(pair.PairId,failure.Id,ct) is not {} current || !current.Matches(failure)) return;
        if (failure.Kind is not (NoticeKind.PutBack or NoticeKind.Failure)) return;
        var side = failure.Subject.Incoming ?? failure.Subject.EndpointSide(failure.Path) ?? Side.Local;
        if (failure.Kind == NoticeKind.Failure)
        {
            var subjects=failure.Subject.Items.Distinct().ToArray();
            // R40: the caller supplies its pair-owned check. Only that owner matches a launch footprint.
            if(checkJob!=null) await checkJob(new Change(Side.Dropbox,ChangeKind.Changed,failure.Path,null,null,null,false,false),ct);
            if(await store.NoticeAsync(pair.PairId,failure.Id,ct) is not {} active || !active.Matches(failure)) return;
            if(failure.Subject.Move!=null) return; // Only the retained move or its accepted job can establish its outcome.
            bool done=true;
            if(subjects.Length==0) done=await Apply(side,failure.Path,null);
            foreach(var subject in subjects)
            {
                var endpoint=subject.Side==Side.Local?local:dropbox;
                if(string.IsNullOrEmpty(subject.Id)){done=false;continue;}
                var at=await endpoint.ReadByIdAsync(subject.Id,ct);
                if(at is not {InScope:true} hit || hit.Location.Entry.Path!=subject.Path) { done=false; continue; }
                done &= await Apply(subject.Side,subject.Path,subject);
            }
            if(done) await store.CloseNoticeAsync(pair.PairId,failure,ct);
            Task<bool> Apply(Side source,string path,NoticeItem? subject)=>ItemSync.ChangeAsync(pair,settings,events,paths,
                new Change(source,ChangeKind.Changed,path,null,subject?.Id,null,source==Side.Local,false),local,dropbox,store,populate,compareOldBinding,ct,heldInput,reviewed:failure,member:subject);
            return;
        }
        var scope = new Scope(failure.Path,failure.Path);
        await FolderDelete.RetryHeldAsync(pair,scope,failure,local,dropbox,store,heldInput,populate,ct);
    }
}
