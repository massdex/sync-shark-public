namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: ResultRootsBound; only successful committed BindRootsAsync state constructs it.</summary>
public sealed record RootBindingResult(string PairId) : IEngineCliData
{
    /// <summary>Binding presence, not current permission.</summary>
    public bool Bound => true;

    /// <summary>The binding sentence; the CLI writer owns the trailing newline.</summary>
    public string RenderHuman() => Text.Get(TextKey.ResultRootsBound, (TextArg.Pair, PairId));

    public int ExitCode => (int)SyncCliExitCode.Success;
}
