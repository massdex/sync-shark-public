using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Make fresh conflict copies on the requested receiving sides, then replace the old links atomically.</summary>
public sealed class KeepBoth
{
    public Task<EffectOutcome> ApplyAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,PathScope paths,Row row,ISide local,ISide dropbox,IStore store,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,CancellationToken ct,Side? receiving=null) =>
        RunCopiesAsync(pair,settings,events,row,local,dropbox,populate,receiving,async (first,second,notice)=>
        {
            if (second==null) await store.CommitKeepBothAsync(pair.PairId,row.Synced,first,notice,ct);
            else await store.CommitKeepBothAsync(pair.PairId,row.Synced,first,second,notice,ct);
            return EffectOutcome.Applied;
        },ct);

    // Keep each chosen label in the existing physical grant until its links and notice are committed.
    internal static Task<EffectOutcome> RunCopiesAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,Row row,ISide local,ISide dropbox,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Side? receiving,Func<SyncedEntry,SyncedEntry?,Notice,Task<EffectOutcome>> settle,CancellationToken ct)
    {
        if (row.Local is not {} pc || row.Dropbox is not {} db) throw new ChangedUnderneathException(Row.Path(row));
        List<NoticeItem> items=[new(Side.Local,pc.Path,pc.Id),new(Side.Dropbox,db.Path,db.Id)];
        var from=receiving==Side.Local?dropbox:local; var to=receiving==Side.Local?local:dropbox;
        return StageCopyAsync(pair,settings,events,row,from,to,items,null,populate,receiving,settle,ct);
    }

    private static async Task<EffectOutcome> StageCopyAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,Row row,ISide from,ISide to,List<NoticeItem> items,SyncedEntry? previous,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Side? receiving,Func<SyncedEntry,SyncedEntry?,Notice,Task<EffectOutcome>> settle,CancellationToken ct)
    {
        var source=row.EntryOn(from.Side)!; var basePath=row.EntryOn(to.Side)!.Path;
        for(int ordinal=1;;ordinal=checked(ordinal+1))
        {
            ct.ThrowIfCancellationRequested();
            var label=ConflictName.For(basePath,from.Side,source.Modified,settings.ComputerName,settings,ordinal);
            var reuse=await Effects.ReadOneAsync(to,label,ct);
            if(reuse!=null && (source.IsFolder || reuse.IsFolder || reuse.Skip!=null || !StringComparer.OrdinalIgnoreCase.Equals(await to.ContentHashAsync(reuse,ct),await from.ContentHashAsync(source,ct)))) continue;
            var outcome=await new ItemLocks().RunAsync<EffectOutcome?>(pair,new(ActionKind.KeepBoth,row,Target:label,Side:to.Side),[],
                ()=>PublishCopyAsync(source,from,label,to,reuse,populate,Finish,ct),ct);
            if(outcome.HasValue) return outcome.Value;
        }

        async Task<EffectOutcome> Finish(Entry final,SyncedEntry link)
        {
            items.Add(new(to.Side,final.Path,final.Id));
            events(new(RunEventKind.Effect,nameof(ActionKind.KeepBoth),final.Path,source.Path,nameof(RemovalOutcome.Published)){Side=to.Side});
            if(receiving==null && previous==null) return await StageCopyAsync(pair,settings,events,row,to,from,items,link,populate,receiving,settle,ct);
            var notice=new Notice(0,NoticeKind.Info,TextKey.InfoKeptBoth,row.Local!.Path,new(items,null),DateTimeOffset.UtcNow,false);
            return await settle(previous??link,previous==null?null:link,notice);
        }
    }

    private static async Task<EffectOutcome?> PublishCopyAsync(Entry source,ISide from,string label,ISide to,Entry? reuse,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<Entry,SyncedEntry,Task<EffectOutcome>> finish,CancellationToken ct)
    {
        RemovalResult result;
        try
        {
            if(reuse!=null) result=new(RemovalOutcome.Published,reuse,null);
            else if(source.IsFolder) result=await to.CreateFolderExclusiveAsync(label,ct);
            else
            {
                await using var bytes=await Effects.OpenReadAsync(from,source,ct);
                await using var stage=await to.StageAsync(label,bytes,source,null,ct);
                result=await to.PublishAsync(stage,null,null,ct);
            }
        }
        catch(PathOccupiedException ex) when(!ct.IsCancellationRequested && RemovalReceipts.AtTarget(ex,to.Side,label))
        {
            if(await Effects.ReadOneAsync(to,label,ct)==null) throw;
            return null;
        }
        if(result.Outcome!=RemovalOutcome.Published) return RemovalReceipts.Incomplete(result,label);
        if(result.Final is not {} final || final.Path!=label || final.IsFolder!=source.IsFolder) throw new ChangedUnderneathException(label){FailedSide=to.Side};
        if(source.IsFolder) await populate(from.Side==Side.Local?new(source.Path,label):new(label,source.Path),from.Side,new(source,from.ReadAllAsync(source.Path,new(true,false),ct)),ct);
        if(!SameObservation(from.Side,source,await Effects.ReadOneAsync(from,source.Path,ct))) throw new ChangedUnderneathException(source.Path){FailedSide=from.Side};
        if(!SameObservation(to.Side,final,await Effects.ReadOneAsync(to,label,ct))) throw new ChangedUnderneathException(label){FailedSide=to.Side};
        return await finish(final,SyncedEntries.From(to.Side,final,source) with {LocalPublished=result.LocalPublished});
    }

    internal static bool SameObservation(Side side, Entry expected, Entry? seen) => seen == expected || side == Side.Local && seen != null &&
        (string.IsNullOrEmpty(seen.ContentHash) || string.IsNullOrEmpty(expected.ContentHash)) && (expected with {ContentHash=seen.ContentHash}) == seen;
}
