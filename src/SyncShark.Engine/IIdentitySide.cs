namespace Massdex.SyncShark.Engine;

/// <summary>Identity-based native lookup shared by AutoSync and P4 first-run moves (AUTOSYNC section 3.1).
/// Null is proven identity absence, never inability to inspect, an out-of-pair location or a malformed
/// reply; exceptions preserve those distinctions. A location outside the current pair is returned intact
/// for shared scope ownership and never decoded through the pair-contained listing path.</summary>
public interface IIdentitySide
{
    /// <summary>The verified location and adapter-owned scope answer, or proven absence. True carries an included
    /// representative; false requires complete evidence that no identity name is included.</summary>
    Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct);
}
