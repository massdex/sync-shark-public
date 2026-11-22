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
        Array.AsReadOnly(new[] { NoticeKind.Failure, NoticeKind.Tie, NoticeKind.NameClash });

    /// <summary>Kinds closed after a KeepBoth resolution.</summary>
    public static IReadOnlyList<NoticeKind> AfterKeepBoth { get; } =
        Array.AsReadOnly(new[] { NoticeKind.Failure, NoticeKind.Tie });

    /// <summary>A PutBack notice is settled only by its own Retry or a later change of the same identified item; no
    /// compare outcome settles it, whether the path is absent or holds another file (R8/R24, F147). Ignore and
    /// Unignore hide or restore it without settling it.</summary>
    public static bool PutBackSettled(bool ownRetryOrLaterChange) => ownRetryOrLaterChange;

    /// <summary>Raise or refresh one notice; the store keeps an unrelated Failure from displacing a PutBack notice and
    /// lets the newest put-back failure at a path replace its predecessor in the same slot (R24).</summary>
    public Task RaiseAsync(IStore store, string pairId, Notice notice, CancellationToken ct) =>
        store.SaveNoticeAsync(pairId, notice, ct);

    /// <summary>Close one notice only when the exact reviewed identity still matches the stored row; a compare or
    /// scan must not route a PutBack through here — only its own Retry or a later change of the item settles it.</summary>
    public Task CloseAsync(IStore store, string pairId, Notice expected, CancellationToken ct) =>
        store.CloseNoticeAsync(pairId, expected, ct);
}
