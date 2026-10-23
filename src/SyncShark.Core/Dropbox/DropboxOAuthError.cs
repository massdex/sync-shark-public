namespace Massdex.SyncShark;

/// <summary>The OAuth error Dropbox's token endpoint returned, named where the pair behaviour depends on it.</summary>
public enum DropboxOAuthError
{
    /// <summary>No typed OAuth refusal is attached: a non-token failure, or a body that named no error.</summary>
    None,

    /// <summary>The refresh token was revoked or is no longer valid; the account needs the user to reconnect.</summary>
    InvalidGrant,

    /// <summary>Any other OAuth refusal; it is not an account-access problem.</summary>
    Other
}
