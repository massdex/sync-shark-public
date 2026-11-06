namespace Massdex.SyncShark.Engine;

/// <summary>Identity-based native lookup shared by AutoSync and P4 first-run moves (AUTOSYNC section 3.1).
/// Null is proven identity absence, never inability to inspect, an out-of-pair location or a malformed
/// reply; exceptions preserve those distinctions. A location outside the current pair is returned intact
/// for shared scope ownership and never decoded through the pair-contained listing path.</summary>
public interface IIdentitySide
{
    /// <summary>The identity's current native domain, path and observed entry, or null when its absence is proven.</summary>
    Task<IdentityLocation?> ReadByIdAsync(string id, CancellationToken ct);
}
