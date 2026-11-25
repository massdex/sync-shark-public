namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Detects a repeating change continuation in constant space, shared by every continuation reader.</summary>
public sealed class ContinuationCycle
{
    private string? _start;
    private string? _checkpoint;
    private long _steps;

    /// <summary>True when next repeats the previous cursor or the retained checkpoint.</summary>
    public bool Repeats(string? previous, string next, bool advancing = true)
    {
        if (string.IsNullOrEmpty(previous)) return false; // A fresh listing has no preceding provider cursor.
        if (!advancing && previous == next) return false;
        _start ??= previous;
        if (previous == next || next == _start || next == _checkpoint) return true;
        _steps++;
        if ((_steps & (_steps - 1)) == 0)
            _checkpoint = next;
        return false;
    }
}
