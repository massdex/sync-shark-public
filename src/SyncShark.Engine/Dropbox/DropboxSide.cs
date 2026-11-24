using System.Buffers;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The pair-scoped Dropbox adapter. Transport, listing, staging and dispatch retain their own responsibilities.</summary>
public sealed class DropboxSide(PairConfig pair, Func<PathScope> resolvedScope, DropboxApi api, Dispatcher dispatcher) : ISide
{
    private readonly DropboxListing listing = new();
    private readonly DropboxFeed feed = new();
    private readonly DeletedHistory history = new();
    private readonly UploadContent stager = new();
    private readonly DropboxWriter writer = new();
    private readonly FolderJobs jobs = new();
    private static readonly ListOptions FeedOptions = new(true, false, true);
    private static readonly FileStreamOptions RecoverySpoolOptions = new()
    {
        Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
        Options = FileOptions.DeleteOnClose | FileOptions.Asynchronous | FileOptions.SequentialScan
    };

    public Side Side => Side.Dropbox;
    public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset => api.ClockOffset;
    public Task<DeletionEvidence> DeletedAsync(string path, CancellationToken ct) => DeletedAsync(path, false, ct);
    public Task<DeletionEvidence> DeletedAsync(string path, bool folder, CancellationToken ct) => history.ReadAsync(api, pair, path, folder, ct);
    public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
        listing.ReadAsync(api, pair, resolvedScope, under, options, ct);
    public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => listing.ReadOneAsync(api, pair, path, ct);

    public async Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct)
    {
        var actual = await listing.ReadByIdAsync(api, pair, id, ct).ConfigureAwait(false);
        if (actual is null) return null;
        var relative = PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(pair.DropboxRoot), actual.NativePath);
        return (actual, relative is not null && !resolvedScope().Excludes(null, relative, actual.Entry.IsFolder));
    }

    public Task<Stream> OpenReadAsync(Entry entry, CancellationToken ct)
    {
        if (entry.IsFolder || string.IsNullOrWhiteSpace(entry.Revision)) throw new ChangedUnderneathException(entry.Path);
        return api.DownloadAsync(DropboxProtocol.Download, JsonSerializer.Serialize(new { path = "rev:" + entry.Revision }), ct);
    }
    public Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(entry.ContentHash);
    }

    public async Task<IStaged> StageAsync(string path, Stream input, Entry source, Entry? selectedTarget, CancellationToken ct) =>
        await stager.StageAsync(api, path, input, source, selectedTarget, ct).ConfigureAwait(false);
    public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct) =>
        writer.UploadAsync(pair, dispatcher, (DropboxStaged)staged, expected, ct);
    public Task<RemovalResult> EnsureFolderAsync(string path, CancellationToken ct) => CreateAsync(path, false, ct);
    public Task<RemovalResult> CreateFolderExclusiveAsync(string path, CancellationToken ct) => CreateAsync(path, true, ct);
    private Task<RemovalResult> CreateAsync(string path, bool exclusive, CancellationToken ct) =>
        DropboxWriter.SubmitAsync(pair, dispatcher, DropboxProtocol.CreateFolder, new(null, path, null), null, exclusive, null, ct);
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) =>
        DeleteSelectedAsync(expected, null, ct);
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, ActionKind origin, CancellationToken ct) =>
        DeleteSelectedAsync(expected, origin, ct);
    private Task<RemovalResult> DeleteSelectedAsync(Entry expected, ActionKind? origin, CancellationToken ct) =>
        DropboxWriter.SubmitAsync(pair, dispatcher, DropboxProtocol.Delete,
            new JobMember(expected.Id, expected.Path, null) { Origin = origin }, expected, false, null, ct);
    public Task<RemovalResult> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
        DropboxWriter.SubmitAsync(pair, dispatcher, DropboxProtocol.Move, new(expected.Id, expected.Path, newPath), expected, false, null, ct);
    public Task<JobReply> ReadJobAsync(AcceptedJob job, CancellationToken ct) => jobs.ReadAsync(api, pair, job, ct);

    /// <summary>Observe only this accepted create's occupant. Its settlement owner receives the original retry evidence.</summary>
    public Task<JobReply.Terminal> ResolveCreateClashAsync(JobMember member, JobReply.Terminal selected, CancellationToken ct) =>
        dispatcher.ResolveCreateClashAsync(pair, member, selected, ct);
    public Task SettleJournalAsync(JournalPosition position, CancellationToken ct) => throw new NotSupportedException();
    public Task<IStaged> ReopenStageAsync(RemovalSelection armedVictim, StageProof expected, CancellationToken ct) => throw new NotSupportedException();

    public async Task<(DirectoryIdentity? Local, DropboxRootIdentity? Dropbox, DriveEvidence? Drive, DriveType? LocalDriveType)> RootAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint(DropboxProtocol.GetCurrentAccount))
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json")
        };
        using var response = await api.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await api.ThrowFailureAsync(response, DropboxProtocol.GetCurrentAccount, ct, pair.DropboxRoot).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var account = await DropboxReply.ReadAsync(body, DropboxReplyShapes.AccountRoot(), ct).ConfigureAwait(false);
        var folder = "ns:" + account.RootNamespaceId;
        if (PathNames.TrimDropboxRoot(pair.DropboxRoot).Length != 0)
        {
            var entry = await ReadOneAsync(string.Empty, ct).ConfigureAwait(false);
            if (entry is not { IsFolder: true, Path.Length: 0 } || string.IsNullOrEmpty(entry.Id)) throw new ChangedUnderneathException(pair.DropboxRoot);
            folder = entry.Id;
        }
        return (null, new(account.AccountId, account.RootNamespaceId, account.HomeNamespaceId, folder), null, null);
    }

    public async Task<(string? Cursor, JournalPosition? Position)> BoundaryAsync(CancellationToken ct) =>
        (await listing.CaptureCursorAsync(api, pair, FeedOptions, ct).ConfigureAwait(false), null);
    public Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, CancellationToken ct) => PageAsync(cursor, null, ct);
    public async Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, (string? Cursor, JournalPosition? Position) until, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(until.Cursor);
        ct.ThrowIfCancellationRequested();
        if (cursor is null || cursor == until.Cursor) return new ChangePage([], until.Cursor, null) { CaughtUp = true };
        var page = await PageAsync(cursor, until.Cursor, ct).ConfigureAwait(false);
        return page with { CaughtUp = !page.ContinuityLost && (page.CaughtUp || page.Cursor == until.Cursor) };
    }
    private async Task<ChangePage> PageAsync(string? cursor, string? boundary, CancellationToken ct)
    {
        try
        {
            return boundary is null ? await feed.ReadAsync(api, pair, cursor, ct).ConfigureAwait(false)
                : await listing.ReadChangePageAsync(api, pair, cursor!, ct).ConfigureAwait(false);
        }
        catch (ListingCursorResetException)
        {
            return new ChangePage([], (await BoundaryAsync(ct).ConfigureAwait(false)).Cursor, null) { ContinuityLost = true };
        }
    }

    public async Task<Recovery?> RecoverAsync(IdentityLocation actual, Entry version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (pair.Roots?.Dropbox.RootNamespaceId is not { Length: > 0 } domain || actual.Domain != domain ||
            string.IsNullOrEmpty(version.Id) || actual.Entry.Id != version.Id || actual.Entry.IsFolder != version.IsFolder) return null;
        if (!version.IsFolder)
        {
            if (string.IsNullOrWhiteSpace(version.Revision) || !ContentHash.IsValid(version.ContentHash)) return null;
            return await RecoverFileAsync(version, ct).ConfigureAwait(false);
        }
        var relative = PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(pair.DropboxRoot), actual.NativePath);
        if (relative is null || !PathNames.Same(relative, actual.Entry.Path)) return null;
        return new Recovery.Folder(actual.Entry, listing.ReadFolderAsync(api, pair, actual.Entry.Id,
            new ListOptions(Recursive: true, IncludeOutOfScope: true), ct));
    }

    // Only spool operations are local failures; a Dropbox read or cancellation keeps its original cause.
    private async Task<Recovery.File> RecoverFileAsync(Entry version, CancellationToken ct)
    {
        ItemFailedException? spoolFailure = null;
        ItemFailedException Failed(Exception error) => spoolFailure = new(version.Path, Local.LocalErrors.Cause(error,
            error is UnauthorizedAccessException ? TextKey.FailLocalInUseOrAccessDenied : TextKey.DownloadIncomplete), error);
        FileStream? stage = null;
        try
        {
            using var hash = new ContentHash();
            var body = await OpenReadAsync(version, ct).ConfigureAwait(false);
            try
            {
                try { stage = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), RecoverySpoolOptions); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Failed(e); }
                await CopyToStageAsync(body, stage, hash, Failed, ct).ConfigureAwait(false);
            }
            finally
            {
                try { await body.DisposeAsync().ConfigureAwait(false); }
                catch when (spoolFailure is not null) { /* Preserve the already-classified spool failure. */ }
            }
            try
            {
                await stage.FlushAsync(ct).ConfigureAwait(false);
                if (stage.Length != version.Size) throw new ItemFailedException(version.Path, TextKey.DownloadIncomplete, null);
                if (!StringComparer.OrdinalIgnoreCase.Equals(hash.Finish(), version.ContentHash))
                    throw new ItemFailedException(version.Path, TextKey.DownloadHashMismatch, null);
                stage.Position = 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Failed(e); }
            return new Recovery.File(version, stage);
        }
        catch
        {
            try { if (stage is not null) await stage.DisposeAsync().ConfigureAwait(false); }
            catch (Exception close) when (close is IOException or UnauthorizedAccessException) { /* Keep the original failure. */ }
            throw;
        }
    }

    private static async Task CopyToStageAsync(Stream body, FileStream stage, ContentHash hash,
        Func<Exception, ItemFailedException> spoolFailure, CancellationToken ct)
    {
        using var owner = MemoryPool<byte>.Shared.Rent();
        var buffer = owner.Memory;
        int read;
        while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            var bytes = buffer[..read];
            hash.Append(bytes.Span);
            try { await stage.WriteAsync(bytes, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw spoolFailure(e); }
        }
    }
}
