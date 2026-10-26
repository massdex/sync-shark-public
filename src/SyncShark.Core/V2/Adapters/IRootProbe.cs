using System.Net;

namespace Massdex.SyncShark.V2;

/// <summary>Reads a root folder's stable object id when a pair is created (replaces observing an unbound root).</summary>
public interface IRootProbe
{
    /// <summary>The root's object id; a missing folder, connection or credential is refused before any effect.</summary>
    Task<string> ObjectIdAsync(string provider, string? accountId, string path, CancellationToken ct);
}
