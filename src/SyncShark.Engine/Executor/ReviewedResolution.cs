namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Apply one reviewed choice, retaining its observations and the subject of any failed read or effect.</summary>
internal sealed class ReviewedResolution(PairConfig pair, EngineSettings settings, Action<RunEvent> events,
    PathScope paths, Notice stored, ISide local, ISide dropbox, IStore store,
    Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,
    Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct,
    Func<Scope,IEnumerable<Change>>? heldInput)
{
    private readonly Dictionary<(Side, string), Entry?> observed = new();
    private Side? selected;
    private Planner.SyncAction? executing;

    /// <summary>Apply each planned effect against the state left by the preceding effects.</summary>
    public async Task<EffectOutcome> ApplyChoiceAsync(ResolutionChoice choice, string? name)
    {
        try
        {
            var pc = await BindingRowAsync(Side.Local);
            var cloud = await BindingRowAsync(Side.Dropbox);
            var boundFolder = false;
            if (choice == ResolutionChoice.ReplaceExisting && stored.Subject.Incoming is { } incoming &&
                stored.Subject.Items.FirstOrDefault(i => i.Side == incoming) is { } incomingItem &&
                observed[(incoming, incomingItem.Path)] is { IsFolder: true } current && current.Id == incomingItem.Id)
            {
                await foreach (var saved in store.ByIdAsync(pair.PairId, incoming, incomingItem.Id, ct))
                    if (saved.IsFolder) { boundFolder = true; break; }
            }
            var appliedDeletes = new List<Planner.SyncAction>();
            foreach (var planned in Planner.Resolutions.For(stored, choice, name, pc, cloud, boundFolder))
            {
                var action = AfterDeletes(planned, appliedDeletes);
                var outcome = await new ItemLocks().RunAsync(pair, action, [], () => ApplyReviewedAsync(planned, action),
                    ct, settings.CaseRenameSuffix);
                executing = null;
                if (outcome != EffectOutcome.Applied) return EffectOutcome.Refused;
                if (action.Kind is ActionKind.DeleteLocal or ActionKind.DeleteDropbox) appliedDeletes.Add(action);
            }
            return EffectOutcome.Applied;
        }
        catch (Exception fault) when (Notices.ItemFailure(pair, fault) is { } error)
        {
            await ReportFailureAsync(error);
            ct.ThrowIfCancellationRequested();
            return EffectOutcome.Refused;
        }
    }

    private Task ReportFailureAsync(ItemFailedException error)
    {
        var token = error.Disposition == FailureDisposition.UnknownEffect ? CancellationToken.None : ct;
        token.ThrowIfCancellationRequested();
        var side = Notices.FailedSide(error) ?? selected;
        var entry = side is { } physical ? executing?.Row.EntryOn(physical) : null;
        var member = entry is not null && entry.Path == error.Path ? new NoticeItem(side!.Value, entry.Path, entry.Id) :
            executing is null ? stored.Subject.Items.FirstOrDefault(i => i.Side == side && i.Path == error.Path) : null;
        var key = side is { } endpoint ? PathNames.KeyFor(endpoint, error.Path) : PathNames.Key(error.Path);
        return ItemSync.ReportFailureAsync(pair, store, events, error, new(member is null ? [] : [member], side),
            key, false, null, token);
    }

    private async Task<Entry?> ReadAsync(Side side, string path)
    {
        if (!observed.TryGetValue((side, path), out var entry))
        {
            selected = side;
            entry = await (side == Side.Local ? local : dropbox).ReadOneAsync(path, ct);
            if (entry?.Skip == nameof(TextKey.SkipUnreadable))
                throw new ItemFailedException(path, Text.ReadFailedOn(side), null);
            observed[(side, path)] = entry;
        }
        return entry;
    }

    private async Task<Row> BindingRowAsync(Side side)
    {
        using var members = stored.Subject.Items.Where(i => i.Side == side).GetEnumerator();
        var first = members.MoveNext() ? members.Current : null;
        var key = PathNames.KeyFor(side, first?.Path ?? stored.Path);
        var saved = first is null ? null : await store.FindAsync(pair.PairId, side, key, ct);
        if (saved is not null && saved.IdOn(side) != first!.Id) saved = null;
        var own = first is null ? null : await ReadAsync(side, first.Path);
        var opposite = PathNames.Other(side);
        var peer = saved is null ? null : await ReadAsync(opposite, saved.PathOn(opposite));
        var extra = new List<Entry>();
        while (members.MoveNext())
            if (await ReadAsync(side, members.Current.Path) is { } entry) extra.Add(entry);
        return new Row(saved?.LocalKey ?? key, side == Side.Local ? own : peer,
            side == Side.Dropbox ? own : peer, saved, extra, null, RowContext.Neutral);
    }

    // This runs under the item lock; the planning observations cannot authorize a later effect by themselves.
    private async Task<EffectOutcome> ApplyReviewedAsync(Planner.SyncAction planned, Planner.SyncAction action)
    {
        if (await store.NoticeAsync(pair.PairId, stored.Id, ct) is not { } current || !current.Matches(stored))
            throw Resolve.Stale(stored.Id);
        foreach (var side in new[]{Side.Local, Side.Dropbox})
        {
            if (planned.Row.EntryOn(side) is not { } reviewedEntry) continue;
            var entry = action.Row.EntryOn(side);
            selected = side;
            if (await (side == Side.Local ? local : dropbox).ReadOneAsync(reviewedEntry.Path, ct) != entry ||
                (entry is null && await store.FindAsync(pair.PairId, side, PathNames.KeyFor(side, reviewedEntry.Path), ct) is not null))
                throw new EngineRequestException(TextKey.ResolveSubjectStale);
        }
        if (planned.Row.Synced is { } link && await store.FindAsync(pair.PairId, Side.Local, link.LocalKey, ct) != action.Row.Synced)
            throw new EngineRequestException(TextKey.ResolveSubjectStale);
        foreach(var side in new[]{Side.Local,Side.Dropbox})
        {
            bool Kept(string? path) => path!=null && (action.Row.EntryOn(side)?.IsFolder==true ? paths.KeepOverlaps(side,PathNames.KeyFor(side,path)) : paths.KeepCovers(side,PathNames.KeyFor(side,path)));
            if(Kept(action.Row.EntryOn(side)?.Path) || Kept(action.Row.Synced?.PathOn(side)) ||
                action.Target is {} target && (action.Kind==ActionKind.KeepBoth ? action.Side==null || action.Side==side : action.WrittenSide()==side) && Kept(target))
                throw new EngineRequestException(TextKey.ResolveSubjectStale);
        }
        executing = action;
        selected = null;
        return await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store,
            populate, compareOldBinding, ct, heldInput, userSelected: action.Kind is ActionKind.DeleteLocal or ActionKind.DeleteDropbox or ActionKind.ReplaceKind ||
                (action.Kind is ActionKind.Upload or ActionKind.Download && action.Row.EntryOn(action.WrittenSide()) is not null));
    }

    /// <summary>Project a later leg against the reviewed deletions already committed by this choice.</summary>
    private static Planner.SyncAction AfterDeletes(Planner.SyncAction planned, IReadOnlyList<Planner.SyncAction> appliedDeletes)
    {
        var row = planned.Row;
        foreach (var deletion in appliedDeletes)
        {
            var side = deletion.WrittenSide();
            if (deletion.Row.EntryOn(side) is { } removed && row.EntryOn(side) == removed)
            {
                row = side == Side.Local ? row with { Local = null } : row with { Dropbox = null };
                row = row with { Context = row.Context with { Destination = removed.Path } };
            }
            if (row.Synced is not null && row.Synced == deletion.Row.Synced) row = row with { Synced = null };
        }
        return ReferenceEquals(row, planned.Row) ? planned : planned with { Row = row };
    }
}
