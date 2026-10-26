namespace Massdex.SyncShark.V2;

/// <summary>Bounded admission for journal metadata reads, not a verification or deletion policy.</summary>
internal static class JournalReadBudget
{
    internal const int Records = V2Settings.JournalRecordsPerPage;
    internal const int Objects = Records * 2;
    internal static readonly TimeSpan StoreTime = V2Settings.JournalStoreSlice;
}
