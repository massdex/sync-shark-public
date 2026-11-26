using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Streams one two-sided acquisition. Only the revision it observed can be retired.</summary>
public static class Repair
{
    public static async Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, Scope? scope, (SyncedEntry? Binding, Scope Before, Scope? After)? physicalHop, CancellationToken ct,bool caughtUp=false,Action? localRepaired=null)
    {
        // A null After marks live meeting folders; their caller owns the root binding. Coverage exits before any scan state exists.
        if(physicalHop is {After:not null})
        {
            var localEnd=local.BoundaryAsync(ct); var remoteEnd=dropbox.BoundaryAsync(ct);
            await Task.WhenAll(localEnd,remoteEnd);
            if(queue.CoversHop(((await localEnd).Position,(await remoteEnd).Cursor))) return;
        }
        var run=new RepairRun(pair,settings,events,paths,queue,local,dropbox,store,scope,physicalHop,ct,caughtUp,localRepaired);
        await run.PrepareAsync();
        if(await run.FastPathAsync()) return;
        store.BeginScan(pair.PairId,run.scan);
        try
        {
            await run.AcquireAsync();
            await run.ScanAsync();
            await run.BookkeepingAsync();
        }
        finally { await run.CleanupAsync(); }
    }

    /// <summary>Resolve the assigned id of this attempt, never retry a replacement at its path.</summary>
    public static async Task<Notice?> CurrentRetryAsync(IStore store,string pair,Notice original,CancellationToken ct)
    {
        if(original.Id!=0) return await store.NoticeAsync(pair,original.Id,ct) is {} current && Matches(current) ? current : null;
        await foreach(var candidate in store.NoticesAtAsync(pair,PathNames.Key(original.Path),ct)) if(Matches(candidate)) return candidate;
        return null;
        bool Matches(Notice current)=>!current.Dismissed && current.Matches(original with {Id=current.Id});
    }

    /// <summary>Read one Repair inventory range, requesting deletion history only on its dedicated acquisition.</summary>
    public static IAsyncEnumerable<Listed> ReadInventory(Func<string,ListOptions,CancellationToken,IAsyncEnumerable<Listed>> read, string under, Action<RunEvent> events, Action<string>? historyCursor, CancellationToken ct, Action<string>? pruned = null) =>
        read(under,new(Recursive:true,IncludeOutOfScope:false,IncludeDeleted:historyCursor!=null){Events=events,CursorAcquired=historyCursor,Pruned=pruned},ct);

    internal static async IAsyncEnumerable<Listed> Observe(IAsyncEnumerable<Listed> rows,string physical,string logical,PrefixSet unread,[EnumeratorCancellation] CancellationToken ct)
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
            if(observed.Entry is {IsFolder:true,Skip:nameof(TextKey.SkipUnreadable)}) unread.Add(PathNames.Key(observed.Path)); // a file is its own item's failure
            yield return observed;
        }
    }
}
