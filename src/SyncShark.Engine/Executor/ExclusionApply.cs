using Massdex.SyncShark.Engine.Planner;
using static Massdex.SyncShark.Engine.Executor.Exclusions;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>One exclusion apply: preserve each eligible item on the kept side, then remove the excluded root.</summary>
/// <remarks>The cursor fields (selected, row, reading, observedLink) name the failure subject when a read or effect faults.</remarks>
internal sealed class ExclusionApply(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths,
    Exclusion exclusion, ISide side, ISide peer, ISide local, ISide dropbox, IStore store,
    Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding,
    CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough)
{
    private Entry? selected;
    private Row? row;
    private Side? reading;
    private SyncedEntry? observedLink;
    private Entry root = null!;
    private SyncedEntry? initial;
    private Scope footprint = null!;
    private string to = null!;
    private string key = null!;
    private SyncedEntry? expectedBinding;
    private Entry? expectedPeer;
    private PathScope scope = null!;
    private PathScope protection = null!;
    private (Side, bool) rule;

    public async Task<(ExclusionOutcome Outcome, TextKey? Cause)> RunAsync()
    {
        reading = side.Side;
        try
        {
            var found = await side.ReadOneAsync(exclusion.Path, ct);
            if (found is null) return (ExclusionOutcome.Applied, null);
            root = found;
            (initial, footprint) = await EndpointsAsync(pair, side.Side, root.Path, store, ct);
            if (await HeldAsync(pair.PairId, footprint, store, ct)) return (ExclusionOutcome.Pending, null);
            to = peer.Side == Side.Local ? footprint.LocalPath : footprint.DropboxPath;
            key = PathNames.KeyFor(side.Side, root.Path);
            expectedBinding = initial;
            scope = paths.Without(exclusion);
            protection = paths.ProtectionWithout(exclusion);
            rule = (exclusion.Side, Owed(pair, exclusion));
            var outstanding = await PreserveItemsAsync();
            return outstanding.Outcome != ExclusionOutcome.Applied ? outstanding : await RemoveAsync();
        }
        catch (Exception fault) when (Notices.ItemFailure(pair, fault) is { } error)
        {
            await ReportAsync(error);
            return (ExclusionOutcome.Pending, error.Cause);
        }
        catch (InventoryIncompleteException inventory) when (selected is null && !ct.IsCancellationRequested)
        {
            // A partial listing cannot prove the remaining subtree empty.
            var error = inventory.InnerException as ItemFailedException ??
                new ItemFailedException(exclusion.Path, Text.ReadFailedOn(side.Side), inventory);
            if (error.Disposition == FailureDisposition.TemporaryNoEffect) throw error;
            await ReportAsync(error);
            return (ExclusionOutcome.Pending, error.Cause);
        }
    }

    // An item left owed keeps the root in place; an owed folder also skips its subtree.
    private async Task<(ExclusionOutcome Outcome, TextKey? Cause)> PreserveItemsAsync()
    {
        (ExclusionOutcome Outcome, TextKey? Cause) outstanding = (ExclusionOutcome.Applied, null);
        PrefixSet? dependent = null;
        await foreach (var item in TargetItemsAsync(side, root, ct))
        {
            if (dependent?.Covers(PathNames.KeyFor(side.Side, item.Path)) == true) continue;
            selected = item;
            row = null;
            reading = side.Side;
            (ExclusionOutcome Outcome, TextKey? Cause) outcome;
            try { outcome = await PreserveAsync(item); }
            catch (Exception fault) when (Notices.ItemFailure(pair, fault) is { } failure)
            {
                await ReportAsync(failure);
                outcome = (ExclusionOutcome.Pending, failure.Cause);
            }
            if (outcome.Outcome != ExclusionOutcome.Applied)
            {
                if (item == root) return outcome;
                if (outstanding.Outcome == ExclusionOutcome.Applied ||
                    outcome.Outcome == ExclusionOutcome.Refused && outstanding.Outcome == ExclusionOutcome.Pending ||
                    outstanding.Outcome == outcome.Outcome && outstanding.Cause is null) outstanding = outcome;
                if (item.IsFolder) (dependent ??= new()).Add(PathNames.KeyFor(side.Side, item.Path));
            }
            selected = null;
            row = null;
            reading = side.Side;
        }
        return outstanding;
    }

    private async Task<(ExclusionOutcome Outcome, TextKey? Cause)> PreserveAsync(Entry item)
    {
        var (saved, endpoints) = await EndpointsAsync(pair, side.Side, item.Path, store, ct);
        if (item == root && (saved, endpoints) != (initial, footprint)) return (ExclusionOutcome.Pending, null);
        if (await SelectAsync(item, saved, endpoints) is { } stopped) return stopped;
        if (await PlanAsync() is not { } action) return (ExclusionOutcome.Refused, null);
        if (action.Kind == ActionKind.None)
        {
            return (Unsettled(action) ? ExclusionOutcome.Pending : ExclusionOutcome.Applied, null);
        }
        return await ExecuteAsync(item, action, saved, endpoints);
    }

    // Observe the item's link and its peer row; a guard that stops the item returns its outcome instead.
    private async Task<(ExclusionOutcome Outcome, TextKey? Cause)?> SelectAsync(Entry item, SyncedEntry? saved, Scope endpoints)
    {
        observedLink = saved;
        reading = side.Side;
        if (item.Skip == nameof(TextKey.SkipUnreadable))
            throw new ItemFailedException(item.Path, Text.ReadFailedOn(side.Side), null);
        if (Protected(item, endpoints, scope, protection))
        {
            await new Notices().RaiseAsync(store, pair.PairId, new(0, NoticeKind.Failure,
                TextKey.FailExcludeHoldsProtected, exclusion.Path, new([new(side.Side, item.Path, item.Id)],
                side.Side), DateTimeOffset.UtcNow, false), ct);
            return (ExclusionOutcome.Applied, null);
        }
        if (await HeldAsync(pair.PairId, endpoints, store, ct)) return (ExclusionOutcome.Pending, null);
        if (scope.Excludes(endpoints.LocalPath, endpoints.DropboxPath, item.IsFolder)) return (ExclusionOutcome.Applied, null);
        reading = peer.Side;
        row = await ObserveAsync(item, saved, endpoints, peer, ct);
        if (item == root) expectedPeer = row.EntryOn(peer.Side);
        return null;
    }

    // Null means the decision needed a local hash the observed row cannot supply.
    private async Task<SyncAction?> PlanAsync()
    {
        var observed = row!;
        var action = Decide.For(observed, pair.FirstRun, DateTimeOffset.UtcNow, true, rule);
        if (action.Kind == ActionKind.NeedLocalHash)
        {
            if (observed.Local is not { IsFolder: false } file) return null;
            reading = Side.Local;
            row = observed with { LocalHash = await local.ContentHashAsync(file, ct) };
            action = Decide.For(row, pair.FirstRun, DateTimeOffset.UtcNow, true, rule);
        }
        return action;
    }

    private async Task<(ExclusionOutcome Outcome, TextKey? Cause)> ExecuteAsync(Entry item, SyncAction action, SyncedEntry? saved, Scope endpoints)
    {
        TextKey? effectCause = null;
        var planned = row!;
        var outcome = await new ItemLocks().RunAsync(pair, action, [], async () =>
        {
            var current = await EndpointsAsync(pair, side.Side, item.Path, store, ct);
            reading = side.Side;
            if (current != (saved, endpoints) || await side.ReadOneAsync(item.Path, ct) != item) return EffectOutcome.Pending;
            reading = peer.Side;
            if (await peer.ReadOneAsync(planned.Context.Destination!, ct) != planned.EntryOn(peer.Side))
                throw new ChangedUnderneathException(planned.Context.Destination!) { FailedSide = peer.Side };
            if (await HeldAsync(pair.PairId, endpoints, store, ct)) return EffectOutcome.Pending;
            if (!await RemovalCoveredAsync(action, local, dropbox, heldInput, coversThrough, ct))
                return EffectOutcome.Pending;
            reading = null;
            var result = await new Effects().ApplyAsync(pair, settings, events, scope, action, local, dropbox,
                store, populate, compareOldBinding, ct, heldInput, coversThrough, failedNotice: n => effectCause = n.Cause);
            if (result == EffectOutcome.Applied && item == root)
            {
                expectedBinding = await store.FindAsync(pair.PairId, side.Side, key, ct);
                reading = peer.Side;
                expectedPeer = await peer.ReadOneAsync(to, ct);
            }
            return result;
        }, ct, settings.CaseRenameSuffix);
        if (outcome == EffectOutcome.Refused) return (ExclusionOutcome.Refused, effectCause ??
            (action.Kind == ActionKind.Notice ? action.Cause : (TextKey?)null));
        if (outcome != EffectOutcome.Applied) return (ExclusionOutcome.Pending, effectCause);
        return (ExclusionOutcome.Applied, null);
    }

    // The root goes only while its link, footprint and peer still match what preservation left.
    private async Task<(ExclusionOutcome Outcome, TextKey? Cause)> RemoveAsync()
    {
        selected = root;
        reading = side.Side;
        var (binding, mapped) = await EndpointsAsync(pair, side.Side, root.Path, store, ct);
        if (mapped != footprint || binding != expectedBinding) return (ExclusionOutcome.Pending, null);
        row = Row.ForOneSide(side.Side, key, root, binding, [], null, RowContext.Neutral with { Destination = to });
        observedLink = binding;
        var deletion = new SyncAction(SyncAction.DeleteOn(side.Side),row,Side:side.Side);
        TextKey? removalCause = null;
        return await new ItemLocks().RunAsync<(ExclusionOutcome,TextKey?)>(pair, deletion, [], async () =>
        {
            if (await side.ReadOneAsync(root.Path, ct) != root ||
                await EndpointsAsync(pair, side.Side, root.Path, store, ct) != (binding, footprint) ||
                await HeldAsync(pair.PairId, footprint, store, ct)) return (ExclusionOutcome.Pending, null);
            reading = peer.Side;
            if (!scope.Excludes(mapped.LocalPath, mapped.DropboxPath, root.IsFolder) &&
                await peer.ReadOneAsync(to, ct) != expectedPeer)
                throw new ChangedUnderneathException(to) { FailedSide = peer.Side };
            if (!await RemovalCoveredAsync(deletion, local, dropbox, heldInput, coversThrough, ct))
                return (ExclusionOutcome.Pending, null);
            reading = null;
            return await new Effects().ApplyAsync(pair, settings, events, protection, deletion, local, dropbox, store,
                populate, compareOldBinding, ct, heldInput, coversThrough, failedNotice: n => removalCause = n.Cause) switch
            {
                EffectOutcome.Applied => (ExclusionOutcome.Applied, null),
                EffectOutcome.Pending => (ExclusionOutcome.Pending, removalCause),
                _ => (ExclusionOutcome.Refused, removalCause)
            };
        }, ct, settings.CaseRenameSuffix);
    }

    private async Task ReportAsync(ItemFailedException error)
    {
        // Preserve an uncertain receipt even when cancellation arrived after dispatch.
        var token = error.Disposition == FailureDisposition.UnknownEffect ? CancellationToken.None : ct;
        token.ThrowIfCancellationRequested();
        var failedSide = Notices.FailedSide(error) ?? reading;
        var entry = failedSide is { } physical ? row?.EntryOn(physical) : null;
        var id = entry is not null && entry.Path == error.Path ? entry.Id :
            failedSide == side.Side && selected is not null && selected.Path == error.Path ? selected.Id : null;
        if (id is null && failedSide is { } known && observedLink is not null && observedLink.PathOn(known) == error.Path)
            id = observedLink.IdOn(known);
        var subject = new IssueSubject(failedSide is { } endpoint && id is not null ? [new(endpoint,error.Path,id)] : [],failedSide);
        await ItemSync.ReportFailureAsync(pair, store, events, error, subject, PathNames.Key(error.Path), false, null, token);
        ct.ThrowIfCancellationRequested();
    }
}
