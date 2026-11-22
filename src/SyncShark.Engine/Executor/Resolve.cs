namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Observe reviewed endpoints together and close the notice only after all selected effects settle.</summary>
public sealed class Resolve
{
    /// <summary>Apply the choice to the stored, reviewed notice; a stale token or endpoint acts on nothing.</summary>
    public async Task<EffectOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice expected, ResolutionChoice choice, string? name, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (choice is ResolutionChoice.Retry or ResolutionChoice.Ignore or ResolutionChoice.Unignore) return EffectOutcome.Refused;
        var stored = await store.NoticeAsync(pair.PairId, expected.Id, ct);
        if (stored is null || !stored.Matches(expected))
            throw new EngineRequestException(TextKey.ResolveStale, (TextArg.Id, expected.Id.ToString()));
        if (choice == ResolutionChoice.Dismiss)
        {
            Planner.Resolutions.For(stored, choice);
        }
        else
        {
            Side? selected = stored.Subject.Incoming;
            try
            {
                var observed = new Dictionary<(Side, string), Entry?>();
                async Task<Entry?> Read(Side side, string path)
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
                async Task<Row> BindingRow(Side side)
                {
                    var members = stored.Subject.Items.Where(i => i.Side == side).ToArray();
                    var first = members.FirstOrDefault();
                    var key = PathNames.KeyFor(side, first?.Path ?? stored.Path);
                    var saved = first is null ? null : await store.FindAsync(pair.PairId, side, key, ct);
                    if (saved is not null && saved.IdOn(side) != first!.Id) saved = null;
                    var own = first is null ? null : await Read(side, first.Path);
                    var opposite = PathNames.Other(side);
                    var peer = saved is null ? null : await Read(opposite, saved.PathOn(opposite));
                    var extra = new List<Entry>();
                    foreach (var member in members.Skip(1))
                        if (await Read(side, member.Path) is { } entry) extra.Add(entry);
                    return new Row(saved?.LocalKey ?? key, side == Side.Local ? own : peer,
                        side == Side.Dropbox ? own : peer, saved, extra, null, RowContext.Neutral);
                }
                var pc = await BindingRow(Side.Local);
                var cloud = await BindingRow(Side.Dropbox);
                var boundFolder = false;
                if (choice == ResolutionChoice.ReplaceExisting && stored.Subject.Incoming is { } incoming &&
                    stored.Subject.Items.FirstOrDefault(i => i.Side == incoming) is { } incomingItem &&
                    observed[(incoming, incomingItem.Path)] is { IsFolder: true } current && current.Id == incomingItem.Id)
                {
                    await foreach (var saved in store.ByIdAsync(pair.PairId, incoming, incomingItem.Id, ct))
                        if (saved.IsFolder) { boundFolder = true; break; }
                }
                foreach (var action in Planner.Resolutions.For(stored, choice, name, pc, cloud, boundFolder))
                {
                    selected = action.Side ?? stored.Subject.Incoming;
                    var outcome = await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store,
                        populate, compareOldBinding, ct);
                    if (outcome != EffectOutcome.Applied) return outcome;
                }
            }
            catch (ItemFailedException error) when (error.Disposition != FailureDisposition.TemporaryNoEffect)
            {
                var token = error.Disposition == FailureDisposition.UnknownEffect ? CancellationToken.None : ct;
                token.ThrowIfCancellationRequested();
                var side = error.Move is not null ? Side.Dropbox : selected ?? stored.Subject.EndpointSide(error.Path);
                var key = side is { } endpoint ? PathNames.KeyFor(endpoint, error.Path) : PathNames.Key(error.Path);
                await ItemSync.ReportFailureAsync(pair, store, events, error, stored.Subject with { Incoming = side },
                    key, false, null, token);
                ct.ThrowIfCancellationRequested();
                return error.Disposition == FailureDisposition.UnknownEffect ? EffectOutcome.Pending : EffectOutcome.Refused;
            }
        }
        await new Notices().CloseAsync(store, pair.PairId, stored, ct);
        return EffectOutcome.Applied;
    }
}
