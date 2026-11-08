using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>The Dropbox side of a scenario: arrange files/folders and snapshot the tree. Offline this is the
/// in-memory account; live it is the engine's own real DropboxApi/DropboxWriter over the owner's account.</summary>
internal interface IDropboxAccount
{
    IReadOnlyList<string> Calls { get; }
    /// <summary>How many content downloads the engine (not the scenario's own snapshot) performed.</summary>
    int ContentDownloads { get; }
    void SeedFile(string path, string content, DateTimeOffset modified);
    void SeedFolder(string path);
    void Rename(string from, string to);
    void Delete(string path);
    IReadOnlyList<(string Path, bool Folder, byte[] Bytes)> Snapshot();
    /// <summary>Refuses the next upload of path, like a provider write failure.</summary>
    void RefuseUpload(string path);
    void ClearWriteRefusals();
    /// <summary>Fails the next count listings at the transport boundary.</summary>
    void FailListings(int count);
    /// <summary>Fails the next count delete-job checks at the transport boundary.</summary>
    void FailFolderJobChecks(int count);
}

/// <summary>The in-memory Dropbox account, wrapped with the faults the cases inject.</summary>
internal sealed class FakeDropboxAccount(Massdex.SyncShark.V2.DropboxWire wire) : IDropboxAccount
{
    public IReadOnlyList<string> Calls => wire.Calls;

    public int ContentDownloads => wire.Calls.Count(call => call == "/2/files/download");

    public void SeedFile(string path, string content, DateTimeOffset modified) =>
        wire.SeedFile(path, content, modified);

    public void SeedFolder(string path)
    {
        if (wire.Snapshot().Any(item => item.Folder && item.Path == path)) return;
        wire.SeedFolder(path);
    }

    public void Rename(string from, string to) => wire.Rename(from, to);

    public void Delete(string path) => wire.Delete(path);

    public IReadOnlyList<(string Path, bool Folder, byte[] Bytes)> Snapshot() => wire.Snapshot();

    // "other" is the provider's unclassified write refusal; "conflict" would be a name clash (PathOccupied).
    public void RefuseUpload(string path) => wire.NameWriteErrors[path] = "other";

    public void ClearWriteRefusals() => wire.NameWriteErrors.Clear();

    // A definite refusal, not a transient 5xx the run may retry into success.
    public void FailListings(int count) =>
        wire.Script("/2/files/list_folder", System.Net.HttpStatusCode.BadRequest, count);

    // The same structured refusal the provider returns for a bad observation.
    public void FailFolderJobChecks(int count) =>
        wire.Script("/2/" + DropboxProtocol.DeleteBatchCheck, System.Net.HttpStatusCode.BadRequest, count);
}

/// <summary>The owner's real Dropbox, arranged through the engine's own side (Stage/Publish/EnsureFolder/Move/Delete)
/// and, for a folder, one test-side authenticated delete bounded by the owned sandbox.</summary>
internal sealed class LiveDropboxAccount(Func<DropboxSide> side, Func<PairConfig> pair, LiveDropboxHandler handler,
    Func<string, CancellationToken, Task<HttpResponseMessage>> deleteOwnedFolderOnce,
    Func<string, CancellationToken, Task<FolderIdObservation>> observeOwnedFolderIdOnce)
    : IDropboxAccount
{
    /// <summary>One delete_v2 reply carries a single metadata object; 64 KiB bounds a malformed body well
    /// above that.</summary>
    private const int MaximumArrangementReplyBytes = 64 * 1024;

    private static CancellationToken Ct => Scenario.Cancellation;

    private string? arrangementRootPath;
    private string? arrangementRootId;

    // The counters keep per-route totals, not a call history: this is each measured route repeated by its attempts.
    public IReadOnlyList<string> Calls => handler.Counters.Snapshot().Routes
        .OrderBy(route => route.Key, StringComparer.Ordinal)
        .SelectMany(route => Enumerable.Repeat("/2/" + route.Key, (int)route.Value.Attempts))
        .ToArray();

    public int ContentDownloads => handler.ContentDownloads;

    public void SeedFile(string path, string content, DateTimeOffset modified)
    {
        Ct.ThrowIfCancellationRequested();
        var existing = Side().ReadOneAsync(path, Ct).GetAwaiter().GetResult();
        var bytes = Encoding.UTF8.GetBytes(content);
        var source = new Entry(path, false, bytes.Length, modified, "", "", "", null);
        using var stream = new MemoryStream(bytes);
        var staged = Side().StageAsync(path, stream, source, Ct).GetAwaiter().GetResult();
        try
        {
            var published = Side().PublishAsync(staged, existing, null, Ct).GetAwaiter().GetResult();
            if (published.Outcome != RemovalOutcome.Published)
                throw new InvalidOperationException("the live account did not publish " + path);
        }
        finally { staged.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public void SeedFolder(string path) => Side().EnsureFolderAsync(path, Ct).GetAwaiter().GetResult();

    public void Rename(string from, string to) =>
        Side().MoveAsync(Required(from), to, Ct).GetAwaiter().GetResult();

    public void Delete(string path)
    {
        if (Side().ReadOneAsync(path, Ct).GetAwaiter().GetResult() is not { } entry) return;
        if (entry.IsFolder)
        {
            DeleteOwnedFolderForArrangement(path, entry.Id);
            return;
        }
        var removed = Side().DeleteAsync(entry, null, Ct).GetAwaiter().GetResult();
        if (removed.Outcome != RemovalOutcome.Removed)
            throw new InvalidOperationException("the live account did not remove " + path);
    }

    public IReadOnlyList<(string Path, bool Folder, byte[] Bytes)> Snapshot()
    {
        // The scenario's own tree read is neither an engine action nor a scripted fault consumer.
        handler.SuspendObservation = true;
        try { return SnapshotAsync().GetAwaiter().GetResult(); }
        finally { handler.SuspendObservation = false; }
    }

    private async Task<IReadOnlyList<(string Path, bool Folder, byte[] Bytes)>> SnapshotAsync()
    {
        var result = new List<(string Path, bool Folder, byte[] Bytes)>();
        if (await Side().ReadOneAsync("", Ct).ConfigureAwait(false) is null) return result;
        // Include out-of-scope entries: the scenario's own tree read must see excluded paths too.
        await foreach (var listed in Side().WithListingContext(Scenario.BeginListing)
            .ReadAllAsync("", new ListOptions(true, true), Ct))
        {
            Ct.ThrowIfCancellationRequested();
            // Skipped (e.g. reserved-name) entries still exist in Dropbox, so the tree read keeps them.
            if (listed.Entry is not { } entry) continue;
            if (entry.IsFolder)
            {
                result.Add((entry.Path, true, []));
                continue;
            }
            await using var stream = await Side().OpenReadAsync(entry, null, Ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, Ct).ConfigureAwait(false);
            result.Add((entry.Path, false, buffer.ToArray()));
        }
        return result.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    public void RefuseUpload(string path) => handler.RefuseUpload(DropboxPaths.Remote(pair(), path));

    public void ClearWriteRefusals() => handler.ClearWriteRefusals();

    public void FailListings(int count) => handler.FailListings(count);

    // The scripted check failure belongs to the in-memory wire; a live account has no equivalent fault.
    public void FailFolderJobChecks(int count) =>
        throw new NotSupportedException("a live Dropbox account cannot script a delete-job check failure");

    /// <summary>Freezes the one owned sandbox folder this account generation may remove, before account exposure.
    /// The path is the pair-relative spelling this account reads with; an empty path is the pair root.</summary>
    internal void BindFolderArrangementScope(string rootPath, string rootId)
    {
        if (arrangementRootId is not null)
            throw new InvalidOperationException("the folder arrangement scope is already bound");
        if (!IsCanonicalRelative(rootPath) || string.IsNullOrEmpty(rootId))
            throw new InvalidOperationException("the folder arrangement scope needs a canonical owned root and its id");
        arrangementRootPath = rootPath;
        arrangementRootId = rootId;
    }

    /// <summary>The one bounded external-actor body for a folder arrangement. Paths are the pair-relative spellings
    /// the account or probe reads with; an empty root path is that pair root. It verifies the owned scope and the
    /// target's current folder identity and kind, sends at most one delete, and completes only on matching folder
    /// metadata plus authoritative absence of both the frozen id and the original path; anything else throws.</summary>
    internal static async Task CompleteFolderArrangementAsync(string rootPath, string rootId, string path,
        string expectedFolderId, Func<string, CancellationToken, Task<HttpResponseMessage>> deleteOnce,
        Func<string, CancellationToken, Task<FolderIdObservation>> observeIdOnce,
        Func<string, CancellationToken, Task<Entry?>> readPathOnce, CancellationToken ct)
    {
        if (!IsCanonicalRelative(rootPath) || string.IsNullOrEmpty(rootId))
            throw new InvalidOperationException("the folder arrangement has no owned sandbox");
        if (!IsCanonicalRelative(path) || rootPath.Length > 0 && !PathNames.IsSelfOrUnder(path, rootPath))
            throw new InvalidOperationException("refusing a folder arrangement outside the owned sandbox: " + path);
        if (string.IsNullOrEmpty(expectedFolderId))
            throw new InvalidOperationException("refusing a folder arrangement without its frozen id: " + path);

        if (await readPathOnce(path, ct).ConfigureAwait(false) is not { } current) return;
        if (!current.IsFolder || !PathNames.Same(current.Path, path) ||
            !string.Equals(current.Id, expectedFolderId, StringComparison.Ordinal) ||
            (PathNames.Same(path, rootPath) && !string.Equals(current.Id, rootId, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "refusing a folder arrangement whose target is not the expected folder: " + path);

        using (var response = await deleteOnce(expectedFolderId, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("the folder delete was refused: " + (int)response.StatusCode);
            using var reply = await ReadBoundedReplyAsync(response, ct).ConfigureAwait(false);
            RequireDeletedFolder(reply, expectedFolderId);
        }

        if (await observeIdOnce(expectedFolderId, ct).ConfigureAwait(false) is not
            FolderIdObservation.AuthoritativeNotFound)
            throw new InvalidOperationException("folder " + expectedFolderId + " is not authoritatively absent");
        if (await readPathOnce(path, ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("the arranged folder path still exists: " + path);
    }

    private void DeleteOwnedFolderForArrangement(string path, string expectedFolderId)
    {
        if (arrangementRootPath is not { } rootPath || arrangementRootId is not { } rootId)
            throw new InvalidOperationException("no folder arrangement scope is bound");
        CompleteFolderArrangementAsync(rootPath, rootId, path, expectedFolderId, deleteOwnedFolderOnce,
            observeOwnedFolderIdOnce, (candidate, ct) => Side().ReadOneAsync(candidate, ct), Ct)
            .GetAwaiter().GetResult();
    }

    /// <summary>True for a spelling with no empty, "." or ".." component and no leading separator.</summary>
    private static bool IsCanonicalRelative(string path) =>
        path.Length == 0 ||
        path[0] != '/' && path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    private static async Task<JsonDocument> ReadBoundedReplyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is not { } content)
            throw new InvalidOperationException("the folder delete returned no receipt");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = new byte[MaximumArrangementReplyBytes + 1];
        var read = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct).ConfigureAwait(false);
        if (read > MaximumArrangementReplyBytes)
            throw new InvalidOperationException("the folder delete receipt is too large");
        return JsonDocument.Parse(bytes.AsMemory(0, read));
    }

    /// <summary>The delete_v2 reply must carry complete, live folder metadata for the frozen id. The separate
    /// original-path read proves the path; the id is the identity this receipt must match.</summary>
    private static void RequireDeletedFolder(JsonDocument reply, string expectedFolderId)
    {
        if (reply.RootElement.ValueKind != JsonValueKind.Object ||
            !reply.RootElement.TryGetProperty(DropboxProtocol.Metadata, out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty(DropboxProtocol.Tag, out var kind) ||
            kind.ValueKind != JsonValueKind.String || kind.GetString() != DropboxProtocol.TagFolder ||
            !metadata.TryGetProperty(DropboxProtocol.Id, out var id) ||
            id.ValueKind != JsonValueKind.String || id.GetString() != expectedFolderId ||
            !metadata.TryGetProperty(DropboxProtocol.PathDisplay, out var display) ||
            display.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(
                "the folder delete returned no complete metadata for " + expectedFolderId);
    }

    private DropboxSide Side() => side();

    private Entry Required(string path) =>
        Side().ReadOneAsync(path, Ct).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException("the live account has no " + path);
}

/// <summary>What observing one frozen folder id found. Only authoritative absence at every path completes an
/// arrangement; a live id at another path and every unrecognized outcome fail it without another delete.</summary>
internal abstract record FolderIdObservation
{
    /// <summary>Complete live metadata for the requested id, at its current provider path.</summary>
    public sealed record Live(string Id, bool IsFolder, string FullProviderPath) : FolderIdObservation;

    /// <summary>A well-formed get_metadata path/not_found reply from the captured account and namespace.</summary>
    public sealed record AuthoritativeNotFound : FolderIdObservation;

    /// <summary>Every other outcome, including token or provider errors, malformed metadata, cancellation and a
    /// lost or uncertain reply.</summary>
    public sealed record Uncertain : FolderIdObservation;
}
