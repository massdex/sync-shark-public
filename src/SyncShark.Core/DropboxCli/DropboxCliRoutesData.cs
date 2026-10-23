namespace Massdex.SyncShark;

/// <summary>The whole user-token route table, or the rows matching a filter.</summary>
public sealed record DropboxCliRoutesData(string SpecCommit, int Count, IReadOnlyList<DropboxCliRouteRow> Routes)
    : IDropboxCliData;
