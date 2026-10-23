namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal const string EffectsLedgerIncompleteReason = "effects-ledger-incomplete";
    /// <summary>Optional diagnostic observation after the result transaction and store gate are released.
    /// Subscribers must not throw or alter persistence. A batch is bounded by the caller's commit batch.
    /// The run explicitly subscribes/unsubscribes; no execution-context propagation is required.</summary>
    internal event Action<PairDef, IReadOnlyList<CommittedAction>>? ResultsCommitted;
}
