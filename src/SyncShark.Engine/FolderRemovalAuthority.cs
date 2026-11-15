namespace Massdex.SyncShark.Engine;

/// <summary>
/// Who authorized a durable folder removal; the lease recheck and mount rule read it. Legacy marks an operation frozen
/// before the authority was recorded, and gets the strictest check.
/// </summary>
public enum FolderRemovalAuthority { Inferred, Chosen, Excluded, Vacate, Repair, Legacy }
