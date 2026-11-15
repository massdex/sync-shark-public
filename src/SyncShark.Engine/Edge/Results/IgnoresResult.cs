using System.Text.Json.Serialization;

namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 5.3: the exact pair's stored pair-specific ignore rules, or with FolderKeep its keep-folder
/// preferences; FolderKeep is omitted from JSON when false.</summary>
public sealed record IgnoresResult(string PairId, IReadOnlyList<string> Rules,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool FolderKeep = false) : IEngineCliData
{
    /// <summary>The header, one line per stored specification in stored order, then the matching guidance; an
    /// empty list still prints the header and guidance.</summary>
    public string RenderHuman()
    {
        var lines = new List<string>
        {
            Text.Get(FolderKeep ? TextKey.ResultFolderKeepRulesHeader : TextKey.ResultIgnoreRulesHeader,
                (TextArg.Pair, PairId))
        };
        lines.AddRange(Rules.Select(rule => Text.Get(TextKey.ResultIgnoreRuleLine, (TextArg.Value, rule))));
        lines.Add(Text.Get(FolderKeep ? TextKey.ResultFolderKeepRulesExplanation : TextKey.ResultIgnoreRulesDefaults));
        return string.Join(Environment.NewLine, lines);
    }

    public int ExitCode => (int)SyncCliExitCode.Success;
}
