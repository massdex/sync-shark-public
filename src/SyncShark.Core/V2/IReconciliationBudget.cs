namespace Massdex.SyncShark.V2;

/// <summary>Shared decision-read admission and observational effect counters; no run or storage authority.</summary>
internal interface IReconciliationBudget
{
    IReadOnlyList<string> Paths { get; }
    IReadOnlySet<string> AliasPaths { get; }
    bool OwnsDecisionPath(string path);
    bool AdmitDecisionHash(ItemState item);
    void RecordEffectRead(int bytes);
    void RecordEffectWrite(int bytes);
}
