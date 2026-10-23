namespace Massdex.SyncShark.V2;

internal sealed class LocalWalkReplayInvalidException(string message, Exception? inner = null) : Exception(message, inner) { }
