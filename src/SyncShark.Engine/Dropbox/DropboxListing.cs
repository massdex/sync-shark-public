using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox listing and single-item read</summary>
public sealed class DropboxListing
{
    private const string ListFolder = "files/list_folder";
    private const string ListFolderContinue = "files/list_folder/continue";
    private const string ListFolderGetLatestCursor = "files/list_folder/get_latest_cursor";
    private const string GetMetadata = "files/get_metadata";
    private const string EntriesName = "entries";
    private const string CursorName = "cursor";
    private const string HasMoreName = "has_more";
    private const int ListPageMaximum = 2000;

    /// <summary>Read operation on the supplied facts.</summary>
    public async IAsyncEnumerable<Listed> ReadAsync(DropboxApi api, PairConfig pair,
        Func<PathScope> resolvedScope, string under, ListOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(resolvedScope);
        ArgumentNullException.ThrowIfNull(options);
        var scope = resolvedScope();
        var first = new
        {
            path = DropboxPaths.Remote(pair, under),
            recursive = options.Recursive,
            include_deleted = options.IncludeDeleted,
            include_mounted_folders = true,
            include_non_downloadable_files = true,
            limit = ListPageMaximum
        };
        await foreach (var listed in PageLoopAsync(api, pair, ListFolder, first, options.Cursor,
            options.CursorAcquired, options.Progress, under, ct))
        {
            if (PathNames.Same(listed.Path, under)) continue;
            if (IsSkipped(listed, options, scope)) continue;
            yield return listed;
        }
    }

    /// <summary>Lists the contents of the folder identified by folderId, anchored to that id rather than a reusable
    /// path, so a recovery still reads the selected folder's own contents after its path spelling or occupant changed.</summary>
    public async IAsyncEnumerable<Listed> ReadFolderAsync(DropboxApi api, PairConfig pair, string folderId,
        ListOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(folderId);
        // W13 passes the provider's own identity, already in the id:… form; send it unchanged.
        var first = new
        {
            path = folderId,
            recursive = options.Recursive,
            include_deleted = options.IncludeDeleted,
            include_mounted_folders = true,
            include_non_downloadable_files = true,
            limit = ListPageMaximum
        };
        await foreach (var listed in PageLoopAsync(api, pair, ListFolder, first, options.Cursor,
            options.CursorAcquired, options.Progress, folderId, ct))
            yield return listed;
    }

    /// <summary>Reads the entry at path exactly, preserving its true spelling under the requested path; a reply that
    /// names a different in-pair key is a failed read, never a renamed success.</summary>
    public async Task<Entry?> ReadOneAsync(DropboxApi api, PairConfig pair, string path, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = await CallAsync(api, GetMetadata,
                new { path = DropboxPaths.Remote(pair, path), include_deleted = false }, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new ItemFailedException(path, TextKey.ReadDropboxFailed, error);
        }
        using (document)
        {
            Listed listed;
            try { listed = DropboxEntries.Parse(document.RootElement, pair); }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                throw new ItemFailedException(path, TextKey.ReadDropboxFailed, error);
            }
            return ExactOutcome(listed, path);
        }
    }

    /// <summary>Returns the current native location of id, including outside this pair.</summary>
    public async Task<IdentityLocation?> ReadByIdAsync(DropboxApi api, PairConfig pair, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ItemFailedException(id, TextKey.ReadDropboxFailed, null);
        if (pair.Roots?.Dropbox.RootNamespaceId is not { Length: > 0 })
            throw new EngineRequestException(TextKey.RootUnbound, (TextArg.Pair, pair.PairId));
        JsonDocument document;
        try
        {
            document = await CallAsync(api, GetMetadata, new { path = id, include_deleted = false }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new ItemFailedException(id, TextKey.ReadDropboxFailed, error);
        }
        using (document)
        {
            Entry entry;
            try
            {
                // Decoded against the namespace root, so a location outside this pair stays intact.
                entry = DropboxReply.Metadata(document.RootElement, pair with { DropboxRoot = string.Empty });
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                throw new ItemFailedException(id, TextKey.ReadDropboxFailed, error);
            }
            return VerifiedById(entry, id, pair);
        }
    }

    /// <summary>Captures the current change cursor for the configured root; a blank cursor is not a usable boundary.</summary>
    public async Task<string> CaptureCursorAsync(DropboxApi api, PairConfig pair, ListOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(options);
        using var document = await CallAsync(api, ListFolderGetLatestCursor, new
        {
            path = DropboxPaths.Remote(pair, ""),
            recursive = options.Recursive,
            include_deleted = options.IncludeDeleted,
            include_mounted_folders = true,
            include_non_downloadable_files = true
        }, ct).ConfigureAwait(false);
        var cursor = await DecodeAsync(document.RootElement, DropboxReplyShapes.Cursor(), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cursor))
            throw new InventoryIncompleteException(Text.Get(TextKey.ReadDropboxFailed,
                (TextArg.Path, pair.DropboxRoot), (TextArg.Message, "")), null);
        return cursor;
    }

    /// <summary>Reads one sparse change page at the supplied cursor; CaughtUp is a proven source end (has_more=false
    /// with a usable cursor).</summary>
    public async Task<ChangePage> ReadChangePageAsync(DropboxApi api, PairConfig pair, string cursor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        JsonDocument document;
        try
        {
            document = await CallAsync(api, ListFolderContinue, new { cursor }, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Without the authenticated refusal surface a reset is not distinguishable here; stay on the ordinary
            // incomplete/retry path rather than inferring a source end.  (See REPORT blocker for the seam.)
            throw Incomplete(pair.DropboxRoot, error);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(EntriesName, out var entries) || entries.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty(CursorName, out var cursorElement) || cursorElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty(HasMoreName, out var moreElement) || moreElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Incomplete(pair.DropboxRoot, new JsonException());
            var decoder = new DropboxEntries();
            var changes = new List<Change>(entries.GetArrayLength());
            foreach (var element in entries.EnumerateArray())
            {
                Listed listed;
                try { listed = decoder.Decode(element, pair); }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    throw Incomplete(pair.DropboxRoot, error);
                }
                var entry = listed.Entry;
                changes.Add(entry is null
                    ? new Change(Side.Dropbox, ChangeKind.Deleted, listed.Path, null, null, null, false, false)
                    : new Change(Side.Dropbox, ChangeKind.Changed, listed.Path, null, entry.Id, entry, false, false));
            }
            ct.ThrowIfCancellationRequested();
            var next = cursorElement.GetString();
            return new ChangePage(changes, next, null)
            {
                CaughtUp = !moreElement.GetBoolean() && !string.IsNullOrWhiteSpace(next)
            };
        }
    }

    /// <summary>A Dropbox reply about requestedPath (true spelling) in true spelling: requestedPath's parent + the
    /// reply's own name. The one correction for single-item replies.</summary>
    public static Entry Respelled(Entry entry, string requestedPath) =>
        entry with { Path = PathNames.Child(PathNames.Parent(requestedPath), PathNames.Name(entry.Path)) };

    /// <summary>The entry for an exact-name reply, refused unless the decoded key equals the requested key and a live
    /// entry came back; the provider's actual leaf spelling is retained.</summary>
    public static Entry ExactOutcome(Listed listed, string requestedPath)
    {
        ArgumentNullException.ThrowIfNull(listed);
        if (!PathNames.Same(listed.Path, requestedPath) || listed.Entry is not { } entry)
            throw new ItemFailedException(requestedPath, TextKey.ReadDropboxFailed, null);
        return Respelled(entry, requestedPath);
    }

    /// <summary>The entry as a proven native location for id; a mismatched identity is a failed read. The location is
    /// preserved intact even outside the current pair.</summary>
    public static IdentityLocation VerifiedById(Entry entry, string id, PairConfig pair)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.Equals(entry.Id, id, StringComparison.Ordinal))
            throw new ItemFailedException(id, TextKey.ReadDropboxFailed, null);
        if (pair.Roots is not { Dropbox.RootNamespaceId: { Length: > 0 } domain })
            throw new EngineRequestException(TextKey.RootUnbound, (TextArg.Pair, pair.PairId));
        var native = PathNames.RootedDropboxPath(string.Empty, entry.Path);
        var relative = PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(pair.DropboxRoot), native);
        return new IdentityLocation(domain, native, entry with { Path = relative ?? native });
    }

    // One shared, paginated page loop for path and id listings: the authenticated RPC, the advancing-continuation
    // repeat guard, and a usable terminal cursor.  Pages are decoded from the returned reply (one page at a time).
    private static async IAsyncEnumerable<Listed> PageLoopAsync(DropboxApi api, PairConfig pair, string firstRoute,
        object firstArg, string? startCursor, Action<string>? acquired, Action<string, long>? progress,
        string subject, [EnumeratorCancellation] CancellationToken ct)
    {
        var route = firstRoute;
        object arg = firstArg;
        if (!string.IsNullOrEmpty(startCursor))
        {
            route = ListFolderContinue;
            arg = new { cursor = startCursor };
        }
        var cycle = new ContinuationCycle();
        var decoder = new DropboxEntries();
        var previous = startCursor ?? "";
        long pages = 0;
        var currentPath = subject;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            JsonDocument document;
            try { document = await CallAsync(api, route, arg, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw Incomplete(currentPath, error);
            }
            using var page = document;
            var root = page.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(EntriesName, out var entries) || entries.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty(CursorName, out var cursorElement) || cursorElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty(HasMoreName, out var moreElement) || moreElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Incomplete(currentPath, new JsonException());
            var cursor = cursorElement.GetString();
            var hasMore = moreElement.GetBoolean();
            foreach (var element in entries.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                Listed listed;
                try { listed = decoder.Decode(element, pair); }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    throw Incomplete(currentPath, error);
                }
                currentPath = listed.Path;
                yield return listed;
            }
            pages++;
            progress?.Invoke(currentPath, pages);
            if (!hasMore)
            {
                if (!string.IsNullOrWhiteSpace(cursor)) acquired?.Invoke(cursor!);
                yield break;
            }
            if (string.IsNullOrWhiteSpace(cursor) || cycle.Repeats(previous, cursor!))
                throw Incomplete(currentPath, new JsonException());
            previous = cursor!;
            route = ListFolderContinue;
            arg = new { cursor };
        }
    }

    private static bool IsSkipped(Listed listed, ListOptions options, PathScope scope)
    {
        // Check the parent spelling, not page order: Dropbox can return a child before its skipped folder.
        if (listed.Entry is null || listed.Entry.Skip == nameof(TextKey.SkipReservedName))
        {
            var parentEnd = PathNames.LastSeparator(listed.Path);
            if (parentEnd > 0 && !PathNames.IsTransferable(listed.Path[..parentEnd])) return true;
        }
        return !options.IncludeOutOfScope && listed.Entry is { } entry &&
            scope.Excludes(null, listed.Path, entry.IsFolder);
    }

    // The one authenticated read boundary: W09's RPC seam adds the bearer token and the path-root namespace and
    // returns the parsed reply; no local header builder.
    internal static Task<JsonDocument> CallAsync(DropboxApi api, string route, object arg, CancellationToken ct) =>
        api.CallJsonAsync(route, JsonSerializer.Serialize(arg), ct);

    // Decode an already-received reply through its owner decoder (small replies only; listing pages stay on the RPC path).
    internal static async Task<T> DecodeAsync<T>(JsonElement root, DropboxReply<T> decoder, CancellationToken ct)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(root.GetRawText()));
        return await DropboxReply.ReadAsync(stream, decoder, ct).ConfigureAwait(false);
    }

    /// <summary>A failed listing read is incomplete inventory, never absence of the unseen items.</summary>
    private static InventoryIncompleteException Incomplete(string path, Exception error) =>
        new(Text.Get(TextKey.ReadDropboxFailed, (TextArg.Path, path), (TextArg.Message, error.Message)), error);
}
