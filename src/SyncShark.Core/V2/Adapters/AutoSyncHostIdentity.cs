namespace Massdex.SyncShark.V2;

public sealed record AutoSyncHostIdentity(string InstanceId, int Pid, DateTimeOffset StartedUtc)
{
    public double UptimeSeconds => Math.Max(0, (DateTimeOffset.UtcNow - StartedUtc).TotalSeconds);
}
