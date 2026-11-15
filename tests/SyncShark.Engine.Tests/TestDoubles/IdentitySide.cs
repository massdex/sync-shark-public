namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Wraps a side with a supplied by-ID observation, for checks whose subject reads an identity.</summary>
internal sealed class IdentitySide(ISide inner,
    Func<string, CancellationToken, Task<(IdentityLocation Location, bool InScope)?>> identity,
    Func<string, CancellationToken, Task<(IdentityLocation Location, bool InScope)?>>? documented = null) : ISide, IIdentitySide
{
    /// <summary>Both production adapters answer identity; this answers from the fake holding the scenario's entries, so
    /// an absent id is proven absent and a present one is in scope. onRead counts the read as the provider call it is.</summary>
    internal static IdentitySide Truthful(ISide side, FakeSide truth, Action? onRead = null) =>
        new(side, (id, _) =>
        {
            onRead?.Invoke();
            return Task.FromResult<(IdentityLocation Location, bool InScope)?>(truth.FindById(id) is { } entry
                ? (new IdentityLocation("1", entry.Path, entry), true) : ((IdentityLocation, bool)?)null);
        });

    public Side Side => inner.Side;
    public Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct) => identity(id, ct);
    public Task<(IdentityLocation Location, bool InScope)?> ReadByIdDocumentedAsync(string id, CancellationToken ct) =>
        (documented ?? identity)(id, ct);
    public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
        inner.ReadAllAsync(under, options, ct);
    public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => inner.ReadOneAsync(path, ct);
    public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) => inner.OpenReadAsync(entry, attempt, ct);
    public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);
    public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) => inner.StageAsync(path, content, source, ct);
    public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct) =>
        inner.PublishAsync(staged, expected, selection, ct);
    public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) => inner.DeleteAsync(expected, selection, ct);
    public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) => inner.MoveAsync(expected, newPath, ct);
    public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
        IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
        inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
    public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount, RequestAttempt attempt, CancellationToken ct) =>
        inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
}
