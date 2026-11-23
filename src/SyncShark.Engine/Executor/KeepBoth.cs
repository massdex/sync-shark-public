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
    // Both arriving identities move to the same ordinal; only their old row participates in the commit.
    internal static async Task<EffectOutcome> RelocateAsync(PairConfig pair,EngineSettings settings,Action<RunEvent> events,Row row,Side written,Entry incumbent,Entry partner,ISide local,ISide dropbox,IStore store,Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compare,CancellationToken ct)
    {
        var incoming=PathNames.Other(written); var source=row.EntryOn(incoming)!;
        var pc=row.Local!; var db=row.Dropbox!; var endpoints=new Scope(pc.Path,db.Path);
        var pcBase=written==Side.Local?incumbent.Path:pc.Path; var dbBase=written==Side.Dropbox?incumbent.Path:db.Path;
        for(int ordinal=1;;ordinal=checked(ordinal+1))
        {
            var left=ConflictName.For(pcBase,incoming,source.Modified,settings.ComputerName,settings,ordinal);
            var right=ConflictName.For(dbBase,incoming,source.Modified,settings.ComputerName,settings,ordinal);
            var locks=new ItemLocks();
            var outcome=await locks.RunAsync<EffectOutcome?>(pair,new(ActionKind.KeepBoth,row,Target:left,Side:Side.Local),[],()=>
                locks.RunAsync<EffectOutcome?>(pair,new(ActionKind.KeepBoth,row,Target:right,Side:Side.Dropbox),[],async ()=>
                {
                    if(await Effects.ReadOneAsync(local,left,ct)!=null || await Effects.ReadOneAsync(dropbox,right,ct)!=null) return null;
                    var mover=new CaseRename();
                    var moveBinding=row.Synced is {} original && pc.Id!=original.LocalId && !pc.IsFolder?SyncedEntries.Relink(original,pc,db):row.Synced;
                    var a=await mover.MoveAsync(local,pc,left,settings.CaseRenameSuffix,moveBinding,endpoints,compare,ct,events);
                    if(a.Outcome!=RemovalOutcome.Published) return Effects.Incomplete(a,pc.Path);
                    var b=await mover.MoveAsync(dropbox,db,right,settings.CaseRenameSuffix,moveBinding,endpoints with {LocalPath=left},compare,ct,events);
                    if(b.Outcome!=RemovalOutcome.Published) return Effects.Incomplete(b,db.Path);
                    var localKept=written==Side.Local?incumbent:partner; var remoteKept=written==Side.Dropbox?incumbent:partner;
                    var pcKept=new NoticeItem(Side.Local,localKept.Path,localKept.Id); var dbKept=new NoticeItem(Side.Dropbox,remoteKept.Path,remoteKept.Id);
                    var pcMoved=new NoticeItem(Side.Local,left,a.Final!.Id); var dbMoved=new NoticeItem(Side.Dropbox,right,b.Final!.Id);
                    List<NoticeItem> items=[pcKept,dbKept,pcMoved,dbMoved];
                    if(row.Synced is {IsFolder:true} saved && pc.IsFolder)
                        await store.ProjectSubtreeAsync(pair.PairId,new(saved.LocalPath,saved.DropboxPath),new(left,right),ct);
                    await store.CommitKeepBothAsync(pair.PairId,row.Synced,SyncedEntries.From(a.Final!,b.Final!),new(0,NoticeKind.Info,TextKey.InfoKeptBothRetained,pcKept.Path,new(items,null),DateTimeOffset.UtcNow,false),ct);
                    return EffectOutcome.Applied;
                },ct),ct);
            if(outcome.HasValue) return outcome.Value;
        }
    }
    internal static bool SameObservation(Side side, Entry expected, Entry? seen) => seen == expected || side == Side.Local && seen != null &&
        (string.IsNullOrEmpty(seen.ContentHash) || string.IsNullOrEmpty(expected.ContentHash)) && (expected with {ContentHash=seen.ContentHash}) == seen;
}
