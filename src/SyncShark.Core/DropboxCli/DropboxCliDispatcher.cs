namespace Massdex.SyncShark;

/// <summary>The single entry point the shared registry calls for every Dropbox group command.</summary>
public static class DropboxCliDispatcher
{
    /// <summary>Runs one Dropbox command and returns its data, spilling an oversized result to a file.</summary>
    public static async Task<object> DispatchAsync(IDropboxCliAccess access, SyncCliInvocation invocation,
        Action<string> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(invocation);
        var verb = DropboxCliCatalog.Verb(invocation.Command)
            ?? throw new SyncCliUsageException("The command '" + invocation.Command + "' is not in the Dropbox group.");
        var arguments = new DropboxCliArguments(invocation, verb, access.DropboxCliSettings);
        await arguments.ResolveJsonFilesAsync(cancellationToken);
        arguments.Validate();
        try
        {
            var data = verb.Handler switch
            {
                DropboxCliHandler.Routes => await DropboxCliRoutesVerb.RunAsync(access, arguments, cancellationToken),
                DropboxCliHandler.Api => await DropboxCliApiVerb.RunAsync(access, arguments, progress, cancellationToken),
                DropboxCliHandler.Put => await DropboxCliPutVerb.RunAsync(access, arguments, progress, cancellationToken),
                DropboxCliHandler.Changes => await DropboxCliChangesVerb.RunAsync(access, arguments, progress,
                    cancellationToken),
                DropboxCliHandler.Route => await DropboxCliRouteVerb.RunAsync(access, arguments, progress,
                    cancellationToken),
                _ => throw new InvalidOperationException("The Dropbox verb '" + invocation.Command +
                    "' names no runnable handler.")
            };
            return await DropboxCliResult.FitAsync(access, invocation.Command, data, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw DropboxCliErrors.Map(error, RouteFor(verb, invocation));
        }
    }

    private static DropboxRoute? RouteFor(DropboxCliCatalog.VerbDefinition verb, SyncCliInvocation invocation)
    {
        var table = DropboxRouteTable.Embedded;
        return verb.Handler switch
        {
            DropboxCliHandler.Api => invocation.Value("--route") is { } name ? table.Find(name) : null,
            DropboxCliHandler.Put => verb.Call(DropboxCliCall.Upload).Route,
            DropboxCliHandler.Changes => verb.Call(DropboxCliCall.Continue).Route,
            _ => verb.FirstCall(option => invocation.Flag(DropboxCliCatalog.Flag(option)))?.Route
        };
    }
}
