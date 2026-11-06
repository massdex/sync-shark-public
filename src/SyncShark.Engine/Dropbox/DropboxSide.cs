using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox side (section 5.2): delegates to DropboxListing and DropboxWriter. Only the listing view is
/// an <see cref="ISide"/>; the concrete side keeps identity, evidence, cursor and writer functions.</summary>
public sealed partial class DropboxSide : IIdentitySide
{
    private readonly DropboxApi _api;
    private readonly PairConfig _pair;
    private readonly DropboxListing _listing;
    private readonly DropboxWriter _writer;
    private readonly DropboxDeletionEvidence _deletionEvidence;

    /// <summary>Creates the listing and writer that share this pair's API, scope and settings.</summary>
    public DropboxSide(DropboxApi api, PairConfig pair, PathScope scope, EngineSettings settings,
        DropboxListing listing)
    {
        _api = api;
        _pair = pair;
        ArgumentNullException.ThrowIfNull(listing);
        _listing = listing;
        _writer = new DropboxWriter(api, pair, settings, _listing);
        _deletionEvidence = new DropboxDeletionEvidence(api, pair, _listing);
    }

    public Side Side => Side.Dropbox;

    internal ISide WithListingContext(Func<ListingReadContext> beginListing) => new ListingView(this, beginListing);

    // The one ISide for this side: only ReadAllAsync reads its bound listing context; the rest forward to the owner.
    private sealed class ListingView(DropboxSide owner, Func<ListingReadContext> beginListing) : ISide
    {
        public Side Side => owner.Side;
        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
            owner._listing.ReadAll(under, options, beginListing(), ct);
        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => owner.ReadOneAsync(path, ct);
        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            owner.OpenReadAsync(entry, attempt, ct);
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => owner.ContentHashAsync(entry, ct);
        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) =>
            owner.StageAsync(path, content, source, ct);
        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct) => owner.PublishAsync(staged, expected, selection, ct);
        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => owner.EnsureFolderAsync(path, ct);
        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) =>
            owner.DeleteAsync(expected, selection, ct);
        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
            owner.MoveAsync(expected, newPath, ct);
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            owner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct) => owner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }
    /// <summary>Reads the entry at path, or null when it is absent.</summary>
    public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => _listing.ReadOneAsync(path, ct);
    /// <summary>Opens a stream of exactly this version; the caller disposes it.</summary>
    public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) => _writer.OpenReadAsync(entry, attempt, ct);
    /// <summary>Returns the entry's stored Dropbox content hash without downloading it.</summary>
    public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => Task.FromResult(entry.ContentHash);
    /// <summary>Prepares content at path without touching the target.</summary>
    public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) =>
        _writer.StageAsync(path, content, source, ct);
    /// <summary>Publishes staged content, requiring expected's version when one is given.</summary>
    public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct) =>
        _writer.PublishAsync(staged, expected, selection, ct);
    public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => _writer.EnsureFolderAsync(path, ct);
    /// <summary>Returns the classified removal; folder effects require the selected folder operation.</summary>
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) => _writer.DeleteAsync(expected, selection, ct);
    /// <summary>Moves expected to newPath, which must be free.</summary>
    public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
        _writer.MoveAsync(expected, newPath, ct);

    /// <summary>Reads the configured root's current account, namespace and folder identity without listing children.</summary>
    public partial Task<DropboxRootIdentity> ReadRootIdentityAsync(CancellationToken ct);

    /// <summary>Returns the current native location, including outside this pair, or proven identity absence.</summary>
    public partial Task<IdentityLocation?> ReadByIdAsync(string id, CancellationToken ct);

    /// <summary>Reads targeted evidence through the shared deletion-evidence owner.</summary>
    public partial Task<DeletionEvidence> ReadDeletionEvidenceAsync(
        string path, bool candidateIsFolder, CancellationToken ct);

    /// <summary>Captures a checked removal's own cursor, never the feed's saved position.</summary>
    public Task<string> CaptureFolderCursorAsync(Func<RequestAttempt, CancellationToken, Task<bool>> complete,
        CancellationToken ct) =>
        RecoveryReadAsync(new object(),
            (attempt, token) => _listing.CaptureCursorAsync(DropboxFeed.FeedOptions, attempt, token), complete, ct);

    /// <summary>Streams every change after a checked removal's cursor through the shared page reader, without
    /// advancing any saved source position. A failed page or repeating continuation throws, so it grants no evidence.</summary>
    public async IAsyncEnumerable<Listed> ReadFolderChangesAsync(string cursor,
        Func<RequestAttempt, CancellationToken, Task<bool>> complete,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cycle = new ContinuationCycle();
        var operation = new object();
        var current = cursor;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await RecoveryReadAsync(operation,
                    (attempt, token) => _listing.ReadChangePageAsync(current, attempt, token), complete, ct)
                .ConfigureAwait(false);
            await using (page)
            {
                foreach (var listed in page.ReadEntries()) yield return listed;
                if (!page.HasMore) yield break;
                if (cycle.Repeats(current, page.Cursor))
                    throw new ItemFailedException(string.Empty, TextKey.ReadDropboxFailed, null);
                current = page.Cursor;
            }
        }
    }

    // One Recovery episode per finite read: a fresh attempt per try, and the caller's completion decides a retry.
    private static async Task<T> RecoveryReadAsync<T>(object operation,
        Func<RequestAttempt, CancellationToken, Task<T>> read,
        Func<RequestAttempt, CancellationToken, Task<bool>> complete, CancellationToken ct)
    {
        while (true)
        {
            var attempt = new RequestAttempt(operation, AttemptOwner.Recovery);
            T result;
            try
            {
                result = await read(attempt, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (await complete(attempt, ct).ConfigureAwait(false)) continue;
                throw;
            }
            await complete(attempt, ct).ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>Submits the persisted wire-ordered effects under their immutable submission identity.</summary>
    public partial Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
        IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct);

    /// <summary>Observes a submitted job without replaying its effects.</summary>
    public partial Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
        RequestAttempt attempt, CancellationToken ct);

    public async partial Task<DropboxRootIdentity> ReadRootIdentityAsync(CancellationToken ct)
    {
        var operation = new object();
        var account = await _api.RpcAsync(DropboxProtocol.GetCurrentAccount, null!,
            DropboxReply.AccountRoot(), new RequestAttempt(operation, AttemptOwner.Request), ct)
            .ConfigureAwait(false);
        string folderId;
        if (PathNames.TrimDropboxRoot(_pair.DropboxRoot).Length == 0)
        {
            folderId = PilotPath.DropboxNamespaceRootIdentity(account.RootNamespaceId);
        }
        else
        {
            var root = await _api.RpcAsync(DropboxProtocol.GetMetadata,
                new { path = DropboxPaths.Remote(_pair, ""), include_deleted = false },
                DropboxReply.Listed(_pair), new RequestAttempt(operation, AttemptOwner.Request), ct)
                .ConfigureAwait(false);
            if (root.Entry is not { IsFolder: true } folder ||
                string.IsNullOrEmpty(folder.Id) || !PathNames.Same(root.Path, ""))
                throw new ChangedUnderneathException(_pair.DropboxRoot);
            folderId = folder.Id;
        }
        return new DropboxRootIdentity(account.AccountId, account.RootNamespaceId,
            account.HomeNamespaceId, folderId);
    }

    public partial Task<IdentityLocation?> ReadByIdAsync(string id, CancellationToken ct) =>
        _listing.ReadByIdAsync(id, new RequestAttempt(new object(), AttemptOwner.Request), ct);

    public partial Task<DeletionEvidence> ReadDeletionEvidenceAsync(
        string path, bool candidateIsFolder, CancellationToken ct) =>
        _deletionEvidence.ReadDeletionEvidenceAsync(path, candidateIsFolder, ct);

    public partial Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
        IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
        _writer.SubmitFolderRemovalAsync(submission, effects, attempt, ct);

    public partial Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
        RequestAttempt attempt, CancellationToken ct) =>
        _writer.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
}
