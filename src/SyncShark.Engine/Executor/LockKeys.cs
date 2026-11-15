using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>The lock keys of section 7.2, built with PathLocks.LocalKey / DropboxKey.</summary>
public static class LockKeys
{
    public static IReadOnlyList<PathLockScope> ForScopes(PairConfig pair,
        IReadOnlyList<LiveEffectScope> scopes) => scopes.Select(scope => new PathLockScope(scope.Side switch
        {
            Side.Local => PathLocks.LocalNativeKey(scope.NativePath),
            Side.Dropbox => PathLocks.DropboxNativeKey(pair.ConnectionId, scope.NativePath),
            _ => throw new ArgumentOutOfRangeException(nameof(scopes))
        }, scope.IncludesDescendants)).GroupBy(scope => scope.Key, PathLocks.KeyComparer)
            .Select(group => new PathLockScope(group.Key, group.Any(scope => scope.IncludesDescendants))).ToArray();

    public static IReadOnlyList<PathLockScope> ForRecovery(PairConfig pair,
        FolderRemovalState operation, PendingRead expectedPending)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var scopes = new List<PathLockScope>();
        void Relative(Side side, string path, bool subtree)
        {
            scopes.Add(new PathLockScope(side == Side.Local ? PathLocks.LocalKey(pair, path) :
                PathLocks.DropboxKey(pair, path), subtree));
            if (path.Length != 0) scopes.Add(new PathLockScope(side == Side.Local ?
                PathLocks.LocalKey(pair, PathNames.Parent(path)) : PathLocks.DropboxKey(pair, PathNames.Parent(path)), false));
        }
        void Saved(SyncedEntry? saved)
        {
            if (saved is null) return;
            Relative(Side.Local, saved.LocalPath, operation.RootEntry.IsFolder);
            Relative(Side.Dropbox, saved.DropboxPath, operation.RootEntry.IsFolder);
        }
        Relative(operation.DeletingSide, operation.RootEntry.Path, true);
        Relative(PathNames.Other(operation.DeletingSide), operation.CounterpartPath, true);
        Saved(operation.SavedRoot);
        switch (expectedPending)
        {
            case NoPending: break;
            case CurrentPending current:
                var pending = current.Value;
                Relative(Side.Local, pending.Path, false);
                Relative(Side.Local, pending.Losing.Expected.Path, false);
                if (pending.Source is { } source) Relative(Side.Dropbox, source.Path, source.IsFolder);
                Saved(pending.Saved);
                if (pending.Stage is { } stage) scopes.Add(new PathLockScope(PathLocks.LocalNativeKey(stage.Path), false));
                if (pending.Victim is { } victim) scopes.Add(new PathLockScope(PathLocks.LocalNativeKey(victim.Path), false));
                break;
            default: throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
        }
        return scopes.GroupBy(scope => scope.Key, PathLocks.KeyComparer)
            .Select(group => new PathLockScope(group.Key, group.Any(scope => scope.IncludesDescendants))).ToArray();
    }

    public static IReadOnlyList<PathLockScope> ForRecovery(PairConfig pair,
        FolderRemovalState operation, PendingRead expectedPending, string? stagePath, string? allocationPath)
    {
        var scopes = ForRecovery(pair, operation, expectedPending).ToList();
        if (stagePath is not null) scopes.Add(new PathLockScope(PathLocks.LocalNativeKey(stagePath), false));
        if (allocationPath is not null) scopes.Add(new PathLockScope(PathLocks.LocalNativeKey(allocationPath), false));
        return scopes.GroupBy(scope => scope.Key, PathLocks.KeyComparer)
            .Select(group => new PathLockScope(group.Key, group.Any(scope => scope.IncludesDescendants))).ToArray();
    }

    /// <summary>Requires the admitted action footprint, including folder ranges, through one owner decision.</summary>
    internal static void Require(IAdmittedExecution execution, PairConfig pair, long generation,
        SyncAction action, KeepBothLabels? labels, IReadOnlyCollection<string> keys)
    {
        if (action.Row.IsFolder())
            execution.RequireScopes(pair.PairId, generation, ForScopes(pair, ForLiveAction(action, pair, labels)));
        else execution.Require(pair.PairId, generation, keys as IReadOnlyList<string> ?? keys.ToArray());
    }

    /// <summary>Projects the same action locations into source-validation scopes, retaining folder ranges.</summary>
    public static IReadOnlyList<LiveEffectScope> ForLiveAction(SyncAction action, PairConfig pair,
        KeepBothLabels? labels) => action.Kind is ActionKind.MoveDropbox or ActionKind.MoveLocal or ActionKind.MoveUnlinked
            ? ForRenamePaths(pair, ActionPaths(action, labels), action.Row.IsFolder())
            : ForLivePaths(pair, ActionPaths(action, labels), IncludesContents(action),
                action.Kind == ActionKind.DeferFolder);

    /// <summary>The same footprint under ordinary evidence, for any action other than the folder plan it was made for.</summary>
    internal static IReadOnlyList<LiveEffectScope> Ordinary(IReadOnlyList<LiveEffectScope> footprint) =>
        footprint.Select(scope => scope with { FolderPlan = false }).ToArray();

    internal static IReadOnlyList<LiveEffectScope> ForSelectedBranch(IReadOnlyList<LiveEffectScope> footprint,
        string key, WorkSelection selection, LiveEffectScope selectedScope) =>
        key == selection.Key ? footprint : footprint.Union([selectedScope]).ToArray();

    // Creating or recording a folder does not read or change its descendants.
    private static bool IncludesContents(SyncAction action) => action.Row.IsFolder() && !action.CreatesNewItem() &&
        action.Kind is not (ActionKind.Record or ActionKind.None);

    /// <summary>Projects relative endpoint paths into source-validation scopes.</summary>
    internal static IReadOnlyList<LiveEffectScope> ForLivePaths(PairConfig pair,
        IEnumerable<(Side Side, string Path)> paths, bool isFolder, bool folderPlan = false) =>
        paths.Select(path => new LiveEffectScope(path.Side,
            path.Side == Side.Local ? PathLocks.LocalNativePath(pair, path.Path) :
                PathLocks.DropboxNativePath(pair, path.Path), isFolder, FolderPlan: folderPlan)).ToArray();

    /// <summary>A folder rename carries child work without consuming it, while retaining subtree locks.</summary>
    internal static IReadOnlyList<LiveEffectScope> ForRenamePaths(PairConfig pair,
        IEnumerable<(Side Side, string Path)> paths, bool isFolder) =>
        ForLivePaths(pair, paths, isFolder).Select(scope => scope with { ExactEvidence = isFolder }).ToArray();

    /// <summary>Collects the lock keys for every path the action reads or writes, including its target and
    /// keep-both labels.</summary>
    public static IReadOnlyCollection<string> For(SyncAction action, PairConfig pair, KeepBothLabels? labels) =>
        ForPaths(pair, ActionPaths(action, labels));

    private static IReadOnlyList<(Side Side, string Path)> ActionPaths(SyncAction action, KeepBothLabels? labels)
    {
        var row = action.Row;
        // Extra candidates share their side's primary entry key by the scan grouping (3.4).
        var paths = new List<(Side Side, string Path)>();
        if (row.Local is { } local) paths.Add((Side.Local, local.Path));
        if (row.Dropbox is { } dropbox) paths.Add((Side.Dropbox, dropbox.Path));
        if (row.Synced is { } synced)
        {
            // On an unchanged row the saved spelling is the observed one: normalize each location once.
            if (row.Local is null || !string.Equals(row.Local.Path, synced.LocalPath, StringComparison.Ordinal))
                paths.Add((Side.Local, synced.LocalPath));
            if (row.Dropbox is null || !string.Equals(row.Dropbox.Path, synced.DropboxPath, StringComparison.Ordinal))
                paths.Add((Side.Dropbox, synced.DropboxPath));
        }
        if (row.Context.Destination is { } destination)
        {
            if (row.Local is null) paths.Add((Side.Local, destination));
            if (row.Dropbox is null) paths.Add((Side.Dropbox, destination));
        }
        if (action.Target is { } target)
        {
            var written = action.WrittenSide();
            // A missing side's destination is already the written location; one comparison beats a second key.
            if (!paths.Contains((written, target))) paths.Add((written, target));
        }
        if (action.Kind == ActionKind.KeepBoth)
        {
            if (labels is null) throw new InvalidOperationException();
            // An omitted leg has no label, so it contributes no lock.
            if (labels.LocalLabel is { } localLabel) paths.Add((Side.Local, localLabel));
            if (labels.DropboxLabel is { } dropboxLabel) paths.Add((Side.Dropbox, dropboxLabel));
        }
        return paths;
    }

    /// <summary>For runners that lock paths directly (rename, folder plan, exclusion, resolution).</summary>
    public static IReadOnlyCollection<string> ForPaths(PairConfig pair, IEnumerable<(Side Side, string Path)> paths) =>
        paths.Select(path => path.Side switch
        {
            Side.Local => PathLocks.LocalKey(pair, path.Path),
            Side.Dropbox => PathLocks.DropboxKey(pair, path.Path),
            _ => throw new ArgumentOutOfRangeException(nameof(paths))
        }).ToHashSet(PathLocks.KeyComparer);
}
