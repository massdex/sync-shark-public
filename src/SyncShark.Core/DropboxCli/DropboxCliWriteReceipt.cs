namespace Massdex.SyncShark;

/// <summary>A durable CLI effect receipt. No request payload or credential is retained.</summary>
public sealed record DropboxCliWriteReceipt(string ReceiptPath, string Command, string AccountId,
    string? NamespaceId, string Route, string Status, string? JobId, string? PollRoute, string Guidance)
{
    public string? Path { get; init; }
    public string? Destination { get; init; }
    public bool EffectUncertain => Status is "effect_unverified" or "job_pending";
}

