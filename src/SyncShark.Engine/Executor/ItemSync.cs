using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Change intake: observe one input and hand its row to RowExecution. Returning means the input settled; exceptions retain it.</summary>
public static class ItemSync
{
    public static Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change change, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput=null, Action<string,bool>? failed=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,Action<Notice>? failedNotice=null) =>
        ChangeAsync(pair,settings,events,paths,change,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,coversThrough:coversThrough,failedNotice:failedNotice);

    // Retain one job: the second decision for a ruled-out alias uses the same observations and custody; moving it out would repeat the call seam.
    internal static async Task<bool> ChangeAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change change, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput=null, Action<string,bool>? failed=null,Notice? reviewed=null,NoticeItem? member=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,Action<Notice>? failedNotice=null)
    {
        var path=change.Path ?? change.Observed?.Path ?? throw new InventoryIncompleteException(nameof(Change.Path),null);
        var key=PathNames.KeyFor(change.Side,path);
        var saved=await store.FindAsync(pair.PairId,change.Side,key,ct);
        var notices=new List<Notice>();
        await foreach(var n in store.NoticesAtAsync(pair.PairId,key,ct)) notices.Add(n);
        if (reviewed==null && notices.Count==0 && Echo(change,saved)) return true;
        // A PC change under a current no-keep rule belongs to that rule's owner: owe its root (sync up, then recycle), never settle it out of scope.
        if(change.Side==Side.Local && paths.Exclusions.FirstOrDefault(e=>!e.Keep && e.Side==Side.Local && PathNames.IsSelfOrUnder(path,e.Path)) is {} noKeep)
        {
            await store.OweScopeAsync(pair.PairId,new Scope(noKeep.Path,await CounterpartAsync(pair,Side.Local,noKeep.Path,store,ct)),ct);
            if(change is {Kind:ChangeKind.Renamed,OldPath:{} old} && !PathNames.IsSelfOrUnder(old,noKeep.Path) &&
               await store.FindAsync(pair.PairId,Side.Local,PathNames.KeyFor(Side.Local,old),ct) is {} left && left.LocalId==change.Id)
                await store.OweScopeAsync(pair.PairId,new Scope(left.LocalPath,left.DropboxPath),ct); // the move out of sync removes the other copy
            return true;
        }
        bool hasDescendant=false;
        if(saved==null && change.Side==Side.Dropbox && (change is {Kind:ChangeKind.Deleted,Id:null} || reviewed is {Subject.Items.Count:0}))
        {
            await foreach(var _ in store.DescendantsAsync(pair.PairId,Side.Dropbox,key,ct))
            {
                hasDescendant=true;
                break;
            }
            if(!hasDescendant && reviewed==null && notices.Count==0) return true;
        }
        var other=PathNames.Other(change.Side);
        var mapped=saved?.PathOn(other) ?? await CounterpartAsync(pair,change.Side,path,store,ct);
        var endpoints=change.Side==Side.Local ? new Scope(path,mapped) : new Scope(mapped,path);
        var debt=reviewed?.Subject.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None;
        try
        {
            if(hasDescendant) await compareOldBinding(null,endpoints,null,ct);
            bool selectedRemoval=SelectedRemoval(change,saved);
            if(change is {Side:Side.Dropbox,Kind:ChangeKind.Deleted} && selectedRemoval && await dropbox.ReadByIdAsync(saved!.DropboxId,ct) is {InScope:true} location)
            {
                var actual=location.Location.Entry;
                ItemFailedException.RequireReadable(Side.Dropbox,actual);
                if(actual.IsFolder!=saved!.IsFolder) Retain(key,failed);
                if(PathNames.SamePlace(saved.DropboxPath,actual.Path)) return true;
                return await RowExecution.ExecuteAsync(pair,settings,events,paths,null,[new(Side.Dropbox,location.Location.Entry,saved!)],false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false,coversThrough:coversThrough);
            }
            if(saved is {IsFolder:true} && selectedRemoval) return await RowExecution.RemoveBoundAsync(pair,settings,events,paths,saved,change.Side,local,dropbox,store,heldInput,populate,compareOldBinding,failed,ct,coversThrough,null);
            var pcTask=local.ReadOneAsync(endpoints.LocalPath,ct);
            var dbTask=dropbox.ReadOneAsync(endpoints.DropboxPath,ct);
            await Task.WhenAll(pcTask,dbTask);
            var pc=await pcTask;
            var db=await dbTask;
            ItemFailedException.RequireReadable(Side.Local,pc);
            ItemFailedException.RequireReadable(Side.Dropbox,db);
            if(change is {Side:Side.Dropbox,Kind:ChangeKind.Deleted,Id:null} && saved is {IsFolder:false} && selectedRemoval &&
                await local.ReadByIdAsync(saved.LocalId,ct) is {InScope:true} live && live.Location.Entry.Path!=saved.LocalPath)
            {
                // By-ID can pick another hard link. A name read supplies actual leaf casing, not its ancestors' casing.
                var name=saved.LocalPath; var entry=pc;
                while(entry!=null && entry.Path==name && (name!=saved.LocalPath || entry.Id==saved.LocalId))
                {
                    name=PathNames.Parent(name);
                    if(name.Length==0) break;
                    entry=await local.ReadOneAsync(name,ct);
                    ItemFailedException.RequireReadable(Side.Local,entry);
                }
                if(name.Length!=0)
                {
                    // Owe the folder that now holds the file; a root-level move owes the root scope. A folder move itself is AutoSync's to mirror.
                    var at=PathNames.Parent(live.Location.Entry.Path);
                    await store.OweScopeAsync(pair.PairId,at.Length==0?new Scope(string.Empty,string.Empty):new Scope(at,await CounterpartAsync(pair,Side.Local,at,store,ct)),ct);
                    return true;
                }
            }
            if(await SettleReusedNameAsync(pair,settings,events,paths,change,key,saved,pc,db,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,reviewed,member,coversThrough) is {} reused) return reused;
            if(change.Side==Side.Local && saved!=null && !selectedRemoval && (pc==null || change.Id==saved.LocalId && pc.Id!=saved.LocalId))
            {
                var current=change.Id==saved.LocalId && (change.Kind==ChangeKind.Deleted || heldInput!=null) ? await local.ReadByIdAsync(saved.LocalId,ct) : null;
                var removed=await SettleRemovalAsync(pair,settings,events,paths,change,key,saved,endpoints,current,pc,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,coversThrough);
                if(removed.Settled is {} settled) return settled;
                selectedRemoval=removed.Selected;
            }
            var observed=ObserveReplacement(paths,change,key,path,saved,mapped,endpoints,pc,db,selectedRemoval,notices,reviewed,member,heldInput,failed);
            if(observed.Settled is {} replaced) return replaced;
            debt=observed.Historical?.Subject.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None;
            var row=observed.Row;
            if(row.Synced==null && row.Local is {IsFolder:false} file && row.Dropbox==null && debt==IssueSubject.FirstRunDeletionKind.Item &&
                observed.Historical is {} historical && historical.Subject.Items.Any(i=>i.Side==Side.Local && i.Id==file.Id && PathNames.Same(i.Path,file.Path)))
            {
                row=(await RowExecution.WithDeletionAsync(row,endpoints.DropboxPath,false,pair.PairId,dropbox,store,ct)) with {
                    Context=row.Context with {CompleteInventory=(reviewed==null ? change.Side==Side.Local : reviewed==historical && member is {Side:Side.Local}) && historical.Subject.Items.Count==1}};
            }
            var candidates=await RenameCandidatesAsync(pair,change,key,saved,row,store,failed,ct);
            var incoming=row.EntryOn(change.Side);
            (row,var changedPutBack)=await ChangedPutBackAsync(pair,change,key,saved,row,incoming,candidates,notices,reviewed,local,store,ct);
            var (ok,_,decision)=await RowExecution.ExecuteWithOutcomeAsync(pair,settings,events,paths,row,candidates,observed.SafeSave||change.LocalContentDirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false,coversThrough:coversThrough,observedMissing:selectedRemoval && change.Side==Side.Local?(change.Side,observed.Successor):null,failedNotice:failedNotice);
            // A by-ID candidate the move owner rules out of scope (a live hard-link alias, or an excluded original) leaves this name an ordinary item;
            // a kept original the first execution already followed here no longer sits at its own key, so it is not re-run.
            if(decision is {Kind:ActionKind.None,None:NoneReason.OutOfScope} && candidates is [{} only] && !row.Context.OutOfScope && (change.OldPath is not {} from || PathNames.KeyFor(change.Side,from)!=only.Synced.KeyOn(change.Side))
                && await store.FindAsync(pair.PairId,change.Side,only.Synced.KeyOn(change.Side),ct) is not null)
                (ok,_,decision)=await RowExecution.ExecuteWithOutcomeAsync(pair,settings,events,paths,row,[],observed.SafeSave||change.LocalContentDirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false,coversThrough:coversThrough,observedMissing:selectedRemoval && change.Side==Side.Local?(change.Side,observed.Successor):null,failedNotice:failedNotice);
            if(ok && saved==null && candidates.Count!=0 && decision is {Kind:ActionKind.ForgetOnly,InfoAfter:TextKey.InfoMovedDeleted,Row.Synced.IsFolder:true})
                return await ChangeAsync(pair,settings,events,paths,change,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,reviewed,member,coversThrough);
            if(ok && (reviewed ?? notices.FirstOrDefault(n=>n.Kind==NoticeKind.Failure)) is {Kind:NoticeKind.Failure} &&
                await store.FindAsync(pair.PairId,change.Side,key,ct) is {IsFolder:true} folder)
            {
                await compareOldBinding(folder,new(folder.LocalPath,folder.DropboxPath),null,ct);
            }
            if (!ok || reviewed!=null) return ok;
            if(changedPutBack!=null) await store.CloseNoticeAsync(pair.PairId,changedPutBack,ct);
            await CloseComparedNoticesAsync(pair.PairId,change,incoming,row.EntryOn(other),notices,store,ct);
            return true;
        }
        catch(ItemFailedException error) when(error.Disposition!=FailureDisposition.TemporaryNoEffect)
        {
            if(!await ReportChangeFailureAsync(pair,events,change,path,key,saved,endpoints,debt,store,heldInput,failed,reviewed,error,ct)) throw;
            return false;
        }
    }

    private static bool SelectedRemoval(Change change,SyncedEntry? saved) =>
        change.Kind==ChangeKind.Deleted && saved!=null && (change.Side==Side.Dropbox ? change.Id==null || change.Id==saved.DropboxId : change.IdentityDeleted && change.Id is {Length:>0} removed && removed==saved.LocalId);

    // At a reused name settle the old identities, not the item that inherited the name.
    private static async Task<bool?> SettleReusedNameAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Change change,string key,SyncedEntry? saved,Entry? pc,Entry? db,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,Notice? reviewed,NoticeItem? member,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough)
    {
        if(change is {Side:Side.Dropbox,Kind:ChangeKind.Changed} && saved!=null && pc?.Id!=saved.LocalId && db!=null && db.Id!=saved.DropboxId)
        {
            var owner=await ObserveBindingAsync(paths,saved,local,dropbox,ct);
            var settled=await RowExecution.ExecuteWithOutcomeAsync(pair,settings,events,paths,owner.Row,owner.Group,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false,coversThrough:coversThrough);
            if(settled.Outcome==EffectOutcome.Accepted) return false; // the saved job settles the old identity; this input is released
            if(!settled.Settled) Retain(key,failed);
            if(await store.FindAsync(pair.PairId,change.Side,key,ct)==saved) Retain(key,failed);
            return await ChangeAsync(pair,settings,events,paths,change,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,reviewed,member,coversThrough);
        }
        return null;
    }

    // A missing journal path may already have moved. Only the selected identity's removal authorizes deletion.
    private static async ValueTask<(bool? Settled,bool Selected)> SettleRemovalAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Change change,string key,SyncedEntry saved,Scope endpoints,(IdentityLocation Location,bool InScope)? currentIdentity,Entry? pc,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough)
    {
        if(change.Kind==ChangeKind.Deleted && change.Id is {Length:>0} selected && selected==saved.LocalId && currentIdentity is null or {InScope:false})
        {
            if(saved.IsFolder) return (await RowExecution.RemoveBoundAsync(pair,settings,events,paths,saved,Side.Local,local,dropbox,store,heldInput,populate,compareOldBinding,failed,ct,coversThrough,null),true);
            return (null,true);
        }
        if(change.Id==saved.LocalId && heldInput!=null && currentIdentity is {InScope:true} located)
        {
            var relocated=located.Location.Entry;
            ItemFailedException.RequireReadable(Side.Local,relocated);
            var rename=heldInput(endpoints).FirstOrDefault(c=>c is {Side:Side.Local,Kind:ChangeKind.Renamed} && c.Id==saved.LocalId && c.OldPath==saved.LocalPath && c.Path==relocated.Path && c.JournalId==change.JournalId && c.Usn>change.Usn);
            if(rename!=null && relocated.Id==saved.LocalId && relocated.IsFolder==saved.IsFolder && relocated.Path!=saved.LocalPath)
            {
                var settled=await RowExecution.ExecuteWithOutcomeAsync(pair,settings,events,paths,null,[new(Side.Local,relocated,saved)],change.LocalContentDirty||rename.LocalContentDirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false,coversThrough:coversThrough);
                if(settled is {Settled:false,Outcome:not EffectOutcome.Accepted}) Retain(key,failed);
                return (true,false); // The rename keeps its own ordered input and reobserves the committed binding.
            }
        }
        // A removed name of a known hard-linked file: Windows' current names for the file no longer include it.
        if(change.Kind==ChangeKind.Deleted && change.Id==saved.LocalId && pc==null && currentIdentity is {InScope:true} && await RowExecution.IsUnlinkAsync(pair,local,saved,store,ct)) return (null,true);
        if(change.Id is {Length:>0} reported && reported!=saved.LocalId) return (true,false);
        Retain(key,failed);
        return (false,false);
    }

    private static (bool? Settled,Row Row,Entry? Successor,bool SafeSave,Notice? Historical) ObserveReplacement(PathScope paths,Change change,string key,string path,SyncedEntry? saved,string mapped,Scope endpoints,Entry? pc,Entry? db,bool selectedRemoval,List<Notice> notices,Notice? reviewed,NoticeItem? member,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed)
    {
        // A successor at the old name belongs to its own input, not the removed file's row.
        var successor=selectedRemoval && change.Side==Side.Local && pc?.Id!=saved!.LocalId ? pc : null;
        // An observed replacement file is current even if its rename arrives on the next journal page.
        bool safeSave=change.IdentityDeleted && successor is {IsFolder:false} replacement && saved is {IsFolder:false} && replacement.Path==saved.LocalPath;
        if(successor!=null && !safeSave) pc=null;
        var incoming=change.Side==Side.Local?pc:db;
        if(member!=null && incoming?.Id!=member.Id) return (false,null!,null,false,null);
        if(!selectedRemoval && change.Id is {Length:>0} expected && incoming?.Id!=expected)
        {
            // Without a binding or an occupant, a missing feed identity can still become visible, unless a later tombstone at its name or an ancestor says it left.
            if(incoming==null && saved==null && change is {Side:Side.Dropbox,Kind:ChangeKind.Changed} && !Departed(heldInput,endpoints.LocalPath,path)) Retain(key,failed);
            return (true,null!,null,false,null);
        }
        bool Matches(Notice n) => n.Subject.Items.Any(i=>i.Id==(i.Side==Side.Local?pc:db)?.Id);
        var tie=notices.FirstOrDefault(n=>n.Kind==NoticeKind.Tie && Matches(n));
        var historical=reviewed!=null && reviewed.Subject.FirstRunDeletion!=IssueSubject.FirstRunDeletionKind.None && Matches(reviewed) ? reviewed : notices.FirstOrDefault(n=>n.Kind==NoticeKind.Failure && n.Subject.FirstRunDeletion!=IssueSubject.FirstRunDeletionKind.None && Matches(n));
        var context=RowContext.Neutral with { Destination=mapped, OutOfScope=paths.Excludes(endpoints.LocalPath,endpoints.DropboxPath,(pc??db)?.IsFolder??saved?.IsFolder??false),Skipped=pc?.Skip!=null || db?.Skip!=null,Held=tie!=null,HeldKind=tie?.Kind, FirstRunDeletion=historical?.Subject.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None, DeletionEligible=historical!=null };
        return (null,new Row(key,pc,db,saved,[],null,context){ObservationPath=path},successor,safeSave,historical);
    }

    // Held input is the not-yet-started acquired reports in receipt order, so a tombstone there arrived after this arrival.
    private static bool Departed(Func<Scope,IEnumerable<Change>>? heldInput,string localPath,string path)
    {
        if(heldInput==null) return false;
        for(var at=path; at.Length!=0; at=PathNames.Parent(at))
            if(heldInput(new Scope(localPath,at)).Any(c=>c is {Side:Side.Dropbox,Kind:ChangeKind.Deleted,Path:{} gone} && PathNames.Same(gone,at))) return true;
        return false;
    }

    private static async ValueTask<IReadOnlyList<RenameCandidate>> RenameCandidatesAsync(PairConfig pair,Change change,string key,SyncedEntry? saved,Row row,IStore store,Action<string,bool>? failed,CancellationToken ct)
    {
        IReadOnlyList<RenameCandidate> candidates=[];
        if (change.Kind==ChangeKind.Renamed && change.OldPath is {} old && row.EntryOn(change.Side) is {} moved)
        {
            var original=await store.FindAsync(pair.PairId,change.Side,PathNames.KeyFor(change.Side,old),ct);
            if (original!=null && original.IdOn(change.Side)==moved.Id) candidates=[new(change.Side,moved,original)];
        }
        var incoming=row.EntryOn(change.Side);
        if(candidates.Count==0 && change.Kind is ChangeKind.Changed or ChangeKind.Renamed && incoming!=null && incoming.Id==change.Id && (saved?.IdOn(change.Side)!=incoming.Id || !PathNames.SamePlace(saved.PathOn(change.Side),incoming.Path)))
        {
            SyncedEntry? source=null;
            await foreach(var binding in store.ByIdAsync(pair.PairId,change.Side,incoming.Id,ct))
            {
                // A second bound name leaves the predecessor unproved (an unlink plus a new hard link reads the same), so the input
                // waits; only B's own key proves it: a capitals-only rename, one Dropbox name either way.
                if(source!=null)
                {
                    if(saved?.IdOn(change.Side)!=incoming.Id) Retain(key,failed);
                    source=saved;
                    break;
                }
                source=binding;
            }
            if(source!=null) candidates=[new(change.Side,incoming,source)];
        }
        return candidates;
    }

    private static async ValueTask<(Row Row,Notice? PutBack)> ChangedPutBackAsync(PairConfig pair,Change change,string key,SyncedEntry? saved,Row row,Entry? incoming,IReadOnlyList<RenameCandidate> candidates,List<Notice> notices,Notice? reviewed,ISide local,IStore store,CancellationToken ct)
    {
        Notice? changedPutBack=null;
        var baseline=candidates.FirstOrDefault(c=>c.Side==change.Side && c.Moved.Id==incoming?.Id)?.Synced??saved;
        if(reviewed==null && incoming is {Skip:null} current && current.Id==change.Id && current.Id.Length!=0 && baseline!=null && baseline.IdOn(change.Side)==current.Id)
        {
            bool IsWarning(Notice n)=>n.Kind==NoticeKind.PutBack && n.Subject.Incoming==change.Side && n.Subject.Items.Any(i=>i.Side==change.Side && i.Id==current.Id && PathNames.Same(i.Path,n.Path));
            Notice? warning=null;
            var subjectKey=baseline.KeyOn(change.Side);
            if(subjectKey==key) warning=notices.FirstOrDefault(IsWarning);
            else await foreach(var n in store.NoticesAtAsync(pair.PairId,subjectKey,ct)) if(IsWarning(n)) {warning=n;break;}
            if(warning!=null)
            {
                var changed=Decide.Changed(change.Side,current,baseline,row.LocalHash,change.LocalContentDirty);
                if(changed==null)
                {
                    row=row with {LocalHash=await local.ContentHashAsync(current,ct)};
                    changed=Decide.Changed(change.Side,current,baseline,row.LocalHash,change.LocalContentDirty);
                }
                if(changed==true) changedPutBack=warning;
            }
        }
        return (row,changedPutBack);
    }

    // A refused observation is not absence. Keep the binding and the exact acquired subject.
    private static async Task<bool> ReportChangeFailureAsync(PairConfig pair,Action<RunEvent> events,Change change,string path,string key,SyncedEntry? saved,Scope endpoints,IssueSubject.FirstRunDeletionKind debt,IStore store,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,Notice? reviewed,ItemFailedException error,CancellationToken ct)
    {
        var items=new List<NoticeItem>();
        if(saved!=null) { items.Add(new(Side.Local,saved.LocalPath,saved.LocalId)); items.Add(new(Side.Dropbox,saved.DropboxPath,saved.DropboxId)); }
        if(change.Id is {Length:>0} id) items.Add(new(change.Side,path,id));
        var subject=reviewed?.Subject ?? (IssueSubject.FromDistinct(items,change.Side) with {FirstRunDeletion=debt});
        bool reproducible=saved is {IsFolder:false} && error.Disposition is FailureDisposition.Permanent or FailureDisposition.UnknownEffect && change.Id==saved.IdOn(change.Side) && change.OldPath==null && change.ConsumeHeld==null && heldInput?.Invoke(endpoints).Any(x=>x.ConsumeHeld!=null)!=true;
        await ReportFailureAsync(pair,store,events,error,subject,key,reproducible,failed,ct,replacing:reviewed);
        // An unknown effect without a saved job is not proved; Repair recompares the bound leaf and may resend it.
        if(reproducible && error.Disposition==FailureDisposition.UnknownEffect) await store.OweScopeAsync(pair.PairId,endpoints,ct);
        return reproducible;
    }

    internal static async Task<(Row Row,IReadOnlyList<RenameCandidate> Group)> ObserveBindingAsync(PathScope paths,SyncedEntry binding,ISide local,ISide dropbox,CancellationToken ct)
    {
        var a=local.ReadByIdAsync(binding.LocalId,ct);var b=dropbox.ReadByIdAsync(binding.DropboxId,ct);
        await Task.WhenAll(a,b);
        var pc=await a is {InScope:true} l?l.Location.Entry:null;
        var db=await b is {InScope:true} d?d.Location.Entry:null;
        if(pc!=null && pc.Id!=binding.LocalId || db!=null && db.Id!=binding.DropboxId) throw new ChangedUnderneathException(binding.LocalPath);
        List<RenameCandidate> moved=[];
        if(pc!=null && !PathNames.SamePlace(binding.LocalPath,pc.Path)) moved.Add(new(Side.Local,pc,binding));
        if(db!=null && !PathNames.SamePlace(binding.DropboxPath,db.Path)) moved.Add(new(Side.Dropbox,db,binding));
        return (new Row(binding.LocalKey,pc,db,binding,[],null,RowContext.Neutral with {OutOfScope=paths.Excludes(pc?.Path??binding.LocalPath,db?.Path??binding.DropboxPath,binding.IsFolder)}),moved);
    }

    /// <summary>Close only exact notices settled by this successful comparison.</summary>
    public static async Task CloseComparedNoticesAsync(string pairId,Change input,Entry? incoming,Entry? counterpart,IEnumerable<Notice> notices,IStore store,CancellationToken ct)
    {
        foreach(var n in notices)
        {
            // Ordinary comparison closes Failure only; PutBack needs the bound item's content-change decision.
            if(n.Kind==NoticeKind.Failure && n.Subject.Items.Any(i=>i.Id==(i.Side==input.Side?incoming:counterpart)?.Id))
                await store.CloseNoticeAsync(pairId,n,ct);
        }
    }

    // The two shape predicates of a removal commit's close (lanes widen these in place, never add a closer): a closable Failure, and members that are exactly the binding.
    // A path-only tombstone's wait names it by an empty-ID Dropbox member; that member is matched by the binding's name.
    internal static bool Closable(Notice n) => n.Kind==NoticeKind.Failure && n.Subject.Move==null && n.Subject.Items.Count>0 && n.Subject.Items.All(i=>i.Id.Length!=0 || WaitsOnPath(n,i.Side,i.Path));
    internal static bool Exact(Notice n,SyncedEntry b) => n.Subject.Items.All(i=>(i.Id.Length==0 || i.Id==b.IdOn(i.Side)) && i.Path==b.PathOn(i.Side));
    public static bool WaitsOnPath(Notice n,Side side,string path) => n is {Kind:NoticeKind.Failure,Cause:TextKey.WaitItemRetry} && n.Subject.Items.Any(i=>i.Side==side && i.Id.Length==0 && PathNames.Same(i.Path,path)); // the path-only input's own identity is its member

    internal static bool Recursive(SyncAction action)
    {
        var row=action.Row;
        if(action.Kind is ActionKind.DeleteLocal or ActionKind.DeleteDropbox) return row.EntryOn(action.WrittenSide())?.IsFolder==true;
        if(action.Kind==ActionKind.Record && row.Synced is {} old && row.Local is {IsFolder:true} pc && row.Dropbox is {IsFolder:true} db)
            return !old.IsFolder || !PathNames.SamePlace(old.LocalPath,pc.Path) || !PathNames.SamePlace(old.DropboxPath,db.Path);
        return (row.Local?.IsFolder==true || row.Dropbox?.IsFolder==true || row.Synced?.IsFolder==true) && action.Kind is (ActionKind.ReplaceKind or ActionKind.DeferFolder or ActionKind.MoveLocal or ActionKind.MoveDropbox or ActionKind.MoveUnlinked or ActionKind.KeepBoth);
    }

    // A completed provider call can still have unsettled work. An unknown outcome stays uncertain, never a plain refusal.
    // False: Dropbox accepted a saved job; the input is released and the job checker settles the effect.
    public static async Task<bool> RequireRemovalAsync(Task<RemovalResult> operation, Action? retain=null, string? path=null)
    {
        var result=await operation;
        if(result.Outcome==RemovalOutcome.Removed) return true;
        if(result is {Accepted:not null,Outcome:RemovalOutcome.Pending or RemovalOutcome.Unknown}) return false;
        // Raise the unknown outcome before retaining: a retain callback that throws must not erase it; the failure owner retains after its notice.
        if(result.Outcome==RemovalOutcome.Unknown)
            throw new ItemFailedException(result.Final?.Path ?? path ?? string.Empty,result.Cause ?? TextKey.FailDropboxOutcomeUnknown,null){Disposition=FailureDisposition.UnknownEffect,Provider=result.Provider};
        retain?.Invoke();
        throw new InventoryIncompleteException(nameof(RemovalOutcome),null);
    }

    internal static async Task ReportFailureAsync(PairConfig pair,IStore store,Action<RunEvent> events,ItemFailedException error,IssueSubject subject,string key,bool reproducible,Action<string,bool>? failed,CancellationToken ct,Action<Notice>? failedNotice=null,Notice? replacing=null)
    {
        subject=subject with {Holder=error.Holder??subject.Holder,Move=error.Move??subject.Move};
        var original=new Notice(0,NoticeKind.Failure,error.Cause,error.Path,subject,DateTimeOffset.UtcNow,false);
        await new Notices().RaiseAsync(store,pair.PairId,original,ct,replacing?.Path==original.Path?replacing:null);
        failedNotice?.Invoke(original);
        events(new(RunEventKind.Failed,NoticeKind.Failure.ToString(),error.Path,error.Message,NoticeKind.Failure.ToString()){Cause=error.Cause,Provider=error.Provider});
        failed?.Invoke(key,reproducible);
    }

    public static bool FinishDecision(SyncAction action,Action<string,bool>? failed)
    {
        if(action.None==NoneReason.Unfinished) Retain(action.Row.Key,failed);
        return action.IsInSync();
    }

    internal static void Retain(string key,Action<string,bool>? failed)
    {
        failed?.Invoke(key,false);
        throw new InventoryIncompleteException(nameof(NoneReason.Unfinished),null);
    }

    private static bool Echo(Change change,SyncedEntry? saved)
    {
        if (change is not {Kind:ChangeKind.Changed,OldPath:null,IdentityDeleted:false,Observed:{Skip:null} e} || saved is null || e.Id.Length==0 || change.Id!=e.Id || e.Id!=saved.IdOn(change.Side) || change.Path!=e.Path || e.Path!=saved.PathOn(change.Side) || e.IsFolder!=saved.IsFolder) return false;
        if (change.Side==Side.Dropbox) return e.IsFolder || ContentHash.IsValid(e.ContentHash) && e.ContentHash==saved.DropboxContentHash;
        return change.Usn is {} usn && saved.LocalPublished is {} boundary && change.JournalId==boundary.JournalId && usn<=boundary.NextUsn && (e.IsFolder || e.Size==saved.LocalSize && e.Modified.UtcTicks==saved.LocalModifiedTicks);
    }

    public static async Task<string> CounterpartAsync(PairConfig pair,Side side,string path,IStore store,CancellationToken ct)
    {
        var other=PathNames.Other(side);
        foreach(var parent in PathNames.Ancestors(path))
            if (await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,parent),ct) is {} binding)
                return PathNames.Child(binding.PathOn(other),PathNames.SpellingOn(other,path[(parent.Length+1)..]));
        return PathNames.SpellingOn(other,path);
    }
}
