namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retry a still-current notice, not the operation that once failed.</summary>
public sealed class Retries
{
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
        var reports = new List<Change>(); Entry? version = null;
        var member = failure.Subject.Items.SingleOrDefault(i=>i.Side==side && PathNames.Same(i.Path,failure.Path));
        if (member is null) throw new ItemFailedException(failure.Path,TextKey.RecoveryIncomplete,null);
        foreach (var report in heldInput(scope))
        {
            if (report.Side!=side || !PathNames.Same(report.Path,failure.Path)) continue;
            // A path-only tombstone belongs to the preceding version, not a later reuse of its name.
            if (report.Kind==ChangeKind.Deleted && (report.Id==member.Id || report.Id is null && version!=null))
            { reports.Add(report); version=null; continue; }
            if (report.Observed is not {} observed || observed.Id!=member.Id)
            { if (report.Observed!=null) version=null; continue; }
            version=observed; reports.Add(report);
        }
        // Missing identifying input is not permission to restore whatever now has this name.
        if (version is null || paths.Excludes(scope.LocalPath,scope.DropboxPath,version.IsFolder))
            throw new ItemFailedException(failure.Path,TextKey.RecoveryIncomplete,null);
        var source = side==Side.Local ? local : dropbox;
        var destination = side==Side.Local ? dropbox : local;
        var at = await source.ReadOneAsync(version.Path,ct);
        if (at!=version) throw new ItemFailedException(version.Path,TextKey.RecoveryIncomplete,null);
        if (await destination.ReadOneAsync(failure.Path,ct)!=null) throw new PathOccupiedException(failure.Path);
        RemovalResult published;
        if (version.IsFolder)
        {
            var location=await source.ReadByIdAsync(version.Id,ct);
            if (location is not {InScope:true} found || await source.RecoverAsync(found.Location,version,ct) is not Recovery.Folder contents || contents.Root!=version)
                throw new ItemFailedException(version.Path,TextKey.RecoveryIncomplete,null);
            published=await destination.CreateFolderExclusiveAsync(failure.Path,ct);
            if (published.Outcome==RemovalOutcome.Published) await populate(scope,side,contents,ct);
        }
        else
        {
            await using var bytes=await source.OpenReadAsync(version,ct);
            await using var staged=await destination.StageAsync(failure.Path,bytes,version,null,ct);
            published=await destination.PublishAsync(staged,null,null,ct);
        }
        if (published is not {Outcome:RemovalOutcome.Published,Final:{} final} || final.IsFolder!=version.IsFolder ||
            !version.IsFolder && (final.Size!=version.Size || final.ContentHash!=version.ContentHash))
            throw new ItemFailedException(version.Path,published.Cause ?? TextKey.RecoveryIncomplete,null);
        var before=await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,version.Path),ct);
        await store.CommitAsync(pair.PairId,before,SyncedEntries.From(side,version,final),published.LocalPublished,null,ct);
        await store.CloseNoticeAsync(pair.PairId,failure,ct);
        foreach (var report in reports) report.ConsumeHeld?.Invoke();
    }
}
