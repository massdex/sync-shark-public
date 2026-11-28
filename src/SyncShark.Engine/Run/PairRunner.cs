using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Root admission and finite source replay for one pair.</summary>
public sealed class PairRunner
{
    /// <summary>Shared across boot, Repair and feed opportunities for this pair.</summary>
    public Unfinished Unfinished { get; } = new();

    internal Func<(CancellationToken Wake,Func<CancellationToken,Task<JournalPosition?>> Rewind)>? JournalReplay { get; set; }

    /// <summary>Continue in the live loop after the finite startup pass.</summary>
    public Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, CancellationToken ct)
    {
        queue.Unfinished = Unfinished;
        return AutoSync.RunAsync(pair, settings, events, paths, queue, local, dropbox, store, ct,JournalReplay);
    }

    /// <summary>A changed drive outranks a root refusal; the one verdict for one observation.</summary>
    public static (NoticeKind Kind,TextKey Cause)? Refusal(PairConfig pair,RootIdentity? observed,DriveEvidence? drive) =>
        (drive==null ? null : DriveEvidence.Change(pair.Drive,drive)) is {} d ? (NoticeKind.DriveChange,d) : RootCause(pair,observed) is {} c ? (NoticeKind.RootIdentity,c) : null;

    /// <summary>Save the observation's refusal, or close a resolved root notice. Never replace a mismatched saved identity.</summary>
    public async Task<bool> CheckRootsAsync(PairConfig pair,(NoticeKind Kind,TextKey Cause)? refusal,IStore store,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (refusal is {} r) { await store.SaveNoticeAsync(pair.PairId,new(0,r.Kind,r.Cause,string.Empty,new([],null),DateTimeOffset.UtcNow,false),ct); return false; }
        Notice? rootNotice = null;
        await foreach (var notice in store.NoticesAtAsync(pair.PairId, string.Empty, ct))
        {
            if (notice.Kind == NoticeKind.DriveChange) return false;
            if (notice.Kind == NoticeKind.RootIdentity) rootNotice = notice;
        }
        if (rootNotice != null) await store.CloseNoticeAsync(pair.PairId,rootNotice,ct);
        return true;
    }

    private static TextKey? RootCause(PairConfig pair,RootIdentity? observed)
    {
        if (pair.Roots is not { } saved) return TextKey.RootUnbound;
        if (observed == null || !observed.IsValid()) return TextKey.RootUnavailable;
        if (!MatchesScope(saved) || !MatchesScope(observed)) return TextKey.RootConfiguredScopeChanged;
        if (saved.Local != observed.Local) return TextKey.RootLocalIdentityChanged;
        if (saved.Dropbox.AccountId != observed.Dropbox.AccountId || saved.Dropbox.RootNamespaceId != observed.Dropbox.RootNamespaceId ||
            saved.Dropbox.HomeNamespaceId != observed.Dropbox.HomeNamespaceId) return TextKey.RootDropboxScopeChanged;
        return saved.Dropbox.FolderId != observed.Dropbox.FolderId ? TextKey.RootDropboxFolderChanged : null;

        bool MatchesScope(RootIdentity root) => PathNames.Same(PathNames.NormalizeLocalPath(root.LocalRoot), PathNames.NormalizeLocalPath(pair.LocalRoot)) &&
            PathNames.Same(PathNames.TrimDropboxRoot(root.DropboxRoot), PathNames.TrimDropboxRoot(pair.DropboxRoot)) && root.ConnectionId == pair.ConnectionId;
    }

    /// <summary>Visit the captured boundaries and known jobs; Host keeps the same readers for live arrivals.</summary>
    public async Task CatchUpAsync(PairConfig pair, (JournalPosition? Local,string? Dropbox) captured, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, Action attempted, CancellationToken ct,bool continueLive=false)
    {
        queue.Unfinished = Unfinished;
        Unfinished.CoversThrough=queue.CoversHop;
        Unfinished.AcquiredThrough=bound=>queue.ProofNow?.Invoke()?.Invoke(bound)==true;
        // Both owners get one opportunity. A running job needs no terminal reply and held intake need not settle.
        var intake=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        // One boot decision under one lock: the first real fault (the reader offers it before recording it, or the visit's) or "attempted".
        var gate=new object(); bool decided=false; Exception? first=null; using var visit=CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool Decide(Exception? fault) { lock(gate) { if(decided) return false; (decided,first)=(true,fault); return true; } }
        var read=AutoSync.ReadThroughAsync(pair,settings,events,paths,queue,local,dropbox,store,captured,()=>intake.TrySetResult(),lifetime.Token,continueLive,JournalReplay,aborted:e=>{ if(!(e is OperationCanceledException && lifetime.IsCancellationRequested) && Decide(e)) _=visit.CancelAsync(); });
        async Task Notify()
        {
            if(await Task.WhenAny(intake.Task,read).ConfigureAwait(false)==read && !intake.Task.IsCompleted) {await read.ConfigureAwait(false);return;}
            await Unfinished.CheckAsync(pair,settings,events,queue.CurrentScope(paths),null,local,dropbox,store,queue.Held,
                (scope,side,folder,token)=>AutoSync.PopulateAsync(pair,settings,events,paths,scope,side,folder,local,dropbox,store,token),
                (binding,before,after,token)=>Repair.RunAsync(pair,settings,events,paths,queue,local,dropbox,store,null,(binding,before,after),token),visit.Token,refused:AutoSync.JobRefused(settings,events));
            ct.ThrowIfCancellationRequested(); if(Decide(null)) attempted(); else lock(gate) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first!).Throw();
        }
        try {await Notify().ConfigureAwait(false);}
        catch(Exception visitError)
        {
            if(!(visitError is OperationCanceledException && visit.IsCancellationRequested)) Decide(visitError); // a cancellation is ours only when our token was cancelled
            // A failed boot visit must not leave the continuous reader running during the Host retry.
            lifetime.Cancel();try {await read.ConfigureAwait(false);} catch { /* Propagate the boot failure. */ }
            lock(gate) if(first!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
            throw;
        }
        await read.ConfigureAwait(false);
    }
}
