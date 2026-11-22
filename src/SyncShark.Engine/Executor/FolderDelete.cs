namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Remove the selected identity without giving away held child evidence.</summary>
public sealed class FolderDelete
{
    /// <summary>The binding overload receives the side where deletion was observed.</summary>
    public async Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncedEntry folder, Side deleting, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var target=deleting==Side.Local?dropbox:local; var source=deleting==Side.Local?local:dropbox;
        if(await source.ReadByIdAsync(folder.IdOn(deleting),ct) is {InScope:true} stillThere) return new(RemovalOutcome.Retained,stillThere.Location.Entry,null);
        foreach(var ancestor in PathNames.Ancestors(folder.PathOn(deleting)))
        {
            var parent=await store.FindAsync(pair.PairId,deleting,PathNames.KeyFor(deleting,ancestor),ct);
            if(parent is not {IsFolder:true} || paths.ExcludesBinding(parent)) break;
            if(await source.ReadByIdAsync(parent.IdOn(deleting),ct) is {InScope:true}) break;
            folder=parent;
        }
        var located=await target.ReadByIdAsync(folder.IdOn(target.Side),ct);
        if(located is null) return new(RemovalOutcome.NoEffect,null,null);
        var actual=located.Value.Location.Entry;
        if(!located.Value.InScope || !PathNames.Same(actual.Path,folder.PathOn(target.Side))) return new(RemovalOutcome.Retained,actual,null);
        return await ApplyAsync(pair,settings,events,paths,actual,folder,target.Side,target.Side==Side.Local?ActionKind.DeleteLocal:ActionKind.DeleteDropbox,local,dropbox,store,heldInput,populate,compareOldBinding,ct);
    }

    /// <summary>Returns the actual removal result. Dispatcher saves Pending; no binding is retired on acceptance.</summary>
    public async Task<RemovalResult> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Entry folder, SyncedEntry? binding, Side deleting, ActionKind origin, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct,bool splitSelected=false,Row? covered=null,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>>? resolveConflict=null)
    {
        var target=deleting==Side.Local?local:dropbox; var peer=deleting==Side.Local?dropbox:local;
        var scope=new Scope(binding?.LocalPath??PathNames.LocalSpelling(folder.Path),binding?.DropboxPath??folder.Path);
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
        var observedEdits=new List<Change>();
        // Acquire once before mutation: an unreadable late sibling vetoes the entire removal.
        if(deleting==Side.Local || protectedPaths.Overlaps(PathNames.Key(folder.Path)))
        {
            await foreach(var row in target.ReadAllAsync(folder.Path,new(true,true),ct))
            {
                if(row.Entry is not {} child || child.Skip==nameof(TextKey.SkipUnreadable))
                {
                    await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.ReadLocalFailed,row.Path,new([new(deleting,row.Path,row.Entry?.Id??string.Empty)],deleting),DateTimeOffset.UtcNow,false),ct);
                    return new(RemovalOutcome.Retained,folder,TextKey.ReadLocalFailed);
                }
                if(child.Skip==nameof(TextKey.SkipLink)) {await Protect(deleting,row.Path); continue;}
                if(deleting!=Side.Local || child.IsFolder || child.Skip!=null || protectedPaths.Covers(PathNames.Key(row.Path))) continue;
                var old=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(row.Path),ct);
                if(old?.LocalId==child.Id && (old.LocalSize!=child.Size || old.LocalModifiedTicks!=child.Modified.UtcTicks))
                {
                    var hash=await local.ContentHashAsync(child,ct);
                    if(hash!=old.DropboxContentHash) observedEdits.Add(new(deleting,ChangeKind.Changed,row.Path,null,child.Id,child with {ContentHash=hash},true,false));
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
                var location=await local.ReadByIdAsync(entry.Id,ct);
                if(location is not {InScope:true} match || match.Location.Entry!=entry || string.IsNullOrEmpty(match.Location.Domain)) return new(RemovalOutcome.Unknown,entry,TextKey.FailChanged);
                var volume=match.Location.Domain;
                if(pair.Roots!=null && pair.Roots.LocalVolumeKey()!=volume) return new(RemovalOutcome.Unknown,entry,TextKey.RootLocalIdentityChanged);
                selection=new(new(entry,volume,entry.IsFolder || entry.Skip==nameof(TextKey.SkipOnlineOnly)?null:await local.ContentHashAsync(entry,ct)),saved);
            }
            var splitRoot=splitSelected && entry==folder;
            var removal=partial || splitRoot?new RemovalResult(RemovalOutcome.Retained,entry,null):await target.DeleteAsync(entry,selection,ct);
            if(partial || splitRoot || removal is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.FailDropboxTooManyFiles} && entry.IsFolder && origin is (ActionKind.DeleteLocal or ActionKind.DeleteDropbox))
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
            if(removal.RecycledVictim is {} victim && victim.Entry.Id!=entry.Id)
            {
                var substitute=new Change(Side.Local,ChangeKind.Changed,entry.Path,null,victim.Entry.Id,victim.Entry with {Path=entry.Path},true,false);
                var subject=new Scope(entry.Path,saved?.DropboxPath??entry.Path);
                await RecoverHeldAsync(pair,subject,Side.Local,local,dropbox,store,_=>[substitute],populate,ct);
                return removal with {Outcome=RemovalOutcome.Unknown};
            }
            if(removal.Outcome!=RemovalOutcome.Removed) return removal;
            var suffix=entry.Path[folder.Path.Length..]; var footprint=new Scope(saved?.LocalPath??scope.LocalPath+suffix,saved?.DropboxPath??scope.DropboxPath+suffix);
            var recycledEdits=new List<Change>();
            if(removal.Recycled!=null)
            {
                await foreach(var observed in removal.Recycled.WithCancellation(ct))
                {
                    if(observed.Entry is not {Skip:null} file) continue;
                    var record=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(file.Path),ct);
                    if((record==null && origin is (ActionKind.MoveLocal or ActionKind.MoveDropbox) || record!=null && record.LocalId==file.Id && !file.IsFolder && file.ContentHash!=record.DropboxContentHash) && (file.IsFolder || ContentHash.IsValid(file.ContentHash)))
                        recycledEdits.Add(new(Side.Local,ChangeKind.Changed,file.Path,null,file.Id,file,true,false));
                }
            }
            if(!await RecoverHeldAsync(pair,footprint,deleting,local,dropbox,store,s=>observedEdits.Concat(heldInput(s)).Concat(recycledEdits),populate,ct,origin:origin)) return new(RemovalOutcome.Retained,null,TextKey.RetryRecoveryRequired);
            if(saved!=null) await compareOldBinding(saved,footprint,footprint,ct);
            return removal;
        }
    }

    internal static async Task<string> MapAsync(PairConfig pair,IStore store,Side side,string path,CancellationToken ct)
    {
        var other=PathNames.Other(side);
        foreach(var prefix in new[]{path}.Concat(PathNames.Ancestors(path)))
            if(await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,prefix),ct) is {} saved)
                return saved.PathOn(other)+PathNames.SpellingOn(other,path[prefix.Length..]);
        return PathNames.SpellingOn(other,path);
    }

    /// <summary>Retry only the exact held subject; an empty replay is not settlement.</summary>
    public static async Task<bool> RetryHeldAsync(PairConfig pair,Scope scope,Notice notice,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct)
    {
        if(notice.Kind!=NoticeKind.PutBack || notice.Subject.Incoming is not {} side) return false;
        var subject=notice.Subject.Items.FirstOrDefault(m=>m.Side==side && PathNames.Same(m.Path,notice.Path));
        if(subject==null || !PathNames.IsSelfOrUnder(subject.Path,side==Side.Local?scope.LocalPath:scope.DropboxPath) || await store.NoticeAsync(pair.PairId,notice.Id,ct) is not {} current || !current.Matches(notice)) return false;
        return await RecoverHeldAsync(pair,scope,side,local,dropbox,store,heldInput,populate,ct,notice);
    }

    /// <summary>Keep failed reports in the queue so Retry can reconstruct the latest required version.</summary>
    internal static async Task<bool> RecoverHeldAsync(PairConfig pair,Scope scope,Side side,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct,Notice? retry=null,ActionKind origin=ActionKind.None)
    {
        var source=side==Side.Local?local:dropbox; bool complete=true;
        var root=side==Side.Local?scope.LocalPath:scope.DropboxPath;
        var subject=retry?.Subject.Items.FirstOrDefault(i=>i.Side==side && PathNames.Same(i.Path,retry.Path));
        var latest=new Dictionary<(string Path,string Id), (long Order,Change Report)>();
        var pathDeletes=new Dictionary<string,long>(PathNames.Comparer);
        var acks=new List<((string Path,string Id) Key,Action Ack)>();long order=0;
        foreach(var c in heldInput(scope))
        {
            if(c.Side!=side || c.Path==null || !PathNames.IsSelfOrUnder(c.Path,root)) continue;
            var key=(PathNames.Key(c.Path),c.Id??c.Observed?.Id??string.Empty);
            if(subject!=null && (!PathNames.Same(subject.Path,c.Path) || key.Item2!=subject.Id && !(side==Side.Dropbox && c.Kind==ChangeKind.Deleted && key.Item2.Length==0 && latest.ContainsKey((key.Item1,subject.Id))))) continue;
            latest[key]=(++order,c);
            if(side==Side.Dropbox && key.Item2.Length==0 && c.Kind==ChangeKind.Deleted) pathDeletes[key.Item1]=order;
            if(c.ConsumeHeld!=null) acks.Add((key,c.ConsumeHeld));
        }
        if(retry!=null && latest.Count==0) return false;
        var settled=new HashSet<(string Path,string Id)>();
        foreach(var (key,candidate) in latest)
        {
            var last=candidate.Report;
            if(pathDeletes.GetValueOrDefault(key.Path)>candidate.Order) {settled.Add(key);continue;}
            if(origin==ActionKind.ReplaceKind && last.Kind!=ChangeKind.Deleted) {complete=false;continue;}
            if(last.Kind!=ChangeKind.Deleted)
            {
                if(last.Observed is not {} version) {complete=false;continue;}
                if(!PathNames.Same(last.Path,version.Path) || !PathNames.IsSelfOrUnder(version.Path,root)) {complete=false;continue;}
                var saved=await store.FindAsync(pair.PairId,side,PathNames.KeyFor(side,version.Path),ct);
                if(saved!=null && saved.IdOn(side)!=version.Id) {complete=false;continue;}
                Notice? owner=retry;
                if(owner==null) await foreach(var open in store.NoticesAtAsync(pair.PairId,PathNames.Key(version.Path),ct))
                    if(open.Kind==NoticeKind.PutBack && open.Subject.Incoming==side && open.Subject.Items.FirstOrDefault() is {} original && original.Id==version.Id && PathNames.Same(original.Path,version.Path)) owner=open;
                var items=owner?.Subject.Items.ToList()??[new(side,version.Path,version.Id)];
                var tail=version.Path[root.Length..]; var endpoints=new Scope(saved?.LocalPath??PathNames.LocalSpelling(scope.LocalPath+tail),saved?.DropboxPath??scope.DropboxPath+tail);
                try
                {
                    var located=await source.ReadByIdAsync(version.Id,ct);
                    if(located==null && side==Side.Local && items.All(i=>i.Side!=Side.Local || i.Id==version.Id)) throw new ChangedUnderneathException(version.Path);
                    var location=located?.Location;
                    if(location==null && side==Side.Dropbox && !version.IsFolder && !string.IsNullOrEmpty(version.Revision) && pair.Roots?.Dropbox.RootNamespaceId is {Length:>0} ns) location=new(ns,PathNames.RootedDropboxPath(pair.DropboxRoot,version.Path),version);

                    bool Keep(Side leg,string path)=>!pair.Exclusions.Any(ex=>!ex.Keep && ex.Side==leg && PathNames.IsSelfOrUnder(path,ex.Path));
                    if(version.IsFolder)
                    {
                        if(location==null || await source.RecoverAsync(location,version,ct) is not Recovery.Folder folder || folder.Root.Id!=version.Id) throw new ChangedUnderneathException(version.Path);
                        if(!Keep(Side.Local,endpoints.LocalPath) || !Keep(Side.Dropbox,endpoints.DropboxPath))
                        {
                            // A two-legged populate cannot implement a kept-leg-only recovery.
                            throw new ItemFailedException(version.Path,TextKey.RetryRecoveryRequired,null);
                        }
                        await populate(endpoints,side,folder,ct);
                    }
                    else
                    {
                        var a=Keep(Side.Local,endpoints.LocalPath)?await Restore(local,endpoints.LocalPath,null):null;
                        var b=Keep(Side.Dropbox,endpoints.DropboxPath)?await Restore(dropbox,endpoints.DropboxPath,a):null;
                        if(a==null && b==null) {complete=false;continue;}
                        if(a!=null && b!=null) await store.CommitAsync(pair.PairId,saved,SyncedEntries.From(a,b),null,null,ct);

                        await Notice(NoticeKind.Info,TextKey.InfoEditBeatsDelete);
                        if(retry==null && owner!=null) await store.CloseNoticeAsync(pair.PairId,owner,ct);
                    }
                    async Task<Entry> Restore(ISide leg,string path,Entry? verifiedLocal)
                    {
                        if(await leg.ReadOneAsync(path,ct) is {} present)
                        {
                            if(present.IsFolder || !(items.Any(m=>m.Side==leg.Side && m.Id==present.Id && PathNames.Same(m.Path,path)) || saved?.IdOn(leg.Side)==present.Id) || present.Size!=version.Size || !ContentHash.IsValid(version.ContentHash) || !StringComparer.OrdinalIgnoreCase.Equals(await leg.ContentHashAsync(present,ct),version.ContentHash)) throw new PathOccupiedException(path);
                            return present;
                        }
                        await using var bytes=verifiedLocal!=null?await local.OpenReadAsync(verifiedLocal,ct):location!=null && await source.RecoverAsync(location,version,ct) is Recovery.File file && file.Version==version?file.Bytes:throw new ChangedUnderneathException(path);
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
            if(!complete || await store.NoticeAsync(pair.PairId,retry.Id,ct) is not {} active || !active.Matches(retry)) return false;
            await store.CloseNoticeAsync(pair.PairId,retry,ct);
            if(await store.NoticeAsync(pair.PairId,retry.Id,ct) is {} remaining && remaining.Matches(retry)) return false;
        }
        foreach(var entry in acks) if(settled.Contains(entry.Key)) entry.Ack();
        return complete;
    }
}
