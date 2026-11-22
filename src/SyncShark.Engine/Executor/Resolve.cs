namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Resolve operations for the executor module: validate a reviewed notice's selected endpoints together, run
/// its bounded action list through the shared effect owner, and close only on a confirmed applied outcome (R14, R31).</summary>
public sealed class Resolve
{
    /// <summary>Apply the choice to the stored, reviewed notice; a stale token or endpoint acts on nothing.</summary>
    public async Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice expected, ResolutionChoice choice, string? name, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Guard every choice on the stored row still matching the reviewed token (BC16); a stale token acts on nothing.
        var stored = await store.NoticeAsync(pair.PairId, expected.Id, ct);
        if (stored is null || !stored.Matches(expected)) return;
        var notices = new Notices();
        if (choice == ResolutionChoice.Dismiss)
        {
            // Only kinds the choice table lets Dismiss close; RootIdentity, DriveChange and PutBack are never dismissed
            // (M12/product-0107; R8/R24). PutBack is settled only by its own Retry or a later change of the same item.
            if (stored.Kind is NoticeKind.RootIdentity or NoticeKind.DriveChange) return; // DELIBERATE RED MUTATION: Dismiss settles a PutBack
            await notices.CloseAsync(store, pair.PairId, stored, ct);
            return;
        }
        if (choice is ResolutionChoice.Ignore or ResolutionChoice.Unignore)
        {
            // Visibility only, inside the reviewed identity: re-read and require the same slot before writing the flag.
            // The exact-subject conditional visibility update is the notice owner's (keeper request; see REPORT).
            var current = await store.NoticeAsync(pair.PairId, expected.Id, ct);
            if (current is null || !current.Matches(stored)) return;
            await notices.RaiseAsync(store, pair.PairId, current with { Ignored = choice == ResolutionChoice.Ignore }, ct);
            return;
        }
        // Retry is settled by its own owner (W16 retry/ItemSync) after the Host routes it; it is not a file choice here.
        if (choice == ResolutionChoice.Retry) return;
        // Every selected endpoint must still be the reviewed identity (stale or a replacement occupant leaves it open).
        if (!await SubjectsStillMatchAsync(stored, local, dropbox, ct)) return;
        // The bound-incoming fact for a replace-existing choice, from the incoming item's saved binding.
        var incomingBound = choice == ResolutionChoice.ReplaceExisting &&
            stored.Subject.Incoming is { } incoming &&
            stored.Subject.Items.FirstOrDefault(item => item.Side == incoming) is { } incomingItem &&
            await store.FindAsync(pair.PairId, incoming, PathNames.KeyFor(incoming, incomingItem.Path), ct) is { IsFolder: true };
        // Plan the at-most-three conditional actions against the two binding rows (both endpoints, their binding and the
        // extra members on each side) and run them in order; the notice closes only when every action reports Applied.
        var actions = Planner.Resolutions.For(stored, choice, name,
            await RowForSideAsync(pair, stored, Side.Local, local, dropbox, store, ct),
            await RowForSideAsync(pair, stored, Side.Dropbox, local, dropbox, store, ct), incomingBound);
        foreach (var action in actions)
        {
            var outcome = await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store,
                populate, compareOldBinding, ct);
            if (outcome != EffectOutcome.Applied) return;
        }
        await notices.CloseAsync(store, pair.PairId, stored, ct);
    }

    // Each subject item is re-observed on its own side and must still carry the reviewed identity; a missing or
    // replaced endpoint is stale and authorizes no action.
    private static async Task<bool> SubjectsStillMatchAsync(Notice notice, ISide local, ISide dropbox,
        CancellationToken ct)
    {
        if (notice.Subject.Items.Count == 0) return false;
        foreach (var item in notice.Subject.Items)
        {
            var side = item.Side == Side.Local ? local : dropbox;
            var entry = await side.ReadOneAsync(item.Path, ct);
            if (entry is null || entry.Id != item.Id) return false;
        }
        return true;
    }

    // The binding row of one side: the first subject item on that side observed with its saved counterpart (so both
    // endpoints are present) and the saved binding, with the remaining members on that side as Extra.
    private static async Task<Row> RowForSideAsync(PairConfig pair, Notice notice, Side side, ISide local,
        ISide dropbox, IStore store, CancellationToken ct)
    {
        var items = notice.Subject.Items.Where(item => item.Side == side).ToArray();
        var path = items.FirstOrDefault()?.Path ?? notice.Path;
        var key = PathNames.KeyFor(side, path);
        var saved = await store.FindAsync(pair.PairId, side, key, ct);
        var counterpart = saved?.PathOn(PathNames.Other(side)) ?? path;
        var own = await (side == Side.Local ? local : dropbox).ReadOneAsync(path, ct);
        var other = await (side == Side.Local ? dropbox : local).ReadOneAsync(counterpart, ct);
        var localEntry = side == Side.Local ? own : other;
        var dropboxEntry = side == Side.Dropbox ? own : other;
        var extra = new List<Entry>();
        foreach (var member in items.Skip(1))
        {
            var entry = await (member.Side == Side.Local ? local : dropbox).ReadOneAsync(member.Path, ct);
            if (entry is not null) extra.Add(entry);
        }
        return new Row(saved?.LocalKey ?? PathNames.Key(localEntry?.Path ?? path), localEntry, dropboxEntry, saved, extra,
            null, RowContext.Neutral with { Destination = counterpart });
    }
}
