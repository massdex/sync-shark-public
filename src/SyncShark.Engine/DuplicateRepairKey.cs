namespace Massdex.SyncShark.Engine;

/// <summary>A duplicate-folder obligation's key: the shared side and the id there of X, the item bound to two folders
/// on the other side. Storage encodes Identity by its side's id rule, so a keyset read keeps the exact storage class.</summary>
public readonly record struct DuplicateRepairKey(Side Side, string Identity);
