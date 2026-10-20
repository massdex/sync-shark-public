namespace Massdex.SyncShark.V2;

/// <summary>One small observation per physical volume, independent of pair/root/run.</summary>
public sealed record JournalSizingState(ulong JournalId, long NextUsn, DateTimeOffset Observed,
    double BytesPerHour = 0, double LongestGapHours = 0, bool Never = false,
    DateTimeOffset? NotBefore = null, long Requested = 0, string? NoticePair = null);

public static class JournalSizing
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> Silent = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> Active = new();
    public const string NoticeKey = "journal-capacity";
    public static string Key(long serial) => NoticeKey + ":" + serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static bool IsNotice(Notice notice) => notice.ReasonKey.StartsWith(NoticeKey + ":", StringComparison.Ordinal);
    public const string Enlarge = "enlarge-journal";
    public const string NotNow = "journal-not-now";
    public const string Never = "journal-never";
    public const long Minimum = 1024L * 1024 * 1024;
    public const long Maximum = 8 * Minimum;
    public static bool IsChoice(string choice) => choice is Enlarge or NotNow or Never;

    public static JournalSizingState Observe(JournalSizingState? previous, ulong journal, long next, DateTimeOffset now)
    {
        if (previous is null) return new(journal, next, now);
        var hours = (now - previous.Observed).TotalHours;
        // Multiple roots and preview/apply reopen the same volume. Do not turn these into run gaps.
        if (hours < 1.0 / 60) return previous;
        var comparable = previous.JournalId == journal && next >= previous.NextUsn;
        var rate = comparable ? (next - previous.NextUsn) / hours : 0;
        return previous with { JournalId = journal, NextUsn = next, Observed = now,
            BytesPerHour = comparable ? Math.Max(rate, previous.BytesPerHour * 0.9) : 0,
            LongestGapHours = Math.Max(hours, previous.LongestGapHours * Math.Pow(0.9, hours / 24)) };
    }

    public static long Target(JournalSizingState state, long current, long free, bool gap)
    {
        var cap = Math.Min(Maximum, Math.Max(0, free) / 20);
        if (state.Never || current >= cap) return current;
        var window = Math.Max(7 * 24, 3 * state.LongestGapHours);
        var needed = state.BytesPerHour * window;
        if (!gap && current >= Minimum && current >= needed) return current;
        var desired = Math.Max(Minimum, Math.Max(2 * needed, gap ? 2.0 * current : 0));
        return Math.Max(current, (long)Math.Min(cap, desired));
    }

    /// <summary>No elevation, task wait or journal mutation on the inventory path. Failure leaves scanning intact.</summary>
    internal static void TryObserve(IStateStore store, string pairId, string root, uint expectedSerial, ulong rootFileId, string? cursor, DateTimeOffset now)
    {
        if (store is not SqliteStateStore sqlite || !OperatingSystem.IsWindows()) return;
        try
        {
            var volume = Path.GetPathRoot(Path.GetFullPath(root))?.TrimEnd('\\').ToUpperInvariant();
            if (volume is null || volume.Length != 2) return;
            var data = SyncSharkJournalNative.Query(volume);
            if (data is null || data[7] != expectedSerial) return; // Mounted folders must not resize their host drive.
            var gap = NtfsUsnCheckpoint.TryParseCursor(cursor, out var id, out var next) &&
                id == unchecked((ulong)data[0]) && next < Math.Max(data[1], data[3]);
            sqlite.ObserveJournal(pairId, volume, data[7], unchecked((ulong)data[0]), data[2], data[4], data[6], gap, now, deferNotice: SyncDiscoveryClient.IsInstalled);
            var pending = sqlite.PendingJournal(data[7]);
            if (pending is not null) QueueSilent(sqlite, pending, new SyncDiscoveryRoot(root, expectedSerial, rootFileId));
        }
        catch (Exception error)
        { System.Diagnostics.Trace.TraceWarning("Journal capacity observation unavailable: {0}", error.GetType().Name); }
    }

    private static void QueueSilent(SqliteStateStore store, SqliteStateStore.JournalPending pending, SyncDiscoveryRoot root)
    {
        if (!SyncDiscoveryClient.IsInstalled) return;
        _ = Task.Run(() => MaintainAsync(store, pending, root, SyncDiscoveryClient.TryGrowJournalAsync,
            SyncSharkJournalNative.Query));
    }

    // Injectable OS boundary: deterministic checks never touch a real journal or worker.
    internal static async Task MaintainAsync(SqliteStateStore store, SqliteStateStore.JournalPending pending,
        SyncDiscoveryRoot root, Func<SyncDiscoveryRoot, long, Task<uint?>> grow, Func<string, long[]?> query)
    {
        if (!Silent.TryAdd(root.VolumeSerial, 0)) return;
        var active = false;
        try
        {
            if (!(active = Active.TryAdd(pending.Serial, 0))) return;
            var current = store.PendingJournal(pending.Serial);
            if (current is null || current.Volume != pending.Volume || current.State.Requested != pending.State.Requested ||
                current.State.NoticePair != pending.State.NoticePair) return;
            var before = query(pending.Volume);
            if (before is not null && before[7] == pending.Serial && before[4] >= pending.State.Requested)
            {
                store.FinishSilentJournal(pending, true, "");
                return;
            }
            var result = await grow(root, pending.State.Requested).ConfigureAwait(false);
            if (result is null) return; // Busy/cooldown: no attempt, no notice. Retry on the next observation.
            if (result == 183)
            {
                store.FinishSilentJournal(pending, true, "");
                return;
            }
            var after = result == 0 ? query(pending.Volume) : null;
            var grew = result == 0 && before is not null && after is not null &&
                before[7] == pending.Serial && after[7] == pending.Serial && before[0] == after[0] && after[4] > before[4];
            store.FinishSilentJournal(pending, grew, result == 112
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
        if (store is not SqliteStateStore sqlite || !IsNotice(notice) || !IsChoice(choice))
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
                    before[0] != after[0] || after[4] <= before[4])
                    throw new IOException("Windows' change history did not grow. Free up disk space and retry; the notice remains available and sync continues.");
            }
            sqlite.CompleteJournalChoice(notice, choice);
        }
        finally { Active.TryRemove(request.Serial, out _); }
    }
}
