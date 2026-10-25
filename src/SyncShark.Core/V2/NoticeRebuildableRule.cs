namespace Massdex.SyncShark.V2;

/// <summary>A first-sync rebuildable rule whose scoped prefix matched no folder. The category and prefix are the
/// rule's stable identity: changing either is a different rule, so a kept notice is not raised again for it.</summary>
public sealed record NoticeRebuildableRule(string Category, string Prefix);
