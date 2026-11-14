namespace Massdex.SyncShark.Engine;

/// <summary>The classifier's answer for one obligation (-6 3.5.2): the first of its checks that decides.</summary>
public enum DuplicateRepairState
{
    /// <summary>Row 2: an existing Failure holds it and X still has the recorded two bindings.</summary>
    Held,
    /// <summary>Rows 2' and 4: X no longer has exactly the recorded two bindings.</summary>
    Obsolete,
    /// <summary>Row 3: an unresolved Repair operation of its own exists.</summary>
    Own,
    /// <summary>Row 5: both bindings are outside the pair's scope.</summary>
    Excluded,
    /// <summary>Row 6: other custody overlaps a binding.</summary>
    Waiting,
    /// <summary>Row 7: an other-side id is bound more than once.</summary>
    LinkedElsewhere,
    /// <summary>Row 8: every check passes; the attempt runs.</summary>
    Valid
}
