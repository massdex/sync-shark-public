namespace Massdex.SyncShark.V2;

internal delegate Task ProviderCheckpointGroup(
    IReadOnlyList<(SyncAction Action, ProviderEffectCheckpoint Checkpoint)> checkpoints);
