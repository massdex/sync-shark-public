namespace Massdex.SyncShark;

/// <summary>A timeout established at a provider transport boundary, not a local admission or interactive-auth timeout.</summary>
internal sealed class ProviderTransportTimeoutException(string message) : TimeoutException(message);
