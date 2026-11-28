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
        // A one-sided PC move onto a name already bound to this PC file, while Dropbox keeps the saved file at its saved spelling, takes the ordinary decision with the moved side absent, as a move out of scope does.
        if (observations.LeavesScope || cloud == null && observations.DropboxAtSynced?.Path == saved.DropboxPath &&
            observations.Destination is { Identity: DestinationIdentity.Here, Side: Side.Local } owner && owner.Binding.LocalId == pc?.Moved.Id)
            return Decide.For(moved.Side == Side.Local ? row with { Local = null } : row with { Dropbox = null }, FirstRunPolicy.NewestWins, DateTimeOffset.MinValue, localContentDirty);
        if (pc != null && cloud != null)
        {
            var path = observations.DropboxDestination ?? PathNames.SpellingOn(Side.Dropbox, pc.Moved.Path);
            if (!PathNames.SamePlace(path, cloud.Moved.Path) && PathNames.CaseOnly(pc.Moved.Path, saved.LocalPath) && PathNames.CaseOnly(cloud.Moved.Path, saved.DropboxPath))
                return new(ActionKind.Record, row, InfoAfter: TextKey.InfoCapitalsDiffer);
            return PathNames.Same(cloud.Moved.Path, path) ? new(ActionKind.Record, row) : Split(row, TextKey.InfoMovedTwice);
        }
        var written = PathNames.Other(moved.Side);
        var other = row.EntryOn(written);
        if (other is null) return Split(row, TextKey.InfoMovedDeleted);
        var target = written == Side.Local ? observations.LocalDestination : observations.DropboxDestination;
        if (target is null) return Failure(row, TextKey.FailChanged);
        // Both sides respelled the name differently: each keeps its own spelling, one report.
        if (PathNames.CaseOnly(moved.Moved.Path, saved.PathOn(moved.Side)) && PathNames.Same(other.Path, saved.PathOn(written)) &&
            !PathNames.SameName(other.Path, saved.PathOn(written)) && !PathNames.SameName(other.Path, target)) return new(ActionKind.Record, row, InfoAfter: TextKey.InfoCapitalsDiffer);
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
}
