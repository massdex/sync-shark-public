namespace Massdex.SyncShark.Engine;

/// <summary>One listing step: Entry != null = present at Path; null = Path and its descendants are gone.</summary>
public sealed record Listed(string Path, Entry? Entry);
