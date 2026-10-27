namespace Massdex.SyncShark.V2;

internal sealed record PrepareNarrowing(string Digest,
    Func<string, bool, bool> Removed, Func<string, bool> ScopeRemoved,
    Func<int, string> Metadata);
