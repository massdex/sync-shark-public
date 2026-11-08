using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox listing and single-item read (section 5.3).</summary>
public sealed partial class DropboxListing(DropboxApi api, PairConfig pair, PathScope scope)
{
    // One queued spelling entry enters or leaves its ancestor accounting.
    private const int SpellingQueued = 1;
    private const int SpellingReleased = -1;

    // The listing envelope's field names, readable only through this owner (F23).
    internal static string EntriesName => DropboxProtocol.Entries;
    internal static string CursorName => DropboxProtocol.Cursor;
    internal static string HasMoreName => DropboxProtocol.HasMore;

    private readonly DropboxApi _api = api;
    private readonly PairConfig _pair = pair;
    private readonly PathScope _scope = scope;

    /// <summary>Lists one subtree page by page, correcting stale ancestor spellings and skipping the under
    /// root, reserved names and out-of-scope entries.</summary>
    public partial IAsyncEnumerable<Listed> ReadAll(string under, ListOptions options,
        ListingReadContext context, CancellationToken ct);

    public partial async IAsyncEnumerable<Listed> ReadAll(string under, ListOptions options,
        ListingReadContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
        ct.ThrowIfCancellationRequested();
        // History live rows are replaced by a fresh listing in PairScan; replay needs provider order, not spelling.
        var spelling = options.Cursor is null ? new Spelling(under) : null;
        var released = new List<Listed>();
        long pages = 0;
        var currentPath = under;
        var route = DropboxProtocol.ListFolder;
        object arg = new
        {
            path = DropboxPaths.Remote(_pair, under),
            recursive = options.Recursive,
            include_deleted = options.IncludeDeleted,
            include_mounted_folders = true,
            include_non_downloadable_files = true,
            limit = DropboxProtocol.ListPageMaximum
        };
        if (options.Cursor is { } cursor)
        {
            route = DropboxProtocol.ListFolderContinue;
            arg = new { cursor };
        }
        string? terminalCursor = null;
        options.Events?.Invoke(RunEvent.Stage(TextKey.StageDropboxPages));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            DropboxPage acquired;
            while (true)
            {
                var attempt = context.BeginAttempt();
                try { acquired = await ReadPageAsync(route, arg, under, attempt, ct).ConfigureAwait(false); }
                catch (Exception error)
                {
                    if (await context.CompleteAttemptAsync(attempt, ct).ConfigureAwait(false)) continue;
                    // A saved cursor the provider will not continue falls back to the fresh listing; a transient
                    // failure already spent its retry above, and a fresh listing keeps its InventoryIncomplete type.
                    if (options.Cursor is not null &&
                        error is InventoryIncompleteException { InnerException: HttpRequestException refusal } &&
                        (DropboxErrors.IsCursorReset(refusal) || !ProviderTransientFailure.IsTransient(refusal, ct)))
                        throw new ListingCursorResetException(error);
                    throw;
                }
                try { await context.CompleteAttemptAsync(attempt, ct).ConfigureAwait(false); }
                catch
                {
                    await acquired.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                break;
            }
            await using var page = acquired;
            if (!page.HasMore)
                options.Events?.Invoke(RunEvent.Stage(TextKey.StageDropboxPages, ended: true, count: pages + 1));
            using var entries = page.ReadEntries().GetEnumerator();
            while (true)
            {
                try
                {
                    if (!entries.MoveNext()) break;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw Incomplete(under, error);
                }
                currentPath = entries.Current.Path;
                var item = PathNames.Same(entries.Current.Path, under)
                    ? Requested(entries.Current, under) : entries.Current;
                if (spelling is null) released.Add(item);
                else spelling.Add(item, released);
                for (var index = 0; index < released.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Skipped(released[index], under, options)) yield return released[index];
                }
                released.Clear();
                ct.ThrowIfCancellationRequested();
            }
            pages++;
            if (!page.HasMore)
            {
                terminalCursor = page.Cursor;
                break;
            }
            options.Progress?.Invoke(currentPath, pages);
            options.Events?.Invoke(RunEvent.Stage(TextKey.StageDropboxPages, count: pages,
                inProgress: true, path: currentPath));
            route = DropboxProtocol.ListFolderContinue;
            arg = new { cursor = page.Cursor };
        }
        options.Events?.Invoke(RunEvent.Stage(TextKey.StageDropboxFlush));
        var retained = spelling?.Flush() ?? [];
        var flushed = 0;
        foreach (var item in retained)
        {
            options.Events?.Invoke(RunEvent.Stage(TextKey.StageDropboxFlush, count: flushed++,
                inProgress: true, path: item.Path, total: retained.Count));
            ct.ThrowIfCancellationRequested();
            if (!Skipped(item, under, options))
            {
                currentPath = item.Path;
                yield return item;
            }
        }
        options.Events?.Invoke(RunEvent.Stage(TextKey.StageDropboxFlush, ended: true, count: retained.Count));
        options.Progress?.Invoke(currentPath, pages);
        options.CursorAcquired?.Invoke(terminalCursor ?? throw new InvalidOperationException());
        }
        finally { await context.EndAsync().ConfigureAwait(false); }
    }

    private bool Skipped(Listed listed, string under, ListOptions options)
    {
        if (PathNames.Same(listed.Path, under)) return true;
        // Check the parent spelling, not page order: Dropbox can return a child before its skipped folder.
        if (listed.Entry is null || listed.Entry.Skip == nameof(TextKey.SkipReservedName))
        {
            var parentEnd = PathNames.LastSeparator(listed.Path);
            if (parentEnd > 0 && !PathNames.IsTransferable(listed.Path[..parentEnd]))
                return true;
        }
        return !options.IncludeOutOfScope && listed.Entry is { } entry &&
            _scope.Excludes(null, listed.Path, entry.IsFolder);
    }

    /// <summary>Ancestors in path_display can be stale; true path = parent's true path + own displayed name.</summary>
    private static Listed Correct(Listed listed, string trueParent)
    {
        var end = PathNames.LastSeparator(listed.Path);
        if (end != 0 && (end < 0 ? ReadOnlySpan<char>.Empty : listed.Path.AsSpan(..end)).SequenceEqual(trueParent.AsSpan())) return listed;
        var path = PathNames.Child(trueParent, PathNames.Name(listed.Path));
        return listed with
        {
            Path = path,
            Entry = listed.Entry is null ? null : listed.Entry with { Path = path }
        };
    }

    /// <summary>A Dropbox reply about requestedPath (true spelling) in true spelling: requestedPath's parent + the
    /// reply's own name. The one correction for single-item replies (get_metadata, upload, create_folder, move).</summary>
    public static Entry Respelled(Entry entry, string requestedPath) => Correct(entry, PathNames.Parent(requestedPath));

    private static Entry Correct(Entry entry, string trueParent) =>
        entry with { Path = PathNames.Child(trueParent, PathNames.Name(entry.Path)) };

    /// <summary>The requested root takes the caller's true spelling; its descendants are corrected to it.</summary>
    private static Listed Requested(Listed listed, string under) =>
        listed with
        {
            Path = under,
            Entry = listed.Entry is { } entry ? entry with { Path = under } : null
        };

    /// <summary>A failed listing read is incomplete inventory, never absence of the unseen items.</summary>
    private static InventoryIncompleteException Incomplete(string path, Exception error) =>
        new(Text.Get(TextKey.ReadDropboxFailed, (TextArg.Path, path), (TextArg.Message, error.Message)), error);

    /// <summary>A folder's displayed path → its true path; live entries wait until their parent folder is known.</summary>
    private sealed class Spelling
    {
        private readonly Dictionary<string, string> _true = new(PathNames.Comparer);
        private readonly Dictionary<string, List<(long Seq, Listed Listed)>> _waiting = new(PathNames.Comparer);
        private readonly Dictionary<string, (int At, int Below, long Tomb, long File)> _held = new(PathNames.Comparer);
        private readonly Queue<(string Path, string True)> _cascade = new();
        private long _next;

        /// <summary>Seeds the pair root and every ancestor of the listing root, so the returned root entry
        /// and its descendants take the caller's true spelling.</summary>
        public Spelling(string under)
        {
            for (var path = under; ; path = PathNames.Parent(path))
            {
                _true[path] = path;
                if (path.Length == 0) break;
            }
        }

        /// <summary>Streams tombstones immediately; live entries wait for their parent's spelling and release
        /// in whole-parent-bucket FIFO order.</summary>
        public void Add(Listed listed, List<Listed> released)
        {
            var seq = ++_next;
            Mark(listed, seq);
            var parent = PathNames.Parent(listed.Path);
            if (listed.Entry is null)
            {
                released.Add(_true.TryGetValue(parent, out var known) ? Correct(listed, known) : listed);
                return;
            }
            if (!_true.TryGetValue(parent, out var parentTrue))
            {
                Wait(parent, (seq, listed));
                return;
            }
            Release(listed, parentTrue, false, released);
            while (_cascade.TryDequeue(out var opened))
            {
                if (!_waiting.Remove(opened.Path, out var waiting)) continue;
                foreach (var child in waiting)
                {
                    var gone = Gone(child);
                    Count(child.Listed.Path, SpellingReleased);
                    Release(child.Listed, opened.True, gone, released);
                }
            }
        }

        /// <summary>Unresolved live entries keep displayed spelling, shallowest first and then in arrival order.</summary>
        public List<Listed> Flush()
        {
            var released = new List<Listed>();
            foreach (var item in _waiting.Values.SelectMany(bucket => bucket)
                .OrderBy(item => PathNames.Depth(item.Listed.Path)).ThenBy(item => item.Seq))
            {
                if (!Gone(item)) released.Add(item.Listed);
                Count(item.Listed.Path, SpellingReleased);
            }
            _waiting.Clear();
            _held.Clear();
            _cascade.Clear();
            return released;
        }

        private void Mark(Listed listed, long seq)
        {
            if (!_held.TryGetValue(listed.Path, out var held)) return;
            if (listed.Entry is null) held.Tomb = seq;
            else if (!listed.Entry.IsFolder && held.Below > 0) held.File = seq;
            else return;
            _held[listed.Path] = held;
        }

        private bool Gone((long Seq, Listed Listed) item)
        {
            for (var path = item.Listed.Path; ; path = PathNames.Parent(path))
            {
                if (_held.TryGetValue(path, out var held) &&
                    (held.Tomb > item.Seq || path != item.Listed.Path && held.File > item.Seq)) return true;
                if (path.Length == 0) return false;
            }
        }

        private void Count(string own, int delta)
        {
            for (var path = own; ; path = PathNames.Parent(path))
            {
                _held.TryGetValue(path, out var held);
                if (path == own) held.At += delta;
                else held.Below += delta;
                if (held.At == 0 && held.Below == 0) _held.Remove(path);
                else _held[path] = held;
                if (path.Length == 0) break;
            }
        }

        private void Release(Listed listed, string parentTrue, bool gone, List<Listed> released)
        {
            var corrected = Correct(listed, parentTrue);
            if (!gone) released.Add(corrected);
            if (corrected.Entry is not { IsFolder: true }) return;
            // Gone folders still supply spelling; finish this bucket before opening any child bucket.
            _true[listed.Path] = corrected.Path;
            _cascade.Enqueue((listed.Path, corrected.Path));
        }

        private void Wait(string parent, (long Seq, Listed Listed) item)
        {
            if (!_waiting.TryGetValue(parent, out var waiting))
            {
                waiting = new();
                _waiting[parent] = waiting;
            }
            waiting.Add(item);
            Count(item.Listed.Path, SpellingQueued);
        }
    }

    private partial Task<DropboxPage> ReadPageAsync(string route, object arg, string path,
        RequestAttempt attempt, CancellationToken ct);

    private partial async Task<DropboxPage> ReadPageAsync(string route, object arg, string path,
        RequestAttempt attempt, CancellationToken ct)
    {
        try
        {
            return await _api.ReadPageAsync(route, arg, _pair, attempt, ct).ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            throw Incomplete(path, error);
        }
        catch (HttpRequestException error)
        {
            throw Incomplete(path, error);
        }
    }

    /// <summary>Null on path/not_found.</summary>
    public partial Task<Entry?> ReadOneAsync(string path, CancellationToken ct);

    public partial async Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
    {
        try
        {
            var listed = await _api.RpcAsync(DropboxProtocol.GetMetadata,
                new { path = DropboxPaths.Remote(_pair, path), include_deleted = false },
                DropboxReplyShapes.Listed(_pair), new RequestAttempt(new object(), AttemptOwner.Request), ct, subject: path)
                .ConfigureAwait(false);
            return listed.Entry is { } entry ? Respelled(entry, path) : null;
        }
        catch (HttpRequestException error)
        {
            // A deleted or missing path is absence on this route, and the structured union proves it even when the
            // error summary is absent or differs, so a both-deleted item reaches the existing planner (bug #95).
            if (error is DropboxApiException api && DropboxErrors.IsStructuredPathNotFound(api)) return null;
            var mapped = DropboxErrors.Map(DropboxProtocol.GetMetadata, error, false, path);
            if (mapped.IsAbsent) return null;
            throw mapped.Error;
        }
        catch (JsonException error)
        {
            // A malformed single-item reply is this item's failure with this endpoint's cause; a malformed
            // inventory page keeps the listing's InventoryIncompleteException instead.
            throw new ItemFailedException(path, TextKey.ReadDropboxFailed, error);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ProviderFailureException)
        {
            // A provider or transport read failure is this item's Failure through the existing notice owner, never
            // an exception that abandons the selection with no notice (bug #95).
            throw new ItemFailedException(path, TextKey.ReadDropboxFailed, error);
        }
    }

    /// <summary>Captures the change cursor for the configured root; options supply the provenance flags.</summary>
    public partial Task<string> CaptureCursorAsync(ListOptions options, RequestAttempt attempt,
        CancellationToken ct);

    public partial Task<string> CaptureCursorAsync(ListOptions options, RequestAttempt attempt,
        CancellationToken ct) =>
        _api.RpcAsync(DropboxProtocol.ListFolderGetLatestCursor, new
        {
            path = DropboxPaths.Remote(_pair, ""),
            recursive = options.Recursive,
            include_deleted = options.IncludeDeleted,
            include_mounted_folders = true,
            include_non_downloadable_files = true
        }, DropboxReplyShapes.Cursor(), attempt, ct);

    /// <summary>Reads one sparse change page; the caller disposes the sealed page after one ordered replay.</summary>
    public partial Task<DropboxPage> ReadChangePageAsync(string cursor, RequestAttempt attempt,
        CancellationToken ct);

    public partial Task<DropboxPage> ReadChangePageAsync(string cursor, RequestAttempt attempt,
        CancellationToken ct) =>
        _api.ReadPageAsync(DropboxProtocol.ListFolderContinue, new { cursor }, _pair, attempt, ct);

    /// <summary>Returns the current native location, including outside this pair, or proven identity absence.</summary>
    public partial Task<IdentityLocation?> ReadByIdAsync(string id, RequestAttempt attempt,
        CancellationToken ct);

    public partial async Task<IdentityLocation?> ReadByIdAsync(string id, RequestAttempt attempt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ItemFailedException(id, TextKey.ReadDropboxFailed, null);
        var domain = _pair.Roots?.Dropbox.RootNamespaceId;
        if (string.IsNullOrEmpty(domain))
            throw new EngineRequestException(TextKey.RootUnbound, (TextArg.Pair, _pair.PairId));
        Entry entry;
        try
        {
            // Decoded against the namespace root, so a location outside this pair stays intact.
            entry = await _api.RpcAsync(DropboxProtocol.GetMetadata,
                new { path = id, include_deleted = false },
                DropboxReplyShapes.Metadata(_pair with { DropboxRoot = string.Empty }), attempt, ct, subject: id).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            var mapped = DropboxErrors.Map(DropboxProtocol.GetMetadata, error, false, id);
            if (mapped.IsAbsent) return null;
            throw mapped.Error;
        }
        if (!string.Equals(entry.Id, id, StringComparison.Ordinal))
            throw new ItemFailedException(id, TextKey.ReadDropboxFailed, null);
        var native = PathNames.RootedDropboxPath(string.Empty, entry.Path);
        var relative = PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(_pair.DropboxRoot), native);
        return new IdentityLocation(domain, native, entry with { Path = relative ?? native });
    }
}
