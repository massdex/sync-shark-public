namespace Massdex.SyncShark.Engine;

/// <summary>One binding's store proofs for a Repair attempt: the obligation's own proofs, then no saved history.</summary>
public enum DuplicateRepairProof
{
    /// <summary>Every proof passes.</summary>
    Valid,
    /// <summary>The obligation or its recorded bindings changed.</summary>
    Changed,
    /// <summary>Another operation's custody overlaps a binding.</summary>
    Busy,
    /// <summary>An other-side id is bound more than once.</summary>
    LinkedElsewhere,
    /// <summary>Synced items are recorded inside the binding.</summary>
    History
}
