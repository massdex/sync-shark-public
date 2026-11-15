namespace Massdex.SyncShark.Engine;

/// <summary>An ephemeral admission lease held for one live effect callback (LIVE 2). It is disposed exactly once,
/// cannot be retained for a later operation and never expands the admitted footprint.</summary>
internal interface IAdmittedExecution : IAsyncDisposable
{
    /// <summary>Verifies the pair, its generation and every required lock key against the admitted footprint.</summary>
    void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys);
    void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes);
    /// <summary>Re-runs the admission validation captured for this lease, so evidence acquired after the admitted
    /// window waits for its own change record instead of being applied.</summary>
    void Revalidate();
    /// <summary>Releases account admission once while retaining the exact footprint until disposal.</summary>
    ValueTask ReleaseAdmissionAsync();
}
