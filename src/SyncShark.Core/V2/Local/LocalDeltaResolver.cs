namespace Massdex.SyncShark.V2;

/// <summary>The inputs one journal-delta resolution needs, supplied by the local inventory.</summary>
internal sealed record ResolveContext(ulong RootFileId, uint Volume, Func<ulong, ItemState?> KnownById,
    int MaxPaths, Func<NtfsUsnChange, string, CancellationToken, IReadOnlyList<string>?> FindNames,
    Func<ulong, ItemState?, bool>? Ambiguous = null, Action<string>? Fallback = null);

/// <summary>Resolves NTFS journal changes into the exact pair-relative paths to re-scan; bounded parent-name and identity reads.</summary>
internal static class LocalDeltaResolver
{
    // USN_REASON_* bits the resolver reasons about, named at their owner.
    private const uint ReasonCreate = 0x100;
    private const uint ReasonDelete = 0x200;
    private const uint ReasonRenameOld = 0x1000;
    private const uint ReasonRenameNew = 0x2000;
    private const uint ReasonHardLink = 0x10000;
    private const uint ReasonReparse = 0x100000;
    private const uint RootFatal = ReasonDelete | ReasonRenameOld | ReasonRenameNew | ReasonReparse;
    private const uint CreateOrRename = ReasonCreate | ReasonRenameOld | ReasonRenameNew;

    /// <summary>The distinct paths to re-scan, or null when the change set cannot be resolved safely.</summary>
    public static Task<IReadOnlyList<string>?> ResolveAsync(IReadOnlyList<NtfsUsnChange> changes,
        ResolveContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(Resolve(changes, context, ct));
    }

    private static IReadOnlyList<string>? Resolve(IReadOnlyList<NtfsUsnChange> changes, ResolveContext context, CancellationToken ct)
    {
        IReadOnlyList<string>? Reject(string reason) { context.Fallback?.Invoke(reason); return null; }
        var collected = new List<string>();
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();
            if ((change.Reason & ReasonHardLink) != 0) return Reject("hard-link journal change");
            if (change.FileId == context.RootFileId)
            {
                if ((change.Reason & RootFatal) != 0) return Reject("root renamed, deleted or reparse identity changed");
                continue;
            }
            var known = context.KnownById(change.FileId);
            if (context.Ambiguous?.Invoke(change.FileId, known) == true) return Reject("changed object failed single-link identity check");
            if (known is not null) collected.Add(known.Path);
            var parent = ParentPath(change, context);
            if (parent is null) continue;
            if (change.Name.Length == 0)
            {
                // An unprivileged journal record omits the name; a content change at a known path needs no re-scan.
                if (known is not null && (change.Reason & CreateOrRename) == 0) continue;
                // Empty is not absence evidence; null requests full reconciliation.
                var names = context.FindNames(change, parent, ct);
                if (names is null) return Reject("parent name lookup failed");
                collected.AddRange(names);
                continue;
            }
            if (!IsUsableName(change.Name)) return Reject("journal name is not a safe child name");
            var namedPath = parent.Length == 0 ? change.Name : parent + "/" + change.Name;
            if (context.Ambiguous?.Invoke(change.FileId, new ItemState(namedPath,
                (change.Attributes & FileAttributes.Directory) != 0 ? ItemKind.Folder : ItemKind.File,
                SyncLocalEndpointStore.ObjectId(context.Volume, change.FileId), null, null, 0, default, ItemFlags.None)) == true)
                return Reject("named object failed single-link identity check");
            collected.Add(namedPath);
        }
        return Finalize(collected, context.MaxPaths, context.Fallback);
    }

    private static string? ParentPath(NtfsUsnChange change, ResolveContext context) =>
        change.ParentFileId == context.RootFileId ? string.Empty :
        context.KnownById(change.ParentFileId) is { Kind: ItemKind.Folder } folder ? folder.Path : null;

    private static bool IsUsableName(string name) =>
        name is not ("." or "..") && name.IndexOfAny(['/', '\\', ':', '\0']) < 0;

    // A case-only rename yields two ordinal spellings of one case-insensitive path; re-scan the real
    // on-disk case by scanning that item's parent folder instead, and refuse to scan the whole root.
    private static IReadOnlyList<string>? Finalize(List<string> collected, int maxPaths, Action<string>? fallback)
    {
        var groups = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in collected)
        {
            if (!groups.TryGetValue(path, out var spellings))
                groups[path] = spellings = new SortedSet<string>(StringComparer.Ordinal);
            spellings.Add(path);
        }
        var result = new List<string>();
        foreach (var spellings in groups.Values)
        {
            var path = spellings.First();
            if (spellings.Count == 1)
            {
                result.Add(path);
                continue;
            }
            var slash = path.LastIndexOf('/');
            if (slash < 0) { fallback?.Invoke("case-only rename at root"); return null; }
            result.Add(path[..slash]);
        }
        var distinct = result.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (distinct.Length > maxPaths) { fallback?.Invoke("resolved path limit exceeded"); return null; }
        return distinct;
    }
}
