namespace Massdex.SyncShark.Engine;

/// <summary>One binding's store proofs for a Repair attempt (-7 1.4): V1-V4 over the obligation, then P2.</summary>
public enum DuplicateRepairProof
{
    /// <summary>Every proof passes.</summary>
    Valid,
    /// <summary>V1 or V2 fails: the obligation or its recorded bindings changed.</summary>
    Changed,
    /// <summary>V4 fails: other custody overlaps a binding.</summary>
    Busy,
    /// <summary>V3 fails: an other-side id is bound more than once.</summary>
    LinkedElsewhere,
    /// <summary>P2 fails: synced items are recorded inside the binding.</summary>
    History
}
