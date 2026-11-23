namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Remove the selected identity without giving away held child evidence.</summary>
public sealed class FolderDelete
{
    /// <summary>The binding overload receives the side where deletion was observed.</summary>
    public async Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncedEntry folder, Side deleting, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct,Row? covered=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null)
    {
        if(await store.FindAsync(pair.PairId,deleting,folder.KeyOn(deleting),ct)!=folder) return new(RemovalOutcome.Retained,null,TextKey.FailChanged);
        var change=new Change(deleting,ChangeKind.Deleted,folder.PathOn(deleting),null,folder.IdOn(deleting),null,false,deleting==Side.Local);
        bool retained=false;
        try
        {
            var settled=await ItemSync.ChangeAsync(pair,settings,events,paths,change,local,dropbox,store,populate,compareOldBinding,ct,heldInput,(_,reproducible)=>retained=!reproducible,coversThrough:coversThrough);
            return new(settled?RemovalOutcome.Removed:RemovalOutcome.Retained,null,null);
        }
        catch(InventoryIncompleteException e) when(retained && e.Message==nameof(RemovalOutcome))
        {
            // RequireRemovalAsync signals a non-removal to queue callers; this overload returns that disposition.
            return new(RemovalOutcome.Retained,null,null);
        }
    }

    /// <summary>Choose the missing ancestor before the caller acquires its exact footprint.</summary>
    public static async Task<SyncedEntry?> HighestMissingAsync(PairConfig pair,PathScope paths,SyncedEntry folder,ISide source,IStore store,CancellationToken ct)
    {
        var deleting=source.Side;
        if(await source.ReadByIdAsync(folder.IdOn(deleting),ct) is not null) return null;
        foreach(var ancestor in PathNames.Ancestors(folder.PathOn(deleting)))
        {
            var parent=await store.FindAsync(pair.PairId,deleting,PathNames.KeyFor(deleting,ancestor),ct);
            if(parent is not {IsFolder:true} || paths.ExcludesBinding(parent)) break;
            if(await source.ReadByIdAsync(parent.IdOn(deleting),ct) is not null) break;
            folder=parent;
        }
        return folder;
    }

    /// <summary>Returns the actual removal result. Dispatcher saves Pending; no binding is retired on acceptance.</summary>
    public async Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Entry folder, SyncedEntry? binding, Side deleting, ActionKind origin, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct,bool splitSelected=false,Row? covered=null,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,bool userSelected=false)
    {
        var target=deleting==Side.Local?local:dropbox; var peer=deleting==Side.Local?dropbox:local;
        var scope=new Scope(binding?.LocalPath??PathNames.LocalSpelling(folder.Path),binding?.DropboxPath??folder.Path);
        if(splitSelected && (deleting!=Side.Dropbox || origin!=ActionKind.DeleteDropbox || coversThrough==null)) return new(RemovalOutcome.Retained,folder,TextKey.FailChanged);
        var protectedPaths=new PrefixSet();
        if(!folder.IsFolder || folder.Path.Length==0 || binding!=null && (binding.IdOn(deleting)!=folder.Id || !PathNames.Same(binding.PathOn(deleting),folder.Path))) return new(RemovalOutcome.Unknown,folder,TextKey.FailChanged);
        async Task Protect(Side side,string path)
        {
            var projected=side==deleting ? path : await MapAsync(pair,store,side,path,ct);
            protectedPaths.Add(PathNames.KeyFor(deleting,projected));
            if(side!=deleting)
                await foreach(var row in store.DescendantsAsync(pair.PairId,side,PathNames.KeyFor(side,path),ct))
                    if(PathNames.IsSelfOrUnder(row.PathOn(deleting),folder.Path)) protectedPaths.Add(row.KeyOn(deleting));
        }
        foreach(var ex in pair.Exclusions) if(paths.CoversKey(ex.Side,PathNames.KeyFor(ex.Side,ex.Path))) await Protect(ex.Side,ex.Path);
        foreach(var side in new[]{Side.Local,Side.Dropbox}) foreach(var root in paths.NestedRoots(side)) await Protect(side,root);
        await foreach(var n in store.NoticesUnderAsync(pair.PairId,scope,ct))
        {
            if(n.Cause is TextKey.ReadLocalFailed or TextKey.SkipUnreadable) return new(RemovalOutcome.Retained,folder,n.Cause);
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
        // Acquire once before mutation: an unreadable late sibling vetoes the entire removal.
        Dictionary<string,Entry>? frozen=userSelected?new(PathNames.Comparer):null;
        Dictionary<string,int>? frozenChildren=userSelected?new(PathNames.Comparer):null;
        bool cleanCoverage=!userSelected && covered is {Context:{CompleteInventory:true,ProtectedContents:false,UnreadableCause:null}} && covered.Synced==binding && covered.EntryOn(deleting)==folder && covered.EntryOn(peer.Side)==null && !protectedPaths.Overlaps(PathNames.Key(folder.Path));
        if(!cleanCoverage && (userSelected || deleting==Side.Local || protectedPaths.Overlaps(PathNames.Key(folder.Path))))
        {
            await foreach(var row in target.ReadAllAsync(folder.Path,new(true,true),ct))
            {
                if(row.Entry is not {} child || child.Skip==nameof(TextKey.SkipUnreadable))
                {
                    await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.ReadLocalFailed,row.Path,new([new(deleting,row.Path,row.Entry?.Id??string.Empty)],deleting),DateTimeOffset.UtcNow,false),ct);
                    return new(RemovalOutcome.Retained,folder,TextKey.ReadLocalFailed);
                }
                if(frozen!=null)
                {
                    frozen.Add(PathNames.KeyFor(deleting,row.Path),child);
                    var parent=PathNames.KeyFor(deleting,PathNames.Parent(row.Path));frozenChildren![parent]=frozenChildren!.GetValueOrDefault(parent)+1;
                }
                if(child.Skip==nameof(TextKey.SkipLink)) await Protect(deleting,row.Path);
            }
        }
        if(!userSelected && origin==ActionKind.ReplaceKind)
        {
            var before=heldInput(scope);
            if(before.Any(c=>c.Kind!=ChangeKind.Deleted && c.Side==deleting && c.Path!=null && PathNames.IsSelfOrUnder(c.Path,folder.Path)))
            {
                // The conflict owner may move either version. Never continue the stale parent replacement after handing it off.
                var applied=await RecoverHeldAsync(pair,scope,deleting,local,dropbox,store,_=>before,populate,ct,origin:origin,resolveConflict:resolveConflict);
                return new(RemovalOutcome.Retained,folder,applied?null:TextKey.FailReplacementRetained);
            }
        }
        return await Remove(folder,binding);

        async Task<bool> Admitted()
        {
            if(coversThrough==null) return false;
            var pc=local.BoundaryAsync(ct);var db=dropbox.BoundaryAsync(ct);await Task.WhenAll(pc,db);
            return (await pc).Position is {} position && (await db).Cursor is {Length:>0} cursor && coversThrough((position,cursor));
        }

        async Task<RemovalResult> Remove(Entry entry,SyncedEntry? saved)
        {
            var key=PathNames.KeyFor(deleting,entry.Path);
            bool partial=false;
            if(protectedPaths.Keys.Any() || entry.Skip!=null)
            {
                var verdict=Planner.FolderDeletes.Classify(protectedPaths.Covers(key),false,entry.Skip==nameof(TextKey.SkipLink),entry.Skip==nameof(TextKey.SkipUnreadable),false);
                partial=!Planner.FolderDeletes.SelectBranch(verdict,protectedPaths.Overlaps(key),false);
                if(verdict==Planner.FolderDeletes.FolderVerdict.Survives || partial && origin==ActionKind.ReplaceKind) return new(RemovalOutcome.Retained,entry,TextKey.FailReplacementRetained);
            }
            RemovalSelection? selection=null;
            if(deleting==Side.Local)
            {
                var location=await local.ReadByIdAsync(entry.Id,ct);
                if(location is not {InScope:true} match || match.Location.Entry!=entry || string.IsNullOrEmpty(match.Location.Domain)) return new(RemovalOutcome.Unknown,entry,TextKey.FailChanged);
                var volume=match.Location.Domain;
                if(pair.Roots!=null && pair.Roots.LocalVolumeKey()!=volume) return new(RemovalOutcome.Unknown,entry,TextKey.RootLocalIdentityChanged);
                selection=new(new(entry,volume,entry.IsFolder || entry.Skip==nameof(TextKey.SkipOnlineOnly)?null:await local.ContentHashAsync(entry,ct)),saved);
            }
            if(!userSelected && coversThrough==null) return new(RemovalOutcome.Retained,entry,TextKey.RetryRecoveryRequired);
            var splitRoot=splitSelected && entry==folder;
            string? cursor=null;
            RemovalResult removal;
            try {removal=partial || splitRoot?new(RemovalOutcome.Retained,entry,null):await DeleteSelected();}
            catch(ItemFailedException e) when(entry.IsFolder && e.Cause==TextKey.FailDropboxTooManyFiles && e.Disposition==FailureDisposition.Permanent) {removal=new(RemovalOutcome.NoEffect,entry,e.Cause);}
            if(partial || splitRoot || removal is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.FailDropboxTooManyFiles} && entry.IsFolder && origin is (ActionKind.DeleteLocal or ActionKind.DeleteDropbox))
            {
                RemovalResult? unsettled=null;ItemFailedException? deferred=null;
                await foreach(var row in target.ReadAllAsync(entry.Path,new(false,true),ct))
                {
                    if(row.Entry is not {} child || child.Skip==nameof(TextKey.SkipUnreadable)) return new(RemovalOutcome.Retained,entry,TextKey.ReadLocalFailed);
                    try
                    {
                        if(frozen!=null && frozen.GetValueOrDefault(PathNames.KeyFor(deleting,child.Path))!=child) return new(RemovalOutcome.Retained,entry,TextKey.FailChanged);
                        var part=await Remove(child,await store.FindAsync(pair.PairId,deleting,PathNames.KeyFor(deleting,child.Path),ct));
                        if(part.Outcome!=RemovalOutcome.Removed && !(partial && part.Outcome==RemovalOutcome.Retained && protectedPaths.Overlaps(PathNames.KeyFor(deleting,child.Path)))) unsettled??=part;
                    }
                    catch(ItemFailedException wait) when(wait.Disposition==FailureDisposition.TemporaryNoEffect) {deferred??=wait;}
                    catch(ItemFailedException error)
                    {
                        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,error.Cause,child.Path,new([new(deleting,child.Path,child.Id)],deleting),DateTimeOffset.UtcNow,false),ct);
                        unsettled??=new(RemovalOutcome.Retained,entry,error.Cause);
                    }
                }
                if(deferred!=null) throw deferred;
                if(unsettled!=null) return new(RemovalOutcome.Retained,entry,unsettled.Cause);
                if(partial) return removal;
                removal=await DeleteSelected();
            }
            async Task<RemovalResult> DeleteSelected()
            {
                if(frozen!=null)
                {
                    if(deleting==Side.Dropbox && (cursor=(await dropbox.BoundaryAsync(ct)).Cursor) is not {Length:>0}) return new(RemovalOutcome.Retained,entry,TextKey.FailChanged);
                    if(await target.ReadOneAsync(entry.Path,ct)!=entry) return new(RemovalOutcome.Retained,entry,TextKey.FailChanged);
                    if(entry.IsFolder)
                    {
                        var counts=new Dictionary<string,int>(PathNames.Comparer){[key]=0};
                        await foreach(var row in target.ReadAllAsync(entry.Path,new(true,true),ct))
                        {
                            var childKey=PathNames.KeyFor(deleting,row.Path);
                            if(row.Entry==null || frozen.GetValueOrDefault(childKey)!=row.Entry) return new(RemovalOutcome.Retained,entry,TextKey.FailChanged);
                            var parent=PathNames.KeyFor(deleting,PathNames.Parent(row.Path)); counts[parent]=counts.GetValueOrDefault(parent)+1;
                            if(row.Entry.IsFolder) counts.TryAdd(childKey,0);
                        }
                        if(counts.Any(x=>frozenChildren!.GetValueOrDefault(x.Key)!=x.Value)) return new(RemovalOutcome.Retained,entry,TextKey.FailChanged);
                    }
                }
                else if(!await Admitted()) return new(RemovalOutcome.Retained,entry,TextKey.RetryRecoveryRequired);
                return deleting==Side.Dropbox?await target.DeleteAsync(entry,null,origin,ct):await target.DeleteAsync(entry,selection,ct);
            }
            if(removal.RecycledVictim is {} victim && selection!=null && victim.Domain==selection.Proof.VolumeId && (victim.Entry.Id!=entry.Id || removal.Outcome==RemovalOutcome.Retained && ContentHash.IsValid(victim.Entry.ContentHash) && victim.Entry.ContentHash!=selection.Proof.ContentHash))
            {
                var subject=new Scope(entry.Path,saved?.DropboxPath??entry.Path);
                var restored=await PutBackSubstituteAsync(pair,subject,victim,local,dropbox,store,populate,ct);
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
            if(!userSelected && !await Admitted()) return new(RemovalOutcome.Retained,null,TextKey.RetryRecoveryRequired);
            bool uncertain=false;
            IEnumerable<Change> reports=userSelected?[]:heldInput(footprint);
            if(frozen!=null)
            {
                if(deleting==Side.Local && entry.IsFolder)
                {
                    if(removal.RecycledVictim is not {} receipt || receipt.Domain!=selection?.Proof.VolumeId || receipt.Entry.Id!=entry.Id || !receipt.Entry.IsFolder || removal.Recycled==null) return new(RemovalOutcome.Unknown,null,TextKey.RecycleUnchecked);
                    await foreach(var row in removal.Recycled.WithCancellation(ct))
                    {
                        if(row.Entry is not {} item || item.Skip==nameof(TextKey.SkipUnreadable)) return new(RemovalOutcome.Unknown,null,TextKey.RecycleUnchecked);
                        if(frozen.GetValueOrDefault(PathNames.Key(row.Path))!=item)
                            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailRecycledLate,row.Path,new([new(deleting,row.Path,item.Id)],deleting),DateTimeOffset.UtcNow,false),ct);
                    }
                }
                if(deleting==Side.Dropbox)
                {
                    var end=await dropbox.BoundaryAsync(ct);if(end.Cursor is not {Length:>0} || cursor==null) return new(RemovalOutcome.Unknown,null,TextKey.FailChanged);
                    uncertain=removal.Reply is not JobReply.Terminal {Members:[({Skip:null} deleted,null,null)],AbsentOrdinals.Count:0} ||
                        deleted.Id!=entry.Id || deleted.IsFolder!=entry.IsFolder || !PathNames.Same(deleted.Path,entry.Path);
                    var edits=new Dictionary<string,Change>(PathNames.Comparer);
                    while(cursor!=end.Cursor)
                    {
                        var page=await dropbox.ReadChangesAsync(cursor,null,end,ct);
                        if(page.ContinuityLost || !page.CaughtUp && (page.Cursor==null || page.Cursor==cursor)) return new(RemovalOutcome.Unknown,null,TextKey.FailChanged);
                        foreach(var change in page.Changes)
                        {
                            if(change.Path is not {} path || !PathNames.IsSelfOrUnder(path,entry.Path) || change.Kind==ChangeKind.Deleted) continue;
                            edits[PathNames.DropboxKey(path)]=change;
                        }
                        if(page.CaughtUp) break;
                        cursor=page.Cursor;
                    }
                    reports=edits.Values.Where(change=>change.Observed is not {} observed ||
                        (PathNames.Same(observed.Path,entry.Path)?entry:frozen.GetValueOrDefault(PathNames.DropboxKey(observed.Path)))!=observed);
                }
            }
            if(!userSelected && removal.Recycled!=null && !cleanCoverage && origin is (ActionKind.MoveLocal or ActionKind.MoveDropbox))
            {
                var arrivals=new List<Change>();
                await foreach(var row in removal.Recycled.WithCancellation(ct))
                {
                    if(row.Entry is not {Skip:null} child) continue;
                    var record=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(row.Path),ct);
                    if(record==null || record.LocalId==child.Id && !child.IsFolder && (record.LocalSize!=child.Size || record.LocalModifiedTicks!=child.Modified.UtcTicks || ContentHash.IsValid(child.ContentHash) && child.ContentHash!=record.DropboxContentHash))
                        arrivals.Add(new(Side.Local,ChangeKind.Changed,row.Path,null,child.Id,child,true,false));
                }
                // A later held delete must supersede a receipt, not be overwritten by it.
                reports=arrivals.Concat(reports);
            }
            if(!await RecoverHeldAsync(pair,footprint,deleting,local,dropbox,store,_=>reports,populate,ct,origin:origin,resolveConflict:resolveConflict,selectedCloudWindow:frozen!=null && deleting==Side.Dropbox)) return new(uncertain?RemovalOutcome.Unknown:RemovalOutcome.Retained,null,TextKey.RetryRecoveryRequired);
            if(uncertain) return new(RemovalOutcome.Unknown,null,TextKey.FailDropboxOutcomeUnknown);
            if(frozen?.Remove(key)==true) frozenChildren![PathNames.KeyFor(deleting,PathNames.Parent(entry.Path))]--;
            if(saved!=null) await compareOldBinding(saved,footprint,footprint,ct);
            if(selection!=null) await store.DisarmAsync(pair.PairId,selection,null,ct);
            return removal;
        }
    }

    /// <summary>Recover the positively observed bin occupant without ever granting the planned replacement.</summary>
    public static Task<bool> PutBackSubstituteAsync(PairConfig pair,Scope scope,IdentityLocation receipt,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct)
    {
        var version=receipt.Entry with {Path=scope.LocalPath};
        var observed=new Change(Side.Local,ChangeKind.Changed,scope.LocalPath,null,version.Id,version,true,false);
        return RecoverHeldAsync(pair,scope,Side.Local,local,dropbox,store,_=>[observed],populate,ct,actualVictim:receipt);
    }

    internal static async Task<string> MapAsync(PairConfig pair,IStore store,Side side,string path,CancellationToken ct)
    {
        var other=PathNames.Other(side);
        if(await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,path),ct) is {} exact) return exact.PathOn(other);
        return PathNames.Parent(path).Length==0?PathNames.SpellingOn(other,path):await ItemSync.CounterpartAsync(pair,side,path,store,ct);
    }

    /// <summary>Retry only the exact held subject; an empty replay is not settlement.</summary>
    public static async Task<bool> RetryHeldAsync(PairConfig pair,Scope scope,Notice notice,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict=null)
    {
        if(notice.Kind!=NoticeKind.PutBack || notice.Subject.Incoming is not {} side) return false;
        var subject=notice.Subject.Items.FirstOrDefault(m=>m.Side==side && PathNames.Same(m.Path,notice.Path));
        if(subject==null || !PathNames.IsSelfOrUnder(subject.Path,side==Side.Local?scope.LocalPath:scope.DropboxPath) || await store.NoticeAsync(pair.PairId,notice.Id,ct) is not {} current || !current.Matches(notice)) return false;
        return await RecoverHeldAsync(pair,scope,side,local,dropbox,store,heldInput,populate,ct,notice,resolveConflict:resolveConflict);
    }

    /// <summary>Keep failed reports in the queue so Retry can reconstruct the latest required version.</summary>
    internal static async Task<bool> RecoverHeldAsync(PairConfig pair,Scope scope,Side side,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct,Notice? retry=null,ActionKind origin=ActionKind.None,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict=null,IdentityLocation? actualVictim=null,bool selectedCloudWindow=false)
    {
        var source=side==Side.Local?local:dropbox; bool complete=true;
        var root=side==Side.Local?scope.LocalPath:scope.DropboxPath;
        var subject=retry?.Subject.Items.FirstOrDefault(i=>i.Side==side && PathNames.Same(i.Path,retry.Path));
        var latest=new Dictionary<(string Path,string Id), (long Order,Change Report)>();
        var pathDeletes=new Dictionary<string,long>(PathNames.Comparer);
        var acks=new List<((string Path,string Id) Key,Action Ack)>();long order=0;
        foreach(var c in heldInput(scope))
        {
            if(c.Side!=side) continue;
            if(c.Path==null || !PathNames.IsSelfOrUnder(c.Path,root))
            {
                if((c.OldPath!=null && PathNames.IsSelfOrUnder(c.OldPath,root) || c.AffectedPath!=null && PathNames.IsSelfOrUnder(c.AffectedPath,root)) && (subject==null || subject.Id==c.Id)) complete=false;
                continue;
            }
            var key=(PathNames.Key(c.Path),c.Id??c.Observed?.Id??string.Empty);
            if(subject!=null && (!PathNames.Same(subject.Path,c.Path) || key.Item2!=subject.Id && !(side==Side.Dropbox && c.Kind==ChangeKind.Deleted && key.Item2.Length==0 && latest.ContainsKey((key.Item1,subject.Id))))) continue;
            latest[key]=(++order,c);
            if(side==Side.Dropbox && key.Item2.Length==0 && c.Kind==ChangeKind.Deleted) pathDeletes[key.Item1]=order;
            if(c.ConsumeHeld!=null) acks.Add((key,c.ConsumeHeld));
        }
        if(retry!=null && latest.Count==0)
        {
            if(subject==null || side!=Side.Local || await source.ReadByIdAsync(subject.Id,ct) is not {InScope:false} bin || bin.Location.Entry.Id!=subject.Id) return false;
            var baseline=await store.FindAsync(pair.PairId,side,PathNames.Key(subject.Path),ct);
            var selected=await store.ArmedAsync(pair.PairId,ct);
            if(!(baseline!=null && baseline.LocalId!=subject.Id) && !(selected is {} note && note.Victim.Saved==baseline && PathNames.Same(note.Victim.Proof.Expected.Path,subject.Path) && note.Victim.Proof.VolumeId==bin.Location.Domain)) return false;
            actualVictim=bin.Location;var recovered=bin.Location.Entry with {Path=subject.Path};
            latest[(PathNames.Key(subject.Path),subject.Id)]=(++order,new(side,ChangeKind.Changed,subject.Path,null,subject.Id,recovered,true,false));
        }
        var settled=new HashSet<(string Path,string Id)>();
        foreach(var (key,candidate) in latest)
        {
            var last=candidate.Report;
            if(pathDeletes.GetValueOrDefault(key.Path)>candidate.Order) {settled.Add(key);continue;}
            if(last.Kind!=ChangeKind.Deleted)
            {
                if(last.Observed is not {} version) {complete=false;continue;}
                if(!PathNames.Same(last.Path,version.Path) || !PathNames.IsSelfOrUnder(version.Path,root)) {complete=false;continue;}
                var saved=await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,version.Path),ct);
                bool foreign=saved!=null && saved.IdOn(side)!=version.Id;
                bool substitute=actualVictim?.Entry.Id==version.Id || foreign && retry?.Subject.Items.Any(i=>i.Side==side && i.Id==version.Id && PathNames.Same(i.Path,version.Path))==true;

                Notice? owner=retry;
                if(owner==null) await foreach(var open in store.NoticesAtAsync(pair.PairId,PathNames.Key(version.Path),ct))
                    if(open.Kind==NoticeKind.PutBack && open.Subject.Incoming==side && open.Subject.Items.FirstOrDefault() is {} original && original.Id==version.Id && PathNames.Same(original.Path,version.Path)) owner=open;
                var items=owner?.Subject.Items.ToList()??[new(side,version.Path,version.Id)];
                var tail=version.Path[root.Length..]; var endpoints=new Scope(saved?.LocalPath??PathNames.LocalSpelling(scope.LocalPath+tail),saved?.DropboxPath??scope.DropboxPath+tail);
                try
                {
                    if(selectedCloudWindow && (version.IsFolder || version.Skip!=null || await source.DeletedAsync(version.Path,ct) is not DeletionEvidence.DeletedFile history || history.Domain!=pair.Roots?.Dropbox.RootNamespaceId || history.RemoteId!=version.Id || history.Revision!=version.Revision || history.ContentHash!=version.ContentHash))
                        throw new ChangedUnderneathException(version.Path);
                    var located=actualVictim==null?await source.ReadByIdAsync(version.Id,ct):null;
                    if(actualVictim==null && located==null && side==Side.Local && items.All(i=>i.Side!=Side.Local || i.Id==version.Id)) throw new ChangedUnderneathException(version.Path);
                    var location=actualVictim??located?.Location;
                    var armed=side==Side.Local && (substitute || owner!=null)?await store.ArmedAsync(pair.PairId,ct):null;
                    bool sameSelection=armed is {} n && n.Victim.Saved==saved && n.Victim.Proof.VolumeId==location?.Domain && PathNames.Same(n.Victim.Proof.Expected.Path,version.Path);
                    bool sameId=sameSelection && armed!.Value.Victim.Proof.Expected.Id==version.Id;
                    if(owner!=null && sameSelection && !sameId && (actualVictim!=null || located is {InScope:false})) substitute=true;
                    if(foreign && !substitute) {complete=false;continue;}
                    Recovery.File? provenFile=null;
                    if(side==Side.Local && located is {InScope:false} bin && bin.Location.Entry.Id==version.Id && !bin.Location.Entry.IsFolder)
                    {
                        var candidateVersion=bin.Location.Entry with {Path=version.Path};
                        provenFile=await source.RecoverAsync(bin.Location,candidateVersion,ct) as Recovery.File;
                        if(provenFile==null) throw new ChangedUnderneathException(version.Path);
                    }
                    if(sameId && owner!=null && provenFile!=null && ContentHash.IsValid(provenFile.Version.ContentHash) && !string.Equals(armed!.Value.Victim.Proof.ContentHash,provenFile.Version.ContentHash,StringComparison.OrdinalIgnoreCase)) substitute=true;
                    // Copy recovery needs the stream; whole-item restore needs only the version before it opens a DELETE handle.
                    await using var verifiedBytes=substitute?null:provenFile?.Bytes;
                    if(provenFile!=null)
                    {
                        await using var proofOnly=substitute?provenFile.Bytes:null;
                        var candidateVersion=location!.Entry with {Path=version.Path};
                        if(!ContentHash.IsValid(provenFile.Version.ContentHash) || (provenFile.Version with {ContentHash=candidateVersion.ContentHash})!=candidateVersion || ContentHash.IsValid(candidateVersion.ContentHash) && provenFile.Version.ContentHash!=candidateVersion.ContentHash) throw new ChangedUnderneathException(version.Path);
                        version=provenFile.Version;
                    }
                    if(location==null && side==Side.Dropbox && !version.IsFolder && !string.IsNullOrEmpty(version.Revision) && pair.Roots?.Dropbox.RootNamespaceId is {Length:>0} ns) location=new(ns,PathNames.RootedDropboxPath(pair.DropboxRoot,version.Path),version);

                    bool Keep(Side leg,string path)=>!pair.Exclusions.Any(ex=>!ex.Keep && ex.Side==leg && PathNames.IsSelfOrUnder(path,ex.Path));
                    if(substitute)
                    {
                        if(side!=Side.Local || location==null || !Keep(Side.Local,endpoints.LocalPath)) throw new ChangedUnderneathException(version.Path);
                        if(await local.ReadOneAsync(endpoints.LocalPath,ct)!=null) throw new PathOccupiedException(endpoints.LocalPath);
                        if(!version.IsFolder && !ContentHash.IsValid(version.ContentHash))
                        {
                            if(await local.RecoverAsync(location,version,ct) is not Recovery.File known) throw new ChangedUnderneathException(version.Path);
                            await using(known.Bytes){if(!ContentHash.IsValid(known.Version.ContentHash) || (known.Version with {ContentHash=version.ContentHash})!=version) throw new ChangedUnderneathException(version.Path);version=known.Version;}
                        }
                        if(sameId && (version.IsFolder || !ContentHash.IsValid(version.ContentHash) || string.Equals(armed!.Value.Victim.Proof.ContentHash,version.ContentHash,StringComparison.OrdinalIgnoreCase))) throw new ChangedUnderneathException(version.Path);
                        // The exact PutBack subject survives the native rename until its matching note is discharged.
                        if(sameSelection && owner==null) await Checkpoint();
                        var restored=await local.RestoreRecycledAsync(location,version,endpoints.LocalPath,ct);
                        if(restored is not {Outcome:RemovalOutcome.Published,Final:{} final} || final.Id!=version.Id || final.IsFolder!=version.IsFolder || !PathNames.Same(final.Path,endpoints.LocalPath) ||
                            !final.IsFolder && (final.Size!=version.Size || !ContentHash.IsValid(final.ContentHash) || ContentHash.IsValid(version.ContentHash) && !string.Equals(final.ContentHash,version.ContentHash,StringComparison.OrdinalIgnoreCase))) throw new ChangedUnderneathException(version.Path);
                        if(sameSelection && armed!.Value.Stage==null) await store.DisarmAsync(pair.PairId,armed.Value.Victim,null,ct);
                        if(!await ReleaseOwner()) {complete=false;continue;}
                        settled.Add(key);continue;
                    }
                    bool competing=false;
                    if(retry!=null && resolveConflict!=null)
                    {
                        var pc=await local.ReadOneAsync(endpoints.LocalPath,ct);var db=await dropbox.ReadOneAsync(endpoints.DropboxPath,ct);
                        competing=pc!=null && (pc.IsFolder!=version.IsFolder || !pc.IsFolder && await local.ContentHashAsync(pc,ct)!=version.ContentHash) || db!=null && (db.IsFolder!=version.IsFolder || !db.IsFolder && db.ContentHash!=version.ContentHash);
                    }
                    if(origin==ActionKind.ReplaceKind && !selectedCloudWindow || competing)
                    {
                        if(resolveConflict==null) throw new ItemFailedException(version.Path,TextKey.RetryRecoveryRequired,null);
                        if(await resolveConflict(endpoints,side,version,ct)!=EffectOutcome.Applied) throw new ItemFailedException(version.Path,TextKey.RetryRecoveryRequired,null);
                        if(!await ReleaseOwner()) {complete=false;continue;}
                        settled.Add(key);continue;
                    }
                    if(version.IsFolder)
                    {
                        if(location==null || await source.RecoverAsync(location,version,ct) is not Recovery.Folder folder || folder.Root.Id!=version.Id) throw new ChangedUnderneathException(version.Path);
                        var keepPc=Keep(Side.Local,endpoints.LocalPath);var keepDb=Keep(Side.Dropbox,endpoints.DropboxPath);
                        if(!(side==Side.Local?keepDb:keepPc)) throw new ItemFailedException(version.Path,TextKey.RetryRecoveryRequired,null);
                        await populate(endpoints,side,folder,ct);
                    }
                    else
                    {
                        Entry? a=null;
                        try {if(Keep(Side.Local,endpoints.LocalPath)) a=await Restore(local,endpoints.LocalPath,null);}
                        catch(PathOccupiedException) when(Keep(Side.Dropbox,endpoints.DropboxPath))
                        {
                            // Preserve the verified old version on the other leg. The ordinary conflict decision owns the two different live versions.
                            try {await Restore(dropbox,endpoints.DropboxPath,null);}
                            catch(PathOccupiedException) when(resolveConflict!=null) { /* Both live paths are kept for the ordinary conflict decision. */ }
                            if(resolveConflict==null || await resolveConflict(endpoints,side,version,ct)!=EffectOutcome.Applied) throw;
                            if(!await ReleaseOwner()) {complete=false;continue;}
                            settled.Add(key);continue;
                        }
                        Entry? b=null;
                        try {if(Keep(Side.Dropbox,endpoints.DropboxPath)) b=await Restore(dropbox,endpoints.DropboxPath,a);}
                        catch(PathOccupiedException) when(a!=null && resolveConflict!=null)
                        {
                            if(await resolveConflict(endpoints,side,version,ct)!=EffectOutcome.Applied) throw;
                            if(!await ReleaseOwner()) {complete=false;continue;}
                            settled.Add(key);continue;
                        }
                        if(a==null && b==null) {complete=false;continue;}
                        if(a!=null && b!=null) await store.CommitAsync(pair.PairId,saved,SyncedEntries.From(a,b),null,null,ct);

                        await Notice(NoticeKind.Info,TextKey.InfoEditBeatsDelete);
                        if(retry==null && !await ReleaseOwner()) {complete=false;continue;}
                    }
                    async Task<Entry> Restore(ISide leg,string path,Entry? verifiedLocal)
                    {
                        if(await leg.ReadOneAsync(path,ct) is {} present)
                        {
                            bool known=items.Any(m=>m.Side==leg.Side && m.Id==present.Id && PathNames.Same(m.Path,path)) || saved?.IdOn(leg.Side)==present.Id;
                            // A lost remote create reply cannot prove our authorship. It can prove ordinary equality against the exact checkpointed local file.
                            bool equalCandidate=leg.Side==Side.Dropbox && verifiedLocal!=null && owner!=null && items.Any(i=>i.Side==Side.Local && i.Id==verifiedLocal.Id && PathNames.Same(i.Path,verifiedLocal.Path)) && await local.ContentHashAsync(verifiedLocal,ct)==version.ContentHash;
                            if(present.IsFolder || !(known || equalCandidate) || present.Size!=version.Size || !ContentHash.IsValid(version.ContentHash) || !StringComparer.OrdinalIgnoreCase.Equals(await leg.ContentHashAsync(present,ct),version.ContentHash)) throw new PathOccupiedException(path);
                            return present;
                        }
                        await using var bytes=verifiedLocal!=null?await local.OpenReadAsync(verifiedLocal,ct):verifiedBytes??(location!=null && await source.RecoverAsync(location,version,ct) is Recovery.File file && file.Version==version?file.Bytes:throw new ChangedUnderneathException(path));
                        foreach(var parent in PathNames.Ancestors(path).Reverse().Where(p=>p.Length>0)) if((await leg.EnsureFolderAsync(parent,ct)).Outcome!=RemovalOutcome.Published) throw new ChangedUnderneathException(parent);
                        await using var stage=await leg.StageAsync(path,bytes,version,null,ct);
                        if(stage.Proof is {} proof && leg.Side==Side.Local)
                        {
                            // Persist the exact staged file identity before create-only publication: restart cannot infer ownership from equal bytes.
                            items.Add(new(leg.Side,path,proof.FileId));
                            await Checkpoint();
                        }
                        var published=await leg.PublishAsync(stage,null,null,ct);
                        if(published is {Outcome:RemovalOutcome.Published,Final:{} confirmed} && !items.Any(i=>i.Side==leg.Side && i.Id==confirmed.Id && PathNames.Same(i.Path,path)))
                        {
                            items.Add(new(leg.Side,path,confirmed.Id)); await Checkpoint();
                        }
                        return published.Outcome==RemovalOutcome.Published && published.Final is {} entry && entry.Size==version.Size && ContentHash.IsValid(version.ContentHash) && StringComparer.OrdinalIgnoreCase.Equals(await leg.ContentHashAsync(entry,ct),version.ContentHash)?entry:throw new ChangedUnderneathException(path);
                    }
                }
                catch(Exception ex) when(ex is ItemFailedException or ChangedUnderneathException or PathOccupiedException or IOException)
                {
                    if(owner==null || await store.NoticeAsync(pair.PairId,owner.Id,ct) is {} activeFailure && activeFailure.Matches(owner)) await Notice(NoticeKind.PutBack,ex is ItemFailedException failure?failure.Cause:ex is PathOccupiedException?TextKey.ClashNameTaken:TextKey.FailChanged);
                    complete=false;continue;
                }
                async Task<bool> ReleaseOwner()
                {
                    if(owner==null) return true;
                    if(!await store.CloseNoticeAsync(pair.PairId,owner,ct)) return false;
                    if(owner==retry) retry=null;
                    return true;
                }
                async Task Checkpoint()
                {
                    if(owner!=null && (await store.NoticeAsync(pair.PairId,owner.Id,ct) is not {} active || !active.Matches(owner))) throw new ChangedUnderneathException(version.Path);
                    await Notice(NoticeKind.PutBack,owner?.Cause??TextKey.RetryRecoveryRequired);
                    await foreach(var n in store.NoticesAtAsync(pair.PairId,PathNames.Key(version.Path),ct))
                        if(n.Kind==NoticeKind.PutBack && n.Subject.Incoming==side && n.Subject.HasSameItems(IssueSubject.FromDistinct(items,side))) {owner=n;if(retry!=null) retry=n;break;}
                }
                Task Notice(NoticeKind kind,TextKey cause)=>store.SaveNoticeAsync(pair.PairId,new(0,kind,cause,version.Path,IssueSubject.FromDistinct(items,side),DateTimeOffset.UtcNow,false),ct);
            }
            settled.Add(key);
        }
        if(retry!=null)
        {
            if(!complete || !await store.CloseNoticeAsync(pair.PairId,retry,ct)) return false;
        }
        foreach(var entry in acks) if(settled.Contains(entry.Key)) entry.Ack();
        return complete;
    }
}
