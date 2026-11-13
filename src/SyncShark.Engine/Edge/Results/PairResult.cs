namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: ResultPairLine.</summary>
public sealed record PairResult(PairView Pair) : IEngineCliData
{
    public string RenderHuman() => Pair.Render();
    public int ExitCode => (int)SyncCliExitCode.Success;
}
