namespace Massdex.SyncShark.V2;

/// <summary>One bounded page of intents plus the exclusive keyset cursor for the next page.</summary>
public sealed record IntentPage(IReadOnlyList<SyncAction> Items, string? NextCursor);
