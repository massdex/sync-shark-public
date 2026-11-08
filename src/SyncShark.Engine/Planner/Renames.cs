using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Rename decisions (section 6.3).</summary>
public static class Renames
{
    /// <summary>Decides a rename from its candidates, current observations and the current comparison time.</summary>
    /// <summary>The owner's no-op decision for a row with nothing to rename.</summary>
    public static RenameDecision None() => new(RenameKind.None);

    /// <summary>The one rename-candidate answer: an observed entry whose saved identity now sits at a different path.</summary>
    public static RenameCandidate? Candidate(Side side, Entry? observed, SyncedEntry? saved) =>
        observed is not null && saved is not null && saved.PathOn(side) != observed.Path
            ? new RenameCandidate(side, observed, saved) : null;

    public static RenameDecision For(IReadOnlyList<RenameCandidate> group, Entry? localById, Observations observations,
        string caseRenameSuffix, DateTimeOffset comparedAt, bool localContentDirty)
    {
        var local = FirstOn(group, Side.Local);
        var dropbox = FirstOn(group, Side.Dropbox);

        // The Dropbox leg of an interrupted case-only move sits at the target spelling plus the marker.
        if (dropbox is not null && dropbox.Moved.Id == dropbox.Synced.DropboxId
            && PathNames.Same(dropbox.Moved.Path, PathNames.CaseRenamePath(dropbox.Synced.DropboxPath, caseRenameSuffix)))
            return new RenameDecision(RenameKind.FinishCaseRename, Target: FinishTarget(dropbox.Synced, localById));

        if (observations.CarriesOtherPair)
            return new RenameDecision(RenameKind.Notice, NoticeKind: IssueKind.Failure, Cause: TextKey.FailCarriesOtherPair);
        if (observations.OldOrNewSkipped || observations.OldOrNewHeld)
            return new RenameDecision(RenameKind.Hold);
        if (observations.NewOutOfScope)
            return new RenameDecision(RenameKind.Leave);

        if (local is not null && dropbox is not null)
        {
            var dropboxPath = PathNames.LocalSpelling(dropbox.Moved.Path);
            if (PathNames.CaseOnly(local.Moved.Path, local.Synced.LocalPath)
                && PathNames.CaseOnly(dropbox.Moved.Path, dropbox.Synced.DropboxPath) && local.Moved.Path != dropboxPath)
                return CaseCompetition(local, dropbox, observations, comparedAt);
            return PathNames.Same(local.Moved.Path, dropboxPath)
                ? new RenameDecision(RenameKind.Adopt)
                : new RenameDecision(RenameKind.Split, NoticeKind: IssueKind.Info, Cause: TextKey.InfoMovedTwice);
        }

        return OneSide(local ?? dropbox ?? throw new ArgumentException(nameof(group)), observations, localContentDirty);
    }

    private static RenameDecision OneSide(RenameCandidate moved, Observations observations, bool localContentDirty)
    {
        var counterpartSide = PathNames.Other(moved.Side);
        var counterpart = observations.AtSynced(counterpartSide);
        if (counterpart is null)
            return new RenameDecision(RenameKind.Split, moved.Side,
                NoticeKind: IssueKind.Info, Cause: TextKey.InfoMovedDeleted);
        var target = counterpartSide == Side.Local ? observations.LocalDestination : observations.DropboxDestination;
        // Imported spelling drift is already settled on both providers, not a move or a content acknowledgement.
        if (counterpart.Path == target && counterpart.Id == moved.Synced.IdOn(counterpartSide)
            && moved.Moved.Id == moved.Synced.IdOn(moved.Side)
            && PathNames.Same(moved.Moved.Path, moved.Synced.PathOn(moved.Side)))
            return new RenameDecision(RenameKind.Adopt);
        if (Decide.Changed(counterpartSide, counterpart, moved.Synced, observations.LocalHash, localContentDirty))
            return new RenameDecision(RenameKind.Split, moved.Side,
                NoticeKind: IssueKind.Info, Cause: TextKey.InfoMovedEdited);
        return new RenameDecision(RenameKind.Follow, moved.Side, target);
    }

    private static RenameDecision CaseCompetition(RenameCandidate local, RenameCandidate dropbox,
        Observations observations, DateTimeOffset comparedAt)
    {
        if (local.Moved.IsFolder || dropbox.Moved.IsFolder)
            return new RenameDecision(RenameKind.Notice, NoticeKind: IssueKind.Tie, Cause: TextKey.TieCaseBoth);
        var order = TimeRules.Compare(local.Moved.Modified, dropbox.Moved.Modified, comparedAt);
        if (order is TimeOrder.Unreliable or TimeOrder.Equal)
            return new RenameDecision(RenameKind.Notice, NoticeKind: IssueKind.Tie, Cause: TextKey.TieCaseBoth);
        return order == TimeOrder.LeftNewer
            ? new RenameDecision(RenameKind.Follow, Side.Local, observations.DropboxDestination)
            : new RenameDecision(RenameKind.Follow, Side.Dropbox, observations.LocalDestination);
    }

    /// <summary>The first candidate observed on a side, or null. Loops without a captured predicate; a null source
    /// fails as LINQ does, and array and list inputs keep their allocation-free indexer path.</summary>
    public static RenameCandidate? FirstOn(IEnumerable<RenameCandidate> candidates, Side side)
    {
        if (candidates is null) return NoSourceFound(candidates, side);
        if (candidates is IReadOnlyList<RenameCandidate> list)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i].Side == side) return list[i];
            return null;
        }
        foreach (var candidate in candidates)
            if (candidate.Side == side) return candidate;
        return null;
    }

    /// <summary>The first candidate observed on a side; a null source or a missing side fails exactly as
    /// Enumerable.First.</summary>
    public static RenameCandidate RequireOn(IEnumerable<RenameCandidate> candidates, Side side) =>
        candidates is null ? NoSourceRequired(candidates, side) : FirstOn(candidates, side) ?? Missing(candidates, side);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RenameCandidate? NoSourceFound(IEnumerable<RenameCandidate>? candidates, Side side) =>
        candidates!.FirstOrDefault(candidate => candidate.Side == side);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RenameCandidate NoSourceRequired(IEnumerable<RenameCandidate>? candidates, Side side) =>
        candidates!.First(candidate => candidate.Side == side);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RenameCandidate Missing(IEnumerable<RenameCandidate> candidates, Side side) =>
        candidates.First(candidate => candidate.Side == side);

    /// <summary>True when a split needs the local counterpart's content hash: a Dropbox-moved file whose local
    /// observation is a same-size metadata change that may still hold the saved content.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool NeedsLocalHash(RenameDecision decision, SyncedEntry synced, Entry? localAtSynced, bool localContentDirty) =>
        decision.Kind == RenameKind.Split
        && decision.Cause == TextKey.InfoMovedEdited
        && decision.MovedSide == Side.Dropbox
        && localAtSynced is { IsFolder: false }
        && Decide.LocalChanged(localAtSynced, synced, null, localContentDirty) is null;

    private static string FinishTarget(SyncedEntry synced, Entry? localById) =>
        localById is not null && PathNames.Same(localById.Path, synced.DropboxPath)
            ? localById.Path
            : synced.DropboxPath;
}
