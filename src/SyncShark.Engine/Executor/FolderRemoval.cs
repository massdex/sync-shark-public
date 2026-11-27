namespace Massdex.SyncShark.Engine.Executor;

/// <summary>One selected folder removal: gather protected paths, acquire the inventory once, then remove, split or keep under that evidence.</summary>
internal sealed class FolderRemoval(PairConfig pair,PathScope paths,Entry folder,SyncedEntry? binding,Side deleting,ActionKind origin,ISide local,ISide dropbox,ISide target,ISide peer,Scope scope,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,bool splitSelected,Row? covered,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough,bool userSelected,TimeProvider clock)
{
    private readonly PrefixSet protectedPaths=new();
    private Dictionary<string,Entry>? frozen;
    private Dictionary<string,int>? frozenChildren;
    private bool cleanCoverage;
    private readonly Dictionary<(Side,string),List<Notice>> tokens=[]; // closable notices read by the protection pass before any effect, keyed by their first member

    /// <summary>Protect, acquire, hand off a replaced parent's held edits, then remove.</summary>
    internal async Task<RemovalResult> RunAsync()
    {
        if(await ProtectedPathsAsync() is {} refused) return refused;
        if(await FreezeAsync() is {} unreadable) return unreadable;
        if(!userSelected && origin==ActionKind.ReplaceKind && await HandOffAsync() is {} handed) return handed;
        return await Remove(folder,binding);
    }

    private async Task Protect(Side side,string path)
    {
        var projected=side==deleting ? path : await FolderDelete.MapAsync(pair,store,side,path,ct);
        protectedPaths.Add(PathNames.KeyFor(deleting,projected));
        if(side!=deleting)
            await foreach(var row in store.DescendantsAsync(pair.PairId,side,PathNames.KeyFor(side,path),ct))
                if(PathNames.IsSelfOrUnder(row.PathOn(deleting),folder.Path)) protectedPaths.Add(row.KeyOn(deleting));
    }

    // Exclusions, nested roots, notice subjects and the children a reused peer folder still holds survive the removal.
    private async Task<RemovalResult?> ProtectedPathsAsync()
    {
        foreach(var ex in pair.Exclusions) if(paths.CoversKey(ex.Side,PathNames.KeyFor(ex.Side,ex.Path))) await Protect(ex.Side,ex.Path);
        foreach(var side in new[]{Side.Local,Side.Dropbox}) foreach(var root in paths.NestedRoots(side)) await Protect(side,root);
        await foreach(var n in store.NoticesUnderAsync(pair.PairId,scope,ct))
        {
            if(ItemSync.Closable(n) && n.Subject.Items[0] is var first) { var at=(first.Side,PathNames.KeyFor(first.Side,first.Path)); (tokens.TryGetValue(at,out var bucket)?bucket:tokens[at]=[]).Add(n); }
            // A read notice vetoes only while its failed PC name may still be unreadable; a user-selected Dropbox-side removal re-reads nothing.
            if(n.Cause is TextKey.ReadLocalFailed or TextKey.SkipUnreadable && (!userSelected || deleting==Side.Local) &&
               await StillUnreadableAsync(local,n,scope,ct)) return new(RemovalOutcome.Retained,folder,n.Cause);
            if(!userSelected && deleting==Side.Dropbox && n.Cause==TextKey.ReadDropboxFailed) return new(RemovalOutcome.Retained,folder,TextKey.ReadLocalFailed); // no automatic Dropbox folder delete over an unread Dropbox file; the returned cause is the unreadable-content one the row settles on
            if(n.Cause==TextKey.InfoLinks || n.Subject.Move!=null || n.Kind==NoticeKind.PutBack)
                foreach(var item in n.Subject.Items) await Protect(item.Side,item.Path);
        }
        if(!userSelected && binding!=null && !pair.Exclusions.Any(ex=>!ex.Keep && ex.Side==deleting && PathNames.Same(ex.Path,folder.Path)) && await peer.ReadOneAsync(binding.PathOn(peer.Side),ct) is {IsFolder:true} reused && origin is ActionKind.DeleteLocal or ActionKind.DeleteDropbox)
        {
            if(reused.Id==binding.IdOn(peer.Side)) return new(RemovalOutcome.Retained,folder,null);
            await foreach(var row in peer.ReadAllAsync(reused.Path,new(true,true),ct))
            {
                if(row.Entry is not {} entry) continue;
                if(entry.Skip==nameof(TextKey.SkipUnreadable)) return new(RemovalOutcome.Retained,folder,TextKey.ReadLocalFailed);
                var child=await store.FindAsync(pair.PairId,peer.Side,PathNames.KeyFor(peer.Side,row.Path),ct);
                if(child!=null && child.IdOn(peer.Side)==entry.Id) protectedPaths.Add(child.KeyOn(deleting));
            }
        }
        return null;
    }

    // An open PC read notice vetoes a removal or merge over the scope unless its failed PC name (the notice path) is under the
    // scope's PC path and is now absent or a readable unskipped file. A folder, a name outside the scope or any read error keeps the veto.
    internal static async Task<bool> StillUnreadableAsync(ISide local,Notice n,Scope scope,CancellationToken ct)
    {
        if(!PathNames.IsSelfOrUnder(n.Path,scope.LocalPath)) return true;
        try
        {
            if(await local.ReadOneAsync(n.Path,ct) is not {} now) return false;
            return now.IsFolder || now.Skip!=null || !ContentHash.IsValid(await local.ContentHashAsync(now,ct));
        }
        catch(Exception e) when(e is ItemFailedException or ChangedUnderneathException or InventoryIncompleteException) {return true;}
    }

    // Acquire once before mutation: an unreadable late sibling vetoes the entire removal.
    private async Task<RemovalResult?> FreezeAsync()
    {
        frozen=userSelected?new(PathNames.Comparer):null;
        frozenChildren=userSelected?new(PathNames.Comparer):null;
        cleanCoverage=!userSelected && covered is {Context:{CompleteInventory:true,ProtectedContents:false,UnreadableCause:null}} && covered.Synced==binding && covered.EntryOn(deleting)==folder && covered.EntryOn(peer.Side)==null && !protectedPaths.Overlaps(PathNames.Key(folder.Path));
        if(!cleanCoverage && (userSelected || deleting==Side.Local || protectedPaths.Overlaps(PathNames.Key(folder.Path))))
        {
            await foreach(var row in target.ReadAllAsync(folder.Path,new(true,true),ct))
            {
                if(row.Entry is not {} child || child.Skip==nameof(TextKey.SkipUnreadable))
                {
                    await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,deleting==Side.Local?TextKey.ReadLocalFailed:TextKey.ReadDropboxFailed,row.Path,new([new(deleting,row.Path,row.Entry?.Id??string.Empty)],deleting),DateTimeOffset.UtcNow,false),ct);
                    return new(RemovalOutcome.Retained,folder,TextKey.ReadLocalFailed);
                }
                if(frozen!=null)
                {
                    frozen.Add(PathNames.KeyFor(deleting,row.Path),child);
                    var parent=PathNames.KeyFor(deleting,PathNames.Parent(row.Path));frozenChildren![parent]=frozenChildren!.GetValueOrDefault(parent)+1;
                }
                if(child.Skip==nameof(TextKey.SkipLink)) await Protect(deleting,row.Path);
                // Edit beats delete: a bound PC file whose size, ID or modified time left its binding may hold the only copy of its version.
                else if(deleting==Side.Local && !userSelected && !child.IsFolder && await store.FindAsync(pair.PairId,Side.Local,PathNames.KeyFor(Side.Local,row.Path),ct) is {IsFolder:false} bound && Planner.Decide.LocalChanged(child,bound,null,false)!=false) await Protect(Side.Local,row.Path);
            }
        }
        return null;
    }

    // The conflict owner may move either version. Never continue the stale parent replacement after handing it off.
    private async Task<RemovalResult?> HandOffAsync()
    {
        var capturedAt=DateTimeOffset.UtcNow;
        var before=heldInput(scope);
        if(!before.Any(c=>c.Kind!=ChangeKind.Deleted && c.Side==deleting && c.Path!=null && PathNames.IsSelfOrUnder(c.Path,folder.Path))) return null;
        var applied=await HeldRecovery.RecoverHeldAsync(pair,scope,deleting,local,dropbox,store,_=>before,populate,ct,preRemoval:true,capturedAt:capturedAt,origin:origin,resolveConflict:resolveConflict);
        return new(RemovalOutcome.Retained,folder,applied?null:TextKey.FailReplacementRetained);
    }

    private async Task<bool> Admitted()
    {
        if(coversThrough is not {} admits) return false;
        var pc=local.BoundaryAsync(ct);var db=dropbox.BoundaryAsync(ct);await Task.WhenAll(pc,db);
        return (await pc).Position is {} position && (await db).Cursor is {Length:>0} cursor && admits((position,cursor));
    }

    // Remove one entry of the selected tree; a partially protected or oversized folder is removed child by child.
    private async Task<RemovalResult> Remove(Entry entry,SyncedEntry? saved)
    {
        var key=PathNames.KeyFor(deleting,entry.Path);
        bool partial=false;
        if(protectedPaths.Keys.Any() || entry.Skip!=null)
        {
            var verdict=Planner.FolderDeletes.Classify(protectedPaths.Covers(key),false,entry.Skip==nameof(TextKey.SkipLink),entry.Skip==nameof(TextKey.SkipUnreadable),false);
            partial=!Planner.FolderDeletes.SelectBranch(verdict,protectedPaths.Overlaps(key),false);
            if(verdict==Planner.FolderDeletes.FolderVerdict.Survives && origin is ActionKind.DeleteLocal or ActionKind.DeleteDropbox)
            {
                // Kept like a kept-only partial. Exclusions, nested roots and links are expected; only an unreadable folder needs its own notice.
                if(entry.Skip!=nameof(TextKey.SkipUnreadable)) return new(RemovalOutcome.Retained,entry,TextKey.FailExcludeHoldsProtected);
                await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,deleting==Side.Local?TextKey.ReadLocalFailed:TextKey.ReadDropboxFailed,entry.Path,new([new(deleting,entry.Path,entry.Id)],deleting),DateTimeOffset.UtcNow,false),ct);
                return new(RemovalOutcome.Retained,entry,TextKey.ReadLocalFailed);
            }
            if(verdict==Planner.FolderDeletes.FolderVerdict.Survives || partial && origin==ActionKind.ReplaceKind) return new(RemovalOutcome.Retained,entry,TextKey.FailReplacementRetained);
        }
        RemovalSelection? selection=null;
        if(deleting==Side.Local)
        {
            if(await RemovalReceipts.NamedAsync(local,entry,paths,ct) is not {} named) return new(RemovalOutcome.Unknown,entry,TextKey.FailChanged);
            var volume=named.Domain;
            if(pair.Roots!=null && pair.Roots.LocalVolumeKey()!=volume) return new(RemovalOutcome.Unknown,entry,TextKey.RootLocalIdentityChanged);
            selection=new(new(entry,volume,entry.IsFolder || entry.Skip==nameof(TextKey.SkipOnlineOnly)?null:await local.ContentHashAsync(entry,ct)),saved);
        }
        if(!userSelected && coversThrough==null) return new(RemovalOutcome.Retained,entry,TextKey.RetryRecoveryRequired);
        var splitRoot=splitSelected && entry==folder;
        string? cursor=null;
        RemovalResult removal;
        try {if(partial || splitRoot) removal=new(RemovalOutcome.Retained,entry,null); else (removal,cursor)=await DeleteSelected(entry,key,selection);}
        catch(ItemFailedException e) when(entry.IsFolder && e.Cause==TextKey.FailDropboxTooManyFiles && e.Disposition==FailureDisposition.Permanent) {removal=new(RemovalOutcome.NoEffect,entry,e.Cause);}
        if(partial || splitRoot || removal is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.FailDropboxTooManyFiles} && entry.IsFolder && origin is (ActionKind.DeleteLocal or ActionKind.DeleteDropbox))
        {
            if(await SplitAsync(entry,partial) is {} unsettled) return unsettled;
            if(partial) return removal with {Cause=TextKey.FailExcludeHoldsProtected}; // every removable child is gone; only protected content keeps the folder
            (removal,cursor)=await DeleteSelected(entry,key,selection);
        }
        return await SettleAsync(entry,saved,key,selection,removal,cursor);
    }

    // Remove each child first; any unsettled child keeps the parent.
    private async Task<RemovalResult?> SplitAsync(Entry entry,bool partial)
    {
        RemovalResult? unsettled=null;ItemFailedException? deferred=null;
        await foreach(var row in target.ReadAllAsync(entry.Path,new(false,true),ct))
        {
            if(row.Entry is not {} child || child.Skip==nameof(TextKey.SkipUnreadable)) return new(RemovalOutcome.Retained,entry,TextKey.ReadLocalFailed);
            try
            {
                if(frozen!=null && frozen.GetValueOrDefault(PathNames.KeyFor(deleting,child.Path))!=child) return new(RemovalOutcome.Retained,entry,TextKey.FailChanged);
                // The PC adapter can neither hash nor send a lone skipped file (only online-only), so it stays with its protected parent.
                if(deleting==Side.Local && !child.IsFolder && child.Skip is not (null or nameof(TextKey.SkipOnlineOnly))) { protectedPaths.Add(PathNames.KeyFor(deleting,child.Path)); continue; }
                var part=await Remove(child,await store.FindAsync(pair.PairId,deleting,PathNames.KeyFor(deleting,child.Path),ct));
                if(part.Outcome is RemovalOutcome.Unknown or RemovalOutcome.Pending) return part; // an unresolved child outranks any sibling refusal
                if(part.Outcome!=RemovalOutcome.Removed && !(partial && part.Outcome==RemovalOutcome.Retained && protectedPaths.Overlaps(PathNames.KeyFor(deleting,child.Path)))) unsettled??=part;
            }
            catch(ItemFailedException wait) when(wait.Disposition==FailureDisposition.TemporaryNoEffect) {deferred=ItemFailedException.Later(deferred,wait,clock);}
            catch(ItemFailedException unknown) when(unknown.Disposition==FailureDisposition.UnknownEffect)
            {
                return new(RemovalOutcome.Unknown,child,deleting==Side.Dropbox?TextKey.FailDropboxOutcomeUnknown:unknown.Cause){Provider=unknown.Provider}; // an unresolved child send, never a plain refusal
            }
            catch(ItemFailedException error)
            {
                await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,error.Cause,child.Path,new([new(deleting,child.Path,child.Id)],deleting),DateTimeOffset.UtcNow,false),ct);
                unsettled??=new(RemovalOutcome.Retained,entry,error.Cause){Provider=error.Provider};
            }
        }
        if(deferred!=null) throw deferred;
        if(unsettled!=null) return new(RemovalOutcome.Retained,entry,unsettled.Cause){Provider=unsettled.Provider};
        return null;
    }

    // Delete only the exact acquired tree; the Dropbox window starts at the returned cursor.
    private async Task<(RemovalResult Removal,string? Cursor)> DeleteSelected(Entry entry,string key,RemovalSelection? selection)
    {
        string? cursor=null;
        if(frozen!=null)
        {
            if(deleting==Side.Dropbox && (cursor=(await dropbox.BoundaryAsync(ct)).Cursor) is not {Length:>0}) return (new(RemovalOutcome.Retained,entry,TextKey.FailChanged),cursor);
            if(await target.ReadOneAsync(entry.Path,ct)!=entry) return (new(RemovalOutcome.Retained,entry,TextKey.FailChanged),cursor);
            if(entry.IsFolder)
            {
                var counts=new Dictionary<string,int>(PathNames.Comparer){[key]=0};
                await foreach(var row in target.ReadAllAsync(entry.Path,new(true,true),ct))
                {
                    var childKey=PathNames.KeyFor(deleting,row.Path);
                    if(row.Entry==null || frozen.GetValueOrDefault(childKey)!=row.Entry) return (new(RemovalOutcome.Retained,entry,TextKey.FailChanged),cursor);
                    var parent=PathNames.KeyFor(deleting,PathNames.Parent(row.Path)); counts[parent]=counts.GetValueOrDefault(parent)+1;
                    if(row.Entry.IsFolder) counts.TryAdd(childKey,0);
                }
                if(counts.Any(x=>frozenChildren!.GetValueOrDefault(x.Key)!=x.Value)) return (new(RemovalOutcome.Retained,entry,TextKey.FailChanged),cursor);
            }
        }
        else if(!await Admitted()) return (new(RemovalOutcome.Retained,entry,TextKey.RetryRecoveryRequired),cursor);
        return (deleting==Side.Dropbox?await target.DeleteAsync(entry,null,origin,ct):await target.DeleteAsync(entry,selection,ct),cursor);
    }

    // A wrong bin occupant is put back; only a proved removal goes on to settlement.
    private async Task<RemovalResult> SettleAsync(Entry entry,SyncedEntry? saved,string key,RemovalSelection? selection,RemovalResult removal,string? cursor)
    {
        if(removal.RecycledVictim is {} victim && selection!=null && victim.Domain==selection.Proof.VolumeId && (victim.Entry.Id!=entry.Id || removal.Outcome==RemovalOutcome.Retained && ContentHash.IsValid(victim.Entry.ContentHash) && victim.Entry.ContentHash!=selection.Proof.ContentHash))
        {
            var subject=new Scope(entry.Path,saved?.DropboxPath??entry.Path);
            var restored=await HeldRecovery.PutBackSubstituteAsync(pair,subject,victim,local,dropbox,store,populate,ct);
            if(restored) await store.DisarmAsync(pair.PairId,selection,null,ct);
            return removal with {Outcome=restored?RemovalOutcome.WrongRestored:RemovalOutcome.Unknown};
        }
        if(removal.Outcome!=RemovalOutcome.Removed)
        {
            if(removal.Outcome==RemovalOutcome.NoEffect && selection!=null) await store.DisarmAsync(pair.PairId,selection,null,ct);
            return removal;
        }
        var suffix=entry.Path[folder.Path.Length..]; var footprint=new Scope(saved?.LocalPath??scope.LocalPath+suffix,saved?.DropboxPath??scope.DropboxPath+suffix);
        // The launch checker recovers the whole split only after its post-effect capture.
        if(splitSelected && deleting==Side.Dropbox && origin==ActionKind.DeleteDropbox) return removal;
        if(!userSelected && !await Admitted()) return entry==folder ? new(RemovalOutcome.Pending,null,null) : new(RemovalOutcome.Retained,null,TextKey.RetryRecoveryRequired);
        var receiptEntry=removal.Reply is JobReply.Terminal {Members:[({Skip:null} hit,null,null)],AbsentOrdinals.Count:0} && hit.Id==entry.Id && hit.IsFolder==entry.IsFolder?hit:null;
        bool uncertain=deleting==Side.Dropbox && (receiptEntry==null || !PathNames.Same(receiptEntry.Path,entry.Path));
        var capturedAt=DateTimeOffset.UtcNow;
        IEnumerable<Change> reports=userSelected?[]:heldInput(footprint);
        if(frozen!=null)
        {
            if(deleting==Side.Local && entry.IsFolder && await RecycledLateAsync(entry,selection,removal,frozen) is {} late) return late;
            if(deleting==Side.Dropbox)
            {
                capturedAt=DateTimeOffset.UtcNow;
                if(await DropboxEditsAsync(entry,receiptEntry,cursor,capturedAt,frozen) is not {} edits) return new(RemovalOutcome.Unknown,null,TextKey.FailChanged);
                reports=edits;
            }
        }
        // A later held delete must supersede a receipt, not be overwritten by it.
        if(!userSelected && removal.Recycled!=null && !cleanCoverage && origin is (ActionKind.MoveLocal or ActionKind.MoveDropbox)) reports=(await ArrivalsAsync(removal.Recycled)).Concat(reports);
        if(uncertain) return removal with {Outcome=RemovalOutcome.Unknown,Cause=TextKey.FailDropboxOutcomeUnknown};
        if(!await HeldRecovery.RecoverHeldAsync(pair,footprint,deleting,local,dropbox,store,_=>reports,populate,ct,capturedAt:capturedAt,origin:origin,resolveConflict:resolveConflict,selectedCloudWindow:frozen!=null && deleting==Side.Dropbox)) return new(RemovalOutcome.Retained,null,TextKey.RetryRecoveryRequired);
        return await RetireAsync(entry,saved,key,selection,removal,footprint);
    }

    // A recycled folder's receipt must name the selection; every item that arrived after acquisition is reported.
    private async Task<RemovalResult?> RecycledLateAsync(Entry entry,RemovalSelection? selection,RemovalResult removal,Dictionary<string,Entry> acquired)
    {
        if(removal.RecycledVictim is not {} receipt || receipt.Domain!=selection?.Proof.VolumeId || receipt.Entry.Id!=entry.Id || !receipt.Entry.IsFolder || removal.Recycled==null) return new(RemovalOutcome.Unknown,null,TextKey.RecycleUnchecked);
        await foreach(var row in removal.Recycled.WithCancellation(ct))
        {
            if(row.Entry is not {} item || item.Skip==nameof(TextKey.SkipUnreadable)) return new(RemovalOutcome.Unknown,null,TextKey.RecycleUnchecked);
            if(acquired.GetValueOrDefault(PathNames.Key(row.Path))!=item)
                await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailRecycledLate,row.Path,new([new(deleting,row.Path,item.Id)],deleting),DateTimeOffset.UtcNow,false),ct);
        }
        return null;
    }

    // Edits inside the selected Dropbox window stay held, each with its exact PutBack version. Null: the window was not read whole.
    private async Task<IEnumerable<Change>?> DropboxEditsAsync(Entry entry,Entry? receiptEntry,string? cursor,DateTimeOffset capturedAt,Dictionary<string,Entry> acquired)
    {
        var end=await dropbox.BoundaryAsync(ct);if(end.Cursor is not {Length:>0} || cursor==null) return null;
        var actualRoot=receiptEntry?.Path;
        var edits=new Dictionary<string,Change>(PathNames.Comparer);
        while(cursor!=end.Cursor)
        {
            var page=await dropbox.ReadChangesAsync(cursor,null,end,ct);
            if(page.ContinuityLost || !page.CaughtUp && (page.Cursor==null || page.Cursor==cursor)) return null;
            foreach(var change in page.Changes)
            {
                if(change.Path is not {} path || !(PathNames.IsSelfOrUnder(path,entry.Path) || actualRoot!=null && !Path.IsPathRooted(actualRoot) && !paths.Excludes(null,path,false) && PathNames.IsSelfOrUnder(path,actualRoot)) || change.Kind==ChangeKind.Deleted) continue;
                edits[PathNames.DropboxKey(path)]=change;
            }
            if(page.CaughtUp) break;
            cursor=page.Cursor;
        }
        var reports=edits.Values.Where(change=>change.Observed is not {} observed ||
            (PathNames.Same(observed.Path,entry.Path)?entry:acquired.GetValueOrDefault(PathNames.DropboxKey(observed.Path)))!=observed);
        foreach(var change in reports)
        {
            if(change.Observed is not {IsFolder:false,Skip:null} v || change.Id!=v.Id || !PathNames.Same(change.Path,v.Path) || string.IsNullOrEmpty(v.Revision) || !ContentHash.IsValid(v.ContentHash) || pair.Roots?.Dropbox.RootNamespaceId is not {Length:>0} ns) continue;
            Notice? prior=null;
            await foreach(var n in store.NoticesAtAsync(pair.PairId,PathNames.DropboxKey(v.Path),ct)) if(n.Kind==NoticeKind.PutBack){prior=n;break;}
            var subject=new IssueSubject([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox){DropboxVersion=new(ns,PathNames.RootedDropboxPath(pair.DropboxRoot,v.Path),v)};
            if(prior?.Subject.DropboxVersion!=subject.DropboxVersion) await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.RetryRecoveryRequired,v.Path,subject,capturedAt,false),ct);
        }
        return reports;
    }

    // A moved tree's recycled children that differ from their records arrive as Local changes.
    private async Task<List<Change>> ArrivalsAsync(IAsyncEnumerable<Listed> recycled)
    {
        var arrivals=new List<Change>();
        await foreach(var row in recycled.WithCancellation(ct))
        {
            if(row.Entry is not {Skip:null} child) continue;
            var record=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(row.Path),ct);
            if(record==null || record.LocalId==child.Id && !child.IsFolder && (record.LocalSize!=child.Size || record.LocalModifiedTicks!=child.Modified.UtcTicks || ContentHash.IsValid(child.ContentHash) && child.ContentHash!=record.DropboxContentHash))
                arrivals.Add(new(Side.Local,ChangeKind.Changed,row.Path,null,child.Id,child,true,false));
        }
        return arrivals;
    }

    // Only after held reports are recovered is the binding retired and the selection disarmed.
    private async Task<RemovalResult> RetireAsync(Entry entry,SyncedEntry? saved,string key,RemovalSelection? selection,RemovalResult removal,Scope footprint)
    {
        if(frozen?.Remove(key)==true) frozenChildren![PathNames.KeyFor(deleting,PathNames.Parent(entry.Path))]--;
        if(saved!=null)
        {
            await compareOldBinding(saved,footprint,footprint,ct);
            // Effects owns its root commit; direct and recursive deletes have no outer writer.
            if(origin is (ActionKind.DeleteLocal or ActionKind.DeleteDropbox) && (entry!=folder || covered==null && !splitSelected) && await store.FindAsync(pair.PairId,deleting,saved.KeyOn(deleting),ct)==saved)
                await store.CommitAsync(pair.PairId,saved,null,null,null,ct,closing:tokens.Count==0?null:([..new[]{Side.Local,Side.Dropbox}.SelectMany(s=>tokens.GetValueOrDefault((s,saved.KeyOn(s)))??[]).Where(n=>ItemSync.Exact(n,saved))],()=>!heldInput(footprint).Any())); // the removed item's own earlier failures close in its removal commit unless a report arrived after the recovered cohort
        }
        if(selection!=null) await store.DisarmAsync(pair.PairId,selection,null,ct);
        return removal;
    }
}
