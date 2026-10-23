using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

/// <summary>The api handler: one call for any route in the user-token table (design 2.3).</summary>
public static class DropboxCliApiVerb
{
    /// <summary>Calls one user-token route, applying the policy, scope, approval and style rules.</summary>
    public static async Task<object> RunAsync(IDropboxCliAccess access, DropboxCliArguments arguments,
        Action<string> progress, CancellationToken cancellationToken)
    {
        var routeName = arguments.Value(DropboxCliOption.Route)!;
        var route = DropboxRouteTable.Embedded.Find(routeName)
            ?? throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.UnknownRoute,
                ("route", routeName)));
        if (route.Policy == DropboxRoutePolicy.Denied)
            throw new SyncCliException("denied",
                DropboxCliCatalog.Message(DropboxCliMessage.RouteDenied, ("route", routeName)),
                SyncCliExitCode.NeedsReview);
        // Any --out is validated before the account is opened, so a refused local path never sends a request.
        var outPath = arguments.Present(DropboxCliOption.Out) ? arguments.LocalPath(DropboxCliOption.Out) : null;
        if (outPath is not null) DropboxCliLocalFiles.PrepareOutput(outPath, arguments.Yes);
        var account = await DropboxCliRouteRunner.OpenAsync(access, arguments, cancellationToken);
        DropboxCliSafety.RequireScope(route, account.Connection);
        var runner = new DropboxCliRouteRunner(account, arguments, access.DropboxCliSettings, progress, access.ResultFolder);
        var style = DropboxCliRouteNames.Style(route);
        var argument = Argument(arguments, route);
        var warnings = new DropboxCliWarnings(progress);
        return route.Style switch
        {
            DropboxRouteStyle.Upload => await UploadAsync(runner, arguments, route, routeName, style, argument,
                warnings, cancellationToken),
            DropboxRouteStyle.Download => await DownloadAsync(runner, arguments, route, routeName, style, argument,
                outPath, warnings, cancellationToken),
            _ => await RpcAsync(runner, arguments, route, routeName, style, argument, outPath, warnings,
                cancellationToken)
        };
    }

    private static string Argument(DropboxCliArguments arguments, DropboxRoute route)
    {
        if (arguments.Present(DropboxCliOption.Body)) return arguments.Json(DropboxCliOption.Body)?.ToJsonString() ?? "null";
        return route.VoidArg ? "null" : new JsonObject().ToJsonString();
    }

    private static async Task<object> UploadAsync(DropboxCliRouteRunner runner, DropboxCliArguments arguments,
        DropboxRoute route, string routeName, string style, string argument, DropboxCliWarnings warnings,
        CancellationToken cancellationToken)
    {
        if (!arguments.Present(DropboxCliOption.In))
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.ApiNeedsIn,
                ("route", routeName), ("style", style)));
        await using var source = DropboxCliLocalFiles.OpenInput(arguments.LocalPath(DropboxCliOption.In));
        using var result = await runner.CallAsync(new DropboxRouteCall(route, argument, arguments.NamespaceId,
            ContentIn: source), cancellationToken);
        return new DropboxCliApiData(routeName, style, DropboxCliRouteNames.Policy(route),
            result.Result.RootElement.Clone(), null, null, null, warnings.Result);
    }

    private static async Task<object> DownloadAsync(DropboxCliRouteRunner runner, DropboxCliArguments arguments,
        DropboxRoute route, string routeName, string style, string argument, string? outPath,
        DropboxCliWarnings warnings, CancellationToken cancellationToken)
    {
        if (outPath is null)
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.ApiNeedsOut,
                ("route", routeName), ("style", style)));
        var download = await DropboxCliDownload.RunAsync(runner, route, argument, arguments.NamespaceId, outPath,
            arguments.Yes, warnings, cancellationToken);
        return new DropboxCliApiData(routeName, style, DropboxCliRouteNames.Policy(route), download.Metadata, null,
            outPath, download.Bytes, warnings.Result);
    }

    private static async Task<object> RpcAsync(DropboxCliRouteRunner runner, DropboxCliArguments arguments,
        DropboxRoute route, string routeName, string style, string argument, string? outPath,
        DropboxCliWarnings warnings, CancellationToken cancellationToken)
    {
        if (arguments.Present(DropboxCliOption.In))
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.ApiIn,
                ("route", routeName), ("style", style)));
        using var result = await runner.CallAsync(new DropboxRouteCall(route, argument, arguments.NamespaceId),
            cancellationToken);
        var json = result.Result.RootElement.Clone();
        if (outPath is null)
            return new DropboxCliApiData(routeName, style, DropboxCliRouteNames.Policy(route), json, null, null, null,
                warnings.Result);
        await DropboxCliLocalFiles.WriteAtomicAsync(outPath, json.GetRawText(), arguments.Yes, warnings,
            cancellationToken);
        return new DropboxCliApiData(routeName, style, DropboxCliRouteNames.Policy(route), null, outPath, outPath,
            new FileInfo(outPath).Length, warnings.Result);
    }
}
