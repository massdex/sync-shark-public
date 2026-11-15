namespace Massdex.SyncShark.Engine.Run;

/// <summary>Resolves the completed session for independently admitted recovery phases.</summary>
internal sealed class RecoveryExecutionBoundary : IRecoveryExecutionBoundary
{
    private readonly Func<PairSession> _session;
    internal RecoveryExecutionBoundary(Func<PairSession> session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));
    public void RequireCurrent() => PairSafety.RequireCurrent(_session());
    public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        PairSafety.ExecuteRecoveryAsync(_session(), operation, member, execute, ct);
    public Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        PairSafety.ExecuteRecoveryAsync(_session(), operations, execute, ct);
    public Task<T> ExecuteAsync<T>(FolderRemovalState operation,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        PairSafety.ExecuteRecoveryAsync(_session(), operation, execute, ct);
}
