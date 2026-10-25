namespace Massdex.SyncShark.V2;

internal sealed record ProviderEffectOwner(
    object Key,
    string PairId,
    ProviderRecoveryRecord RecordRecovery,
    ProviderCheckpointGroup RecordCheckpoints);
