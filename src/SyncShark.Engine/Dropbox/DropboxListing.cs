using System.Net;
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
    private const string ErrorSummaryName = "error_summary";
    private const string TagName = ".tag";
    private const string IdPathPrefix = "id:";
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
        // Ancestor spelling is resolved later from the stored scan rows; this reader streams provider order only.
        var route = ListFolder;
        object arg = new
        {
            path = DropboxPaths.Remote(pair, under),
            recursive = options.Recursive,
            include_deleted = options.IncludeDeleted,
            include_mounted_folders = true,
            include_non_downloadable_files = true,
            limit = ListPageMaximum
        };
        if (options.Cursor is { } start)
        {
            route = ListFolderContinue;
            arg = new { cursor = start };
        }
        long pages = 0;
        var currentPath = under;
        var decoder = new DropboxEntries();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            JsonDocument document;
            try { document = await ReadReplyAsync(api, route, arg, ct).ConfigureAwait(false); }
            catch (RefusedException refused) when (refused.IsReset) { throw new ListingCursorResetException(refused); }
            catch (Exception error) when (error is RefusedException or JsonException) { throw Incomplete(under, error); }
            using var page = document;
            var root = page.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(EntriesName, out var entries) || entries.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty(CursorName, out var cursorElement) || cursorElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty(HasMoreName, out var moreElement) || moreElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Incomplete(under, new JsonException());
            var cursor = cursorElement.GetString()!;
            var hasMore = moreElement.GetBoolean();
            foreach (var element in entries.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                Listed listed;
                try { listed = decoder.Decode(element, pair); }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    throw Incomplete(under, error);
                }
                currentPath = listed.Path;
                if (IsSkipped(listed, under, options, scope)) continue;
                yield return listed;
            }
            pages++;
            options.Progress?.Invoke(currentPath, pages);
            if (!hasMore)
            {
                options.CursorAcquired?.Invoke(cursor);
                yield break;
            }
            route = ListFolderContinue;
            arg = new { cursor };
        }
    }

    /// <summary>Reads the entry at path exactly, preserving its true spelling under the requested path; null when the
    /// path or its parent is absent.</summary>
    public async Task<Entry?> ReadOneAsync(DropboxApi api, PairConfig pair, string path, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = await ReadReplyAsync(api, GetMetadata,
                new { path = DropboxPaths.Remote(pair, path), include_deleted = false }, ct).ConfigureAwait(false);
        }
        catch (RefusedException refused) when (refused.IsNotFound) { return null; }
        catch (RefusedException refused) { throw new ItemFailedException(path, TextKey.ReadDropboxFailed, refused); }
        catch (JsonException error) { throw new ItemFailedException(path, TextKey.ReadDropboxFailed, error); }
        using (document)
        {
            try
            {
                var listed = new DropboxEntries().Decode(document.RootElement, pair);
                return listed.Entry is { } entry ? Respelled(entry, path) : null;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                throw new ItemFailedException(path, TextKey.ReadDropboxFailed, error);
            }
        }
    }

    /// <summary>Returns the current native location of id, including outside this pair, or null for proven absence.</summary>
    public async Task<IdentityLocation?> ReadByIdAsync(DropboxApi api, PairConfig pair, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ItemFailedException(id, TextKey.ReadDropboxFailed, null);
        if (pair.Roots?.Dropbox.RootNamespaceId is not { Length: > 0 })
            throw new EngineRequestException(TextKey.RootUnbound, (TextArg.Pair, pair.PairId));
        JsonDocument document;
        try
        {
            document = await ReadReplyAsync(api, GetMetadata, new { path = id, include_deleted = false }, ct)
                .ConfigureAwait(false);
        }
        catch (RefusedException refused) when (refused.IsNotFound) { return null; }
        catch (RefusedException refused) { throw new ItemFailedException(id, TextKey.ReadDropboxFailed, refused); }
        catch (JsonException error) { throw new ItemFailedException(id, TextKey.ReadDropboxFailed, error); }
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

    /// <summary>Captures the current change cursor for the configured root.</summary>
    public async Task<string> CaptureCursorAsync(DropboxApi api, PairConfig pair, ListOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(options);
        using var document = await ReadReplyAsync(api, ListFolderGetLatestCursor, new
        {
            path = DropboxPaths.Remote(pair, ""),
            recursive = options.Recursive,
            include_deleted = options.IncludeDeleted,
            include_mounted_folders = true,
            include_non_downloadable_files = true
        }, ct).ConfigureAwait(false);
        return CursorOf(document.RootElement, pair);
    }

    /// <summary>Reads one sparse change page at the supplied cursor; a refused cursor is a continuity loss.</summary>
    public async Task<ChangePage> ReadChangePageAsync(DropboxApi api, PairConfig pair, string cursor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        JsonDocument document;
        try
        {
            document = await ReadReplyAsync(api, ListFolderContinue, new { cursor }, ct).ConfigureAwait(false);
        }
        catch (RefusedException refused) when (refused.IsReset)
        {
            throw new ListingCursorResetException(refused);
        }
        catch (Exception error) when (error is RefusedException or JsonException)
        {
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
            var listed = new List<Listed>();
            foreach (var element in entries.EnumerateArray())
            {
                try { listed.Add(decoder.Decode(element, pair)); }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    throw Incomplete(pair.DropboxRoot, error);
                }
            }
            ct.ThrowIfCancellationRequested();
            // CaughtUp only when this page proved the source end with has_more=false.
            return new ChangePage(DropboxFeed.ToChanges(listed), cursorElement.GetString(), null)
            {
                CaughtUp = !moreElement.GetBoolean()
            };
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
        var route = ListFolder;
        object arg = new
        {
            path = IdPathPrefix + folderId,
            recursive = options.Recursive,
            include_deleted = options.IncludeDeleted,
            include_mounted_folders = true,
            include_non_downloadable_files = true,
            limit = ListPageMaximum
        };
        var decoder = new DropboxEntries();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            JsonDocument document;
            try { document = await ReadReplyAsync(api, route, arg, ct).ConfigureAwait(false); }
            catch (RefusedException refused) when (refused.IsReset) { throw new ListingCursorResetException(refused); }
            catch (Exception error) when (error is RefusedException or JsonException) { throw Incomplete(folderId, error); }
            using var page = document;
            var root = page.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(EntriesName, out var entries) || entries.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty(CursorName, out var cursorElement) || cursorElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty(HasMoreName, out var moreElement) || moreElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Incomplete(folderId, new JsonException());
            var cursor = cursorElement.GetString()!;
            var hasMore = moreElement.GetBoolean();
            foreach (var element in entries.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                Listed listed;
                try { listed = decoder.Decode(element, pair); }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    throw Incomplete(folderId, error);
                }
                yield return listed;
            }
            if (!hasMore) yield break;
            route = ListFolderContinue;
            arg = new { cursor };
        }
    }

    /// <summary>A Dropbox reply about requestedPath (true spelling) in true spelling: requestedPath's parent + the
    /// reply's own name. The one correction for single-item replies (get_metadata, upload, create_folder, move).</summary>
    public static Entry Respelled(Entry entry, string requestedPath) =>
        Correct(entry, PathNames.Parent(requestedPath));

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

    // The shared Dropbox read boundary: build the RPC request from the route catalog, send it, and parse the reply.
    // A refused reply is a typed exception the caller turns into its own outcome (null, retry, reset or failure).
    internal static async Task<JsonDocument> ReadReplyAsync(DropboxApi api, string route, object arg,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(route))
        {
            Content = new StringContent(JsonSerializer.Serialize(arg), Encoding.UTF8, "application/json")
        };
        using var response = await api.SendAsync(request, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var document = DropboxProtocol.Parse(stream);
        if (response.IsSuccessStatusCode) return document;
        var refused = new RefusedException(response.StatusCode, Summary(document.RootElement));
        document.Dispose();
        throw refused;
    }

    private static Entry Correct(Entry entry, string trueParent) =>
        entry with { Path = PathNames.Child(trueParent, PathNames.Name(entry.Path)) };

    private static bool IsSkipped(Listed listed, string under, ListOptions options, PathScope scope)
    {
        if (PathNames.Same(listed.Path, under)) return true;
        // Check the parent spelling, not page order: Dropbox can return a child before its skipped folder.
        if (listed.Entry is null || listed.Entry.Skip == nameof(TextKey.SkipReservedName))
        {
            var parentEnd = PathNames.LastSeparator(listed.Path);
            if (parentEnd > 0 && !PathNames.IsTransferable(listed.Path[..parentEnd])) return true;
        }
        return !options.IncludeOutOfScope && listed.Entry is { } entry &&
            scope.Excludes(null, listed.Path, entry.IsFolder);
    }

    private static string CursorOf(JsonElement root, PairConfig pair)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(CursorName, out var cursor) && cursor.ValueKind == JsonValueKind.String)
            return cursor.GetString()!;
        throw new InventoryIncompleteException(Text.Get(TextKey.ReadDropboxFailed,
            (TextArg.Path, pair.DropboxRoot), (TextArg.Message, "")), null);
    }

    private static string Summary(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return "";
        if (error.TryGetProperty(ErrorSummaryName, out var summary) && summary.ValueKind == JsonValueKind.String)
            return summary.GetString() ?? "";
        return Tag(error);
    }

    private static string Tag(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(TagName, out var tag) || tag.ValueKind != JsonValueKind.String) return "";
        return tag.GetString() ?? "";
    }

    /// <summary>A failed listing read is incomplete inventory, never absence of the unseen items.</summary>
    private static InventoryIncompleteException Incomplete(string path, Exception error) =>
        new(Text.Get(TextKey.ReadDropboxFailed, (TextArg.Path, path), (TextArg.Message, error.Message)), error);

    /// <summary>A provider refusal carrying the status and the error summary the caller classifies.</summary>
    internal sealed class RefusedException(HttpStatusCode status, string summary) : Exception(summary)
    {
        public HttpStatusCode Status { get; } = status;

        public string Summary { get; } = summary;

        /// <summary>The provider invalidated the cursor; only fresh acquisition can replace it.</summary>
        public bool IsReset => Status == HttpStatusCode.Conflict && Summary.StartsWith("reset", StringComparison.Ordinal);

        /// <summary>The exact path is gone; a successful history negative on the path-mode routes.</summary>
        public bool IsNotFound => Status == HttpStatusCode.Conflict &&
            (Summary.StartsWith("path/not_found", StringComparison.Ordinal) ||
             Summary.StartsWith("path_lookup/not_found", StringComparison.Ordinal) ||
             Summary.StartsWith("from_lookup/not_found", StringComparison.Ordinal));
    }
}
