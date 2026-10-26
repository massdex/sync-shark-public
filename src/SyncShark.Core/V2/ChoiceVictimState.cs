namespace Massdex.SyncShark.V2;

/// <summary>The outcome of one item frozen under a folder that an explicit conflict choice removes.</summary>
public enum ChoiceVictimState
{
    /// <summary>Not yet removed; a retry removes it only if it is still the frozen version.</summary>
    Pending = 0,
    /// <summary>Removed after its content was preserved (files) or after it was emptied (folders).</summary>
    Removed = 1,
    /// <summary>Changed since it was frozen, so it was kept.</summary>
    Kept = 2,
    /// <summary>Put back from Recovery by an explicit restore.</summary>
    Restored = 3,
    /// <summary>Already gone before this choice reached it.</summary>
    Absent = 4,
    /// <summary>Ignored content left to the enclosing folder writer's decision-80 preservation policy.</summary>
    Carried = 5,
    RestoredCopy = 6,

}
