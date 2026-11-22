namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Moves operations for the planner module.</summary>
public static class Moves
{

    /// <summary>Decide a selected move from its per-member reply and documented by-ID observation, not a reused pathname.</summary>
    public static SyncAction For(RenameCandidate candidate, Row destination, JobMember selected, JobReply reply, (IdentityLocation Location,bool InScope)? observed) => throw new NotImplementedException();

    /// <summary>Decide the adjacent candidates for one saved binding without buffering or sorting the pair.</summary>
    public static SyncAction For(IReadOnlyList<RenameCandidate> bindingCandidates, Row destination, bool sourceAbsent) => throw new NotImplementedException();
}
