using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Make fresh conflict copies on the requested receiving sides, then replace the old links atomically.</summary>
public sealed class KeepBoth
{
    public async Task<EffectOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Row row, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Side? receiving = null)
    {
        if (row.Local is not {} pc || row.Dropbox is not {} db) throw new ChangedUnderneathException(Row.Path(row));
        List<NoticeItem> items = [new(Side.Local, pc.Path, pc.Id), new(Side.Dropbox, db.Path, db.Id)];
        SyncedEntry? first = null, second = null;
        if (receiving is null or Side.Dropbox)
        {
            var result = await Copy(pc, local, db.Path, dropbox);
            if (result.Outcome != RemovalOutcome.Published) return Effects.Incomplete(result);
            first = SyncedEntries.From(pc, result.Final!);
        }
        if (receiving is null or Side.Local)
        {
            var result = await Copy(db, dropbox, pc.Path, local);
            if (result.Outcome != RemovalOutcome.Published) return Effects.Incomplete(result);
            var binding = SyncedEntries.From(result.Final!, db) with { LocalPublished = result.LocalPublished };
            if (first is null) first = binding; else second = binding;
        }
        var notice = new Notice(0, NoticeKind.Info, TextKey.InfoKeptBoth, pc.Path, new(items, null), DateTimeOffset.UtcNow, false);
        if (second is null) await store.CommitKeepBothAsync(pair.PairId, row.Synced, first!, notice, ct);
        else await store.CommitKeepBothAsync(pair.PairId, row.Synced, first!, second, notice, ct);
        return EffectOutcome.Applied;

        async Task<RemovalResult> Copy(Entry source, ISide from, string basePath, ISide to)
        {
            for (int ordinal = 1; ; ordinal = checked(ordinal + 1))
            {
                ct.ThrowIfCancellationRequested();
                var label = ConflictName.For(basePath, from.Side, source.Modified, settings.ComputerName, settings, ordinal);
                if (await to.ReadOneAsync(label, ct) is not null) continue;
                RemovalResult result;
                try
                {
                    if (source.IsFolder) result = await to.CreateFolderExclusiveAsync(label, ct);
                    else
                    {
                        await using var bytes = await from.OpenReadAsync(source, ct);
                        await using var stage = await to.StageAsync(label, bytes, source, null, ct);
                        result = await to.PublishAsync(stage, null, null, ct);
                    }
                }
                catch (PathOccupiedException) when (!ct.IsCancellationRequested) { continue; }
                if (result.Outcome != RemovalOutcome.Published) return result;
                if (result.Final is not {} final || final.Path != label || final.IsFolder != source.IsFolder) throw new ChangedUnderneathException(label);
                if (source.IsFolder)
                {
                    var scope = from.Side == Side.Local ? new Scope(source.Path, label) : new Scope(label, source.Path);
                    await populate(scope, from.Side, new(source, from.ReadAllAsync(source.Path, new(true, false), ct)), ct);
                }
                if (await from.ReadOneAsync(source.Path, ct) != source || await to.ReadOneAsync(label, ct) != final) throw new ChangedUnderneathException(label);
                items.Add(new(to.Side, label, final.Id));
                events(new(RunEventKind.Effect, nameof(ActionKind.KeepBoth), label, source.Path, nameof(RemovalOutcome.Published)) { Side = to.Side });
                return result;
            }
        }
    }
}
