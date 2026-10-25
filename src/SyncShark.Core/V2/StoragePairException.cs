namespace Massdex.SyncShark.V2;

internal sealed class StoragePairException(string cause, string message, Exception? inner = null) : IOException(message, inner)
{
    internal string Cause { get; } = cause;
}
