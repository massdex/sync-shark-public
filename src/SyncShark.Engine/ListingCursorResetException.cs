namespace Massdex.SyncShark.Engine;

/// <summary>The provider invalidated this inventory cursor; only fresh acquisition can replace it.</summary>
public sealed class ListingCursorResetException(Exception inner) : Exception(null, inner);
