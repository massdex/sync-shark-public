namespace Massdex.SyncShark;

/// <summary>One Dropbox route from the embedded user-token route table (design dropbox-cli.md §2.2).</summary>
public sealed record DropboxRoute(
    string Name,
    DropboxRouteHost Host,
    DropboxRouteStyle Style,
    string Auth,
    string Scope,
    DropboxRoutePolicy Policy,
    DropboxRouteSpecial? Special,
    bool VoidArg,
    bool Deprecated,
    bool Preview,
    Uri Endpoint)
{
    /// <summary>The auth value whose routes carry no Authorization and no path-root header.</summary>
    public const string NoAuth = "noauth";

    /// <summary>True when this route is called without any Dropbox account credential.</summary>
    public bool IsNoAuth => string.Equals(Auth, NoAuth, StringComparison.Ordinal);
}
