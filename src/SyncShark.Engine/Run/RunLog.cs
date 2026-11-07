using System.ComponentModel;
using System.Globalization;
using Massdex.SyncShark.Engine.Dropbox;
using System.Text;
using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>One run's log file (section 8.4, D35). A write or open failure sets Failed and stops further writes; events
/// are still counted and progress still goes to the CLI. Run lifetime: the log is created and disposed by one
/// repair or resolution operation, which supplies its run id and owns its writer. Field contents per RunEventKind:
/// docs/design/engine-files.md.</summary>
public sealed class RunLog : IDisposable, IProgress<RunEvent>
{
    // Log format tokens (literals rule): file name <pairId><NameSeparator><runId><LogExtension>, tab-separated fields.
    /// <summary>Separates the pair id from the run id in the log file name.</summary>
    public const string NameSeparator = ResourceNames.LogNameSeparator;
    public const string FieldSeparator = "\t";
    /// <summary>The canonical record's fixed column count.</summary>
    public const int RecordFieldCount = 6;
    public const int FieldTimestamp = 0;
    public const int FieldKind = 1;
    public const int FieldWhat = 2;
    public const int FieldPath = 3;
    public const int FieldDetail = 4;
    public const int FieldOutcome = 5;
    /// <summary>Kind of the closing summary line.</summary>
    public const string CompleteKind = "complete";
    public const string ProvisionalKind = "provisional";
    public const long CompletedOperation = 1;
    public const string EscapeTab = "\\t";
    public const string EscapeReturn = "\\r";
    public const string EscapeNewLine = "\\n";
    public const string EscapeBackslash = "\\\\";
    /// <summary>Maximum human line width; a narrower terminal may render less.</summary>
    public const int HumanWidth = 100;

    private const string UnicodeEscape = "\\u";
    private const string UnicodeFormat = "x4";
    private const int UnicodeDigitCount = 4;
    private const int UnicodePrefixLength = 2;
    private const int UnicodeEscapeLength = 5;
    private const string TimestampFormat = "O";
    public const string TrueText = "true";
    public const string FalseText = "false";
    private const string CountSeparator = "=";
    private const string CountJoiner = ";";

    private readonly SuppressingProgress _progress;
    private readonly object _gate = new();
    private readonly object _deliveryGate = new();
    private long _sequence;
    private long _deliveredSequence;
    private readonly Dictionary<string, int> _effects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _issues = new(StringComparer.Ordinal);
    private TextWriter? _writer;
    private bool _failed;
    private readonly HashSet<Exception> _reportedFailures = new(ReferenceEqualityComparer.Instance);
    private bool _completed;
    private bool _disposed;
    private readonly Dictionary<Side, ListingProgress> _listing = [];
    private readonly Dictionary<TextKey, long> _stages = [];
    private readonly Dictionary<TextKey, RunEvent> _stageProgress = [];
    private readonly Dictionary<object, (RunEvent Item, long Started, DateTimeOffset StartedAt)> _requests =
        new(ReferenceEqualityComparer.Instance);
    private readonly TimeProvider _time;
    private readonly HumanLog _human;
    private readonly bool _perFileDetail;

    /// <summary>Replaces one side's latest consumed facts without formatting, writing or notifying a sink.</summary>
    public void Observe(ListingProgress progress)
    {
        lock (_gate) _listing[progress.Side] = progress;
    }

    // One progress snapshot composes stage, listing and request facts into one ordered message set delivered under one sequence.
    /// <summary>Renders active stages and listing facts on the host's existing quiet signal.</summary>
    public bool ReportProgress(string? fallback = null)
    {
        string[] messages;
        long sequence;
        lock (_gate)
        {
            if (_disposed || _completed) return false;
            var stages = _stages.Select(stage => (_stageProgress[stage.Key],
                Detail: FormatStageProgress(_stageProgress[stage.Key], _time.GetElapsedTime(stage.Value)))).ToArray();
            var listings = _listing.Values.Select(FormatListing).ToArray();
            foreach (var (item, detail) in stages)
                Write(nameof(RunEventKind.Stage), item.StageKey.ToString()!, item.Path, detail, nameof(RunEvent.InProgress));
            foreach (var message in listings)
                Write(nameof(ListingProgress), string.Empty, string.Empty, message, string.Empty);
            var requests = _requests.Values.Select(request =>
                FormatRequest(request.Item, _time.GetElapsedTime(request.Started), request.StartedAt, waiting: true)).ToArray();
            foreach (var message in requests)
                Write(nameof(RunEventKind.ProviderRequest), string.Empty, string.Empty, message, string.Empty);
            // One leaf fact per concurrent activity: the newest open stage, each side's latest listing (at most
            // two) and one in-flight request. The diagnostic records above are unchanged; the human block no
            // longer prints every active sentence.
            var details = new List<string>();
            if (stages.Length > 0) details.Add(stages[^1].Detail);
            details.AddRange(listings);
            if (requests.Length > 0) details.Add(requests[^1]);
            messages = details.ToArray();
            if (messages.Length == 0)
            {
                if (fallback is null) return false;
                messages = [fallback];
                Write(nameof(RunEventKind.Stage), string.Empty, string.Empty, fallback, nameof(RunEvent.InProgress));
            }
            else
            {
                // One compact human block per quiet observation; the diagnostic records above are unchanged.
                messages = [_human.ProgressBlock(messages, _time.GetUtcNow()) ?? messages[0]];
            }
            Flush();
            sequence = ++_sequence;
        }
        Deliver(sequence, messages);
        return true;
    }

    /// <summary>Publishes the final consumed count once, after the sink has completed.</summary>
    public void FinishListing(Side side)
    {
        // Final counts are history: admit and deliver them in the same transport order as stages and waits.
        lock (_deliveryGate)
        {
            string message;
            lock (_gate)
            {
                if (!_listing.Remove(side, out var item)) return;
                message = FormatListing(item);
                Write(nameof(ListingProgress), side.ToString(), item.Path, message, string.Empty);
                Flush();
                _deliveredSequence = ++_sequence;
            }
            // The canonical record keeps the reversible escape; the console gets the display form, whose only
            // data value (the path) is escaped before it reaches the terminal (R1 review 1).
            PublishProgress(EscapeDisplay(message));
        }
    }

    private static string FormatListing(ListingProgress item)
    {
        var format = Text.Get(TextKey.ListingNumberFormat);
        var count = Text.Get(item.Baseline == 0 ? TextKey.ListingCount : TextKey.ListingEstimate,
            (TextArg.Count, item.Observations.ToString(format, CultureInfo.InvariantCulture)),
            (TextArg.SourceCount, item.Baseline.ToString(format, CultureInfo.InvariantCulture)),
            (TextArg.Value, item.Baseline == 0 ? string.Empty :
                ((double)item.Observations / item.Baseline).ToString(Text.Get(TextKey.ListingPercentFormat), CultureInfo.InvariantCulture)));
        return Text.Get(item.Side == Side.Local ? TextKey.StageListingLocal : TextKey.StageListingDropbox,
            (TextArg.Count, item.Units.ToString(format, CultureInfo.InvariantCulture)),
            (TextArg.Message, count), (TextArg.Path, item.Path));
    }

    private RunLog(string path, string pairId, string runId, Action<string> progress, TimeProvider time,
        bool perFileDetail, TextKey title, int width, TextKey mode)
    {
        _time = time;
        Path = path;
        RunId = runId;
        _perFileDetail = perFileDetail;
        _progress = new SuppressingProgress(progress, Failed);
        _human = new HumanLog(pairId, runId, path, title, perFileDetail, width, mode);
    }

    /// <summary>The run's id, part of the file name; Repair reports it in the summary.</summary>
    public string RunId { get; }

    /// <summary>Opens the run's log file through a writer with the supplied character buffer, or returns a
    /// failed log when the file cannot be opened. `mode` is the owner's Direct/Hosted/Preview fact for the header.</summary>
    public static RunLog Open(string folder, string pairId, string runId, Action<string> progress, int bufferCharacters,
        TimeProvider? time = null, bool perFileDetail = false, TextKey title = TextKey.HumanTitleRun,
        int width = HumanWidth, TextKey mode = TextKey.HumanModeDirect)
    {
        var clock = time ?? TimeProvider.System;
        var log = new RunLog(System.IO.Path.Combine(folder, pairId + NameSeparator + runId + ResourceNames.LogExtension),
            pairId, runId, progress, clock, perFileDetail, title, width, mode);
        try
        {
            log._writer = LogFiles.Open(log.Path, bufferCharacters);
        }
        catch (Exception error) when (LocalErrors.IsManagedIoFailure(error))
        {
            log._failed = true;
        }
        return log;
    }

    public string Path { get; }
    /// <summary>True after a file failure or failed final progress delivery; a healthy file remains writable.</summary>
    public bool HasFailed { get { lock (_gate) return _failed || _progress.HasFailed; } }

    /// <summary>Writes a failure line for the exception and its own message.</summary>
    public void Failed(Exception error) => Event(RunEvent.Failed(error));

    /// <summary>Writes a failure line for the exception with one classified reason in place of its message.</summary>
    public void Failed(Exception error, TextKey reason) => Event(RunEvent.Failed(error, reason));

    void IProgress<RunEvent>.Report(RunEvent item) => Event(item);

    /// <summary>Writes the line and counts it: Effect events by What (an EffectKind name), Issue events by What (an
    /// IssueKind name). Stage, issue, failure, wait and (with file detail) effect facts also reach the human
    /// projection; ordinary provider requests stay diagnostic only.</summary>
    public void Event(RunEvent item)
    {
        if (item.Kind == RunEventKind.ProviderRequest)
        {
            Request(item);
            return;
        }
        if (item.InProgress && item.StageKey is { } stage)
        {
            lock (_gate)
                if (!_disposed && !_completed && _stages.ContainsKey(stage))
                    _stageProgress[stage] = item with
                    {
                        Count = Math.Max(item.Count ?? 0, _stageProgress[stage].Count ?? 0),
                        Total = item.Total ?? _stageProgress[stage].Total,
                        Path = item.Path.Length == 0 ? _stageProgress[stage].Path : item.Path
                    };
            return;
        }
        string? human;
        // An effect the human view does not show never waits on the delivery gate: a slow console can then
        // never stall effect producers (R1 review 9). Its count, record and write stay under the state gate.
        if (item.Kind == RunEventKind.Effect && !_perFileDetail)
        {
            lock (_gate)
            {
                item = Present(item);
                Count(_effects, item.What);
                _human.CountEffect(item, _time.GetUtcNow());
                Write(item.Kind.ToString(), item.What, item.Path, item.Detail, item.Outcome);
            }
            return;
        }
        lock (_deliveryGate)
        {
            lock (_gate)
            {
                if (item.Kind == RunEventKind.Failed && item.Error is { } failure &&
                    !_reportedFailures.Add(failure is PrerequisiteStoppedException stopped ? stopped.InnerException! : failure))
                    return;
                if (item.StageKey is { } openStage)
                {
                    var elapsed = TimeSpan.Zero;
                    if (item.Ended)
                    {
                        if (_stages.Remove(openStage, out var started)) elapsed = _time.GetElapsedTime(started);
                        _stageProgress.Remove(openStage);
                    }
                    else
                    {
                        _stages[openStage] = _time.GetTimestamp();
                        _stageProgress[openStage] = item;
                    }
                    item = item with
                    {
                        What = openStage.ToString(),
                        Detail = StageDetail(openStage, item.Ended, item.Count, elapsed),
                        Outcome = item.Ended ? nameof(RunEvent.Ended) : string.Empty
                    };
                }
                item = Present(item);
                if (item.Kind == RunEventKind.Effect) Count(_effects, item.What);
                if (item.Kind == RunEventKind.Issue) Count(_issues, item.What);
                Write(item.Kind.ToString(), item.What, item.Path, item.Detail, item.Outcome);
                human = _human.Accept(item, _time.GetUtcNow());
                if (human is not null) _deliveredSequence = ++_sequence;
            }
            if (human is not null) PublishProgress(human);
        }
    }

    private void Request(RunEvent item)
    {
        if (item.RequestId is not { } id) return;
        lock (_deliveryGate)
        {
            string detail;
            lock (_gate)
            {
                if (_disposed || _completed) return;
                if (!item.Ended)
                {
                    _requests[id] = (item, _time.GetTimestamp(), _time.GetUtcNow());
                    return;
                }
                if (!_requests.Remove(id, out var request)) return;
                detail = FormatRequest(item, _time.GetElapsedTime(request.Started), request.StartedAt);
                Write(nameof(RunEventKind.ProviderRequest), item.What, item.Path, detail,
                    item.RequestResult?.ToString() ?? string.Empty);
                Flush();
                _deliveredSequence = ++_sequence;
            }
        }
    }

    private static string FormatRequest(RunEvent item, TimeSpan elapsed, DateTimeOffset started, bool waiting = false) =>
        Text.Get(waiting ? TextKey.RequestWaiting :
                item.Detail.Length == 0 ? TextKey.RequestEnded : TextKey.RequestEndedCause,
            (TextArg.Name, item.What), (TextArg.Path, item.Path),
            (TextArg.Value, started.ToString(TimestampFormat, CultureInfo.InvariantCulture)),
            (TextArg.Reason, item.RequestResult?.ToString() ?? Text.Get(TextKey.CountUnavailable)),
            (TextArg.Cause, item.Detail),
            (TextArg.Seconds, elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture)));

    private static string FormatStageProgress(RunEvent item, TimeSpan elapsed) =>
        Text.Get(TextKey.StageProgress,
            (TextArg.Name, Text.Get(item.StageKey!.Value)),
            (TextArg.Count, item.Count?.ToString(CultureInfo.InvariantCulture) ?? Text.Get(TextKey.CountUnavailable)),
            (TextArg.SourceCount, item.Total?.ToString(CultureInfo.InvariantCulture) ?? Text.Get(TextKey.CountUnavailable)),
            (TextArg.Path, item.Path),
            (TextArg.Seconds, elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The shared stage text, also used before a validated pair can own a run file.</summary>
    public static string StageDetail(TextKey stage, bool ended = false, long? count = null, TimeSpan elapsed = default) =>
        Text.Get(ended ? TextKey.StageEnded : TextKey.StageStarted,
            (TextArg.Name, Text.Get(stage)),
            (TextArg.Count, count?.ToString(CultureInfo.InvariantCulture) ?? Text.Get(TextKey.CountUnavailable)),
            (TextArg.Seconds, elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Committed effects of this run by EffectKind name (RepairSummary.Effects).</summary>
    public IReadOnlyDictionary<string, int> Effects => _effects;

    /// <summary>Issues raised in this run by IssueKind name (RepairSummary.IssuesByKind).</summary>
    public IReadOnlyDictionary<string, int> IssuesByKind => _issues;

    /// <summary>Writes a wait line and reports it as a distinct waiting block.</summary>
    public void Wait(WaitEvent item)
    {
        var seconds = ((long)item.Wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var detail = Text.Get(item.Reason, (TextArg.Seconds, seconds));
        // Wait notices are history, not replaceable snapshots; never coalesce them away.
        lock (_deliveryGate)
        {
            string? human;
            lock (_gate)
            {
                Write(nameof(RunEventKind.Wait), item.Reason.ToString(), "", detail, seconds);
                _deliveredSequence = ++_sequence;
                human = _human.Wait(detail, _time.GetUtcNow());
            }
            if (human is not null) PublishProgress(human);
        }
    }

    /// <summary>Flushes provisional counts before persistence without claiming completion.</summary>
    public void Provisional(RepairSummary summary)
    {
        lock (_gate)
        {
            Write(ProvisionalKind, string.Empty, string.Empty,
                string.Join(CountJoiner, Counts(summary.Effects).Concat(Counts(summary.IssuesByKind))), string.Empty);
            Flush();
        }
    }

    /// <summary>Observes an already persisted Repair without letting its final subscriber change that outcome.</summary>
    public void FinishRepair(RepairSummary summary)
    {
        Event(RunEvent.Stage(TextKey.StageFinish, ended: true, count: CompletedOperation));
        Complete(summary);
    }

    /// <summary>The completion line with the summary counts, then the human completion summary.</summary>
    public void Complete(RepairSummary summary)
    {
        // outcome = Completed (true|false); detail = Name=Count pairs, effects then issues (engine-files.md event table).
        var detail = string.Join(CountJoiner, Counts(summary.Effects).Concat(Counts(summary.IssuesByKind)));
        lock (_gate)
        {
            _completed = true;
            _listing.Clear();
            ++_sequence;
        }
        // Join admitted transport before returning; a checked quiet callback cannot start after completion.
        lock (_deliveryGate)
        {
            string? human;
            lock (_gate)
            {
                CloseInterruptedStages();
                Write(CompleteKind, "", "", detail, summary.Completed ? TrueText : FalseText);
                _deliveredSequence = ++_sequence;
                human = _human.Complete(summary.Completed, summary.Effects, summary.IssuesByKind, summary.Finished);
            }
            if (human is not null) PublishProgress(human);
        }
    }

    /// <summary>Flushes and closes the writer once.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseInterruptedStages();
            _listing.Clear();
            try
            {
                _writer?.Dispose();
            }
            catch (IOException)
            {
                _failed = true;
            }
            _writer = null;
        }
    }

    private void CloseInterruptedStages()
    {
        foreach (var (stage, started) in _stages)
            Write(nameof(RunEventKind.Stage), stage.ToString(), string.Empty,
                Text.Get(TextKey.StageInterrupted, (TextArg.Name, Text.Get(stage)),
                    (TextArg.Seconds, _time.GetElapsedTime(started).TotalSeconds.ToString(CultureInfo.InvariantCulture))),
                nameof(RunEventKind.Failed));
        _stages.Clear();
    }

    /// <summary>Projects semantic effect, issue and failure facts into the shared log representation.</summary>
    public static RunEvent Present(RunEvent item)
    {
        if (item.Error is not null || item.Cause is not null)
        {
            if (item.Kind == RunEventKind.Failed && item.Path.Length == 0)
                for (var error = item.Error; error is not null; error = error.InnerException)
                    if (error is ItemFailedException failed)
                    {
                        item = item with { Path = failed.Path };
                        break;
                    }
            var reason = FailureReason(item.Error);
            if (item.Kind == RunEventKind.Failed && item.Error is { } failure)
                reason += SyncActivityText.ApplicationLocation(failure);
            item = item with
            {
                What = item.IssueKind?.ToString() ?? FailureName(item.Error) ?? item.What,
                Detail = item.Cause is { } cause
                    ? Text.Get(TextKey.FailureDetail,
                        (TextArg.Name, Text.Get(cause, (TextArg.Path, item.Path), (TextArg.Message, string.Empty))),
                        (TextArg.Message, reason)) : reason,
                Outcome = item.Cause?.ToString() ?? item.Outcome
            };
        }
        return item.EffectKind is { } effect
            ? item with { What = effect.ToString(), Outcome = item.Side?.ToString() ?? string.Empty } : item;
    }

    /// <summary>A stopped prerequisite attempt presents as the cancellation the log and its human projection already
    /// name, so a stop stays one outcome instead of a new failure vocabulary.</summary>
    private static string? FailureName(Exception? error) => error switch
    {
        null => null,
        PrerequisiteStoppedException => nameof(OperationCanceledException),
        _ => error.GetType().Name
    };

    private static string FailureReason(Exception? error)
    {
        if (error is null) return string.Empty;
        if (error is AggregateException aggregate)
            return string.Join(Environment.NewLine, aggregate.Flatten().InnerExceptions.Select(FailureReason));
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is ProviderFailureException provider)
                return Text.Get(TextKey.ProviderFailure, (TextArg.Name, provider.Route),
                    (TextArg.Count, provider.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                    (TextArg.Message, DropboxErrors.SafeProviderTag(provider.Tag)));
            if (current is DropboxApiException api)
                return Text.Get(TextKey.ProviderFailure,
                    (TextArg.Name, api.Route ?? string.Empty),
                    (TextArg.Count, ((int?)api.StatusCode)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                    (TextArg.Message, DropboxErrors.SafeProviderTag(api.SafeReason)));
            if (current is EngineRequestException request) return request.Key.ToString();
            if (current is ItemFailedException item)
                return item.Cause + CountJoiner + FailureReason(item.InnerException);
            if (current is LiveAdmissionStaleException stale)
                return stale.Detail is null ? stale.Reason.ToString() : stale.Reason + CountJoiner + stale.Detail;
            if (current is Win32Exception native)
                return Text.Get(TextKey.FailureNativeCode, (TextArg.Name, native.GetType().Name),
                    (TextArg.Count, native.NativeErrorCode.ToString(CultureInfo.InvariantCulture)));
        }
        var root = error.GetBaseException();
        return Text.Get(TextKey.FailureCode, (TextArg.Name, root.GetType().Name),
            (TextArg.Count, root.HResult.ToString(CultureInfo.InvariantCulture)));
    }

    private static string SafeProviderTag(string value)
    {
        foreach (var character in value)
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or DropboxProtocol.ErrorSummarySeparator or '.'))
                return Text.Get(TextKey.ProviderReasonUnavailable);
        return value.Length == 0 ? Text.Get(TextKey.ProviderReasonUnavailable) : value;
    }

    // The human projection has already escaped every data value; escaping the block again would flatten its
    // layout newlines into literal \n (R1 review 1). The canonical TSV writer keeps its own reversible escape.
    private void PublishProgress(string message) => _progress.Publish(message);

    // Serialize only transport delivery, never holding the log's state/file gate across callbacks.
    private void Deliver(long sequence, string[] messages)
    {
        lock (_deliveryGate)
        {
            if (sequence <= _deliveredSequence) return;
            _deliveredSequence = sequence;
            foreach (var message in messages)
            {
                lock (_gate)
                    if (_disposed || _completed || sequence < _sequence) return;
                PublishProgress(message);
            }
        }
    }

    private static IEnumerable<string> Counts(IReadOnlyDictionary<string, int> counts) =>
        counts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + CountSeparator + pair.Value.ToString(CultureInfo.InvariantCulture));

    private static void Count(Dictionary<string, int> counts, string what)
    {
        counts.TryGetValue(what, out var count);
        counts[what] = count + 1;
    }

    private void Write(string kind, string what, string path, string detail, string outcome)
    {
        if (_failed || _writer is null) return;
        try
        {
            _writer.WriteLine(string.Join(FieldSeparator,
                _time.GetUtcNow().ToString(TimestampFormat, CultureInfo.InvariantCulture),
                Escape(kind), Escape(what), Escape(path), Escape(detail), Escape(outcome)));
        }
        catch (IOException)
        {
            _failed = true;
            return;
        }
        // Important boundaries flush early; routine tail loss is the accepted limit.
        if (kind == nameof(RunEventKind.Stage) || kind == nameof(RunEventKind.Issue)
            || kind == nameof(RunEventKind.Failed) || kind == CompleteKind)
        {
            Flush();
        }
    }

    private void Flush()
    {
        try
        {
            _writer?.Flush();
        }
        catch (IOException)
        {
            _failed = true;
        }
    }

    private static string Escape(string value) => EscapeValue(value, escapeBackslash: true);

    /// <summary>Escapes control characters from a data value for display, leaving a Windows path's own separators
    /// readable. The canonical file keeps the fully reversible <see cref="Escape"/>.</summary>
    public static string EscapeDisplay(string value) => EscapeValue(value, escapeBackslash: false);

    // One escape loop for both forms; only the reversible file form also escapes backslashes.
    private static string EscapeValue(string value, bool escapeBackslash)
    {
        if (value.Length == 0) return value;
        var escapeAt = 0;
        while (escapeAt < value.Length && !char.IsControl(value[escapeAt]) &&
               !(escapeBackslash && value[escapeAt] == '\\')) escapeAt++;
        if (escapeAt == value.Length) return value;
        var escaped = new StringBuilder(value.Length);
        escaped.Append(value.AsSpan(..escapeAt));
        for (var index = escapeAt; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '\\' when escapeBackslash: escaped.Append(EscapeBackslash); break;
                case '\t': escaped.Append(EscapeTab); break;
                case '\r': escaped.Append(EscapeReturn); break;
                case '\n': escaped.Append(EscapeNewLine); break;
                default:
                    if (char.IsControl(value[index]))
                        escaped.Append(UnicodeEscape).Append(((int)value[index]).ToString(UnicodeFormat, CultureInfo.InvariantCulture));
                    else escaped.Append(value[index]);
                    break;
            }
        }
        return escaped.ToString();
    }

    /// <summary>Reverses the canonical escape, or reports a malformed record rather than guessing.</summary>
    public static bool TryUnescape(string value, out string result)
    {
        result = value;
        if (value.IndexOf('\\') < 0) return true;
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }
            if (index + 1 >= value.Length) return false;
            switch (value[index + 1])
            {
                case 't': builder.Append('\t'); index++; break;
                case 'r': builder.Append('\r'); index++; break;
                case 'n': builder.Append('\n'); index++; break;
                case '\\': builder.Append('\\'); index++; break;
                case 'u':
                    if (index + UnicodeEscapeLength >= value.Length) return false;
                    if (!ushort.TryParse(value.AsSpan(index + UnicodePrefixLength, UnicodeDigitCount),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) return false;
                    builder.Append((char)code);
                    index += UnicodeEscapeLength;
                    break;
                default: return false;
            }
        }
        result = builder.ToString();
        return true;
    }

    /// <summary>Decodes one canonical six-field record, or reports it malformed. Only exact declared vocabulary is
    /// accepted, so a numeric or unknown enum spelling never reaches a text lookup (R1 review 3).</summary>
    public static bool TryDecodeRecord(string line, out LogRecord record)
    {
        record = null!;
        var fields = line.Split(FieldSeparator);
        if (fields.Length != RecordFieldCount) return false;
        if (!DateTimeOffset.TryParse(fields[FieldTimestamp], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var at)) return false;
        if (!TryUnescape(fields[FieldKind], out var kind) ||
            !TryUnescape(fields[FieldWhat], out var what) ||
            !TryUnescape(fields[FieldPath], out var path) ||
            !TryUnescape(fields[FieldDetail], out var detail) ||
            !TryUnescape(fields[FieldOutcome], out var outcome)) return false;
        if (!ValidGrammar(kind, what, outcome)) return false;
        record = new LogRecord(at, kind, what, path, detail, outcome);
        return true;
    }

    // The record schema's own grammar: finite vocabularies only, so a reader's memory cannot grow with the file.
    private static bool ValidGrammar(string kind, string what, string outcome)
    {
        // A completion record's outcome is the closed two-value vocabulary; any other spelling is malformed, never
        // silently read as "incomplete" (R1 review 3).
        if (kind == CompleteKind) return outcome is TrueText or FalseText;
        if (kind == ProvisionalKind || kind == nameof(ListingProgress)) return true;
        if (!EnumNames.TryExact<RunEventKind>(kind, out var eventKind)) return false;
        return eventKind switch
        {
            RunEventKind.Stage => (what.Length == 0 || EnumNames.TryExact<TextKey>(what, out _)) && StageOutcome(outcome),
            RunEventKind.Effect => EnumNames.TryExact<EffectKind>(what, out _) &&
                (outcome.Length == 0 || EnumNames.TryExact<Side>(outcome, out _)),
            RunEventKind.Issue => EnumNames.TryExact<IssueKind>(what, out _) && CauseName(outcome),
            RunEventKind.Failed => CauseName(outcome),
            RunEventKind.Wait => what.Length == 0 || EnumNames.TryExact<TextKey>(what, out _),
            RunEventKind.ProviderRequest => outcome.Length == 0 || EnumNames.TryExact<RequestResult>(outcome, out _),
            _ => true,
        };
    }

    private static bool StageOutcome(string outcome) => outcome.Length == 0 ||
        outcome == nameof(RunEvent.InProgress) || outcome == nameof(RunEvent.Ended) ||
        outcome == nameof(RunEventKind.Failed);

    private static bool CauseName(string outcome) => outcome.Length == 0 || EnumNames.TryExact<TextKey>(outcome, out _);
}
