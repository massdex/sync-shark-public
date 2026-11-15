using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: ResultStatusLine (ResultStatusLineNever before the first Repair) per pair, then its exclusions
/// (D27's standing list; skipped items are listed by `engine issues --kind skipped`).</summary>
public sealed record StatusResult(IReadOnlyList<StatusInfo> Items) : IEngineCliData
{
    /// <summary>A status line per pair, followed by its exclusions.</summary>
    public string RenderHuman() => Items.Count == 0
        ? Text.Get(TextKey.NoPairsYet)
        : string.Join(Environment.NewLine, Items.SelectMany(Lines));

    public int ExitCode => (int)SyncCliExitCode.Success;

    private static IEnumerable<string> Lines(StatusInfo item)
    {
        var count = item.IssueCounts.Values.Sum().ToString(CultureInfo.InvariantCulture);
        yield return item.LastRepair is { } last
            ? Text.Get(TextKey.ResultStatusLine, (TextArg.Pair, item.Pair.PairId), (TextArg.Name, item.Pair.Name),
                (TextArg.Finished, last.Finished.ToString(CultureInfo.InvariantCulture)), (TextArg.Count, count))
            : Text.Get(TextKey.ResultStatusLineNever, (TextArg.Pair, item.Pair.PairId), (TextArg.Name, item.Pair.Name),
                (TextArg.Count, count));
        foreach (var exclusion in item.Pair.Exclusions)
            yield return Text.Get(exclusion.Keep ? TextKey.ResultStatusExclusionKept : TextKey.ResultStatusExclusion,
                (TextArg.Side, exclusion.Side.ToString()), (TextArg.Path, exclusion.Path));
    }
}
