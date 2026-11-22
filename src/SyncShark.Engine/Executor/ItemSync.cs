using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>One observe, decide and apply path. Returning means the input settled; exceptions retain it.</summary>
public static class ItemSync
{
    private static readonly ItemLocks Locks = new();

    public static async Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change change, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput=null, Action<string,bool>? failed=null)
    {
        var path=change.Path ?? change.Observed?.Path ?? throw new InventoryIncompleteException(nameof(Change.Path),null);
        var key=PathNames.KeyFor(change.Side,path);
        var saved=await store.FindAsync(pair.PairId,change.Side,key,ct);
        var notices=new List<Notice>();
        await foreach(var n in store.NoticesAtAsync(pair.PairId,key,ct)) notices.Add(n);
        if (notices.Count==0 && Echo(change,saved)) return;
        var other=PathNames.Other(change.Side);
        var mapped=saved?.PathOn(other) ?? await CounterpartAsync(pair,change.Side,path,store,ct);
        var endpoints=change.Side==Side.Local ? new Scope(path,mapped) : new Scope(mapped,path);
        var debt=IssueSubject.FirstRunDeletionKind.None;
        try
        {
            var pcTask=local.ReadOneAsync(endpoints.LocalPath,ct); var dbTask=dropbox.ReadOneAsync(endpoints.DropboxPath,ct);
            await Task.WhenAll(pcTask,dbTask);
            var pc=await pcTask; var db=await dbTask;
            bool Matches(Notice n) => n.Subject.Items.Any(i=>i.Id==(i.Side==Side.Local?pc:db)?.Id);
            var tie=notices.FirstOrDefault(n=>n.Kind==NoticeKind.Tie && Matches(n));
            var historical=notices.FirstOrDefault(n=>n.Kind==NoticeKind.Failure && n.Subject.FirstRunDeletion!=IssueSubject.FirstRunDeletionKind.None && Matches(n));
            debt=historical?.Subject.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None;
            var context=RowContext.Neutral with { Destination=mapped, OutOfScope=paths.Excludes(endpoints.LocalPath,endpoints.DropboxPath,(pc??db)?.IsFolder??saved?.IsFolder??false),Skipped=pc?.Skip!=null || db?.Skip!=null,Held=tie!=null,HeldKind=tie?.Kind, FirstRunDeletion=historical?.Subject.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None, DeletionEligible=historical!=null };
            var row=new Row(key,pc,db,saved,[],null,context){ObservationPath=path};
            if (saved is null && pc is {IsFolder:false} && db is null && debt==IssueSubject.FirstRunDeletionKind.Item && historical!.Subject.Items.Any(i=>i.Side==Side.Local && i.Id==pc.Id && PathNames.Same(i.Path,pc.Path)))
                row=row with {Deletion=await dropbox.DeletedAsync(endpoints.DropboxPath,false,ct),DeletionClock=dropbox.ClockOffset};
            IReadOnlyList<RenameCandidate> candidates=[];
            if (change.Kind==ChangeKind.Renamed && change.OldPath is {} old && row.EntryOn(change.Side) is {} moved)
            {
                var original=await store.FindAsync(pair.PairId,change.Side,PathNames.KeyFor(change.Side,old),ct);
                if (original!=null && original.IdOn(change.Side)==moved.Id) candidates=[new(change.Side,moved,original)];
            }
            bool ok=await ExecuteAsync(pair,settings,events,paths,row,candidates,change.LocalContentDirty,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,false);
            if (!ok) return;
            foreach(var n in notices)
            {
                // Only later same-item source input (not a comparison at its old name) settles PutBack.
                bool sameChange=change.Id is {Length:>0} id && (change.Observed!=null || change.IdentityDeleted) && n.Subject.Items.Any(i=>i.Side==change.Side && i.Id==id);
                if(n.Kind==NoticeKind.PutBack && sameChange || n.Kind==NoticeKind.Failure && Matches(n))
                    await store.CloseNoticeAsync(pair.PairId,n,ct);
            }
        }
        catch(ItemFailedException error) when(error.Disposition!=FailureDisposition.TemporaryNoEffect)
        {
            // A refused observation is not absence. Keep the binding and the exact acquired subject.
            var items=new List<NoticeItem>();
            if(saved!=null) { items.Add(new(Side.Local,saved.LocalPath,saved.LocalId)); items.Add(new(Side.Dropbox,saved.DropboxPath,saved.DropboxId)); }
            if(change.Id is {Length:>0} id) items.Add(new(change.Side,path,id));
            var subject=IssueSubject.FromDistinct(items,change.Side) with {FirstRunDeletion=debt};
            bool reproducible=saved is {IsFolder:false} && error.Disposition==FailureDisposition.Permanent && change.Id==saved.IdOn(change.Side) && change.OldPath==null && change.ConsumeHeld==null && heldInput?.Invoke(endpoints).Any(x=>x.ConsumeHeld!=null)!=true;
            await ReportFailureAsync(pair,store,events,error,subject,key,reproducible,failed,ct);
            if(!reproducible) throw;
            return;
        }

    }

    internal static bool Recursive(SyncAction action)
    {
        var row=action.Row;
        return (row.Local?.IsFolder==true || row.Dropbox?.IsFolder==true || row.Synced?.IsFolder==true) && action.Kind is (ActionKind.DeleteLocal or ActionKind.DeleteDropbox or ActionKind.ReplaceKind or ActionKind.DeferFolder or ActionKind.MoveLocal or ActionKind.MoveDropbox or ActionKind.MoveUnlinked or ActionKind.KeepBoth);
    }

    // A completed provider call can still have unsettled work.
    public static async Task RequireRemovalAsync(Task operation, Action? retain=null)
    {
        // shortcut: PREP still declares Task; narrow the parameter when it matches the folder owner's typed return.
        if(operation is Task<RemovalResult> typed) { if((await typed).Outcome==RemovalOutcome.Removed) return; }
        else await operation;
        retain?.Invoke();
        throw new InventoryIncompleteException(nameof(RemovalOutcome),null);
    }

    private static async Task ReportFailureAsync(PairConfig pair,IStore store,Action<RunEvent> events,ItemFailedException error,IssueSubject subject,string key,bool reproducible,Action<string,bool>? failed,CancellationToken ct)
    {
        subject=subject with {Holder=error.Holder,Move=error.Move};
        await new Notices().RaiseAsync(store,pair.PairId,new Notice(0,NoticeKind.Failure,error.Cause,error.Path,subject,DateTimeOffset.UtcNow,false),ct);
        events(new(RunEventKind.Failed,NoticeKind.Failure.ToString(),error.Path,error.Message,NoticeKind.Failure.ToString()){Cause=error.Cause});
        failed?.Invoke(key,reproducible);
    }

    private static bool Echo(Change change,SyncedEntry? saved)
    {
        if (change is not {Kind:ChangeKind.Changed,OldPath:null,IdentityDeleted:false,Observed:{Skip:null} e} || saved is null || e.Id.Length==0 || change.Id!=e.Id || e.Id!=saved.IdOn(change.Side) || change.Path!=e.Path || e.Path!=saved.PathOn(change.Side) || e.IsFolder!=saved.IsFolder) return false;
        if (change.Side==Side.Dropbox) return e.IsFolder || ContentHash.IsValid(e.ContentHash) && e.ContentHash==saved.DropboxContentHash;
        return change.Usn is {} usn && saved.LocalPublished is {} boundary && change.JournalId==boundary.JournalId && usn<=boundary.NextUsn && (e.IsFolder || e.Size==saved.LocalSize && e.Modified.UtcTicks==saved.LocalModifiedTicks);
    }

    private static async Task<string> CounterpartAsync(PairConfig pair,Side side,string path,IStore store,CancellationToken ct)
    {
        var other=PathNames.Other(side);
        foreach(var parent in PathNames.Ancestors(path))
            if (await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,parent),ct) is {} binding)
                return PathNames.Child(binding.PathOn(other),PathNames.SpellingOn(other,path[(parent.Length+1)..]));
        return PathNames.SpellingOn(other,path);
    }

    /// <summary>Keep acquisition context and decide a rename group as a unit. Population admits only additions.</summary>
    public static async Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Row? prepared, IReadOnlyList<RenameCandidate> bindingCandidates, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput=null, Action<string,bool>? failed=null, bool addOnly=false)
    {
        try { await ExecuteAsync(pair,settings,events,paths,prepared,bindingCandidates,false,local,dropbox,store,populate,compareOldBinding,ct,heldInput,failed,addOnly); }
        catch(ItemFailedException error) when(error.Disposition!=FailureDisposition.TemporaryNoEffect)
        {
            var items=new List<NoticeItem>();
            if(prepared?.Local is {} pc) items.Add(new(Side.Local,pc.Path,pc.Id));
            if(prepared?.Dropbox is {} db) items.Add(new(Side.Dropbox,db.Path,db.Id));
            foreach(var c in bindingCandidates) items.Add(new(c.Side,c.Moved.Path,c.Moved.Id));
            var subject=IssueSubject.FromDistinct(items,null) with {FirstRunDeletion=prepared?.Context.FirstRunDeletion??IssueSubject.FirstRunDeletionKind.None};
            bool reproducible=prepared?.Synced is {IsFolder:false} && bindingCandidates.Count==0 && items.Count>0 && error.Disposition==FailureDisposition.Permanent && heldInput?.Invoke(new(Row.Paths(prepared).Local,Row.Paths(prepared).Dropbox)).Any(c=>c.ConsumeHeld!=null)!=true;
            await ReportFailureAsync(pair,store,events,error,subject,prepared?.Key??PathNames.Key(error.Path),reproducible,failed,ct);
            if(!reproducible) throw;
        }
    }

    private static async Task<bool> ExecuteAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Row? row,IReadOnlyList<RenameCandidate> candidates,bool dirty,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding,CancellationToken ct,Func<Scope,IEnumerable<Change>>? heldInput,Action<string,bool>? failed,bool addOnly)
    {
        if (row is null && candidates.Count==0) throw new ArgumentException(nameof(row));
        if (row?.Local is {Skip:nameof(TextKey.SkipUnreadable)} unreadablePc) throw new ItemFailedException(unreadablePc.Path,TextKey.ReadLocalFailed,null);
        if (row?.Dropbox is {Skip:nameof(TextKey.SkipUnreadable)} unreadableCloud) throw new ItemFailedException(unreadableCloud.Path,TextKey.ReadDropboxFailed,null);
        if (candidates.Count==1 && row is null)
        {
            var c=candidates[0]; var other=PathNames.Other(c.Side); var destination=await CounterpartAsync(pair,c.Side,c.Moved.Path,store,ct);
            var peer=await (other==Side.Local?local:dropbox).ReadOneAsync(destination,ct);
            row=new(PathNames.Key(c.Moved.Path),c.Side==Side.Local?c.Moved:peer,c.Side==Side.Dropbox?c.Moved:peer,c.Synced,[],null,RowContext.Neutral with {Destination=destination});
        }
        bool sourceAbsent=false;
        if(candidates.Count==1)
        {
            var c=candidates[0]; var original=await (c.Side==Side.Local?local:dropbox).ReadOneAsync(c.Synced.PathOn(c.Side),ct);
            sourceAbsent=original?.Id!=c.Synced.IdOn(c.Side);
        }
        var now=DateTimeOffset.UtcNow;
        var action=candidates.Count!=0?Moves.For(candidates,row,sourceAbsent):Decide.For(row!,pair.FirstRun,now,dirty);
        row=action.Row;
        if(action.Kind==ActionKind.NeedLocalHash && row.Local is {} file)
        { row=row with {LocalHash=await local.ContentHashAsync(file,ct)}; action=Decide.For(row,pair.FirstRun,now,dirty); }
        if(action.Kind==ActionKind.NeedDeletionEvidence)
        { row=row with {Deletion=await dropbox.DeletedAsync(Row.Paths(row).Dropbox,row.IsFolder(),ct),DeletionClock=dropbox.ClockOffset}; action=Decide.For(row,pair.FirstRun,now,dirty); }
        if(action.Kind==ActionKind.DeferFolder && row.Synced is null)
        {
            var mark=row.Context.ProtectedContents;
            if(mark is null && row.Context.UnreadableCause is null) throw new InventoryIncompleteException(nameof(RowContext.ProtectedContents),null);
            action=FolderDeletes.For(row,mark==true,row.Context.UnreadableCause);
        }
        // An already-reviewed item with fresh history must finish or refresh its notice, never silently remain Held.
        if(action is {Kind:ActionKind.None,None:NoneReason.Held} && row.Deletion!=null && row.Context.FirstRunDeletion==IssueSubject.FirstRunDeletionKind.Item && !row.Context.Held)
            throw new ItemFailedException(Row.Path(row),TextKey.RetryRecoveryRequired,null);
        if(action.Kind==ActionKind.None) return action.None==NoneReason.InSync;
        if(addOnly && !(action.Kind==ActionKind.Record || action.Kind is (ActionKind.Upload or ActionKind.EnsureDropboxFolder) && row.Dropbox==null || action.Kind is (ActionKind.Download or ActionKind.EnsureLocalFolder) && row.Local==null)) return true;
        var endpoints=Row.Paths(row);
        using var lease=await Locks.AcquireAsync(pair,action,candidates,ct);
        var pcCheck=local.ReadOneAsync(endpoints.Local,ct); var dbCheck=dropbox.ReadOneAsync(endpoints.Dropbox,ct);
        await Task.WhenAll(pcCheck,dbCheck);
        if(await pcCheck!=row.Local || await dbCheck!=row.Dropbox) throw new ChangedUnderneathException(Row.Path(row));
        var currentBinding=await store.FindAsync(pair.PairId,Side.Local,row.Synced?.LocalKey??PathNames.Key(endpoints.Local),ct);
        if(currentBinding!=row.Synced) throw new ChangedUnderneathException(Row.Path(row));
        if(action.Kind==ActionKind.DeferFolder && row.Synced is {} folder)
        {
            if(heldInput is null) throw new InventoryIncompleteException(nameof(heldInput),null);
            await RequireRemovalAsync(new FolderDelete().ApplyAsync(pair,settings,events,paths,folder,row.Local==null?Side.Local:Side.Dropbox,local,dropbox,store,heldInput,populate,compareOldBinding,ct),()=>failed?.Invoke(row.Key,false));
            return true;
        }
        if(action.Kind is ActionKind.NeedLocalHash or ActionKind.NeedDeletionEvidence) throw new InventoryIncompleteException(action.Kind.ToString(),null);
        // Effects owns physical-leg logging and passes the original hop callback between legs.
        var outcome=await new Effects().ApplyAsync(pair,settings,events,paths,action,local,dropbox,store,populate,compareOldBinding,ct);
        if(outcome==EffectOutcome.Pending) throw new InventoryIncompleteException(nameof(EffectOutcome.Pending),null);
        if(outcome==EffectOutcome.Refused) { failed?.Invoke(row.Key,false); throw new ChangedUnderneathException(Row.Path(row)); }
        return true;

    }
}
