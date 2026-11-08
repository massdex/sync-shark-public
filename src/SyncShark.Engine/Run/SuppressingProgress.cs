namespace Massdex.SyncShark.Engine.Run;

/// <summary>Contains a failed diagnostic subscriber without changing the operation it observes.</summary>
internal sealed class SuppressingProgress(Action<string> publish, Action<Exception> diagnose)
{
    private Exception? _failure;
    internal bool HasFailed => Volatile.Read(ref _failure) is not null;

    internal void Publish(string message)
    {
        if (HasFailed) return;
        try { publish(message); }
        catch (Exception error)
        {
            if (Interlocked.CompareExchange(ref _failure, error, null) is null) diagnose(error);
        }
    }
}
