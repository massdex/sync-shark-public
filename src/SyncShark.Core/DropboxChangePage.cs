namespace Massdex.SyncShark;

/// <summary>One parsed page of Dropbox change events with the continuation cursor and more flag.</summary>
internal sealed record DropboxChangePage(string Cursor, bool HasMore, IReadOnlyList<DropboxChange> Changes);
