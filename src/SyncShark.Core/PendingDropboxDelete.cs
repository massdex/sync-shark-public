using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>One durable-intent-backed file waiting for its individual delete batch result.</summary>
internal sealed record PendingDropboxDelete(PilotRemoteEntry Expected, ProviderEffectContext? Context,
    CancellationToken StopToken)
{
    internal int ThrottleCount { get; set; }
    internal int WriteOperationRetries { get; set; }
    internal RunInstrumentation? Instrumentation { get; } = RunInstrumentation.Current;
    internal IDisposable? WaitTiming { get; } = RunInstrumentation.Current?.Measure(RunTimingStage.BatchWait);
    internal TaskCompletionSource Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
