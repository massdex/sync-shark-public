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
        if (choice == ResolutionChoice.Dismiss)
        {
            // A PutBack notice is settled only by its own Retry or a later change of the same item (R8/R24); Dismiss
            // never closes it, whether the path is absent or holds another file.
            if (stored.Kind == NoticeKind.PutBack) return;
            await store.CloseNoticeAsync(pair.PairId, stored, ct);
            return;
        }
        if (choice is ResolutionChoice.Ignore or ResolutionChoice.Unignore)
        {
            // Visibility only, inside the reviewed identity: re-read and require the same slot before writing the flag,
            // so a newer PutBack that landed meanwhile is not overwritten by this action.
            var current = await store.NoticeAsync(pair.PairId, expected.Id, ct);
            if (current is null || !current.Matches(stored)) return;
            await store.SaveNoticeAsync(pair.PairId, current with { Ignored = choice == ResolutionChoice.Ignore }, ct);
            return;
        }
        // Retry settles its own failed attempt through the retry owner; it is not a file choice for this planner.
        if (choice == ResolutionChoice.Retry) return;
        // Every selected endpoint must still be the reviewed identity (stale or a replacement occupant leaves it open).
        if (!await SubjectsStillMatchAsync(stored, local, dropbox, ct)) return;
        // Plan the at-most-three conditional actions against the two binding rows and run them in order through the
        // shared effect owner; the notice closes only when every action reports Applied (R31).
        var actions = Planner.Resolutions.For(stored, choice, name,
            await RowForSideAsync(pair, stored, Side.Local, local, store, ct),
            await RowForSideAsync(pair, stored, Side.Dropbox, dropbox, store, ct));
        foreach (var action in actions)
        {
            var outcome = await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store,
                populate, compareOldBinding, ct);
            if (outcome != EffectOutcome.Applied) return;
        }
        await store.CloseNoticeAsync(pair.PairId, stored, ct);
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

    // The binding row of one side: the first subject item on that side, observed with its saved binding.
    private static async Task<Row> RowForSideAsync(PairConfig pair, Notice notice, Side side, ISide endpoint,
        IStore store, CancellationToken ct)
    {
        var path = notice.Subject.Items.FirstOrDefault(item => item.Side == side)?.Path ?? notice.Path;
        var entry = await endpoint.ReadOneAsync(path, ct);
        var key = PathNames.KeyFor(side, path);
        var saved = await store.FindAsync(pair.PairId, side, key, ct);
        return Row.ForOneSide(side, key, entry, saved, [], null, RowContext.Neutral);
    }
}
