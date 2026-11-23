using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Acquire the whole physical footprint together, across all pairs on the host.</summary>
public sealed class ItemLocks
{
    private static readonly AsyncLocal<Lease?> Owner = new();
    private static readonly object Gate = new();
    private static readonly SortedSet<string> Held = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Trees = new(StringComparer.Ordinal);
    private static TaskCompletionSource changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static string Physical(PairConfig pair, Side side, string path) => side == Side.Local
        ? PathNames.Key(PathNames.NormalizeLocalPath(Path.Combine(pair.LocalRoot, path)).Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).TrimEnd('/'))
        : pair.ConnectionId + ":" + PathNames.DropboxKey(PathNames.RootedDropboxPath(pair.DropboxRoot,path)).TrimEnd('/');

    /// <summary>Acquire only the observed, saved and target locations belonging to this action.</summary>
    public ValueTask<IDisposable> AcquireAsync(PairConfig pair,SyncAction action,IReadOnlyList<RenameCandidate> candidates,CancellationToken ct,string? caseSuffix=null)
    {
        if(action is {Kind:ActionKind.KeepBoth,Target:{} label,Side:{} receiver})
            return AcquireAsync([(receiver,Physical(pair,receiver,label),action.Row.EntryOn(PathNames.Other(receiver))?.IsFolder==true)],ct);
        var row=action.Row; var endpoints=Row.Paths(row); var tree=ItemSync.Recursive(action);
        var keys=new List<(Side,string,bool)>();
        Side? moving=action.Kind is ActionKind.MoveLocal or ActionKind.MoveDropbox or ActionKind.MoveUnlinked ? action.WrittenSide() : null;
        var victim=action.Kind is ActionKind.DeleteLocal or ActionKind.DeleteDropbox ? row.EntryOn(action.WrittenSide()) : null;
        void Add(Side side,string path) => keys.Add((side,Physical(pair,side,path),tree && (moving==null || moving==side) && (victim==null || side==action.WrittenSide() && PathNames.Same(path,victim.Path))));
        Add(Side.Local,endpoints.Local); Add(Side.Dropbox,endpoints.Dropbox);
        if(row.Synced is {} old) { Add(Side.Local,old.LocalPath); Add(Side.Dropbox,old.DropboxPath); }
        foreach(var c in candidates){ Add(c.Side,c.Moved.Path); Add(Side.Local,c.Synced.LocalPath); Add(Side.Dropbox,c.Synced.DropboxPath); }
        if(action.Target is {} target)
        {
            if(action.Kind is ActionKind.Upload or ActionKind.Download or ActionKind.EnsureLocalFolder or ActionKind.EnsureDropboxFolder or ActionKind.DeleteLocal or ActionKind.DeleteDropbox or ActionKind.ReplaceKind || moving!=null)
                Add(action.WrittenSide(),target);
            else if(action.Side is {} written) Add(written,target);
            if(moving==Side.Dropbox && caseSuffix!=null && row.Dropbox is {} oldRemote && PathNames.CaseOnly(oldRemote.Path,target))
                Add(Side.Dropbox,PathNames.CaseRenamePath(target,caseSuffix));
        }
        return AcquireAsync(keys,ct);
    }

    public async Task<T> RunAsync<T>(PairConfig pair,SyncAction action,IReadOnlyList<RenameCandidate> candidates,Func<Task<T>> work,CancellationToken ct,string? caseSuffix=null)
    {
        var previous=Owner.Value;
        using var lease=(Lease)await AcquireAsync(pair,action,candidates,ct,caseSuffix);
        Owner.Value=lease;
        try { return await work(); }
        finally { Owner.Value=previous; }
    }

    /// <summary>The legacy shape requests exact endpoints, not their children.</summary>
    public ValueTask<IDisposable> AcquireAsync(IReadOnlyList<(Side Side,string Key)> footprint, CancellationToken ct) =>
        AcquireAsync(footprint.Select(x => (x.Side,x.Key,false)).ToArray(),ct);

    /// <summary>Keys are physical endpoint keys. Only a recursive effect locks descendants.</summary>
    public async ValueTask<IDisposable> AcquireAsync(IReadOnlyList<(Side Side,string Key,bool Subtree)> footprint, CancellationToken ct)
    {
        var keys = footprint.GroupBy(x => x.Side + ":" + PathNames.TreeKey(x.Key),StringComparer.Ordinal)
            .Select(g => (Key:g.Key,Tree:g.Any(x => x.Subtree))).ToArray();
        while (true)
        {
            Task signal;
            lock (Gate)
            {
                ct.ThrowIfCancellationRequested();
                var parent=Owner.Value;
                var needed=parent==null ? keys : keys.Where(k=>!parent.Covers(k)).ToArray();
                if(parent!=null && needed.Any(parent.Overlaps)) throw new InventoryIncompleteException(nameof(footprint),null);
                if (!needed.Any(x => Held.Contains(x.Key) || PathNames.Ancestors(x.Key).Any(Trees.Contains) ||
                    x.Tree && PathNames.HasDescendantKey(Held,x.Key)))
                {
                    foreach (var x in needed) { Held.Add(x.Key); if (x.Tree) Trees.Add(x.Key); }
                    return new Lease(needed,parent);
                }
                signal = changed.Task;
            }
            await signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class Lease((string Key,bool Tree)[] keys,Lease? parent) : IDisposable
    {
        private bool released;
        internal bool Covers((string Key,bool Tree) child) => !released && (keys.Any(k=>k.Key==child.Key && (!child.Tree || k.Tree) || k.Tree && PathNames.IsUnderKey(child.Key,k.Key)) || parent?.Covers(child)==true);
        internal bool Overlaps((string Key,bool Tree) child) => !released && (keys.Any(k=>k.Key==child.Key || k.Tree && PathNames.IsUnderKey(child.Key,k.Key) || child.Tree && PathNames.IsUnderKey(k.Key,child.Key)) || parent?.Overlaps(child)==true);
        public void Dispose()
        {
            lock (Gate)
            {
                if (released) return;
                released = true;
                foreach (var x in keys) { Held.Remove(x.Key); if (x.Tree) Trees.Remove(x.Key); }
                var old = changed; changed = NewSignal(); old.SetResult();
            }
        }
    }
}
