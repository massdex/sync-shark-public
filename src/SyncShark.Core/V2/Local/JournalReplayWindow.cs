namespace Massdex.SyncShark.V2;

/// <summary>Single owner of bounded USN windows and adaptive overflow retries.</summary>
internal sealed class JournalReplayWindow
{
    private readonly long _maximumBytes;
    private long _bytes;
    internal long Bytes => _bytes;
    internal long MaximumBytes => _maximumBytes;
    internal long MinimumBytes { get; }
    internal JournalReplayWindow(V2Settings settings, long? learned = null)
    {
        _maximumBytes = settings.JournalReplayWindowBytes;
        MinimumBytes = settings.JournalReplayMinimumWindowBytes;
        _bytes = Math.Clamp(learned is > 0 ? learned.Value : _maximumBytes, MinimumBytes, _maximumBytes);
    }

    private bool _retriedNoProgress;
    internal bool RetryNoProgress()
    {
        if (_retriedNoProgress) return false;
        _retriedNoProgress = true;
        _bytes = _maximumBytes;
        return true;
    }
    internal void MadeProgress() => _retriedNoProgress = false;

    internal NtfsUsnCheckpoint Through(long next, NtfsUsnCheckpoint target) =>
        target with { NextUsn = next + Math.Min(_bytes, target.NextUsn - next) };

    internal bool Halve(long attemptedBytes)
    {
        _bytes = Math.Min(_bytes, attemptedBytes);
        if (_bytes <= MinimumBytes) return false;
        _bytes = Math.Max(MinimumBytes, _bytes / 2);
        return true;
    }
}
