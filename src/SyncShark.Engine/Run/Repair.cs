using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Streams one two-sided acquisition. Only the revision it observed can be retired.</summary>
public static class Repair
{
    public static async Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, Scope? scope, (SyncedEntry? Binding, Scope Before, Scope? After)? physicalHop, CancellationToken ct,bool? caughtUp=false,Action? localRepaired=null,Action<RunEvent>? progress=null)
    {
        var run=new RepairRun(pair,settings,events,paths,queue,local,dropbox,store,scope,physicalHop,ct,caughtUp,localRepaired,progress);
        // A null After marks live meeting folders; their caller owns the root binding. Coverage exits before any scan state exists.
        if(physicalHop is {After:not null})
        {
            if(run.removal) await Executor.RowExecution.RequireDropboxRootAsync(pair,dropbox,ct); // a confirmed-removal cleanup proves the saved root before any exit, covered or not
            var localEnd=local.BoundaryAsync(ct); var remoteEnd=dropbox.BoundaryAsync(ct);
            await Task.WhenAll(localEnd,remoteEnd);
            if(queue.CoversHop(((await localEnd).Position,(await remoteEnd).Cursor))) return;
        }
        await run.PrepareAsync();
        if(await run.FastPathAsync()) { if(run.Completion is {} fast) events(fast); return; }
        store.BeginScan(pair.PairId,run.scan);
        try
        {
            await run.AcquireAsync();
            await run.ScanAsync();
            await Staged(run.BookkeepingAsync,physicalHop==null?events:null,TextKey.StageFinish,null);
        }
        finally { await run.CleanupAsync(); }
        if(run.Completion is {} completion) events(completion);
    }

    /// <summary>Logs one phase's start, then its end with the number of items it passed; a null log keeps a hop's phases silent.
    /// Each item's path and count go only to an attached live view; without one, no per-item record is made.</summary>
    internal static async IAsyncEnumerable<T> Counted<T>(IAsyncEnumerable<T> rows,Action<RunEvent>? stage,Action<RunEvent>? progress,TextKey what,Func<T,string> path,[EnumeratorCancellation] CancellationToken ct)
    {
        var at=Stopwatch.GetTimestamp(); stage?.Invoke(Stage(what,null,null)); long count=0;
        await foreach(var row in rows.WithCancellation(ct)) { count++; progress?.Invoke(new(RunEventKind.Stage,what.ToString(),path(row),string.Empty,string.Empty){Outcome=RunLog.InProgressOutcome,Count=count}); yield return row; }
        stage?.Invoke(Stage(what,count,Stopwatch.GetElapsedTime(at)));
    }

    /// <summary>Logs one phase's start, then its end with the effects applied so far when counted.</summary>
    internal static async Task Staged(Func<Task> phase,Action<RunEvent>? stage,TextKey what,Dictionary<string,int>? effects)
    {
        var at=Stopwatch.GetTimestamp(); stage?.Invoke(Stage(what,null,null)); await phase();
        stage?.Invoke(Stage(what,effects==null ? null : Applied(effects),Stopwatch.GetElapsedTime(at)));
    }

    /// <summary>A path-free 0.3.95 stage line: a start, or an end (with elapsed time) that states its count and the time since its paired start.</summary>
    internal static RunEvent Stage(TextKey what,long? count,TimeSpan? elapsed) => new(RunEventKind.Stage,what.ToString(),string.Empty,StageDetail(what,elapsed!=null,count,elapsed??default),string.Empty){Ended=elapsed!=null,Count=count};

    /// <summary>The shared 0.3.95 stage text: a start with its count, or an end with its count and elapsed seconds.</summary>
    public static string StageDetail(TextKey stage, bool ended = false, long? count = null, TimeSpan elapsed = default) =>
        Text.Get(ended ? TextKey.StageEnded : TextKey.StageStarted,
            (TextArg.Name, Text.Get(stage)),
            (TextArg.Count, count?.ToString(CultureInfo.InvariantCulture) ?? Text.Get(TextKey.CountUnavailable)),
            (TextArg.Seconds, elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The run's completion line: the effects applied and the time since the run started.</summary>
    internal static RunEvent Completed(Dictionary<string,int> effects,DateTimeOffset started) => Stage(TextKey.ResultRunCompleted,Applied(effects),DateTimeOffset.UtcNow-started);

    /// <summary>The effects this run applied, read under the run's own lock.</summary>
    internal static long Applied(Dictionary<string,int> effects) { lock(effects) return effects.Values.Sum(); }

    /// <summary>Resolve the assigned id of this attempt, never retry a replacement at its path.</summary>
    public static async Task<Notice?> CurrentRetryAsync(IStore store,string pair,Notice original,CancellationToken ct)
    {
        if(original.Id!=0) return await store.NoticeAsync(pair,original.Id,ct) is {} current && Matches(current) && current.At==original.At ? current : null;
        return null;
        bool Matches(Notice current)=>!current.Dismissed && current.Matches(original with {Id=current.Id});
    }

    /// <summary>Read one Repair inventory range, requesting deletion history only on its dedicated acquisition.</summary>
    public static IAsyncEnumerable<Listed> ReadInventory(Func<string,ListOptions,CancellationToken,IAsyncEnumerable<Listed>> read, string under, Action<RunEvent> events, Action<string>? historyCursor, CancellationToken ct, Action<string>? pruned = null, Func<bool,Task>? pageWait = null, bool isolate = false) =>
        read(under,new(Recursive:true,IncludeOutOfScope:false,IncludeDeleted:historyCursor!=null){Events=events,CursorAcquired=historyCursor,Pruned=pruned,PageWait=pageWait,IsolateUnreadableFiles=isolate},ct);

    /// <summary>The pair's top folders still have their bound identities; a changed or missing one stops the Repair.</summary>
    internal static async Task RootsUnchangedAsync(PairConfig pair,ISide local,ISide dropbox,CancellationToken ct)
    {
        if(pair.Roots is not {} saved) return;
        var pc=local.RootAsync(ct); var db=dropbox.RootAsync(ct); await Task.WhenAll(pc,db);
        if((await pc).Local!=saved.Local) throw new ChangedUnderneathException(pair.LocalRoot);
        if((await db).Dropbox!=saved.Dropbox) throw new ChangedUnderneathException(pair.DropboxRoot);
    }

    internal static async IAsyncEnumerable<Listed> Observe(IAsyncEnumerable<Listed> rows,string physical,string logical,PrefixSet unread,[EnumeratorCancellation] CancellationToken ct,Action? isolated=null)
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
            if(observed.Entry is {Skip:nameof(TextKey.SkipUnreadable)} skipped) {if(skipped.IsFolder) unread.Add(PathNames.Key(observed.Path)); else isolated?.Invoke();} // a file is its own item's failure; an unreadable Dropbox file also marks the run
            yield return observed;
        }
    }
}
