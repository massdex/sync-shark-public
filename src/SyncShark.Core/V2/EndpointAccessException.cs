namespace Massdex.SyncShark.V2;

/// <summary>
/// Thrown at a provider boundary when the account's credential is missing, expired or revoked; no catch in the core
/// swallows it, an effect it interrupts commits nothing, and continuous sync holds the pair until the user reconnects.
/// </summary>
public sealed class EndpointAccessException : Exception
{
    public EndpointAccessException(string message) : base(message)
    {
    }

    public EndpointAccessException(string message, Exception? inner) : base(message, inner)
    {
    }
}
