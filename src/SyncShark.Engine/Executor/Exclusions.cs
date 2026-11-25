using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Preserve eligible changes on the kept side before removing the excluded copy.</summary>
public sealed class Exclusions
{
    /// <summary>Unsettled work leaves the exclusion owed and the source in place.</summary>
    public async Task<(ExclusionOutcome Outcome, TextKey? Cause)> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput = null, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough = null, Func<Row,Func<PathScope,CancellationToken,Task>,CancellationToken,Task>? admit = null)
    {
        ct.ThrowIfCancellationRequested();
        if (exclusion.Keep) return (ExclusionOutcome.Applied, null);
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var peer = exclusion.Side == Side.Local ? dropbox : local;
        return await new ExclusionApply(pair, settings, events, paths, exclusion, side, peer, local, dropbox, store,
            populate, compareOldBinding, ct, heldInput, coversThrough, admit).RunAsync();
    }

    /// <summary>Estimate potential copies without hashing, and count removal bytes in the same listing.</summary>
    public async Task<ExcludePreview> PreviewAsync(PairConfig pair, Exclusion exclusion, ISide local, ISide dropbox, IStore store, PathScope paths, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var recovery = exclusion.Side == Side.Local ? TextKey.RecoveryRecycleBin : TextKey.RecoveryDropboxHistory;
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var peer = exclusion.Side == Side.Local ? dropbox : local;
        var root = exclusion.Keep ? null : await side.ReadOneAsync(exclusion.Path, ct);
        if (root is null) return new(exclusion.Side, exclusion.Path, 0, 0, 0, 0, recovery, false);
        var scope = paths.Without(exclusion);
        var protection = paths.ProtectionWithout(exclusion);
        var rule = (exclusion.Side, Owed: true);
        var tally = new PreviewTally();
        await foreach (var item in TargetItemsAsync(side, root, ct))
        {
            if (!item.IsFolder)
            {
                tally.Removals++;
                tally.RemoveBytes = checked(tally.RemoveBytes + item.Size);
            }
            var key = PathNames.KeyFor(side.Side, item.Path);
            var (saved, endpoints) = await EndpointsAsync(pair, side.Side, item.Path, store, ct);
            tally.Blocked |= Protected(item, endpoints, scope, protection);
            if (tally.Blocked || tally.Dependent?.Covers(key) == true || scope.Excludes(endpoints.LocalPath, endpoints.DropboxPath, item.IsFolder))
                continue;
            if (await HeldAsync(pair.PairId, endpoints, store, ct))
            {
                if (item.IsFolder)
                    (tally.Dependent ??= new()).Add(key);
                continue;
            }
            var row = await ObserveAsync(item, saved, endpoints, peer, ct);
            var action = Decide.For(row, pair.FirstRun, DateTimeOffset.UtcNow, true, rule);
            Tally(ref tally, item, key, action, peer);
        }
        return new(exclusion.Side, exclusion.Path, tally.Blocked ? 0 : tally.Copies, tally.Blocked ? 0 : tally.CopyBytes,
            tally.Removals, tally.RemoveBytes, recovery, tally.Blocked);
    }

    private static void Tally(ref PreviewTally tally, Entry item, string key, SyncAction action, ISide peer)
    {
        if (item.IsFolder)
        {
            if (action.Kind == ActionKind.Notice || Unsettled(action))
                (tally.Dependent ??= new()).Add(key);
            return;
        }
        if (action.Kind is ActionKind.NeedLocalHash or ActionKind.KeepBoth || action.Kind == SyncAction.CopyOn(false, peer.Side))
        {
            tally.Copies++;
            tally.CopyBytes = checked(tally.CopyBytes + item.Size);
        }
    }

    private struct PreviewTally
    {
        public int Copies;
        public int Removals;
        public long CopyBytes;
        public long RemoveBytes;
        public bool Blocked;
        public PrefixSet? Dependent;
    }

    // This is a pre-send check. A later receipt still needs its own fresh coverage before settlement.
    internal static async ValueTask<bool> RemovalCoveredAsync(Planner.SyncAction action, ISide local, ISide dropbox,
        Func<Scope,IEnumerable<Change>>? heldInput, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough,
        CancellationToken ct)
    {
        if (action.Kind is not (ActionKind.DeleteLocal or ActionKind.DeleteDropbox or ActionKind.ReplaceKind) &&
            !(action.Kind is (ActionKind.Upload or ActionKind.Download) && action.Row.EntryOn(action.WrittenSide()) is not null))
            return true;
        if (heldInput is null || coversThrough is null) return false;
        var pc = local.BoundaryAsync(ct);
        var cloud = dropbox.BoundaryAsync(ct);
        await Task.WhenAll(pc, cloud);
        return (await pc).Position is { } position && (await cloud).Cursor is { Length: > 0 } cursor &&
            coversThrough((position, cursor));
    }

    internal static async IAsyncEnumerable<Entry> TargetItemsAsync(ISide side, Entry root,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return root;
        if (!root.IsFolder || root.Skip is not null) yield break;
        await foreach (var listed in side.ReadAllAsync(root.Path, new ListOptions(true, true), ct))
            if (listed.Entry is { } child) yield return child;
    }

    internal static async Task<(SyncedEntry? Saved, Scope Paths)> EndpointsAsync(PairConfig pair, Side source,
        string path, IStore store, CancellationToken ct)
    {
        var saved = await store.FindAsync(pair.PairId, source, PathNames.KeyFor(source, path), ct);
        var other = PathNames.Other(source);
        var target = saved?.PathOn(other) ?? await ItemSync.CounterpartAsync(pair, source, path, store, ct);
        return (saved, source == Side.Local ? new Scope(path, target) : new Scope(target, path));
    }

    internal static async Task<bool> HeldAsync(string pairId, Scope endpoints, IStore store, CancellationToken ct)
    {
        await foreach (var notice in store.NoticesUnderAsync(pairId, endpoints, ct))
            // An unresolved clash or put-back holds its whole exclusion, before any removal could split around it.
            if (notice is { Dismissed: false, Kind: NoticeKind.Tie or NoticeKind.NameClash or NoticeKind.PutBack } or { Kind: NoticeKind.Failure, Subject.Move: not null })
                return true;
        return false;
    }

    internal static async Task<Row> ObserveAsync(Entry item, SyncedEntry? saved, Scope paths, ISide peer,
        CancellationToken ct)
    {
        var target = peer.Side == Side.Dropbox ? paths.DropboxPath : paths.LocalPath;
        var other = await peer.ReadOneAsync(target, ct);
        if (other?.Skip == nameof(TextKey.SkipUnreadable))
            throw new ItemFailedException(target, Text.ReadFailedOn(peer.Side), null);
        return new(saved?.LocalKey ?? PathNames.Key(paths.LocalPath), peer.Side == Side.Dropbox ? item : other,
            peer.Side == Side.Local ? item : other, saved, [], null, RowContext.Neutral with { Destination = target, Skipped = other?.Skip is not null });
    }

    internal static bool Protected(Entry item, Scope endpoints, PathScope scope, PathScope protection) =>
        item.Skip is not null || protection.Excludes(endpoints.LocalPath, endpoints.DropboxPath, item.IsFolder) ||
        scope.HoldsNestedRoot(Side.Local, endpoints.LocalPath) || scope.HoldsNestedRoot(Side.Dropbox, endpoints.DropboxPath);

    internal static bool Unsettled(SyncAction action) => action.Kind == ActionKind.None &&
        action.None is not (NoneReason.InSync or NoneReason.OutOfScope);

    internal static bool Owed(PairConfig pair, Exclusion exclusion) =>
        pair.OwedScopes is { } scopes && scopes.Any(s => exclusion.Side == Side.Local
            ? PathNames.IsSelfOrUnder(exclusion.Path, s.LocalPath)
            : PathNames.IsSelfOrUnder(exclusion.Path, s.DropboxPath));
}
