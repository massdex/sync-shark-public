namespace Massdex.SyncShark.V2;

/// <summary>Applies planned effects to one Dropbox root with conditional, never-blindly-retried writes.</summary>
public sealed partial class DropboxWriter : IEndpointWriter, ISyncTransferResourceEndpoint, IProviderEffectReconciler,
    IFolderTreeDeleter, ISyncDirectDownloadSource, IEndpointMetadataPages, ISourceAbsenceProbe, IRecoveryRecordReader
{
    internal Task<bool> HasUploadSpaceAsync(long needBytes, CancellationToken ct) => _client.HasUploadSpaceAsync(needBytes, ct);

    private readonly Side _side;
    private readonly string? _pairId;
    private readonly DropboxPilotClient _client;
    private readonly string _spoolDirectory;
    private readonly V2Settings _settings;
    private readonly IReadOnlyList<SyncIgnoreRule> _ignoreRules;
    string ISyncTransferResourceEndpoint.TransferResourceKey =>
        ((ISyncTransferResourceEndpoint)_client).TransferResourceKey;

    public DropboxWriter(RootDef root, Side side, DropboxPilotClient client, string spoolDirectory,
        V2Settings? settings = null, IReadOnlyList<SyncIgnoreRule>? ignoreRules = null, string? pairId = null)
    {
        _side = side;
        _pairId = pairId;
        _client = client;
        _spoolDirectory = spoolDirectory;
        _settings = settings ?? V2Settings.Default;
        _ignoreRules = SyncIgnorePolicy.Normalize(ignoreRules);
    }

    /// <summary>Creates or updates one Dropbox file from a hashed, seekable source stream.</summary>
    public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) => Guard(async () =>
    {
        if (a.Source is not { Kind: ItemKind.File } source || source.Hash is null || !content.CanSeek)
            throw new ArgumentException("A Dropbox put requires a file source with a hash and a seekable stream.");
        if (content.Length != source.Size)
            throw new ExpectedTargetMismatchException("The upload source length changed since planning.");
        var expected = a.ExpectedTarget;
        if (expected is not null && (expected.Kind != ItemKind.File ||
            string.IsNullOrWhiteSpace(expected.VersionId) || !PairPath.Same(a.Path, expected.Path)))
            throw new ExpectedTargetMismatchException("A conditional upload requires the planned file path and revision.");
        var result = content.Length <= DropboxPilotClient.BatchedUploadMaximumBytes
            ? await _client.UploadBatchedAsync(a.Path, content, source.Hash, expected?.VersionId, _settings, ct, source.Mtime)
            : await _client.UploadWithMtimeAsync(a.Path, content, source.Hash, expected?.VersionId, source.Mtime, ct);
        if (expected is not null && !string.Equals(expected.ObjectId, result.FileId, StringComparison.Ordinal))
            throw new UncertainEffectException("Dropbox returned another object identity after a conditional upload.");
        return new ItemState(a.Path, ItemKind.File, result.FileId, result.Revision, result.ContentHash,
            result.Size, result.ClientModifiedUtc ?? throw new UncertainEffectException(
                "Dropbox accepted the upload but did not return client_modified."), ItemFlags.None, DropboxNameMapping.Fact(a.Path));

    });

    /// <summary>Moves the object at the action's source path to its destination, preserving identity.</summary>
    public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => Guard(async () =>
    {
        if (string.IsNullOrWhiteSpace(a.FromPath) || a.ExpectedTarget is null)
            throw new ArgumentException("A Dropbox move requires a source path and the expected target.");
        if (a.ProviderEffect?.Kind == CaseRenameCheckpointKind ||
            PairPath.Same(a.FromPath, a.Path) && !string.Equals(a.FromPath, a.Path, StringComparison.Ordinal))
            return await MoveCaseAsync(a, ct).ConfigureAwait(false);
        var moved = await _client.MoveRevisionAsync(
            DropboxItems.ToEntry(a.ExpectedTarget with { Path = a.FromPath! }), a.Path, a.RequireMoveRevision, ct);
        return DropboxItems.ToItem(moved);
    });

    /// <summary>Deletes a conditional file target or a folder containing only ignored leftovers.</summary>
    public Task DeleteAsync(SyncAction a, CancellationToken ct) => Guard(async () =>
    {
        if (a.ExpectedTarget is null)
            throw new ArgumentException("A Dropbox delete requires the expected target.");
        if (!PairPath.Same(a.Path, a.ExpectedTarget.Path))
            throw new ExpectedTargetMismatchException("The delete target path differs from its planned identity.");
        if (a.ExpectedTarget.Kind == ItemKind.Folder)
            await _client.DeleteEmptyFolderAsync(DropboxItems.ToEntry(a.ExpectedTarget), ct, a.PreserveIgnoredChildren ? [] : _ignoreRules);
        else
            await _client.DeleteBatchedFileAsync(DropboxItems.ToEntry(a.ExpectedTarget), _settings, ct);
    });

    /// <summary>Checks a saved delete or folder-create job without sending another mutation.</summary>
    public Task<bool?> ResolveProviderEffectAsync(SyncAction action, ProviderJobCheckCache? jobChecks, CancellationToken ct) => Guard(async () =>
    {
        if (action.ProviderEffect is not { } checkpoint) return (bool?)null;
        if (CanResumeProviderEffect(action)) return await ResumeCaseAsync(action, ct).ConfigureAwait(false);
        using var document = System.Text.Json.JsonDocument.Parse(checkpoint.Id);
        var job = document.RootElement;
        if (checkpoint.Kind == "mkdir-job")
            return await _client.ResolveMkDirBatchJobAsync(action.Path, job.GetProperty("jobId").GetString()!,
                job.GetProperty("entryIndex").GetInt32(), job.GetProperty("entryCount").GetInt32(), ct, jobChecks);
        if (action.Kind != ActionKind.Delete || action.ExpectedTarget is null || checkpoint.Kind != "delete-job")
            return (bool?)null;
        return await _client.ResolveDeleteBatchJobAsync(DropboxItems.ToEntry(action.ExpectedTarget),
            job.GetProperty("jobId").GetString()!, job.GetProperty("entryIndex").GetInt32(),
            job.GetProperty("entryCount").GetInt32(), ct, jobChecks);
    });

    /// <summary>Creates a folder through the coalescing create batch; an existing folder at the path is the same result.</summary>
    public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => Guard(async () =>
        DropboxItems.ToItem(await _client.MkDirBatchedAsync(path, _settings, ct)));

    /// <summary>
    /// No provider call: Dropbox keeps the displaced revision in version history, so the reference is the
    /// victim's own path, revision, hash and size -10-14).
    /// </summary>
    public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
        ReadRevisionRecoveryAsync(victim, ct);

    // Pure metadata construction, also used by isolated plan-only reconciliation; no writer effect is called.
    internal Task<RecoveryRef> ReadRevisionRecoveryAsync(ItemState victim, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (victim.Kind != ItemKind.File)
            throw new ArgumentException("Dropbox version history preserves files only; a folder has no content.");
        if (victim.VersionId is null || victim.Hash is null)
            throw new ArgumentException("A Dropbox file recovery reference requires its revision and hash.");
        return Task.FromResult(new RecoveryRef(_side, victim.Path, victim.VersionId, victim.Hash, victim.Size));
    }

    private readonly SemaphoreSlim _absenceReads = new(8, 8);

    /// <inheritdoc/>
    public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) => ObserveAsync(path, ct);

    /// <inheritdoc/>
    public async Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct)
    {
        await _absenceReads.WaitAsync(ct).ConfigureAwait(false);
        try { return await _client.ObjectExistsAsync(historical.ObjectId, ct).ConfigureAwait(false); }
        catch (Exception error) when (DropboxItems.MapAccessError(error) is var mapped && mapped != error)
        { throw mapped; }
        finally { _absenceReads.Release(); }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AbsenceObservation>> IsAbsentBatchAsync(
        IReadOnlyList<(ItemState Historical, string Path)> requests, CancellationToken ct)
    {
        if (requests.Count > 128) throw new ArgumentOutOfRangeException(nameof(requests));
        try
        {
            return await _client.ProbeSourceAbsenceAsync(requests.Select(x =>
                (x.Historical.ObjectId, x.Historical.Path, x.Path)).ToArray(), _absenceReads, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (DropboxItems.MapAccessError(error) is var mapped && mapped != error)
        { throw mapped; }
    }

    /// <inheritdoc/>
    public async Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct)
    {
        if (historical is null || string.IsNullOrWhiteSpace(historical.ObjectId)) return null;
        return (await IsAbsentBatchAsync([(historical, path)], ct).ConfigureAwait(false))[0].Absent;
    }

    /// <summary>One fresh exact-path observation; absence is returned as null.</summary>
    public async Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
    {
        try
        {
            var entry = await _client.FindEntryAtPathAsync(path, ct);
            return entry is null ? null : DropboxItems.ToItem(entry);
        }
        catch (Exception exception) when (DropboxItems.MapAccessError(exception) is var mapped && mapped != exception)
        {
            throw mapped;
        }
    }

    public async Task<IReadOnlyDictionary<string, ItemState?>> ObservePageAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var result = new Dictionary<string, ItemState?>(StringComparer.Ordinal);
        foreach (var page in paths.Chunk(128))
        {
            var entries = await _client.ReadMetadataPageAsync(page, ct).ConfigureAwait(false);
            foreach (var entry in entries) result[entry.Key] = entry.Value is null ? null : DropboxItems.ToItem(entry.Value);
        }
        return result;
    }

    /// <summary>Downloads exactly the requested revision into a delete-on-close spool and returns it positioned at zero.</summary>
    public async Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct)
    {
        if (s.Kind != ItemKind.File) throw new ArgumentException("A Dropbox read requires a file.");
        var spool = NewSpool();
        var keep = false;
        try
        {
            var download = await _client.DownloadAsync(DropboxItems.ToEntry(s), spool, ct);
            RequireDownloadIdentity(s, download);
            if (spool.Length != s.Size) throw new ExpectedTargetMismatchException("Dropbox download length differs from the planned file.");
            spool.Position = 0;
            keep = true;
            return spool;
        }
        catch (Exception exception) when (DropboxItems.MapWriteError(exception) is var mapped && mapped != exception)
        {
            throw mapped;
        }
        finally
        {
            if (!keep) await spool.DisposeAsync();
        }
    }

    /// <summary>
    /// Downloads exactly the requested revision into the caller's stream, with no product spool: the destination
    /// is the target's own temporary file. The response revision, content hash and size must match the planned
    /// source exactly; a mismatch refuses the download before the target is published.
    /// </summary>
    public Task<SyncDownloadResult> DownloadToAsync(ItemState s, Stream destination, CancellationToken ct) =>
        Guard(async () =>
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (s.Kind != ItemKind.File || s.VersionId is null || s.Hash is null)
                throw new InvalidDataException("A direct Dropbox download requires a planned file revision and hash.");
            var download = await _client.DownloadAsync(DropboxItems.ToEntry(s), destination, ct)
                .ConfigureAwait(false);
            RequireDownloadIdentity(s, download);
            return download;
        });

    internal static void RequireDownloadIdentity(ItemState expected, SyncDownloadResult actual)
    {
        if (!string.Equals(actual.FileId, expected.ObjectId, StringComparison.Ordinal) ||
            !string.Equals(actual.Revision, expected.VersionId, StringComparison.Ordinal) ||
            actual.Size != expected.Size ||
            Content.SameContent(actual.Size, actual.ContentHash, expected.Size, expected.Hash) != true)
            throw new ExpectedTargetMismatchException("Dropbox download identity or size differs from the planned file.");
    }

    private FileStream NewSpool()
    {
        Directory.CreateDirectory(_spoolDirectory);
        return new FileStream(Path.Combine(_spoolDirectory, Guid.NewGuid().ToString("N") + ".part"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }

    private static async Task<T> Guard<T>(Func<Task<T>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception exception) when (DropboxItems.MapWriteError(exception) is var mapped && mapped != exception)
        {
            throw mapped;
        }
    }

    private static async Task Guard(Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception exception) when (DropboxItems.MapWriteError(exception) is var mapped && mapped != exception)
        {
            throw mapped;
        }
    }
}
