using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Expected effects of applying the exclusion.</summary>
public sealed record ExcludePreview(Side Side, string Path, int CopyFiles, long CopyBytes, int RemoveFiles,
    long RemoveBytes, TextKey RecoveryPlace, bool BlockedByProtected);

/// <summary>Only Applied settles the exclusion.</summary>
public enum ExclusionOutcome { Applied, Pending, Refused }

/// <summary>Preserve eligible changes on the kept side before removing the excluded copy.</summary>
public sealed class Exclusions
{
    /// <summary>Unsettled work leaves the exclusion owed and the source in place.</summary>
    public async Task<ExclusionOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput = null)
    {
        ct.ThrowIfCancellationRequested();
        if (exclusion.Keep) return ExclusionOutcome.Applied;
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var peer = exclusion.Side == Side.Local ? dropbox : local;
        Entry? selected = null;
        Row? row = null;
        try
        {
            var root = await side.ReadOneAsync(exclusion.Path, ct);
            if (root is null) return ExclusionOutcome.Applied;
            var (initial, footprint) = await EndpointsAsync(pair, side.Side, root.Path, store, ct);
            if (await HeldAsync(pair.PairId, footprint, store, ct)) return ExclusionOutcome.Pending;
            var victim = await ObserveAsync(root, initial, footprint, peer, ct);
            var removal = new SyncAction(SyncAction.DeleteOn(side.Side), victim, Side: side.Side);
            return await new ItemLocks().RunAsync(pair, removal, [], async () =>
            {
                if (await side.ReadOneAsync(root.Path, ct) != root ||
                    await peer.ReadOneAsync(victim.Context.Destination!, ct) != victim.EntryOn(peer.Side) ||
                    await EndpointsAsync(pair, side.Side, root.Path, store, ct) != (initial, footprint))
                    return ExclusionOutcome.Pending;
                var scope = paths.Without(exclusion);
                var protection = paths.ProtectionWithout(exclusion);
                var rule = (exclusion.Side, Owed(pair, exclusion));
                var outstanding = ExclusionOutcome.Applied;
                PrefixSet? dependent = null;
                await foreach (var item in TargetItemsAsync(side, root, ct))
                {
                    if (dependent?.Covers(PathNames.KeyFor(side.Side, item.Path)) == true) continue;
                    selected = item;
                    ExclusionOutcome outcome;
                    try { outcome = await PreserveAsync(item); }
                    catch (Exception fault) when (Notices.ItemFailure(pair, fault) is { } failure)
                    {
                        await ReportAsync(failure);
                        outcome = ExclusionOutcome.Pending;
                    }
                    if (outcome != ExclusionOutcome.Applied)
                    {
                        if (item == root) return outcome;
                        if (outcome == ExclusionOutcome.Refused || outstanding == ExclusionOutcome.Applied) outstanding = outcome;
                        if (item.IsFolder) (dependent ??= new()).Add(PathNames.KeyFor(side.Side, item.Path));
                    }
                    selected = null;
                    row = null;
                }
                if (outstanding != ExclusionOutcome.Applied) return outstanding;
                // Preservation may have created the binding; selection must use the committed version.
                selected = root;
                row = null;
                var key = PathNames.KeyFor(side.Side, root.Path);
                var binding = await store.FindAsync(pair.PairId, side.Side, key, ct);
                row = Row.ForOneSide(side.Side, key, root, binding, [], null, RowContext.Neutral);
                var deletion = new SyncAction(SyncAction.DeleteOn(side.Side), row, Side: side.Side);
                return await new Effects().ApplyAsync(pair, settings, events, protection, deletion, local, dropbox, store,
                    populate, compareOldBinding, ct, heldInput) switch
                {
                    EffectOutcome.Applied => ExclusionOutcome.Applied,
                    EffectOutcome.Pending => ExclusionOutcome.Pending,
                    _ => ExclusionOutcome.Refused
                };

                async Task<ExclusionOutcome> PreserveAsync(Entry item)
                {
                    var (saved, endpoints) = await EndpointsAsync(pair, side.Side, item.Path, store, ct);
                    if (item.Skip == nameof(TextKey.SkipUnreadable))
                        throw new ItemFailedException(item.Path, Text.ReadFailedOn(side.Side), null);
                    if (Protected(item, endpoints, scope, protection))
                    {
                        await new Notices().RaiseAsync(store, pair.PairId, new(0, NoticeKind.Failure,
                            TextKey.FailExcludeHoldsProtected, exclusion.Path, new([new(side.Side, item.Path, item.Id)],
                            side.Side), DateTimeOffset.UtcNow, false), ct);
                        return ExclusionOutcome.Refused;
                    }
                    if (await HeldAsync(pair.PairId, endpoints, store, ct)) return ExclusionOutcome.Pending;
                    if (scope.Excludes(endpoints.LocalPath, endpoints.DropboxPath, item.IsFolder)) return ExclusionOutcome.Applied;
                    row = await ObserveAsync(item, saved, endpoints, peer, ct);
                    var action = Decide.For(row, pair.FirstRun, DateTimeOffset.UtcNow, true, rule);
                    if (action.Kind == ActionKind.NeedLocalHash)
                    {
                        if (row.Local is not { IsFolder: false } file) return ExclusionOutcome.Refused;
                        row = row with { LocalHash = await local.ContentHashAsync(file, ct) };
                        action = Decide.For(row, pair.FirstRun, DateTimeOffset.UtcNow, true, rule);
                    }
                    if (action.Kind == ActionKind.None)
                    {
                        return Unsettled(action) ? ExclusionOutcome.Pending : ExclusionOutcome.Applied;
                    }
                    var planned = row;
                    var outcome = await new ItemLocks().RunAsync(pair, action, [], async () =>
                    {
                        var current = await EndpointsAsync(pair, side.Side, item.Path, store, ct);
                        if (current != (saved, endpoints) || await side.ReadOneAsync(item.Path, ct) != item ||
                            await peer.ReadOneAsync(planned.Context.Destination!, ct) != planned.EntryOn(peer.Side) ||
                            await HeldAsync(pair.PairId, endpoints, store, ct)) return EffectOutcome.Pending;
                        return await new Effects().ApplyAsync(pair, settings, events, scope, action, local, dropbox,
                            store, populate, compareOldBinding, ct, heldInput);
                    }, ct, settings.CaseRenameSuffix);
                    if (outcome == EffectOutcome.Refused) return ExclusionOutcome.Refused;
                    if (outcome != EffectOutcome.Applied) return ExclusionOutcome.Pending;
                    return ExclusionOutcome.Applied;
                }
            }, ct, settings.CaseRenameSuffix);
        }
        catch (Exception fault) when (Notices.ItemFailure(pair, fault) is { } error)
        {
            await ReportAsync(error);
            return ExclusionOutcome.Pending;
        }
        catch (InventoryIncompleteException inventory) when (selected is null && !ct.IsCancellationRequested)
        {
            // A partial listing cannot prove the remaining subtree empty.
            var error = inventory.InnerException as ItemFailedException ??
                new ItemFailedException(exclusion.Path, Text.ReadFailedOn(side.Side), inventory);
            if (error.Disposition == FailureDisposition.TemporaryNoEffect) throw error;
            await ReportAsync(error);
            return ExclusionOutcome.Pending;
        }

        async Task ReportAsync(ItemFailedException error)
        {
            // Preserve an uncertain receipt even when cancellation arrived after dispatch.
            var token = error.Disposition == FailureDisposition.UnknownEffect ? CancellationToken.None : ct;
            token.ThrowIfCancellationRequested();
            var items = new List<NoticeItem>();
            if (row?.Local is { } pc) items.Add(new(Side.Local, pc.Path, pc.Id));
            if (row?.Dropbox is { } db) items.Add(new(Side.Dropbox, db.Path, db.Id));
            if (items.Count == 0 && selected is not null)
                items.Add(new(side.Side, selected.Path, selected.Id));
            await ItemSync.ReportFailureAsync(pair, store, events, error, new(items, side.Side),
                PathNames.KeyFor(side.Side, error.Path), false, null, token);
            ct.ThrowIfCancellationRequested();
        }
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
        int copies = 0, removals = 0;
        long copyBytes = 0, removeBytes = 0;
        var blocked = false;
        PrefixSet? dependent = null;
        await foreach (var item in TargetItemsAsync(side, root, ct))
        {
            if (!item.IsFolder) { removals++; removeBytes = checked(removeBytes + item.Size); }
            var key = PathNames.KeyFor(side.Side, item.Path);
            var (saved, endpoints) = await EndpointsAsync(pair, side.Side, item.Path, store, ct);
            blocked |= Protected(item, endpoints, scope, protection);
            if (blocked || dependent?.Covers(key) == true || scope.Excludes(endpoints.LocalPath, endpoints.DropboxPath, item.IsFolder)) continue;
            if (await HeldAsync(pair.PairId, endpoints, store, ct))
            {
                if (item.IsFolder) (dependent ??= new()).Add(key);
                continue;
            }
            var row = await ObserveAsync(item, saved, endpoints, peer, ct);
            var action = Decide.For(row, pair.FirstRun, DateTimeOffset.UtcNow, true, rule);
            if (item.IsFolder)
            {
                if (action.Kind == ActionKind.Notice || Unsettled(action)) (dependent ??= new()).Add(key);
                continue;
            }
            if (action.Kind is ActionKind.NeedLocalHash or ActionKind.KeepBoth || action.Kind == SyncAction.CopyOn(false, peer.Side))
            {
                copies++;
                copyBytes = checked(copyBytes + item.Size);
            }
        }
        return new(exclusion.Side, exclusion.Path, blocked ? 0 : copies, blocked ? 0 : copyBytes,
            removals, removeBytes, recovery, blocked);
    }

    private static async IAsyncEnumerable<Entry> TargetItemsAsync(ISide side, Entry root,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return root;
        if (!root.IsFolder || root.Skip is not null) yield break;
        await foreach (var listed in side.ReadAllAsync(root.Path, new ListOptions(true, true), ct))
            if (listed.Entry is { } child) yield return child;
    }

    private static async Task<(SyncedEntry? Saved, Scope Paths)> EndpointsAsync(PairConfig pair, Side source,
        string path, IStore store, CancellationToken ct)
    {
        var saved = await store.FindAsync(pair.PairId, source, PathNames.KeyFor(source, path), ct);
        var other = PathNames.Other(source);
        var target = saved?.PathOn(other) ?? await ItemSync.CounterpartAsync(pair, source, path, store, ct);
        return (saved, source == Side.Local ? new Scope(path, target) : new Scope(target, path));
    }

    private static async Task<bool> HeldAsync(string pairId, Scope endpoints, IStore store, CancellationToken ct)
    {
        await foreach (var notice in store.NoticesUnderAsync(pairId, endpoints, ct))
            if (notice is { Dismissed: false, Kind: NoticeKind.Tie } or { Kind: NoticeKind.Failure, Subject.Move: not null })
                return true;
        return false;
    }

    private static async Task<Row> ObserveAsync(Entry item, SyncedEntry? saved, Scope paths, ISide peer,
        CancellationToken ct)
    {
        var target = peer.Side == Side.Dropbox ? paths.DropboxPath : paths.LocalPath;
        var other = await peer.ReadOneAsync(target, ct);
        if (other?.Skip == nameof(TextKey.SkipUnreadable))
            throw new ItemFailedException(target, Text.ReadFailedOn(peer.Side), null);
        return new(saved?.LocalKey ?? PathNames.Key(paths.LocalPath), peer.Side == Side.Dropbox ? item : other,
            peer.Side == Side.Local ? item : other, saved, [], null, RowContext.Neutral with { Destination = target, Skipped = other?.Skip is not null });
    }

    private static bool Protected(Entry item, Scope endpoints, PathScope scope, PathScope protection) =>
        item.Skip is not null || protection.Excludes(endpoints.LocalPath, endpoints.DropboxPath, item.IsFolder) ||
        scope.HoldsNestedRoot(Side.Local, endpoints.LocalPath) || scope.HoldsNestedRoot(Side.Dropbox, endpoints.DropboxPath);

    private static bool Unsettled(SyncAction action) => action.Kind == ActionKind.None &&
        action.None is not (NoneReason.InSync or NoneReason.OutOfScope);

    private static bool Owed(PairConfig pair, Exclusion exclusion) =>
        pair.OwedScopes is { } scopes && scopes.Any(s => exclusion.Side == Side.Local
            ? PathNames.IsSelfOrUnder(exclusion.Path, s.LocalPath)
            : PathNames.IsSelfOrUnder(exclusion.Path, s.DropboxPath));
}
