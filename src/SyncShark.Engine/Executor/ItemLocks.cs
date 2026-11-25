using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Acquire the whole physical footprint together, across all pairs on the host.</summary>
public sealed class ItemLocks
{
    private static readonly AsyncLocal<Lease?> Owner = new();
    private static readonly SemaphoreSlim Serial = new(1,1);
    private static Lease? Live => Owner.Value is {Released:false} lease ? lease : null;
    internal static void Detach() => Owner.Value=null;
    internal static bool Holding => Live?.Root.Holds==true;
    internal static bool Resumed => Live?.Root.Resumed==true;
    private static readonly object Gate = new();
    private static readonly SortedSet<string> Held = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Trees = new(StringComparer.Ordinal);
    private static TaskCompletionSource changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static string Physical(PairConfig pair, Side side, string path) => side == Side.Local
        ? PathNames.Key(PathNames.NormalizeLocalPath(Path.Combine(pair.LocalRoot, path)).Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).TrimEnd('/'))
        : pair.ConnectionId + ":" + PathNames.DropboxKey(PathNames.RootedDropboxPath(pair.DropboxRoot,path)).TrimEnd('/');

    /// <summary>True while a lease holds this location or a recursive ancestor; never waits.</summary>
    internal static bool Busy(PairConfig pair,Side side,string path)
    {
        var key=side+":"+PathNames.TreeKey(Physical(pair,side,path));
        lock(Gate) return Held.Contains(key) || PathNames.Ancestors(key).Any(Trees.Contains);
    }

    /// <summary>Acquire only the observed, saved and target locations belonging to this action.</summary>
    public ValueTask<IDisposable> AcquireAsync(PairConfig pair,SyncAction action,IReadOnlyList<RenameCandidate> candidates,CancellationToken ct,string? caseSuffix=null,SyncedEntry? incumbent=null)
        => AcquireAsync(Footprint(pair,action,candidates,caseSuffix,incumbent),ct);

    private static IReadOnlyList<(Side Side,string Key,bool Subtree)> Footprint(PairConfig pair,SyncAction action,IReadOnlyList<RenameCandidate> candidates,string? caseSuffix,SyncedEntry? incumbent)
    {
        if(action is {Kind:ActionKind.KeepBoth,Target:{} label,Side:{} receiver})
            return [(receiver,Physical(pair,receiver,label),action.Row.EntryOn(PathNames.Other(receiver))?.IsFolder==true)];
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
        if(incumbent!=null && moving is {} receiverSide && action.Target is {} destination)
        {
            var other=PathNames.Other(receiverSide);bool subtree=tree || incumbent.IsFolder;
            foreach(var side in new[]{receiverSide,other})
                keys.Add((side,Physical(pair,side,row.EntryOn(side)!.Path),subtree));
            keys.Add((receiverSide,Physical(pair,receiverSide,destination),subtree));
            keys.Add((other,Physical(pair,other,incumbent.PathOn(other)),subtree));
        }
        return keys;
    }

    public Task<T> RunAsync<T>(PairConfig pair,SyncAction action,IReadOnlyList<RenameCandidate> candidates,Func<Task<T>> work,CancellationToken ct,string? caseSuffix=null,SyncedEntry? incumbent=null,bool serial=false)
        => RunAsync(Footprint(pair,action,candidates,caseSuffix,incumbent),work,ct,serial || action.Kind==ActionKind.KeepBoth);

    /// <summary>Run as the footprint owner so nested work can use its covered locations.</summary>
    public async Task<T> RunAsync<T>(IReadOnlyList<(Side Side,string Key,bool Subtree)> footprint, Func<Task<T>> work, CancellationToken ct,bool serial=false)
    {
        var previous=Live;
        bool permit=serial && previous==null;
        if(permit && !Serial.Wait(0,ct)) throw new InventoryIncompleteException(nameof(footprint),null);
        else if(serial) await PromoteAsync(ct);
        try
        {
            using var lease=(Lease)await AcquireAsync(footprint,ct);
            if(permit) {lease.Holds=true;permit=false;}
            Owner.Value=lease;
            try { return await work(); }
            finally { Owner.Value=previous; }
        }
        finally { if(permit) Serial.Release(); }
    }

    // A contender cannot wait with keys that the current conflict may need.
    internal static async ValueTask PromoteAsync(CancellationToken ct)
    {
        if(Live is not {} leaf || leaf.Root.Holds) return;
        if(Serial.Wait(0,ct)) {leaf.Root.Holds=true;return;}
        lock(Gate) {leaf.Suspend();Wake();}
        await Serial.WaitAsync(ct);
        leaf.Root.Holds=true;
        while(true)
        {
            Task signal;
            lock(Gate)
            {
                ct.ThrowIfCancellationRequested();
                if(leaf.CanResume()) {leaf.Resume();return;}
                signal=changed.Task;
            }
            await signal.WaitAsync(ct);
        }
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
            bool promote;
            lock (Gate)
            {
                ct.ThrowIfCancellationRequested();
                var parent=Live;
                var needed=parent==null ? keys : keys.Where(k=>!parent.Covers(k)).ToArray();
                if(parent!=null && needed.Any(parent.Overlaps)) throw new InventoryIncompleteException(nameof(footprint),null);
                if (needed.All(Free))
                {
                    foreach (var x in needed) Take(x);
                    return new Lease(needed,parent);
                }
                promote=parent!=null && !parent.Root.Holds;
                signal = changed.Task;
            }
            if(promote) await PromoteAsync(ct); else await signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool Free((string Key,bool Tree) x) => !Held.Contains(x.Key) && !PathNames.Ancestors(x.Key).Any(Trees.Contains) && !(x.Tree && PathNames.HasDescendantKey(Held,x.Key));
    private static void Take((string Key,bool Tree) x) {Held.Add(x.Key);if(x.Tree) Trees.Add(x.Key);}
    private static void Drop((string Key,bool Tree) x) {Held.Remove(x.Key);if(x.Tree) Trees.Remove(x.Key);}
    private static void Wake() {var old=changed;changed=NewSignal();old.SetResult();}

    private sealed class Lease((string Key,bool Tree)[] keys,Lease? parent) : IDisposable
    {
        internal Lease Root => parent?.Root ?? this;
        internal bool Released, Holds, Resumed;
        private bool suspended;
        internal void Suspend() {foreach(var key in keys) Drop(key);suspended=true;parent?.Suspend();}
        internal bool CanResume() => keys.All(Free) && (parent?.CanResume()??true);
        internal void Resume() {foreach(var key in keys) Take(key);suspended=false;Resumed=true;parent?.Resume();}
        internal bool Covers((string Key,bool Tree) child) => !Released && (keys.Any(k=>k.Key==child.Key && (!child.Tree || k.Tree) || k.Tree && PathNames.IsUnderKey(child.Key,k.Key)) || parent?.Covers(child)==true);
        internal bool Overlaps((string Key,bool Tree) child) => !Released && (keys.Any(k=>k.Key==child.Key || k.Tree && PathNames.IsUnderKey(child.Key,k.Key) || child.Tree && PathNames.IsUnderKey(k.Key,child.Key)) || parent?.Overlaps(child)==true);
        public void Dispose()
        {
            lock (Gate)
            {
                if (Released) return;
                Released = true;
                if(!suspended) foreach(var key in keys) Drop(key);
                if(Holds) Serial.Release();
                Wake();
            }
        }
    }
}
