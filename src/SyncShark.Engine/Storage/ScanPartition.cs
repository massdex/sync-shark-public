namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Process-local authority and borrower lifetime for one disposable scan partition.</summary>
internal sealed class ScanPartition(ScanRecord record, long pairRow)
{
    internal ScanRecord Record { get; } = record;
    internal long PairRow { get; } = pairRow;
    internal byte[] Key { get; } = record.Token.ToByteArray();
    internal bool Active { get; set; } = true;
    internal bool FreshLocal { get; set; } = true;
    internal bool ResumedHistory { get; set; }
    internal bool IncrementalHistory { get; set; }
    internal int Borrowers { get; set; }
}
