using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Delegates a real FakeSide, adding skip-flagged entries and/or one listing failure.</summary>
internal sealed class ProxySide(ISide inner) : ISide
{
    public IReadOnlyList<Entry> Extra { get; init; } = [];
    // FakeSide keeps no history, so a discovery listing's deleted names are supplied here.
    public IReadOnlyList<string> Deleted { get; init; } = [];
    public Exception? Failure { get; init; }
    public Action? OnListing { get; init; }
    public Func<Task>? ListingGate { get; init; }
    public Action<string>? OnDelete { get; init; }
    public Action<IReadOnlyList<FolderRemovalEffect>>? OnSubmit { get; init; }
    public Func<string, Exception?>? ReadFault { get; init; }
    public Func<string, Task?>? BlockEnsure { get; init; }
    public Func<string, CancellationToken, Task>? BlockPublish { get; init; }
    public Action<Entry>? AfterPublish { get; init; }
    public Action<IStaged>? AfterStage { get; init; }
    public Action<Entry?, RemovalSelection?>? OnPublish { get; init; }
    public Func<string, Exception?>? EnsureFault { get; init; }
    public bool Finished { get; private set; }
    public bool LateMarkers { get; init; }
    public Func<string, Exception?>? ListingFault { get; init; }
    public Action<string>? OnRead { get; init; }
    public Func<string, CancellationToken, Task>? BeforeRead { get; init; }
    public Func<Entry, CancellationToken, Task<string>>? Hash { get; init; }
    public int Listings { get; private set; }
    public List<ListOptions> ListingOptions { get; } = [];
    public int Hashes { get; private set; }

    public Side Side => inner.Side;

    public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Listings++;
        ListingOptions.Add(options);
        OnListing?.Invoke();
        if (ListingGate is { } gate) await gate();
        var yielded = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await foreach (var item in inner.ReadAllAsync(under, options, ct))
            {
                if (item.Entry is { } entry) yielded.Add(entry.Path);
                yield return LateMarkers ? item : Replaced(item);
            }
            foreach (var entry in Extra)
                if ((LateMarkers || !yielded.Contains(entry.Path)) && IsUnder(entry.Path, under))
                    yield return new Listed(entry.Path, entry);
            if (options.IncludeDeleted)
                foreach (var path in Deleted)
                    if (IsUnder(path, under)) yield return new Listed(path, null);
            if (ListingFault?.Invoke(under) is { } listingFault) throw listingFault;
            if (Failure is { } failure) throw failure;
        }
        finally
        {
            Finished = true;
        }
    }

    public async Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
    {
        if (BeforeRead is { } before) await before(path, ct);
        OnRead?.Invoke(path);
        if (ReadFault?.Invoke(path) is { } fault) throw fault;
        return Replace(path) is { } skipped ? skipped : await inner.ReadOneAsync(path, ct);
    }

    public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
        inner.OpenReadAsync(entry, attempt, ct);
    public Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
    {
        Hashes++;
        return Hash?.Invoke(entry, ct) ?? inner.ContentHashAsync(entry, ct);
    }
    public Func<string, CancellationToken, Task>? BeforeStage { get; init; }
    public async Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
    {
        if (BeforeStage is { } before) await before(path, ct);
        var staged = await inner.StageAsync(path, content, source, ct);
        AfterStage?.Invoke(staged);
        return staged;
    }
    public async Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
        CancellationToken ct)
    {
        if (BlockPublish is { } block) await block(staged.Path, ct);
        OnPublish?.Invoke(expected, selection);
        var published = await inner.PublishAsync(staged, expected, selection, ct);
        if (published.Outcome == RemovalOutcome.Published && published.Final is { } entry) AfterPublish?.Invoke(entry);
        return published;
    }

    public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
    {
        if (EnsureFault?.Invoke(path) is { } error) return Task.FromException<Entry>(error);
        // A blocked folder action completes even after the run is cancelled, so the producer reaches its check.
        return BlockEnsure?.Invoke(path) is { } blocked
            ? EnsureAfterAsync(blocked, path)
            : inner.EnsureFolderAsync(path, ct);
    }

    private async Task<Entry> EnsureAfterAsync(Task blocked, string path)
    {
        await blocked;
        return await inner.EnsureFolderAsync(path, CancellationToken.None);
    }
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
    {
        OnDelete?.Invoke(expected.Path);
        return inner.DeleteAsync(expected, selection, ct);
    }
    public Func<string, Exception?>? MoveFault { get; init; }
    public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
        MoveFault?.Invoke(newPath) is { } fault ? Task.FromException<Entry>(fault) : inner.MoveAsync(expected, newPath, ct);

    public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
        IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
    {
        OnSubmit?.Invoke(effects);
        return inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
    }
    public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
        RequestAttempt attempt, CancellationToken ct) =>
        inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);

    private static bool IsUnder(string path, string under) => under.Length == 0 ||
        path.StartsWith(under + PathNames.PathSeparator, StringComparison.Ordinal);

    private Entry? Replace(string path) => Extra.FirstOrDefault(candidate => candidate.Path == path);

    private Listed Replaced(Listed item) =>
        item.Entry is { } entry && Replace(entry.Path) is { } skipped
            ? new Listed(entry.Path, skipped)
            : item;
}
