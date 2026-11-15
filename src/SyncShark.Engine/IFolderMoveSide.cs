namespace Massdex.SyncShark.Engine;

/// <summary>Provider capability for exclusive destination creation and positional folder-move batches.</summary>
public interface IFolderMoveSide
{
    /// <summary>Only a successful create proves ownership; no occupying folder is adopted.</summary>
    Task<Entry> CreateFolderExclusiveAsync(string path, CancellationToken ct);

    /// <summary>Submits persisted wire-order members. From is the original provider identity, never a source path.</summary>
    Task<FolderSubmitResult> SubmitMoveBatchAsync(IReadOnlyList<(string From, string To)> members,
        RequestAttempt attempt, CancellationToken ct);

    /// <summary>Observes a job against its durable member count without resending its effects.</summary>
    Task<FolderJobResult> ReadMoveBatchAsync(string jobId, int expectedCount, RequestAttempt attempt, CancellationToken ct);
}
