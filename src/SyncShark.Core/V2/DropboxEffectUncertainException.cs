namespace Massdex.SyncShark;

public sealed class DropboxEffectUncertainException(string message, Exception? innerException = null) : IOException(message, innerException)
{
    /// <summary>The provider explicitly rejected an expired/unknown async job id, not a transient lookup failure.</summary>
    public bool JobIdUnavailable { get; init; }
}
