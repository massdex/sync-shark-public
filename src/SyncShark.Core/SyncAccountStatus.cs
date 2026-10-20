namespace Massdex.SyncShark;

// Presentation/routing metadata only; never accepted as provider effect or credential authority.
public sealed record SyncAccountBinding(string ConnectionId, string Provider, string AccountId,
    string DisplayName, string Email, string AccountType, IReadOnlyList<string> Scopes,
    DateTimeOffset? ConnectedUtc, DateTimeOffset? LastVerifiedUtc,
    string? RootNamespaceId = null, string? HomeNamespaceId = null)
{
    public string ProviderName => SyncProviders.DisplayName(Provider);
    public static SyncAccountBinding From(DropboxConnectionBinding value) => new(value.ConnectionId,
        SyncProviders.Dropbox, value.AccountId, value.DisplayName, value.Email, value.AccountType,
        value.Scopes, value.ConnectedUtc, value.LastVerifiedUtc, value.RootNamespaceId, value.HomeNamespaceId);
    public static SyncAccountBinding From(GoogleDriveConnectionBinding value) => new(value.ConnectionId,
        SyncProviders.GoogleDrive, value.PermissionId, value.DisplayName, value.Email, "Google",
        value.Scopes, value.ConnectedUtc, value.LastVerifiedUtc);
}

public sealed record SyncAccountStatus(SyncAccountBinding? Binding, bool HasProtectedCredential,
    bool LiveVerified, string Health, string? FailureType, int DependentPairCount,
    DateTimeOffset? LastSuccessfulSyncUtc, DropboxSpaceUsage? SpaceUsage = null)
{
    public static implicit operator SyncAccountStatus(DropboxConnectionStatus value) => new(
        value.Binding is { } binding ? SyncAccountBinding.From(binding) : null,
        value.HasProtectedCredential, value.LiveVerified, value.Health, value.FailureType,
        value.DependentPairCount, value.LastSuccessfulSyncUtc, value.SpaceUsage);
}
