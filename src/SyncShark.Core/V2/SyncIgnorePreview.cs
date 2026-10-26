namespace Massdex.SyncShark;

public sealed record SyncIgnorePreview(int MatchCount, IReadOnlyList<string> Examples);
