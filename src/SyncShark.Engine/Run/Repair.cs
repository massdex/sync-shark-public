using System.Runtime.CompilerServices;
using System.Text.Json;
using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Streams one two-sided acquisition. Only the revision it observed can be retired.</summary>
public static class Repair
{
    private sealed record FolderMark(Row Row, string End)
    {
        internal bool Content;
        internal TextKey? Unreadable;
    }

    public static async Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, Scope? scope, (SyncedEntry Binding, Scope Before, Scope After)? physicalHop, CancellationToken ct)
    {
        var id=pair.PairId; var scan=Guid.NewGuid(); var started=DateTimeOffset.UtcNow;
        if(physicalHop!=null)
        {
            var localEnd=local.BoundaryAsync(ct); var remoteEnd=dropbox.BoundaryAsync(ct);
            await Task.WhenAll(localEnd,remoteEnd);
            if(queue.CoversHop(((await localEnd).Position,(await remoteEnd).Cursor))) return;
        }
        var localDebt=await store.ReadRepairAsync(id,Side.Local,ct); var remoteDebt=await store.ReadRepairAsync(id,Side.Dropbox,ct);
        var first=!(await store.RepairStateAsync(id,ct)).FirstRepairDone;
        var full=scope==null && physicalHop==null;
        var acquireHistory=full && first && (await store.DeletedHistoryAsync(id,ct)).Owed;
        string? terminal=null;
        var selected=physicalHop?.After??scope??new Scope("","");
        var logical=physicalHop is {} step ? new Scope(step.Binding.LocalPath,step.Binding.DropboxPath) : selected;
        var mapped=logical!=selected;
        var unreadLocal=new PrefixSet(); var unreadRemote=new PrefixSet();
        var folders=new List<FolderMark>();
        var absent=new Dictionary<long,(Notice Expected,HashSet<NoticeItem> Members)>();
        var discoveryEnded=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted=new HashSet<Task>();
        Exception? admissionError=null;
        void Schedule(Task work)
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
        async Task Drain()
        {
            Task[] pending; lock(admitted) pending=admitted.ToArray();
            await Task.WhenAll(pending);
            lock(admitted) if(admissionError!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(admissionError).Throw();
        }
        var effects=new Dictionary<string,int>(); var issues=new Dictionary<string,int>();
        void Log(RunEvent e)
        {
            events(e);
            if(e.Kind==RunEventKind.Effect && e.Action is {} a) lock(effects) effects[a.ToString()]=effects.GetValueOrDefault(a.ToString())+1;
            if(e.Kind==RunEventKind.Issue && e.Notice is {} n) lock(issues) issues[n.ToString()]=issues.GetValueOrDefault(n.ToString())+1;
        }
        Task CheckJob(Change change,CancellationToken token) => queue.Unfinished.CheckAsync(pair,settings,Log,queue.CurrentScope(paths),change,local,dropbox,store,queue.Held,Populate,Hop,token);
        var retries=new Retries();
        Task Populate(Scope range,Side side,Recovery.Folder folder,CancellationToken token) => AutoSync.PopulateAsync(pair,settings,Log,paths,range,side,folder,local,dropbox,store,token);
        Task Hop(SyncedEntry binding,Scope before,Scope after,CancellationToken token) => RunAsync(pair,settings,Log,paths,queue,local,dropbox,store,before,(binding,before,after),token);
        if (physicalHop==null)
        {
            await queue.RetryHeldAsync(scope,ct);
            await queue.Unfinished.CheckAsync(pair,settings,Log,paths,null,local,dropbox,store,queue.Held,Populate,Hop,ct);
        }
        if(full && !first && (localDebt.Reason is null or "requested") && (remoteDebt.Reason is null or "requested") &&
            pair.Roots?.LocalVolumeKey() is {} bound && await store.PositionAsync(bound,ct) is {} savedPosition && await store.CursorAsync(id,ct) is {Length:>0})
        {
            var pcEnd=local.BoundaryAsync(ct); var dbEnd=dropbox.BoundaryAsync(ct); await Task.WhenAll(pcEnd,dbEnd);
            if((await pcEnd).Position is {} pcAt && pcAt.JournalId==savedPosition.JournalId && pcAt.NextUsn>=savedPosition.NextUsn && (await dbEnd).Cursor is {Length:>0} dbAt)
            {
                await AutoSync.ReadThroughAsync(pair,settings,Log,paths,queue,local,dropbox,store,(pcAt,dbAt),static()=>{},ct);
                await ReconcileDirtyNotices();
                await Finish();
                return;
            }
        }
        store.BeginScan(id,scan);
        try
        {
            if (full)
            {
                var pc=local.BoundaryAsync(ct); var db=dropbox.BoundaryAsync(ct); await Task.WhenAll(pc,db);
                if ((await pc).Position is {} position && pair.Roots?.LocalVolumeKey() is {} volume && await store.PositionAsync(volume,ct)==null)
                    await local.SettleJournalAsync(position,ct);
                if ((await db).Cursor is {} cursor && await store.CursorAsync(id,ct)==null) await store.SaveCursorAsync(id,cursor,ct);
            }
            await Task.WhenAll(store.AddScanAsync(id,scan,Side.Local,Observe(ReadInventory(local.ReadAllAsync,selected.LocalPath,Log,null,ct),selected.LocalPath,logical.LocalPath,unreadLocal,ct),null,ct),
                store.AddScanAsync(id,scan,Side.Dropbox,Observe(ReadInventory(dropbox.ReadAllAsync,selected.DropboxPath,Log,acquireHistory?value=>terminal=value:null,ct),selected.DropboxPath,logical.DropboxPath,unreadRemote,ct),
                    acquireHistory?()=>terminal??throw new InvalidDataException(nameof(ListOptions.CursorAcquired)):null,ct));
            if(acquireHistory) await store.PublishDeletedHistoryAsync(id,scan,ct);
            var moves=new List<RenameCandidate>();
            await foreach(var move in store.RenamesAsync(id,scan,physicalHop!=null?logical:scope,ct))
            {
                if(mapped) throw new WorkQueue.Unsettled();
                if (moves.Count!=0 && moves[0].Synced!=move.Synced) { await Submit(null,moves.ToArray()); moves.Clear(); }
                moves.Add(move);
            }
            if(moves.Count!=0) await Submit(null,moves);
            await foreach(var (raw,notices) in store.CompareAsync(id,scan,physicalHop!=null?logical:scope,Evidence,ct))
            {
                await FinishFolders(raw.Key);
                if(notices.Count==0 && !raw.Ambiguous && raw.Local is {Skip:null} pc && raw.Dropbox is {Skip:null} db && pc.IsFolder==db.IsFolder && raw.Synced is {} old && SyncedEntries.From(pc,db)==(old with {LocalPublished=null})) continue;
                if(mapped)
                {
                    // Retry this input at its logical paths; its old name may already belong to another file.
                    throw new WorkQueue.Unsettled();
                }
                var row=raw;
                var parent=NearestPending(row.Key);
                if(parent!=null && row.Local is {} child)
                {
                    if(child.Skip==nameof(TextKey.SkipUnreadable)) parent.Unreadable=TextKey.ReadLocalFailed;
                    if(paths.CoversKey(Side.Local,row.Key) || child.Skip!=null || (!child.IsFolder && !paths.Excludes(child.Path,null,false))) parent.Content=true;
                }
                foreach(var (notice,member,covered,touched) in notices)
                {
                    if(!covered || touched || notice.Kind==NoticeKind.PutBack || member!=null && store.ScanTouched(id,scan,member.Side,member.Path,false)) continue;
                    if(raw.Local==null && raw.Dropbox==null && !raw.CounterpartElsewhere && !raw.Ambiguous && raw.Context.CompleteInventory && Retirable(notice))
                    {
                        if(!absent.TryGetValue(notice.Id,out var found)) absent.Add(notice.Id,found=(notice,[]));
                        if(member!=null && found.Expected.Matches(notice)) found.Members.Add(member);
                    }
                    if(notice.Cause==TextKey.InfoLinks && member!=null && !(row.EntryOn(member.Side) is {} current && current.Skip==nameof(TextKey.SkipLink) && current.Id==member.Id))
                        await store.UpdateLinkMemberAsync(id,member,false,ct);
                }
                if(row.Local==null && row.Dropbox==null && row.Synced==null) continue;
                var lp=row.Local?.Path??row.Synced?.LocalPath??row.Context.Destination;
                var dp=row.Dropbox?.Path??row.Synced?.DropboxPath??row.Context.Destination;
                lp??=await MissingPath(Side.Local,dp!); dp??=await MissingPath(Side.Dropbox,lp);
                var coveredLocal=Evidence(Side.Local,lp).Covered; var coveredRemote=Evidence(Side.Dropbox,dp).Covered;
                if((row.Local==null && !coveredLocal) || (row.Dropbox==null && !coveredRemote)) continue;
                foreach(var side in new[]{Side.Local,Side.Dropbox})
                    if(row.EntryOn(side) is {Skip:nameof(TextKey.SkipLink)} link && !store.ScanTouched(id,scan,side,link.Path,false) && !notices.Any(n=>n.Notice.Cause==TextKey.InfoLinks && n.Member==new NoticeItem(side,link.Path,link.Id)))
                        await store.UpdateLinkMemberAsync(id,new(side,link.Path,link.Id),true,ct);
                if(row.Local?.Skip==nameof(TextKey.SkipLink) || row.Dropbox?.Skip==nameof(TextKey.SkipLink)) continue;
                var historical=notices.Where(n=>n.Notice.Subject.FirstRunDeletion!=IssueSubject.FirstRunDeletionKind.None && n.Notice.Subject.Items.Any(m=>row.EntryOn(m.Side)?.Id==m.Id)).Select(n=>n.Notice.Subject.FirstRunDeletion).FirstOrDefault();
                var eligible=first && row.Local is {} pcItem && row.Dropbox==null && row.Synced==null && coveredRemote && pcItem.Skip==null && !paths.Excludes(lp,dp,pcItem.IsFolder) && await store.DeletedNameAsync(id,scan,PathNames.DropboxKey(dp),pcItem.IsFolder,ct);
                var hold=notices.Select(n=>n.Notice).FirstOrDefault(n=>n.Kind==NoticeKind.Tie || n.Kind==NoticeKind.NameClash);
                row=row with {Context=row.Context with {FirstRun=first, CompleteInventory=coveredLocal&&coveredRemote, DeletionEligible=eligible,FirstRunDeletion=historical,
                    Held=hold!=null,HeldKind=hold?.Kind,OutOfScope=paths.Excludes(lp,dp,row.IsFolder()),Skipped=row.Local?.Skip!=null || row.Dropbox?.Skip!=null,Destination=row.Local==null?lp:dp}};
                if (eligible && row.Local is {IsFolder:true})
                {
                    folders.Add(new(row,PathNames.DescendantBounds(row.Key).High) {Content=paths.HoldsNestedRoot(Side.Local,lp)||paths.HoldsNestedRoot(Side.Dropbox,dp)});
                }
                else if(!(parent!=null && row.Local!=null && row.Dropbox==null && row.Synced==null)) await Submit(row,[],coveredFolder:row.IsFolder() && row.Synced==null && coveredLocal && coveredRemote);
            }
            await FinishFolders(null);
            discoveryEnded.TrySetResult();
            await Drain();
            foreach(var (expected,members) in absent.Values)
                if(members.SetEquals(expected.Subject.Items)) await CloseIfAbsent(expected,true);
            if(unreadLocal.Keys.Concat(unreadRemote.Keys).FirstOrDefault() is {} unreadPath) throw new InventoryIncompleteException(Text.Get(TextKey.SkipUnreadable,(TextArg.Path,unreadPath)),null);
            if(physicalHop!=null) return;
            await Finish();
        }
        finally { discoveryEnded.TrySetCanceled(); try {await Drain();} finally {await store.ClearScanAsync(id,scan,CancellationToken.None);} }

        async Task ReconcileDirtyNotices()
        {
            await foreach(var notice in store.NoticesAsync(id,ct))
                await CloseIfAbsent(notice,false);
        }
        async Task<Scope> Footprint(Side side,string path,CancellationToken token)
        {
            var binding=await store.FindAsync(id,side,PathNames.KeyFor(side,path),token);
            var peer=binding?.PathOn(PathNames.Other(side))??await ItemSync.CounterpartAsync(pair,side,path,store,token);
            return side==Side.Local ? new Scope(path,peer) : new Scope(peer,path);
        }
        async Task CloseIfAbsent(Notice notice,bool acquired)
        {
            if(notice.Ignored || notice.Dismissed || !Retirable(notice)) return;
            try
            {
                var ranges=new HashSet<Scope>();
                if(notice.Subject.Items.Count==0) ranges.Add(await Footprint(notice.Subject.Incoming??Side.Local,notice.Path,ct));
                else foreach(var member in notice.Subject.Items) ranges.Add(await Footprint(member.Side,member.Path,ct));
                foreach(var f in ranges)
                {
                    if(!CanClose(f)) return;
                    // The scan's notice row may have no binding to join its mapped counterpart.
                    var pc=local.ReadOneAsync(f.LocalPath,ct); var db=dropbox.ReadOneAsync(f.DropboxPath,ct);
                    await Task.WhenAll(pc,db); if(await pc!=null || await db!=null) return;
                }
                if(ranges.All(CanClose)) await new Notices().CloseAsync(store,id,notice,ct);
            }
            catch(Exception e) when(e is ItemFailedException or IOException || e is ChangedUnderneathException race && !PathNames.Same(race.Path,pair.LocalRoot) && !PathNames.Same(race.Path,pair.DropboxRoot))
            { Log(new(RunEventKind.Failed,nameof(ISide.ReadOneAsync),notice.Path,e.Message,nameof(NoticeKind.Failure)){Error=e}); }
            bool CanClose(Scope f) => !queue.Unfinished.CoversOpenJob(f) && !queue.Held(f).Any() && !queue.CurrentScope(paths).Excludes(f.LocalPath,f.DropboxPath,true) &&
                (!acquired || Evidence(Side.Local,f.LocalPath).Covered && Evidence(Side.Dropbox,f.DropboxPath).Covered &&
                !store.ScanTouched(id,scan,Side.Local,f.LocalPath,false) && !store.ScanTouched(id,scan,Side.Dropbox,f.DropboxPath,false));
        }
        async Task Finish()
        {
            bool completed=scope==null ? await store.FinishRepairAsync(id,(localDebt.Revision,remoteDebt.Revision),JsonSerializer.Serialize(new {
                RunId=scan,Started=started,Finished=DateTimeOffset.UtcNow,Completed=true,FirstRun=first,Effects=effects,IssuesByKind=issues }),ct)
                : await store.FinishRepairAsync(id,scope,remoteDebt.Revision,ct);
            if(!completed) Log(new(RunEventKind.Wait,nameof(Repair),"",Text.Get(TextKey.RepairSuperseded),""));
        }
        async Task RetryOwn(Notice original,CancellationToken token)
        {
            var notice=await CurrentRetryAsync(store,id,original,token); if(notice==null) return;
            var remote=notice.Subject.Items.FirstOrDefault(m=>m.Side==Side.Dropbox);
            await CheckJob(new Change(Side.Dropbox,ChangeKind.Changed,remote?.Path??notice.Path,null,remote?.Id,null,false,false),token);
            if(notice.Subject.Items.Count==0 && queue.Unfinished.CoversOpenJob(await Footprint(notice.Subject.Incoming??Side.Local,notice.Path,token))) return;
            foreach(var member in notice.Subject.Items)
                if(queue.Unfinished.CoversOpenJob(await Footprint(member.Side,member.Path,token))) return;
            await retries.RetryAsync(pair,settings,Log,queue.CurrentScope(paths),notice,local,dropbox,store,queue.Held,Populate,Hop,token);
        }
        (bool Covered,bool Touched) Evidence(Side side,string path)
        {
            var range=side==Side.Local?logical.LocalPath:logical.DropboxPath; var unread=side==Side.Local?unreadLocal:unreadRemote;
            // A pruned range is not evidence that a previously recorded child is absent.
            var key=PathNames.KeyFor(side,path);
            return ((range.Length==0 || PathNames.IsSelfOrUnder(path,range)) && !paths.CoversKey(side,key) && !unread.Covers(key),false);
        }
        FolderMark? NearestPending(string key) => folders.FindLast(mark=>PathNames.IsUnderKey(key,mark.Row.Key));
        async Task FinishFolders(string? next)
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
                await Submit(mark.Row with {Context=mark.Row.Context with {ProtectedContents=mark.Content,UnreadableCause=mark.Unreadable}},[],coveredFolder:true);
            }
        }
        bool Touched(Row row,bool descendants) =>
            (row.Local?.Path??row.Synced?.LocalPath??row.Context.Destination) is {} localPath && store.ScanTouched(id,scan,Side.Local,localPath,descendants) ||
            (row.Dropbox?.Path??row.Synced?.DropboxPath??row.Context.Destination) is {} remotePath && store.ScanTouched(id,scan,Side.Dropbox,remotePath,descendants);
        Task<string> MissingPath(Side to,string path) => ItemSync.CounterpartAsync(pair,PathNames.Other(to),path,store,ct);
        async Task PopulateCovered(Scope range,Side source,Recovery.Folder folder,CancellationToken token)
        {
            var sourceRoot=source==Side.Local?range.LocalPath:range.DropboxPath;
            var targetRoot=source==Side.Local?range.DropboxPath:range.LocalPath;
            await foreach(var (child,_) in store.CompareAsync(id,scan,range,Evidence,token))
            {
                if(child.EntryOn(source) is not {Skip:null} entry || PathNames.RelativeUnderDropboxRoot(sourceRoot,entry.Path) is not {} suffix ||
                    child.EntryOn(PathNames.Other(source))!=null || Touched(child,false)) continue;
                var destination=PathNames.Child(targetRoot,suffix); var current=queue.CurrentScope(paths);
                if(current.Excludes(source==Side.Local?entry.Path:destination,source==Side.Dropbox?entry.Path:destination,entry.IsFolder)) continue;
                await ItemSync.ApplyAsync(pair,settings,Log,current,child with {Context=child.Context with {Destination=destination}},[],local,dropbox,store,
                    (_,_,_,_)=>Task.CompletedTask,Hop,token,queue.Held,addOnly:true);
            }
        }
        Task Submit(Row? row,IReadOnlyList<RenameCandidate> candidates,bool coveredFolder=false)
        {
            bool endAttempted=false;
            async Task Apply(CancellationToken token)
            {
                if(row!=null && Touched(row,row.IsFolder()) || candidates.Any(c=>store.ScanTouched(id,scan,c.Side,c.Moved.Path,c.Moved.IsFolder))) return;
                var current=queue.CurrentScope(paths); var prepared=row;
                if(prepared!=null)
                {
                    var (lp,dp)=Row.Paths(prepared);
                    prepared=prepared with {Context=prepared.Context with {OutOfScope=current.Excludes(lp,dp,prepared.IsFolder())}};
                }
                Notice? own=null;
                try { await ItemSync.ApplyAsync(pair,settings,Log,current,prepared,candidates,local,dropbox,store,coveredFolder?PopulateCovered:Populate,Hop,token,queue.Held,
                    (_,reproducible)=>{if(!reproducible) throw new WorkQueue.Unsettled();},failedNotice:n=>own=n); }
                catch(WorkQueue.Unsettled) when(own!=null && !endAttempted && physicalHop==null)
                {
                    // The queue re-attempts this exact callback after it catches Unsettled. Wait for discovery, not release.
                    await discoveryEnded.Task.WaitAsync(token); endAttempted=true; throw;
                }
                if(own!=null && !endAttempted && physicalHop==null)
                {
                    await discoveryEnded.Task.WaitAsync(token); endAttempted=true;
                    await RetryOwn(own,token);
                }
            }
            if(physicalHop!=null) return Apply(ct);
            Schedule(queue.AdmitAsync(pair,row,candidates,Apply,ct));
            return Task.CompletedTask;
        }
    }

    private static bool Retirable(Notice notice)=>notice.Kind is (NoticeKind.Failure or NoticeKind.Tie or NoticeKind.NameClash) &&
        notice.Subject.Move==null && notice.Cause!=TextKey.FailDropboxDeleteJobInvalid;

    /// <summary>Resolve the assigned id of this attempt, never retry a replacement at its path.</summary>
    public static async Task<Notice?> CurrentRetryAsync(IStore store,string pair,Notice original,CancellationToken ct)
    {
        if(original.Id!=0) return await store.NoticeAsync(pair,original.Id,ct) is {} current && Matches(current) ? current : null;
        await foreach(var candidate in store.NoticesAtAsync(pair,PathNames.Key(original.Path),ct)) if(Matches(candidate)) return candidate;
        return null;
        bool Matches(Notice current)=>!current.Ignored && !current.Dismissed && current.Matches(original with {Id=current.Id});
    }

    /// <summary>Read one Repair inventory range, requesting deletion history only on its dedicated acquisition.</summary>
    public static IAsyncEnumerable<Listed> ReadInventory(Func<string,ListOptions,CancellationToken,IAsyncEnumerable<Listed>> read, string under, Action<RunEvent> events, Action<string>? historyCursor, CancellationToken ct) =>
        read(under,new(Recursive:true,IncludeOutOfScope:false,IncludeDeleted:historyCursor!=null){Events=events,CursorAcquired=historyCursor},ct);

    private static async IAsyncEnumerable<Listed> Observe(IAsyncEnumerable<Listed> rows,string physical,string logical,PrefixSet unread,[EnumeratorCancellation] CancellationToken ct)
    {
        await foreach(var item in rows.WithCancellation(ct))
        {
            var observed=item;
            if(physical!=logical)
            {
                var suffix=physical.Length==0 ? item.Path : PathNames.RelativeUnderDropboxRoot(physical,item.Path) ?? throw new ChangedUnderneathException(item.Path);
                string path=PathNames.Child(logical,suffix);
                observed=new(path,item.Entry is {} entry ? entry with {Path=path} : null);
            }
            if(observed.Entry?.Skip==nameof(TextKey.SkipUnreadable)) unread.Add(PathNames.Key(observed.Path));
            yield return observed;
        }
    }
}
