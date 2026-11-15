namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: a finished command whose whole result is one sentence, built by the caller with Text
/// (ResultPairRemoved).</summary>
public sealed record DoneResult(string Message) : IEngineCliData
{
    public string RenderHuman() => Message;
    public int ExitCode => (int)SyncCliExitCode.Success;
}
