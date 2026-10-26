using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>One durable-intent-backed folder waiting for its individual create-batch result.</summary>
internal sealed record PendingDropboxMkDir(string Path, ProviderEffectContext? Context, CancellationToken StopToken)
{
    internal int WriteOperationRetries { get; set; }
    internal RunInstrumentation? Instrumentation { get; } = RunInstrumentation.Current;
    internal IDisposable? WaitTiming { get; } = RunInstrumentation.Current?.Measure(RunTimingStage.BatchWait);
    internal TaskCompletionSource<PilotRemoteEntry> Result { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
