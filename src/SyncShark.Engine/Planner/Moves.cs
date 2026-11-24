namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Decisions for the adjacent observations of one moved binding.</summary>
public static class Moves
{
    public enum DestinationIdentity { Gone, Moving, Here, MovingAncestor }
    public sealed record DestinationFacts(SyncedEntry Binding, Side Side, DestinationIdentity Identity);
    public sealed record Observations(Entry? LocalAtSynced, Entry? DropboxAtSynced, bool OldOrNewSkipped,
        bool OldOrNewHeld, bool NewOutOfScope, bool CarriesOtherPair, string? LocalHash,
        string? LocalDestination, string? DropboxDestination, DestinationFacts? Destination = null, bool LeavesScope = false);

    /// <summary>Adopt equal destinations; for different capitals keep both spellings in one binding. Split other bilateral moves.</summary>
    public static SyncAction For(IReadOnlyList<RenameCandidate> bindingCandidates, Row? destination,
        Observations observations, bool localContentDirty)
    {
        RenameCandidate? pc = null, cloud = null;
        foreach (var candidate in bindingCandidates)
            if (candidate.Side == Side.Local) pc ??= candidate; else cloud ??= candidate;
        var moved = pc ?? cloud ?? throw new ArgumentException(nameof(bindingCandidates));
        var saved = moved.Synced;
        var row = new Row(saved.KeyOn(moved.Side), pc?.Moved ?? observations.LocalAtSynced,
            cloud?.Moved ?? observations.DropboxAtSynced, saved, [], observations.LocalHash, RowContext.Neutral);
        if (Ineligible(row, pc, observations) is { } stop) return stop;
        // A one-sided move out of scope takes the ordinary decision with the moved side absent.
        if (observations.LeavesScope) return Decide.For(moved.Side == Side.Local ? row with { Local = null } : row with { Dropbox = null }, FirstRunPolicy.NewestWins, DateTimeOffset.MinValue, localContentDirty);
        if (pc != null && cloud != null)
        {
            var path = observations.DropboxDestination ?? PathNames.SpellingOn(Side.Dropbox, pc.Moved.Path);
            if (cloud.Moved.Path != path && PathNames.CaseOnly(pc.Moved.Path, saved.LocalPath) && PathNames.CaseOnly(cloud.Moved.Path, saved.DropboxPath))
                return new(ActionKind.Record, row, InfoAfter: TextKey.InfoCapitalsDiffer);
            return PathNames.Same(cloud.Moved.Path, path) ? new(ActionKind.Record, row) : Split(row, TextKey.InfoMovedTwice);
        }
        var written = PathNames.Other(moved.Side);
        var other = row.EntryOn(written);
        if (other is null) return Split(row, TextKey.InfoMovedDeleted);
        var target = written == Side.Local ? observations.LocalDestination : observations.DropboxDestination;
        if (target is null) return Failure(row, TextKey.FailChanged);
        if (other.Path == target && other.Id == saved.IdOn(written) && moved.Moved.Id == saved.IdOn(moved.Side) &&
            PathNames.Same(moved.Moved.Path, saved.PathOn(moved.Side))) return new(ActionKind.Record, row);
        var changed = Decide.Changed(written, other, saved, observations.LocalHash, localContentDirty);
        if (changed is null) return new(ActionKind.NeedLocalHash, row);
        if (changed.Value) return Split(row, TextKey.InfoMovedEdited);
        if (observations.Destination is { Identity: DestinationIdentity.Moving })
            return new(ActionKind.None, row, NoneReason.Held);
        if (observations.Destination is { Identity: DestinationIdentity.Gone } gone)
            return new(ActionKind.ForgetOnly, (destination ?? row) with { Synced = gone.Binding }, Target: target, Side: gone.Side);
        // Effects observes any actual competitor and uses the ordinary decision; this remains the selected source.
        return new(written == Side.Local ? ActionKind.MoveLocal : ActionKind.MoveDropbox, row, Target: target, Side: written);
    }

    private static SyncAction? Ineligible(Row row, RenameCandidate? pc, Observations observations)
    {
        if (observations.CarriesOtherPair) return Failure(row, TextKey.FailCarriesOtherPair);
        if (observations.OldOrNewSkipped || observations.OldOrNewHeld) return new(ActionKind.None, row, NoneReason.Held);
        if (observations.NewOutOfScope) return new(ActionKind.None, row, NoneReason.OutOfScope);
        if (pc is { Moved.IsFolder: false } && observations.LocalAtSynced is { IsFolder: false } alias &&
            alias.Id == pc.Moved.Id && alias.Path != pc.Moved.Path) return new(ActionKind.None, row, NoneReason.OutOfScope);
        return null;
    }

    /// <summary>Identify a file move whose surviving endpoints need separate bindings.</summary>
    public static bool IsFileSplit(SyncAction action) => action is {Kind:ActionKind.ForgetOnly, Row.Synced.IsFolder:false, InfoAfter:TextKey.InfoMovedEdited or TextKey.InfoMovedDeleted or TextKey.InfoMovedTwice};

    private static SyncAction Split(Row row, TextKey info) => new(ActionKind.ForgetOnly, row, InfoAfter: info);
    private static SyncAction Failure(Row row, TextKey cause) => new(ActionKind.Notice, row, NoticeKind: NoticeKind.Failure, Cause: cause);

    // Dropbox preserves leaf spelling but may report different capitalization for ancestors.
    private static bool SameLanding(string left, string right) => PathNames.Same(left, right) && PathNames.SameName(left, right);

    /// <summary>Consume one selected member reply. An invalid job needs a completed by-ID read (null means proved absent); a read failure never enters here.</summary>
    public static SyncAction For(RenameCandidate candidate, Row destination, JobMember selected, JobReply reply,
        (IdentityLocation Location, bool InScope)? observed, Observations observations, bool localContentDirty, Func<string, SyncedEntry?>? savedDropboxParent = null)
    {
        if (reply is JobReply.Running) return new(ActionKind.None, destination, NoneReason.Unfinished);
        if (selected.Id != candidate.Synced.DropboxId || selected.Destination is null) return Failure(destination, TextKey.FailChanged);
        Entry? remote;
        if (reply is JobReply.Terminal { Members: [var member] })
        {
            if (member.Failure == FailureDisposition.TemporaryNoEffect) return new(ActionKind.None, destination, NoneReason.Unfinished);
            if (member.Cause is { } cause && member.Failure != FailureDisposition.UnknownEffect) return Failure(destination, cause);
            if (member.Entry is { } result && member.Failure is null && member.Cause is null)
            {
                if (result.Id != selected.Id || !SameLanding(result.Path, selected.Destination))
                    return Failure(destination, TextKey.FailDropboxOutcomeUnknown);
                if (candidate.Side != Side.Local || candidate.Moved.Id != candidate.Synced.LocalId || result.IsFolder != candidate.Synced.IsFolder)
                    return Failure(destination, TextKey.FailChanged);
                // A later by-ID observation elsewhere is not evidence that this receipt moved the item there.
                if (observed is { } later && (!later.InScope || !SameLanding(later.Location.Entry.Path, result.Path) || later.Location.Entry.Id != result.Id))
                    remote = later.InScope ? later.Location.Entry : null;
                else
                {
                    var current = observed?.Location.Entry ?? result;
                    var row = new Row(candidate.Synced.LocalKey, candidate.Moved, current, candidate.Synced, [], null, RowContext.Neutral);
                    if (Ineligible(row, candidate, observations) is { } stop) return stop;
                    // Map the receipt's exact path, not the destination of a newer ordinary observation.
                    if (savedDropboxParent is null && PathNames.Parent(result.Path).Length != 0) return Failure(row, TextKey.FailChanged);
                    var localTarget = PathNames.MappedDestination(Side.Local, result.Path, savedDropboxParent,
                        static (lookup, _, parentKey) => lookup!(parentKey));
                    return candidate.Moved.Path == localTarget ? new(ActionKind.Record, row) :
                        new(ActionKind.MoveLocal, row, Target: localTarget, Side: Side.Local);
                }
            }
            else if (member.Failure == FailureDisposition.UnknownEffect) remote = observed is { InScope: true } seen ? seen.Location.Entry : null;
            else return Failure(destination, TextKey.FailDropboxOutcomeUnknown);
        }
        else if (reply is JobReply.Invalid) remote = observed is { InScope: true } seen ? seen.Location.Entry : null;
        else return Failure(destination, TextKey.FailDropboxOutcomeUnknown);
        if (remote is not null && remote.Id != selected.Id) return Failure(destination, TextKey.FailChanged);
        var facts = observations with { DropboxAtSynced = remote };
        if (remote is not null && !SameLanding(remote.Path, candidate.Synced.DropboxPath) && candidate.Side == Side.Local)
            return For([candidate, new(Side.Dropbox, remote, candidate.Synced)], destination, facts, localContentDirty);
        return For([candidate], destination, facts, localContentDirty);
    }
}
