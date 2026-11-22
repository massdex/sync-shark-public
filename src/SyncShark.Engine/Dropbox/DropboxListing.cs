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

    /// <summary>Reads the entry at path exactly; a reply naming a different in-pair key is a failed read, and a
    /// classified exact absence returns null.</summary>
    public async Task<Entry?> ReadOneAsync(DropboxApi api, PairConfig pair, string path, CancellationToken ct)
    {
        JsonDocument body;
        HttpStatusCode status;
        try
        {
            (status, body) = await ReadReplyAsync(api, GetMetadata,
                new { path = DropboxPaths.Remote(pair, path), include_deleted = false }, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException)
        {
            throw new ItemFailedException(path, TextKey.ReadDropboxFailed, error);
        }
        using (body)
        {
            if (status != HttpStatusCode.OK)
            {
                var classified = Classify(status, body.RootElement, GetMetadata, expectedIsNull: false);
                if (IsAbsence(classified)) return null;
                throw new ItemFailedException(path, classified.Cause, null);
            }
            Listed listed;
            try { listed = DropboxEntries.Parse(body.RootElement, pair); }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                throw new ItemFailedException(path, TextKey.ReadDropboxFailed, error);
            }
            return ExactOutcome(listed, path);
        }
    }

    /// <summary>Returns the current native location of id, including outside this pair; a classified exact absence
    /// returns null.</summary>
    public async Task<IdentityLocation?> ReadByIdAsync(DropboxApi api, PairConfig pair, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ItemFailedException(id, TextKey.ReadDropboxFailed, null);
        if (pair.Roots?.Dropbox.RootNamespaceId is not { Length: > 0 })
            throw new EngineRequestException(TextKey.RootUnbound, (TextArg.Pair, pair.PairId));
        JsonDocument body;
        HttpStatusCode status;
        try
        {
            (status, body) = await ReadReplyAsync(api, GetMetadata, new { path = id, include_deleted = false }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException)
        {
            throw new ItemFailedException(id, TextKey.ReadDropboxFailed, error);
        }
        using (body)
        {
            if (status != HttpStatusCode.OK)
            {
                var classified = Classify(status, body.RootElement, GetMetadata, expectedIsNull: false);
                if (IsAbsence(classified)) return null;
                throw new ItemFailedException(id, classified.Cause, null);
            }
            Entry entry;
            try
            {
                // Decoded against the namespace root, so a location outside this pair stays intact.
                entry = DropboxReply.Metadata(body.RootElement, pair with { DropboxRoot = string.Empty });
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
        JsonDocument body;
        HttpStatusCode status;
        try
        {
            (status, body) = await ReadReplyAsync(api, ListFolderGetLatestCursor, new
            {
                path = DropboxPaths.Remote(pair, ""),
                recursive = options.Recursive,
                include_deleted = options.IncludeDeleted,
                include_mounted_folders = true,
                include_non_downloadable_files = true
            }, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException)
        {
            throw Incomplete(pair.DropboxRoot, error);
        }
        using (body)
        {
            if (status != HttpStatusCode.OK)
                throw Incomplete(pair.DropboxRoot, Refusal(Classify(status, body.RootElement,
                    ListFolderGetLatestCursor, expectedIsNull: false)));
            var cursor = await DecodeAsync(body.RootElement, DropboxReplyShapes.Cursor(), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(cursor)) throw Incomplete(pair.DropboxRoot, new JsonException());
            return cursor;
        }
    }

    /// <summary>Reads one sparse change page through the sealed page decoder (no whole page in memory); a continuation
    /// reset propagates, and CaughtUp requires has_more=false with a usable cursor.</summary>
    public async Task<ChangePage> ReadChangePageAsync(DropboxApi api, PairConfig pair, string cursor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        DropboxPage page;
        try
        {
            page = await api.ReadPageAsync(ListFolderContinue, JsonSerializer.Serialize(new { cursor }), pair, ct)
                .ConfigureAwait(false);
        }
        catch (ListingCursorResetException) { throw; }
        catch (Exception error) when (error is not (OperationCanceledException or NotImplementedException))
        {
            // A non-reset refusal stays on the ordinary incomplete/retry path, never a fabricated source end.
            throw Incomplete(pair.DropboxRoot, error);
        }
        await using (page)
        {
            var changes = new List<Change>();
            foreach (var listed in page.ReadEntries())
            {
                ct.ThrowIfCancellationRequested();
                var entry = listed.Entry;
                changes.Add(entry is null
                    ? new Change(Side.Dropbox, ChangeKind.Deleted, listed.Path, null, null, null, false, false)
                    : new Change(Side.Dropbox, ChangeKind.Changed, listed.Path, null, entry.Id, entry, false, false));
            }
            var next = page.Cursor;
            return new ChangePage(changes, next, null)
            {
                CaughtUp = !page.HasMore && !string.IsNullOrWhiteSpace(next)
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

    /// <summary>True when the classifier marks the exact target as absent (a successful history/identity negative),
    /// never a generic refusal.</summary>
    public static bool IsAbsence(DropboxErrors.Classified classified) => false; // red-evidence mutation: ignores Absent

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

    // One shared, paginated page loop for path and id listings: W09's sealed ReadPageAsync streams each page to a
    // spool (R12/R26/R28), replayed once here; a reset propagates and advancing continuations use the repeat guard.
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
        var previous = startCursor ?? "";
        long pages = 0;
        var currentPath = subject;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            DropboxPage page;
            try { page = await api.ReadPageAsync(route, JsonSerializer.Serialize(arg), pair, ct).ConfigureAwait(false); }
            catch (ListingCursorResetException) { throw; }
            catch (Exception error) when (error is not (OperationCanceledException or NotImplementedException))
            {
                throw Incomplete(currentPath, error);
            }
            await using (page)
            {
                foreach (var listed in page.ReadEntries())
                {
                    ct.ThrowIfCancellationRequested();
                    currentPath = listed.Path;
                    yield return listed;
                }
                pages++;
                progress?.Invoke(currentPath, pages);
                var cursor = page.Cursor;
                if (!page.HasMore)
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

    // The one authenticated non-page read boundary (R35): W09's SendAsync authenticates and returns the status and the
    // unchanged body stream (even a 409); this parses the reply so W10 classifies it. No limiter permit here.
    internal static async Task<(HttpStatusCode Status, JsonDocument Body)> ReadReplyAsync(DropboxApi api, string route,
        object arg, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(route))
        {
            Content = new StringContent(JsonSerializer.Serialize(arg), Encoding.UTF8, "application/json")
        };
        using var response = await api.SendAsync(request, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return (response.StatusCode, DropboxProtocol.Parse(stream));
    }

    /// <summary>Classifies one provider refusal through W10's owner (never a local prefix test).</summary>
    internal static DropboxErrors.Classified Classify(HttpStatusCode status, JsonElement body, string route,
        bool expectedIsNull) =>
        new DropboxErrors().Classify(status, body, route, expectedIsNull,
            new DropboxErrors.RequestEvidence(Read: true, Dispatched: false, ProvenNoEffect: true,
                Transient: (int)status >= 500));

    // Decode an already-received reply through its owner decoder (small replies only; listing pages stream via ReadPageAsync).
    internal static async Task<T> DecodeAsync<T>(JsonElement root, DropboxReply<T> decoder, CancellationToken ct)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(root.GetRawText()));
        return await DropboxReply.ReadAsync(stream, decoder, ct).ConfigureAwait(false);
    }

    private static InventoryIncompleteException Refusal(DropboxErrors.Classified classified) =>
        new(Text.Get(classified.Cause), null);

    /// <summary>A failed listing read is incomplete inventory, never absence of the unseen items.</summary>
    private static InventoryIncompleteException Incomplete(string path, Exception error) =>
        new(Text.Get(TextKey.ReadDropboxFailed, (TextArg.Path, path), (TextArg.Message, error.Message)), error);
}
