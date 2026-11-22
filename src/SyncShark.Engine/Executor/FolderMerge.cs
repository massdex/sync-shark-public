namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Merge direct children on the written side, then remove only the selected source.</summary>
public sealed class FolderMerge
{
    /// <summary>Move eligible branches whole; recurse only on folder collisions or a definite move-size refusal.</summary>
    public async Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Entry moved, Entry destination, Side written, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct,Func<Entry,Entry,Entry?,SyncedEntry?,CancellationToken,Task<EffectOutcome>>? collide=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null)
    {
        var side=written==Side.Local?local:dropbox;
        if(!moved.IsFolder || !destination.IsFolder || PathNames.IsSelfOrUnder(destination.Path,moved.Path) || PathNames.IsSelfOrUnder(moved.Path,destination.Path)) throw new ChangedUnderneathException(moved.Path);
        if(await side.ReadOneAsync(moved.Path,ct)!=moved || await side.ReadOneAsync(destination.Path,ct)!=destination) throw new ChangedUnderneathException(moved.Path);
        var other=PathNames.Other(written);
        var sourceParent=await FolderDelete.MapAsync(pair,store,written,moved.Path,ct);
        var destParent=await FolderDelete.MapAsync(pair,store,written,destination.Path,ct);
        bool retained=false; RemovalResult? unfinished=null;
        await foreach(var listed in side.ReadAllAsync(moved.Path,new(false,true),ct))
        {
            if(listed.Entry is not {} child || child.Skip is nameof(TextKey.SkipUnreadable) or nameof(TextKey.SkipLink)) {retained=true;continue;}
            try
            {
                var to=PathNames.Child(destination.Path,PathNames.Name(child.Path));
                var binding=await store.FindAsync(pair.PairId,written,PathNames.KeyFor(written,child.Path),ct);
                var leaf=PathNames.SpellingOn(other,PathNames.Name(child.Path));
                var mapped=binding?.PathOn(other)??PathNames.Child(sourceParent,leaf);
                var destBinding=await store.FindAsync(pair.PairId,written,PathNames.KeyFor(written,to),ct);
                var mappedTo=destBinding?.PathOn(other)??PathNames.Child(destParent,leaf);
                var actual=written==Side.Local?new Scope(child.Path,mapped):new Scope(mapped,child.Path);
                if(await Protected(actual) || await Protected(written==Side.Local?new(to,mappedTo):new(mappedTo,to))) {retained=true;continue;}
                var existing=await side.ReadOneAsync(to,ct);
                RemovalResult result;
                if(existing==null)
                {
                    try { result=await side.MoveAsync(child,to,ct); }
                    catch(ItemFailedException ex) when(child.IsFolder && ex.Cause==TextKey.FailDropboxTooManyFiles && ex.Disposition==FailureDisposition.Permanent) {result=new(RemovalOutcome.NoEffect,child,ex.Cause);}
                    catch(PathOccupiedException ex)
                    {
                        if(!Effects.AtTarget(ex,written,to)) throw;
                        var incumbent=await side.ReadOneAsync(to,ct);
                        if(incumbent==null) throw;
                        if(child.IsFolder && incumbent.IsFolder) result=await Merge(child,incumbent);
                        else result=await Collision(incumbent);
                    }
                    if(child.IsFolder && result is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.FailDropboxTooManyFiles})
                    {
                        var created=await side.EnsureFolderAsync(to,ct);
                        result=created is {Outcome:RemovalOutcome.Published,Final:{IsFolder:true} next}?await Merge(child,next):created;
                    }
                    else if(result.Outcome==RemovalOutcome.Published && binding!=null && result.Final is {} after && after.Id==child.Id && PathNames.Same(after.Path,to))
                        await compareOldBinding(binding,actual,written==Side.Local?actual with {LocalPath=to}:actual with {DropboxPath=to},ct);
                }
                else if(existing.IsFolder && child.IsFolder) result=await Merge(child,existing);
                else result=await Collision(existing);
                if(result.Outcome==RemovalOutcome.Retained) retained=true;
                else if(result.Outcome is not (RemovalOutcome.Removed or RemovalOutcome.Published)) unfinished??=result;
                async Task<RemovalResult> Collision(Entry incumbent)
                {
                    if(collide==null) return new(RemovalOutcome.Retained,child,null);
                    var peer=written==Side.Local?dropbox:local;
                    var at=await peer.ReadOneAsync(mappedTo,ct);
                    var outcome=await collide(child,incumbent,at,binding,ct);
                    return new(outcome switch {EffectOutcome.Applied=>RemovalOutcome.Removed,EffectOutcome.Pending=>RemovalOutcome.Pending,_=>RemovalOutcome.Retained},null,null);
                }
            }
            catch(ItemFailedException e)
            {
                await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,e.Cause,child.Path,new([new(written,child.Path,child.Id)],written),DateTimeOffset.UtcNow,false),ct);
                retained=true;
            }
        }
        if(unfinished!=null) return unfinished;
        if(retained) return new(RemovalOutcome.Retained,moved,null);
        var saved=await store.FindAsync(pair.PairId,written,PathNames.KeyFor(written,moved.Path),ct);
        if(saved!=null && saved.IdOn(written)!=moved.Id) return new(RemovalOutcome.Retained,moved,TextKey.FailChanged);
        return await new FolderDelete().ApplyAsync(pair,settings,events,paths,moved,saved,written,written==Side.Local?ActionKind.MoveLocal:ActionKind.MoveDropbox,local,dropbox,store,heldInput,populate,compareOldBinding,ct);

        Task<RemovalResult> Merge(Entry source,Entry dest)=>ApplyAsync(pair,settings,events,paths,source,dest,written,local,dropbox,store,heldInput,populate,compareOldBinding,ct,collide);
        async Task<bool> Protected(Scope scope)
        {
            if(paths.CoversKey(Side.Local,PathNames.Key(scope.LocalPath)) || paths.CoversKey(Side.Dropbox,PathNames.DropboxKey(scope.DropboxPath)) || paths.HoldsNestedRoot(Side.Local,scope.LocalPath) || paths.HoldsNestedRoot(Side.Dropbox,scope.DropboxPath)) return true;
            if(pair.Exclusions.Any(ex=>PathNames.IsSelfOrUnder(ex.Path,ex.Side==Side.Local?scope.LocalPath:scope.DropboxPath))) return true;
            await foreach(var n in store.NoticesUnderAsync(pair.PairId,scope,ct)) if(n.Cause is TextKey.ReadLocalFailed or TextKey.SkipUnreadable or TextKey.InfoLinks || n.Subject.Move!=null) return true;
            return false;
        }
    }
}
