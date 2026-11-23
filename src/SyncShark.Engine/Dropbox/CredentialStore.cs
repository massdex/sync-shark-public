namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>CredentialStore operations for the dropbox module.</summary>
public sealed class CredentialStore
{
    /// <summary>Save operation on the supplied facts.</summary>
    public void Save(string connectionId, string token) => throw new NotImplementedException();
    /// <summary>Read operation on the supplied facts.</summary>
    public string? Read(string connectionId) => throw new NotImplementedException();
    /// <summary>Remove operation on the supplied facts.</summary>
    public void Remove(string connectionId) => throw new NotImplementedException();

    /// <summary>AccountTarget for the selected CredentialStore operation.</summary>
    public static string AccountTarget(string connectionId) => throw new NotImplementedException();
    /// <summary>Registered application credential target.</summary>
    public const string RegisteredTarget = "massdex/account-access/v1/dropbox/sync-shark/production/refresh-token";
}

/// <summary>A retained connection has no available credential.</summary>
public sealed class DropboxCredentialUnavailableException : Exception
{
    /// <summary>Identify the unavailable connection.</summary>
    public DropboxCredentialUnavailableException(string connectionId) => throw new NotImplementedException();
    /// <summary>The unavailable connection.</summary>
    public string ConnectionId => throw new NotImplementedException();
}
