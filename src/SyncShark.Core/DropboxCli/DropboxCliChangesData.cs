namespace Massdex.SyncShark;

/// <summary>The latest cursor, a long-poll's answer, or one change page.</summary>
public sealed record DropboxCliChangesData(string Cursor, bool? Changes, int? BackoffSeconds,
    IReadOnlyList<DropboxCliEntry>? Entries, bool? HasMore) : IDropboxCliData;
