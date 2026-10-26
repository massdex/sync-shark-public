namespace Massdex.SyncShark.V2;

public sealed record DeleteNoticePage(IReadOnlyList<DeleteNoticeItem> Items, string? NextToken);
