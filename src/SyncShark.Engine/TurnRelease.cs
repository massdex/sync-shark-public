namespace Massdex.SyncShark.Engine;

/// <summary>Releases a ReplaceTurn once.</summary>
internal sealed class TurnRelease(SemaphoreSlim turn) : IAsyncDisposable
{
    private bool released;

    /// <summary>Releases the semaphore once; later calls do nothing.</summary>
    public ValueTask DisposeAsync()
    {
        if (released) return ValueTask.CompletedTask;
        released = true;
        turn.Release();
        return ValueTask.CompletedTask;
    }
}
