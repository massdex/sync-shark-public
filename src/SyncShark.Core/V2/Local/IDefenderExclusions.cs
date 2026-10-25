namespace Massdex.SyncShark.V2;

public interface IDefenderExclusions
{
    Task<DefenderExclusionView> ReadAsync(CancellationToken ct);
}
