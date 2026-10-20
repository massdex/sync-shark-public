namespace Massdex.SyncShark;

/// <summary>The data spelling of each route value, shared by <c>dropbox api</c> and <c>dropbox routes</c>.</summary>
public static class DropboxCliRouteNames
{
    /// <summary>The style name for one route.</summary>
    public static string Style(DropboxRoute route) => route.Style switch
    {
        DropboxRouteStyle.Rpc => "rpc",
        DropboxRouteStyle.Upload => "upload",
        _ => "download"
    };

    /// <summary>The host name for one route.</summary>
    public static string Host(DropboxRoute route) => route.Host switch
    {
        DropboxRouteHost.Api => "api",
        DropboxRouteHost.Content => "content",
        _ => "notify"
    };

    /// <summary>The policy name for one route.</summary>
    public static string Policy(DropboxRoute route) => route.Policy switch
    {
        DropboxRoutePolicy.Read => "read",
        DropboxRoutePolicy.Write => "write",
        _ => "denied"
    };

    /// <summary>The special name for one route, or null when the route has none.</summary>
    public static string? Special(DropboxRoute route) => route.Special switch
    {
        null => null,
        DropboxRouteSpecial.Continue => "continue",
        DropboxRouteSpecial.AsyncCheck => "async-check",
        DropboxRouteSpecial.UploadSession => "upload-session",
        _ => "longpoll"
    };
}
