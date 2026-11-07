using System.Globalization;
using System.Text;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>The one human projection of RunEvent facts (D-1105-6, R1). Live console delivery and the read-only
/// historical log view both render through this class; the canonical six-column TSV remains a separate writer.
/// Only finite counters and the currently open stages are retained, never per-item history.</summary>
public sealed class HumanLog
{
    private readonly string _pairId;
    private readonly string _runId;
    private readonly string _logPath;
    private readonly TextKey _title;
    private readonly TextKey? _mode;
    private readonly bool _perFile;
    private readonly int _width;
    private readonly List<OpenStage> _open = [];
    private readonly List<TextKey> _stageOrder = [];
    private readonly Dictionary<TextKey, TimeSpan> _stageElapsed = [];
    private readonly Dictionary<(string Name, string Side), int> _effects = [];
    private readonly Dictionary<(string Kind, string Cause), int> _issueGroups = [];
    private DateTimeOffset? _started;
    private DateTimeOffset? _last;
    private DateTimeOffset? _lastProgressAt;
    private DateTime _date;
    private bool _dateSeen;
    private bool _headerEmitted;
    private bool _sizeNotePending = true;
    private bool _completionSeen;
    private int _malformed;
    private int _firstMalformedLine;

    /// <summary>One projection for one run. `perFile` enables per-action cards; `width` is the human line budget;
    /// a null `mode` is the historical view, where the run mode was not recorded.</summary>
    public HumanLog(string pairId, string runId, string logPath, TextKey title, bool perFile, int width,
        TextKey? mode = null)
    {
        _pairId = pairId;
        _runId = runId;
        _logPath = logPath;
        _title = title;
        _perFile = perFile;
        _mode = mode;
        _width = width <= 0 ? RunLog.HumanWidth : width;
    }

    /// <summary>Projects one live event. Returns the block to deliver, or null when nothing is shown.</summary>
    public string? Accept(RunEvent item, DateTimeOffset at)
    {
        _started ??= at;
        _last = at;
        _lastProgressAt = null;
        return item.Kind switch
        {
            RunEventKind.Stage => Stage(item, at),
            RunEventKind.Effect => Effect(item, at),
            RunEventKind.Issue => Issue(item, at),
            RunEventKind.Failed => Failed(item, at),
            RunEventKind.Wait => WaitBlock(item.Detail, at),
            _ => null,
        };
    }

    /// <summary>Counts one committed effect for the completion rows without rendering it, so a hidden effect never
    /// needs the delivery gate. It still starts the recorded span, because the run began there (R1 review 8).</summary>
    public void CountEffect(RunEvent item, DateTimeOffset at)
    {
        _started ??= at;
        Count(_effects, (item.What, item.Outcome));
    }

    /// <summary>Projects one provider or limiter wait; the reason is already the owner's localized sentence.</summary>
    public string? Wait(string reason, DateTimeOffset at)
    {
        _started ??= at;
        _last = at;
        _lastProgressAt = null;
        return WaitBlock(reason, at);
    }

    /// <summary>One compact periodic progress block from the owner's already-selected leaf facts: one line per
    /// concurrent activity, never a sentence for every open stage and request.</summary>
    public string? ProgressBlock(IReadOnlyList<string> details, DateTimeOffset at)
    {
        if (details.Count == 0) return null;
        _started ??= at;
        _last = at;
        _lastProgressAt = at;
        var builder = new StringBuilder();
        builder.AppendLine(Text.Get(TextKey.HumanStageActive, (TextArg.Time, Clock(at))));
        foreach (var detail in details)
            AppendWrappedText(builder, Escape(detail), Text.Get(TextKey.HumanIndent));
        return Deliver(builder.ToString().TrimEnd('\r', '\n'), at);
    }

    /// <summary>Projects the completion record: only facts this run actually recorded, everything else labelled.</summary>
    public string? Complete(bool completed, IReadOnlyDictionary<string, int>? effects,
        IReadOnlyDictionary<string, int>? issues, DateTimeOffset at)
    {
        if (_completionSeen) return null;
        _completionSeen = true;
        _last = at;
        _lastProgressAt = null;
        var builder = new StringBuilder();
        AppendFooter(builder, at,
            completed ? TextKey.HumanFinishedComplete : TextKey.HumanFinishedIncomplete,
            completed ? TextKey.HumanProcessingComplete : TextKey.HumanProcessingIncomplete,
            noCompletion: false, effects, issues);
        return Deliver(builder.ToString(), at);
    }

    /// <summary>Decodes one canonical TSV record and returns its block, or null when the record is not shown. A
    /// malformed record is reported visibly and the next record still renders.</summary>
    public string? AcceptFileLine(int lineNumber, string line, bool wellFormed = true)
    {
        if (!wellFormed || !RunLog.TryDecodeRecord(line, out var record)) return Malformed(lineNumber);
        _started ??= record.Timestamp;
        _last = record.Timestamp;
        switch (record.Kind)
        {
            case RunLog.CompleteKind:
                // A count that cannot be read or summed is a malformed record, not an aborted view (R1 review 3).
                if (!TryParseCounts(record.Detail, out var effects, out var issues)) return Malformed(lineNumber);
                return Complete(record.Outcome == RunLog.TrueText, effects, issues, record.Timestamp);
            case RunLog.ProvisionalKind:
                return null;
            case nameof(ListingProgress):
                return ProgressRecord(record.Detail, record.Timestamp);
        }
        if (!EnumNames.TryExact<RunEventKind>(record.Kind, out var kind)) return Malformed(lineNumber);
        if (kind == RunEventKind.Stage && record.Outcome == nameof(RunEvent.InProgress))
            return ProgressRecord(record.Detail, record.Timestamp);
        // A waiting snapshot with no terminal result is useful progress; a completed request is diagnostic.
        if (kind == RunEventKind.ProviderRequest)
            return record.Outcome.Length == 0 ? ProgressRecord(record.Detail, record.Timestamp) : null;
        return Accept(new RunEvent(kind, record.What, record.Path, record.Detail, record.Outcome), record.Timestamp);
    }

    /// <summary>Closes a historical view: an unfinished run is reported as unknown, then malformed and partial notes.</summary>
    public string? FinishFile(bool unfinishedTrailingRecord)
    {
        var builder = new StringBuilder();
        var at = _last ?? DateTimeOffset.UnixEpoch;
        if (!_completionSeen)
            AppendFooter(builder, at, TextKey.HumanFinishedUnknown, TextKey.HumanNotRecorded,
                noCompletion: true, effects: null, issues: null);
        if (unfinishedTrailingRecord)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.Append(Text.Get(TextKey.HumanUnfinished));
        }
        if (_malformed > 0)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.Append(Text.Get(TextKey.HumanMalformed,
                (TextArg.Count, Number(_malformed)),
                (TextArg.Value, _firstMalformedLine.ToString(CultureInfo.InvariantCulture))));
        }
        if (builder.Length == 0) return null;
        return Deliver(builder.ToString(), _last, tight: true);
    }

    private string? Stage(RunEvent item, DateTimeOffset at)
    {
        if (!EnumNames.TryExact<TextKey>(item.What, out var key))
        {
            // A manually supplied stage sentence (not a typed stage) stays visible, escaped, rather than disappearing.
            return item.Detail.Length == 0 ? null : Deliver(Escape(item.Detail), at, tight: true);
        }
        var ended = item.Outcome is nameof(RunEvent.Ended) or nameof(RunEventKind.Failed);
        if (!ended)
        {
            AddOpen(new OpenStage(key, at));
            return _open.Count == 1 ? Deliver(Heading(key, at), at) : null;
        }
        var open = RemoveOpen(key);
        var elapsed = open is null ? (TimeSpan?)null : at - open.Started;
        // A repeated stage keeps the run's total time for that stage instead of overwriting the earlier occurrence.
        if (elapsed is { } value) _stageElapsed[key] = _stageElapsed.GetValueOrDefault(key) + value;
        var unit = Unit(key);
        var body = item.Outcome == nameof(RunEventKind.Failed)
            ? Text.Get(TextKey.HumanStageInterrupted,
                (TextArg.Indent, string.Empty), (TextArg.Name, Text.Get(key)), (TextArg.Elapsed, Duration(elapsed)))
            : item.Count is { } count && unit is { } unitKey
                ? Text.Get(TextKey.HumanStageComplete,
                    (TextArg.Indent, string.Empty), (TextArg.Name, Text.Get(key)), (TextArg.Count, Number(count)),
                    (TextArg.Unit, Text.Get(unitKey)), (TextArg.Elapsed, Duration(elapsed)))
                : Text.Get(TextKey.HumanStageCompleteNoCount,
                    (TextArg.Indent, string.Empty), (TextArg.Name, Text.Get(key)), (TextArg.Elapsed, Duration(elapsed)));
        return Deliver(body, at, tight: true);
    }

    private string? Effect(RunEvent item, DateTimeOffset at)
    {
        CountEffect(item, at);
        if (!_perFile || !EnumNames.TryExact<EffectKind>(item.What, out var kind)) return null;
        var (operation, result) = CardText(kind);
        var builder = new StringBuilder();
        builder.AppendLine(Text.Get(TextKey.HumanCardHeading,
            (TextArg.Time, Clock(at)), (TextArg.Label, Text.Get(TextKey.HumanOkLabel)),
            (TextArg.Operation, Text.Get(operation))));
        switch (kind)
        {
            case EffectKind.MovedLocal or EffectKind.MovedDropbox:
                AppendWrapped(builder, TextKey.HumanCardFrom, TextArg.Path, item.Detail);
                AppendWrapped(builder, TextKey.HumanCardTo, TextArg.Path, item.Path);
                break;
            case EffectKind.KeptBoth:
                AppendWrapped(builder, TextKey.HumanCardOriginal, TextArg.Path, item.Detail);
                AppendWrapped(builder, TextKey.HumanCardCopy, TextArg.Path, item.Path);
                break;
            default:
                AppendWrapped(builder, TextKey.HumanCardFile, TextArg.Path, item.Path);
                break;
        }
        if (kind is EffectKind.Uploaded or EffectKind.Downloaded)
            builder.AppendLine(Text.Get(TextKey.HumanCardDirection,
                (TextArg.Message, Text.Get(kind == EffectKind.Uploaded ? TextKey.HumanDirectionUpload : TextKey.HumanDirectionDownload))));
        if (item.Outcome.Length > 0 && kind is not (EffectKind.Uploaded or EffectKind.Downloaded))
            builder.AppendLine(Text.Get(TextKey.HumanCardSide, (TextArg.Side, SideText(item.Outcome))));
        builder.Append(Text.Get(TextKey.HumanCardResult, (TextArg.Message, Text.Get(result))));
        // Sizes are a later owner fact; state the gap once instead of leaving the card silently incomplete.
        if (_sizeNotePending)
        {
            _sizeNotePending = false;
            builder.AppendLine();
            builder.Append(Text.Get(TextKey.HumanSizesNotRecorded));
        }
        return Deliver(builder.ToString(), at);
    }

    private string? Issue(RunEvent item, DateTimeOffset at)
    {
        Count(_issueGroups, (item.What, item.Outcome));
        var label = item.What == nameof(IssueKind.Failure) ? TextKey.HumanAttentionFileNotSynced
            : item.What is nameof(IssueKind.Info) or nameof(IssueKind.Skipped) ? TextKey.HumanNotice
            : TextKey.HumanAttention;
        var result = item.What is nameof(IssueKind.Info) or nameof(IssueKind.Skipped)
            ? TextKey.HumanAttentionAutomaticResult : TextKey.HumanAttentionUnsyncedResult;
        return Attention(item, at, label, result);
    }

    private string? Failed(RunEvent item, DateTimeOffset at)
    {
        var stopped = item.What == nameof(OperationCanceledException);
        var label = stopped ? TextKey.HumanStopped : TextKey.HumanError;
        var result = stopped ? TextKey.HumanAttentionStoppedResult : TextKey.HumanAttentionFailedResult;
        return Attention(item, at, label, result);
    }

    private string? Attention(RunEvent item, DateTimeOffset at, TextKey label, TextKey result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Text.Get(TextKey.HumanAttentionHeading,
            (TextArg.Time, Clock(at)), (TextArg.Label, Text.Get(label))));
        if (item.Path.Length > 0) AppendWrapped(builder, TextKey.HumanCardFile, TextArg.Path, item.Path);
        if (item.Detail.Length > 0) AppendWrapped(builder, TextKey.HumanAttentionReason, TextArg.Message, item.Detail);
        AppendWrapped(builder, TextKey.HumanAttentionResult, TextArg.Message, Text.Get(result));
        return Deliver(builder.ToString(), at);
    }

    private string? WaitBlock(string reason, DateTimeOffset at)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Text.Get(TextKey.HumanAttentionHeading,
            (TextArg.Time, Clock(at)), (TextArg.Label, Text.Get(TextKey.HumanWaitLabel))));
        AppendWrapped(builder, TextKey.HumanWaitReason, TextArg.Message, reason);
        builder.Append(Text.Get(TextKey.HumanWaitActivity));
        return Deliver(builder.ToString(), at);
    }

    private string? ProgressRecord(string detail, DateTimeOffset at)
    {
        if (detail.Length == 0) return null;
        var builder = new StringBuilder();
        if (_lastProgressAt != at)
        {
            _lastProgressAt = at;
            builder.Append(Text.Get(TextKey.HumanStageActive, (TextArg.Time, Clock(at)))).AppendLine();
        }
        AppendWrappedText(builder, Escape(detail), Text.Get(TextKey.HumanIndent));
        _last = at;
        return Deliver(builder.ToString().TrimEnd('\r', '\n'), at);
    }

    private void AppendFooter(StringBuilder builder, DateTimeOffset at, TextKey status, TextKey processing,
        bool noCompletion, IReadOnlyDictionary<string, int>? effects, IReadOnlyDictionary<string, int>? issues)
    {
        AppendInterrupted(builder, at);
        builder.AppendLine(Text.Get(TextKey.HumanFinished,
            (TextArg.Name, Text.Get(_title)), (TextArg.Status, Text.Get(status)),
            (TextArg.Suffix, FailureSuffix(issues))));
        builder.AppendLine(Text.Get(TextKey.HumanHeaderPair, (TextArg.Pair, Escape(_pairId))));
        builder.AppendLine(Text.Get(TextKey.HumanRunProcessing, (TextArg.Status, Text.Get(processing))));
        if (noCompletion)
            builder.AppendLine(Text.Get(TextKey.HumanNoComplete, (TextArg.Status, Text.Get(TextKey.HumanNotRecorded))));
        AppendSpan(builder, at);
        AppendOutcomes(builder, effects, issues);
        AppendIssues(builder);
        AppendStageTimings(builder);
        builder.AppendLine();
        builder.Append(Text.Get(TextKey.HumanLogLine, (TextArg.LogPath, Escape(_logPath))));
    }

    private static string FailureSuffix(IReadOnlyDictionary<string, int>? issues) =>
        issues is not null && issues.GetValueOrDefault(nameof(IssueKind.Failure)) > 0
            ? Text.Get(TextKey.HumanFinishedFailureSuffix) : string.Empty;

    private void AppendInterrupted(StringBuilder builder, DateTimeOffset at)
    {
        // Without a completion record a stored log may still be growing, so a stage without its end is unfinished,
        // not proven interrupted (R1 review 5).
        var key = _completionSeen ? TextKey.HumanStageInterrupted : TextKey.HumanStageUnfinished;
        foreach (var stage in _open)
            builder.AppendLine(Text.Get(key, (TextArg.Indent, string.Empty), (TextArg.Name, Text.Get(stage.Key)),
                (TextArg.Elapsed, Duration(at - stage.Started))));
        _open.Clear();
    }

    private void AppendOutcomes(StringBuilder builder, IReadOnlyDictionary<string, int>? effects,
        IReadOnlyDictionary<string, int>? issues)
    {
        var rows = new List<(string Name, int Count)>();
        foreach (var kind in Enum.GetValues<EffectKind>())
        {
            var name = kind.ToString();
            // A recorded completion is authoritative even when it totals zero; only a missing completion record
            // falls back to the observed counters (R1 review 9).
            if (effects is not null)
            {
                if (!effects.TryGetValue(name, out var total) || total == 0) continue;
                var sides = _effects.Where(pair => pair.Key.Name == name).ToArray();
                if (sides.Length > 0 && sides.Sum(pair => (long)pair.Value) == total)
                    foreach (var (key, count) in sides) rows.Add((OutcomeLabel(kind, key.Side), count));
                else rows.Add((Text.Get(EffectLabel(kind)), total));
                continue;
            }
            foreach (var side in new[] { string.Empty, nameof(Side.Local), nameof(Side.Dropbox) })
                if (_effects.TryGetValue((name, side), out var count) && count > 0)
                    rows.Add((OutcomeLabel(kind, side), count));
        }
        if (rows.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(Text.Get(TextKey.HumanOutcomeTitle));
            var nameWidth = rows.Max(row => row.Name.Length);
            var countWidth = rows.Max(row => Number(row.Count).Length);
            foreach (var row in rows)
                builder.AppendLine(Text.Get(TextKey.HumanOutcomeRow, (TextArg.Indent, Text.Get(TextKey.HumanIndent)),
                    (TextArg.Name, row.Name.PadRight(nameWidth)),
                    (TextArg.Count, Number(row.Count).PadLeft(countWidth))));
            builder.AppendLine(Text.Get(TextKey.HumanOutcomeAll, (TextArg.Indent, Text.Get(TextKey.HumanIndent)),
                (TextArg.Count, Number(rows.Sum(row => (long)row.Count)))));
        }
        // A recorded issue total is known even when it is zero; only a missing record leaves the row out.
        if (issues is not null)
        {
            var failure = issues.GetValueOrDefault(nameof(IssueKind.Failure));
            var information = issues.GetValueOrDefault(nameof(IssueKind.Info));
            builder.AppendLine(Text.Get(TextKey.HumanNoticeEmissions,
                (TextArg.Count, Number(issues.Values.Sum(value => (long)value))),
                (TextArg.Value, Number(failure)),
                (TextArg.Message, Number(information))));
        }
        builder.AppendLine(Text.Get(TextKey.HumanNewNotices, (TextArg.Value, Text.Get(TextKey.HumanNotRecorded))));
        builder.AppendLine(Text.Get(TextKey.HumanUniqueFiles, (TextArg.Value, Text.Get(TextKey.HumanNotMeasured))));
        builder.Append(Text.Get(TextKey.HumanPeakMemory, (TextArg.Value, Text.Get(TextKey.HumanNotRecorded))));
    }

    private static string OutcomeLabel(EffectKind kind, string side) => Text.Get(EffectLabel(kind)) +
        (side.Length == 0 ? string.Empty : Text.Get(TextKey.HumanOutcomeSideSuffix, (TextArg.Side, SideText(side))));

    private void AppendIssues(StringBuilder builder)
    {
        if (_issueGroups.Count == 0) return;
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine(Text.Get(TextKey.HumanAttentionSummary));
        foreach (var (group, count) in _issueGroups)
        {
            var label = CauseLabel(group.Cause);
            if (label.Length == 0) label = Text.Get(IssueLabel(group.Kind));
            builder.AppendLine(Text.Get(TextKey.HumanAttentionCountRow, (TextArg.Indent, Text.Get(TextKey.HumanIndent)),
                (TextArg.Name, label), (TextArg.Count, Number(count))));
        }
        builder.Append(Text.Get(TextKey.HumanInspect, (TextArg.Pair, Escape(_pairId))));
    }

    // The cause's own localized sentence, cut to its first clause: the shared summary groups by cause instead of
    // merging different failures into one kind count.
    private static string CauseLabel(string cause)
    {
        if (cause.Length == 0 || !EnumNames.TryExact<TextKey>(cause, out var key)) return string.Empty;
        var text = Text.Get(key, (TextArg.Path, string.Empty), (TextArg.Message, string.Empty)).Trim();
        var stop = text.IndexOf('.');
        if (stop > 0) text = text[..stop];
        return text.TrimEnd();
    }

    private static TextKey IssueLabel(string kind) =>
        EnumNames.TryExact<IssueKind>(kind, out var issue) ? IssueLabel(issue) : TextKey.HumanAttention;

    private void AppendStageTimings(StringBuilder builder)
    {
        if (_stageElapsed.Count == 0) return;
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine(Text.Get(TextKey.HumanStageTimings));
        foreach (var key in _stageOrder)
            if (_stageElapsed.TryGetValue(key, out var elapsed))
                builder.AppendLine(Text.Get(TextKey.HumanStageTimingRow,
                    (TextArg.Indent, Text.Get(TextKey.HumanIndent)),
                    (TextArg.Name, Text.Get(key)), (TextArg.Elapsed, Duration(elapsed))));
        builder.Append(Text.Get(TextKey.HumanStageOverlap));
    }

    private void AppendSpan(StringBuilder builder, DateTimeOffset at)
    {
        var span = _started is { } started && at >= started ? at - started : (TimeSpan?)null;
        builder.AppendLine(Text.Get(TextKey.HumanSpan, (TextArg.Value, Duration(span))));
    }

    private void AddOpen(OpenStage stage)
    {
        if (!_stageOrder.Contains(stage.Key)) _stageOrder.Add(stage.Key);
        // One open entry per key: the stage vocabulary is finite, so a repeated start cannot grow the view.
        for (var index = 0; index < _open.Count; index++)
            if (_open[index].Key == stage.Key) { _open[index] = stage; return; }
        _open.Add(stage);
    }

    private OpenStage? RemoveOpen(TextKey key)
    {
        for (var index = _open.Count - 1; index >= 0; index--)
        {
            if (_open[index].Key != key) continue;
            var open = _open[index];
            _open.RemoveAt(index);
            return open;
        }
        if (!_stageOrder.Contains(key)) _stageOrder.Add(key);
        return null;
    }

    private string Heading(TextKey key, DateTimeOffset at) =>
        Text.Get(TextKey.HumanStageHeading, (TextArg.Time, Clock(at)),
            (TextArg.Name, Text.Get(key).ToUpper(CultureInfo.InvariantCulture)));

    private string? Deliver(string? body, DateTimeOffset? at, bool tight = false)
    {
        var divider = at is { } moment ? NextDivider(moment) : null;
        string? result;
        if (!_headerEmitted)
        {
            _headerEmitted = true;
            var header = Header();
            result = body is null ? header : header + Environment.NewLine + Environment.NewLine + body;
        }
        else result = body is null ? null : (tight ? string.Empty : Environment.NewLine) + body;
        if (divider is not null) result = result is null ? divider : divider + Environment.NewLine + result;
        return result is null ? null : Fit(result);
    }

    private string? NextDivider(DateTimeOffset at)
    {
        var date = at.UtcDateTime.Date;
        if (_dateSeen && date == _date) return null;
        var first = !_dateSeen;
        _dateSeen = true;
        _date = date;
        return first ? null : Text.Get(TextKey.HumanDateDivider,
            (TextArg.Value, date.ToString(Text.Get(TextKey.HumanDateFormat), CultureInfo.InvariantCulture)));
    }

    // The shared delivery boundary: every line honours the client's width, including fields composed as one
    // template rather than through AppendWrapped.
    private string Fit(string text)
    {
        var builder = new StringBuilder(text.Length);
        var firstLine = true;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (!firstLine) builder.Append(Environment.NewLine);
            firstLine = false;
            var remaining = line;
            while (true)
            {
                if (remaining.Length <= _width) { builder.Append(remaining); break; }
                var take = BreakAt(remaining, _width);
                builder.Append(remaining[..take]).Append(Environment.NewLine);
                remaining = remaining[take..];
            }
        }
        return builder.ToString();
    }

    private string Header()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Text.Get(TextKey.HumanHeaderTitle, (TextArg.Name, Text.Get(_title))));
        AppendWrapped(builder, TextKey.HumanHeaderPair, TextArg.Pair, _pairId);
        builder.AppendLine(Text.Get(TextKey.HumanHeaderStarted, (TextArg.Started,
            _started is { } started ? Stamp(started) : Text.Get(TextKey.HumanNotRecorded))));
        builder.AppendLine(Text.Get(TextKey.HumanHeaderMode,
            (TextArg.Mode, _mode is { } mode ? Text.Get(mode) : Text.Get(TextKey.HumanNotRecorded)),
            (TextArg.PerFile, Text.Get(_perFile ? TextKey.HumanPerFileOn : TextKey.HumanPerFileOff))));
        AppendWrapped(builder, TextKey.HumanHeaderRun, TextArg.Run, _runId);
        AppendWrapped(builder, TextKey.HumanHeaderLog, TextArg.LogPath, _logPath);
        return builder.ToString().TrimEnd('\r', '\n');
    }

    private void AppendWrapped(StringBuilder builder, TextKey template, TextArg arg, string value)
    {
        var escaped = Escape(value);
        var prefix = Text.Get(template, (arg, string.Empty));
        if (prefix.Length + escaped.Length <= _width)
        {
            builder.AppendLine(Text.Get(template, (arg, escaped)));
            return;
        }
        AppendWrappedText(builder, escaped, prefix);
    }

    private void AppendWrappedText(StringBuilder builder, string text, string prefix)
    {
        var continuation = new string(' ', prefix.Length);
        var remaining = text;
        var first = true;
        while (remaining.Length > 0)
        {
            var current = first ? prefix : continuation;
            var room = _width - current.Length;
            if (room <= 0) room = _width;
            var take = BreakAt(remaining, room);
            builder.AppendLine(current + remaining[..take]);
            remaining = remaining[take..];
            first = false;
        }
    }

    private static int BreakAt(string value, int room)
    {
        if (value.Length <= room) return value.Length;
        var cut = 0;
        var pathCut = 0;
        for (var index = room - 1; index > 0; index--)
        {
            var character = value[index];
            if (character == ' ')
            {
                cut = index + 1;
                break;
            }
            if (pathCut == 0 && (character == '\\' || character == System.IO.Path.DirectorySeparatorChar ||
                character == System.IO.Path.AltDirectorySeparatorChar)) pathCut = index + 1;
        }
        if (cut == 0) cut = pathCut == 0 ? room : pathCut;
        if (cut < value.Length && char.IsHighSurrogate(value[cut - 1])) cut--;
        if (cut < value.Length && value[cut - 1] == '\\' && value[cut] == 'u') cut--;
        return cut <= 0 ? room : cut;
    }

    private static string Escape(string value) => RunLog.EscapeDisplay(value);

    private static string Clock(DateTimeOffset at) =>
        at.UtcDateTime.ToString(Text.Get(TextKey.HumanClockFormat), CultureInfo.InvariantCulture);

    private static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString(Text.Get(TextKey.HumanDateFormat), CultureInfo.InvariantCulture) + ' ' +
        Clock(at) + ' ' + Text.Get(TextKey.HumanUtc);

    private static string Number(long value) => value.ToString(Text.Get(TextKey.HumanNumberFormat), CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan? elapsed)
    {
        if (elapsed is not { } value) return Text.Get(TextKey.HumanNotRecorded);
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        // TimeSpan's h format wraps at one day; below it the format is accurate, above it total hours stay honest.
        if (value.Days == 0)
            return value.ToString(value.TotalHours >= 1
                ? Text.Get(TextKey.HumanDurationFormat) : Text.Get(TextKey.HumanShortDurationFormat),
                CultureInfo.InvariantCulture);
        var padded = Text.Get(TextKey.HumanPaddedNumberFormat);
        return ((long)value.TotalHours).ToString(padded, CultureInfo.InvariantCulture) + ':' +
            value.Minutes.ToString(padded, CultureInfo.InvariantCulture) + ':' +
            value.Seconds.ToString(padded, CultureInfo.InvariantCulture);
    }

    private static TextKey? Unit(TextKey key) => key switch
    {
        TextKey.StageLocalWalk or TextKey.StageDropboxListing or TextKey.StageListing => TextKey.HumanUnitObservations,
        TextKey.StageRows => TextKey.HumanUnitRows,
        TextKey.StageDropboxPages => TextKey.HumanUnitPages,
        TextKey.StageRenames => TextKey.HumanUnitGroups,
        TextKey.StageFolderPlans => TextKey.HumanUnitMembers,
        _ => null,
    };

    private static TextKey EffectLabel(EffectKind kind) => kind switch
    {
        EffectKind.Downloaded => TextKey.HumanEffectDownloaded,
        EffectKind.Uploaded => TextKey.HumanEffectUploaded,
        EffectKind.FolderCreated => TextKey.HumanEffectFoldersCreated,
        EffectKind.DeletedLocal or EffectKind.DeletedDropbox => TextKey.HumanEffectRemoved,
        EffectKind.KeptBoth => TextKey.HumanEffectKeepBoth,
        EffectKind.Recorded => TextKey.HumanEffectRecorded,
        EffectKind.Forgotten => TextKey.HumanEffectForgotten,
        EffectKind.MovedLocal or EffectKind.MovedDropbox => TextKey.HumanEffectMoved,
        _ => TextKey.HumanEffectReplaced,
    };

    private static (TextKey Operation, TextKey Result) CardText(EffectKind kind) => kind switch
    {
        EffectKind.Uploaded => (TextKey.HumanOperationUpload, TextKey.HumanResultSynced),
        EffectKind.Downloaded => (TextKey.HumanOperationDownload, TextKey.HumanResultSynced),
        EffectKind.FolderCreated => (TextKey.HumanOperationFolderCreate, TextKey.HumanResultFolderCreated),
        EffectKind.DeletedLocal or EffectKind.DeletedDropbox =>
            (TextKey.HumanOperationRemove, TextKey.HumanResultRemovalCommitted),
        EffectKind.MovedLocal or EffectKind.MovedDropbox =>
            (TextKey.HumanOperationMove, TextKey.HumanResultMoveCommitted),
        EffectKind.KeptBoth => (TextKey.HumanOperationKeepBoth, TextKey.HumanResultKeptBoth),
        EffectKind.Recorded => (TextKey.HumanOperationRecorded, TextKey.HumanResultRecorded),
        EffectKind.Forgotten => (TextKey.HumanOperationForgotten, TextKey.HumanResultForgotten),
        _ => (TextKey.HumanOperationReplace, TextKey.HumanResultReplaced),
    };

    private static TextKey IssueLabel(IssueKind kind) => kind switch
    {
        IssueKind.Failure => TextKey.HumanIssueFailure,
        IssueKind.Tie => TextKey.HumanIssueTie,
        IssueKind.NameClash => TextKey.HumanIssueNameClash,
        IssueKind.Info => TextKey.HumanIssueInfo,
        IssueKind.Skipped => TextKey.HumanIssueSkipped,
        IssueKind.DriveChange => TextKey.HumanIssueDriveChange,
        _ => TextKey.HumanIssueRootIdentity,
    };

    private static string SideText(string side) =>
        EnumNames.TryExact<Side>(side, out var value)
            ? Text.Get(value == Side.Local ? TextKey.HumanSideLocal : TextKey.HumanSideDropbox)
            : Escape(side);

    private static void Count<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
    {
        counts.TryGetValue(key, out var count);
        counts[key] = count + 1;
    }

    private string? Malformed(int lineNumber)
    {
        if (_malformed == 0) _firstMalformedLine = lineNumber;
        _malformed++;
        return Deliver(Text.Get(TextKey.HumanMalformedRecord, (TextArg.Value, Number(lineNumber))), null, tight: true);
    }

    private sealed record OpenStage(TextKey Key, DateTimeOffset Started);

    // The completion record's counts are accepted only when every known value parses and each total still fits the
    // displayed integer, so an unreadable or overflowing record is reported malformed instead of aborting the view.
    private static bool TryParseCounts(string detail, out IReadOnlyDictionary<string, int> effects,
        out IReadOnlyDictionary<string, int> issues)
    {
        var effectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var issueCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var parsed = true;
        foreach (var pair in detail.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=');
            if (split.Length == 1) continue;
            if (!int.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                parsed = false;
                break;
            }
            if (EnumNames.TryExact<EffectKind>(split[0], out _)) effectCounts[split[0]] = count;
            else if (EnumNames.TryExact<IssueKind>(split[0], out _)) issueCounts[split[0]] = count;
        }
        effects = effectCounts;
        issues = issueCounts;
        return parsed &&
            effectCounts.Values.Sum(value => (long)value) <= int.MaxValue &&
            issueCounts.Values.Sum(value => (long)value) <= int.MaxValue;
    }
}
