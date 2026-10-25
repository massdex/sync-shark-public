namespace Massdex.SyncShark;

/// <summary>The routes handler: lists the embedded user-token route table, with no network call.</summary>
public static class DropboxCliRoutesVerb
{
    /// <summary>Returns the route table, optionally filtered and grant-checked against one account.</summary>
    public static async Task<object> RunAsync(IDropboxCliAccess access, DropboxCliArguments arguments,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? scopes = null;
        if (arguments.Present(DropboxCliOption.Account))
        {
            var account = await DropboxCliRouteRunner.OpenAsync(access, arguments, cancellationToken);
            scopes = account.Connection.Scopes;
        }
        var table = DropboxRouteTable.Embedded;
        var search = arguments.Value(DropboxCliOption.Search);
        var rows = table.Routes
            .Where(route => search is null || route.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(route => new DropboxCliRouteRow(
                route.Name, DropboxCliRouteNames.Style(route), DropboxCliRouteNames.Host(route),
                DropboxCliRouteNames.Policy(route), route.Scope,
                scopes is null ? null : route.Scope.Length == 0 || scopes.Contains(route.Scope, StringComparer.Ordinal),
                route.Deprecated, route.Preview, DropboxCliRouteNames.Special(route)))
            .ToArray();
        return new DropboxCliRoutesData(table.SpecCommit, rows.Length, rows);
    }
}
