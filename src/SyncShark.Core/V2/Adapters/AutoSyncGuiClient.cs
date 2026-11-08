namespace Massdex.SyncShark.V2;

/// <summary>A GUI session is a client only: closing it never closes the shared engine.</summary>
public sealed class AutoSyncGuiClient(AutoSyncHostClient client)
{
    public Task<AutoSyncHostReply?> StatusAsync(CancellationToken ct = default) => Send("status", ct: ct);
    public Task<AutoSyncHostReply?> SetEnabledAsync(string? pairId, bool enabled, CancellationToken ct = default) =>
        Send(enabled ? "enable" : "disable", pairId, pairId is null, ct: ct);
    public Task<AutoSyncHostReply?> SetIntervalAsync(int minutes, CancellationToken ct = default) =>
        Send("interval", minutes: minutes, ct: ct);
    public Task<AutoSyncHostReply?> RunNowAsync(string pairId, CancellationToken ct = default) => Send("now", pairId, ct: ct);
    public Task<AutoSyncHostReply?> HistoryAsync(string pairId, CancellationToken ct = default) => Send("history", pairId, ct: ct);
    private Task<AutoSyncHostReply?> Send(string command, string? pairId = null, bool all = false, int? minutes = null,
        CancellationToken ct = default) => client.TrySendAsync(AutoSyncHostClient.Request(command, pairId, all, minutes),
            TimeSpan.FromMilliseconds(500), ct);
}
