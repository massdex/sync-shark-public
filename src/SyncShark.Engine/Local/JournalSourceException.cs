namespace Massdex.SyncShark.Engine.Local;

/// <summary>A journal page failure that stopped the live change feed, carrying the classified page status and the
/// native error as safe evidence for the run log. The message stays the shared user sentence.</summary>
public sealed class JournalSourceException(NtfsUsnPageStatus status, int? nativeError, string message)
    : IOException(message)
{
    /// <summary>The page status that made the acquisition unavailable.</summary>
    public NtfsUsnPageStatus Status { get; } = status;

    /// <summary>The native Win32 error behind the status when the page read established one.</summary>
    public int? NativeError { get; } = nativeError;
}
