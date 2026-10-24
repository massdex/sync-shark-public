namespace Massdex.SyncShark.V2;

public partial interface IStateStore
{
    /// <summary>Whether complete replay windows can hand off planning before the run finishes.</summary>
    bool SupportsJournalWindows => false;
    /// <summary>Persist a complete window and its planning debt before advancing the cursor.</summary>
    void CheckpointJournalWindow(Delta delta) => throw new NotSupportedException();
}
