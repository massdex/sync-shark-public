using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>The imported configuration and the complete baseline accounting.</summary>
public sealed record PairImportResult(PairView Pair, BaselineImportCounts Baselines) : IEngineCliData
{
    public string RenderHuman() => Pair.Render() + Environment.NewLine +
        Text.Get(TextKey.ResultPairImported, (TextArg.Count, Baselines.ImportedRows.ToString(CultureInfo.InvariantCulture)),
            (TextArg.Value, Baselines.DiscardedFolderRows.ToString(CultureInfo.InvariantCulture)),
            (TextArg.SourceCount, Baselines.SourceRows.ToString(CultureInfo.InvariantCulture)));
    public int ExitCode => (int)SyncCliExitCode.Success;
}
