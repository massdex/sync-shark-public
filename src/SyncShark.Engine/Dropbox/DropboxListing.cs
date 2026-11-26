using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox listing and single-item read</summary>
public sealed class DropboxListing
{
    private ContinuationCycle? changeCycle;
    private string? expectedCursor;

    /// <summary>Streams entries below the requested path after applying the listing scope and skip rules.</summary>
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
            limit = DropboxProtocol.ListPageMaximum
        };
        await foreach (var listed in PageLoopAsync(api, pair, DropboxProtocol.ListFolder, first, options.Cursor,
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
        var first = new
        {
            path = folderId,
            recursive = options.Recursive,
            include_deleted = options.IncludeDeleted,
            include_mounted_folders = true,
            include_non_downloadable_files = true,
            limit = DropboxProtocol.ListPageMaximum
        };
        await foreach (var listed in PageLoopAsync(api, pair, DropboxProtocol.ListFolder, first, options.Cursor,
            options.CursorAcquired, options.Progress, folderId, ct))
            yield return listed;
    }

    /// <summary>Reads the entry at path exactly; a reply naming a different in-pair key is a failed read, and a
    /// classified exact absence returns null.</summary>
    public async Task<Entry?> ReadOneAsync(DropboxApi api, PairConfig pair, string path, CancellationToken ct)
    {
        var (absent, listed) = await ReadReplyAsync(api, DropboxProtocol.GetMetadata, path,
            new { path = DropboxPaths.Remote(pair, path), include_deleted = false }, DropboxReplyShapes.Listed(pair), ct);
        return absent ? null : ExactOutcome(listed, path);
    }

    /// <summary>Returns the current native location of id, including outside this pair; a classified exact absence
    /// returns null.</summary>
    public async Task<IdentityLocation?> ReadByIdAsync(DropboxApi api, PairConfig pair, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ItemFailedException(id, TextKey.ReadDropboxFailed, null);
        if (pair.Roots?.Dropbox.RootNamespaceId is not { Length: > 0 })
            throw new EngineRequestException(TextKey.RootUnbound, (TextArg.Pair, pair.PairId));
        var (absent, entry) = await ReadReplyAsync(api, DropboxProtocol.GetMetadata, id, new { path = id, include_deleted = false },
            DropboxReplyShapes.Metadata(pair with { DropboxRoot = string.Empty }), ct);
        return absent ? null : VerifiedById(entry, id, pair);
    }

    /// <summary>Captures the current change cursor for the configured root; a blank cursor is not a usable boundary.</summary>
    public async Task<string> CaptureCursorAsync(DropboxApi api, PairConfig pair, ListOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(options);
        var (_, cursor) = await ReadReplyAsync(api, DropboxProtocol.ListFolderGetLatestCursor, pair.DropboxRoot, new
        {
            path = DropboxPaths.Remote(pair, ""), recursive = options.Recursive, include_deleted = options.IncludeDeleted,
            include_mounted_folders = true, include_non_downloadable_files = true
        }, DropboxReplyShapes.Cursor(), ct);
        if (string.IsNullOrWhiteSpace(cursor)) throw UnreadPage(pair.DropboxRoot);
        return cursor;
    }

    /// <summary>Reads one sparse change page through the sealed page decoder (no whole page in memory); a continuation
    /// reset propagates, and CaughtUp requires has_more=false with a usable cursor.</summary>
    public async Task<ChangePage> ReadChangePageAsync(DropboxApi api, PairConfig pair, string cursor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        if (string.IsNullOrWhiteSpace(cursor)) throw UnreadPage(pair.DropboxRoot);
        DropboxPage page;
        try { page = await api.ReadPageAsync(DropboxProtocol.ListFolderContinue, JsonSerializer.Serialize(new { cursor }), pair, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested && IsUnreadPage(error)) { throw UnreadPage(pair.DropboxRoot, error); }
        await using var owned = page;
        var next = page.Cursor;
        if (string.IsNullOrWhiteSpace(next)) throw UnreadPage(pair.DropboxRoot);
        var changes = DropboxFeed.ToChanges(page.ReadEntries());
        var cycle = expectedCursor == cursor && changeCycle is not null ? changeCycle : new ContinuationCycle();
        if (cycle.Repeats(cursor, next, page.HasMore || changes.Count != 0)) throw UnreadPage(pair.DropboxRoot);
        changeCycle = page.HasMore ? cycle : null;
        expectedCursor = next;
        return new ChangePage(changes, next, null) { CaughtUp = !page.HasMore };
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

    // One loop owns the cursor, the same-page retry and publication, so a retry can never advance or publish a page early.
    private static async IAsyncEnumerable<Listed> PageLoopAsync(DropboxApi api, PairConfig pair, string firstRoute,
        object firstArg, string? previous, Action<string>? acquired, Action<string, long>? progress,
        string subject, [EnumeratorCancellation] CancellationToken ct)
    {
        var (route, arg) = FirstRequest(firstRoute, firstArg, previous, subject);
        var cycle = new ContinuationCycle();
        long pages = 0;
        var failures = 0;
        var currentPath = subject;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            DropboxPage page;
            try { page = await api.ReadPageAsync(route, JsonSerializer.Serialize(arg), pair, ct).ConfigureAwait(false); }
            // A transient failure retries the same page with the same cursor, keeping the pages already read.
            catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or IOException or TimeoutException
                or ItemFailedException { Disposition: FailureDisposition.TemporaryNoEffect })
            {
                await api.WaitAsync(error is ItemFailedException provider && RemainingProviderWait(api, provider) is { } remaining ? remaining : api.RetryDelay(failures++), ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception error) when (IsIncompleteRead(error)) { throw Incomplete(currentPath, error); }
            failures = 0;
            await using var owned = page;
            var cursor = CheckedCursor(page, cycle, previous, currentPath);
            foreach (var listed in page.ReadEntries())
            {
                ct.ThrowIfCancellationRequested();
                if (previous == cursor) throw Incomplete(currentPath, new JsonException());
                currentPath = listed.Path;
                yield return listed;
            }
            pages++;
            progress?.Invoke(currentPath, pages);
            if (!page.HasMore)
            {
                acquired?.Invoke(cursor);
                yield break;
            }
            previous = cursor;
            route = DropboxProtocol.ListFolderContinue;
            arg = new { cursor };
        }
    }

    private static (string Route, object Arg) FirstRequest(string firstRoute, object firstArg, string? cursor, string subject)
    {
        if (cursor is null) return (firstRoute, firstArg);
        if (string.IsNullOrWhiteSpace(cursor)) throw Incomplete(subject, new JsonException());
        return (DropboxProtocol.ListFolderContinue, new { cursor });
    }

    private static TimeSpan? RemainingProviderWait(DropboxApi api, ItemFailedException error)
    {
        if (error.Disposition != FailureDisposition.TemporaryNoEffect || error.ProviderWait is not { } delay || delay <= TimeSpan.Zero)
            return null;
        return error.ProviderWaitStarted is { } start ? delay - api.Clock.GetElapsedTime(start) : delay;
    }

    private static bool IsIncompleteRead(Exception error) => error is not (OperationCanceledException or NotImplementedException
        or ListingCursorResetException or ItemFailedException { Disposition: FailureDisposition.TemporaryNoEffect });

    private static string CheckedCursor(DropboxPage page, ContinuationCycle cycle, string? previous, string currentPath)
    {
        var cursor = page.Cursor;
        if (string.IsNullOrWhiteSpace(cursor) || cycle.Repeats(previous, cursor, page.HasMore))
            throw Incomplete(currentPath, new JsonException());
        return cursor;
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

    internal static async Task<(bool Absent, T Value)> ReadReplyAsync<T>(DropboxApi api, string route, string path,
        object arg, DropboxReply<T> decoder, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(route))
        {
            Content = new StringContent(JsonSerializer.Serialize(arg), Encoding.UTF8, "application/json")
        };
        try
        {
            using var response = await api.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                await DropboxHttp.ThrowFailureAsync(api, response, route, ct, path).ConfigureAwait(false);
                return (true, default!);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return (false, await DropboxReply.ReadAsync(stream, decoder, ct).ConfigureAwait(false));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InventoryIncompleteException)
        {
            throw new ItemFailedException(path, TextKey.ReadDropboxFailed, error);
        }
        catch (Exception error) when (error is HttpRequestException or IOException || error is TimeoutException && !ct.IsCancellationRequested)
        {
            throw new ItemFailedException(path, TextKey.ReadDropboxFailed, error)
                { Disposition = FailureDisposition.TemporaryNoEffect };
        }
    }

    internal static bool IsUnreadPage(Exception error) =>
        error is JsonException or InvalidDataException or InventoryIncompleteException or TimeoutException or HttpRequestException { StatusCode: null };

    internal static IOException UnreadPage(string path, Exception? error = null) =>
        new(Text.Get(TextKey.ReadDropboxFailed, (TextArg.Path, path), (TextArg.Message, error?.Message ?? string.Empty)), error);

    /// <summary>A failed listing read is incomplete inventory, never absence of the unseen items.</summary>
    private static InventoryIncompleteException Incomplete(string path, Exception error) =>
        new(Text.Get(TextKey.ReadDropboxFailed, (TextArg.Path, path), (TextArg.Message, error.Message)), error);
}
