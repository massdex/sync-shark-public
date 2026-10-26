namespace Massdex.SyncShark.V2;

internal sealed record JournalPending(long Serial, string Volume, JournalSizingState State);
