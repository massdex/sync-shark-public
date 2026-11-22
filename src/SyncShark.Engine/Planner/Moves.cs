namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Decisions for the adjacent observations of one moved binding.</summary>
public static class Moves
{
    public enum DestinationIdentity { Gone, Moving, Here, MovingAncestor }
    public sealed record DestinationFacts(SyncedEntry Binding, Side Side, DestinationIdentity Identity);
    public sealed record Observations(Entry? LocalAtSynced, Entry? DropboxAtSynced, bool OldOrNewSkipped,
        bool OldOrNewHeld, bool NewOutOfScope, bool CarriesOtherPair, string? LocalHash,
        string? LocalDestination, string? DropboxDestination, DestinationFacts? Destination = null);

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
        if (observations.CarriesOtherPair) return Failure(row, TextKey.FailCarriesOtherPair);
        if (observations.OldOrNewSkipped || observations.OldOrNewHeld) return new(ActionKind.None, row, NoneReason.Held);
        if (observations.NewOutOfScope) return new(ActionKind.None, row, NoneReason.OutOfScope);
        if (pc is { Moved.IsFolder: false } && observations.LocalAtSynced is { IsFolder: false } alias &&
            alias.Id == pc.Moved.Id && alias.Path != pc.Moved.Path) return new(ActionKind.None, row, NoneReason.OutOfScope);
        if (pc != null && cloud != null)
        {
            var path = PathNames.LocalSpelling(cloud.Moved.Path);
            if (pc.Moved.Path != path && PathNames.CaseOnly(pc.Moved.Path, saved.LocalPath) && PathNames.CaseOnly(cloud.Moved.Path, saved.DropboxPath))
                return new(ActionKind.Record, row, InfoAfter: TextKey.InfoCapitalsDiffer);
            return PathNames.Same(pc.Moved.Path, path) ? new(ActionKind.Record, row) : Split(row, TextKey.InfoMovedTwice);
        }
        var written = PathNames.Other(moved.Side);
        var other = row.EntryOn(written);
        if (other is null) return Split(row, TextKey.InfoMovedDeleted);
        var target = written == Side.Local ? observations.LocalDestination : observations.DropboxDestination;
        if (target is null) return Failure(row, TextKey.FailChanged);
        if (other.Path == target && other.Id == saved.IdOn(written) && moved.Moved.Id == saved.IdOn(moved.Side) &&
            PathNames.Same(moved.Moved.Path, saved.PathOn(moved.Side))) return new(ActionKind.Record, row);
        var changed = Changed(written, other, saved, observations.LocalHash, localContentDirty);
        if (changed is null) return new(ActionKind.NeedLocalHash, row);
        if (changed.Value) return Split(row, TextKey.InfoMovedEdited);
        if (observations.Destination is { Identity: DestinationIdentity.Moving or DestinationIdentity.MovingAncestor })
            return new(ActionKind.None, row, NoneReason.Held);
        if (observations.Destination is { Identity: DestinationIdentity.Gone } gone)
            return new(ActionKind.ForgetOnly, (destination ?? row) with { Synced = gone.Binding }, Target: target, Side: gone.Side);
        if (destination?.EntryOn(written) is { } incumbent && incumbent.Id != other.Id)
        {
            var clash = destination with { Local = written == Side.Local ? incumbent : moved.Moved,
                Dropbox = written == Side.Dropbox ? incumbent : moved.Moved,
                LocalHash = written == Side.Local || destination.Local == moved.Moved ? destination.LocalHash : null };
            if (incumbent.IsFolder != moved.Moved.IsFolder) return new(ActionKind.KeepBoth, clash, Target: target);
            if (!incumbent.IsFolder)
            {
                if (clash.LocalHash is null) return new(ActionKind.NeedLocalHash, clash);
                return new(clash.LocalHash == clash.Dropbox!.ContentHash ? ActionKind.Record : ActionKind.KeepBoth, clash, Target: target);
            }
        }
        return new(written == Side.Local ? ActionKind.MoveLocal : ActionKind.MoveDropbox, row, Target: target, Side: written);
    }

    private static bool? Changed(Side side, Entry entry, SyncedEntry synced, string? hash, bool dirty)
    {
        if (entry.IsFolder != synced.IsFolder) return true;
        if (entry.IsFolder) return false;
        if (side == Side.Dropbox) return entry.ContentHash != synced.DropboxContentHash;
        if (entry.Size != synced.LocalSize) return true;
        if (!dirty && entry.Id == synced.LocalId && entry.Modified.UtcTicks == synced.LocalModifiedTicks) return false;
        return hash is null ? null : hash != synced.DropboxContentHash;
    }

    private static SyncAction Split(Row row, TextKey info) => new(ActionKind.ForgetOnly, row, InfoAfter: info);
    private static SyncAction Failure(Row row, TextKey cause) => new(ActionKind.Notice, row, NoticeKind: NoticeKind.Failure, Cause: cause);

    /// <summary>Consume one selected member reply. An invalid job needs a completed by-ID read (null means proved absent); a read failure never enters here.</summary>
    public static SyncAction For(RenameCandidate candidate, Row destination, JobMember selected, JobReply reply,
        (IdentityLocation Location, bool InScope)? observed, Observations observations, bool localContentDirty)
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
                if (result.Id != selected.Id || !PathNames.Same(result.Path, selected.Destination) || !PathNames.SameName(result.Path, selected.Destination))
                    return Failure(destination, TextKey.FailDropboxOutcomeUnknown);
                remote = result;
            }
            else if (member.Failure == FailureDisposition.UnknownEffect) remote = observed is { InScope: true } seen ? seen.Location.Entry : null;
            else return Failure(destination, TextKey.FailDropboxOutcomeUnknown);
        }
        else if (reply is JobReply.Invalid) remote = observed is { InScope: true } seen ? seen.Location.Entry : null;
        else return Failure(destination, TextKey.FailDropboxOutcomeUnknown);
        if (remote is not null && remote.Id != selected.Id) return Failure(destination, TextKey.FailChanged);
        var facts = observations with { DropboxAtSynced = remote };
        if (remote is not null && remote.Path != candidate.Synced.DropboxPath && candidate.Side == Side.Local)
            return For([candidate, new(Side.Dropbox, remote, candidate.Synced)], destination, facts, localContentDirty);
        return For([candidate], destination, facts, localContentDirty);
    }
}
