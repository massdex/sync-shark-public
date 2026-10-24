namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static void WindowSizingPersistence(string folder)
    {
        const long serial = 123;
        var learned = JournalReplayWindow.MinimumWindowBytes * 2;
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SaveJournalWindowSize(serial, "F:", "root-a", learned);
            store.SaveJournalWindowSize(serial, "F:", "root-b", learned * 2);
        }
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var window = new JournalReplayWindow(reopened.JournalWindowSize(serial, "root-a"));
        var target = new NtfsUsnCheckpoint(1, 0, JournalReplayWindow.ReplayWindowBytes * 4, 0);
        LocalChecks.Require(window.Through(128, target).NextUsn == 128 + learned &&
            reopened.JournalWindowSize(serial, "root-b") == learned * 2,
            "reopened store remembers each root's learned size independently without a halving ladder");
        LocalChecks.Require(window.RetryNoProgress() && window.Bytes == JournalReplayWindow.ReplayWindowBytes &&
            !window.RetryNoProgress(), "stuck reader retries at the maximum once, then falls back");
        window.MadeProgress();
        LocalChecks.Require(window.RetryNoProgress(), "progress resets the bounded no-progress retry");
        Console.WriteLine("PASS persisted per-root journal window sizing and bounded stalled-reader retry");
    }
}
