namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Read-only readable view of one stored run log (D-1105-6, section 4.2). The Run owner streams the
/// canonical file through the shared human projection and changes no sync state or file. Human blocks were already
/// delivered through the caller's output transport; structured output carries the same six-field records.</summary>
public sealed record LogsResult(string PairId, string RunId, LogView View) : IEngineCliData
{
    public string RenderHuman() => View.Status switch
    {
        // The Found view streamed its blocks; nothing is re-rendered here.
        LogViewStatus.Found => string.Empty,
        LogViewStatus.Invalid => Text.Get(TextKey.HumanLogInvalid),
        LogViewStatus.NotFound => Text.Get(TextKey.HumanLogNotFound,
            (TextArg.Pair, PairId), (TextArg.Run, RunId)),
        _ => Text.Get(TextKey.HumanLogUnreadable),
    };

    public int ExitCode => (int)SyncCliExitCode.Success;
}
