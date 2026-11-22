using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The pair-scoped Dropbox adapter. Transport, listing, staging and dispatch retain their own responsibilities.</summary>
public sealed class DropboxSide(PairConfig pair, Func<PathScope> resolvedScope, DropboxApi api, Dispatcher dispatcher) : ISide
{
    private readonly DropboxListing listing = new();
    private readonly DropboxFeed feed = new();
    private readonly DeletedHistory history = new();
    private readonly Func<DropboxApi,string,Stream,Entry,Entry?,CancellationToken,Task<DropboxStaged>> stage = new UploadContent().StageAsync;

    /// <summary>Supplies the same staging operation when the pair uses a scripted provider.</summary>
    public DropboxSide(PairConfig pair, Func<PathScope> scope, DropboxApi api, Dispatcher dispatcher,
        Func<DropboxApi,string,Stream,Entry,Entry?,CancellationToken,Task<DropboxStaged>> stage)
        : this(pair, scope, api, dispatcher) => this.stage = stage;
    private readonly DropboxWriter writer = new();
    private readonly FolderJobs jobs = new();
    private static readonly ListOptions FeedOptions = new(true, false, true);

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
        return api.DownloadAsync("files/download", JsonSerializer.Serialize(new { path = "rev:" + entry.Revision }), ct);
    }
    public Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(entry.ContentHash);
    }

    public async Task<IStaged> StageAsync(string path, Stream input, Entry source, Entry? selectedTarget, CancellationToken ct) =>
        await stage(api, path, input, source, selectedTarget, ct).ConfigureAwait(false);
    public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct) =>
        writer.UploadAsync(pair, dispatcher, (DropboxStaged)staged, expected, ct);
    public Task<RemovalResult> EnsureFolderAsync(string path, CancellationToken ct) => CreateAsync(path, false, ct);
    public Task<RemovalResult> CreateFolderExclusiveAsync(string path, CancellationToken ct) => CreateAsync(path, true, ct);
    private Task<RemovalResult> CreateAsync(string path, bool exclusive, CancellationToken ct) =>
        DropboxWriter.SubmitAsync(pair, dispatcher, DropboxWriter.Create, new(null, path, null), null, exclusive, null, ct);
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) =>
        DropboxWriter.SubmitAsync(pair, dispatcher, DropboxWriter.Delete, new(expected.Id, expected.Path, null), expected, false, null, ct);
    public Task<RemovalResult> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
        DropboxWriter.SubmitAsync(pair, dispatcher, DropboxWriter.Move, new(expected.Id, expected.Path, newPath), expected, false, null, ct);
    public Task<JobReply> ReadJobAsync(AcceptedJob job, CancellationToken ct) => jobs.ReadAsync(api, pair, job, ct);
    public Task SettleJournalAsync(JournalPosition position, CancellationToken ct) => throw new NotSupportedException();
    public Task<IStaged> ReopenStageAsync(RemovalSelection armedVictim, StageProof expected, CancellationToken ct) => throw new NotSupportedException();

    public async Task<(DirectoryIdentity? Local, DropboxRootIdentity? Dropbox, DriveEvidence? Drive, DriveType? LocalDriveType)> RootAsync(CancellationToken ct)
    {
        using var account = await api.CallJsonAsync("users/get_current_account", "null", ct).ConfigureAwait(false);
        var info = account.RootElement.GetProperty("root_info");
        var root = Required(info, "root_namespace_id");
        var folder = "ns:" + root;
        if (PathNames.TrimDropboxRoot(pair.DropboxRoot).Length != 0)
        {
            var entry = await ReadOneAsync(string.Empty, ct).ConfigureAwait(false);
            if (entry is not { IsFolder: true, Path.Length: 0 } || string.IsNullOrEmpty(entry.Id)) throw new ChangedUnderneathException(pair.DropboxRoot);
            folder = entry.Id;
        }
        return (null, new(Required(account.RootElement, "account_id"), root, Required(info, "home_namespace_id"), folder), null, null);
    }
    private static string Required(JsonElement obj, string name) => obj.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new JsonException();

    public async Task<(string? Cursor, JournalPosition? Position)> BoundaryAsync(CancellationToken ct) =>
        (await listing.CaptureCursorAsync(api, pair, FeedOptions, ct).ConfigureAwait(false), null);
    public Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, CancellationToken ct) => PageAsync(cursor, ct);
    public async Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, (string? Cursor, JournalPosition? Position) until, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(until.Cursor);
        ct.ThrowIfCancellationRequested();
        if (cursor is null || cursor == until.Cursor) return new ChangePage([], until.Cursor, null) { CaughtUp = true };
        var page = await PageAsync(cursor, ct).ConfigureAwait(false);
        return page with { CaughtUp = !page.ContinuityLost && (page.CaughtUp || page.Cursor == until.Cursor) };
    }
    private async Task<ChangePage> PageAsync(string? cursor, CancellationToken ct)
    {
        try { return await feed.ReadAsync(api, pair, cursor, ct).ConfigureAwait(false); }
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
            return new Recovery.File(version, await OpenReadAsync(version, ct).ConfigureAwait(false));
        }
        var relative = PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(pair.DropboxRoot), actual.NativePath);
        if (relative is null || !PathNames.Same(relative, actual.Entry.Path)) return null;
        return new Recovery.Folder(actual.Entry, listing.ReadFolderAsync(api, pair, actual.Entry.Id,
            new ListOptions(Recursive: true, IncludeOutOfScope: true), ct));
    }
}
