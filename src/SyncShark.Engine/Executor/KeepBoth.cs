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
        return receiving==Side.Local?Copy(db,dropbox,pc.Path,local,null):Copy(pc,local,db.Path,dropbox,null);

        async Task<EffectOutcome> Copy(Entry source,ISide from,string basePath,ISide to,SyncedEntry? previous)
        {
            for(int ordinal=1;;ordinal=checked(ordinal+1))
            {
                ct.ThrowIfCancellationRequested();
                var label=ConflictName.For(basePath,from.Side,source.Modified,settings.ComputerName,settings,ordinal);
                if (await Effects.ReadOneAsync(to,label,ct)!=null) continue;
                var outcome=await new ItemLocks().RunAsync<EffectOutcome?>(pair,new(ActionKind.KeepBoth,row,Target:label,Side:to.Side),[],UnderGrant,ct);
                if (outcome.HasValue) return outcome.Value;

                async Task<EffectOutcome?> UnderGrant()
                {
                    RemovalResult result;
                    try
                    {
                        if (source.IsFolder) result=await to.CreateFolderExclusiveAsync(label,ct);
                        else
                        {
                            await using var bytes=await Effects.OpenReadAsync(from,source,ct);
                            await using var stage=await to.StageAsync(label,bytes,source,null,ct);
                            result=await to.PublishAsync(stage,null,null,ct);
                        }
                    }
                    catch(PathOccupiedException ex) when(!ct.IsCancellationRequested && Effects.AtTarget(ex,to.Side,label))
                    {
                        if(await Effects.ReadOneAsync(to,label,ct)==null) throw;
                        return null;
                    }
                    if (result.Outcome!=RemovalOutcome.Published) return Effects.Incomplete(result,label);
                    if (result.Final is not {} final || final.Path!=label || final.IsFolder!=source.IsFolder) throw new ChangedUnderneathException(label){FailedSide=to.Side};
                    if(source.IsFolder) await populate(from.Side==Side.Local?new(source.Path,label):new(label,source.Path),from.Side,new(source,from.ReadAllAsync(source.Path,new(true,false),ct)),ct);
                    if(!SameObservation(from.Side,source,await Effects.ReadOneAsync(from,source.Path,ct))) throw new ChangedUnderneathException(source.Path){FailedSide=from.Side};
                    if(!SameObservation(to.Side,final,await Effects.ReadOneAsync(to,label,ct))) throw new ChangedUnderneathException(label){FailedSide=to.Side};
                    items.Add(new(to.Side,label,final.Id));
                    events(new(RunEventKind.Effect,nameof(ActionKind.KeepBoth),label,source.Path,nameof(RemovalOutcome.Published)){Side=to.Side});
                    var link=SyncedEntries.From(to.Side,final,source) with {LocalPublished=result.LocalPublished};
                    if(receiving==null && to.Side==Side.Dropbox) return await Copy(db,dropbox,pc.Path,local,link);
                    var notice=new Notice(0,NoticeKind.Info,TextKey.InfoKeptBoth,pc.Path,new(items,null),DateTimeOffset.UtcNow,false);
                    return await settle(previous??link,previous==null?null:link,notice);
                }
            }
        }
    }
    internal static bool SameObservation(Side side, Entry expected, Entry? seen) => seen == expected || side == Side.Local && seen != null &&
        (string.IsNullOrEmpty(seen.ContentHash) || string.IsNullOrEmpty(expected.ContentHash)) && (expected with {ContentHash=seen.ContentHash}) == seen;
}
