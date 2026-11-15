namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Credential boundary for synthetic transports; never accesses the Windows credential store.</summary>
internal sealed class SyntheticCredentials : IDropboxRefreshTokenStore
{
    public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>("synthetic-refresh");
    }

    public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException();

    public Task DeleteAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException();

    public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) => this;
}
