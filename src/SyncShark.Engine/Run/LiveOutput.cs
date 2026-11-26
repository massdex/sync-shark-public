using System.Globalization;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>The readable live view of one run for the command that waits on it: each record's block, in record order,
/// and, when nothing else was shown for a quiet period, the newest open phase with its current file and real count.
/// Lines queue in memory so a slow reader never holds the run; a failed write stops later writes, and
/// <see cref="Dispose"/> waits until every queued line has been written.</summary>
public sealed class LiveOutput : IDisposable
{
    private readonly object _gate = new();
    private readonly TextWriter _output;
    private readonly List<(RunEvent Latest, long Started)> _open = new(); // in start order; the newest started is current
    private readonly TimeProvider _clock;
    private readonly ITimer _quiet;
    private readonly bool _perFile;
    private Task _tail = Task.CompletedTask;
    private volatile bool _failed;
    private bool _disposed, _shown;

    /// <summary>Writes the run's readable lines to the waiting command's output, timing quiet periods on the given clock;
    /// per-file effects are shown only when asked. A header first names the pair, the mode and the run id that `logs` reads.</summary>
    public LiveOutput(TextWriter output, TimeProvider clock, bool perFile = false, (string Pair, string Run)? header = null)
    {
        (_output, _clock, _perFile) = (output, clock, perFile);
        if (header is ({ } pair, { } run))
            lock (_gate) Print(string.Join(Environment.NewLine, Text.Get(TextKey.HumanHeaderTitle, (TextArg.Name, Text.Get(TextKey.HumanTitleRepair))),
                Text.Get(TextKey.HumanHeaderPair, (TextArg.Pair, pair)), Text.Get(TextKey.HumanHeaderMode, (TextArg.Mode, Text.Get(TextKey.HumanModeHosted)),
                    (TextArg.PerFile, Text.Get(perFile ? TextKey.HumanPerFileOn : TextKey.HumanPerFileOff))), Text.Get(TextKey.HumanHeaderRun, (TextArg.Run, run))));
        var period = TimeSpan.FromSeconds(EngineSettings.QuietSeconds);
        _quiet = clock.CreateTimer(_ => Quiet(), null, period, period);
    }

    /// <summary>Shows one record and tracks the open phases; a per-file effect is left to the log unless asked for.</summary>
    public void Accept(RunEvent value)
    {
        lock (_gate)
        {
            if (value.Kind == RunEventKind.Stage) Track(value);
            if (value.Kind == RunEventKind.Effect && !_perFile || value.Outcome == RunLog.InProgressOutcome) return;
            Print(LogText.Format(value));
        }
    }

    private void Track(RunEvent value)
    {
        var at = _open.FindIndex(open => open.Latest.What == value.What);
        if (value.Ended) { if (at >= 0) _open.RemoveAt(at); }
        else if (at >= 0) _open[at] = (value, _open[at].Started);
        else _open.Add((value, _clock.GetTimestamp()));
    }

    // Once per quiet period with nothing else shown: the newest open phase's file and count, else that it is still working.
    private void Quiet()
    {
        lock (_gate)
        {
            if (!_shown) Print(LogText.Heading(_open.Count == 0 ? Text.Get(TextKey.StatusStillQuiet) : Current(_open[^1])));
            _shown = false;
        }
    }

    private string Current((RunEvent Latest, long Started) open)
    {
        var name = EnumNames.TryExact<TextKey>(open.Latest.What, out var key) ? Text.Get(key) : RunLog.EscapeDisplay(open.Latest.What);
        if (open.Latest.Count is not { } count) return Text.Get(TextKey.StatusStillWorking, (TextArg.Name, name));
        return Text.Get(TextKey.StageProgress, (TextArg.Name, name), (TextArg.Count, count.ToString(CultureInfo.InvariantCulture)),
            (TextArg.SourceCount, Text.Get(TextKey.CountUnavailable)), (TextArg.Path, RunLog.EscapeDisplay(open.Latest.Path)),
            (TextArg.Seconds, _clock.GetElapsedTime(open.Started).TotalSeconds.ToString(CultureInfo.InvariantCulture)));
    }

    // Caller holds _gate, so lines keep the order they were accepted in.
    private void Print(string text)
    {
        if (_disposed || _failed || text.Length == 0) return;
        _shown = true;
        var line = text + Environment.NewLine;
        _tail = _tail.ContinueWith(_ =>
        {
            if (_failed) return;
            try { _output.Write(line); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { _failed = true; }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Stops accepting lines and waits for the queued ones, so none follows the command's exit.</summary>
    public void Dispose()
    {
        _quiet.Dispose();
        Task tail;
        lock (_gate)
        {
            _disposed = true;
            tail = _tail;
        }
        tail.Wait();
    }
}
