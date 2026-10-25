namespace Massdex.SyncShark.V2;

internal sealed class OwnedPointerDeferredException(StoragePairException cause) : IOException(cause.Message, cause)
{
    internal StoragePairException Cause { get; } = cause;
}
