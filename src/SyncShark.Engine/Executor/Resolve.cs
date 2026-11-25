namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Observe reviewed endpoints together and close the notice only after all selected effects settle.</summary>
public sealed class Resolve
{
    /// <summary>Apply the choice to the stored, reviewed notice; a stale token or endpoint acts on nothing.</summary>
    public async Task<EffectOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice expected, ResolutionChoice choice, string? name, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput = null)
    {
        ct.ThrowIfCancellationRequested();
        if (choice is ResolutionChoice.Retry or ResolutionChoice.Ignore or ResolutionChoice.Unignore) return EffectOutcome.Refused;
        using var click = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = click.Token;
        var stored = await store.NoticeAsync(pair.PairId, expected.Id, ct);
        if (stored is null || !stored.Matches(expected))
            throw Stale(expected.Id);
        if (choice == ResolutionChoice.Dismiss)
        {
            Planner.Resolutions.For(stored, choice);
        }
        else
        {
            var resolution = new ReviewedResolution(pair, settings, events, paths, stored, local, dropbox, store,
                populate, compareOldBinding, ct, heldInput);
            if (await resolution.ApplyChoiceAsync(choice, name) != EffectOutcome.Applied) return EffectOutcome.Refused;
        }
        if (!await new Notices().CloseAsync(store, pair.PairId, stored, ct)) throw Stale(expected.Id);
        return EffectOutcome.Applied;
    }

    internal static EngineRequestException Stale(long id) => new(TextKey.ResolveStale, (TextArg.Id, id.ToString()));
}
