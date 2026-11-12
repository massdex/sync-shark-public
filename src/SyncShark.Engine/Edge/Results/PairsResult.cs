namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: ResultPairLine per pair; NoPairsYet when there are none.</summary>
public sealed record PairsResult(IReadOnlyList<PairView> Pairs) : IEngineCliData
{
    /// <summary>A line per pair, or the no-pairs sentence.</summary>
    public string RenderHuman() => Pairs.Count == 0
        ? Text.Get(TextKey.NoPairsYet)
        : string.Join(Environment.NewLine, Pairs.Select(pair => pair.Render()));

    public int ExitCode => (int)SyncCliExitCode.Success;
}
