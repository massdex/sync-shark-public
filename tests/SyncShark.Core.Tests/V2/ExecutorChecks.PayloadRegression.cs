namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    internal static async Task RunPayloadRegressionAsync()
    {
        await UploadSessionStart_GateAsync();
        await UploadSessionStart_ConcurrencyCapAsync();
        await UploadSessionStart_BoundedRetryAsync();
        await UploadPipeline_Safety();
        await UploadPipeline_IsolatedFailureAndLostAck();
        Console.WriteLine("existing upload retry, concurrency, conditional safety and lost-ack checks passed");
    }
}
