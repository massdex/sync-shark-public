namespace Massdex.SyncShark.Engine;

/// <summary>Aggregate progress of a durable folder removal, not proof of individual member outcomes.</summary>
public enum FolderRemovalPhase
{
    Prepared,
    Observed,
    SubmitArmed,
    Submitted,
    Terminal,
    Checking,
    Recovering,
    Complete,
    Unknown,
    Retained
}
