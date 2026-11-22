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
            await foreach (var job in store.JobsAsync(pair.PairId,ct))
                if (job.Launch.Any(m => PathNames.Same(m.Path,failure.Path) || PathNames.Same(m.Destination,failure.Path)))
                {
                    await new Unfinished().CheckAsync(pair,settings,events,paths,null,local,dropbox,store,heldInput,populate,compareOldBinding,ct);
                    return;
                }
            var item = failure.Subject.Items.FirstOrDefault(x => x.Side==side && PathNames.Same(x.Path,failure.Path));
            await ItemSync.ApplyAsync(pair,settings,events,paths,new Change(side,ChangeKind.Changed,failure.Path,null,item?.Id,null,side==Side.Local,false),local,dropbox,store,populate,compareOldBinding,ct,heldInput);
            return;
        }
        var scope = new Scope(failure.Path,failure.Path);
        if (await FolderDelete.RetryHeldAsync(pair,scope,failure,local,dropbox,store,heldInput,populate,ct))
            await store.CloseNoticeAsync(pair.PairId,failure,ct);
    }
}
