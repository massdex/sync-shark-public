namespace Massdex.SyncShark.Engine;

/// <summary>An invalid request from Edge (unknown or disabled pair, bad resolution); Edge maps it to a usage error.</summary>
public sealed class EngineRequestException(TextKey key, params (TextArg Name, string Value)[] values)
    : Exception(Text.Get(key, values))
{
    /// <summary>The text key of the refused request, for the caller's message.</summary>
    public TextKey Key { get; } = key;
}
