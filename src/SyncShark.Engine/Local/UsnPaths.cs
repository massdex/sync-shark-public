namespace Massdex.SyncShark.Engine.Local;

/// <summary>Assembles one native USN page into the pair's ordered change notices (AutoSync 2.3). A full file
/// reference is resolved at most once per page; a saved mapping supplies the old path of a deleted or
/// renamed-away identity. History this owner cannot classify is never reported as absence: a known pair item's
/// unreadability is raised to the current issue owner, and genuinely unclassifiable history asks the caller for a
/// normal Repair obligation.</summary>
public sealed class UsnPaths
{
    // USN_REASON_* bits this owner reasons about; the OS values are frozen declarations (LocalDeltaResolver
    // named the legacy set).
    [Flags]
    private enum Reason : uint
    {
        DataOverwrite = 0x00000001,
        DataExtend = 0x00000002,
        DataTruncation = 0x00000004,
        Create = 0x00000100,
        Delete = 0x00000200,
        RenameOld = 0x00001000,
        RenameNew = 0x00002000,
        HardLink = 0x00010000,
        Reparse = 0x00100000,
        Close = 0x80000000
    }

    private const Reason ContentWrite = Reason.DataOverwrite | Reason.DataExtend | Reason.DataTruncation;
    private const Reason Relevant = ContentWrite | Reason.Create | Reason.Delete | Reason.RenameOld | Reason.RenameNew |
        Reason.HardLink | Reason.Reparse | Reason.Close;
    private const Reason RootFatal = Reason.Delete | Reason.RenameOld | Reason.RenameNew | Reason.Reparse;

    private readonly PairConfig _pair;
    private readonly IStateStore _store;
    private readonly LocalListing _listing;
    private readonly DirectoryIdentity _rootIdentity;
    private readonly List<(ulong FileId, ulong Usn, uint Reason, FileAttributes Attributes)> _page = new();
    private readonly Dictionary<ulong, (string? Relative, IReadOnlyList<string>? Links, Exception? Failure)> _resolved = new();
    private readonly Dictionary<ulong, Reason> _reasons = new();
    private readonly HashSet<ulong> _linksProcessed = new();
    internal Action<ulong>? BeforeIdentityRead { get; set; }

    /// <summary>Binds the assembly to one pair and its current local root identity.</summary>
    public UsnPaths(PairConfig pair, IStateStore store, LocalListing listing, DirectoryIdentity rootIdentity)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(rootIdentity);
        _pair = pair;
        _store = store;
        _listing = listing;
        _rootIdentity = rootIdentity;
    }

    /// <summary>True when the last page held relevant history this owner could not classify; the caller must
    /// install the normal Repair obligation instead of treating that reference as absent.</summary>
    public bool NeedsRepair { get; private set; }

    /// <summary>Starts one native page, reusing the previous page's arrays and dropping its resolved references.</summary>
    public void BeginPage()
    {
        _page.Clear();
        _resolved.Clear();
        _reasons.Clear();
        _linksProcessed.Clear();
        NeedsRepair = false;
    }

    /// <summary>Captures one native scalar record during its page; the signature matches
    /// <c>NtfsUsnJournal.ReadChangePageAsync</c>'s record callback. The native timestamp is not retained because
    /// the observed entry supplies the recorded time, and an irrelevant reason is dropped without allocating.</summary>
    public void Observe(ulong fileId, ulong parentFileId, ulong usn, long timestamp, uint reason,
        FileAttributes attributes)
    {
        if (((Reason)reason & Relevant) == 0) return;
        _page.Add((fileId, usn, reason, attributes));
        _reasons[fileId] = _reasons.GetValueOrDefault(fileId) | (Reason)reason;
    }

    /// <summary>Resolves the observed page into notices in provider order. A known pair item's typed failure is
    /// raised to the current issue owner only after the page's other notices have been delivered, so one unreadable
    /// item never drops the rest. Call <see cref="BeginPage"/> before each page.</summary>
    public IEnumerable<ChangeNotice> Resolve(CancellationToken ct, Action<ItemFailedException>? failed = null)
    {
        try
        {
            ItemFailedException? failure = null;
            foreach (var (fileId, usn, reason, attributes) in _page)
            {
                ct.ThrowIfCancellationRequested();
                ChangeNotice? notice;
                IReadOnlyList<ChangeNotice>? aliases;
                try
                {
                    notice = ResolveOne(fileId, usn, reason, attributes, ct, out aliases);
                }
                catch (ItemFailedException error)
                {
                    if (failed is null) failure ??= error;
                    else failed(error);
                    continue;
                }
                if (notice is not null) yield return notice;
                if (aliases is not null)
                    foreach (var alias in aliases) yield return alias;
            }
            if (failure is not null) throw failure;
        }
        finally
        {
            _page.Clear();
            _resolved.Clear();
            _reasons.Clear();
            _linksProcessed.Clear();
        }
    }

    // One USN record: a rename invalidates the file's cached resolution before it is resolved, the saved binding is looked up by id, and the notice takes its observation fresh from the listing owner.
    private ChangeNotice? ResolveOne(ulong fileId, ulong usn, uint reason, FileAttributes attributes, CancellationToken ct,
        out IReadOnlyList<ChangeNotice>? aliases)
    {
        aliases = null;
        var flags = (Reason)reason;
        if (fileId == _rootIdentity.FileId)
        {
            if ((flags & RootFatal) != 0) NeedsRepair = true;
            return null;
        }
        if (_linksProcessed.Contains(fileId)) return null;
        var folded = _reasons[fileId];
        var deleting = (flags & Reason.Delete) != 0;
        var renaming = (flags & (Reason.RenameOld | Reason.RenameNew)) != 0;
        // A rename changes this reference's current path, so an earlier resolution in the same page is stale.
        if (renaming) _resolved.Remove(fileId);
        var (relative, links, failure) = ResolvePath(fileId, (folded & Reason.HardLink) != 0, ct);
        var id = LocalIdentity.FileIdText(fileId);
        if (failure is not null)
        {
            // A known pair item's unreadability belongs to the current issue owner; an unknown system scope is
            // never guessed to be part of this pair.
            var mapped = Saved(id);
            if (mapped is null) return null;
            throw new ItemFailedException(mapped.LocalPath, LocalErrors.Cause(failure, TextKey.ReadLocalFailed),
                failure) { LocalId = id };
        }
        if ((attributes & FileAttributes.Directory) == 0 && (links is not null || (folded & Reason.HardLink) != 0))
        {
            _linksProcessed.Add(fileId);
            aliases = AliasNotices(id, links, folded, usn, ct);
            return null;
        }
        // Engine stage files are internal: their lifecycle belongs to the stage owner, never a user notice.
        if ((attributes & FileAttributes.Directory) == 0 && relative is not null &&
            StageNames.IsStage(PathNames.Name(relative))) return null;
        var saved = relative is null || renaming ? Saved(id) : null;
        var path = relative ?? saved?.LocalPath;
        if (path is null) return null;
        if ((attributes & FileAttributes.Directory) != 0 &&
            (deleting || renaming || (flags & Reason.Create) != 0))
            InvalidateDirectory(fileId, relative, saved?.LocalPath);
        var (kind, oldPath) = Classify(deleting, renaming, relative, saved);
        var observed = kind == ChangeKind.Deleted ? null : _listing.One(path);
        return new ChangeNotice(Side.Local, kind, path, oldPath, id, observed,
            (flags & ContentWrite) != 0, deleting, usn);
    }

    // A rename whose identity left this pair is a deletion at its saved path; a rename with no saved previous
    // path is a new item under its current path.
    private static (ChangeKind Kind, string? OldPath) Classify(bool deleting, bool renaming, string? relative,
        SyncedEntry? saved)
    {
        if (deleting || renaming && relative is null && saved is not null)
            return (ChangeKind.Deleted, null);
        if (renaming && relative is not null && saved is not null && relative != saved.LocalPath)
            return (ChangeKind.Renamed, saved.LocalPath);
        return (ChangeKind.Changed, null);
    }

    private IReadOnlyList<ChangeNotice> AliasNotices(string id, IReadOnlyList<string>? links, Reason flags,
        ulong usn, CancellationToken ct)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var saved in _store.SyncedBindingsById(_pair.PairId, Side.Local, id))
            paths.TryAdd(saved.LocalKey, saved.LocalPath);
        foreach (var full in links ?? [])
            if (PathNames.RelativeUnderLocalRoot(_pair.LocalRoot, full) is { } path)
                paths.TryAdd(PathNames.Key(path), path);
        var notices = new List<ChangeNotice>(paths.Count);
        foreach (var path in paths.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (StageNames.IsStage(PathNames.Name(path))) continue;
            var observed = _listing.One(path);
            notices.Add(new ChangeNotice(Side.Local, observed is null ? ChangeKind.Deleted : ChangeKind.Changed,
                path, null, null, observed, (flags & ContentWrite) != 0, false, usn));
        }
        return notices;
    }

    private SyncedEntry? Saved(string id) => _store.SyncedById(_pair.PairId, Side.Local, id);

    private (string? Relative, IReadOnlyList<string>? Links, Exception? Failure) ResolvePath(ulong fileId,
        bool forceEnumeration, CancellationToken ct)
    {
        if (_resolved.TryGetValue(fileId, out var cached)) return cached;
        (string? Relative, IReadOnlyList<string>? Links, Exception? Failure) result;
        try
        {
            BeforeIdentityRead?.Invoke(fileId);
            var (full, links) = NtfsIdentityPaths.ResolveLinksById(_pair.LocalRoot, _rootIdentity, fileId, forceEnumeration, ct);
            result = (full is null ? (string?)null : PathNames.RelativeUnderLocalRoot(_pair.LocalRoot, full), links, null);
        }
        catch (Exception error) when (error is not NtfsLinkEnumerationChangedException && LocalErrors.IsFileFailure(error))
        {
            result = (null, null, error);
        }
        _resolved[fileId] = result;
        return result;
    }

    // A directory change makes paths resolved earlier in the same page stale for that directory and its subtree;
    // unrelated references keep their resolution. Only a reference classified as this pair's reaches here.
    private void InvalidateDirectory(ulong directoryId, string? newPath, string? oldPath)
    {
        _resolved.Remove(directoryId);
        if (newPath is null && oldPath is null) return;
        List<ulong>? stale = null;
        foreach (var (id, (relative, _, _)) in _resolved)
        {
            if (relative is null) continue;
            if (newPath is not null && PathNames.IsSelfOrUnder(relative, newPath) ||
                oldPath is not null && PathNames.IsSelfOrUnder(relative, oldPath))
                (stale ??= new()).Add(id);
        }
        if (stale is null) return;
        foreach (var id in stale) _resolved.Remove(id);
    }
}
