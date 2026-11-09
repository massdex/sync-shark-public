using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Planner;

// One ordinary-policy owner: every table is a case of one per-row action choice over the same facts.
/// <summary>Section 6.2 decision tables; folder-delete classification receives in-scope, listed rows.</summary>
public static class Decide
{
    /// <summary>Leaves destructive or unproved subtree transitions to their owning work; Retry alone may settle first-run history.</summary>
    public static SyncAction AdmitInBatch(SyncAction action, bool boundElsewhere, Side? unlisted, bool settlesFirstRun)
    {
        var row = action.Row;
        var firstRun = IssuePolicy.InFirstRunScope(row, row.Context.FirstRun, row.Context.FirstRunDeletion);
        var preserved = row.Context.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice;
        var left = row.Synced is not null && (row.Local is null || row.Dropbox is null) ||
            row.Synced is null && (boundElsewhere || unlisted is { } side && row.EntryOn(side) is null) ||
            action.Kind is ActionKind.ReplaceKind or ActionKind.MoveLocal or ActionKind.MoveDropbox or ActionKind.MoveUnlinked or
                ActionKind.DeleteDropbox or ActionKind.ForgetOnly ||
            action.Kind == ActionKind.DeleteLocal && !(settlesFirstRun && firstRun && row.Context.CompleteInventory) ||
            firstRun && !preserved && (!settlesFirstRun || !row.Context.CompleteInventory);
        return left ? None(row, NoneReason.OutOfScope) : action;
    }

    /// <summary>Consumes the settled child barrier for a retained preservation without replaying history.</summary>
    public static SyncAction ResumePreservedFolder(Row row, FirstRunPolicy policy, DateTimeOffset comparedAt)
    {
        var action = For(row, policy, comparedAt, localContentDirty: false);
        return action.Kind == ActionKind.DeferFolder && row.Local is { IsFolder: true } &&
            row.Context.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice
            ? Target(row, ActionKind.EnsureDropboxFolder, row.Context.Destination, TextKey.InfoDropboxHistoryUnchecked)
            : action;
    }

    /// <summary>The settled non-action for a row that is out of scope or skipped, before content evidence can matter.</summary>
    internal static SyncAction? WithoutContent(Row row) =>
        row.Context.OutOfScope ? None(row, NoneReason.OutOfScope)
        : row.Context.Skipped ? None(row, NoneReason.Skipped) : null;

    /// <summary>Choose the section 6.2 action from the supplied row, first-run policy, current comparison time and
    /// whether acquired evidence or a current content observation says the stamp does not prove the content.</summary>
    public static SyncAction For(Row row, FirstRunPolicy policy, DateTimeOffset comparedAt, bool localContentDirty)
    {
        var context = row.Context;
        if (WithoutContent(row) is { } settled) return settled;
        if (row.Ambiguous) return Notice(row, IssueKind.NameClash, TextKey.ClashMappedNames);
        if (context.Held && context.HeldKind == IssueKind.Failure) return None(row, NoneReason.Held);
        SyncAction action;
        if (context.UnderFolderDelete is { } deleting
            && FolderDeletes.Classify(row, deleting, new FolderChildFacts(
                InThisScan: true, InScope: true, HoldsNestedRoot: context.HoldsNestedRoot, Restored: false,
                Unreadable: false), localContentDirty)
                == FolderVerdict.Removed)
        {
            action = None(row, NoneReason.RemovedWithFolder);
        }
        else
        {
            action = row.Synced is null ? NoRecord(row, policy, comparedAt) : WithRecord(row, localContentDirty);
        }
        // Evaluate Tie and NameClash holds normally; only these outcomes may pass through.
        return !context.Held
            || action.Kind is ActionKind.NeedLocalHash or ActionKind.Record
            || (action.Kind == ActionKind.Notice && action.NoticeKind == context.HeldKind)
            || (action.IsInSync() && row.Synced is not { IsFolder: true })
            ? action
            : None(row, NoneReason.Held);
    }

    private enum SideState { Absent, Unchanged, Changed, Flipped }

    // One decision table: its branches are ordered cases of one action choice over the same row.
    /// <summary>Table A: no saved record.</summary>
    private static SyncAction NoRecord(Row row, FirstRunPolicy policy, DateTimeOffset comparedAt)
    {
        var local = row.Local;
        var dropbox = row.Dropbox;
        var context = row.Context;
        var chosen = context.FirstRun ? policy : FirstRunPolicy.NewestWins;

        if (local is null)
        {
            return dropbox is null
                ? None(row, NoneReason.InSync)
                : Target(row, SyncAction.CopyOn(dropbox.IsFolder, Side.Local), context.Destination);
        }
        if (dropbox is null)
        {
            var preserve = context.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice;
            if (preserve && local.IsFolder)
                return context.CompleteInventory ? new SyncAction(ActionKind.DeferFolder, row)
                    : None(row, NoneReason.Held);
            if (!context.DeletionEligible)
                return Target(row, SyncAction.CopyOn(local.IsFolder, Side.Dropbox), context.Destination,
                    preserve ? TextKey.InfoDropboxHistoryUnchecked : null);
            if (!context.CompleteInventory) return None(row, NoneReason.Held);
            return local.IsFolder ? new SyncAction(ActionKind.DeferFolder, row)
                : FirstRunDeletion(row, local, comparedAt);
        }
        if (local.IsFolder != dropbox.IsFolder) return Notice(row, IssueKind.NameClash, TextKey.ClashFileFolder);
        var localSpelling = PathNames.LocalSpelling(dropbox.Path);
        if (local.IsFolder)
        {
            return PathNames.SameName(local.Path, localSpelling)
                ? Record(row)
                : chosen == FirstRunPolicy.DropboxWins
                    ? Target(row, ActionKind.MoveLocal, localSpelling, TextKey.InfoFolderSpelling)
                    : Target(row, ActionKind.MoveDropbox, local.Path, TextKey.InfoFolderSpelling);
        }
        if (PathNames.IsMapped(dropbox.Path, localSpelling))
        {
            if (local.Size != dropbox.Size) return Notice(row, IssueKind.NameClash, TextKey.ClashMappedDiffers);
            return Compare(row) ?? Notice(row, IssueKind.NameClash, TextKey.ClashMappedDiffers);
        }
        // P6: compute the pure time order once; equal-size unreliable-time rows hash before Record or KeepBoth.
        var order = TimeRules.Compare(local.Modified, dropbox.Modified, comparedAt);
        if (context.FirstRun && Same(local, dropbox) && order != TimeOrder.Unreliable) return Record(row);
        // Dropbox times are unreliable; compare equal-size content on every run, including the first.
        if (local.Size == dropbox.Size && Compare(row) is { } compared)
        {
            return compared;
        }
        if (chosen == FirstRunPolicy.LocalWins) return Target(row, ActionKind.Upload, dropbox.Path);
        if (chosen == FirstRunPolicy.DropboxWins) return Target(row, ActionKind.Download, local.Path);
        if (order == TimeOrder.Unreliable) return KeepBoth(row);
        if (order == TimeOrder.Equal) return Notice(row, IssueKind.Tie, TextKey.TieEqualTimes);
        var info = context.FirstRun ? (TextKey?)null : TextKey.InfoNewestWins;
        return order == TimeOrder.LeftNewer
            ? Target(row, ActionKind.Upload, dropbox.Path, info)
            : Target(row, ActionKind.Download, local.Path, info);
    }

    /// <summary>Table B: saved record.</summary>
    private static SyncAction WithRecord(Row row, bool localContentDirty)
    {
        var synced = row.Synced!;
        if (CrossedFar(row.Local, row.Dropbox, synced) is { } far)
        {
            var view = far == Side.Local
                ? synced with { LocalKey = PathNames.Key(row.Local!.Path), LocalPath = row.Local.Path }
                : synced with { DropboxKey = PathNames.DropboxKey(row.Dropbox!.Path), DropboxPath = row.Dropbox.Path };
            return WithRecord(row with { Synced = view }, localContentDirty) with { Row = row };
        }
        var (localState, needHash) = LocalState(row, synced, localContentDirty);
        if (needHash) return NeedHash(row);
        return (localState, DropboxState(row, synced)) switch
        {
            (SideState.Absent, SideState.Absent) => new SyncAction(ActionKind.ForgetOnly, row),
            (SideState.Absent, SideState.Unchanged) =>
                new SyncAction(synced.IsFolder ? ActionKind.DeferFolder : ActionKind.DeleteDropbox, row),
            (SideState.Absent, SideState.Changed) =>
                Target(row, ActionKind.Download, synced.LocalPath, TextKey.InfoEditBeatsDelete),
            (SideState.Absent, SideState.Flipped) => ReplaceKind(row, Side.Local, synced.LocalPath),
            (SideState.Unchanged, SideState.Absent) =>
                new SyncAction(synced.IsFolder ? ActionKind.DeferFolder : ActionKind.DeleteLocal, row),
            (SideState.Unchanged, SideState.Unchanged) => UnchangedBoth(row, synced),
            (SideState.Unchanged, SideState.Changed) => Target(row, ActionKind.Download, synced.LocalPath),
            (SideState.Unchanged, SideState.Flipped) => ReplaceKind(row, Side.Local, synced.LocalPath),
            (SideState.Changed, SideState.Absent) =>
                Target(row, ActionKind.Upload, synced.DropboxPath, TextKey.InfoEditBeatsDelete),
            (SideState.Changed, SideState.Unchanged) => Target(row, ActionKind.Upload, synced.DropboxPath),
            (SideState.Changed, SideState.Changed) => BothChanged(row),
            (SideState.Changed, SideState.Flipped) => Notice(row, IssueKind.Tie, TextKey.TieKindBoth),
            (SideState.Flipped, SideState.Absent) => ReplaceKind(row, Side.Dropbox, synced.DropboxPath),
            (SideState.Flipped, SideState.Unchanged) => ReplaceKind(row, Side.Dropbox, synced.DropboxPath),
            (SideState.Flipped, SideState.Changed) => Notice(row, IssueKind.Tie, TextKey.TieKindBoth),
            (SideState.Flipped, SideState.Flipped) => BothFlipped(row),
            _ => throw new System.Diagnostics.UnreachableException(),
        };
    }

    private static SyncAction UnchangedBoth(Row row, SyncedEntry synced)
    {
        var local = row.Local!;
        var dropbox = row.Dropbox!;
        // Both contents are unchanged; refresh only the applicable saved facts.
        var refresh = synced.IsFolder
            ? local.Path != synced.LocalPath || dropbox.Path != synced.DropboxPath
            : LocalStampDiffers(local, synced) || !DropboxMatchesBaseline(dropbox, synced);
        return refresh ? Record(row) : None(row, NoneReason.InSync);
    }

    private static SyncAction BothChanged(Row row)
    {
        if (row.Local!.Size != row.Dropbox!.Size) return KeepBoth(row);
        return Compare(row) ?? KeepBoth(row);
    }

    private static SyncAction BothFlipped(Row row)
    {
        if (row.Local!.IsFolder) return Record(row);
        if (row.Local.Size != row.Dropbox!.Size) return Notice(row, IssueKind.Tie, TextKey.TieKindBoth);
        return Compare(row) ?? Notice(row, IssueKind.Tie, TextKey.TieKindBoth);
    }

    /// <summary>The local side of table B; NeedHash = LMeta with an equal size and no content hash yet.</summary>
    private static (SideState State, bool NeedHash) LocalState(Row row, SyncedEntry synced, bool localContentDirty)
    {
        if (row.Local is not { } local) return (SideState.Absent, false);
        if (local.IsFolder != synced.IsFolder) return (SideState.Flipped, false);
        return LocalChanged(local, synced, row.LocalHash, localContentDirty) switch
        {
            true => (SideState.Changed, false),
            false => (SideState.Unchanged, false),
            null => (SideState.Unchanged, true),
        };
    }

    private static SideState DropboxState(Row row, SyncedEntry synced)
    {
        if (row.Dropbox is not { } dropbox) return SideState.Absent;
        if (dropbox.IsFolder != synced.IsFolder) return SideState.Flipped;
        return DropboxChanged(dropbox, synced) ? SideState.Changed : SideState.Unchanged;
    }

    private static bool InSyncIdentityHolds(Row row) =>
        row.Synced is not { IsFolder: true } ||
        LocalMatchesBaseline(row.Local, row.Synced) && DropboxMatchesBaseline(row.Dropbox, row.Synced);

    /// <summary>Whether Local metadata matches the saved binding; settlement still requires clean acquired evidence.</summary>
    internal static bool LocalMatchesBaseline(Entry? local, SyncedEntry? saved) =>
        local is { Skip: null } && saved is not null &&
        local.Path == saved.LocalPath && local.IsFolder == saved.IsFolder &&
        (local.IsFolder ? local.Id == saved.LocalId : LocalChanged(local, saved, null, false) == false);

    // The saved Dropbox identity remains the authority independently of content or path changes.
    internal static bool DropboxMatchesBaseline(Entry? dropbox, SyncedEntry saved) =>
        dropbox is not null && dropbox.Id == saved.DropboxId;

    /// <summary>Did the local file's content change since the record? null = unknown, the hash is needed. Folders never change.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool? LocalChanged(Entry entry, SyncedEntry synced, string? localHash, bool localContentDirty)
    {
        if (entry.IsFolder || synced.IsFolder) return false;
        if (entry.Size != synced.LocalSize) return true;
        if (!localContentDirty && !LocalStampDiffers(entry, synced)) return false;
        return localHash is null ? null : localHash != synced.DropboxContentHash;
    }

    /// <summary>Did the local id or modified ticks change since the record? Size and kind are separate checks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LocalStampDiffers(Entry entry, SyncedEntry synced) =>
        entry.Id != synced.LocalId || entry.Modified.UtcTicks != synced.LocalModifiedTicks;

    /// <summary>Did the Dropbox file's content change since the record? Folders never change.</summary>
    internal static bool DropboxChanged(Entry entry, SyncedEntry synced) =>
        !entry.IsFolder && entry.ContentHash != synced.DropboxContentHash;

    // Unknown local content stays protected as changed until the Executor supplies its hash.
    internal static bool Changed(Side side, Entry entry, SyncedEntry synced, string? localHash, bool localContentDirty) =>
        entry.IsFolder != synced.IsFolder || (side == Side.Dropbox
            ? DropboxChanged(entry, synced)
            : LocalChanged(entry, synced, localHash, localContentDirty) ?? true);

    /// <summary>A same-side create at a path another binding holds, observed on exactly one side and no longer
    /// matching that binding's id: the create must use expected: null. Both bindings count, either may be null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFreeIncoming(Entry? local, Entry? dropbox, SyncedEntry? localBinding,
        SyncedEntry? dropboxBinding) =>
        (localBinding is not null || dropboxBinding is not null) &&
        (local is not null && dropbox is null && local.Id != localBinding?.LocalId ||
         dropbox is not null && local is null && dropbox.Id != dropboxBinding?.DropboxId);

    /// <summary>The returning original opposite a live crossed-file endpoint; same-identity moves remain renames.</summary>
    public static Side? CrossedFar(Entry? local, Entry? dropbox, SyncedEntry? binding)
    {
        if (local is not { IsFolder: false, Skip: null } || dropbox is not { IsFolder: false, Skip: null } ||
            binding is not { IsFolder: false } || binding.LocalKey == binding.DropboxKey ||
            !PathNames.Same(local.Path, PathNames.LocalSpelling(dropbox.Path))) return null;
        if (PathNames.Same(local.Path, binding.LocalKey) && local.Id == binding.LocalId &&
            !DropboxMatchesBaseline(dropbox, binding)) return Side.Dropbox;
        if (PathNames.Same(dropbox.Path, binding.DropboxKey) && DropboxMatchesBaseline(dropbox, binding) &&
            local.Id != binding.LocalId) return Side.Local;
        return null;
    }

    /// <summary>Null means different content; otherwise record equality or request the missing hash.</summary>
    private static SyncAction? Compare(Row row) =>
        row.LocalHash is null ? NeedHash(row)
        : row.LocalHash == row.Dropbox!.ContentHash ? Record(row) : null;

    private static bool Same(Entry local, Entry dropbox) =>
        local.Size == dropbox.Size && TimeRules.SameSecond(local.Modified, dropbox.Modified);

    private static SyncAction ReplaceKind(Row row, Side side, string target) =>
        new(ActionKind.ReplaceKind, row, Target: target, Side: side);

    private static SyncAction Target(Row row, ActionKind kind, string? target, TextKey? info = null) =>
        new(kind, row, Target: target, InfoAfter: info);

    private static SyncAction Notice(Row row, IssueKind kind, TextKey cause) =>
        new(ActionKind.Notice, row, NoticeKind: kind, Cause: cause);

    internal static SyncAction None(Row row, NoneReason reason) => new(ActionKind.None, row, reason);

    /// <summary>An action held by the caller's current conflict or unfinished work.</summary>
    internal static SyncAction Held(Row row) => None(row, NoneReason.Held);

    /// <summary>The first-run folder action postponed to its independent folder phase.</summary>
    internal static SyncAction DeferFolder(Row row) => new(ActionKind.DeferFolder, row);

    private static SyncAction Record(Row row) => new(ActionKind.Record, row);

    private static SyncAction NeedHash(Row row) => new(ActionKind.NeedLocalHash, row);

    private static SyncAction KeepBoth(Row row) => new(ActionKind.KeepBoth, row, InfoAfter: TextKey.InfoKeptBoth);

    /// <summary>P4 first-run file decision: request the one prepared deletion evidence, then apply P4-T06..T10.
    /// The evidence path must be the row's exact mapped Dropbox target; null is no answer, never a deletion.</summary>
    private static SyncAction FirstRunDeletion(Row row, Entry local, DateTimeOffset comparedAt)
    {
        if (row.Context.Destination is not { } destination)
            return Notice(row, IssueKind.Failure, TextKey.FailChanged);
        if (row.Deletion is not { } evidence)
            return new SyncAction(ActionKind.NeedDeletionEvidence, row, Target: destination);
        if (!PathNames.Same(evidence.Path, destination))
            return Notice(row, IssueKind.Failure, TextKey.FailChanged);
        return evidence switch
        {
            DeletionEvidence.Unknown unknown => Notice(row, IssueKind.Failure, unknown.Cause),
            // T10-a: genuine history unavailability preserves the item as new with the existing D12 notice.
            DeletionEvidence.HistoryUnavailable =>
                Target(row, ActionKind.Upload, destination, TextKey.InfoDropboxHistoryUnchecked),
            // T06: a documented history negative preserves through the ordinary upload, with no notice.
            DeletionEvidence.NoRetainedHistory => Target(row, ActionKind.Upload, destination),
            DeletionEvidence.DeletedFile deleted => DeletedFileDecision(row, local, destination, deleted, comparedAt),
            // A live location is reobserved by the Executor before any re-decision; the folder-only variants
            // cannot apply to a file. All keep a non-destructive failure so the item stays eligible.
            _ => Notice(row, IssueKind.Failure, TextKey.FailChanged),
        };
    }

    /// <summary>P4-T07/T08/T09/T10 for one validated deleted file; the first applicable row wins.</summary>
    private static SyncAction DeletedFileDecision(Row row, Entry local, string destination,
        DeletionEvidence.DeletedFile deleted, DateTimeOffset comparedAt)
    {
        // T07: a reliable deletion strictly before the local edit preserves without requesting a hash.
        if (deleted.DeletedAt is { } deletedAt
            && TimeRules.Compare(deletedAt, local.Modified, comparedAt) == TimeOrder.RightNewer)
            return Target(row, ActionKind.Upload, destination,
                BothHashesDiffer(deleted.ContentHash, row.LocalHash) ? TextKey.InfoEditBeatsDelete : (TextKey?)null);
        // T08: proven differing content preserves the edit even when the deletion time cannot decide.
        if (BothHashesDiffer(deleted.ContentHash, row.LocalHash))
            return Target(row, ActionKind.Upload, destination, TextKey.InfoEditBeatsDelete);
        // T09: equal proven content and a strictly newer deletion authorizes the local removal.
        if (deleted.ContentHash is { } contentHash && contentHash == row.LocalHash
            && deleted.DeletedAt is { } newestDeletion
            && TimeRules.Compare(newestDeletion, local.Modified, comparedAt) == TimeOrder.LeftNewer)
            return new SyncAction(ActionKind.DeleteLocal, row, Target: local.Path);
        // The content comparison is the next proof needed before T08 or T09 can settle.
        if (deleted.ContentHash is not null && row.LocalHash is null) return NeedHash(row);
        // T10-b: contradictory or unusable ordering keeps a failure and the item's eligibility.
        return Notice(row, IssueKind.Failure,
            deleted.ContentHash is null ? deleted.HashUnavailable ?? TextKey.FailChanged
                : deleted.TimeUnavailable ?? TextKey.FailChanged);
    }

    private static bool BothHashesDiffer(string? dropboxHash, string? localHash) =>
        dropboxHash is not null && localHash is not null && dropboxHash != localHash;
}
