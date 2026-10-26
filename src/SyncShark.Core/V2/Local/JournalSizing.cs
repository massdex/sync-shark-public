namespace Massdex.SyncShark.V2;

public static class JournalSizing
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> Silent = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> Active = new();
    public const string NoticeKey = NoticeVocabulary.JournalNoticeKey;
    public static string Key(long serial) => NoticeKey + ":" + serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static bool IsNotice(Notice notice) => NoticeVocabulary.IsJournalNotice(notice);
    public const string Enlarge = NoticeVocabulary.JournalEnlarge;
    public const string NotNow = NoticeVocabulary.JournalNotNow;
    public const string Never = NoticeVocabulary.JournalNever;
    public const long Minimum = JournalPolicy.Minimum;
    public const long Maximum = JournalPolicy.Maximum;
    public static bool IsChoice(string choice) => NoticeVocabulary.IsJournalChoice(choice);

    public static JournalSizingState Observe(JournalSizingState? previous, ulong journal, long next, DateTimeOffset now) =>
        JournalPolicy.Observe(previous, journal, next, now);

    public static long Target(JournalSizingState state, long current, long free, bool gap, long allocation = JournalPolicy.Delta) =>
        JournalPolicy.Target(state, current, free, gap, allocation);

    /// <summary>No elevation, task wait or journal mutation on the inventory path. Failure leaves scanning intact.</summary>
    internal static void TryObserve(IStateStore store, string pairId, string root, uint expectedSerial, ulong rootFileId, string? cursor, DateTimeOffset now, bool overflow = false, bool observeOnly = false,
        Func<string, long[]?>? query = null, Action<IJournalStore, JournalPending, SyncDiscoveryRoot>? queue = null)
    {
        if (store is not IJournalStore sqlite || !OperatingSystem.IsWindows()) return;
        try
        {
            var volume = Path.GetPathRoot(Path.GetFullPath(root))?.TrimEnd('\\').ToUpperInvariant();
            if (volume is null || volume.Length != 2) return;
            var data = (query ?? SyncSharkJournalNative.Query)(volume);
            if (data is null || data[7] != expectedSerial) return; // Mounted folders must not resize their host drive.
            var gap = overflow || (NtfsUsnCheckpoint.TryParseCursor(cursor, out var id, out var next) &&
                id == unchecked((ulong)data[0]) && next < Math.Max(data[1], data[3]));
            sqlite.ObserveJournal(pairId, volume, data[7], unchecked((ulong)data[0]), data[2], data[4], data[6], gap, now, deferNotice: !observeOnly && SyncDiscoveryClient.IsInstalled, allocation: data[5]);
            var pending = sqlite.PendingJournal(data[7]);
            if (pending is not null && !observeOnly) (queue ?? QueueSilent)(sqlite, pending, new SyncDiscoveryRoot(root, expectedSerial, rootFileId));
            else RunTrace.Emit($"local journal capacity: volume={volume}; maximum={data[4]}; allocation={data[5]}; " +
                (pending is null ? "no pending growth" : "preview recommendation only; journal growth disabled"));
        }
        catch (Exception error)
        { System.Diagnostics.Trace.TraceWarning("Journal capacity observation unavailable: {0}", error.GetType().Name); }
    }

    private static void QueueSilent(IJournalStore store, JournalPending pending, SyncDiscoveryRoot root)
    {
        if (!SyncDiscoveryClient.IsInstalled) return;
        _ = Task.Run(() => MaintainAsync(store, pending, root, SyncDiscoveryClient.TryGrowJournalAsync,
            SyncSharkJournalNative.Query));
    }

    // Injectable OS boundary: deterministic checks never touch a real journal or worker.
    internal static async Task MaintainAsync(IJournalStore store, JournalPending pending,
        SyncDiscoveryRoot root, Func<SyncDiscoveryRoot, long, Task<uint?>> grow, Func<string, long[]?> query)
    {
        using var admission = StoreAdmission.WithCancellation(CancellationToken.None);
        if (!Silent.TryAdd(root.VolumeSerial, 0)) return;
        var active = false;
        try
        {
            if (!(active = Active.TryAdd(pending.Serial, 0))) return;
            var current = store.PendingJournal(pending.Serial);
            if (current is null || current.Volume != pending.Volume || current.State.Requested != pending.State.Requested ||
                current.State.NoticePair != pending.State.NoticePair || current.State.Allocation != pending.State.Allocation) return;
            var before = query(pending.Volume);
            if (before is not null && before[7] == pending.Serial && before[4] >= pending.State.Requested && before[5] >= pending.State.Allocation)
            {
                store.FinishSilentJournal(pending, true, "");
                return;
            }
            var result = await grow(root, pending.State.Requested).ConfigureAwait(false);
            if (result is null) return; // Busy/cooldown: no attempt, no notice. Retry on the next observation.
            var after = result is 0 or 183 ? query(pending.Volume) : null;
            var grew = (result is 0 or 183) && before is not null && after is not null &&
                before[7] == pending.Serial && after[7] == pending.Serial && before[0] == after[0] &&
                after[4] >= pending.State.Requested && after[5] >= pending.State.Allocation;
            var partial = before is not null && after is not null &&
                before[7] == pending.Serial && after[7] == pending.Serial && before[0] == after[0] &&
                (after[4] > before[4] || after[5] > before[5]);
            store.FinishSilentJournal(pending, grew, partial
                ? $"Windows' change history grew to {after![4]} bytes (allocation {after[5]} bytes); {pending.State.Requested} bytes (allocation {pending.State.Allocation} bytes) requested. Free space may limit further growth. Free up disk space and Retry, or Ignore."
                : result == 112
                ? $"Not enough free space to enlarge Windows' change history toward {pending.State.Requested / (double)Minimum:0.##} GB. Free up disk space and Retry, or Ignore."
                : result == 0 ? "Windows' change history did not grow. Retry or Ignore."
                : "Windows' change history is too small and automatic enlargement is unavailable or failed.");
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Background journal maintenance unavailable: {0}", error.GetType().Name);
            try { store.FinishSilentJournal(pending, false, "Automatic change-history enlargement failed (" + error.GetType().Name + ")."); }
            catch (Exception noticeError)
            { System.Diagnostics.Trace.TraceWarning("Journal notice persistence unavailable: {0}", noticeError.GetType().Name); }
        }
        finally
        {
            if (active) Active.TryRemove(pending.Serial, out _);
            Silent.TryRemove(root.VolumeSerial, out _);
        }
    }

    public static async Task ApplyAsync(IStateStore store, Notice notice, string choice, IElevatedAction elevated, CancellationToken ct) =>
        await ApplyCoreAsync(store, notice, choice, elevated, ct, SyncSharkJournalNative.Query).ConfigureAwait(false);

    internal static async Task ApplyCoreAsync(IStateStore store, Notice notice, string choice, IElevatedAction elevated,
        CancellationToken ct, Func<string, long[]?> query)
    {
        if (store is not IJournalStore sqlite || !IsNotice(notice) || !IsChoice(choice))
            throw new InvalidOperationException("Refresh the journal notice and choose an offered action.");
        var request = sqlite.JournalRequest(notice);
        if (!Active.TryAdd(request.Serial, 0)) throw new InvalidOperationException("A journal choice for this volume is already in progress. Sync continues.");
        try
        {
            if (choice == Enlarge)
            {
                var before = query(notice.Path);
                var result = await elevated.RunAsync(AttendedElevatedAction.JournalGrow,
                    [notice.Path, request.Serial.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        request.State.Requested.ToString(System.Globalization.CultureInfo.InvariantCulture)], ct).ConfigureAwait(false);
                var after = query(notice.Path);
                if (result != 0) throw new IOException("Windows did not enlarge its change history. Sync continues; the notice remains available.");
                if (before is null || after is null || before[7] != request.Serial || after[7] != request.Serial ||
                    before[0] != after[0] || after[4] < request.State.Requested || after[5] < request.State.Allocation)
                    throw new IOException("Windows' change history did not grow. Free up disk space and retry; the notice remains available and sync continues.");
            }
            sqlite.CompleteJournalChoice(notice, choice);
        }
        finally { Active.TryRemove(request.Serial, out _); }
    }
}
