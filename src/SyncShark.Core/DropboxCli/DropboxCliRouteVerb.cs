namespace Massdex.SyncShark;

/// <summary>The template handler: runs one data-described call for every verb but put/changes/api/routes.</summary>
public static class DropboxCliRouteVerb
{
    /// <summary>Runs the first matching call and shapes its result.</summary>
    public static async Task<object> RunAsync(IDropboxCliAccess access, DropboxCliArguments arguments,
        Action<string> progress, CancellationToken cancellationToken)
    {
        var verb = arguments.Verb;
        var call = verb.FirstCall(arguments.Present)
            ?? throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.NoCall,
                ("command", arguments.Command)));
        var output = verb.OutputFor(call)
            ?? throw new InvalidDataException(DropboxCliCatalog.Message(DropboxCliMessage.NoOutput,
                ("command", arguments.Command)));
        // A download's --out is validated before the account is opened, so a refused path sends no request.
        var outPath = output.Kind == "Download" ? arguments.LocalPath(DropboxCliOption.Out) : null;
        if (outPath is not null) DropboxCliLocalFiles.PrepareOutput(outPath, arguments.Yes);
        var account = await DropboxCliRouteRunner.OpenAsync(access, arguments, cancellationToken);
        var runner = new DropboxCliRouteRunner(account, arguments, access.DropboxCliSettings, progress);
        var argument = DropboxCliTemplate.Render(call.Body, arguments);
        if (outPath is not null)
            return await DownloadAsync(runner, arguments, call.Route, argument, outPath,
                new DropboxCliWarnings(progress), cancellationToken);
        using var result = await runner.CallAsync(
            new DropboxRouteCall(call.Route, argument, arguments.NamespaceId), cancellationToken);
        if (call.Poll is null) return DropboxCliOutput.Map(output, call.Route, result);
        var root = result.Result.RootElement;
        if (DropboxCliRouteRunner.TryCompletedBatch(root, out var completed)) return completed;
        var jobId = DropboxCliOutput.Str(root, "async_job_id")
            ?? throw new InvalidDataException(DropboxCliCatalog.Message(DropboxCliMessage.NoJobId,
                ("route", call.Route.Name)));
        return await runner.PollBatchAsync(call.Poll, jobId, cancellationToken);
    }

    private static async Task<object> DownloadAsync(DropboxCliRouteRunner runner, DropboxCliArguments arguments,
        DropboxRoute route, string argument, string outPath, DropboxCliWarnings warnings,
        CancellationToken cancellationToken)
    {
        var download = await DropboxCliDownload.RunAsync(runner, route, argument, arguments.NamespaceId, outPath,
            arguments.Yes, warnings, cancellationToken);
        return new DropboxCliDownloadData(route.Name, outPath, download.Bytes, download.ContentHash, download.Verified,
            DropboxCliOutput.Entry(download.Metadata), warnings.Result);
    }
}
