namespace Massdex.SyncShark;

public sealed record SyncActiveTransfer(string OperationId, string RelativePath, string Stage, string Detail);

public sealed record SyncTransferBatchProgress(int Queued, int InFlight, int Completed, int Failed,
    int Conflicts, int Deferred, int CompletedFiles, long PayloadBytesProcessed, long ElapsedMilliseconds,
    IReadOnlyList<SyncActiveTransfer> Active)
{
    public double FilesPerSecond => ElapsedMilliseconds > 0 ? CompletedFiles * 1000d / ElapsedMilliseconds : 0;
    public double BytesPerSecond => ElapsedMilliseconds > 0 ? PayloadBytesProcessed * 1000d / ElapsedMilliseconds : 0;

    public string Describe(int maximumPaths = 3)
    {
        var summary = $"{InFlight:N0} active · {Queued:N0} queued · {Completed:N0} completed · " +
                      $"{Failed:N0} failed · {Conflicts:N0} conflicts · {Deferred:N0} deferred\n" +
                      $"{FilesPerSecond:N1} files/s · {BytesPerSecond / 1024d:N1} KiB/s payload processed (batch average)";
        foreach (var item in Active.Take(maximumPaths))
            summary += "\n" + item.Stage + " — " + item.RelativePath + ": " + item.Detail;
        if (maximumPaths > 0 && Active.Count > maximumPaths)
            summary += $"\n+{Active.Count - maximumPaths:N0} other active files; see the operation log for every file.";
        return summary;
    }
}

// All access is through the pair runner's serialized reporting gate. Retained
// file events remain the detail log; this is a bounded active-set projection.
internal sealed class SyncTransferBatchTracker
{
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedFileIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SyncActiveTransfer> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Operation, string Phase), long> _bytes = new();
    private long _startedTick, _payloadBytes;
    private int _completedFiles;

    public void Register(IEnumerable<string> operationIds)
    {
        foreach (var id in operationIds)
            if (_states.TryAdd(id, "Queued")) _counts["Queued"] = _counts.GetValueOrDefault("Queued") + 1;
    }

    public SyncTransferBatchProgress Observe(SyncAuthorityLiveAction action)
    {
        var tick = Environment.TickCount64;
        if (_startedTick == 0) _startedTick = tick;
        _states.TryGetValue(action.OperationId, out var previous);
        var state = action.Phase switch
        {
            SyncActivityPhase.Completed => "Completed",
            SyncActivityPhase.Blocked or SyncActivityPhase.EffectUnknown => "Failed",
            SyncActivityPhase.Conflict => "Conflict",
            SyncActivityPhase.Superseded or SyncActivityPhase.Cancelled => "Deferred",
            SyncActivityPhase.Planned => "Queued",
            _ => "Active"
        };
        if (action.Stage == "Not attempted") state = "Deferred";
        if (state == "Completed" && action.ItemKind == "File" && _completedFileIds.Add(action.OperationId)) _completedFiles++;
        if (previous != state)
        {
            if (previous is not null) _counts[previous] = _counts.GetValueOrDefault(previous) - 1;
            _counts[state] = _counts.GetValueOrDefault(state) + 1;
        }
        _states[action.OperationId] = state;
        if (state == "Active")
            _active[action.OperationId] = new(action.OperationId, action.RelativePath, action.Stage, action.Detail);
        else _active.Remove(action.OperationId);
        // Hashing/readback is not another transfer. Upload bytes are explicitly
        // labelled processed/request bytes, not proof of provider commit.
        if (action.Progress is { Unit: "bytes processed" } progress &&
            (action.Stage == "Reading bytes for upload" || action.Stage == "Downloading to verified staging"))
        {
            var key = (action.OperationId, action.Stage);
            var before = _bytes.GetValueOrDefault(key);
            var current = Math.Max(before, progress.Completed);
            _payloadBytes += current - before;
            _bytes[key] = current;
        }
        return new(_counts.GetValueOrDefault("Queued"), _active.Count,
            _counts.GetValueOrDefault("Completed"), _counts.GetValueOrDefault("Failed"),
            _counts.GetValueOrDefault("Conflict"), _counts.GetValueOrDefault("Deferred"),
            _completedFiles, _payloadBytes, Math.Max(0, tick - _startedTick), _active.Values.ToArray());
    }
}
