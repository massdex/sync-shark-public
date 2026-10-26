namespace Massdex.SyncShark.V2;

/// <summary>
/// A target endpoint that stages a remote revision in its own temporary file beside the destination, verifies
/// it, and publishes it with an atomic create-only rename. No second copy on the target volume is made.
/// </summary>
public interface ISyncDirectDownloadTarget
{
    /// <summary>
    /// Stages and verifies the download, then invokes <paramref name="preservedAfterVerify"/> (when supplied) before
    /// moving any original. The callback journals creation before bytes arrive and verification before any Recovery step.
    /// </summary>
    Task<ItemState> PutDownloadedAsync(SyncAction a, ISyncDirectDownloadSource source,
        Func<SyncDownloadStage, CancellationToken, Task>? preservedAfterVerify, CancellationToken ct);

    /// <summary>
    /// Finishes or rolls back one interrupted direct download whose durable checkpoint is still open. It never
    /// overwrites a present divergent target; anything unproven returns <see cref="Outcome.Uncertain"/>, so the run
    /// gate keeps the path out of planning.
    /// </summary>
    Task<ActionResult> ResumeDownloadAsync(SyncAction action, ProviderEffectCheckpoint checkpoint,
        CancellationToken ct);
}
