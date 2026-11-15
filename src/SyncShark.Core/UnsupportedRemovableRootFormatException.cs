namespace Massdex.SyncShark;

/// <summary>The existing unsupported removable-root format refusal.</summary>
public sealed class UnsupportedRemovableRootFormatException(string message) : IOException(message);
