namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Resolve operations for the executor module: plan one reviewed notice's choice, run its bounded action
/// list through the shared effects owner, then close the notice on success (M12, BC16).</summary>
public sealed class Resolve
{
    /// <summary>Apply the choice to the stored, reviewed notice; a stale token or row does nothing.</summary>
    public async Task ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice expected, ResolutionChoice choice, string? name, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Guard every choice on the stored row still matching the reviewed token (BC16); a stale token acts on nothing.
        var stored = await store.NoticeAsync(pair.PairId, expected.Id, ct);
        if (stored is null || !stored.Matches(expected)) return;
        // Row-free choices: Dismiss closes, Ignore hides, Unignore restores. None plans a sync action (old 39-44).
        if (choice == ResolutionChoice.Dismiss)
        {
            await store.CloseNoticeAsync(pair.PairId, stored, ct);
            return;
        }
        if (choice is ResolutionChoice.Ignore or ResolutionChoice.Unignore)
        {
            await store.SaveNoticeAsync(pair.PairId, stored with { Ignored = choice == ResolutionChoice.Ignore }, ct);
            return;
        }
        var row = await ObserveAsync(pair, stored, local, dropbox, store, ct);
        var action = Planner.Resolutions.For(stored, choice, row, name);
        await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store, populate,
            compareOldBinding, ct);
        await store.CloseNoticeAsync(pair.PairId, stored, ct);
    }

    // The two endpoint rows the choice acts on, observed fresh; the saved binding supplies each side's key.
    private static async Task<Row> ObserveAsync(PairConfig pair, Notice notice, ISide local, ISide dropbox,
        IStore store, CancellationToken ct)
    {
        var path = notice.Subject.Items.FirstOrDefault()?.Path ?? notice.Path;
        var entryLocal = await local.ReadOneAsync(path, ct);
        var entryDropbox = await dropbox.ReadOneAsync(path, ct);
        var key = PathNames.KeyFor(Side.Local, path);
        var saved = await store.FindAsync(pair.PairId, Side.Local, key, ct);
        return new Row(key, entryLocal, entryDropbox, saved, [], null, RowContext.Neutral);
    }
}
