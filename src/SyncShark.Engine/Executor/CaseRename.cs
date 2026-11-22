namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Move the selected identity and compare folder children at each confirmed physical hop before re-pathing.</summary>
public sealed class CaseRename
{
    public async Task<RemovalResult> MoveAsync(ISide side, Entry expected, string destination, string suffix, SyncedEntry? savedBinding, Scope currentEndpoints, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Action<RunEvent>? events = null)
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
            catch (PathOccupiedException error) when (next==destination && Effects.AtTarget(error,side.Side,next))
            { return new(RemovalOutcome.NoEffect,expected,TextKey.ClashNameTaken); }
            catch (ItemFailedException error) when (expected.Path!=initial.Path && error.Disposition==FailureDisposition.Permanent)
            { return new(RemovalOutcome.NoEffect,expected,error.Cause); }
            if (result.Outcome==RemovalOutcome.NoEffect && expected.Path!=initial.Path) return result with {Final=expected};
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
}
