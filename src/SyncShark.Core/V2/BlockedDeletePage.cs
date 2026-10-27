namespace Massdex.SyncShark.V2;

public sealed record BlockedDeletePage(IReadOnlyList<BlockedDeleteItem> Items, string? NextToken);
