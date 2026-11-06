using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Folder-delete classification and decision (section 6.5).</summary>
public static class FolderDeletes
{
    /// <summary>True when the current folder facts justify deleting the folder (7.6): the deleting side is a folder,
    /// and the other side is absent or a folder with a restored strict descendant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool PlanJustified(Entry? presentOnDeletingSide, Entry? other, bool hasRestored) =>
        presentOnDeletingSide is { IsFolder: true } && (other is null || other.IsFolder && hasRestored);

    /// <summary>The side a deferred folder is being deleted from, from the original row: an absent Local means Dropbox.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Side DeletingSide(Row row) => row.Local is null ? Side.Dropbox : Side.Local;

    /// <summary>Classifies a child using saved content and current scope facts.</summary>
    public static FolderVerdict Classify(Row childRow, Side deletingFrom, FolderChildFacts facts, bool localContentDirty)
    {
        var entry = childRow.EntryOn(deletingFrom);
        if (facts.Unreadable || IssuePolicy.ProtectsSubtree(entry?.Skip) || facts.HoldsNestedRoot || facts.Restored)
            return FolderVerdict.Survives;
        // Ignored, ordinarily skipped and out-of-scope children are Removed before the saved-content check (6.5).
        if (!facts.InScope || entry is null || entry.Skip is not null) return FolderVerdict.Removed;
        if (childRow.Synced is not { } synced)
            return facts.InThisScan ? FolderVerdict.Removed : FolderVerdict.Survives;
        return Planner.Decide.Changed(deletingFrom, entry, synced, childRow.LocalHash, localContentDirty)
            ? FolderVerdict.Survives
            : FolderVerdict.Removed;
    }

    /// <summary>Selects P4 folder finalization from the completed actual-content walk, never a recursive plan.
    /// Null emptiness means unreadable contents; keep coverage comes from PathScope, not a filtered inventory.
    /// The caller supplies fresh evidence and separately enforces C-10 authority before an empty-folder effect.</summary>
    public static SyncAction FirstRun(Row row, bool? physicallyEmpty,
        bool hasIgnoredContents, bool ignoredContentsCovered)
    {
        var context = row.Context;
        if (context.OutOfScope) return new(ActionKind.None, row, NoneReason.OutOfScope);
        if (context.Skipped || row.Local?.Skip is not null)
            return new(ActionKind.None, row, NoneReason.Skipped);
        if (row.Ambiguous)
            return new(ActionKind.Notice, row, NoticeKind: IssueKind.NameClash, Cause: TextKey.ClashMappedNames);
        if (context.Held || context.HoldsNestedRoot || !context.CompleteInventory || !context.DeletionEligible
            || row.Synced is not null || row.Local is not { IsFolder: true } || row.Dropbox is not null
            || physicallyEmpty is null)
            return new(ActionKind.None, row, NoneReason.Held);

        // Evidence belongs to the mapped target, not merely to the same local spelling.
        if (context.Destination is not { } destination
            || row.Deletion is { } evidence && !PathNames.Same(evidence.Path, destination))
            return new(ActionKind.Notice, row, NoticeKind: IssueKind.Failure, Cause: TextKey.FailChanged);
        if (row.Deletion is DeletionEvidence.Unknown unknown)
            return new(ActionKind.Notice, row, NoticeKind: IssueKind.Failure, Cause: unknown.Cause);
        if (row.Deletion is DeletionEvidence.LiveAtTarget or DeletionEvidence.LiveElsewhere
            or DeletionEvidence.DeletedFile)
            return new(ActionKind.Notice, row, NoticeKind: IssueKind.Failure, Cause: TextKey.FailChanged);

        System.Diagnostics.Debug.Assert(physicallyEmpty != true || !hasIgnoredContents);
        var nonempty = physicallyEmpty == false || hasIgnoredContents;
        var info = row.Deletion switch
        {
            DeletionEvidence.DeletedPath when hasIgnoredContents && !ignoredContentsCovered =>
                (TextKey?)TextKey.InfoIgnoredFolderKept,
            DeletionEvidence.HistoryUnavailable => TextKey.InfoDropboxHistoryUnchecked,
            _ => null
        };
        if (row.Deletion is null)
        {
            if (!nonempty || hasIgnoredContents && !ignoredContentsCovered)
                return new(ActionKind.NeedDeletionEvidence, row, Target: destination);
        }
        else if (row.Deletion is DeletionEvidence.DeletedPath && !nonempty)
        {
            return new(ActionKind.DeleteLocal, row, Target: row.Local.Path);
        }
        return new(ActionKind.EnsureDropboxFolder, row, Target: destination, InfoAfter: info);
    }

    public static bool DeleteWhole(bool hasSurvivors) => !hasSurvivors;

    /// <summary>Checks one removed child; the caller's parent-first order makes selected branches maximal.</summary>
    public static bool SelectBranch(FolderVerdict verdict, bool hasSurvivorAtOrBelow, bool hasSelectedAncestor) =>
        verdict == FolderVerdict.Removed && !hasSurvivorAtOrBelow && !hasSelectedAncestor;

    /// <summary>Survivor follow-ups must not repeat a restore or observe unreadable content.</summary>
    public static bool FollowUp(FolderVerdict verdict, bool present, bool restored, bool unreadable) =>
        verdict == FolderVerdict.Survives && present && !restored && !unreadable;
}
