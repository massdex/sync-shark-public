namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Moves operations for the planner module.</summary>
public static class Moves
{

    /// <summary>Decide a selected move from its per-member reply and documented by-ID observation, not a reused pathname.</summary>
    public static SyncAction For(RenameCandidate candidate, Row destination, JobMember selected, JobReply reply, (IdentityLocation Location,bool InScope)? observed) => throw new NotImplementedException();

    /// <summary>Decide the adjacent candidates for one saved binding without buffering or sorting the pair. A two-sided group needs no destination row: adopt the same mapped path, otherwise split with InfoMovedTwice. Executor/CaseRename checks interrupted case-rename legs.</summary>
    public static SyncAction For(IReadOnlyList<RenameCandidate> bindingCandidates, Row? destination, bool sourceAbsent) => throw new NotImplementedException();

    /// <summary>For for the selected Moves operation.</summary>
    public static SyncAction For(IReadOnlyList<RenameCandidate> bindingCandidates, Row? destination, Observations observations, bool localContentDirty) => throw new NotImplementedException();
    /// <summary>For for the selected Moves operation.</summary>
    public static SyncAction For(RenameCandidate candidate, Row destination, JobMember selected, JobReply reply, (IdentityLocation Location, bool InScope)? observed, Observations observations, bool localContentDirty) => throw new NotImplementedException();
    /// <summary>Classifies destinationidentity for the selected operation.</summary>
    public enum DestinationIdentity { Gone, Moving, Here, MovingAncestor }
    /// <summary>Classifies destinationfacts for the selected operation.</summary>
    public sealed record DestinationFacts(SyncedEntry Binding, Side Side, DestinationIdentity Identity);
    /// <summary>Facts observed at both ends of a move.</summary>
    public sealed record Observations(Entry? LocalAtSynced, Entry? DropboxAtSynced, bool OldOrNewSkipped,
        bool OldOrNewHeld, bool NewOutOfScope, bool CarriesOtherPair, string? LocalHash,
        string? LocalDestination, string? DropboxDestination, DestinationFacts? Destination = null);
}
