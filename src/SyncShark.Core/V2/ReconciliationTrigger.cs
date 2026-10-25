namespace Massdex.SyncShark.V2;

/// <summary>Selects the existing change-feed evidence used to find affected files.</summary>
internal enum ReconciliationTrigger
{
    JournalReplay,
    GapSameJournal,
    JournalReplaced,
    JournalPositionInvalid,
    DropboxReset,
    OverflowReplay
}
