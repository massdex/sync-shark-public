namespace Massdex.SyncShark.V2;

/// <summary>
/// An endpoint that can delete a whole folder in one provider call and then prove, from its change feed, which items
/// that call removed . Only Dropbox implements it.
/// </summary>
public interface IFolderTreeDeleter
{
    /// <summary>
    /// Deletes each folder whole. <paramref name="scanCursor"/> is the change-feed cursor taken before the plan's
    /// scan. <paramref name="beforeSubmit"/> runs with the trees about to be sent, before any effect, so their intents
    /// are durable first. A tree returned NotSubmitted was never sent.
    /// </summary>
    Task<IReadOnlyList<FolderTreeOutcome>> DeleteFolderTreesAsync(string scanCursor, IReadOnlyList<FolderTree> trees,
        Func<IReadOnlyList<FolderTree>, string, Task> beforeSubmit, CancellationToken ct);

    /// <summary>Resumes the stored group check/restoration, never resubmits the deletion. Unknown acceptance
    /// remains Uncertain. Checkpoints are durable before returning; restored notices survive retries.</summary>
    Task<IReadOnlyList<FolderTreeOutcome>> ReconcileFolderTreesAsync(string scanCursor,
        IReadOnlyList<FolderTree> trees, string recoveryState, Func<string, Task> checkpoint, CancellationToken ct);
}
