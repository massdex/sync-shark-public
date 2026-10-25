namespace Massdex.SyncShark.V2;

internal delegate Task ProviderRecoveryRecord(
    string actionId,
    string path,
    RecoveryRef recovery,
    string pairId,
    CancellationToken ct);
