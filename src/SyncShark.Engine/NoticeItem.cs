namespace Massdex.SyncShark.Engine;

/// <summary>One item an issue is about.</summary>
public sealed record NoticeItem(Side Side, string Path, string Id);
