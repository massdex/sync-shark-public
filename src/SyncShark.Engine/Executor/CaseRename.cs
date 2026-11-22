namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Move the selected identity and compare folder children at each confirmed physical hop before re-pathing.</summary>
public sealed class CaseRename
{
    public async Task<RemovalResult> MoveAsync(ISide side, Entry expected, string destination, string suffix, SyncedEntry? savedBinding, Scope currentEndpoints, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct, Action<RunEvent>? events = null)
    {
        if (savedBinding != null && expected.Id != savedBinding.IdOn(side.Side)) throw new ChangedUnderneathException(expected.Path);
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
            catch (ItemFailedException error) when (side.Side==Side.Dropbox && expected.IsFolder && next==destination && !PathNames.CaseOnly(expected.Path,destination) && error.Cause==TextKey.FailDropboxTooManyFiles && error.Disposition==FailureDisposition.Permanent && PathNames.Same(error.Path,expected.Path))
            { return new(RemovalOutcome.NoEffect,expected,error.Cause); }
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
