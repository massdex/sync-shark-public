using System.Net;
using System.Text.Json;
using Massdex.SyncShark;

namespace Massdex.SyncShark.DropboxCli;

/// <summary>The route-table contract checks (dropbox-cli.md §5 group 4).</summary>
internal static class DropboxRouteTableChecks
{
    // Every route a §1.1 verb names; the table must contain each one.
    private static readonly string[] VerbRoutes =
    [
        "users/get_current_account", "users/get_space_usage", "files/list_folder", "files/list_folder/continue",
        "files/get_metadata", "files/download", "files/upload", "files/upload_session/start",
        "files/upload_session/append_v2", "files/upload_session/finish", "files/create_folder_v2",
        "files/delete_v2", "files/delete_batch", "files/delete_batch/check", "files/move_v2", "files/move_batch_v2",
        "files/move_batch/check_v2", "files/copy_v2", "files/copy_batch_v2", "files/copy_batch/check_v2",
        "files/search_v2", "files/search/continue_v2", "files/list_revisions", "files/restore",
        "files/list_folder/get_latest_cursor", "files/list_folder/longpoll", "files/get_thumbnail_v2"
    ];

    public static void Run()
    {
        var table = DropboxRouteTable.Embedded;
        Check(!string.IsNullOrWhiteSpace(table.SpecCommit), "the route table names its spec commit");
        Check(table.Routes.Count > 0, "the route table has routes");
        Check(table.Routes.Select(route => route.Name).Distinct(StringComparer.Ordinal).Count() == table.Routes.Count,
            "route names are unique");
        Check(table.Hosts.Keys.Order().SequenceEqual(
                new[] { DropboxRouteHost.Api, DropboxRouteHost.Content, DropboxRouteHost.Notify }),
            "every Dropbox host is declared");
        var denied = table.Routes.Where(route => route.Policy == DropboxRoutePolicy.Denied).ToArray();
        Check(denied.Length == 1 && denied[0].Name == "auth/token/revoke", "exactly one route is denied");
        var problems = table.Routes.Where(route => !IsUserAuth(route.Auth) || ExpectedPolicy(route) != route.Policy ||
                                                   route.Endpoint.Scheme != Uri.UriSchemeHttps)
            .Select(route => route.Name).Take(5).ToArray();
        Check(problems.Length == 0, "auth and policy rules hold for every route: " + string.Join(", ", problems));
        var missing = VerbRoutes.Where(name => table.Find(name) is null).ToArray();
        Check(missing.Length == 0, "every verb's routes exist: " + string.Join(", ", missing));
        Console.WriteLine("--dropbox-route-checks passed (table).");
    }

    // A user token can call a route only when its auth names the user; a team- or app-only route cannot.
    private static bool IsUserAuth(string auth) =>
        auth == DropboxRoute.NoAuth || auth.Split(',').Contains("user", StringComparer.Ordinal);

    // The generator's policy rule (design §2.2), re-derived here so the table can never drift from it.
    private static DropboxRoutePolicy ExpectedPolicy(DropboxRoute route)
    {
        if (route.Name == "auth/token/revoke") return DropboxRoutePolicy.Denied;
        if (route.Name == "files/get_temporary_link") return DropboxRoutePolicy.Write;
        if (route.Scope.EndsWith(".read", StringComparison.Ordinal) || route.Special == DropboxRouteSpecial.AsyncCheck)
            return DropboxRoutePolicy.Read;
        return DropboxRoutePolicy.Write;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Dropbox route table check failed: " + description);
        Console.WriteLine("PASS " + description);
    }
}
