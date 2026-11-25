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

    /// <summary>A never-synced KEEP rule is a path rule only: when its item is gone from that path, raise one Failure asking
    /// for an exclusion at the new place; close it when the item is back or the rule is gone. One probe per rule.</summary>
    internal static async Task CheckKeepRulesAsync(PairConfig pair, ISide local, ISide dropbox, IStore store, CancellationToken ct)
    {
        var open = new List<Notice>();
        await foreach (var n in store.NoticesAsync(pair.PairId, ct)) if (n.Cause == TextKey.KeepRuleItemMissing) open.Add(n);
        foreach (var rule in pair.Exclusions.Where(e => e.Keep && e.KeptBy is null))
        {
            var raised = open.Find(n => n.Subject.Incoming == rule.Side && PathNames.Same(n.Path, rule.Path));
            if (raised != null) open.Remove(raised);
            Entry? at;
            try { at = await (rule.Side == Side.Local ? local : dropbox).ReadOneAsync(rule.Path, ct); }
            catch (Exception e) when (e is ItemFailedException or IOException) { continue; } // Unread is not absent.
            if (at != null && raised != null) await store.CloseNoticeAsync(pair.PairId, raised, ct);
            if (at == null && raised == null)
                await store.SaveNoticeAsync(pair.PairId, new(0, NoticeKind.Failure, TextKey.KeepRuleItemMissing, rule.Path, new([], rule.Side), DateTimeOffset.UtcNow, false), ct);
        }
        foreach (var orphan in open) await store.CloseNoticeAsync(pair.PairId, orphan, ct); // Its rule was removed or now rides a synced row.
    }
}
