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
        var failed=new HashSet<string>(PathNames.Comparer);
        var effects=new Dictionary<string,int>(); var issues=new Dictionary<string,int>();
        void Log(RunEvent e)
        {
            events(e);
            if(e.Kind==RunEventKind.Effect && e.Action is {} a) lock(effects) effects[a.ToString()]=effects.GetValueOrDefault(a.ToString())+1;
            if(e.Kind==RunEventKind.Issue && e.Notice is {} n) lock(issues) issues[n.ToString()]=issues.GetValueOrDefault(n.ToString())+1;
        }
        Task Populate(Scope range,Side side,Recovery.Folder folder,CancellationToken token) => AutoSync.PopulateAsync(pair,settings,Log,paths,range,side,folder,local,dropbox,store,token);
        Task Hop(SyncedEntry binding,Scope before,Scope after,CancellationToken token) => RunAsync(pair,settings,Log,paths,queue,local,dropbox,store,before,(binding,before,after),token);
        if (physicalHop==null) await queue.Unfinished.CheckAsync(pair,settings,Log,paths,null,local,dropbox,store,queue.Held,Populate,Hop,ct);
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
            var options=new ListOptions(true,true){Events=Log};
            var dbOptions=options with {IncludeDeleted=acquireHistory,CursorAcquired=acquireHistory?value=>terminal=value:null};
            await Task.WhenAll(store.AddScanAsync(id,scan,Side.Local,Observe(local.ReadAllAsync(selected.LocalPath,options,ct),selected.LocalPath,logical.LocalPath,unreadLocal,ct),null,ct),
                store.AddScanAsync(id,scan,Side.Dropbox,Observe(dropbox.ReadAllAsync(selected.DropboxPath,dbOptions,ct),selected.DropboxPath,logical.DropboxPath,unreadRemote,ct),
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
                if(mapped)
                {
                    if(raw.Local!=null && raw.Dropbox!=null && raw.Synced is {} old && SyncedEntries.From(raw.Local,raw.Dropbox)==(old with {LocalPublished=null}) && notices.Count==0) continue;
                    // Retry this input at its logical paths; its old name may already belong to another file.
                    throw new WorkQueue.Unsettled();
                }
                await FinishFolders(raw.Key);
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
                else if(!(parent!=null && row.Local is {IsFolder:true,Skip:null} && row.Dropbox==null && row.Synced==null)) await Submit(row,[]);
            }
            await FinishFolders(null);
            foreach(var key in failed)
                await foreach(var notice in store.NoticesAtAsync(id,key,ct))
                    if(notice.Kind==NoticeKind.Failure && !notice.Ignored && !notice.Dismissed)
                        await new Retries().RetryAsync(pair,settings,Log,paths,notice,local,dropbox,store,queue.Held,Populate,Hop,ct);
            if(unreadLocal.Keys.Concat(unreadRemote.Keys).FirstOrDefault() is {} unreadPath) throw new InventoryIncompleteException(Text.Get(TextKey.SkipUnreadable,(TextArg.Path,unreadPath)),null);
            if(physicalHop!=null) return;
            bool completed=scope==null ? await store.FinishRepairAsync(id,(localDebt.Revision,remoteDebt.Revision),JsonSerializer.Serialize(new {
                RunId=scan,Started=started,Finished=DateTimeOffset.UtcNow,Completed=true,FirstRun=first,Effects=effects,IssuesByKind=issues }),ct)
                : await store.FinishRepairAsync(id,scope,remoteDebt.Revision,ct);
            if(!completed) Log(new(RunEventKind.Wait,nameof(Repair),"",Text.Get(TextKey.RepairSuperseded),""));
        }
        finally { await store.ClearScanAsync(id,scan,CancellationToken.None); }

        (bool Covered,bool Touched) Evidence(Side side,string path)
        {
            var range=side==Side.Local?logical.LocalPath:logical.DropboxPath; var unread=side==Side.Local?unreadLocal:unreadRemote;
            return ((range.Length==0 || PathNames.IsSelfOrUnder(path,range)) && !unread.Covers(PathNames.KeyFor(side,path)),false);
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
                    if(!mark.Content && mark.Unreadable==null) continue;
                }
                await Submit(mark.Row with {Context=mark.Row.Context with {ProtectedContents=mark.Content,UnreadableCause=mark.Unreadable}},[]);
            }
        }
        bool Touched(Row row,bool descendants) =>
            (row.Local?.Path??row.Synced?.LocalPath??row.Context.Destination) is {} localPath && store.ScanTouched(id,scan,Side.Local,localPath,descendants) ||
            (row.Dropbox?.Path??row.Synced?.DropboxPath??row.Context.Destination) is {} remotePath && store.ScanTouched(id,scan,Side.Dropbox,remotePath,descendants);
        async ValueTask<string> MissingPath(Side to,string fromPath)
        {
            var from=PathNames.Other(to);
            foreach(var parent in PathNames.Ancestors(fromPath))
                if(await store.FindAsync(id,from,PathNames.KeyFor(from,parent),ct) is {} binding)
                    return PathNames.Child(binding.PathOn(to),PathNames.SpellingOn(to,fromPath[(parent.Length+1)..]));
            return PathNames.SpellingOn(to,fromPath);
        }
        Task Submit(Row? row,IReadOnlyList<RenameCandidate> candidates)
        {
            Task Apply(CancellationToken token)=> (row!=null && Touched(row,row.IsFolder()) || candidates.Any(c=>store.ScanTouched(id,scan,c.Side,c.Moved.Path,c.Moved.IsFolder))) ? Task.CompletedTask : ItemSync.ApplyAsync(pair,settings,Log,paths,row,candidates,local,dropbox,store,Populate,Hop,token,queue.Held,(key,reproducible)=>{ if(!reproducible) throw new WorkQueue.Unsettled(); lock(failed) failed.Add(key); });
            return physicalHop!=null ? Apply(ct) : queue.AdmitAsync(pair,row,candidates,Apply,ct);
        }
    }

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
