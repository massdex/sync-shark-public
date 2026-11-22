namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Remove the selected identity without giving away held child evidence.</summary>
public sealed class FolderDelete
{
    /// <summary>The binding overload receives the side where deletion was observed.</summary>
    public async Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncedEntry folder, Side deleting, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var target=deleting==Side.Local?dropbox:local;
        var located=await target.ReadByIdAsync(folder.IdOn(target.Side),ct);
        if(located is null) return new(RemovalOutcome.NoEffect,null,null);
        var actual=located.Value.Location.Entry;
        if(!located.Value.InScope || !PathNames.Same(actual.Path,folder.PathOn(target.Side))) return new(RemovalOutcome.Retained,actual,null);
        return await ApplyAsync(pair,settings,events,paths,actual,folder,target.Side,target.Side==Side.Local?ActionKind.DeleteLocal:ActionKind.DeleteDropbox,local,dropbox,store,heldInput,populate,compareOldBinding,ct);
    }

    /// <summary>Returns the actual removal result. Dispatcher saves Pending; no binding is retired on acceptance.</summary>
    public async Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Entry folder, SyncedEntry? binding, Side deleting, ActionKind origin, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var target=deleting==Side.Local?local:dropbox; var peer=deleting==Side.Local?dropbox:local;
        var scope=new Scope(binding?.LocalPath??PathNames.LocalSpelling(folder.Path),binding?.DropboxPath??folder.Path);
        var protectedPaths=new PrefixSet();
        string Map(Side side,string path)
        {
            string root=side==Side.Local?scope.LocalPath:scope.DropboxPath;
            if(side==deleting) return path;
            if(PathNames.IsSelfOrUnder(root,path)) return folder.Path;
            return PathNames.IsSelfOrUnder(path,root)?folder.Path+path[root.Length..]:path;
        }
        foreach(var ex in pair.Exclusions) if(paths.CoversKey(ex.Side,PathNames.KeyFor(ex.Side,ex.Path))) protectedPaths.Add(PathNames.Key(Map(ex.Side,ex.Path)));
        foreach(var side in new[]{Side.Local,Side.Dropbox}) foreach(var root in paths.NestedRoots(side)) protectedPaths.Add(PathNames.Key(Map(side,root)));
        await foreach(var n in store.NoticesUnderAsync(pair.PairId,scope,ct))
        {
            if(n.Cause is TextKey.ReadLocalFailed or TextKey.SkipUnreadable) return new(RemovalOutcome.Retained,folder,n.Cause);
            if(n.Cause==TextKey.InfoLinks || n.Subject.Move!=null || n.Kind==NoticeKind.PutBack)
                foreach(var item in n.Subject.Items) protectedPaths.Add(PathNames.Key(Map(item.Side,item.Path)));
        }
        if(binding!=null && await peer.ReadOneAsync(binding.PathOn(peer.Side),ct) is {IsFolder:true} reused && origin is ActionKind.DeleteLocal or ActionKind.DeleteDropbox)
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
        // Local may already have observed the edit before the journal page reached this removal.
        var changed=new List<Change>();
        if(deleting==Side.Local && binding!=null)
        {
            await foreach(var row in store.DescendantsAsync(pair.PairId,deleting,binding.LocalKey,ct))
            {
                if(protectedPaths.Covers(row.LocalKey)) continue;
                try
                {
                    var current=await local.ReadOneAsync(row.LocalPath,ct);
                    if(current?.Skip==nameof(TextKey.SkipUnreadable)) throw new ItemFailedException(row.LocalPath,TextKey.ReadLocalFailed,null);
                    if(current?.Skip==nameof(TextKey.SkipLink)) { protectedPaths.Add(row.LocalKey);continue; }
                    if(current is {IsFolder:false,Skip:null} && current.Id==row.LocalId && (current.Size!=row.LocalSize || current.Modified.UtcTicks!=row.LocalModifiedTicks))
                    {
                        var hash=await local.ContentHashAsync(current,ct);
                        if(hash!=row.DropboxContentHash) changed.Add(new(deleting,ChangeKind.Changed,current.Path,null,current.Id,current with {ContentHash=hash},true,false));
                    }
                }
                catch(ItemFailedException ex)
                {
                    await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,ex.Cause,row.LocalPath,new([new(deleting,row.LocalPath,row.LocalId)],deleting),DateTimeOffset.UtcNow,false),ct);
                    return new(RemovalOutcome.Retained,folder,ex.Cause);
                }
            }
        }
        return await Remove(folder,binding);

        async Task<RemovalResult> Remove(Entry entry,SyncedEntry? saved)
        {
            var key=PathNames.KeyFor(deleting,entry.Path); bool partial=protectedPaths.Overlaps(key);
            if(protectedPaths.Covers(key) || entry.Skip==nameof(TextKey.SkipLink) || partial && origin==ActionKind.ReplaceKind) return new(RemovalOutcome.Retained,entry,TextKey.FailReplacementRetained);
            RemovalSelection? selection=null;
            if(deleting==Side.Local)
            {
                var volume=pair.Roots?.LocalVolumeKey() ?? (await local.RootAsync(ct)).Local?.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if(volume==null) return new(RemovalOutcome.Unknown,entry,TextKey.RootLocalIdentityChanged);
                selection=new(new(entry,volume,entry.IsFolder || entry.Skip==nameof(TextKey.SkipOnlineOnly)?null:await local.ContentHashAsync(entry,ct)),saved);
            }
            var removal=partial?new RemovalResult(RemovalOutcome.Retained,entry,null):await target.DeleteAsync(entry,selection,ct);
            if(partial || removal is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.FailDropboxTooManyFiles} && entry.IsFolder && origin is (ActionKind.DeleteLocal or ActionKind.DeleteDropbox))
            {
                await foreach(var row in target.ReadAllAsync(entry.Path,new(false,true),ct))
                {
                    if(row.Entry is not {} child || child.Skip==nameof(TextKey.SkipUnreadable)) return new(RemovalOutcome.Retained,entry,TextKey.ReadLocalFailed);
                    var part=await Remove(child,await store.FindAsync(pair.PairId,deleting,PathNames.KeyFor(deleting,child.Path),ct));
                    if(part.Outcome!=RemovalOutcome.Removed && !(partial && part.Outcome==RemovalOutcome.Retained)) return part;
                }
                if(partial) return removal;
                removal=await target.DeleteAsync(entry,selection,ct);
            }
            if(removal.Outcome!=RemovalOutcome.Removed) return removal;
            var suffix=entry.Path[folder.Path.Length..]; var footprint=new Scope(saved?.LocalPath??scope.LocalPath+suffix,saved?.DropboxPath??scope.DropboxPath+suffix);
            if(!await RecoverHeldAsync(pair,footprint,deleting,local,dropbox,store,s=>changed.Concat(heldInput(s)),populate,ct)) return new(RemovalOutcome.Retained,null,TextKey.RetryRecoveryRequired);
            await foreach(var row in store.DescendantsAsync(pair.PairId,deleting,key,ct)) await Retire(row);
            if(saved!=null && saved.IdOn(deleting)==entry.Id) await Retire(saved);
            return removal;
        }
        async Task Retire(SyncedEntry row)
        {
            if(await target.ReadOneAsync(row.PathOn(deleting),ct)==null && await peer.ReadOneAsync(row.PathOn(peer.Side),ct)==null) await store.CommitAsync(pair.PairId,row,null,null,null,ct);
        }
    }

    /// <summary>Retry only the exact held subject; an empty replay is not settlement.</summary>
    public static async Task<bool> RetryHeldAsync(PairConfig pair,Scope scope,Notice notice,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct)
    {
        if(notice.Kind!=NoticeKind.PutBack || notice.Subject.Incoming is not {} side) return false;
        var subject=notice.Subject.Items.FirstOrDefault(m=>m.Side==side && PathNames.Same(m.Path,notice.Path));
        if(subject==null) return false;
        var reports=heldInput(scope).Where(c=>c.Side==side && PathNames.Same(c.Path,subject.Path) && (c.Id??c.Observed?.Id)==subject.Id).ToArray();
        return reports.Length!=0 && await RecoverHeldAsync(pair,scope,side,local,dropbox,store,_=>reports,populate,ct);
    }

    /// <summary>Keep failed reports in the queue so Retry can reconstruct the latest required version.</summary>
    internal static async Task<bool> RecoverHeldAsync(PairConfig pair,Scope scope,Side side,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct)
    {
        var source=side==Side.Local?local:dropbox; bool complete=true;
        var root=side==Side.Local?scope.LocalPath:scope.DropboxPath;
        foreach(var group in heldInput(scope).Where(c=>c.Side==side && c.Path!=null && PathNames.IsSelfOrUnder(c.Path,root)).GroupBy(c=>c.Path!,PathNames.Comparer))
        {
            var last=group.Last();
            if(last.Kind!=ChangeKind.Deleted)
            {
                if(last.Observed is not {} version) {complete=false;continue;}
                var saved=await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,version.Path),ct);
                var tail=version.Path[root.Length..]; var endpoints=new Scope(saved?.LocalPath??PathNames.LocalSpelling(scope.LocalPath+tail),saved?.DropboxPath??scope.DropboxPath+tail);
                try
                {
                    var located=await source.ReadByIdAsync(version.Id,ct);
                    if(located==null && side==Side.Local) throw new ChangedUnderneathException(version.Path);
                    var origin=located?.Location??new IdentityLocation(pair.ConnectionId,version.Path,version);
                    if(version.IsFolder)
                    {
                        if(await source.RecoverAsync(origin,version,ct) is not Recovery.Folder folder || folder.Root.Id!=version.Id) throw new ChangedUnderneathException(version.Path);
                        await populate(endpoints,side,folder,ct);
                    }
                    else
                    {
                        var a=await Restore(local,endpoints.LocalPath,null); var b=await Restore(dropbox,endpoints.DropboxPath,a);
                        await store.CommitAsync(pair.PairId,saved,SyncedEntries.From(a,b),null,null,ct);
                        await Notice(NoticeKind.Info,TextKey.InfoEditBeatsDelete);
                    }
                    async Task<Entry> Restore(ISide leg,string path,Entry? verifiedLocal)
                    {
                        if(await leg.ReadOneAsync(path,ct) is {} present)
                        {
                            if(present.IsFolder || leg.Side==side && present.Id!=version.Id || present.Size!=version.Size || !ContentHash.IsValid(version.ContentHash) || !StringComparer.OrdinalIgnoreCase.Equals(await leg.ContentHashAsync(present,ct),version.ContentHash)) throw new PathOccupiedException(path);
                            return present;
                        }
                        await using var bytes=verifiedLocal!=null?await local.OpenReadAsync(verifiedLocal,ct):await source.RecoverAsync(origin,version,ct) is Recovery.File file && file.Version==version?file.Bytes:throw new ChangedUnderneathException(path);
                        foreach(var parent in PathNames.Ancestors(path).Reverse().Where(p=>p.Length>0)) if((await leg.EnsureFolderAsync(parent,ct)).Outcome!=RemovalOutcome.Published) throw new ChangedUnderneathException(parent);
                        await using var stage=await leg.StageAsync(path,bytes,version,null,ct);
                        var published=await leg.PublishAsync(stage,null,null,ct);
                        return published.Outcome==RemovalOutcome.Published && published.Final is {} entry && entry.Size==version.Size && ContentHash.IsValid(version.ContentHash) && StringComparer.OrdinalIgnoreCase.Equals(await leg.ContentHashAsync(entry,ct),version.ContentHash)?entry:throw new ChangedUnderneathException(path);
                    }
                }
                catch(Exception ex) when(ex is ItemFailedException or ChangedUnderneathException or PathOccupiedException or IOException)
                {
                    await Notice(NoticeKind.PutBack,ex is ItemFailedException failure?failure.Cause:ex is PathOccupiedException?TextKey.ClashNameTaken:TextKey.FailChanged);
                    complete=false;continue;
                }
                Task Notice(NoticeKind kind,TextKey cause)=>store.SaveNoticeAsync(pair.PairId,new(0,kind,cause,version.Path,new([new(side,version.Path,version.Id)],side),DateTimeOffset.UtcNow,false),ct);
            }
            foreach(var report in group) report.ConsumeHeld?.Invoke();
        }
        return complete;
    }
}
