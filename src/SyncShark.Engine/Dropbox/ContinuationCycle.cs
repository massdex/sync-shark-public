namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Detects a repeating change continuation in constant space, shared by every continuation reader.</summary>
internal sealed class ContinuationCycle
{
    private string? _checkpoint;
    private long _steps;

    /// <summary>True when next repeats the previous cursor or the retained checkpoint.</summary>
    internal bool Repeats(string previous, string next)
    {
        if (string.Equals(previous, next, StringComparison.Ordinal)) return true;
        _steps = _steps + 1;
        if ((_steps & (_steps - 1)) == default)
        {
            _checkpoint = next;
            return false;
        }
        return string.Equals(_checkpoint, next, StringComparison.Ordinal);
    }
}
