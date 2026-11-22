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

    // Root identity failures belong to pair admission; temporary no-effect failures retain their provider wait.
    internal static ItemFailedException? ItemFailure(PairConfig pair, Exception fault) => fault switch
    {
        ItemFailedException item when item.Disposition != FailureDisposition.TemporaryNoEffect => item,
        ChangedUnderneathException changed when !PathNames.Same(changed.Path, pair.LocalRoot) &&
            !PathNames.Same(changed.Path, pair.DropboxRoot) => new(changed.Path, TextKey.FailChanged, changed),
        PathOccupiedException occupied => new(occupied.Path, TextKey.ClashNameTaken, occupied),
        _ => null
    };

    /// <summary>Raise or refresh one notice; the store keeps an unrelated Failure from displacing a PutBack notice and
    /// lets the newest put-back failure at a path replace its predecessor in the same slot.</summary>
    public Task RaiseAsync(IStore store, string pairId, Notice notice, CancellationToken ct) =>
        store.SaveNoticeAsync(pairId, notice, ct);

    /// <summary>Close one ordinary notice only when the exact reviewed identity still matches the stored row. A PutBack
    /// notice is never closed here: only its own successful Retry or a later change of the same identified item settles
    /// it.</summary>
    public async Task<bool> CloseAsync(IStore store, string pairId, Notice expected, CancellationToken ct)
    {
        if (expected.Kind != NoticeKind.PutBack) await store.CloseNoticeAsync(pairId, expected, ct);
        return true;
    }
}
