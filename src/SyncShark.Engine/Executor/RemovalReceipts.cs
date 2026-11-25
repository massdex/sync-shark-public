using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Select a removal victim, remove it and accept only a receipt that proves the selected version.</summary>
internal static class RemovalReceipts
{
    internal static bool AtTarget(PathOccupiedException error,Side side,string target) => PathNames.Same(error.Path,target) || side==Side.Local && PathNames.Same(error.Path,PathNames.Name(target));

    internal static EffectOutcome Incomplete(RemovalResult result,string path,IssueSubject.UnknownMove? move=null)
    {
        if (result.Outcome == RemovalOutcome.Pending || result.Outcome == RemovalOutcome.Unknown && result.Accepted != null) return EffectOutcome.Pending;
        if (result.Outcome==RemovalOutcome.NoEffect && result.ObservedLocal is {} observed) throw new ChangedUnderneathException(path){ObservedLocal=observed};
        throw new ItemFailedException(path,result.Cause ?? TextKey.FailChanged,null) {Disposition=result.Outcome==RemovalOutcome.Unknown?FailureDisposition.UnknownEffect:FailureDisposition.Permanent, Move=result.Outcome==RemovalOutcome.Unknown?move:null};
    }

    internal static async Task<RemovalResult> CheckReceiptAsync(PairConfig pair,Scope footprint,RemovalSelection? selection,StageProof? stage,RemovalResult result,Entry? peerBefore,ActionKind origin,ISide local,ISide dropbox,IStore store,Func<Scope,IEnumerable<Change>> heldInput,Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate,Func<Scope,Side,Entry,CancellationToken,Task<EffectOutcome>> resolveConflict,Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough,CancellationToken ct,bool userSelected=false)
    {
        if (selection==null) return result;
        var proof=selection.Proof;
        if (result.RecycledVictim is {} receipt && (proof.VolumeId!=receipt.Domain || proof.Expected.Id!=receipt.Entry.Id || proof.Expected.IsFolder!=receipt.Entry.IsFolder ||
            result.Outcome==RemovalOutcome.Retained && !receipt.Entry.IsFolder && ContentHash.IsValid(receipt.Entry.ContentHash) && !StringComparer.OrdinalIgnoreCase.Equals(proof.ContentHash,receipt.Entry.ContentHash)))
        {
            if (!await HeldRecovery.PutBackSubstituteAsync(pair,footprint,receipt,local,dropbox,store,populate,ct)) throw new ItemFailedException(footprint.LocalPath,TextKey.RetryRecoveryRequired,null);
            await store.DisarmAsync(pair.PairId,selection,stage,ct);
            return result with { Outcome=RemovalOutcome.Retained, Cause=TextKey.FailChanged };
        }
        if (result.Outcome==RemovalOutcome.NoEffect) await store.DisarmAsync(pair.PairId,selection,stage,ct);
        if (result.Outcome is not (RemovalOutcome.Removed or RemovalOutcome.Published)) return result;
        if (!userSelected)
        {
            var pcFence=local.BoundaryAsync(ct); var dbFence=dropbox.BoundaryAsync(ct);
            await Task.WhenAll(pcFence,dbFence);
            var fence=((await pcFence).Position,(await dbFence).Cursor);
            if (fence.Position==null || fence.Cursor==null || coversThrough?.Invoke(fence)!=true ||
                !await HeldRecovery.RecoverHeldAsync(pair,footprint,Side.Local,local,dropbox,store,heldInput,populate,ct,origin:origin,resolveConflict:resolveConflict))
                return result with {Outcome=RemovalOutcome.Pending};
        }
        var current=await Effects.ReadOneAsync(local,footprint.LocalPath,ct);
        var peer=await Effects.ReadOneAsync(dropbox,footprint.DropboxPath,ct);
        var binding=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(selection.Saved?.LocalPath ?? footprint.LocalPath),ct);
        if ((result.Final==null?current!=null:!KeepBoth.SameObservation(Side.Local,result.Final,current)) || peer!=peerBefore || binding!=selection.Saved)
        {
            // Only a proved replacement link settles the recovered versions; unrelated changes retain custody.
            if (current!=null && peer!=null && binding!=null && binding==SyncedEntries.From(current,peer))
                await store.DisarmAsync(pair.PairId,selection,stage,ct);
            return result with {Outcome=RemovalOutcome.Pending};
        }
        return result;
    }

    internal static async Task<RemovalSelection?> SelectAsync(PairConfig pair, ISide side, Entry expected, SyncedEntry? saved, string? hash, CancellationToken ct)
    {
        if (side.Side != Side.Local) return null;
        var found = await side.ReadByIdAsync(expected.Id,ct);
        if (found is not {InScope:true} hit || !KeepBoth.SameObservation(Side.Local,expected,hit.Location.Entry) || string.IsNullOrEmpty(hit.Location.Domain) || pair.Roots != null && pair.Roots.LocalVolumeKey() != hit.Location.Domain) throw new ChangedUnderneathException(expected.Path);
        var digest = expected.IsFolder || expected.Skip == nameof(TextKey.SkipOnlineOnly) ? null : hash ?? await side.ContentHashAsync(expected,ct);
        return new(new(expected,hit.Location.Domain,digest),saved);
    }

    internal static async Task<RemovalResult> RemoveAsync(Effects effects, PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, SyncAction action, Entry victim, Side written, ISide to, Entry? source, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> held, Func<Scope,IEnumerable<Change>>? heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> compareOldBinding, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough, bool userSelected, Action<RemovalSelection?> arm, CancellationToken ct)
    {
        var row = action.Row; var saved = row.Synced;
        if (victim.IsFolder) return await new FolderDelete().ApplyAsync(pair,settings,events,paths,victim,saved,written,action.Kind,local,dropbox,store,held,populate,compareOldBinding,ct,covered:row,
            resolveConflict:(scope,side,version,token)=>effects.RecoverConflictAsync(pair,settings,events,paths,scope,side,version,local,dropbox,store,populate,compareOldBinding,token),coversThrough:coversThrough,userSelected:userSelected);
        if (written==Side.Local && !userSelected && (coversThrough==null || heldInput==null)) return new(RemovalOutcome.Pending,null,null);
        var armed = await SelectAsync(pair,to,victim,saved,row.LocalHash,ct);
        arm(armed);
        var removed = written==Side.Dropbox ? await to.DeleteAsync(victim,armed,action.Kind,ct) : await to.DeleteAsync(victim,armed,ct);
        return await CheckReceiptAsync(pair,new(victim.Path,source?.Path ?? saved?.DropboxPath ?? victim.Path),armed,null,removed,source,action.Kind,local,dropbox,store,held,populate,(scope,side,version,token)=>effects.RecoverConflictAsync(pair,settings,events,paths,scope,side,version,local,dropbox,store,populate,compareOldBinding,token),coversThrough,ct,userSelected);
    }
}
