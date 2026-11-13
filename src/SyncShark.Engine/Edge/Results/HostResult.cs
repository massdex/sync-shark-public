namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: the host status view (8.8). The state is the value the host actually reported from its
/// registered operations and source lifetimes; the message is Edge's data-owned text for that state, including the
/// actual work phase, waiting reason or last progress. Reporting Unavailable is a truthful state, never cached
/// health, and a responsive status is not proof of progress.</summary>
public sealed record HostResult(string State, string Message) : IEngineCliData
{
    /// <summary>The host's own data-owned status text, unchanged.</summary>
    public string RenderHuman() => Message;

    /// <summary>Reporting the host's actual state, including Unavailable, is a successful read.</summary>
    public int ExitCode => (int)SyncCliExitCode.Success;
}
