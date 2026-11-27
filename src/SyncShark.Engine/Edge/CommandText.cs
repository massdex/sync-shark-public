using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>The CLI's presentation helpers: pair and exclusion sentences, the detached-log record filter and its readable view.</summary>
public static class CommandText
{
    /// <summary>The ResultPairLine (ResultPairLineOff when switched off) of this pair.</summary>
    public static string RenderPair(PairConfig pair) =>
        Text.Get(pair.Enabled ? TextKey.ResultPairLine : TextKey.ResultPairLineOff,
            (TextArg.Pair, pair.PairId), (TextArg.Name, pair.Name), (TextArg.LocalRoot, pair.LocalRoot),
            (TextArg.DropboxRoot, pair.DropboxRoot), (TextArg.Account, pair.ConnectionId),
            (TextArg.FirstRun, pair.FirstRun.ToString()));

    /// <summary>A line per pair, or the no-pairs sentence.</summary>
    public static string RenderPairs(IReadOnlyList<PairConfig> pairs) =>
        pairs.Count == default ? Text.Get(TextKey.NoPairsYet) : string.Join(Environment.NewLine, pairs.Select(RenderPair));

    /// <summary>The applied exclusion sentence.</summary>
    public static string RenderExclusionApplied(string path) => Text.Get(TextKey.ResultExcludeApplied, (TextArg.Path, path));

    /// <summary>The destructive-exclusion warning: the recovery destination together with the recovery period.</summary>
    public static string RenderExclusionPreview(string path, Side side, long copyFiles, long copyBytes,
        long removeFiles, long removeBytes, string recoveryPlace) =>
        Text.Get(TextKey.ResultExcludePreview, (TextArg.Path, path),
            (TextArg.CopyFiles, copyFiles.ToString(CultureInfo.InvariantCulture)),
            (TextArg.CopyBytes, copyBytes.ToString(CultureInfo.InvariantCulture)),
            (TextArg.RemoveFiles, removeFiles.ToString(CultureInfo.InvariantCulture)),
            (TextArg.RemoveBytes, removeBytes.ToString(CultureInfo.InvariantCulture)),
            (TextArg.Side, side.ToString()), (TextArg.Place, recoveryPlace));

    /// <summary>The removed-pair sentence.</summary>
    public static string RenderPairRemoved(string pairId) => Text.Get(TextKey.ResultPairRemoved, (TextArg.Pair, pairId));

    /// <summary>The refused pair-removal sentence, carrying the returned reason unchanged.</summary>
    public static string RenderPairNotRemoved(string pairId, TextKey cause) =>
        Text.Get(TextKey.ResultPairNotRemoved, (TextArg.Pair, pairId), (TextArg.Reason, Text.Get(cause)));

    /// <summary>The pair's ignore-rule listing.</summary>
    public static string RenderIgnores(string pairId, IReadOnlyList<string> rules)
    {
        var lines = new List<string> { Text.Get(TextKey.ResultIgnoreRulesHeader, (TextArg.Pair, pairId)) };
        lines.AddRange(rules.Select(rule => Text.Get(TextKey.ResultIgnoreRuleLine, (TextArg.Value, rule))));
        lines.Add(Text.Get(TextKey.ResultIgnoreRulesDefaults));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Per-file (Effect) records print only with --show-files; all other records always print.</summary>
    public static bool ShowLogRecord(string line, bool showFiles)
    {
        if (showFiles) return true;
        // Record grammar (LogText.Format): timestamp<TAB>kind<TAB>what<TAB>path<TAB>detail<TAB>outcome.
        var firstTab = line.IndexOf('	');
        if (firstTab < 0) return true;
        var start = firstTab + 1;
        var kindEnd = line.IndexOf('	', start);
        var kind = kindEnd < 0 ? line[start..] : line[start..kindEnd];
        return !string.Equals(kind, RunEventKind.Effect.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The readable block of one stored record at its recorded time (the file itself is unchanged), or the
    /// malformed-record line, so one bad record never hides the rest.</summary>
    public static string ReadableRecord(string line, bool wellFormed, int number)
    {
        var fields = line.Split(Run.RunLog.FieldSeparator);
        if (wellFormed && fields.Length == Run.RunLog.RecordFieldCount && EnumNames.TryExact<RunEventKind>(fields[1], out var kind) &&
            DateTimeOffset.TryParseExact(fields[0], Run.RunLog.TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) &&
            Run.RunLog.TryUnescape(fields[2], out var what) && Run.RunLog.TryUnescape(fields[3], out var path) &&
            Run.RunLog.TryUnescape(fields[4], out var detail) && Run.RunLog.TryUnescape(fields[5], out var outcome))
            return Run.LogText.Format(new RunEvent(kind, what, path, detail, outcome), at);
        return Text.Get(TextKey.HumanMalformedRecord, (TextArg.Value, number.ToString(CultureInfo.InvariantCulture)));
    }

    internal static void WriteLogMissing(TextWriter output, string pair, string run) =>
        output.WriteLine(Text.Get(TextKey.HumanLogNotFound, (TextArg.Pair, pair), (TextArg.Run, run)));
}
