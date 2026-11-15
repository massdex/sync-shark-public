namespace Massdex.SyncShark;

/// <summary>The one place a Dropbox effect is approved and a route's granted scope is checked.</summary>
public static class DropboxCliSafety
{
    /// <summary>Refuses a write route without --yes; a read route needs no approval.</summary>
    public static void RequireApproval(DropboxRoute route, bool yes, string command)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Policy == DropboxRoutePolicy.Write && !yes)
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.RequiresYes,
                ("command", command)));
    }

    /// <summary>Refuses a route whose scope the account was not granted; no request is sent.</summary>
    public static void RequireScope(DropboxRoute route, DropboxConnectionBinding connection)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(connection);
        if (route.Scope.Length == 0 || connection.Scopes.Contains(route.Scope, StringComparer.Ordinal)) return;
        throw new SyncCliException("missing_scope",
            DropboxCliCatalog.Message(DropboxCliMessage.MissingScope, ("route", route.Name),
                ("scope", route.Scope), ("account", connection.ConnectionId)),
            SyncCliExitCode.NeedsReview);
    }
}
