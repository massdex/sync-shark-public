namespace Massdex.SyncShark.V2;

internal interface IJournalStore
{
    void ObserveJournal(string pairId, string volume, long serial, ulong journal, long next,
        long maximum, long free, bool gap, DateTimeOffset now, bool deferNotice, long allocation);
    JournalPending? PendingJournal(long serial);
    void FinishSilentJournal(JournalPending request, bool grew, string explanation);
    (long Serial, JournalSizingState State) JournalRequest(Notice notice);
    void CompleteJournalChoice(Notice notice, string choice);
}
