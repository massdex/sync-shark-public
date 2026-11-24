using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>One observe, decide and apply path. Returning means the input settled; exceptions retain it.</summary>
public static class ItemSync
{
    private static readonly ItemLocks Locks = new();

    public static Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change change, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput=null, Action<string,bool>? failed=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null) =>
        ChangeAsync(pair,settings,events,paths,change,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,coversThrough:coversThrough);

    internal static async Task<bool> ChangeAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change change, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput=null, Action<string,bool>? failed=null,Notice? reviewed=null,NoticeItem? member=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null)
    {
        var path=change.Path ?? change.Observed?.Path ?? throw new InventoryIncompleteException(nameof(Change.Path),null);
        var key=PathNames.KeyFor(change.Side,path);
        var saved=await store.FindAsync(pair.PairId,change.Side,key,ct);
        var notices=new List<Notice>();
        await foreach(var n in store.NoticesAtAsync(pair.PairId,key,ct)) notices.Add(n);
        if (reviewed==null && notices.Count==0 && Echo(change,saved)) return true;
        var other=PathNames.Other(change.Side);
        var mapped=saved?.PathOn(other) ?? await CounterpartAsync(pair,change.Side,path,store,ct);
        var endpoints=change.Side==Side.Local ? new Scope(path,mapped) : new Scope(mapped,path);
        var debt=reviewed?.Subject.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None;
        try
        {
            var tombstone=change.Side==Side.Dropbox && change.Kind==ChangeKind.Deleted && saved!=null;
            bool selectedRemoval=change.Kind==ChangeKind.Deleted && saved!=null && (change.Side==Side.Dropbox ? change.Id==null || change.Id==saved.DropboxId : change.IdentityDeleted && change.Id is {Length:>0} removed && removed==saved.LocalId);
            if(tombstone && selectedRemoval && await dropbox.ReadByIdAsync(saved!.DropboxId,ct) is {InScope:true} location)
            {
                var actual=location.Location.Entry; RequireReadable(Side.Dropbox,actual);
                if(actual.Id!=saved.DropboxId || actual.IsFolder!=saved.IsFolder) Retain(key,failed);
                if(actual.Path==saved.DropboxPath) return true; // The same identity still owns this name; this tombstone cannot delete it.
                return await ExecuteAsync(pair,settings,events,paths,null,[new(Side.Dropbox,actual,saved)],false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false,coversThrough:coversThrough);
            }
            if(saved is {IsFolder:true} && selectedRemoval)
                return await RemoveBoundAsync(pair,settings,events,paths,saved,change.Side,local,dropbox,store,heldInput,populate,compareOldBinding,failed,ct,coversThrough);
            var pcTask=local.ReadOneAsync(endpoints.LocalPath,ct); var dbTask=dropbox.ReadOneAsync(endpoints.DropboxPath,ct);
            await Task.WhenAll(pcTask,dbTask);
            var pc=await pcTask; var db=await dbTask;
            RequireReadable(Side.Local,pc); RequireReadable(Side.Dropbox,db);
            // At a reused name settle the old identities, not the item that inherited the name.
            if(change is {Side:Side.Dropbox,Kind:ChangeKind.Changed} && saved!=null && pc?.Id!=saved.LocalId && db!=null && db.Id!=saved.DropboxId)
            {
                var owner=await ObserveBindingAsync(paths,saved,local,dropbox,ct);
                if(!await ExecuteAsync(pair,settings,events,paths,owner.Row,owner.Group,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false,coversThrough:coversThrough)) Retain(key,failed);
                if(await store.FindAsync(pair.PairId,change.Side,key,ct)==saved) Retain(key,failed);
                return await ChangeAsync(pair,settings,events,paths,change,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,reviewed,member,coversThrough);
            }
            // A missing journal path may already have moved. Only the selected identity's removal authorizes deletion.
            if(change.Side==Side.Local && saved!=null && !selectedRemoval && (pc==null || change.Id==saved.LocalId && pc.Id!=saved.LocalId))
            {
                var currentIdentity=change.Id==saved.LocalId && (change.Kind==ChangeKind.Deleted || heldInput!=null) ? await local.ReadByIdAsync(saved.LocalId,ct) : null;
                if(change.Kind==ChangeKind.Deleted && change.Id is {Length:>0} selected && selected==saved.LocalId && currentIdentity is null or {InScope:false})
                {
                    selectedRemoval=true;
                    if(saved.IsFolder) return await RemoveBoundAsync(pair,settings,events,paths,saved,Side.Local,local,dropbox,store,heldInput,populate,compareOldBinding,failed,ct,coversThrough);
                }
                else if(change.Id==saved.LocalId && heldInput!=null && currentIdentity is {InScope:true} located)
                {
                    var relocated=located.Location.Entry; RequireReadable(Side.Local,relocated);
                    var rename=heldInput(endpoints).FirstOrDefault(c=>c is {Side:Side.Local,Kind:ChangeKind.Renamed} && c.Id==saved.LocalId && c.OldPath==saved.LocalPath && c.Path==relocated.Path && c.JournalId==change.JournalId && c.Usn>change.Usn);
                    if(rename!=null && relocated.Id==saved.LocalId && relocated.IsFolder==saved.IsFolder && relocated.Path!=saved.LocalPath)
                    {
                        bool settled=await ExecuteAsync(pair,settings,events,paths,null,[new(Side.Local,relocated,saved)],change.LocalContentDirty||rename.LocalContentDirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false,coversThrough:coversThrough);
                        if(!settled) Retain(key,failed);
                        return true; // The rename keeps its own ordered input and reobserves the committed binding.
                    }
                }
                if(!selectedRemoval) Retain(key,failed);
            }
            // A successor at the old name belongs to its own input, not the removed file's row.
            var successor=selectedRemoval && change.Side==Side.Local && pc?.Id!=saved!.LocalId ? pc : null;
            // An observed replacement file is current even if its rename arrives on the next journal page.
            bool safeSave=change.IdentityDeleted && successor is {IsFolder:false} replacement && saved is {IsFolder:false} && replacement.Path==saved.LocalPath;
            if(successor!=null && !safeSave) pc=null;
            var incoming=change.Side==Side.Local?pc:db;
            if(!selectedRemoval && change.Id is {Length:>0} expected && incoming?.Id!=expected) Retain(key,failed);
            if(member!=null && (change.Side==Side.Local?pc:db)?.Id!=member.Id) return false;
            bool Matches(Notice n) => n.Subject.Items.Any(i=>i.Id==(i.Side==Side.Local?pc:db)?.Id);
            var tie=notices.FirstOrDefault(n=>n.Kind==NoticeKind.Tie && Matches(n));
            var historical=reviewed!=null && reviewed.Subject.FirstRunDeletion!=IssueSubject.FirstRunDeletionKind.None && Matches(reviewed) ? reviewed : notices.FirstOrDefault(n=>n.Kind==NoticeKind.Failure && n.Subject.FirstRunDeletion!=IssueSubject.FirstRunDeletionKind.None && Matches(n));
            debt=historical?.Subject.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None;
            var context=RowContext.Neutral with { Destination=mapped, OutOfScope=paths.Excludes(endpoints.LocalPath,endpoints.DropboxPath,(pc??db)?.IsFolder??saved?.IsFolder??false),Skipped=pc?.Skip!=null || db?.Skip!=null,Held=tie!=null,HeldKind=tie?.Kind, FirstRunDeletion=historical?.Subject.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None, DeletionEligible=historical!=null };
            var row=new Row(key,pc,db,saved,[],null,context){ObservationPath=path};
            if (saved is null && pc is {IsFolder:false} && db is null && debt==IssueSubject.FirstRunDeletionKind.Item && historical!.Subject.Items.Any(i=>i.Side==Side.Local && i.Id==pc.Id && PathNames.Same(i.Path,pc.Path)))
                row=row with {Deletion=await dropbox.DeletedAsync(endpoints.DropboxPath,false,ct),DeletionClock=dropbox.ClockOffset,
                    Context=context with {CompleteInventory=reviewed==historical && member is {Side:Side.Local} && historical!.Subject.Items.Count==1}};
            IReadOnlyList<RenameCandidate> candidates=[];
            if (change.Kind==ChangeKind.Renamed && change.OldPath is {} old && row.EntryOn(change.Side) is {} moved)
            {
                var original=await store.FindAsync(pair.PairId,change.Side,PathNames.KeyFor(change.Side,old),ct);
                if (original!=null && original.IdOn(change.Side)==moved.Id) candidates=[new(change.Side,moved,original)];
            }
            if(candidates.Count==0 && change.Side==Side.Dropbox && change.Kind==ChangeKind.Changed && db!=null && db.Id==change.Id && (saved?.DropboxId!=db.Id || saved.DropboxPath!=db.Path))
            {
                await foreach(var binding in store.ByIdAsync(pair.PairId,Side.Dropbox,db.Id,ct))
                {
                    if(candidates.Count!=0) Retain(key,failed);
                    if(binding.DropboxPath!=db.Path) candidates=[new(Side.Dropbox,db,binding)];
                }
            }
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
            bool ok=await ExecuteAsync(pair,settings,events,paths,row,candidates,safeSave||change.LocalContentDirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false,coversThrough:coversThrough,observedMissing:selectedRemoval && change.Side==Side.Local?(change.Side,successor):null);
            if (!ok || reviewed!=null) return ok;
            if(changedPutBack!=null) await store.CloseNoticeAsync(pair.PairId,changedPutBack,ct);
            await CloseComparedNoticesAsync(pair.PairId,change,incoming,change.Side==Side.Local?db:pc,notices,store,ct);
            return true;
        }
        catch(ItemFailedException error) when(error.Disposition!=FailureDisposition.TemporaryNoEffect)
        {
            // A refused observation is not absence. Keep the binding and the exact acquired subject.
            var items=new List<NoticeItem>();
            if(saved!=null) { items.Add(new(Side.Local,saved.LocalPath,saved.LocalId)); items.Add(new(Side.Dropbox,saved.DropboxPath,saved.DropboxId)); }
            if(change.Id is {Length:>0} id) items.Add(new(change.Side,path,id));
            var subject=reviewed?.Subject ?? (IssueSubject.FromDistinct(items,change.Side) with {FirstRunDeletion=debt});
            bool reproducible=saved is {IsFolder:false} && error.Disposition==FailureDisposition.Permanent && change.Id==saved.IdOn(change.Side) && change.OldPath==null && change.ConsumeHeld==null && heldInput?.Invoke(endpoints).Any(x=>x.ConsumeHeld!=null)!=true;
            await ReportFailureAsync(pair,store,events,error,subject,key,reproducible,failed,ct);
            if(!reproducible) throw;
            return false;
        }

    }

    internal static async Task<(Row Row,IReadOnlyList<RenameCandidate> Group)> ObserveBindingAsync(PathScope paths,SyncedEntry binding,ISide local,ISide dropbox,CancellationToken ct)
    {
        var a=local.ReadByIdAsync(binding.LocalId,ct);var b=dropbox.ReadByIdAsync(binding.DropboxId,ct);
        await Task.WhenAll(a,b);
        var pc=await a is {InScope:true} l?l.Location.Entry:null;
        var db=await b is {InScope:true} d?d.Location.Entry:null;
        if(pc!=null && pc.Id!=binding.LocalId || db!=null && db.Id!=binding.DropboxId) throw new ChangedUnderneathException(binding.LocalPath);
        List<RenameCandidate> moved=[];
        if(pc!=null && pc.Path!=binding.LocalPath) moved.Add(new(Side.Local,pc,binding));
        if(db!=null && db.Path!=binding.DropboxPath) moved.Add(new(Side.Dropbox,db,binding));
        return (new Row(binding.LocalKey,pc,db,binding,[],null,RowContext.Neutral with {OutOfScope=paths.Excludes(pc?.Path??binding.LocalPath,db?.Path??binding.DropboxPath,binding.IsFolder)}),moved);
    }

    /// <summary>Close only exact notices settled by this successful comparison.</summary>
    public static async Task CloseComparedNoticesAsync(string pairId,Change input,Entry? incoming,Entry? counterpart,IEnumerable<Notice> notices,IStore store,CancellationToken ct)
    {
        foreach(var n in notices)
        {
            // Ordinary comparison closes Failure only; PutBack needs the bound item’s content-change decision.
            if(n.Kind==NoticeKind.Failure && n.Subject.Items.Any(i=>i.Id==(i.Side==input.Side?incoming:counterpart)?.Id))
                await store.CloseNoticeAsync(pairId,n,ct);
        }
    }

    // Ancestors are selected before acquisition. The selected-entry overload cannot widen after the grant.
    private static async Task<bool> RemoveBoundAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,SyncedEntry folder,Side deleted,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>>? heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,Action<string,bool>? failed,CancellationToken ct,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null)
    {
        var source=deleted==Side.Local?local:dropbox; var target=deleted==Side.Local?dropbox:local;
        var highest=await FolderDelete.HighestMissingAsync(pair,paths,folder,source,store,ct);
        if(highest is null) { Retain(folder.KeyOn(deleted),failed); return false; }
        folder=highest;
        var row=new Row(folder.KeyOn(deleted),null,null,folder,[],null,RowContext.Neutral);
        return await Locks.RunAsync(pair,new(ActionKind.DeferFolder,row),[],async ()=>
        {
            if(heldInput==null || paths.ExcludesBinding(folder) || await store.FindAsync(pair.PairId,deleted,folder.KeyOn(deleted),ct)!=folder || await source.ReadByIdAsync(folder.IdOn(deleted),ct) is {InScope:true}) Retain(row.Key,failed);
            var found=await target.ReadByIdAsync(folder.IdOn(target.Side),ct);
            // Both bound identities are gone (R57, R95 B1): the selected removal already settled; retire the binding as ForgetOnly does.
            if(found is not {InScope:true} at) { await store.CommitAsync(pair.PairId,folder,null,null,null,ct); return true; }
            if(!at.Location.Entry.IsFolder || at.Location.Entry.Id!=folder.IdOn(target.Side) || !PathNames.Same(at.Location.Entry.Path,folder.PathOn(target.Side))) { Retain(row.Key,failed); return false; }
            var selected=at.Location.Entry; RequireReadable(target.Side,selected);
            await RequireRemovalAsync(new FolderDelete().ApplyAsync(pair,settings,events,paths,selected,folder,target.Side,SyncAction.DeleteOn(target.Side),local,dropbox,store,heldInput!,populate,compareOldBinding,ct,resolveConflict:(where,side,version,token)=>new Effects().RecoverConflictAsync(pair,settings,events,paths,where,side,version,local,dropbox,store,populate,compareOldBinding,token),coversThrough:coversThrough),()=>failed?.Invoke(row.Key,false));
            return true;
        },ct);
    }

    private static void RequireReadable(Side side,Entry? entry)
    {
        if(entry is {Skip:nameof(TextKey.SkipUnreadable)}) throw new ItemFailedException(entry.Path,side==Side.Local?TextKey.ReadLocalFailed:TextKey.ReadDropboxFailed,null);
    }

    internal static bool Recursive(SyncAction action)
    {
        var row=action.Row;
        if(action.Kind is ActionKind.DeleteLocal or ActionKind.DeleteDropbox) return row.EntryOn(action.WrittenSide())?.IsFolder==true;
        if(action.Kind==ActionKind.Record && row.Synced is {} old && row.Local is {IsFolder:true} pc && row.Dropbox is {IsFolder:true} db)
            return !old.IsFolder || old.LocalPath!=pc.Path || old.DropboxPath!=db.Path;
        return (row.Local?.IsFolder==true || row.Dropbox?.IsFolder==true || row.Synced?.IsFolder==true) && action.Kind is (ActionKind.ReplaceKind or ActionKind.DeferFolder or ActionKind.MoveLocal or ActionKind.MoveDropbox or ActionKind.MoveUnlinked or ActionKind.KeepBoth);
    }

    // A completed provider call can still have unsettled work.
    public static async Task RequireRemovalAsync(Task<RemovalResult> operation, Action? retain=null)
    {
        if((await operation).Outcome==RemovalOutcome.Removed) return;
        retain?.Invoke();
        throw new InventoryIncompleteException(nameof(RemovalOutcome),null);
    }

    internal static async Task ReportFailureAsync(PairConfig pair,IStore store,Action<RunEvent> events,ItemFailedException error,IssueSubject subject,string key,bool reproducible,Action<string,bool>? failed,CancellationToken ct,Action<Notice>? failedNotice=null)
    {
        subject=subject with {Holder=error.Holder??subject.Holder,Move=error.Move??subject.Move};
        var original=new Notice(0,NoticeKind.Failure,error.Cause,error.Path,subject,DateTimeOffset.UtcNow,false);
        await new Notices().RaiseAsync(store,pair.PairId,original,ct);
        failedNotice?.Invoke(original);
        events(new(RunEventKind.Failed,NoticeKind.Failure.ToString(),error.Path,error.Message,NoticeKind.Failure.ToString()){Cause=error.Cause});
        failed?.Invoke(key,reproducible);
    }

    public static bool FinishDecision(SyncAction action,Action<string,bool>? failed)
    {
        if(action.None==NoneReason.Unfinished) Retain(action.Row.Key,failed);
        return action.IsInSync();
    }

    private static void Retain(string key,Action<string,bool>? failed)
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

    /// <summary>Keep acquisition context and decide a rename group as a unit. Population admits only additions.</summary>
    public static async Task<EffectOutcome?> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Row? prepared, IReadOnlyList<RenameCandidate> bindingCandidates, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput=null, Action<string,bool>? failed=null, bool addOnly=false, Action<Notice>? failedNotice=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null)
    {
        try { return (await ExecuteWithOutcomeAsync(pair,settings,events,paths,prepared,bindingCandidates,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,addOnly,failedNotice,coversThrough)).Outcome; }
        catch(ItemFailedException error) when(error.Disposition!=FailureDisposition.TemporaryNoEffect)
        {
            var items=new List<NoticeItem>();
            if(prepared?.Local is {} pc) items.Add(new(Side.Local,pc.Path,pc.Id));
            if(prepared?.Dropbox is {} db) items.Add(new(Side.Dropbox,db.Path,db.Id));
            foreach(var c in bindingCandidates) items.Add(new(c.Side,c.Moved.Path,c.Moved.Id));
            var subject=IssueSubject.FromDistinct(items,null) with {FirstRunDeletion=prepared?.Context.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None};
            bool reproducible=prepared?.Synced is {IsFolder:false} && bindingCandidates.Count==0 && items.Count>0 && error.Disposition==FailureDisposition.Permanent && heldInput?.Invoke(new(Row.Paths(prepared).Local,Row.Paths(prepared).Dropbox)).Any(c=>c.ConsumeHeld!=null)!=true;
            await ReportFailureAsync(pair,store,events,error,subject,prepared?.Key??PathNames.Key(error.Path),reproducible,failed,ct,failedNotice);
            if(!reproducible) throw;
        }
        return null;
    }

    internal static async Task<bool> ExecuteAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Row? row,IReadOnlyList<RenameCandidate> candidates,bool dirty,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,bool addOnly,Action<Notice>? failedNotice=null,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough=null,(Side Side,Entry? Occupant)? observedMissing=null)
        => (await ExecuteWithOutcomeAsync(pair,settings,events,paths,row,candidates,dirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,addOnly,failedNotice,coversThrough,observedMissing)).Settled;

    private static async Task<(bool Settled,EffectOutcome? Outcome)> ExecuteWithOutcomeAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Row? row,IReadOnlyList<RenameCandidate> candidates,bool dirty,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,bool addOnly,Action<Notice>? failedNotice,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough,(Side Side,Entry? Occupant)? observedMissing=null)
    {
        if (row is null && candidates.Count==0) throw new ArgumentException(nameof(row));
        RequireReadable(Side.Local,row?.Local); RequireReadable(Side.Dropbox,row?.Dropbox);
        var now=DateTimeOffset.UtcNow;
        var priorBinding=row?.Synced;
        // Use final mapped names for the content decision, but commit against the actual old binding.
        bool mappedChild=false;
        if(candidates.Count==0 && row?.Synced is {IsFolder:false} original && row.Local is {IsFolder:false} left && row.Dropbox is {IsFolder:false} right &&
            left.Id==original.LocalId && right.Id==original.DropboxId && left.Path!=original.LocalPath && right.Path!=original.DropboxPath)
        {
            if(await CounterpartAsync(pair,Side.Local,left.Path,store,ct)!=right.Path) Retain(row.Key,failed);
            row=row with {Synced=SyncedEntries.Relink(original,left,right)};mappedChild=true;
        }
        else if(candidates.Count==0 && row?.Synced is {IsFolder:false} single && (row.Local==null)!=(row.Dropbox==null) && row.Context.Destination is {} far &&
            (row.Local??row.Dropbox) is {IsFolder:false} present && present.Id==single.IdOn(row.Local!=null?Side.Local:Side.Dropbox) &&
            present.Path!=single.PathOn(row.Local!=null?Side.Local:Side.Dropbox) && far!=single.PathOn(row.Local!=null?Side.Dropbox:Side.Local))
        {
            // A one-sided child at a confirmed hop: decide at both final names, commit against the old binding.
            var (pcPath,dbPath)=row.Local!=null?(present.Path,far):(far,present.Path);
            if(await CounterpartAsync(pair,Side.Local,pcPath,store,ct)!=dbPath) Retain(row.Key,failed);
            row=row with {Synced=single with {LocalPath=pcPath,LocalKey=PathNames.Key(pcPath),DropboxPath=dbPath,DropboxKey=PathNames.DropboxKey(dbPath)}};mappedChild=true;
        }
        var action=candidates.Count==0 ? Decide.For(row!,pair.FirstRun,now,dirty) :
            await Effects.DecideMoveAsync(pair,settings,paths,candidates,local,dropbox,store,dirty,ct);
        row=action.Row;
        if(candidates.Count==0 && action.Kind==ActionKind.NeedLocalHash && row.Local is {} file)
        { row=row with {LocalHash=await local.ContentHashAsync(file,ct)}; action=Decide.For(row,pair.FirstRun,now,dirty); }

        bool classifyFolder=action.Kind==ActionKind.DeferFolder && row.Synced is null;
        if(classifyFolder) action=Classify(row);
        if(action.Kind==ActionKind.NeedDeletionEvidence)
        {
            row=action.Row;
            row=row with {Deletion=await dropbox.DeletedAsync(action.Target??Row.Paths(row).Dropbox,row.IsFolder(),ct),DeletionClock=dropbox.ClockOffset};
            action=classifyFolder?Classify(row):Decide.For(row,pair.FirstRun,now,dirty);
        }
        if(action.Kind==ActionKind.NeedLocalHash && action.Row.Local is {} requested && action.Row.LocalHash==null)
        {
            row=action.Row with {LocalHash=await local.ContentHashAsync(requested,ct)};
            action=classifyFolder?Classify(row):Decide.For(row,pair.FirstRun,now,dirty);
        }
        row=action.Row;
        if(mappedChild)
        {
            row=row with {Synced=priorBinding};
            action=action.IsInSync()?new(ActionKind.Record,row):action with {Row=row};
        }
        SyncAction Classify(Row r)
        {
            var mark=r.Context.ProtectedContents;
            if(mark is null && r.Context.UnreadableCause is null) Retain(r.Key,failed);
            return FolderDeletes.For(r,mark==true,r.Context.UnreadableCause,pair.FirstRun,now,dirty);
        }
        if(action.Kind==ActionKind.None) return (FinishDecision(action,failed),null);
        if(addOnly && !(action.Kind==ActionKind.Record || action.Kind is (ActionKind.Upload or ActionKind.EnsureDropboxFolder) && row.Dropbox==null || action.Kind is (ActionKind.Download or ActionKind.EnsureLocalFolder) && row.Local==null)) return (true,null);
        if(action.Kind==ActionKind.DeferFolder && row.Synced is {} selectedFolder)
        {
            var removed=await RemoveBoundAsync(pair,settings,events,paths,selectedFolder,row.Local==null?Side.Local:Side.Dropbox,local,dropbox,store,heldInput,populate,compareOldBinding,failed,ct,coversThrough);
            return (removed,null);
        }
        var endpoints=Row.Paths(row);
        // Only a create into the absent bound leg can collide with an independent successor.
        Entry? successor=null;Side? missing=null;
        if(row.Synced is {} prior && action.Kind is (ActionKind.Download or ActionKind.Upload or ActionKind.ReplaceKind or ActionKind.EnsureLocalFolder or ActionKind.EnsureDropboxFolder) && row.EntryOn(action.WrittenSide())==null)
        {
            missing=action.WrittenSide();var leg=missing==Side.Local?local:dropbox;
            var occupant=observedMissing is {} observed && observed.Side==missing ? observed.Occupant : await leg.ReadOneAsync(action.Target ?? prior.PathOn(leg.Side),ct);
            if(occupant!=null)
            {
                if(occupant.Id==prior.IdOn(leg.Side)) throw new ChangedUnderneathException(occupant.Path);
                RequireReadable(leg.Side,occupant);successor=occupant;
                row=leg.Side==Side.Local?row with {Local=occupant}:row with {Dropbox=occupant};
                var occupied=row with {Synced=null,Context=row.Context with {FirstRun=false}};
                action=Decide.For(occupied,pair.FirstRun,now,dirty);
                if(action.Kind==ActionKind.NeedLocalHash)
                {
                    occupied=occupied with {LocalHash=await local.ContentHashAsync(occupied.Local!,ct)};
                    action=Decide.For(occupied,pair.FirstRun,now,dirty);
                }
                row=occupied with {Synced=prior};action=action with {Row=row};
            }
        }
        SyncedEntry? incumbent=null;
        if(action.Kind is (ActionKind.MoveLocal or ActionKind.MoveDropbox) && row.Synced is {} moving && action.Target is {} target)
        {
            var written=action.WrittenSide();var leg=written==Side.Local?local:dropbox;
            if(await leg.ReadOneAsync(target,ct) is {} occupant && occupant.Id!=moving.IdOn(written))
                incumbent=await store.FindAsync(pair.PairId,written,PathNames.KeyFor(written,target),ct);
            if(incumbent==moving) incumbent=null;
        }
        Task<(bool,EffectOutcome?)> ApplyWithLocks() => Locks.RunAsync(pair,action,candidates,ApplyLocked,ct,settings.CaseRenameSuffix,incumbent);
        var applied=Moves.IsFileSplit(action) ? await ApplySplitLocked() : await ApplyWithLocks();
        async Task<(bool,EffectOutcome?)> ApplySplitLocked()
        {
            var keys=new List<(Side,string,bool)>();
            foreach(var side in new[]{Side.Local,Side.Dropbox})
            {
                void Add(Side leg,string path) => keys.Add((leg,ItemLocks.Physical(pair,leg,path),false));
                Add(side,row.Synced!.PathOn(side));
                if(row.EntryOn(side) is not {} source) continue;
                Add(side,source.Path);
                Add(PathNames.Other(side),await CounterpartAsync(pair,side,source.Path,store,ct));
            }
            return await Locks.RunAsync(keys,ApplyLocked,ct);
        }
        if(applied.Item1 && candidates.Count!=0 && candidates[0] is {Moved.IsFolder:false} move)
        {
            var settled=await store.FindAsync(pair.PairId,move.Side,PathNames.KeyFor(move.Side,move.Moved.Path),ct);
            if(settled!=null && settled.LocalId==move.Synced.LocalId && settled.DropboxId==move.Synced.DropboxId && settled.PathOn(move.Side)==move.Moved.Path)
            {
                var final=await ObserveBindingAsync(paths,settled,local,dropbox,ct);
                if(final.Group.Count!=0 || final.Row.Local==null || final.Row.Dropbox==null) Retain(row.Key,failed);
                if(!await ExecuteAsync(pair,settings,events,paths,final.Row,[],dirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,addOnly,failedNotice,coversThrough)) Retain(row.Key,failed);
            }
        }
        return applied;
        async Task<(bool,EffectOutcome?)> ApplyLocked()
        {
            var pcCheck=local.ReadOneAsync(endpoints.Local,ct); var dbCheck=dropbox.ReadOneAsync(endpoints.Dropbox,ct);
            await Task.WhenAll(pcCheck,dbCheck);
            RequireReadable(Side.Local,await pcCheck); RequireReadable(Side.Dropbox,await dbCheck);
            if(row.Synced!=null && row.Local!=null && await pcCheck==null) Retain(row.Key,failed);
            foreach(var side in new[]{Side.Local,Side.Dropbox})
            {
                var seen=side==Side.Local?await pcCheck:await dbCheck;var expected=row.EntryOn(side);
                if(row.Synced is {} owned && (expected==null || successor!=null && missing==side))
                {
                    var leg=side==Side.Local?local:dropbox;
                    if(await leg.ReadByIdAsync(owned.IdOn(side),ct) is {InScope:true} live && !paths.Excludes(side==Side.Local?live.Location.Entry.Path:null,side==Side.Dropbox?live.Location.Entry.Path:null,live.Location.Entry.IsFolder)) throw new ChangedUnderneathException(owned.PathOn(side));
                    // An unrelated occupant is not the missing bound leg. A state-only or peer-only operation never writes it.
                    if(expected==null && seen!=null && (observedMissing is not {} observation || observation.Side!=side || observation.Occupant==seen) && seen.Id!=owned.IdOn(side) && (action.Kind==ActionKind.ForgetOnly || action.Kind is (ActionKind.DeleteLocal or ActionKind.DeleteDropbox) && action.WrittenSide()!=side)) continue;
                }
                if(seen!=expected) throw new ChangedUnderneathException(Row.Path(row));
            }
            if(row.Synced is {} linked && action.Kind is (ActionKind.DeleteLocal or ActionKind.DeleteDropbox) && row.EntryOn(action.WrittenSide())?.Id!=linked.IdOn(action.WrittenSide())) throw new ChangedUnderneathException(Row.Path(row));
            var currentBinding=await store.FindAsync(pair.PairId,Side.Local,row.Synced?.LocalKey??PathNames.Key(endpoints.Local),ct);
            if(currentBinding!=row.Synced) throw new ChangedUnderneathException(Row.Path(row));
            if(action.Kind is ActionKind.NeedLocalHash or ActionKind.NeedDeletionEvidence) throw new InventoryIncompleteException(action.Kind.ToString(),null);
            if(successor is {IsFolder:true} && action.Kind==ActionKind.Record && row.Local is {IsFolder:true} leftFolder && row.Dropbox is {IsFolder:true} rightFolder)
            {
                var scope=new Scope(leftFolder.Path,rightFolder.Path);
                await compareOldBinding(row.Synced!,scope,null,ct);
            }
            // Effects owns physical-leg logging and passes the original hop callback between legs.
            var outcome=await new Effects().ApplyAsync(pair,settings,events,paths,action,local,dropbox,store,populate,compareOldBinding,ct,heldInput,coversThrough,failedNotice,grantedIncumbent:incumbent);
            if(outcome==EffectOutcome.Pending) Retain(row.Key,failed);
            // Vacating another binding only clears the destination; the arriving move still owns its input.
            if(outcome==EffectOutcome.Applied && action.Kind==ActionKind.ForgetOnly && candidates.Count!=0 && row.Synced!=candidates[0].Synced) Retain(row.Key,failed);
            if(outcome==EffectOutcome.Refused) return (false,outcome); // The notice is the decision, not a race.
            return (true,outcome);
        }

    }
}
