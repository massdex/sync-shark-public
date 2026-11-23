namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Notices operations for the executor module: the one owner that raises and closes an issue, with the
/// named kind sets its completed call sites close through (section 2.4).</summary>
public sealed class Notices
{
    /// <summary>Kinds a completed operation closes when its result is not in sync: an applied exclusion removal, a
    /// completed rename or a published pending recovery.</summary>
    public static IReadOnlyList<NoticeKind> AfterEffect { get; } = Array.AsReadOnly(new[] { NoticeKind.Failure });

    /// <summary>Kinds closed after an ordinary in-sync result.</summary>
    public static IReadOnlyList<NoticeKind> AfterInSync { get; } =
        Array.AsReadOnly(new[] { NoticeKind.Failure, NoticeKind.NameClash });

    /// <summary>Kinds closed when a held row becomes in sync; successful copy, delete and move effects close these too.</summary>
    public static IReadOnlyList<NoticeKind> AfterHeldInSync { get; } =
        AfterInSync;

    /// <summary>Kinds closed after a KeepBoth resolution.</summary>
    public static IReadOnlyList<NoticeKind> AfterKeepBoth { get; } =
        AfterEffect;

    // This is a pre-send check. A later receipt still needs its own fresh coverage before settlement.
    internal static async ValueTask<bool> RemovalCoveredAsync(Planner.SyncAction action, ISide local, ISide dropbox,
        Func<Scope,IEnumerable<Change>>? heldInput, Func<(JournalPosition? Local,string? Dropbox),bool>? coversThrough,
        CancellationToken ct)
    {
        if (action.Kind is not (ActionKind.DeleteLocal or ActionKind.DeleteDropbox or ActionKind.ReplaceKind) &&
            !(action.Kind is (ActionKind.Upload or ActionKind.Download) && action.Row.EntryOn(action.WrittenSide()) is not null))
            return true;
        if (heldInput is null || coversThrough is null) return false;
        var pc = local.BoundaryAsync(ct);
        var cloud = dropbox.BoundaryAsync(ct);
        await Task.WhenAll(pc, cloud);
        return (await pc).Position is { } position && (await cloud).Cursor is { Length: > 0 } cursor &&
            coversThrough((position, cursor));
    }

    // Root identity failures belong to pair admission; temporary no-effect failures retain their provider wait.
    internal static ItemFailedException? ItemFailure(PairConfig pair, Exception fault) => fault switch
    {
        ItemFailedException item when item.Disposition != FailureDisposition.TemporaryNoEffect => item,
        ChangedUnderneathException changed when !PathNames.Same(changed.Path, pair.LocalRoot) &&
            !PathNames.Same(changed.Path, pair.DropboxRoot) => new(changed.Path, TextKey.FailChanged, changed),
        PathOccupiedException occupied => new(occupied.Path, TextKey.ClashNameTaken, occupied),
        _ => null
    };

    // Use evidence carried by the failure, not the direction of the planned transfer.
    internal static Side? FailedSide(ItemFailedException error) => error.InnerException switch
    {
        PathOccupiedException { CollidingSide: { } collision } => collision,
        ChangedUnderneathException { FailedSide: { } failed } => failed,
        ChangedUnderneathException { ObservedLocal: not null } => Side.Local,
        _ when error.Move is not null || error.Cause == TextKey.ReadDropboxFailed => Side.Dropbox,
        _ when error.LocalId is not null || error.Holder is not null || error.Cause is TextKey.ReadLocalFailed or
            TextKey.FailLocalInUse or TextKey.FailLocalInUseOrAccessDenied or TextKey.FailLocalNoSpace => Side.Local,
        _ => null
    };

    /// <summary>Raise or refresh one notice; the store keeps an unrelated Failure from displacing a PutBack notice and
    /// lets the newest put-back failure at a path replace its predecessor in the same slot.</summary>
    public Task RaiseAsync(IStore store, string pairId, Notice notice, CancellationToken ct) =>
        store.SaveNoticeAsync(pairId, notice, ct);

    /// <summary>Close one ordinary notice only when the exact reviewed identity still matches the stored row. A PutBack
    /// notice is never closed here: only its own successful Retry or a later change of the same identified item settles
    /// it.</summary>
    public Task<bool> CloseAsync(IStore store, string pairId, Notice expected, CancellationToken ct) =>
        expected.Kind == NoticeKind.PutBack ? Task.FromResult(false) : store.CloseNoticeAsync(pairId, expected, ct);
}
