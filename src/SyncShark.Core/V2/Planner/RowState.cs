namespace Massdex.SyncShark.V2;

/// <summary>How one side of a joined row compares with its baseline (planner-internal).</summary>
internal enum RowState
{
    /// <summary>No current item and no evidence; never act.</summary>
    Unknown,

    /// <summary>Matched an ignore rule (rule 31).</summary>
    Ignored,

    /// <summary>Flagged or unhashable (rule 30).</summary>
    Unreadable,

    /// <summary>Unchanged since the baseline (rule 1).</summary>
    Same,

    /// <summary>Edited in place (rule 2).</summary>
    Edited,

    /// <summary>Moved, content unchanged (rules 9, 20, 25).</summary>
    Moved,

    /// <summary>Moved and edited on one side (rule 28).</summary>
    MovedEdited,

    /// <summary>Kind changed, e.g. file to folder (rule 26).</summary>
    Replaced,

    /// <summary>Absent with deletion evidence (rule 6).</summary>
    Deleted,

    /// <summary>Present with no baseline (rules 16, 18).</summary>
    Present,
}
