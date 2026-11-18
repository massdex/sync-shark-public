namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: the exact pair's saved exclusions (side, keep, pair-relative path), then its ignore rules.</summary>
public sealed record ExclusionsResult(string PairId, IReadOnlyList<Exclusion> Exclusions,
    IReadOnlyList<string> IgnoreRules) : IEngineCliData
{
    /// <summary>A header and one line per exclusion, then the pair's ignore rules; an empty list still says so.</summary>
    public string RenderHuman()
    {
        var lines = new List<string>
        {
            Text.Get(TextKey.ResultExclusionsHeader, (TextArg.Pair, PairId))
        };
        if (Exclusions.Count == 0)
            lines.Add(Text.Get(TextKey.ResultExclusionsNone));
        else
            lines.AddRange(Exclusions.Select(exclusion => Text.Get(TextKey.ResultExclusionLine,
                (TextArg.Side, exclusion.Side.ToString().ToLowerInvariant()),
                (TextArg.Value, Text.Get(exclusion.Keep ? TextKey.ResultExclusionKeep : TextKey.ResultExclusionNoKeep)),
                (TextArg.Path, exclusion.Path))));
        lines.Add(Text.Get(TextKey.ResultIgnoreRulesHeader, (TextArg.Pair, PairId)));
        lines.AddRange(IgnoreRules.Select(rule => Text.Get(TextKey.ResultIgnoreRuleLine, (TextArg.Value, rule))));
        lines.Add(Text.Get(TextKey.ResultIgnoreRulesDefaults));
        return string.Join(Environment.NewLine, lines);
    }

    public int ExitCode => (int)SyncCliExitCode.Success;
}
