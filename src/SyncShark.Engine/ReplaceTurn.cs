namespace Massdex.SyncShark.Engine;

/// <summary>Serialises the bin-then-rename step of one pair's local replaces (D31).</summary>
public sealed class ReplaceTurn
{
    private const int SingleHolder = 1;
    private readonly SemaphoreSlim turn = new(SingleHolder, SingleHolder);

    /// <summary>Waits for this pair's turn and returns its release.</summary>
    public async Task<IAsyncDisposable> EnterAsync(CancellationToken ct)
    {
        await turn.WaitAsync(ct).ConfigureAwait(false);
        return new TurnRelease(turn);
    }
}
