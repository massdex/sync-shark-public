namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Move the selected identity and compare folder children at each confirmed physical hop before re-pathing, and resolve refused moves.</summary>
public sealed class CaseRename
{
    public async Task<RemovalResult> MoveAsync(ISide side, Entry expected, string destination, string suffix, SyncedEntry? savedBinding, Scope currentEndpoints, Func<SyncedEntry,Scope,Scope?,CancellationToken,Task> compareOldBinding, CancellationToken ct, Action<RunEvent>? events = null)
    {
        if (savedBinding != null && expected.Id != savedBinding.IdOn(side.Side)) throw new ChangedUnderneathException(expected.Path);
        var initial=expected;
        if (side.Side == Side.Dropbox && PathNames.CaseOnly(expected.Path, destination))
        {
            var first = await Hop(PathNames.CaseRenamePath(destination, suffix));
            if (first.Outcome != RemovalOutcome.Published) return first;
        }
        return await Hop(destination);

        async Task<RemovalResult> Hop(string next)
        {
            RemovalResult result;
            try { result = await side.MoveAsync(expected,next,ct); }
            catch (PathOccupiedException error) when (next==destination && RemovalReceipts.AtTarget(error,side.Side,next))
            { return new(RemovalOutcome.NoEffect,expected,TextKey.ClashNameTaken); }
            catch (ItemFailedException error) when (expected.Path!=initial.Path && error.Disposition==FailureDisposition.Permanent)
            { return new(RemovalOutcome.NoEffect,expected,error.Cause); }
            if (result.Outcome is (RemovalOutcome.NoEffect or RemovalOutcome.Unknown) && expected.Path!=initial.Path) return result with {Final=expected};
            if (result.Outcome != RemovalOutcome.Published) return result;
            if (result.Final is not {} final || final.Id != expected.Id || final.Path != next || final.IsFolder != expected.IsFolder)
                throw new ChangedUnderneathException(next);
            events?.Invoke(new(RunEventKind.Effect, nameof(ISide.MoveAsync), next, expected.Path, nameof(RemovalOutcome.Published)) { Side = side.Side });
            var after = side.Side == Side.Local ? currentEndpoints with { LocalPath = next } : currentEndpoints with { DropboxPath = next };
            if (final.IsFolder && savedBinding != null) await compareOldBinding(savedBinding, currentEndpoints, after, ct);
            currentEndpoints = after;
            expected = final;
            return result;
        }
    }

    // A refused rename either met a competitor at the target or hit the Dropbox folder-move limit.
    internal static async ValueTask<(EffectOutcome? Done, Entry Expected, Entry? Incumbent, bool Competing)> ResolveRefusalAsync(EngineSettings settings, Side written, ISide to, Entry expected, string target, Entry? incumbent, RemovalResult result, CancellationToken ct)
    {
        var competing = false;
        if (result is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.ClashNameTaken})
        {
            incumbent = await Effects.ReadOneAsync(to,target,ct);
            if (incumbent == null || incumbent.Id == expected.Id) throw new ChangedUnderneathException(target){FailedSide=to.Side};
            // If the first case leg succeeded the selected source now lives at the temporary name.
            if (written==Side.Dropbox && PathNames.CaseOnly(expected.Path,target))
            {
                var physical = await to.ReadByIdAsync(expected.Id,ct);
                if (physical is not {InScope:true} at || !PathNames.Same(at.Location.Entry.Path,PathNames.CaseRenamePath(target,settings.CaseRenameSuffix))) throw new ChangedUnderneathException(target);
                expected = at.Location.Entry;
            }
            competing = true;
        }
        if (written==Side.Dropbox && expected.IsFolder && !PathNames.CaseOnly(expected.Path,target) && result is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.FailDropboxTooManyFiles})
        {
            try
            {
                var created=await to.EnsureFolderAsync(target,ct);
                if (created.Outcome!=RemovalOutcome.Published) return (RemovalReceipts.Incomplete(created,target),expected,incumbent,competing);
                if (created.Final is not {IsFolder:true} folder || folder.Path!=target) throw new ChangedUnderneathException(target);
                incumbent=folder;
            }
            catch (PathOccupiedException error) when (RemovalReceipts.AtTarget(error,written,target))
            {
                incumbent=await Effects.ReadOneAsync(to,target,ct);
                if (incumbent==null) throw;
            }
            competing=true;
        }
        return (null,expected,incumbent,competing);
    }
}
