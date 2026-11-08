namespace Massdex.SyncShark;

/// <summary>Classifies host reads from the one command registry: a row marked <c>HostRead</c> is served by the
/// running Engine host. Client routing and host dispatch ask this, so the two cannot drift.</summary>
public static class EngineHostReadCatalog
{
    private static readonly IReadOnlyDictionary<string, EngineHostReadKind> Reads = SyncCliRegistry.Rows
        .Where(row => row.HostRead is { } read)
        .ToDictionary(row => row.Command, row => row.HostRead!.Value, StringComparer.Ordinal);

    /// <summary>The host read a command names, or null for every other command.</summary>
    public static EngineHostReadKind? KindOf(string? command) =>
        command is not null && Reads.TryGetValue(command, out var kind) ? kind : null;

    public static bool Owns(string? command) => KindOf(command) is not null;
}
