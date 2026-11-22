using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Make fresh conflict copies on the requested receiving sides, then replace the old links atomically.</summary>
public sealed class KeepBoth
{
    public async Task<EffectOutcome> ApplyAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Row row,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct,Side? receiving=null)
    {
        var copies=await CopyAsync(settings,events,row,local,dropbox,populate,ct,receiving);
        if (copies.Outcome!=EffectOutcome.Applied) return copies.Outcome;
        if (copies.Second==null) await store.CommitKeepBothAsync(pair.PairId,row.Synced,copies.First!,copies.Notice!,ct);
        else await store.CommitKeepBothAsync(pair.PairId,row.Synced,copies.First!,copies.Second,copies.Notice!,ct);
        return EffectOutcome.Applied;
    }

    internal static async Task<(EffectOutcome Outcome,SyncedEntry? First,SyncedEntry? Second,Notice? Notice)> CopyAsync(EngineSettings settings,Action<RunEvent> events,Row row,ISide local,ISide dropbox,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct,Side? receiving=null)
    {
        if (row.Local is not {} pc || row.Dropbox is not {} db) throw new ChangedUnderneathException(Row.Path(row));
        List<NoticeItem> items=[new(Side.Local,pc.Path,pc.Id),new(Side.Dropbox,db.Path,db.Id)];
        SyncedEntry? first=null,second=null;
        if (receiving is null or Side.Dropbox)
        {
            var result=await Copy(pc,local,db.Path,dropbox);
            if (result.Outcome!=RemovalOutcome.Published) return (Effects.Incomplete(result,pc.Path),null,null,null);
            first=SyncedEntries.From(pc,result.Final!);
        }
        if (receiving is null or Side.Local)
        {
            var result=await Copy(db,dropbox,pc.Path,local);
            if (result.Outcome!=RemovalOutcome.Published) return (Effects.Incomplete(result,db.Path),null,null,null);
            var link=SyncedEntries.From(result.Final!,db) with {LocalPublished=result.LocalPublished};
            if (first==null) first=link; else second=link;
        }
        return (EffectOutcome.Applied,first,second,new Notice(0,NoticeKind.Info,TextKey.InfoKeptBoth,pc.Path,new(items,null),DateTimeOffset.UtcNow,false));

        async Task<RemovalResult> Copy(Entry source, ISide from, string basePath, ISide to)
        {
            for (int ordinal = 1; ; ordinal = checked(ordinal + 1))
            {
                ct.ThrowIfCancellationRequested();
                var label = await FreeLabelAsync(to,basePath,from.Side,source,settings,ct,ordinal);
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
                catch (PathOccupiedException ex) when (!ct.IsCancellationRequested && Effects.AtTarget(ex,to.Side,label))
                {
                    if (await to.ReadOneAsync(label,ct) == null) throw;
                    continue;
                }
                if (result.Outcome != RemovalOutcome.Published) return result;
                if (result.Final is not {} final || final.Path != label || final.IsFolder != source.IsFolder) throw new ChangedUnderneathException(label);
                if (source.IsFolder)
                {
                    var scope = from.Side == Side.Local ? new Scope(source.Path, label) : new Scope(label, source.Path);
                    await populate(scope, from.Side, new(source, from.ReadAllAsync(source.Path, new(true, false), ct)), ct);
                }
                if (!SameObservation(from.Side,source,await from.ReadOneAsync(source.Path,ct)) || !SameObservation(to.Side,final,await to.ReadOneAsync(label,ct))) throw new ChangedUnderneathException(label);
                items.Add(new(to.Side, label, final.Id));
                events(new(RunEventKind.Effect, nameof(ActionKind.KeepBoth), label, source.Path, nameof(RemovalOutcome.Published)) { Side = to.Side });
                return result;
            }
        }
    }
    internal static async Task<string> FreeLabelAsync(ISide to,string basePath,Side sourceSide,Entry source,EngineSettings settings,CancellationToken ct,int ordinal=1)
    {
        for (;;ordinal=checked(ordinal+1))
        {
            ct.ThrowIfCancellationRequested();
            var label=ConflictName.For(basePath,sourceSide,source.Modified,settings.ComputerName,settings,ordinal);
            if (await to.ReadOneAsync(label,ct)==null) return label;
        }
    }

    internal static bool SameObservation(Side side, Entry expected, Entry? seen) => seen == expected || side == Side.Local && seen != null &&
        (string.IsNullOrEmpty(seen.ContentHash) || string.IsNullOrEmpty(expected.ContentHash)) && (expected with {ContentHash=seen.ContentHash}) == seen;
}
