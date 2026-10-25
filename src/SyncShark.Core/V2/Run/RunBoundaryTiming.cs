using System.Diagnostics;
using System.Globalization;

namespace Massdex.SyncShark.V2;

/// <summary>Cheap monotonic boundary measurements; no retained rows or effect on run authority.</summary>
internal sealed class RunBoundaryTiming(string phase, bool enabled) : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private bool _finished;
    internal long Entities { get; set; }
    internal long Aliases { get; set; }
    internal long PeakScopeRows { get; set; }

    public void Dispose()
    {
        if (_finished) return;
        _finished = true;
        if (!enabled) return;
        try
        {
            RunLog.Trace(string.Create(CultureInfo.InvariantCulture,
                $"run-boundary phase={phase} elapsed-ms={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F3} admitted-entities={Entities} admitted-aliases={Aliases} peak-scope-rows={PeakScopeRows}"));
        }
        catch { /* Diagnostic sinks must never interrupt a sync. */ }
    }
}
