namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Merge direct children on the written side, then remove only the selected source.</summary>
public sealed class FolderMerge
{
    /// <summary>Move eligible branches whole; recurse only on folder collisions or a definite move-size refusal.</summary>
    public Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Entry moved, Entry destination, Side written, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct,Func<Entry,Entry,Entry?,SyncedEntry?,CancellationToken,Task<EffectOutcome>>? collide=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null) =>
        new MergeWalk(pair,settings,events,paths,written,local,dropbox,store,heldInput,populate,compareOldBinding,ct,collide,coversThrough).Merge(moved,destination);

    private sealed class MergeWalk(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Side written, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct,Func<Entry,Entry,Entry?,SyncedEntry?,CancellationToken,Task<EffectOutcome>>? collide,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough)
    {
        private ISide Writer => written==Side.Local?local:dropbox;
        private ISide Peer => written==Side.Local?dropbox:local;
        private Side Other => PathNames.Other(written);

        internal async Task<RemovalResult> Merge(Entry moved,Entry destination)
        {
            if(!moved.IsFolder || !destination.IsFolder || PathNames.IsSelfOrUnder(destination.Path,moved.Path) || PathNames.IsSelfOrUnder(moved.Path,destination.Path)) throw new ChangedUnderneathException(moved.Path);
            if(await Writer.ReadOneAsync(moved.Path,ct)!=moved || await Writer.ReadOneAsync(destination.Path,ct)!=destination) throw new ChangedUnderneathException(moved.Path);
            var sourceParent=await FolderDelete.MapAsync(pair,store,written,moved.Path,ct);
            var destParent=await FolderDelete.MapAsync(pair,store,written,destination.Path,ct);
            bool retained=false; RemovalResult? unfinished=null;ItemFailedException? deferred=null;
            await foreach(var listed in Writer.ReadAllAsync(moved.Path,new(false,true),ct))
            {
                if(listed.Entry is not {} child || child.Skip is nameof(TextKey.SkipUnreadable) or nameof(TextKey.SkipLink)) {retained=true;continue;}
                try
                {
                    var result=await MergeChildAsync(child,destination.Path,sourceParent,destParent);
                    if(result.Outcome==RemovalOutcome.Retained) retained=true;
                    else if(result.Outcome is not (RemovalOutcome.Removed or RemovalOutcome.Published)) unfinished??=result;
                }
                catch(ItemFailedException wait) when(wait.Disposition==FailureDisposition.TemporaryNoEffect) {deferred??=wait;}
            }
            if(deferred!=null) throw deferred;
            if(unfinished!=null) return unfinished;
            if(retained) return new(RemovalOutcome.Retained,moved,null);
            var saved=await store.FindAsync(pair.PairId,written,PathNames.KeyFor(written,moved.Path),ct);
            if(saved!=null && saved.IdOn(written)!=moved.Id) return new(RemovalOutcome.Retained,moved,TextKey.FailChanged);
            return await new FolderDelete().ApplyAsync(pair,settings,events,paths,moved,saved,written,written==Side.Local?ActionKind.MoveLocal:ActionKind.MoveDropbox,local,dropbox,store,heldInput,populate,compareOldBinding,ct,coversThrough:coversThrough);
        }

        private async Task<RemovalResult> MergeChildAsync(Entry child,string destination,string sourceParent,string destParent)
        {
            var reading=written;
            try
            {
                var to=PathNames.Child(destination,PathNames.Name(child.Path));
                var binding=await store.FindAsync(pair.PairId,written,PathNames.KeyFor(written,child.Path),ct);
                var leaf=PathNames.SpellingOn(Other,PathNames.Name(child.Path));
                var mapped=binding?.PathOn(Other)??PathNames.Child(sourceParent,leaf);
                var destBinding=await store.FindAsync(pair.PairId,written,PathNames.KeyFor(written,to),ct);
                var mappedTo=destBinding?.PathOn(Other)??PathNames.Child(destParent,leaf);
                var actual=written==Side.Local?new Scope(child.Path,mapped):new Scope(mapped,child.Path);
                if(await Protected(actual) || await Protected(written==Side.Local?new(to,mappedTo):new(mappedTo,to))) return new(RemovalOutcome.Retained,child,null);
                async Task<RemovalResult> CollisionOrMerge(Entry at)
                {
                    if(child.IsFolder && at.IsFolder) return await Merge(child,at);
                    if(collide==null) return new(RemovalOutcome.Retained,child,null);
                    reading=Other;var mate=await Peer.ReadOneAsync(mappedTo,ct);reading=written;
                    var outcome=await collide(child,at,mate,binding,ct);
                    return new(outcome switch {EffectOutcome.Applied=>RemovalOutcome.Removed,EffectOutcome.Pending=>RemovalOutcome.Pending,_=>RemovalOutcome.Retained},null,null);
                }
                if(await Writer.ReadOneAsync(to,ct) is {} existing) return await CollisionOrMerge(existing);
                var result=await MoveAsync(child,to,CollisionOrMerge);
                if(result.Outcome==RemovalOutcome.Published && binding!=null && result.Final is {} after && after.Id==child.Id && PathNames.Same(after.Path,to))
                {
                    if(binding.IdOn(written)!=child.Id || destBinding!=null && destBinding!=binding) throw new ChangedUnderneathException(to);
                    reading=Other;var mate=await FindPeerAsync(child,binding,mapped,mappedTo);reading=written;
                    await RelinkAsync(binding,actual,after,mate,to);
                }
                return result;
            }
            catch(Exception e) when(e is ItemFailedException {Disposition:not FailureDisposition.TemporaryNoEffect} or ChangedUnderneathException or PathOccupiedException)
            {
                var failedSide=(e as ChangedUnderneathException)?.FailedSide??(e as PathOccupiedException)?.CollidingSide??reading;
                var path=e switch {ChangedUnderneathException change=>change.Path,PathOccupiedException clash=>clash.Path,_=>((ItemFailedException)e).Path};
                var item=new NoticeItem(written,child.Path,child.Id);
                await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,e is ItemFailedException failure?failure.Cause:e is PathOccupiedException?TextKey.ClashNameTaken:TextKey.FailChanged,path,new([item],failedSide),DateTimeOffset.UtcNow,false),ct);
                return new(RemovalOutcome.Retained,child,null);
            }
        }

        private async Task<RemovalResult> MoveAsync(Entry child,string to,Func<Entry,Task<RemovalResult>> collision)
        {
            RemovalResult result;
            try {result=await Writer.MoveAsync(child,to,ct);}
            catch(ItemFailedException ex) when(child.IsFolder && ex.Cause==TextKey.FailDropboxTooManyFiles && ex.Disposition==FailureDisposition.Permanent) {result=new(RemovalOutcome.NoEffect,child,ex.Cause);}
            catch(PathOccupiedException ex) when(RemovalReceipts.AtTarget(ex,written,to))
            {
                var at=await Writer.ReadOneAsync(to,ct);if(at==null) throw;
                result=await collision(at);
            }
            if(!child.IsFolder || result is not {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.FailDropboxTooManyFiles}) return result;
            try
            {
                var created=await Writer.EnsureFolderAsync(to,ct);
                return created is {Outcome:RemovalOutcome.Published,Final:{IsFolder:true} next}?await Merge(child,next):created;
            }
            catch(PathOccupiedException ex) when(RemovalReceipts.AtTarget(ex,written,to))
            {
                var at=await Writer.ReadOneAsync(to,ct);if(at==null) throw new ChangedUnderneathException(to){FailedSide=written};
                return await collision(at);
            }
        }

        private async Task<Entry> FindPeerAsync(Entry child,SyncedEntry binding,string mapped,string mappedTo)
        {
            if(await Peer.ReadByIdAsync(binding.IdOn(Other),ct) is not {InScope:true} hit || hit.Location.Entry is not {Skip:null} mate || mate.Id!=binding.IdOn(Other) || mate.IsFolder!=child.IsFolder || !(PathNames.Same(mate.Path,mapped) || PathNames.Same(mate.Path,mappedTo)) || await Peer.ReadOneAsync(mate.Path,ct)!=mate) throw new ChangedUnderneathException(mapped);
            return mate;
        }

        private async Task RelinkAsync(SyncedEntry binding,Scope actual,Entry after,Entry mate,string to)
        {
            if(await Writer.ReadOneAsync(to,ct)!=after || await store.FindAsync(pair.PairId,written,binding.KeyOn(written),ct)!=binding) throw new ChangedUnderneathException(to);
            var linked=SyncedEntries.Relink(binding,written==Side.Local?after:mate,written==Side.Dropbox?after:mate);
            var next=new Scope(linked.LocalPath,linked.DropboxPath);
            if(binding.IsFolder) await store.ProjectSubtreeAsync(pair.PairId,new(binding.LocalPath,binding.DropboxPath),next,ct);
            await store.CommitAsync(pair.PairId,binding,linked,null,null,ct);
            await compareOldBinding(binding,actual,next,ct);
        }

        private async Task<bool> Protected(Scope scope)
        {
            if(paths.CoversKey(Side.Local,PathNames.Key(scope.LocalPath)) || paths.CoversKey(Side.Dropbox,PathNames.DropboxKey(scope.DropboxPath)) || paths.HoldsNestedRoot(Side.Local,scope.LocalPath) || paths.HoldsNestedRoot(Side.Dropbox,scope.DropboxPath)) return true;
            if(pair.Exclusions.Any(ex=>PathNames.IsSelfOrUnder(ex.Path,ex.Side==Side.Local?scope.LocalPath:scope.DropboxPath))) return true;
            await foreach(var n in store.NoticesUnderAsync(pair.PairId,scope,ct)) if(n.Cause is TextKey.ReadLocalFailed or TextKey.SkipUnreadable or TextKey.InfoLinks || n.Subject.Move!=null) return true;
            return false;
        }
    }
}
