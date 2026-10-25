namespace Massdex.SyncShark.V2;

public interface IDefenderHistory
{
    Task<DefenderHistoryResult> ReadAsync(CancellationToken ct);
}
