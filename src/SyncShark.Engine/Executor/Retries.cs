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
            var subjects=failure.Subject.Items.Distinct().ToArray();
            // R40: Host/Repair runs the pair's retained work and accepted-job owner before ordinary retry.
            if(failure.Subject.Move is {} move)
            {
                var located=await dropbox.ReadByIdAsync(move.ExpectedId,ct);
                if(located is not {InScope:true} at || at.Location.Entry.Id!=move.ExpectedId || at.Location.Entry.IsFolder!=move.IsFolder)
                { await Stale(); return; }
                var remote=at.Location.Entry;
                var binding=await store.FindAsync(pair.PairId,Side.Dropbox,PathNames.DropboxKey(move.Source),ct);
                if(binding==null || binding.DropboxId!=remote.Id) { await Stale(); return; }
                var pc=await local.ReadByIdAsync(binding.LocalId,ct);
                if(pc is not {InScope:true} peer || peer.Location.Entry.Id!=binding.LocalId) { await Stale(); return; }
                List<RenameCandidate> group=[];
                if(peer.Location.Entry.Path!=binding.LocalPath) group.Add(new(Side.Local,peer.Location.Entry,binding));
                if(remote.Path!=binding.DropboxPath) group.Add(new(Side.Dropbox,remote,binding));
                if(group.Count==0) { await Stale(); return; }
                if(await ItemSync.ExecuteAsync(pair,settings,events,paths,null,group,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,null,false))
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
}
