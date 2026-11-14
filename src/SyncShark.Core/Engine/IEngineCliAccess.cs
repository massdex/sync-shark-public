namespace Massdex.SyncShark;

/// <summary>A CLI runtime that can run engine commands (engine design 1.3).</summary>
public interface IEngineCliAccess
{
    Task<object> RunEngineCommandAsync(SyncCliInvocation invocation, Action<string> progress, CancellationToken ct);
}
