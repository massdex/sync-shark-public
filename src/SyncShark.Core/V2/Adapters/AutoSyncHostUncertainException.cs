namespace Massdex.SyncShark.V2;

public sealed class AutoSyncHostUncertainException(string requestId, Exception inner)
    : IOException("Stopped waiting for AutoSync request " + requestId +
        ". It may still be running. Inspect status/history before deciding whether to retry; it was not replayed.", inner)
{
    public string RequestId { get; } = requestId;
}
