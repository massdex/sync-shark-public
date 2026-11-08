namespace Massdex.SyncShark;

internal sealed record MutableJsonValue<T>(bool Present, T? Value);
