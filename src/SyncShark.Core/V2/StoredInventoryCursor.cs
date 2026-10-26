namespace Massdex.SyncShark.V2;

/// <summary>A pending inventory cursor captured by prepare or a successfully guarded apply repreview.</summary>
internal sealed record StoredInventoryCursor(string RootId, string Cursor);
