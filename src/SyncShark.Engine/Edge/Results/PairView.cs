namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>A pair as shown by the CLI (section 9.3). RootsBound reports a saved root binding is present, not current permission.</summary>
public sealed record PairView(string Id, string Name, string LocalRoot, string DropboxRoot, string Account,
    FirstRunPolicy FirstRun, bool Enabled, IReadOnlyList<Exclusion> Exclusions, bool RootsBound)
{
    /// <summary>The ResultPairLine (ResultPairLineOff when switched off) of this pair.</summary>
    public string Render() => Text.Get(Enabled ? TextKey.ResultPairLine : TextKey.ResultPairLineOff,
        (TextArg.Pair, Id), (TextArg.Name, Name), (TextArg.LocalRoot, LocalRoot), (TextArg.DropboxRoot, DropboxRoot),
        (TextArg.Account, Account), (TextArg.FirstRun, FirstRun.ToString()));
}
