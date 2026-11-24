using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Decides one item from its observed content, saved record and first-run policy.</summary>
public static class Decide
{
    /// <summary>An exclusion supplies the excluded side and whether its original application is still owed.</summary>
    public static SyncAction For(Row row, FirstRunPolicy policy, DateTimeOffset comparedAt, bool localContentDirty,
        (Side Excluded, bool Owed)? exclusion = null)
    {
        var context = row.Context;
        if (context.OutOfScope && exclusion is null) return None(row, NoneReason.OutOfScope);
        var stub = row.Local is { IsFolder: false, Skip: nameof(TextKey.SkipOnlineOnly) };
        if (context.Skipped && !stub) return None(row, NoneReason.Skipped);
        if (row.Ambiguous) return Notice(row, NoticeKind.NameClash, TextKey.ClashMappedNames);
        var action = stub ? Placeholder(row, exclusion?.Excluded == Side.Local)
            : exclusion is { } rule ? Excluded(row, rule.Excluded, rule.Owed, policy, comparedAt, localContentDirty)
            : Ordinary(row, policy, comparedAt, localContentDirty);
        return context.Held && context.HeldKind == NoticeKind.Tie && (row.Local is not null || row.Dropbox is not null)
            && action.Kind != ActionKind.NeedLocalHash && !(action.Kind == ActionKind.Notice && action.NoticeKind == NoticeKind.Tie)
            ? None(row, NoneReason.Held) : action;
    }

    // Never read the placeholder. Its expected metadata stays on the ordinary download action.
    private static SyncAction Placeholder(Row row, bool excludedLocal)
    {
        if (excludedLocal) return Notice(row, NoticeKind.Failure, TextKey.FailExcludeHoldsProtected);
        return row.Dropbox switch
        {
            null => Notice(row, NoticeKind.Skipped, TextKey.SkipOnlineOnly),
            { IsFolder: false, Skip: null } => Target(row, ActionKind.Download, row.Local!.Path),
            _ => Notice(row, NoticeKind.Failure, TextKey.ReadDropboxFailed)
        };
    }

    private static SyncAction Ordinary(Row row, FirstRunPolicy policy, DateTimeOffset comparedAt, bool dirty) =>
        row.Synced is null ? NoRecord(row, policy, comparedAt) : WithRecord(row, dirty);

    private static SyncAction Excluded(Row row, Side excluded, bool owed, FirstRunPolicy policy, DateTimeOffset at, bool dirty)
    {
        if (row.EntryOn(excluded) is not { } source) return None(row, NoneReason.OutOfScope);
        var kept = PathNames.Other(excluded);
        var other = row.EntryOn(kept);
        var target = other?.Path ?? row.Context.Destination ?? row.Synced?.PathOn(kept) ?? PathNames.SpellingOn(kept, source.Path);
        if (other is not null && other.IsFolder != source.IsFolder)
            return owed ? Notice(row, NoticeKind.NameClash, TextKey.ClashFileFolder) : ReplaceKind(row, kept, target);
        var copy = SyncAction.CopyOn(source.IsFolder, kept);
        if (!owed)
        {
            if (other is not null && source.IsFolder) return Record(row);
            if (other is not null && source.Size == other.Size && Compare(row) is { } compared) return compared;
            return Target(row, copy, target);
        }
        var d = Ordinary(row, policy, at, dirty);
        if ((d.Kind is ActionKind.Upload or ActionKind.Download or ActionKind.DeleteLocal or ActionKind.DeleteDropbox
             && d.WrittenSide() == excluded) || (d.Kind == ActionKind.DeferFolder && other is null))
            return None(row, NoneReason.OutOfScope);
        if (d.Kind == ActionKind.KeepBoth) return d with { Side = kept };
        return d.Kind == copy || d.Kind is ActionKind.Record or ActionKind.Notice or ActionKind.NeedLocalHash || d.IsInSync()
            ? d : Notice(row, NoticeKind.Failure, TextKey.FailChanged);
    }

    private enum SideState { Absent, Unchanged, Changed, Flipped }

    // One decision table: its branches are ordered cases of one action choice over the same row.
    /// <summary>Table A: no saved record.</summary>
    internal static SyncAction NoRecord(Row row, FirstRunPolicy policy, DateTimeOffset comparedAt)
    {
        var local = row.Local;
        var dropbox = row.Dropbox;

        if (local is null)
        {
            return dropbox is null
                ? None(row, NoneReason.InSync)
                : Target(row, SyncAction.CopyOn(dropbox.IsFolder, Side.Local), row.Context.Destination);
        }
        if (dropbox is null) return FirstRunDeletions.ForLocalOnly(row, local, policy, comparedAt);

        var context = row.Context;
        var chosen = context.FirstRun ? policy : FirstRunPolicy.NewestWins;
        if (local.IsFolder != dropbox.IsFolder) return chosen switch
        {
            FirstRunPolicy.LocalWins => ReplaceKind(row, Side.Dropbox, dropbox.Path),
            FirstRunPolicy.DropboxWins => ReplaceKind(row, Side.Local, local.Path),
            _ => KeepBoth(row)
        };
        if (local.IsFolder) return Record(row);
        var order = context.FirstRun ? TimeRules.Compare(local.Modified, dropbox.Modified, comparedAt) : TimeOrder.Unreliable;
        if (order == TimeOrder.Equal && local.Size == dropbox.Size) return Record(row);
        // Dropbox times are unreliable; compare equal-size content on every run, including the first.
        if (local.Size == dropbox.Size && Compare(row) is { } compared) return compared;
        if (chosen == FirstRunPolicy.LocalWins) return Target(row, ActionKind.Upload, dropbox.Path);
        if (chosen == FirstRunPolicy.DropboxWins) return Target(row, ActionKind.Download, local.Path);
        if (order is TimeOrder.Unreliable or TimeOrder.Equal) return KeepBoth(row);
        return order == TimeOrder.LeftNewer ? Target(row, ActionKind.Upload, dropbox.Path)
            : Target(row, ActionKind.Download, local.Path);
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
            (SideState.Changed, SideState.Flipped) => KeepBoth(row),
            (SideState.Flipped, SideState.Absent) => ReplaceKind(row, Side.Dropbox, synced.DropboxPath),
            (SideState.Flipped, SideState.Unchanged) => ReplaceKind(row, Side.Dropbox, synced.DropboxPath),
            (SideState.Flipped, SideState.Changed) => KeepBoth(row),
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
            : LocalStampDiffers(local, synced) || !DropboxIdentityHolds(dropbox, synced);
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
        if (row.Local.Size != row.Dropbox!.Size) return KeepBoth(row);
        return Compare(row) ?? KeepBoth(row);
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

    /// <summary>Whether Local metadata matches the saved binding; settlement still requires clean acquired evidence.</summary>
    internal static bool LocalMatchesBaseline(Entry? local, SyncedEntry? saved) =>
        local is { Skip: null } && saved is not null &&
        local.Path == saved.LocalPath && local.IsFolder == saved.IsFolder &&
        (local.IsFolder ? local.Id == saved.LocalId : LocalChanged(local, saved, null, false) == false);

    /// <summary>Exact no-effect Dropbox echo match; source and responsibility guards belong to settlement.</summary>
    internal static bool DropboxMatchesBaseline(Entry? dropbox, SyncedEntry? saved) =>
        dropbox is { Skip: null } && saved is not null && dropbox.Path == saved.DropboxPath &&
        dropbox.IsFolder == saved.IsFolder && dropbox.Id == saved.DropboxId && !DropboxChanged(dropbox, saved);

    // The saved Dropbox identity remains the authority independently of content or path changes.
    internal static bool DropboxIdentityHolds(Entry? dropbox, SyncedEntry saved) =>
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

    /// <summary>Null requests the PC hash; no caller may infer unchanged content from unknown evidence.</summary>
    public static bool? Changed(Side side, Entry entry, SyncedEntry synced, string? localHash, bool localContentDirty) =>
        entry.IsFolder != synced.IsFolder ? true : side == Side.Dropbox
            ? DropboxChanged(entry, synced) : LocalChanged(entry, synced, localHash, localContentDirty);

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
            !DropboxIdentityHolds(dropbox, binding)) return Side.Dropbox;
        if (PathNames.Same(dropbox.Path, binding.DropboxKey) && DropboxIdentityHolds(dropbox, binding) &&
            local.Id != binding.LocalId) return Side.Local;
        return null;
    }

    /// <summary>Null means different content; otherwise record equality or request the missing hash.</summary>
    private static SyncAction? Compare(Row row) =>
        row.LocalHash is null ? NeedHash(row)
        : row.LocalHash == row.Dropbox!.ContentHash ? Record(row) : null;

    private static SyncAction ReplaceKind(Row row, Side side, string target) =>
        new(ActionKind.ReplaceKind, row, Target: target, Side: side);

    internal static SyncAction Target(Row row, ActionKind kind, string? target, TextKey? info = null) =>
        new(kind, row, Target: target, InfoAfter: info);

    internal static SyncAction Notice(Row row, NoticeKind kind, TextKey cause) =>
        new(ActionKind.Notice, row, NoticeKind: kind, Cause: cause);

    internal static SyncAction None(Row row, NoneReason reason) => new(ActionKind.None, row, reason);

    /// <summary>An action held by the caller's current conflict or unfinished work.</summary>
    internal static SyncAction Held(Row row) => None(row, NoneReason.Held);

    /// <summary>The first-run folder action postponed to its independent folder phase.</summary>
    internal static SyncAction DeferFolder(Row row) => new(ActionKind.DeferFolder, row);

    private static SyncAction Record(Row row) => new(ActionKind.Record, row);

    internal static SyncAction NeedHash(Row row) => new(ActionKind.NeedLocalHash, row);

    private static SyncAction KeepBoth(Row row) => new(ActionKind.KeepBoth, row, InfoAfter: TextKey.InfoKeptBoth);
}
