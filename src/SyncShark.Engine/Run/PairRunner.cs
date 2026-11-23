using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Root admission and finite source replay for one pair.</summary>
public sealed class PairRunner
{
    /// <summary>Shared across boot, Repair and feed opportunities for this pair.</summary>
    public Unfinished Unfinished { get; } = new();

    /// <summary>Continue in the live loop after the finite startup pass.</summary>
    public Task RunAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, CancellationToken ct)
    {
        queue.Unfinished = Unfinished;
        return AutoSync.RunAsync(pair, settings, events, paths, queue, local, dropbox, store, ct);
    }

    /// <summary>A changed drive outranks a root refusal. Never replace a mismatched saved identity.</summary>
    public async Task<bool> CheckRootsAsync(PairConfig pair, RootIdentity? observed, DriveEvidence? observedDrive, IStore store, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var driveCause = observedDrive == null ? null : DriveEvidence.Change(pair.Drive, observedDrive);
        var cause = driveCause ?? RootCause();
        if (cause != null)
        {
            await store.SaveNoticeAsync(pair.PairId, new(0, driveCause != null ? NoticeKind.DriveChange : NoticeKind.RootIdentity,
                cause.Value, string.Empty, new([], null), DateTimeOffset.UtcNow, false), ct);
            return false;
        }
        Notice? rootNotice = null;
        await foreach (var notice in store.NoticesAtAsync(pair.PairId, string.Empty, ct))
        {
            if (notice.Kind == NoticeKind.DriveChange) return false;
            if (notice.Kind == NoticeKind.RootIdentity) rootNotice = notice;
        }
        if (rootNotice != null) await store.CloseNoticeAsync(pair.PairId,rootNotice,ct);
        return true;

        TextKey? RootCause()
        {
            if (pair.Roots is not { } saved) return TextKey.RootUnbound;
            if (observed == null || !observed.IsValid()) return TextKey.RootUnavailable;
            if (!MatchesScope(saved) || !MatchesScope(observed)) return TextKey.RootConfiguredScopeChanged;
            if (saved.Local != observed.Local) return TextKey.RootLocalIdentityChanged;
            if (saved.Dropbox.AccountId != observed.Dropbox.AccountId || saved.Dropbox.RootNamespaceId != observed.Dropbox.RootNamespaceId ||
                saved.Dropbox.HomeNamespaceId != observed.Dropbox.HomeNamespaceId) return TextKey.RootDropboxScopeChanged;
            return saved.Dropbox.FolderId != observed.Dropbox.FolderId ? TextKey.RootDropboxFolderChanged : null;
        }
        bool MatchesScope(RootIdentity root) => PathNames.Same(PathNames.NormalizeLocalPath(root.LocalRoot), PathNames.NormalizeLocalPath(pair.LocalRoot)) &&
            PathNames.Same(PathNames.TrimDropboxRoot(root.DropboxRoot), PathNames.TrimDropboxRoot(pair.DropboxRoot)) && root.ConnectionId == pair.ConnectionId;
    }

    /// <summary>Visit only the captured boundaries and known jobs; later arrivals belong to the live loop.</summary>
    public async Task CatchUpAsync(PairConfig pair, (JournalPosition? Local,string? Dropbox) captured, EngineSettings settings, Action<RunEvent> events, PathScope paths, WorkQueue queue, ISide local, ISide dropbox, IStore store, Action attempted, CancellationToken ct)
    {
        queue.Unfinished = Unfinished;
        Unfinished.CoversThrough=queue.CoversHop;
        var jobs=Unfinished.CheckAsync(pair, settings, events, paths, null, local, dropbox, store, queue.Held,
            (scope, side, folder, token) => AutoSync.PopulateAsync(pair, settings, events, paths, scope, side, folder, local, dropbox, store, token),
            (binding, before, after, token) => Repair.RunAsync(pair, settings, events, paths, queue, local, dropbox, store, null, (binding,before,after), token), ct);
        // Both owners get one opportunity. A running job needs no terminal reply and held intake need not settle.
        var intake=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read=AutoSync.ReadThroughAsync(pair,settings,events,paths,queue,local,dropbox,store,captured,()=>intake.TrySetResult(),ct);
        async Task Notify()
        {
            await jobs.ConfigureAwait(false);
            if(await Task.WhenAny(intake.Task,read).ConfigureAwait(false)==read && !intake.Task.IsCompleted) {await read.ConfigureAwait(false);return;}
            ct.ThrowIfCancellationRequested(); attempted();
        }
        await Task.WhenAll(read,Notify()).ConfigureAwait(false);
    }
}
