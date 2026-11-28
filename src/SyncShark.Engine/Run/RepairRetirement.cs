using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Which of a Repair run's notices its scan proved retirable, and their closing.</summary>
internal sealed class RepairRetirement(RepairRun run)
{
    private static bool Retirable(Notice notice)=>notice.Kind is (NoticeKind.Failure or NoticeKind.Tie or NoticeKind.NameClash) &&
        notice.Subject.Move==null && notice.Cause is not (TextKey.FailDropboxDeleteJobInvalid or TextKey.FailDropboxOutcomeUnknown); // an unresolved effect only positive settlement releases

    private async Task<Scope> Footprint(Side side,string path,CancellationToken token)
    {
        var binding=await run.store.FindAsync(run.id,side,PathNames.KeyFor(side,path),token);
        var peer=binding?.PathOn(PathNames.Other(side))??await ItemSync.CounterpartAsync(run.pair,side,path,run.store,token);
        return side==Side.Local ? new Scope(path,peer) : new Scope(peer,path);
    }
    private bool CanClose(Scope f) => !run.queue.Unfinished.CoversOpenJob(f) && !run.queue.Held(f).Any() && !run.queue.CurrentScope(run.paths).Excludes(f.LocalPath,f.DropboxPath,true);
    internal async Task<Scope?> RetiringFootprint(Row raw,Notice notice,NoticeItem? member,CancellationToken token)
    {
        var (left,right)=Row.Paths(raw); var items=notice.Subject.Items;
        if(Retirable(notice) && !notice.Ignored && !notice.Dismissed && !raw.CounterpartElsewhere && !raw.Ambiguous && raw.Context.CompleteInventory &&
            (member==null ? items.Count==0 : items.Count==1 && items[0]==member ||
                // Every member of a multi-member notice sits on this bilaterally absent row (A51/A52).
                items.Count>1 && raw.Local==null && raw.Dropbox==null && items.Contains(member) && items.All(i=>PathNames.Same(i.Path,i.Side==Side.Local?left:right))))
        {
            var footprint=await Footprint(member?.Side??notice.Subject.Incoming??Side.Local,member?.Path??notice.Path,token);
            if(PathNames.Same(left,footprint.LocalPath) && PathNames.Same(right,footprint.DropboxPath) && CanClose(footprint) &&
                !run.store.ScanTouched(run.id,run.scan,Side.Local,left,raw.IsFolder()) && !run.store.ScanTouched(run.id,run.scan,Side.Dropbox,right,raw.IsFolder()) &&
                (raw.Local==null && raw.Dropbox==null || member!=null && notice.Kind!=NoticeKind.Tie && raw.EntryOn(member.Side) is {Skip:null} currentMember && currentMember.Id!=member.Id && raw.EntryOn(PathNames.Other(member.Side))==null))
            {
                return footprint;
            }
        }
        return null;
    }
    internal async Task CloseSelected(List<(Scope Footprint,Notice Notice)> selectedNotices,CancellationToken token)
    {
        foreach(var (footprint,notice) in selectedNotices)
            if(CanClose(footprint)) await new Notices().CloseAsync(run.store,run.id,notice,token,()=>CanClose(footprint)); // re-decided inside the close transaction
    }
}
