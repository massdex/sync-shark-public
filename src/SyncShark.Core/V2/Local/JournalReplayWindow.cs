namespace Massdex.SyncShark.V2;

/// <summary>Single owner of bounded USN windows and adaptive overflow retries.</summary>
internal sealed class JournalReplayWindow
{
    internal const long ReplayWindowBytes = 64L * 1024 * 1024;
    internal const long MinimumWindowBytes = 1024L * 1024;
    private long _bytes;
    internal long Bytes => _bytes;
    internal JournalReplayWindow(long? learned = null) =>
        _bytes = Math.Clamp(learned is > 0 ? learned.Value : ReplayWindowBytes, MinimumWindowBytes, ReplayWindowBytes);

    private bool _retriedNoProgress;
    internal bool RetryNoProgress()
    {
        if (_retriedNoProgress) return false;
        _retriedNoProgress = true;
        _bytes = ReplayWindowBytes;
        return true;
    }
    internal void MadeProgress() => _retriedNoProgress = false;

    internal NtfsUsnCheckpoint Through(long next, NtfsUsnCheckpoint target) =>
        target with { NextUsn = next + Math.Min(_bytes, target.NextUsn - next) };

    internal bool Halve(long attemptedBytes)
    {
        _bytes = Math.Min(_bytes, attemptedBytes);
        if (_bytes <= MinimumWindowBytes) return false;
        _bytes = Math.Max(MinimumWindowBytes, _bytes / 2);
        return true;
    }
}
