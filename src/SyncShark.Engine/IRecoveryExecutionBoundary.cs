namespace Massdex.SyncShark.Engine;

/// <summary>The Executor port to Run admission for recovery (LIVE 2): one durable folder-removal operation runs
/// inside an ephemeral <see cref="IAdmittedExecution"/> lease that is disposed once. Run supplies the session,
/// root checks, the exact operation descriptor and its footprint behind the port; every overload passes the
/// callback value, exception and cancellation through unchanged and takes the operation, never a fabricated
/// work selection.</summary>
internal interface IRecoveryExecutionBoundary
{
    void RequireCurrent();

    Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct);

    Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct);

    Task<T> ExecuteAsync<T>(FolderRemovalState operation,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute,
        CancellationToken ct);
}
