namespace Massdex.SyncShark;

/// <summary>The existing unsupported network-root storage refusal.</summary>
public sealed class UnsupportedNetworkRootException(string message) : IOException(message);
