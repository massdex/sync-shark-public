namespace Massdex.SyncShark.Engine;

/// <summary>The requested acquisition root or range could not be listed; it proves no absence. A failed child is a scoped skip, not this exception. Reason is a Text sentence.</summary>
public sealed class InventoryIncompleteException(string reason, Exception? inner) : Exception(reason, inner);
