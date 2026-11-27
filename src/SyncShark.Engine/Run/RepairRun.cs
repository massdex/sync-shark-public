using System.Runtime.CompilerServices;
using System.Text.Json;
using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>One Repair run's state: its scan, debts, pending folders, link writes, admitted attempts and counters.</summary>
internal sealed class RepairRun
{
    private sealed record FolderMark(Row Row, string End)
    {
        internal List<(Scope Footprint,Notice Notice)>? Retiring;
        internal bool Content;
        internal TextKey? Unreadable;
    }

    internal readonly PairConfig pair; internal readonly EngineSettings settings; private readonly Action<RunEvent> events; internal readonly PathScope paths;
    internal readonly WorkQueue queue; internal readonly ISide local, dropbox; internal readonly IStore store; private readonly Scope? scope;
    internal readonly (SyncedEntry? Binding, Scope Before, Scope? After)? physicalHop; internal readonly CancellationToken ct; private readonly bool? caughtUp; private readonly Action? localRepaired; private readonly Action<RunEvent>? progress; // caughtUp null: AutoSync is still catching up and owns the journal and the unfinished-job visit
    internal readonly string id; internal readonly Guid scan; private readonly DateTimeOffset started; internal readonly bool removal; private readonly bool full, mapped;
    private readonly Scope selected, logical;
    private readonly PrefixSet unreadLocal=new(), unreadRemote=new();
    private readonly List<FolderMark> folders=new();
    private readonly List<(NoticeItem Member,bool Present)> linkWrites=new();
    private readonly HashSet<Exclusion> recreated=new(); // filled by the PC listing's one sequential enumerator
    private readonly List<Scope> skipped=new(); // unreadable folders, owed again after the finish
    internal readonly TaskCompletionSource discoveryEnded=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<Task> admitted=new();
    private readonly SemaphoreSlim slots; // discovery waits for the queue: at most one storage page of differences in memory
    private readonly Dictionary<string,int> effects=new(), issues=new();
    private (long Revision,string? Reason) localDebt, remoteDebt;
    private bool first, scanCleared, owedSelected; internal RunEvent? Completion; // written by Repair only after bookkeeping and cleanup return
    private Notice? pageWait; // this Repair's page-wait receipt (one Dropbox inventory listing at a time)
    private string? terminal;
    private Exception? admissionError;
    internal readonly RepairRetirement retirement;

    internal RepairRun(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, Scope? scope, (SyncedEntry? Binding, Scope Before, Scope? After)? physicalHop, CancellationToken ct,bool? caughtUp,Action? localRepaired,Action<RunEvent>? progress)
    {
        (this.pair,this.settings,this.events,this.paths,this.queue,this.local,this.dropbox,this.store)=(pair,settings,events,paths,queue,local,dropbox,store);
        (this.scope,this.physicalHop,this.ct,this.caughtUp,this.localRepaired,this.progress)=(scope,physicalHop,ct,caughtUp,localRepaired,progress);
        id=pair.PairId; scan=Guid.NewGuid(); started=DateTimeOffset.UtcNow;
        removal=physicalHop is {After:{} gone} cleanup && gone==cleanup.Before; // Equal scopes: confirmed-removal cleanup; the caller retires only the selected root.
        full=scope==null && physicalHop==null;
        selected=physicalHop is {} hop ? hop.After??hop.Before : scope??new Scope("","");
        logical=physicalHop is {Binding:{} owner,After:{} to} step && step.Before!=to ? new Scope(owner.LocalPath,owner.DropboxPath) : selected;
        mapped=logical!=selected;
        retirement=new(this);
        slots=new(settings.StoragePageSize);
    }

    internal bool ScanCleared => Volatile.Read(ref scanCleared);
    internal bool Isolated { get; private set; } // this acquisition isolated an undecodable Dropbox file, so it proves no removal

    internal async Task PrepareAsync()
    {
        localDebt=await store.ReadRepairAsync(id,Side.Local,ct);
        // A scope owed during a full run is served after it, so the full fence reads the revision net of owed-scope advances.
        remoteDebt=scope==null ? await store.ReadFullRepairAsync(id,ct) : await store.ReadRepairAsync(id,Side.Dropbox,ct);
        first=!(await store.RepairStateAsync(id,ct)).FirstRepairDone;
        // A page-wait notice open before this Repair started is a previous run's (one Repair per pair at a time, the Manual slot): close that exact row.
        if(physicalHop==null) { await foreach(var n in store.NoticesAtAsync(id,string.Empty,ct)) if(n is {Kind:NoticeKind.Failure,Cause:TextKey.RequiredRepairRetry}) { pageWait=n; break; } await PageWait(false); }
    }

    // The pair's one page-wait notice for this Repair's own listing; closed by its exact receipt. Never throws: a store or log fault is logged once and the wait goes on.
    private async Task PageWait(bool waiting)
    {
        try { if(waiting) pageWait=await store.SaveNoticeAsync(id,new(0,NoticeKind.Failure,TextKey.RequiredRepairRetry,string.Empty,new([],null),DateTimeOffset.UtcNow,false),CancellationToken.None);
              else if(pageWait is {} open) { pageWait=null; await new Notices().CloseAsync(store,id,open,CancellationToken.None); } }
        catch(Exception error) { try { Log(new(RunEventKind.Failed,nameof(IStore.SaveNoticeAsync),string.Empty,error.Message,string.Empty){Error=error}); } catch(Exception) { } }
    }

    private void Schedule(Task work)
    {
        lock(admitted) admitted.Add(work);
        _=Retire();
        async Task Retire()
        {
            try { await work; }
            catch(Exception error) {lock(admitted) admissionError??=error;}
            finally {lock(admitted) admitted.Remove(work);}
        }
    }
    private async Task Drain()
    {
        Task[] pending; lock(admitted) pending=admitted.ToArray();
        await Task.WhenAll(pending);
        lock(admitted) if(admissionError!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(admissionError).Throw();
    }
    internal void Log(RunEvent e)
    {
        events(e);
        if(e.Kind==RunEventKind.Effect && e.Action is {} a) lock(effects) effects[a.ToString()]=effects.GetValueOrDefault(a.ToString())+1;
        if(e.Kind==RunEventKind.Issue && e.Notice is {} n) lock(issues) issues[n.ToString()]=issues.GetValueOrDefault(n.ToString())+1;
    }
    internal Task Populate(Scope range,Side side,Recovery.Folder folder,CancellationToken token) => AutoSync.PopulateAsync(pair,settings,Log,paths,range,side,folder,local,dropbox,store,token,unread:Unread);
    internal Task Hop(SyncedEntry? binding,Scope before,Scope? after,CancellationToken token) => Repair.RunAsync(pair,settings,Log,paths,queue,local,dropbox,store,before,(binding,before,after),token);

    /// <summary>True when held work and a journal replay finished this run without a scan.</summary>
    internal async Task<bool> FastPathAsync()
    {
        if(physicalHop==null) await Repair.RootsUnchangedAsync(pair,local,dropbox,ct);
        if (physicalHop==null)
            await Repair.Staged(async()=>{await queue.RetryHeldAsync(scope,ct); if(caughtUp==false) await queue.Unfinished.CheckAsync(pair,settings,Log,paths,null,local,dropbox,store,queue.Held,Populate,Hop,ct,refused:AutoSync.JobRefused(settings,Log));},
                events,TextKey.StagePending,null);
        if(full && !first && caughtUp!=null && (localDebt.Reason is null) && (remoteDebt.Reason is null) &&
            pair.Roots?.LocalVolumeKey() is {} bound && await store.PositionAsync(bound,ct) is {} savedPosition && await store.CursorAsync(id,ct) is {Length:>0})
        {
            bool replayed=caughtUp==true;
            if(!replayed)
            {
                var pcEnd=local.BoundaryAsync(ct); var dbEnd=AutoSync.ReadPageAsync(dropbox.BoundaryAsync,Side.Dropbox,settings,events,ct); await Task.WhenAll(pcEnd,dbEnd);
                if((await pcEnd).Position is {} pcAt && pcAt.JournalId==savedPosition.JournalId && pcAt.NextUsn>=savedPosition.NextUsn && (await dbEnd).Cursor is {Length:>0} dbAt)
                {
                    await AutoSync.ReadThroughAsync(pair,settings,Log,paths,queue,local,dropbox,store,(pcAt,dbAt),static()=>{},ct);
                    replayed=true;
                }
            }
            if(replayed)
            {
                await retirement.ReconcileDirtyNotices();
                await Finish();
                return true;
            }
        }
        return false;
    }

    /// <summary>Acquire both inventories and publish any first-run deletion history before comparing them.</summary>
    internal async Task AcquireAsync()
    {
        if (full)
        {
            // The cancellable Repair waits out a temporary Dropbox refusal on its boundary with the source retry; other boundary callers stay single-attempt.
            var pc=local.BoundaryAsync(ct); var db=AutoSync.ReadPageAsync(dropbox.BoundaryAsync,Side.Dropbox,settings,events,ct); await Task.WhenAll(pc,db);
            if ((await pc).Position is {} position && pair.Roots?.LocalVolumeKey() is {} volume && await store.PositionAsync(volume,ct)==null)
                await local.SettleJournalAsync(position,ct);
            if ((await db).Cursor is {} cursor && await store.CursorAsync(id,ct)==null) await store.SaveCursorAsync(id,cursor,ct);
        }
        // A full run lists with deleted entries whenever the history is owed, also after first sync (a damaged history is re-acquired);
        // only first-sync eligibility needs the saved blocks restored into this scan.
        var acquireHistory=full && (first ? await store.DeletedHistoryAsync(id,scan,ct) : await store.DeletedHistoryAsync(id,ct)).Owed;
        await Task.WhenAll(store.AddScanAsync(id,scan,Side.Local,Repair.Counted(Repair.Observe(Inventory(local,selected.LocalPath,null,ct),selected.LocalPath,logical.LocalPath,unreadLocal,ct),physicalHop==null?events:null,progress,TextKey.StageLocalWalk,static item=>item.Path,ct),null,ct),
            store.AddScanAsync(id,scan,Side.Dropbox,Repair.Counted(Repair.Observe(Inventory(dropbox,selected.DropboxPath,acquireHistory?value=>terminal=value:null,ct),selected.DropboxPath,logical.DropboxPath,unreadRemote,ct,()=>Isolated=true),physicalHop==null?events:null,progress,TextKey.StageDropboxListing,static item=>item.Path,ct),
                acquireHistory?()=>terminal??throw new InvalidDataException(nameof(ListOptions.CursorAcquired)):null,ct));
        if(physicalHop==null) await Repair.RootsUnchangedAsync(pair,local,dropbox,ct); // no compare or history publish on a changed root
        if(acquireHistory) await store.PublishDeletedHistoryAsync(id,scan,ct);
    }

    /// <summary>Submit moves before compared rows. Keep row preparation inline to avoid another task per suspended row.</summary>
    internal async Task ScanAsync()
    {
        var moves=new List<RenameCandidate>();
        HashSet<(Side,string)>? movedNames=null;
        await foreach(var move in Repair.Counted(store.RenamesAsync(id,scan,physicalHop!=null?logical:scope,ct),physicalHop==null?events:null,progress,TextKey.StageRenames,static move=>move.Moved.Path,ct))
        {
            if(mapped)
            {
                // In the hop's old coordinates only independent renames remain. Neither end is an addition or deletion.
                (movedNames??=[]).Add((move.Side,move.Moved.Path));
                movedNames.Add((move.Side,move.Synced.PathOn(move.Side)));
                continue;
            }
            if (moves.Count!=0 && moves[0].Synced!=move.Synced) { await Submit(null,moves.ToArray()); moves.Clear(); }
            moves.Add(move);
        }
        if(moves.Count!=0) await Submit(null,moves);
        bool? canonical=null; // the hop's final mapping does not change within one pass
        async ValueTask<bool> Canonical() => canonical ??= await MissingPath(Side.Dropbox,selected.LocalPath)==selected.DropboxPath;
        await foreach(var (raw,notices) in Repair.Counted(store.CompareAsync(id,scan,physicalHop!=null?logical:scope,Evidence,ct),physicalHop==null?events:null,progress,TextKey.StageRows,static row=>Row.Path(row.Row),ct))
        {
            // Descendant listings omit the hop's own root; its caller owns that binding (Record, Relink or meeting).
            if(physicalHop is {} owner && raw.Local==null && raw.Dropbox==null && (owner.Binding is {} binding ? raw.Synced==binding : raw.Synced==null && PathNames.Same(Row.Path(raw),selected.DropboxPath))) continue;
            await FinishFolders(raw.Key);
            if(notices.Count==0 && !raw.Ambiguous && raw.Local is {Skip:null} pc && raw.Dropbox is {Skip:null} db && pc.IsFolder==db.IsFolder && raw.Synced is {} old && SyncedEntries.From(pc,db)==(old with {LocalPublished=null})) continue;
            // A child listed before its folder's listing failed proves nothing and takes no effect.
            if((raw.Local?.Path??raw.Synced?.LocalPath) is {} below && BelowUnread(unreadLocal,PathNames.Key(below)) ||
                (raw.Dropbox?.Path??raw.Synced?.DropboxPath) is {} beneath && BelowUnread(unreadRemote,PathNames.Key(beneath))) continue;
            if(mapped)
            {
                var left=raw.Local; var right=raw.Dropbox; var link=raw.Synced;
                if(movedNames!=null && ((left?.Path??link?.LocalPath) is {} lpMove && movedNames.Contains((Side.Local,lpMove)) ||
                    (right?.Path??link?.DropboxPath) is {} dpMove && movedNames.Contains((Side.Dropbox,dpMove)))) continue;
                // An unreadable mapped leaf takes no effect here; the owed scoped Repair reports this exact file.
                if(left?.Skip==nameof(TextKey.SkipUnreadable) || right?.Skip==nameof(TextKey.SkipUnreadable)) {if(await Canonical()) await OweSelectedAsync(); continue;} // a temporary case leg owes nothing; the final leg owes
                var held=notices.Any(x=>x.Notice.Kind is NoticeKind.Tie or NoticeKind.NameClash or NoticeKind.PutBack || x.Notice.Subject.Move!=null);
                var undecided=notices.Count!=0 || !raw.Context.CompleteInventory || link is {} b && (left!=null && left.Id!=b.LocalId || right!=null && right.Id!=b.DropboxId);
                if(held || raw.Ambiguous || left==null && right==null || left?.Skip!=null || right?.Skip!=null ||
                    (link==null ? undecided || left!=null && right!=null : link.IsFolder || left?.IsFolder==true || right?.IsFolder==true) || !await Canonical()) continue;
                var l=PathNames.RelativeUnderDropboxRoot(logical.LocalPath,left?.Path??link?.LocalPath??string.Empty);
                var d=PathNames.RelativeUnderDropboxRoot(logical.DropboxPath,right?.Path??link?.DropboxPath??string.Empty);
                if(link==null){l??=d;d??=l;}
                if(l==null || d==null) continue;
                var pcPath=PathNames.Child(selected.LocalPath,l); var dbPath=PathNames.Child(selected.DropboxPath,d); var current=queue.CurrentScope(paths);
                // An unlinked child under a recorded KEEP folder/anchor is covered by the logical name, even before the new scope is published (K01).
                if(link==null && (current.KeepCovers(Side.Local,PathNames.Key(PathNames.Child(logical.LocalPath,l))) ||
                    current.KeepCovers(Side.Dropbox,PathNames.DropboxKey(PathNames.Child(logical.DropboxPath,d))))) continue;
                if(current.Excludes(pcPath,dbPath,raw.IsFolder()) || link!=null && current.ExcludesBinding(link)) continue;
                if(undecided || Touched(raw,false)) { if((left==null)!=(right==null)) await OweSelectedAsync(); continue; } // the owed scoped Repair applies this child's own notices, holds and proof
                var actual=raw with {Local=left is null?null:left with {Path=pcPath},Dropbox=right is null?null:right with {Path=dbPath},
                    Context=raw.Context with {Destination=left is null?pcPath:dbPath}};
                var proof=queue.ProofNow?.Invoke();
                try { await RowExecution.ApplyAsync(pair,settings,Log,current,actual,[],local,dropbox,store,Populate,Hop,ct,queue.Held,(_,_)=>throw new WorkQueue.Unsettled(),coversThrough:proof); }
                catch(WorkQueue.Unsettled)
                {
                    await OweSelectedAsync();
                    if(proof!=null && link!=null && (left==null || right==null))
                    {
                        var item=actual.Local??actual.Dropbox!; var side=left==null?Side.Dropbox:Side.Local;
                        await new Notices().RaiseAsync(store,id,new(0,NoticeKind.Failure,TextKey.WaitItemRetry,item.Path,new([new(side,item.Path,item.Id)],side),DateTimeOffset.UtcNow,false),ct);
                    }
                }
                catch(ChangedUnderneathException race) when(!PathNames.Same(race.Path,pair.LocalRoot) && !PathNames.Same(race.Path,pair.DropboxRoot)) { await OweSelectedAsync(); } // a raced canonical row is the owed scope's to compare
                continue;
            }
            var row=raw;
            var parent=NearestPending(row.Key);
            if(parent!=null && row.Local is {} child)
            {
                if(child.Skip==nameof(TextKey.SkipUnreadable)) parent.Unreadable=TextKey.ReadLocalFailed;
                if(paths.CoversKey(Side.Local,row.Key) || child.Skip!=null || (!child.IsFolder && !paths.Excludes(child.Path,null,false))) parent.Content=true;
            }
            var unreadable=row.Local is {IsFolder:false,Skip:nameof(TextKey.SkipUnreadable)};
            var coveredChild=parent!=null && row.Local!=null && !unreadable && row.Dropbox==null && row.Synced==null; // its own notice; its folder stays blocked by it
            List<(Scope Footprint,Notice Notice)>? retiring=null;
            foreach(var (notice,member,covered,touched) in notices)
            {
                if(notice.Kind==NoticeKind.PutBack) continue;
                if(!covered || touched || member!=null && store.ScanTouched(id,scan,member.Side,member.Path,false)) continue;
                // A skipped folder's notice closes once the same folder is listed and its PC side is no longer skipped.
                if(notice.Cause==TextKey.SkipUnreadable && member!=null && row.Local?.Skip!=nameof(TextKey.SkipUnreadable) && row.EntryOn(member.Side) is {IsFolder:true,Skip:null} listed && listed.Id==member.Id) { await new Notices().CloseAsync(store,id,notice,ct); continue; }
                if(!coveredChild && await retirement.RetiringFootprint(raw,notice,member,ct) is {} footprint)
                {
                    if(raw.Local==null && raw.Dropbox==null && raw.Synced==null) await retirement.CloseSelected([(footprint,notice)],ct);
                    else (retiring??=[]).Add((footprint,notice));
                }
                if(notice.Cause==TextKey.InfoLinks && member!=null && !(row.EntryOn(member.Side) is {} current && current.Skip==nameof(TextKey.SkipLink) && current.Id==member.Id))
                    linkWrites.Add((member,false));
            }
            if(row.Local==null && row.Dropbox==null && row.Synced==null) continue;
            var lp=row.Local?.Path??row.Synced?.LocalPath??row.Context.Destination;
            var dp=row.Dropbox?.Path??row.Synced?.DropboxPath??row.Context.Destination;
            lp??=await MissingPath(Side.Local,dp!); dp??=await MissingPath(Side.Dropbox,lp);
            if(row.Local is {IsFolder:true,Skip:nameof(TextKey.SkipUnreadable)} || row.Dropbox is {IsFolder:true,Skip:nameof(TextKey.SkipUnreadable)}) skipped.Add(new(lp,dp));
            var coveredLocal=Evidence(Side.Local,lp).Covered; var coveredRemote=Evidence(Side.Dropbox,dp).Covered;
            if((row.Local==null && !coveredLocal) || (row.Dropbox==null && !coveredRemote)) continue;
            foreach(var side in new[]{Side.Local,Side.Dropbox})
                if(row.EntryOn(side) is {Skip:nameof(TextKey.SkipLink)} link && !store.ScanTouched(id,scan,side,link.Path,false) && !notices.Any(n=>n.Notice.Cause==TextKey.InfoLinks && n.Member==new NoticeItem(side,link.Path,link.Id)))
                    linkWrites.Add((new(side,link.Path,link.Id),true));
            if(row.Local?.Skip==nameof(TextKey.SkipLink) || row.Dropbox?.Skip==nameof(TextKey.SkipLink)) continue;
            var historical=notices.Where(n=>n.Notice.Subject.FirstRunDeletion!=IssueSubject.FirstRunDeletionKind.None && n.Notice.Subject.Items.Any(m=>row.EntryOn(m.Side)?.Id==m.Id)).Select(n=>n.Notice.Subject.FirstRunDeletion).FirstOrDefault();
            var eligible=first && row.Local is {} pcItem && row.Dropbox==null && row.Synced==null && coveredRemote && (pcItem.Skip==null || unreadable && parent==null) && !paths.Excludes(lp,dp,pcItem.IsFolder) && await store.DeletedNameAsync(id,scan,PathNames.DropboxKey(dp),pcItem.IsFolder,ct);
            var hold=notices.Select(n=>n.Notice).FirstOrDefault(n=>n.Kind==NoticeKind.Tie || n.Kind==NoticeKind.NameClash);
            // Coverage only: touch is decided at Attempt, which keeps the confirmed-removal exception for absent rows.
            // A first-run file that is eligible for deletion carries the Item marker into every own Failure notice; a folder never does.
            var debt=historical==IssueSubject.FirstRunDeletionKind.None && eligible && row.Local is {IsFolder:false} ? IssueSubject.FirstRunDeletionKind.Item : historical;
            row=row with {Context=row.Context with {FirstRun=first, CompleteInventory=coveredLocal&&coveredRemote, DeletionEligible=eligible,FirstRunDeletion=debt,
                Held=hold!=null,HeldKind=hold?.Kind,OutOfScope=paths.Excludes(lp,dp,row.IsFolder()),Skipped=row.Local?.Skip!=null || row.Dropbox?.Skip!=null,Destination=row.Local==null?lp:dp}};
            if (eligible && row.Local is {IsFolder:true})
            {
                folders.Add(new(row,PathNames.DescendantBounds(row.Key).High) {Retiring=retiring,Content=paths.HoldsNestedRoot(Side.Local,lp)||paths.HoldsNestedRoot(Side.Dropbox,dp)});
            }
            else if(!coveredChild) await Submit(row,[],retiring,coveredFolder:row.IsFolder() && row.Synced==null && coveredLocal && coveredRemote);
        }
        await FinishFolders(null);
        discoveryEnded.TrySetResult();
        await Repair.Staged(Drain,physicalHop==null?events:null,TextKey.StageExecution,effects);
    }
    private async IAsyncEnumerable<Listed> Inventory(ISide side,string path,Action<string>? cursor,[EnumeratorCancellation] CancellationToken token)
    {
        if(scope!=null && physicalHop==null && path.Length!=0)
        {
            // Descendant listings omit the scoped root and cannot distinguish a missing root from a failed listing.
            if(await side.ReadOneAsync(path,token) is not {} root) yield break;
            if(!root.IsFolder || root.Skip!=null) { yield return new(root.Path,root); yield break; }
            // A folder whose entry reads but whose listing is denied is skipped like any unreadable folder, not a pair-root failure.
            IAsyncEnumerator<Listed>? items=null; var next=false;
            try { items=Repair.ReadInventory(side.ReadAllAsync,path,Log,cursor,token,pageWait:side.Side==Side.Dropbox && physicalHop==null ? PageWait : null,isolate:physicalHop==null).GetAsyncEnumerator(token); next=await items.MoveNextAsync(); }
            catch(InventoryIncompleteException) when(side.Side==Side.Local) { root=root with {Skip=nameof(TextKey.SkipUnreadable)}; }
            try
            {
                yield return new(root.Path,root);
                for(;next;next=await items!.MoveNextAsync()) yield return items!.Current;
            }
            finally { if(items!=null) await items.DisposeAsync(); }
            yield break;
        }
        else if(removal || physicalHop is {Binding:null})
        {
            var root=await side.ReadOneAsync(path,token);
            if(physicalHop is {Binding:null}) ItemFailedException.RequireReadable(side.Side,root);
            if(root is null or {IsFolder:false,Skip:null}) yield break;
        }
        await foreach(var item in Repair.ReadInventory(side.ReadAllAsync,path,Log,cursor,token,full && side.Side==Side.Local ? Found : null,side.Side==Side.Dropbox && physicalHop==null ? PageWait : null,physicalHop==null)) yield return item;
    }

    internal async Task BookkeepingAsync()
    {
        // Apply scan bookkeeping after attempts so it cannot mask an outside change.
        foreach(var (member,present) in linkWrites) await store.UpdateLinkMemberAsync(id,member,present,ct);
        // A Repair skips an unreadable folder (its row raised its own notice and its subtree proved nothing); a hop's capture must be whole.
        if(physicalHop!=null && unreadLocal.Keys.Concat(unreadRemote.Keys).FirstOrDefault() is {} unreadPath) throw new InventoryIncompleteException(Text.Get(TextKey.SkipUnreadable,(TextArg.Path,unreadPath)),null);
        if(physicalHop!=null) return;
        await Finish();
    }
    internal async Task CleanupAsync()
    {
        discoveryEnded.TrySetCanceled(); try {await Drain();} finally {Volatile.Write(ref scanCleared,true);await store.ClearScanAsync(id,scan,ct);}
    }

    private async Task Finish()
    {
        if(full) await Notices.CheckKeepRulesAsync(pair,local,dropbox,store,ct);
        // A full run always completes; a side whose revision moved keeps its reason, so the whole-pair Repair still runs again for it.
        var matched=scope==null ? await store.FinishRepairAsync(id,(localDebt.Revision,remoteDebt.Revision),JsonSerializer.Serialize(new {
            RunId=scan,Started=started,Finished=DateTimeOffset.UtcNow,Completed=true,FirstRun=first,Effects=effects,IssuesByKind=issues }),ct)
            : (Local:true,Dropbox:await store.FinishRepairAsync(id,scope,remoteDebt.Revision,ct));
        foreach(var (side,label) in new[]{(matched.Local,TextKey.HumanSideLocal),(matched.Dropbox,TextKey.HumanSideDropbox)})
            if(!side) Log(new(RunEventKind.Wait,nameof(Repair),"",Text.Get(TextKey.RepairSuperseded,(TextArg.Side,Text.Get(label))),""));
        var completed=full || matched.Dropbox;
        if(completed) Completion=Repair.Completed(effects,started);
        // Owed only after the finish, so the next owed Repair retries each skipped folder; nothing wakes it now.
        if(completed) foreach(var s in skipped) await store.OweScopeAsync(id,s,ct);
        if(full)
        {
            if(matched.Local) localRepaired?.Invoke();
            // Owe each present no-keep root only after the finish, so its revision cannot supersede this run.
            foreach(var e in recreated) await store.OweScopeAsync(id,new Scope(e.Path,await ItemSync.CounterpartAsync(pair,Side.Local,e.Path,store,ct)),ct);
            if(recreated.Count!=0) queue.WakeOwedRepair?.Invoke(null);
        }
    }
    // The listing prunes at the topmost excluded name, so a present PC no-keep root arrives as exactly its rule's path.
    private void Found(string path)
    {
        foreach(var e in pair.Exclusions) if(!e.Keep && e.Side==Side.Local && PathNames.Same(e.Path,path)) recreated.Add(e);
    }
    internal (bool Covered,bool Touched) Evidence(Side side,string path)
    {
        var range=side==Side.Local?logical.LocalPath:logical.DropboxPath; var unread=side==Side.Local?unreadLocal:unreadRemote;
        // A pruned range is not evidence that a previously recorded child is absent.
        var key=PathNames.KeyFor(side,path);
        return ((range.Length==0 || PathNames.IsSelfOrUnder(path,range)) && !paths.CoversKey(side,key) && !unread.Covers(key),false);
    }
    private static bool BelowUnread(PrefixSet unread,string key) => unread.Keys.Any(folder=>PathNames.IsUnderKey(key,folder));
    internal bool Unread(Side side,string path) => (side==Side.Local?unreadLocal:unreadRemote).Covers(PathNames.KeyFor(side,path)); // the listing proved nothing at or under it
    private async Task OweSelectedAsync()
    { if(owedSelected) return; await store.OweScopeAsync(id,selected,ct); owedSelected=true; if(queue.ProofNow?.Invoke()!=null) queue.WakeOwedRepair?.Invoke(null); }
    private FolderMark? NearestPending(string key) => folders.FindLast(mark=>PathNames.IsUnderKey(key,mark.Row.Key));
    private async Task FinishFolders(string? next)
    {
        // A! sorts between A and A/; only the interval end proves that no child remains.
        while(folders.Count!=0 && (next==null || string.CompareOrdinal(next,folders[^1].End)>=0))
        {
            var mark=folders[^1]; folders.RemoveAt(folders.Count-1);
            if(NearestPending(mark.Row.Key) is {} outer)
            {
                outer.Content|=mark.Content; outer.Unreadable??=mark.Unreadable;
                continue;
            }
            await Submit(mark.Row with {Context=mark.Row.Context with {ProtectedContents=mark.Content,UnreadableCause=mark.Unreadable}},[],mark.Retiring,coveredFolder:true);
        }
    }
    internal bool Touched(Row row,bool descendants) =>
        (row.Local?.Path??row.Synced?.LocalPath??row.Context.Destination) is {} localPath && store.ScanTouched(id,scan,Side.Local,localPath,descendants) ||
        (row.Dropbox?.Path??row.Synced?.DropboxPath??row.Context.Destination) is {} remotePath && store.ScanTouched(id,scan,Side.Dropbox,remotePath,descendants);
    private Task<string> MissingPath(Side to,string path)
    {
        if(physicalHop==null && scope!=null)
        {
            var (source,target)=to==Side.Local?(scope.DropboxPath,scope.LocalPath):(scope.LocalPath,scope.DropboxPath);
            if(PathNames.Same(path,source)) return Task.FromResult(target);
            // Empty pair-wide scopes retain saved ancestor spellings.
            if(PathNames.RelativeUnderDropboxRoot(source,path) is {} suffix)
                return Task.FromResult(PathNames.Child(target,PathNames.SpellingOn(to,suffix)));
        }
        return ItemSync.CounterpartAsync(pair,PathNames.Other(to),path,store,ct);
    }
    private async Task Submit(Row? row,IReadOnlyList<RenameCandidate> candidates,List<(Scope Footprint,Notice Notice)>? retiring=null,bool coveredFolder=false)
    {
        var submission=new RepairSubmission(this,row,candidates,retiring,coveredFolder);
        if(physicalHop!=null) { await (removal && row is {Local:null,Dropbox:null,Synced:not null} ? submission.Settle() : submission.Apply(ct)); return; }
        await slots.WaitAsync(ct).ConfigureAwait(false);
        Schedule(AdmitAsync(submission,row,candidates));
    }
    // A row that still fails after its in-place retry is tried once more at the end of this Repair, then ends.
    private async Task AdmitAsync(RepairSubmission submission,Row? row,IReadOnlyList<RenameCandidate> candidates)
    {
        bool owesEnd;
        try { owesEnd=await TurnsAsync(submission,row,candidates).ConfigureAwait(false); }
        finally { slots.Release(); } // a failed row's notice is saved; waiting for its end turn must not stall discovery
        if(!owesEnd) return;
        try {await discoveryEnded.Task.WaitAsync(ct).ConfigureAwait(false);} catch(OperationCanceledException) {return;} // a failed or cancelled scan leaves the notice
        if(submission.Failed && await submission.EndAsync(ct).ConfigureAwait(false))
            await queue.AdmitRepairAsync(pair,row,candidates,submission.Apply,ct,bypassNoEffect:true).ConfigureAwait(false);
    }
    // The first turn and its in-place retry; true when the row still failed or was declined and owes the end turn.
    private async Task<bool> TurnsAsync(RepairSubmission submission,Row? row,IReadOnlyList<RenameCandidate> candidates)
    {
        if(!await queue.AdmitRepairAsync(pair,row,candidates,submission.Apply,ct).ConfigureAwait(false))
        {
            // A held or blocked path owns this difference; give it its own notice and never join the other work. Its end turn compares afresh.
            await submission.DeclinedAsync(ct).ConfigureAwait(false);
            return submission.Failed;
        }
        if(submission.OwesInPlace)
        {
            // A distinct turn: the first write no longer holds Started, so a waiting publication can commit first.
            submission.InPlace();
            if(!await queue.AdmitRepairAsync(pair,row,candidates,submission.Apply,ct).ConfigureAwait(false))
            {
                await submission.DeclinedAsync(ct).ConfigureAwait(false);
                return submission.Failed;
            }
        }
        return submission.Failed;
    }
}
