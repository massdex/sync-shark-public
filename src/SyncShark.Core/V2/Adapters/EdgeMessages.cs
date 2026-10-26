namespace Massdex.SyncShark.V2;

/// <summary>The edge's user-facing wording, loaded once from the embedded catalog (standards §2.2).</summary>
internal static class EdgeMessages
{
    /// <summary>The full "command not in core v2 yet" message for a stable wording key.</summary>
    internal static NotSupportedException NotInV2(string whatKey) => new(Compose("command", whatKey));

    /// <summary>The short "not in core v2 yet" notice for a stable wording key.</summary>
    internal static string NotInV2Text(string whatKey) => Compose("notice", whatKey);

    /// <summary>A whole edge message for a stable key, with an optional "{detail}" filled in.</summary>
    internal static string Text(string messageKey, string? detail = null) =>
        MessageCatalog.Default.Message(messageKey).Replace("{detail}", detail ?? string.Empty, StringComparison.Ordinal);

    /// <summary>The one continuous-sync monitor wording for a stable key (status words and the projection kind).</summary>
    internal static string Monitor(string monitorKey) => MessageCatalog.Default.Monitor(monitorKey);

    private static string Compose(string templateKey, string whatKey) =>
        MessageCatalog.Default.Template(templateKey)
            .Replace("{what}", MessageCatalog.Default.What(whatKey), StringComparison.Ordinal);
}
