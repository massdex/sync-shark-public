namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox provider facts as named constants and the sole provider-wait applicability decoder</summary>
public static class DropboxProtocol
{
    /// <summary>Parse operation on the supplied facts.</summary>
    public static System.Text.Json.JsonDocument Parse(Stream response) => throw new NotImplementedException();

    /// <summary>ProviderWait for the selected DropboxProtocol operation.</summary>
    public static TimeSpan? ProviderWait(TimeSpan? retryAfter, System.Text.Json.JsonElement error) => throw new NotImplementedException();
    /// <summary>ProviderConstraintFor for the selected DropboxProtocol operation.</summary>
    internal static ProviderConstraint? ProviderConstraintFor( string accountId, TimeSpan? wait, long startedTimestamp) => throw new NotImplementedException();
    /// <summary>LongpollConstraintFor for the selected DropboxProtocol operation.</summary>
    internal static ProviderConstraint? LongpollConstraintFor( string accountId, string sourceIdentity, TimeSpan? backoff, long startedTimestamp) => throw new NotImplementedException();
    /// <summary>Provider wait scoped to the account, route or change source.</summary>
    internal readonly record struct ProviderConstraint(
        (string AccountId, string? RouteName, string? SourceIdentity) Scope,
        long StartedTimestamp, TimeSpan Duration);
}
