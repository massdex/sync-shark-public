namespace Massdex.SyncShark.V2;

/// <summary>One bounded page of unresolved intent rows in rowid order, plus the next exclusive cursor.</summary>
public sealed record IntentRowsPage(IReadOnlyList<IntentRow> Items, string? NextCursor);
