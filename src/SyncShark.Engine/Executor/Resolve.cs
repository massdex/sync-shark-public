namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Observe reviewed endpoints together and close the notice only after all selected effects settle.</summary>
public sealed class Resolve
{
    /// <summary>Apply the choice to the stored, reviewed notice; a stale token or endpoint acts on nothing.</summary>
    public async Task<EffectOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Notice expected, ResolutionChoice choice, string? name, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Func<Scope,IEnumerable<Change>>? heldInput = null)
    {
        ct.ThrowIfCancellationRequested();
        if (choice is ResolutionChoice.Retry or ResolutionChoice.Ignore or ResolutionChoice.Unignore) return EffectOutcome.Refused;
        var stored = await store.NoticeAsync(pair.PairId, expected.Id, ct);
        if (stored is null || !stored.Matches(expected))
            throw Stale();
        if (choice == ResolutionChoice.Dismiss)
        {
            Planner.Resolutions.For(stored, choice);
        }
        else
        {
            Side? selected = null;
            string? selectedPath = null;
            Planner.SyncAction? executing = null;
            try
            {
                var observed = new Dictionary<(Side, string), Entry?>();
                async Task<Entry?> Read(Side side, string path)
                {
                    if (!observed.TryGetValue((side, path), out var entry))
                    {
                        selected = side;
                        selectedPath = path;
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
                var appliedDeletes = new List<Planner.SyncAction>();
                foreach (var planned in Planner.Resolutions.For(stored, choice, name, pc, cloud, boundFolder))
                {
                    var action = AfterDeletes(planned, appliedDeletes);
                    var outcome = await new ItemLocks().RunAsync(pair, action, [], async () =>
                    {
                        if (await store.NoticeAsync(pair.PairId, stored.Id, ct) is not { } current || !current.Matches(stored))
                            throw Stale();
                        foreach (var side in new[]{Side.Local, Side.Dropbox})
                        {
                            if (action.Row.EntryOn(side) is not { } entry) continue;
                            selected = side;
                            selectedPath = entry.Path;
                            if (await (side == Side.Local ? local : dropbox).ReadOneAsync(entry.Path, ct) != entry)
                                throw new EngineRequestException(TextKey.ResolveSubjectStale);
                        }
                        if (action.Row.Synced is { } link && await store.FindAsync(pair.PairId, Side.Local, link.LocalKey, ct) != link)
                            throw new EngineRequestException(TextKey.ResolveSubjectStale);
                        executing = action;
                        selected = null;
                        return await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store,
                            populate, compareOldBinding, ct, heldInput);
                    }, ct, settings.CaseRenameSuffix);
                    executing = null;
                    if (outcome != EffectOutcome.Applied) return outcome;
                    if (action.Kind is ActionKind.DeleteLocal or ActionKind.DeleteDropbox) appliedDeletes.Add(action);
                }
            }
            catch (Exception fault) when (Notices.ItemFailure(pair, fault) is { } error)
            {
                var token = error.Disposition == FailureDisposition.UnknownEffect ? CancellationToken.None : ct;
                token.ThrowIfCancellationRequested();
                var side = executing is null ? selected : fault switch
                {
                    PathOccupiedException { CollidingSide: { } collision } => collision,
                    ChangedUnderneathException { ObservedLocal: not null } => Side.Local,
                    _ when error.Move is not null || error.Cause == TextKey.ReadDropboxFailed => Side.Dropbox,
                    _ when error.LocalId is not null || error.Holder is not null || error.Cause is TextKey.ReadLocalFailed or
                        TextKey.FailLocalInUse or TextKey.FailLocalInUseOrAccessDenied or TextKey.FailLocalNoSpace => Side.Local,
                    _ => executing.Kind == ActionKind.KeepBoth ? executing.Side : executing.WrittenSide()
                };
                var entry = side is { } physical ? executing?.Row.EntryOn(physical) : null;
                var member = entry is not null ? new NoticeItem(side!.Value, entry.Path, entry.Id) :
                    stored.Subject.Items.FirstOrDefault(i => i.Side == side && i.Path == (executing is null ? selectedPath : error.Path));
                var key = side is { } endpoint ? PathNames.KeyFor(endpoint, error.Path) : PathNames.Key(error.Path);
                await ItemSync.ReportFailureAsync(pair, store, events, error, new(member is null ? [] : [member], side),
                    key, false, null, token);
                ct.ThrowIfCancellationRequested();
                return error.Disposition == FailureDisposition.UnknownEffect ? EffectOutcome.Pending : EffectOutcome.Refused;
            }
        }
        if (!await new Notices().CloseAsync(store, pair.PairId, stored, ct)) throw Stale();
        return EffectOutcome.Applied;

        EngineRequestException Stale() => new(TextKey.ResolveStale, (TextArg.Id, expected.Id.ToString()));
    }
    /// <summary>Project a later leg against the reviewed deletions already committed by this choice.</summary>
    public static Planner.SyncAction AfterDeletes(Planner.SyncAction planned, IReadOnlyList<Planner.SyncAction> appliedDeletes) => planned;
}
