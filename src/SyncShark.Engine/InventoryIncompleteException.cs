namespace Massdex.SyncShark.Engine;

/// <summary>A full listing could not complete; Repair makes no changes (D22). Reason is a Text sentence.</summary>
public sealed class InventoryIncompleteException(string reason, Exception? inner) : Exception(reason, inner);
