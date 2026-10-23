namespace Massdex.SyncShark.V2;

/// <summary>The inputs one journal-delta resolution needs, supplied by the local inventory.</summary>
internal sealed record ResolveContext(ulong RootFileId, uint Volume, Func<ulong, ItemState?> KnownById,
    int MaxPaths, Func<NtfsUsnChange, string, CancellationToken, IReadOnlyList<string>?> FindNames,
    Func<ulong, ItemState?, bool>? Ambiguous = null, Action<string>? Fallback = null,
    Action<NtfsUsnChange, ItemState?, string?, string>? Defer = null, Func<bool>? ScopeOverflow = null);
