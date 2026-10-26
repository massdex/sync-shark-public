namespace Massdex.SyncShark;

/// <summary>One page of entries with its continuation cursor.</summary>
public sealed record DropboxCliPageData(IReadOnlyList<DropboxCliEntry> Entries, string? Cursor, bool HasMore,
    bool IsDeleted = false) : IDropboxCliData;
